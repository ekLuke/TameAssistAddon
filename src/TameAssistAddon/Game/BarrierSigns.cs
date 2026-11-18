using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Server-only vanilla diagnostic signs. A single tagged automatic sign is
/// retained for each nearby animal group instead of creating another sign
/// each time animals move and a new fence piece is selected. Player signs
/// located ON a detected barrier supersede all automatic signs in the group.
/// These are diagnostic candidates, not validated animal pens.
/// </summary>
internal static class BarrierSigns
{
    internal const string MarkerKey = "thelair.tameassistaddon.barrierSign";
    private const string ParentKey = "thelair.tameassistaddon.barrierParent";
    private const float ManualOnBarrierRadius = 2.4f;
    private const float GroupRadius = 12f;
    private const float ManualSearchRadius = 28f;
    private const int MaxSigns = 12;
    private const float ProtectedHealth = 100000000f;

    private sealed class Managed
    {
        internal ZDOID BarrierId;
        internal ZDOID SignId;
        internal readonly HashSet<ZDOID> Animals = new HashSet<ZDOID>();
        internal Vector3 Center;
    }

    private sealed class Cluster
    {
        internal readonly List<Target> Targets = new List<Target>();
        internal readonly HashSet<ZDOID> AnimalIds = new HashSet<ZDOID>();
        internal Vector3 Center;
        internal Target Primary => Targets.OrderBy(t => t.Animal.m_uid.ToString(),
            StringComparer.Ordinal).First();
    }

    internal sealed class Target
    {
        internal ZDO Animal = null!;
        internal ZDO Barrier = null!;
        internal Vector3 Viewer;
        internal string Text = "";
        internal IReadOnlyList<ZDO> NearbyBarriers = Array.Empty<ZDO>();
    }

    private static readonly List<Managed> Active = new List<Managed>();

    internal static void Sync(ManualLogSource log, IReadOnlyList<Target> requested,
        bool enabled, bool debug)
    {
        var net = ZNet.instance;
        var scene = ZNetScene.instance;
        var manager = ZDOMan.instance;
        if (net == null || !net.IsServer() || scene == null || manager == null)
            return;

        if (!enabled)
        {
            int cleared = RemoveAll();
            if (debug && cleared > 0)
                log.LogInfo($"BarrierSigns disabled: removed={cleared}");
            return;
        }

        var prefab = scene.GetPrefab("sign");
        var view = prefab != null ? prefab.GetComponent<ZNetView>() : null;
        if (view == null)
        {
            log.LogError("BarrierSigns cannot find vanilla sign prefab ZNetView; no signs created.");
            return;
        }

        var allSigns = new List<ZDO>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in scene.m_prefabs)
        {
            if (!p || !known.Add(p.name) || p.GetComponent<Sign>() == null)
                continue;
            if (!Collect(p.name, allSigns))
            {
                log.LogError($"BarrierSigns sign prefab scan incomplete ({p.name}); refusing modifications.");
                return;
            }
        }
        var manualSigns = allSigns.Where(z => z != null && z.IsValid() &&
            !z.GetBool(MarkerKey, false)).ToArray();

        // Remove only our own orphaned objects. A missing original barrier
        // is the only ordinary reason to delete an otherwise valid auto sign.
        int removed = 0, updated = 0, created = 0, manualOverrides = 0;
        int emptyFilled = 0, manualDeferred = 0, skipped = 0;
        foreach (var entry in Active.ToArray())
        {
            var barrier = manager.GetZDO(entry.BarrierId);
            var sign = manager.GetZDO(entry.SignId);
            if (barrier == null || !barrier.IsValid())
            {
                if (RemoveOwned(sign)) removed++;
                Active.Remove(entry);
                if (debug) log.LogInfo($"BarrierSigns parent destroyed: barrier={entry.BarrierId}, sign={entry.SignId}, drops=0.");
            }
            else if (sign == null || !sign.IsValid() ||
                !sign.GetBool(MarkerKey, false))
            {
                Active.Remove(entry);
                if (debug) log.LogInfo($"BarrierSigns missing auto sign: barrier={entry.BarrierId}, sign={entry.SignId}.");
            }
        }

