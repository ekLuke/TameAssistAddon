using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using TheLair.TameAssistAddon.Core;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// EXPERIMENTAL server-side write adapter for vanilla Boar only.
/// Disabled by default; must be validated on a disposable world before use.
/// Does not alter TameAssist or introduce custom client-visible prefabs.
/// </summary>
internal static class ExperimentalBoarRanch
{
    private const float ClientExclusionRadius = 256f;
    private const string LastTickKey = "thelair.tameassistaddon.lastTick";
    private const string LastBirthKey = "thelair.tameassistaddon.lastBirth";
    private static readonly string[] AcceptedFoods = { "Raspberry", "Blueberries", "Mushroom", "Carrot", "Turnip" };

    internal static void Tick(
        ManualLogSource log, TameAssistConfiguration settings, float speed,
        float catchupHours, float groundRadius, float chestRadius,
        int maxPopulation)
    {
        var zdoMan = ZDOMan.instance;
        var net = ZNet.instance;
        var scene = ZNetScene.instance;
        if (zdoMan == null || net == null || !net.IsServer() || scene == null)
            return;

        var prefab = scene.GetPrefab("Boar");
        var childPrefab = scene.GetPrefab("Boar_piggy");
        if (!prefab || !childPrefab) return;

        var tameable = prefab.GetComponent<Tameable>();
        var procreation = prefab.GetComponent<Procreation>();
        var childView = childPrefab.GetComponent<ZNetView>();
        if (!tameable || !procreation || !childView) return;

        var adults = Collect("Boar");
        var young = Collect("Boar_piggy");
        // Public prefab-indexed enumeration; avoids private ZDOMan fields.
        var sourceNames = new HashSet<string>(AcceptedFoods, StringComparer.Ordinal);
        foreach (var sourcePrefab in scene.m_prefabs)
            if (sourcePrefab && sourcePrefab.GetComponent<Container>())
                sourceNames.Add(sourcePrefab.name);
        var worldObjects = new List<ZDO>();
        foreach (var sourceName in sourceNames)
            worldObjects.AddRange(Collect(sourceName));
        long now = net.GetTime().Ticks;
        int affected = 0, births = 0, fed = 0;

        foreach (var animal in adults)
        {
            try
            {
                if (!animal.IsValid() || !IsFarFromPlayers(animal.GetPosition()))
                    continue;

                // Never run over a live object, even if a peer's position is stale.
                if (scene.FindInstance(animal) != null)
                    continue;

                long previous = animal.GetLong(LastTickKey, now);
                if (previous > now) previous = now;
                double elapsed = Math.Min((now - previous) / (double)TimeSpan.TicksPerSecond,
                    Math.Max(0, catchupHours) * 3600d) * Math.Max(0, speed) / 100d;
                if (elapsed < 1) { EnsureOwner(animal); animal.Set(LastTickKey, now); continue; }

                EnsureOwner(animal);
                animal.Set(LastTickKey, now);
                bool tamed = animal.GetBool(ZDOVars.s_tamed, false);
                long lastFed = animal.GetLong(ZDOVars.s_tameLastFeeding, 0L);
                double fedDuration = tameable.m_fedDuration * settings.FedDurationMultiplier;
                bool hungry = lastFed <= 0 || (now - lastFed) / (double)TimeSpan.TicksPerSecond > fedDuration;

                if (hungry && TryFeed(animal, worldObjects, now, settings,
                    groundRadius, chestRadius))
                {
                    fed++;
                    hungry = false;
                }
                if (hungry) continue;

                // Only credit time during which this animal was actually fed.
                // Feeding at the end of a long absence must NOT retroactively
                // award hours of taming or breeding progress.
                long effectiveFedAt = animal.GetLong(ZDOVars.s_tameLastFeeding, 0L);
                double currentSeconds = now / (double)TimeSpan.TicksPerSecond;
                double fedStartSeconds = effectiveFedAt / (double)TimeSpan.TicksPerSecond;
                double fedElapsed = Math.Max(0d,
                    Math.Min(currentSeconds, fedStartSeconds + fedDuration) -
                    Math.Max(currentSeconds - elapsed, fedStartSeconds));
                if (fedElapsed <= 0d) continue;

                if (!tamed)
                {
                    float duration = tameable.m_tamingTime * settings.TamingTimeMultiplier;
                    float left = animal.GetFloat(ZDOVars.s_tameTimeLeft, duration);
                    if (left > 0)
                    {
                        left = Math.Max(0f, left - (float)fedElapsed);
                        animal.Set(ZDOVars.s_tameTimeLeft, left);
                        affected++;
                    }
                    if (left <= 0)
                    {
                        animal.Set(ZDOVars.s_tamed, true);
                        affected++;
                    }
                    continue;
                }

                long pregnant = animal.GetLong(ZDOVars.s_pregnant, 0L);
                if (pregnant > 0)
                {
                    if ((now - pregnant) / (double)TimeSpan.TicksPerSecond < procreation.m_pregnancyDuration)
                        continue;
                    if (CountNearby(animal.GetPosition(), adults, young, procreation.m_totalCheckRange) >=
                        maxPopulation)
                        continue;
                    // Clear pregnancy and save birth marker BEFORE creating the baby;
                    // this chooses possible lost birth over duplicate birth on a crash.
                    animal.Set(ZDOVars.s_pregnant, 0L);
                    animal.Set(LastBirthKey, now);
                    var baby = CreateBaby(animal, childPrefab, childView, now, procreation.m_minOffspringLevel);
                    young.Add(baby);
                    births++; affected++;
                    continue;
                }

                int nearby = CountNearby(animal.GetPosition(), adults, young, procreation.m_totalCheckRange);
                if (nearby >= Math.Min(maxPopulation, procreation.m_maxCreatures)) continue;
                bool partner = adults.Any(other => other != animal && other.IsValid() &&
                    other.GetBool(ZDOVars.s_tamed, false) &&
                    other.GetLong(ZDOVars.s_pregnant, 0L) <= 0 &&
                    Vector3.Distance(animal.GetPosition(), other.GetPosition()) <= Math.Max(3, procreation.m_partnerCheckRange) &&
                    IsFed(other, now, fedDuration) && IsFarFromPlayers(other.GetPosition()));
                if (!partner) continue;

                int attempts = Math.Min(3600, (int)(fedElapsed / Math.Max(1f, procreation.m_updateInterval)));
                int points = animal.GetInt(ZDOVars.s_lovePoints, 0);
                for (int i = 0; i < attempts; i++)
                {
                    // Simulate the vanilla skip chance; no Unity random state is modified.
                    if (DeterministicRoll(animal.m_uid.ToString(), previous, i) <= procreation.m_pregnancyChance)
                        continue;
                    points++;
                    if (points < procreation.m_requiredLovePoints) continue;
                    points = 0;
                    animal.Set(ZDOVars.s_pregnant, now);
                    break;
                }
                animal.Set(ZDOVars.s_lovePoints, points);
                if (attempts > 0) affected++;
            }
            catch (Exception ex)
            {
                log.LogWarning($"Skipped one boar ZDO after error: {ex.Message}");
            }
        }
        if (affected > 0 || births > 0 || fed > 0)
            log.LogInfo($"Experimental unloaded boars: state updates={affected}, feeds={fed}, births={births}");
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

    private static bool IsFarFromPlayers(Vector3 point)
    {
        var radiusSqr = ClientExclusionRadius * ClientExclusionRadius;
        return !ZNet.instance.GetPeers().Any(peer =>
            (peer.m_refPos - point).sqrMagnitude < radiusSqr);
    }

    private static bool IsFed(ZDO animal, long now, double durationSeconds)
    {
        long fedAt = animal.GetLong(ZDOVars.s_tameLastFeeding, 0L);
        return fedAt > 0 && fedAt <= now &&
            (now - fedAt) / (double)TimeSpan.TicksPerSecond <= durationSeconds;
    }

    private static void EnsureOwner(ZDO obj)
    {
        if (!obj.IsOwner())
            obj.SetOwner(ZDOMan.GetSessionID());
    }

    private static int CountNearby(Vector3 point, IEnumerable<ZDO> adult, IEnumerable<ZDO> young, float radius)
    {
        float sqr = radius * radius;
        return adult.Concat(young).Count(a => a.IsValid() &&
            (a.GetPosition() - point).sqrMagnitude <= sqr);
    }

    private static bool TryFeed(ZDO animal, IReadOnlyList<ZDO> all, long now,
        TameAssistConfiguration settings, float groundRadius, float chestRadius)
    {
        var origin = animal.GetPosition();
        var maxRadius = Math.Max(groundRadius, chestRadius);
        foreach (var food in all.Where(o => o.IsValid() &&
            (o.GetPosition() - origin).sqrMagnitude <= maxRadius * maxRadius)
            .OrderBy(o => (o.GetPosition() - origin).sqrMagnitude))
        {
            if (!food.IsValid() || !IsFarFromPlayers(food.GetPosition()) ||
                ZNetScene.instance.FindInstance(food) != null)
                continue;
            var pos = food.GetPosition();
            float distance = (pos - origin).sqrMagnitude;
            var gamePrefab = ZNetScene.instance.GetPrefab(food.GetPrefab());
            if (!gamePrefab) continue;

            bool droppedFood = distance <= groundRadius * groundRadius &&
                AcceptedFoods.Contains(gamePrefab.name, StringComparer.OrdinalIgnoreCase) &&
                gamePrefab.GetComponent<ItemDrop>();
            if (droppedFood)
            {
                EnsureOwner(food);
                var count = food.GetInt("stack", 1);
                if (count < 1) continue;
                // Feed first so a crash cannot erase food without saving the fed state.
                animal.Set(ZDOVars.s_tameLastFeeding, now);
                if (count == 1) ZDOMan.instance.DestroyZDO(food);
                else food.Set("stack", count - 1);
                return true;
            }

            if (!settings.FeedFromContainers || chestRadius <= 0 ||
                distance > chestRadius * chestRadius ||
                !gamePrefab.GetComponent<Container>() ||
                food.GetInt(ZDOVars.s_inUse, 0) != 0)
                continue;

            // Only an unoccupied, unloaded container can be modified.
            byte[] raw = food.GetByteArray(ZDOVars.s_items, null);
            if (raw == null || raw.Length == 0) continue;
            var inventory = new Inventory("TameAssistAddon", null, 16, 16);
            inventory.Load(new ZPackage(raw));
            var slot = inventory.GetAllItems().FirstOrDefault(item =>
                item.m_dropPrefab &&
                AcceptedFoods.Contains(item.m_dropPrefab.name, StringComparer.OrdinalIgnoreCase) &&
                item.m_stack > settings.FeedFromContainersLeaveAtLeast);
            if (slot == null) continue;
            EnsureOwner(food);
            animal.Set(ZDOVars.s_tameLastFeeding, now);
            slot.m_stack--;
            if (slot.m_stack <= 0) inventory.RemoveItem(slot);
            var package = new ZPackage();
            inventory.Save(package);
            food.Set(ZDOVars.s_items, package.GetArray());
            return true;
        }
        return false;
    }

    private static ZDO CreateBaby(ZDO mother, GameObject prefab, ZNetView view, long now, int minLevel)
    {
        var position = mother.GetPosition() + new Vector3(0.5f, 0.8f, 0.5f);
        int hash = StableHash(prefab.name);
        ZDO baby = ZDOMan.instance.CreateNewZDO(position, hash);
        baby.SetPrefab(hash);
        baby.Persistent = view.m_persistent;
        baby.Type = view.m_type;
        baby.Distant = view.m_distant;
        baby.Set(ZDOVars.s_tamed, true);
        baby.Set(ZDOVars.s_level, Math.Max(minLevel, mother.GetInt(ZDOVars.s_level, 1)));
        baby.Set(ZDOVars.s_spawnTime, now);
        return baby;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            int a = 5381, b = 5381;
            for (int i = 0; i < text.Length && text[i] != '\0'; i += 2)
            {
                a = ((a << 5) + a) ^ text[i];
                if (i == text.Length - 1 || text[i + 1] == '\0') break;
                b = ((b << 5) + b) ^ text[i + 1];
            }
            return a + b * 1566083941;
        }
    }

    private static double DeterministicRoll(string id, long tick, int step)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in id) hash = (hash ^ c) * 16777619;
            hash = (hash ^ (uint)tick) * 16777619;
            hash = (hash ^ (uint)(tick >> 32)) * 16777619;
            hash = (hash ^ (uint)step) * 16777619;
            return (hash & 0xffffff) / 16777216d;
        }
    }
}
