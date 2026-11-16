using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using TheLair.TameAssistAddon.Core;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Experimental feeding for wild boars *currently being tamed*.
/// Uses live, server-owned objects only. Does not seize ownership,
/// patch TameAssist, or write an unloaded inventory blob.
/// Animals/chests owned by a client are deliberately skipped.
/// </summary>
internal static class TamingChestFeed
{
    private static readonly HashSet<string> AcceptedFoods = new HashSet<string>(
        new[] { "Raspberry", "Blueberries", "Mushroom", "Carrot", "Turnip" },
        StringComparer.OrdinalIgnoreCase);

    internal static void Tick(ManualLogSource log, TameAssistConfiguration settings, float overrideRange)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() ||
            ZNetScene.instance == null || ZDOMan.instance == null)
            return;

        // Stay aligned with the user's TameAssist setting.
        if (!settings.FeedFromContainers) return;

        float range = overrideRange > 0 ? overrideRange : settings.FeedFromContainersRange;
        if (range <= 0) return;

        var scene = ZNetScene.instance;
        var boars = Collect("Boar");
        var chestZdos = new List<ZDO>();
        foreach (var prefab in scene.m_prefabs)
        {
            if (prefab != null && prefab.GetComponent<Container>() != null)
                chestZdos.AddRange(Collect(prefab.name));
        }

        int feeds = 0, skippedClientOwnership = 0;
        double nowSeconds = ZNet.instance.GetTimeSeconds();
        foreach (var animalZdo in boars)
        {
            if (!animalZdo.IsValid() || animalZdo.GetBool(ZDOVars.s_tamed, false))
                continue;

            var animalObject = scene.FindInstance(animalZdo);
            if (animalObject == null) continue;
            var animalView = animalObject.GetComponent<ZNetView>();
            var tameable = animalObject.GetComponent<Tameable>();
            var ai = animalObject.GetComponent<MonsterAI>();
            if (animalView == null || tameable == null || ai == null) continue;
            if (!animalView.IsOwner())
            {
                skippedClientOwnership++;
                continue;
            }

            var lastFedTicks = animalZdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            double lastFedSeconds = lastFedTicks / (double)TimeSpan.TicksPerSecond;
            if (lastFedTicks > 0 && nowSeconds - lastFedSeconds <= tameable.m_fedDuration)
                continue;

            // Use the actual prefab's accepted food list, not arbitrary items.
            var edibleNames = new HashSet<string>(
                ai.m_consumeItems.Where(x => x != null).Select(x => x.name),
                StringComparer.OrdinalIgnoreCase);
            edibleNames.IntersectWith(AcceptedFoods);
            if (edibleNames.Count == 0) continue;

            var pos = animalZdo.GetPosition();
            foreach (var chestZdo in chestZdos.OrderBy(z => (z.GetPosition() - pos).sqrMagnitude))
            {
                if (!chestZdo.IsValid() || (chestZdo.GetPosition() - pos).sqrMagnitude > range * range)
                    continue;

                var chestObject = scene.FindInstance(chestZdo);
                if (chestObject == null) continue;
                var chestView = chestObject.GetComponent<ZNetView>();
                var container = chestObject.GetComponent<Container>();
                if (chestView == null || container == null || !chestView.IsOwner())
                    continue;
                if (chestZdo.GetBool(ZDOVars.s_inUse, false))
                    continue;

                var inventory = container.GetInventory();
                var foods = inventory.GetAllItems().Where(x =>
                    x.m_dropPrefab != null &&
                    edibleNames.Contains(x.m_dropPrefab.name)).ToList();

                // Respect TameAssist's per-food reserve across all stacks.
                int total = foods.Sum(x => x.m_stack);
                if (total <= settings.FeedFromContainersLeaveAtLeast)
                    continue;

                var selected = foods.FirstOrDefault(x => x.m_stack > 0);
                if (selected == null) continue;

                // Update the game inventory through the live Container API.
                // This avoids overwriting inventory ZDO serialization while a
                // chest is being used and lets vanilla save/notify the container.
                inventory.RemoveItem(selected, 1);
                container.Save();
                animalZdo.Set(ZDOVars.s_tameLastFeeding, ZNet.instance.GetTime().Ticks);
                feeds++;
                break;
            }
        }

        if (feeds > 0)
            log.LogInfo($"Taming chest feed: fed {feeds} wild boars.");
        if (skippedClientOwnership > 0)
            log.LogDebug($"Taming chest feed: skipped {skippedClientOwnership} client-owned boars (vanilla PS5/PC ownership not overridden).");
    }

    private static List<ZDO> Collect(string prefab)
    {
        var list = new List<ZDO>();
        int index = 0;
        for (int i = 0; i < 1024; i++)
            if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, list, ref index))
                break;
        return list;
    }
}