        var targets = requested.Where(t => t != null && t.Animal != null &&
            t.Animal.IsValid() && t.Barrier != null && t.Barrier.IsValid())
            .OrderBy(t => t.Animal.m_uid.ToString(), StringComparer.Ordinal).ToArray();

        // Group targets whose seed animals share the same local pen area.
        // This is intentionally not claimed as a 3D-verified pen boundary.
        var clusters = new List<Cluster>();
        foreach (var target in targets)
        {
            var matching = clusters.Where(c => c.Targets.Any(t =>
                FlatDistanceSquared(t.Viewer, target.Viewer) <= GroupRadius * GroupRadius))
                .ToArray();
            Cluster cluster;
            if (matching.Length == 0)
            {
                cluster = new Cluster();
                clusters.Add(cluster);
            }
            else
            {
                cluster = matching[0];
                foreach (var other in matching.Skip(1))
                {
                    cluster.Targets.AddRange(other.Targets);
                    cluster.AnimalIds.UnionWith(other.AnimalIds);
                    clusters.Remove(other);
                }
            }
            cluster.Targets.Add(target);
            cluster.AnimalIds.Add(target.Animal.m_uid);
        }

        foreach (var cluster in clusters)
        {
            cluster.Center = new Vector3(
                cluster.Targets.Average(t => t.Viewer.x),
                cluster.Targets.Average(t => t.Viewer.y),
                cluster.Targets.Average(t => t.Viewer.z));
        }

