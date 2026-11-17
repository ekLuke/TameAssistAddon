using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Read-only inventory of species with Valheim tame/breed/growth components.
/// This does NOT assert an animal is inside a fenced pen; perimeter geometry
/// must be verified separately before any authoritative offline writes.
/// </summary>
internal static class RanchAnimalDiagnostics
{
    internal static void LogSweep(ManualLogSource log)
    {
        var scene = ZNetScene.instance;
        var manager = ZDOMan.instance;
        if (scene == null || manager == null || ZNet.instance == null ||
            !ZNet.instance.IsServer()) return;

        var species = scene.m_prefabs
            .Where(go => go != null &&
                (go.GetComponent<Tameable>() != null ||
                 go.GetComponent<Procreation>() != null))
            .OrderBy(go => go.name, StringComparer.Ordinal)
            .ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefab in species)
        {
            if (!names.Add(prefab.name)) continue;
            var tameable = prefab.GetComponent<Tameable>();
            var procreation = prefab.GetComponent<Procreation>();
            if (tameable == null && procreation == null) continue;

            var list = Collect(prefab.name, out bool complete);
            if (!complete)
            {
                log.LogWarning($"Ranch species scan incomplete: {prefab.name}. No count reported.");
                continue;
            }
            int tame = 0, taming = 0, wild = 0, loaded = 0;
            foreach (var zdo in list)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                bool isTamed = zdo.GetBool(ZDOVars.s_tamed, false);
                if (isTamed) tame++;
                else if (tameable != null)
                {
                    float duration = zdo.GetFloat("tamingTime", tameable.m_tamingTime);
                    float left = zdo.GetFloat(ZDOVars.s_tameTimeLeft, duration);
                    if (duration > 0f && left >= 0f && left < duration) taming++;
                    else wild++;
                }
                else wild++;
                if (scene.FindInstance(zdo) != null) loaded++;
            }
            var babyPrefab = procreation != null ? procreation.m_offspring : null;
            string babyName = babyPrefab != null ? babyPrefab.name : "none";
            // Some species produce an egg ItemDrop instead of a character;
            // an egg requires incubation/roof/heat rules, not Growup.
            bool babyComplete = true;
            var young = babyPrefab != null ? Collect(babyName, out babyComplete) : null;
            string youngCount = !babyComplete ? "scan-incomplete" :
                young == null ? "not-applicable" :
                young.Count(z => z != null && z.IsValid()).ToString();
            log.LogInfo($"Ranch species (world-wide, read-only): species={prefab.name}, " +
                $"tamed={tame}, taming={taming}, wild={wild}, loaded={loaded}, " +
                $"offspringPrefab={babyName}, offspringZdos={youngCount}. " +
                "NO verified pen membership; no offline writes.");
        }
    }

    private static List<ZDO> Collect(string prefab, out bool complete)
    {
        var found = new List<ZDO>();
        int index = 0;
        for (int pass = 0; pass < 1024; pass++)
            if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, found, ref index))
            {
                complete = true;
                return found;
            }
        complete = false;
        return found;
    }
}
