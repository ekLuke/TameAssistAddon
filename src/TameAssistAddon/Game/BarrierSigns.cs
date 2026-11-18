using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Server-created vanilla temporary signs for pen barrier diagnostics.
/// A player-built sign near the selected barrier takes precedence; a
/// genuinely empty manual sign receives a single default text fill.
/// No client mod, custom prefab, or custom network RPC is required.
/// </summary>
internal static class BarrierSigns
{
    internal const string MarkerKey = "thelair.tameassistaddon.barrierSign";
    private const string ParentKey = "thelair.tameassistaddon.barrierParent";
    private const float ProtectedHealth = 100000000f;
    private const float ManualSignRadius = 1.8f;
    private const int MaxSigns = 12;
    private static readonly Dictionary<ZDOID, ZDOID> Created = new Dictionary<ZDOID, ZDOID>();
    private static readonly List<ZDOID> RemoveIds = new List<ZDOID>();

    internal sealed class Target
    {
        internal ZDO Barrier = null!;
        internal Vector3 Viewer;
        internal string Text = "";
    }

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
            if (Created.Count > 0) RemoveAll(log, debug);
            return;
        }

        var signPrefab = scene.GetPrefab("sign");
        var signView = signPrefab != null ? signPrefab.GetComponent<ZNetView>() : null;
        if (signPrefab == null || signView == null)
        {
            log.LogWarning("BarrierSigns disabled for this sweep: vanilla 'sign' prefab or ZNetView missing.");
            return;
        }

        var manualSigns = new List<ZDO>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefab in scene.m_prefabs)
        {
            if (!prefab || !seenNames.Add(prefab.name) ||
                prefab.GetComponent<Sign>() == null) continue;
            if (!Collect(prefab.name, manualSigns))
            {
                log.LogWarning("BarrierSigns scan incomplete; preserving existing signs, no new writes.");
                return;
            }
        }

        int created = 0, updated = 0, removed = 0, custom = 0;
        int filled = 0, ownershipBlocked = 0, skipped = 0;

        var uniqueTargets = requested.Where(x => x != null && x.Barrier != null &&
            x.Barrier.IsValid()).GroupBy(x => x.Barrier.m_uid)
            .Select(g => g.First()).Take(MaxSigns).ToArray();

        foreach (var target in uniqueTargets)
        {
            var barrier = target.Barrier;
            var id = barrier.m_uid;
            var manual = manualSigns.Where(s =>
                s != null && s.IsValid() && !s.GetBool(MarkerKey, false) &&
                (s.GetPosition() - barrier.GetPosition()).sqrMagnitude <=
                ManualSignRadius * ManualSignRadius)
                .OrderBy(s => (s.GetPosition() - barrier.GetPosition()).sqrMagnitude)
                .FirstOrDefault();

            if (manual != null)
            {
                if (Created.TryGetValue(id, out var previous))
                {
                    if (RemoveOwned(manager.GetZDO(previous)))
                    {
                        Created.Remove(id);
                        removed++;
                    }
                    else
                    {
                        log.LogWarning($"BarrierSigns cannot replace auto sign for barrier={id}; keeping old mapping.");
                        continue;
                    }
                }

                string current = manual.GetString(ZDOVars.s_text, "");
                if (string.IsNullOrWhiteSpace(current))
                {
                    // Never seize a connected player's manually placed sign.
                    // If it is still client-owned, defer until ownership is
                    // released rather than racing a player's text editor.
                    if (net.GetPeers().Any(peer => peer.m_uid == manual.GetOwner()))
                    {
                        ownershipBlocked++;
                        if (debug) log.LogInfo($"BarrierSigns manual empty sign deferred (online owner): barrier={id}, sign={manual.m_uid}.");
                    }
                    else
                    {
                        if (!manual.IsOwner()) manual.SetOwner(ZDOMan.GetSessionID());
                        manual.Set(ZDOVars.s_text, target.Text);
                        filled++;
                        if (debug) log.LogInfo($"BarrierSigns manually placed empty sign filled: barrier={id}, sign={manual.m_uid}, text={target.Text.Replace("\n", " | ")}");
                    }
                }
                else
                {
                    custom++;
                    if (debug) log.LogInfo($"BarrierSigns preserving manual text: barrier={id}, sign={manual.m_uid}.");
                }
                continue;
            }

            if (Created.TryGetValue(id, out var existingId))
            {
                var current = manager.GetZDO(existingId);
                if (current != null && current.IsValid() && current.GetBool(MarkerKey, false))
                {
                    if (current.GetString(ZDOVars.s_text, "") != target.Text)
                    {
                        current.Set(ZDOVars.s_text, target.Text);
                        updated++;
                        if (debug) log.LogInfo($"BarrierSigns updated: barrier={id}, sign={existingId}.");
                    }
                    continue;
                }
                Created.Remove(id);
            }

            // Never create additional signs just because the selected
            // animal moved to a different fence segment.
            if (Created.Count >= MaxSigns)
            {
                skipped++;
                continue;
            }
            var made = Create(barrier, target.Viewer, target.Text, signView);
            if (made == null)
            {
                skipped++;
                continue;
            }
            Created[id] = made.m_uid;
            created++;
            if (debug)
                log.LogInfo($"BarrierSigns auto-created: barrier={id}, sign={made.m_uid}, " +
                    $"prefab={scene.GetPrefab(barrier.GetPrefab())?.name ?? "unknown"}, " +
                    $"text={target.Text.Replace("\n", " | ")}");
        }

        // Generated signs stay attached to their original barrier even if
        // another animal or fence is chosen on later scan passes. Removing
        // the underlying barrier is the only normal automatic cleanup.
        RemoveIds.Clear();
        foreach (var pair in Created)
        {
            var barrier = manager.GetZDO(pair.Key);
            var sign = manager.GetZDO(pair.Value);
            if (barrier == null || !barrier.IsValid())
            {
                RemoveIds.Add(pair.Key);
                continue;
            }
            if (sign == null || !sign.IsValid() ||
                !sign.GetBool(MarkerKey, false))
                RemoveIds.Add(pair.Key);
        }
        foreach (var id in RemoveIds)
        {
            if (Created.TryGetValue(id, out var signId))
            {
                var barrier = manager.GetZDO(id);
                if ((barrier == null || !barrier.IsValid()) &&
                    RemoveOwned(manager.GetZDO(signId)))
                {
                    removed++;
                    if (debug) log.LogInfo($"BarrierSigns removed after barrier deletion: barrier={id}, sign={signId}, drops=0.");
                }
                Created.Remove(id);
            }
        }

        log.LogInfo($"BarrierSigns sweep: candidates={uniqueTargets.Length}, " +
            $"created={created}, updated={updated}, autoRemoved={removed}, " +
            $"manualOverrides={custom}, emptyManualFilled={filled}, " +
            $"manualOwnerDeferred={ownershipBlocked}, skipped={skipped}, " +
            $"autoActive={Created.Count}, manualSignZdos={manualSigns.Count}. " +
            "Vanilla signs only; no animal, inventory or offline ranch writes.");
    }

    private static ZDO? Create(ZDO barrier, Vector3 viewer, string text, ZNetView view)
    {
        try
        {
            var pos = barrier.GetPosition();
            // The animal is on the inside of the pen. Place and face the
            // sign on the OPPOSITE side of its fence (exterior), not facing
            // back toward the animal. Use the wall's horizontal collider
            // normal so even corner/near-end animals keep it on the wall.
            var toOutside = ExteriorNormal(barrier, viewer);

            // Nonpersistent vanilla sign; survives normal scans but not a
            // world restart. It cannot be harvested as free building mats.
            var at = pos + toOutside * 0.62f + Vector3.up * 1.17f;
            int hash = StableHash("sign");
            var zdo = ZDOMan.instance.CreateNewZDO(at, hash);
            zdo.SetPrefab(hash);
            zdo.Persistent = false;
            zdo.Type = view.m_type;
            zdo.Distant = view.m_distant;
            zdo.SetRotation(Quaternion.LookRotation(toOutside));
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
        if (prefab != null)
        {
            foreach (var box in prefab.GetComponentsInChildren<BoxCollider>(true))
            {
                if (box == null || !box.enabled || box.isTrigger) continue;
                var root = prefab.transform;
                Vector3 vx = root.InverseTransformVector(
                    box.transform.TransformVector(new Vector3(box.size.x, 0f, 0f)));
                Vector3 vz = root.InverseTransformVector(
                    box.transform.TransformVector(new Vector3(0f, 0f, box.size.z)));
                var tangent = barrier.GetRotation() *
                    (vx.sqrMagnitude >= vz.sqrMagnitude ? vx : vz);
                tangent.y = 0;
                if (tangent.sqrMagnitude < 0.04f) continue;
                var normal = Vector3.Cross(Vector3.up, tangent).normalized;
                if (Vector3.Dot(normal, fromInside) < 0) normal = -normal;
                return normal;
            }
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

    private static void RemoveAll(ManualLogSource log, bool debug)
    {
        int removed = 0;
        foreach (var id in Created.Values.ToArray())
            if (RemoveOwned(ZDOMan.instance.GetZDO(id))) removed++;
        Created.Clear();
        if (debug) log.LogInfo($"BarrierSigns disabled; removed {removed} generated temporary signs.");
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