        // Preserve association by the actual animal ZDO identity; movement
        // cannot create extra signs simply by selecting a different fence.
        var handled = new HashSet<Managed>();
        foreach (var group in clusters.OrderBy(c => c.Primary.Animal.m_uid.ToString(),
            StringComparer.Ordinal))
        {
            var matching = Active.Where(entry =>
                entry.Animals.Overlaps(group.AnimalIds) ||
                FlatDistanceSquared(entry.Center, group.Center) <=
                    GroupRadius * GroupRadius).ToArray();

            // If an old version created several automatic signs for animals
            // in the same pen, keep one and delete the duplicates.
            Managed? keep = matching.FirstOrDefault(entry =>
                entry.Animals.Overlaps(group.AnimalIds)) ?? matching.FirstOrDefault();
            foreach (var duplicate in matching.Where(entry => entry != keep))
            {
                if (RemoveOwned(manager.GetZDO(duplicate.SignId))) removed++;
                Active.Remove(duplicate);
                if (debug) log.LogInfo($"BarrierSigns removed duplicate in group: barrier={duplicate.BarrierId}, sign={duplicate.SignId}, drops=0.");
            }

            // A manual sign on ANY of the nearby fence pieces overrides the
            // group, even when the automatic sign belongs to a different
            // individual fence section.
            var nearbyBarriers = group.Targets.SelectMany(t => t.NearbyBarriers)
                .Where(z => z != null && z.IsValid())
                .GroupBy(z => z.m_uid).Select(g => g.First()).ToArray();

            var manuallyPlaced = manualSigns.Where(sign =>
                FlatDistanceSquared(sign.GetPosition(), group.Center) <=
                    ManualSearchRadius * ManualSearchRadius &&
                nearbyBarriers.Any(barrier =>
                    (sign.GetPosition() - barrier.GetPosition()).sqrMagnitude <=
                        ManualOnBarrierRadius * ManualOnBarrierRadius))
                .OrderBy(sign => FlatDistanceSquared(sign.GetPosition(), group.Center))
                .FirstOrDefault();

            if (manuallyPlaced != null)
            {
                string textToInherit = group.Primary.Text;
                if (keep != null)
                {
                    var oldAuto = manager.GetZDO(keep.SignId);
                    if (oldAuto != null && oldAuto.IsValid())
                        textToInherit = oldAuto.GetString(ZDOVars.s_text, textToInherit);
                    if (RemoveOwned(oldAuto)) removed++;
                    Active.Remove(keep);
                    if (debug) log.LogInfo($"BarrierSigns auto removed for manual override: parent={keep.BarrierId}, auto={keep.SignId}, manual={manuallyPlaced.m_uid}, drops=0.");
                }
                string manualText = manuallyPlaced.GetString(ZDOVars.s_text, "");
                if (string.IsNullOrWhiteSpace(manualText))
                {
                    if (net.GetPeers().Any(peer => peer.m_uid == manuallyPlaced.GetOwner()))
                    {
                        manualDeferred++;
                        if (debug) log.LogInfo($"BarrierSigns empty manual sign deferred (online owner): sign={manuallyPlaced.m_uid}.");
                    }
                    else
                    {
                        if (!manuallyPlaced.IsOwner())
                            manuallyPlaced.SetOwner(ZDOMan.GetSessionID());
                        manuallyPlaced.Set(ZDOVars.s_text, textToInherit);
                        emptyFilled++;
                        if (debug) log.LogInfo($"BarrierSigns filled empty manual sign: sign={manuallyPlaced.m_uid}, no auto sign created.");
                    }
                }
                else
                {
                    manualOverrides++;
                    if (debug) log.LogInfo($"BarrierSigns manual override preserved: sign={manuallyPlaced.m_uid}, no auto sign created.");
                }
                continue;
            }

            if (keep != null)
            {
                keep.Animals.UnionWith(group.AnimalIds);
                keep.Center = group.Center;
                handled.Add(keep);
                var auto = manager.GetZDO(keep.SignId);
                if (auto == null || !auto.IsValid() ||
                    !auto.GetBool(MarkerKey, false))
                {
                    Active.Remove(keep);
                    continue;
                }
                // An existing sign follows the diagnostic state, NOT animal
                // position or the selected barrier. No new sign is created.
                if (auto.GetString(ZDOVars.s_text, "") != group.Primary.Text)
                {
                    auto.Set(ZDOVars.s_text, group.Primary.Text);
                    updated++;
                }
                continue;
            }

            if (Active.Count >= MaxSigns)
            {
                skipped++;
                continue;
            }

            var chosen = group.Primary;
            var made = Create(chosen.Barrier, chosen.Viewer, chosen.Text, view);
            if (made == null)
            {
                skipped++;
                continue;
            }
            var entry = new Managed
            {
                BarrierId = chosen.Barrier.m_uid,
                SignId = made.m_uid,
                Center = group.Center
            };
            entry.Animals.UnionWith(group.AnimalIds);
            Active.Add(entry);
            handled.Add(entry);
            created++;
            if (debug) log.LogInfo($"BarrierSigns created: groupAnimals={group.AnimalIds.Count}, barrier={entry.BarrierId}, sign={entry.SignId}, text={chosen.Text.Replace("\n", " | ")}");
        }

