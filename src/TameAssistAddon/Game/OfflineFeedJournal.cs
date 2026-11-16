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
            int handled = 0;
            foreach (var path in Directory.GetFiles(_directory, "*.tx").OrderByDescending(x => x, StringComparer.Ordinal))
            {
                var record = Load(path);
                if (!Reconcile(record))
                {
                    log.LogError("Offline transaction recovery BLOCKED by mismatched ZDO: " +
                        Path.GetFileName(path) + ". World writes remain disabled; restore backup or inspect journal.");
                    return;
                }
                File.Delete(path);
                handled++;
            }
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
            // In-memory changes are intentionally after the durable journal.
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

    private static bool Reconcile(Record r)
    {
        var animal = Lookup(r.AnimalId);
        var source = Lookup(r.SourceId);
        if (animal == null || source == null || !animal.IsValid() || !source.IsValid())
            return false;
        // Do not roll back a transaction while another player is currently
        // simulating either object. Retry next tick when the region unloads.
        if (ZNet.instance.GetPeers().Any(peer =>
            (peer.m_refPos - animal.GetPosition()).sqrMagnitude < 256f * 256f ||
            (peer.m_refPos - source.GetPosition()).sqrMagnitude < 256f * 256f))
            return false;
        long fed = animal.GetLong(ZDOVars.s_tameLastFeeding, 0);
        if (fed != r.OldFed && fed != r.NewFed)
            return false;

        if (r.Container)
        {
            byte[] current = source.GetByteArray(ZDOVars.s_items, null);
            if (current == null || (!current.SequenceEqual(r.OldInventory) &&
                !current.SequenceEqual(r.NewInventory)))
                return false;
            // Roll back even if both postimages exist; deterministic and
            // harmless except for sacrificing an unpersisted feed cycle.
            if (!current.SequenceEqual(r.OldInventory))
                source.Set(ZDOVars.s_items, r.OldInventory);
        }
        else
        {
            int current = source.GetInt("stack", 1);
            if (current != r.OldStack && current != r.NewStack)
                return false;
            if (current != r.OldStack) source.Set("stack", r.OldStack);
        }
        if (fed != r.OldFed) animal.Set(ZDOVars.s_tameLastFeeding, r.OldFed);
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
