using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Write-ahead undo journal for unloaded feeding. A record is forced to disk
/// before either ZDO is touched and retained until the next world load.
/// On recovery, both objects must match either their before or after images.
/// Any conflict blocks further offline writes instead of guessing.
/// </summary>
internal static class OfflineFeedJournal
{
    private const string Magic = "TAA_FEED_V1";
    private static string _world = "";
    private static string _directory = "";
    private static bool _ready;
    internal static bool Ready => _ready;

    private sealed class Record
    {
        internal string File = "";
        internal string AnimalId = "";
        internal string SourceId = "";
        internal long OldFed;
        internal long NewFed;
        internal bool Container;
        internal int OldStack;
        internal int NewStack;
        internal byte[] OldInventory = Array.Empty<byte>();
        internal byte[] NewInventory = Array.Empty<byte>();
    }

    internal static void EnsureRecovered(ManualLogSource log)
    {
        var net = ZNet.instance;
        if (net == null || !net.IsServer() || ZDOMan.instance == null) return;
        var worldName = net.GetWorldName();
        if (string.IsNullOrEmpty(worldName)) return;
        var cleanName = string.Concat(worldName.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_'));
        if (cleanName != _world)
        {
            _ready = false;
            _world = cleanName;
            _directory = Path.Combine(Paths.ConfigPath, "TameAssistAddon", "offline-journal", cleanName);
        }
        if (_ready) return;

        try
        {
            Directory.CreateDirectory(_directory);
            var paths = Directory.GetFiles(_directory, "*.tx")
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var records = paths.Select(Load).ToArray();
            if (records.Length > 0 && !ReconcileBatch(records, log))
            {
                log.LogError("Offline WAL recovery blocked by mismatched or live ZDO. No offline world writes allowed.");
                return;
            }
            foreach (var path in paths)
                File.Delete(path);
            int handled = records.Length;
            _ready = true;
            log.LogInfo($"Offline feeding journal ready; recovered {handled} transaction(s). Pending transactions are rolled back to their pre-transaction snapshots.");
        }
        catch (Exception ex)
        {
            log.LogError("Offline feeding journal recovery FAILED; refusing offline writes: " + ex);
        }
    }

    internal static bool TryCommit(ManualLogSource log, ZDO animal, ZDO source,
        long newFed, byte[] oldInventory, byte[] newInventory)
    {
        if (!_ready || !animal.IsValid() || !source.IsValid()) return false;
        return Commit(log, new Record
        {
            AnimalId = animal.m_uid.ToString(), SourceId = source.m_uid.ToString(),
            OldFed = animal.GetLong(ZDOVars.s_tameLastFeeding, 0),
            NewFed = newFed,
            Container = true,
            OldInventory = oldInventory, NewInventory = newInventory
        }, animal, source);
    }

    internal static bool TryCommitStack(ManualLogSource log, ZDO animal, ZDO source,
        long newFed, int oldStack)
    {
        // A stack of one would require DestroyZDO; never destroy food in this
        // offline path. The original world retains that item untouched.
        if (!_ready || oldStack <= 1 || !animal.IsValid() || !source.IsValid()) return false;
        return Commit(log, new Record
        {
            AnimalId = animal.m_uid.ToString(), SourceId = source.m_uid.ToString(),
            OldFed = animal.GetLong(ZDOVars.s_tameLastFeeding, 0),
            NewFed = newFed, Container = false,
            OldStack = oldStack, NewStack = oldStack - 1
        }, animal, source);
    }

    private static bool Commit(ManualLogSource log, Record r, ZDO animal, ZDO source)
    {
        if (!_ready) return false;
        // Do not steal live peer ownership even if its client is physically
        // far away. Offline processing must not race an active container owner.
        if (ZNet.instance.GetPeers().Any(peer =>
            peer.m_uid == animal.GetOwner() || peer.m_uid == source.GetOwner()))
            return false;
        // Journal-first: failed writes leave the game objects intact.
        var path = Path.Combine(_directory, DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N") + ".tx");
        r.File = path;
        try
        {
            var data = Serialize(r);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var buffer = Encoding.UTF8.GetBytes(data);
                stream.Write(buffer, 0, buffer.Length);
                stream.Flush(true);
            }
            File.Move(temp, path);
            // In-memory changes and ownership assignment are intentionally
            // after the durable journal. Only disconnected-owner objects
            // reach this point.
            if (!animal.IsOwner()) animal.SetOwner(ZDOMan.GetSessionID());
            if (!source.IsOwner()) source.SetOwner(ZDOMan.GetSessionID());
            animal.Set(ZDOVars.s_tameLastFeeding, r.NewFed);
            if (r.Container) source.Set(ZDOVars.s_items, r.NewInventory);
            else source.Set("stack", r.NewStack);
            // Retain journal until restart: world persistence and fsync are NOT
            // atomic with this file, so a future recovery must reconcile.
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("Offline feed transaction interrupted; further offline writes blocked: " + ex);
            _ready = false;
            // Keep journal for recovery. If no journal was created, no ZDO
            // should have been modified.
            return false;
        }
    }

    private sealed class Image
    {
        internal long? Fed;
        internal byte[] Inventory;
        internal int? Stack;
        internal Image Copy() => new Image
        {
            Fed = Fed,
            Inventory = Inventory != null ? (byte[])Inventory.Clone() : null,
            Stack = Stack
        };
    }

    private static bool ImageEquals(Image expected, Image actual) =>
        expected.Fed == actual.Fed &&
        expected.Stack == actual.Stack &&
        ((expected.Inventory == null && actual.Inventory == null) ||
         (expected.Inventory != null && actual.Inventory != null &&
          expected.Inventory.SequenceEqual(actual.Inventory)));

    /// <summary>
    /// All records form an ordered stream of paired ZDO updates. At restart,
    /// the game DB may contain ANY complete prefix of those transactions
    /// because its save time is independent of the journal. Keep a matching
    /// complete prefix, or roll back all touched fields to the initial image
    /// when the DB contains a torn/inconsistent save.
    /// </summary>
    private static bool ReconcileBatch(Record[] records, ManualLogSource log)
    {
        var ids = records.SelectMany(r => new[] { r.AnimalId, r.SourceId })
            .Distinct(StringComparer.Ordinal).ToArray();
        var objects = new System.Collections.Generic.Dictionary<string, ZDO>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var zdo = Lookup(id);
            if (zdo == null || !zdo.IsValid()) return false;
            if (ZNetScene.instance.FindInstance(zdo) != null ||
                ZNet.instance.GetPeers().Any(peer =>
                    (peer.m_refPos - zdo.GetPosition()).sqrMagnitude < 256f * 256f))
                return false;
            objects.Add(id, zdo);
        }

        var images = new System.Collections.Generic.Dictionary<string, Image>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (!images.TryGetValue(r.AnimalId, out var animal))
                images[r.AnimalId] = animal = new Image { Fed = r.OldFed };
            if (!images.TryGetValue(r.SourceId, out var source))
            {
                source = r.Container
                    ? new Image { Inventory = r.OldInventory }
                    : new Image { Stack = r.OldStack };
                images[r.SourceId] = source;
            }
        }
        var first = images.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(),
            StringComparer.Ordinal);
        var current = new System.Collections.Generic.Dictionary<string, Image>(StringComparer.Ordinal);
        foreach (var pair in images)
        {
            var zdo = objects[pair.Key];
            current[pair.Key] = new Image
            {
                Fed = pair.Value.Fed.HasValue ?
                    zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L) : (long?)null,
                Inventory = pair.Value.Inventory != null ?
                    zdo.GetByteArray(ZDOVars.s_items, null) : null,
                Stack = pair.Value.Stack.HasValue ?
                    zdo.GetInt("stack", 1) : (int?)null
            };
        }

        int bestPrefix = -1;
        if (images.All(pair => ImageEquals(pair.Value, current[pair.Key])))
            bestPrefix = 0;
        bool chainValid = true;
        // Also track every known legal object image to distinguish a torn
        // world save from an unexpected player modification.
        var known = images.ToDictionary(x => x.Key,
            x => new System.Collections.Generic.List<Image> { x.Value.Copy() },
            StringComparer.Ordinal);
        for (int i = 0; i < records.Length; i++)
        {
            var r = records[i];
            var animal = images[r.AnimalId];
            var food = images[r.SourceId];
            if (animal.Fed != r.OldFed ||
                (r.Container
                    ? food.Inventory == null ||
                      !food.Inventory.SequenceEqual(r.OldInventory)
                    : food.Stack != r.OldStack))
            {
                chainValid = false;
                break;
            }
            animal.Fed = r.NewFed;
            if (r.Container) food.Inventory = r.NewInventory;
            else food.Stack = r.NewStack;
            known[r.AnimalId].Add(animal.Copy());
            known[r.SourceId].Add(food.Copy());
            if (images.All(pair => ImageEquals(pair.Value, current[pair.Key])))
                bestPrefix = i + 1;
        }
        if (!chainValid) return false;
        if (bestPrefix >= 0)
        {
            log.LogInfo($"Offline WAL recovered a consistent saved prefix: {bestPrefix}/{records.Length} complete feed operations. No items changed.");
            return true;
        }

        // Torn save: rollback only if EVERY observed field matches some
        // value created by this WAL, never overwrite an external edit.
        if (!current.All(pair => known[pair.Key].Any(img => ImageEquals(img, pair.Value))))
            return false;
        foreach (var pair in first)
        {
            var zdo = objects[pair.Key];
            if (pair.Value.Fed.HasValue)
                zdo.Set(ZDOVars.s_tameLastFeeding, pair.Value.Fed.Value);
            if (pair.Value.Inventory != null)
                zdo.Set(ZDOVars.s_items, pair.Value.Inventory);
            if (pair.Value.Stack.HasValue)
                zdo.Set("stack", pair.Value.Stack.Value);
        }
        log.LogWarning("Offline WAL found torn world snapshot; restored prior animal feeding and food counts. Verify next world save.");
        return true;
    }

    private static ZDO Lookup(string uid)
    {
        // Avoid parsing ZDOID's network-specific formatting: resolve through
        // the prefab-indexed enumeration of objects used by the adapter.
        var kinds = new[] { "Boar", "Raspberry", "Blueberries", "Mushroom", "Carrot", "Turnip" };
        foreach (var name in kinds)
        {
            int index = 0;
            var matches = new System.Collections.Generic.List<ZDO>();
            for (int i = 0; i < 1024; i++)
                if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(name, matches, ref index))
                    break;
            var found = matches.FirstOrDefault(z => z != null && z.IsValid() &&
                z.m_uid.ToString() == uid);
            if (found != null) return found;
        }
        // Container prefab names vary by world: inspect all registered
        // vanilla container prefabs without using private ZDOMan fields.
        foreach (var prefab in ZNetScene.instance.m_prefabs)
        {
            if (!prefab || !prefab.GetComponent<Container>()) continue;
            int index = 0;
            var matches = new System.Collections.Generic.List<ZDO>();
            for (int i = 0; i < 1024; i++)
                if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab.name, matches, ref index))
                    break;
            var found = matches.FirstOrDefault(z => z != null && z.IsValid() &&
                z.m_uid.ToString() == uid);
            if (found != null) return found;
        }
        return null;
    }

    private static string Serialize(Record r) =>
        string.Join("\n", new[] { Magic, r.AnimalId, r.SourceId,
            r.OldFed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.NewFed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Container ? "1" : "0",
            r.OldStack.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.NewStack.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(r.OldInventory),
            Convert.ToBase64String(r.NewInventory) }) + "\n";

    private static Record Load(string file)
    {
        var lines = File.ReadAllLines(file);
        if (lines.Length != 10 || lines[0] != Magic)
            throw new InvalidDataException("Invalid offline feeding journal: " + file);
        return new Record
        {
            File = file, AnimalId = lines[1], SourceId = lines[2],
            OldFed = long.Parse(lines[3], System.Globalization.CultureInfo.InvariantCulture),
            NewFed = long.Parse(lines[4], System.Globalization.CultureInfo.InvariantCulture),
            Container = lines[5] == "1",
            OldStack = int.Parse(lines[6], System.Globalization.CultureInfo.InvariantCulture),
            NewStack = int.Parse(lines[7], System.Globalization.CultureInfo.InvariantCulture),
            OldInventory = Convert.FromBase64String(lines[8]),
            NewInventory = Convert.FromBase64String(lines[9])
        };
    }
}
