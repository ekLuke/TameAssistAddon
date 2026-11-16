using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Read-only Valheim ZDO discovery. The main game thread must call this method.
/// This is not an exact loaded-zone detector: player proximity is conservative
/// and must not be used to authorize persistence writes.
/// </summary>
internal static class BoarDiagnostics
{
    private const float ExclusionRadius = 256f;
    internal static void LogSweep(ManualLogSource log)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
            return;

        var found = new List<ZDO>();
        int index = 0;
        bool finished = false;
        // Bounded iterations prevent frame stalls on unexpected API behavior.
        for (int pass = 0; pass < 64 && !finished; pass++)
            finished = ZDOMan.instance.GetAllZDOsWithPrefabIterative("Boar", found, ref index);

        if (!finished)
        {
            log.LogWarning("Boar discovery incomplete; this scan is diagnostic only.");
            return;
        }

        var players = ZNet.instance.GetPlayerList();
        int potentiallyUnloaded = 0;
        int tame = 0;
        foreach (var animal in found)
        {
            if (animal == null || !animal.IsValid()) continue;
            if (animal.GetBool(ZDOVars.s_tamed, false)) tame++;
            var position = animal.GetPosition();
            bool nearPlayer = players.Any(p =>
            {
                var offset = position - p.m_position;
                return offset.sqrMagnitude <= ExclusionRadius * ExclusionRadius;
            });
            if (!nearPlayer) potentiallyUnloaded++;
        }

        log.LogInfo($"Boar ZDO diagnostics: {found.Count} found, {tame} tame, " +
                    $"{potentiallyUnloaded} far from players (NOT proof of unloaded status). " +
                    "No world changes are performed.");
    }
}