        log.LogInfo($"BarrierSigns sweep: groups={clusters.Count}, created={created}, " +
            $"updated={updated}, autoRemoved={removed}, manualOverrides={manualOverrides}, " +
            $"emptyManualFilled={emptyFilled}, manualOwnerDeferred={manualDeferred}, " +
            $"skipped={skipped}, autoActive={Active.Count}, manualSigns={manualSigns.Length}. " +
            "No animals or inventories modified.");
    }

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x, z = a.z - b.z;
        return x * x + z * z;
    }

    private static ZDO? Create(ZDO barrier, Vector3 inside, string text, ZNetView view)
    {
        try
        {
            var pos = barrier.GetPosition();
            var outward = ExteriorNormal(barrier, inside);
            var at = pos + outward * 0.62f + Vector3.up * 1.17f;
            int hash = StableHash("sign");
            var zdo = ZDOMan.instance.CreateNewZDO(at, hash);
            zdo.SetPrefab(hash);
            zdo.Persistent = false;
            zdo.Type = view.m_type;
            zdo.Distant = view.m_distant;
            zdo.SetRotation(Quaternion.LookRotation(outward));
            zdo.Set("HasFields", true);
            zdo.Set("HasFieldsWearNTear", true);
            zdo.Set("WearNTear.m_supports", false);
            zdo.Set("WearNTear.m_noSupportWear", true);
            zdo.Set("WearNTear.m_noRoofWear", true);
            zdo.Set("WearNTear.m_burnable", false);
            zdo.Set("WearNTear.m_health", ProtectedHealth);
            zdo.Set(ZDOVars.s_health, ProtectedHealth);
            zdo.Set("HasFieldsPiece", true);
            zdo.Set("Piece.m_canBeRemoved", false);
            zdo.Set(MarkerKey, true);
            zdo.Set(ParentKey, barrier.m_uid.ToString());
            zdo.Set(ZDOVars.s_text, text);
            return zdo;
        }
        catch
        {
            return null;
        }
    }

    private static Vector3 ExteriorNormal(ZDO barrier, Vector3 inside)
    {
        Vector3 pos = barrier.GetPosition();
        Vector3 fromInside = pos - inside;
        fromInside.y = 0;
        if (fromInside.sqrMagnitude < 0.0001f)
            fromInside = barrier.GetRotation() * Vector3.forward;
        var prefab = ZNetScene.instance.GetPrefab(barrier.GetPrefab());
        Vector3 longest = Vector3.zero;
        float longestSq = 0f;
        if (prefab != null)
        {
            foreach (var box in prefab.GetComponentsInChildren<BoxCollider>(true))
            {
                if (box == null || !box.enabled || box.isTrigger) continue;
                var root = prefab.transform;
                var vx = root.InverseTransformVector(box.transform.TransformVector(
                    new Vector3(box.size.x, 0, 0)));
                var vz = root.InverseTransformVector(box.transform.TransformVector(
                    new Vector3(0, 0, box.size.z)));
                var v = barrier.GetRotation() *
                    (vx.sqrMagnitude >= vz.sqrMagnitude ? vx : vz);
                v.y = 0;
                if (v.sqrMagnitude > longestSq)
                {
                    longest = v;
                    longestSq = v.sqrMagnitude;
                }
            }
        }
        if (longestSq >= 0.04f)
        {
            var normal = Vector3.Cross(Vector3.up, longest).normalized;
            if (Vector3.Dot(normal, fromInside) < 0) normal = -normal;
            return normal;
        }
        return fromInside.normalized;
    }

    private static bool RemoveOwned(ZDO? sign)
    {
        if (sign == null || !sign.IsValid()) return true;
        if (!sign.GetBool(MarkerKey, false)) return false;
        sign.SetOwner(ZDOMan.GetSessionID());
        ZDOMan.instance.DestroyZDO(sign);
        return true;
    }

    private static int RemoveAll()
    {
        int removed = 0;
        foreach (var entry in Active)
            if (RemoveOwned(ZDOMan.instance.GetZDO(entry.SignId))) removed++;
        Active.Clear();
        return removed;
    }

    private static bool Collect(string prefab, List<ZDO> output)
    {
        int index = 0;
        for (int i = 0; i < 1024; i++)
            if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, output, ref index))
                return true;
        return false;
    }

    private static int StableHash(string name)
    {
        unchecked
        {
            int a = 5381, b = 5381;
            for (int i = 0; i < name.Length; i += 2)
            {
                a = ((a << 5) + a) ^ name[i];
                if (i == name.Length - 1) break;
                b = ((b << 5) + b) ^ name[i + 1];
            }
            return a + b * 1566083941;
        }
    }
}
