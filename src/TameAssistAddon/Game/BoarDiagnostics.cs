using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Read-only diagnostics for tamed or partly-tamed boars only.
/// TameableRegistryProcessor defines taming as remaining time strictly
/// smaller than full taming duration; an unprogressed wild boar is ignored.
/// </summary>
internal static class BoarDiagnostics
{
    private const float ProximityRadius = 256f;

    internal static void LogSweep(ManualLogSource log)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() ||
            ZDOMan.instance == null || ZNetScene.instance == null)
            return;

        var prefab = ZNetScene.instance.GetPrefab("Boar");
        var tameable = prefab != null ? prefab.GetComponent<Tameable>() : null;
        if (tameable == null)
        {
            log.LogWarning("Boar diagnostics: Tameable prefab not available yet.");
            return;
        }

        var found = new List<ZDO>();
        int index = 0;
        bool finished = false;
        for (int pass = 0; pass < 64 && !finished; pass++)
            finished = ZDOMan.instance.GetAllZDOsWithPrefabIterative("Boar", found, ref index);
        if (!finished)
        {
            log.LogWarning("Boar diagnostics: scan incomplete; no counts reported.");
            return;
        }

        int tamed = 0, taming = 0, loaded = 0, nearby = 0;
        int skippedWild = 0;
        foreach (var animal in found)
        {
            if (animal == null || !animal.IsValid()) continue;

            bool isTamed = animal.GetBool(ZDOVars.s_tamed, false);
            // The ZDO field may override prefab settings (e.g. TameAssist multiplier).
            float fullTime = animal.GetFloat("tamingTime", tameable.m_tamingTime);
            float remaining = animal.GetFloat(ZDOVars.s_tameTimeLeft, fullTime);
            bool inProgress = !isTamed && fullTime > 0 && remaining >= 0 && remaining < fullTime;
            if (!isTamed && !inProgress)
            {
                skippedWild++;
                continue;
            }

            if (isTamed) tamed++;
            else taming++;

            if (ZNetScene.instance.FindInstance(animal) != null) loaded++;
            var position = animal.GetPosition();
            if (ZNet.instance.GetPlayerList().Any(p =>
                (position - p.m_position).sqrMagnitude <= ProximityRadius * ProximityRadius))
                nearby++;
        }

        log.LogInfo($"Boar diagnostics (managed only): tamed={tamed}, taming={taming}, " +
            $"loaded={loaded}, within256mOfPlayer={nearby}, wildExcluded={skippedWild}. " +
            "Read-only; proximity is NOT proof of ownership or zone activity.");
    }
}
