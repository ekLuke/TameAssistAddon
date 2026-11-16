using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Keeps vanilla client-created boar piglets close to their parents using
/// standard ZDO Procreation field overrides, understood by unmodded clients.
/// No custom RPC, client plugin, or client-side gameplay patch.
/// </summary>
internal static class BoarPenBirths
{
    private const string SpawnOffset = "Procreation.m_spawnOffset";
    private const string SpawnOffsetMax = "Procreation.m_spawnOffsetMax";
    private const string HasFields = "HasFields";
    private const string HasProcreationFields = "HasFieldsProcreation";
    private const string AlreadyPlaced = "thelair.tameassistaddon.piggyPlaced";
    private static readonly HashSet<ZDOID> Seen = new HashSet<ZDOID>();

    internal static void Tick(ManualLogSource log, bool birthAtParent, bool moveNewborn,
        float newbornSeconds, bool debug)
    {
        var net = ZNet.instance;
        var scene = ZNetScene.instance;
        var manager = ZDOMan.instance;
        if (net == null || !net.IsServer() || scene == null || manager == null)
            return;
        var boarPrefab = scene.GetPrefab("Boar");
        var procreation = boarPrefab != null ? boarPrefab.GetComponent<Procreation>() : null;
        if (procreation == null) return;

        var adults = Enumerate("Boar");
        var piglets = moveNewborn ? Enumerate("Boar_piggy") : new List<ZDO>();
        var activeAdults = adults.Where(z => z != null && z.IsValid() &&
            z.GetBool(ZDOVars.s_tamed, false) &&
            NearPlayer(z.GetPosition(), 150f) &&
            scene.FindInstance(z) != null).ToList();

        // Apply to tamed parents only; vanilla clients read these fields when
        // their ZNetView is instantiated. Replace only on the first change.
        int parentsChanged = 0, newbornsPlaced = 0;
        foreach (var parent in activeAdults)
        {
            try
            {
                if (!birthAtParent) continue;
                bool changed = false;
                changed |= WriteFloatIfDifferent(parent, SpawnOffset, 0f);
                changed |= WriteFloatIfDifferent(parent, SpawnOffsetMax, 0f);
                if (!changed) continue;
                parent.Set(HasFields, true);
                parent.Set(HasProcreationFields, true);
                Replace(parent, parent.GetPosition());
                parentsChanged++;
            }
            catch (Exception ex)
            {
                log.LogWarning("BoarPenBirths skipped parent: " + ex.Message);
            }
        }

        if (moveNewborn)
        {
            Seen.Clear();
            foreach (var baby in piglets)
            {
                if (baby == null || !baby.IsValid() || !Seen.Add(baby.m_uid) ||
                    !baby.GetBool(ZDOVars.s_tamed, false) ||
                    baby.GetBool(AlreadyPlaced, false) ||
                    !NearPlayer(baby.GetPosition(), 150f))
                    continue;
                try
                {
                    long born = baby.GetLong(ZDOVars.s_spawnTime, 0L);
                    double age = born > 0L ?
                        (net.GetTime().Ticks - born) / (double)TimeSpan.TicksPerSecond :
                        double.MaxValue;
                    if (age < 0 || age > newbornSeconds) continue;
                    var pos = baby.GetPosition();
                    var parent = activeAdults.Where(a => a.IsValid() &&
                        (a.GetPosition() - pos).sqrMagnitude <= 16f)
                        .OrderBy(a => (a.GetPosition() - pos).sqrMagnitude)
                        .FirstOrDefault();
                    if (parent == null) continue;
                    var partner = activeAdults.Where(a => a != parent && a.IsValid() &&
                        (a.GetPosition() - parent.GetPosition()).sqrMagnitude <=
                        Mathf.Pow(Mathf.Max(3f, procreation.m_partnerCheckRange) + 5f, 2f))
                        .OrderBy(a => (a.GetPosition() - parent.GetPosition()).sqrMagnitude)
                        .FirstOrDefault();
                    if (partner == null) continue;
                    // Midpoint is safely between the two penned adults.
                    Vector3 dest = (parent.GetPosition() + partner.GetPosition()) * 0.5f;
                    dest.y = Mathf.Max(parent.GetPosition().y, partner.GetPosition().y) + 0.5f;
                    baby.Set(AlreadyPlaced, true);
                    Replace(baby, dest);
                    newbornsPlaced++;
                }
                catch (Exception ex)
                {
                    log.LogWarning("BoarPenBirths skipped newborn: " + ex.Message);
                }
            }
        }

        if (debug && (parentsChanged > 0 || newbornsPlaced > 0))
            log.LogInfo($"Boar pen births: parentsUpdated={parentsChanged}, pigletsPlaced={newbornsPlaced}");
    }

    private static bool WriteFloatIfDifferent(ZDO zdo, string field, float wanted)
    {
        if (Mathf.Approximately(zdo.GetFloat(field, float.NaN), wanted))
            return false;
        zdo.Set(field, wanted);
        return true;
    }

    private static bool NearPlayer(Vector3 pos, float range)
    {
        float sqr = range * range;
        return ZNet.instance.GetPeers().Any(p =>
            (p.m_refPos - pos).sqrMagnitude < sqr);
    }

    private static List<ZDO> Enumerate(string prefab)
    {
        var result = new List<ZDO>();
        int index = 0;
        for (int i = 0; i < 1024; i++)
            if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, result, ref index))
                return result;
        return new List<ZDO>();
    }

    private static void Replace(ZDO original, Vector3 destination)
    {
        var pkg = new ZPackage();
        original.Serialize(pkg);
        pkg.SetPos(0);
        var replacement = ZDOMan.instance.CreateNewZDO(destination, original.GetPrefab());
        replacement.Deserialize(pkg);
        replacement.SetPosition(destination);
        replacement.SetOwner(0L);
        original.SetOwnerInternal(ZDOMan.GetSessionID());
        ZDOMan.instance.DestroyZDO(original);
    }
}
