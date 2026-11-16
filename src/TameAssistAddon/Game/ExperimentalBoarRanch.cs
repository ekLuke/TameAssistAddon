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
        int maxPopulation, bool confineToPen, Vector3 penMin, Vector3 penMax,
        bool enableUnprotectedBirthGrowth)
    {
        var zdoMan = ZDOMan.instance;
        var net = ZNet.instance;
        var scene = ZNetScene.instance;
        if (zdoMan == null || net == null || !net.IsServer() || scene == null)
            return;

        // Replay incomplete offline feeding before any new world changes.
        OfflineFeedJournal.EnsureRecovered(log);
        if (!OfflineFeedJournal.Ready)
            return;

        var prefab = scene.GetPrefab("Boar");
        var childPrefab = scene.GetPrefab("Boar_piggy");
        if (!prefab || !childPrefab) return;

        var tameable = prefab.GetComponent<Tameable>();
        var procreation = prefab.GetComponent<Procreation>();
        var childView = childPrefab.GetComponent<ZNetView>();
        var adultView = prefab.GetComponent<ZNetView>();
        var growup = childPrefab.GetComponent<Growup>();
        if (!tameable || !procreation || !childView || !adultView) return;

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
        int affected = 0, births = 0, fed = 0, blockedOutsidePen = 0, matured = 0;
        bool validPen = confineToPen && penMax.x > penMin.x + 2f && penMax.z > penMin.z + 2f;

        foreach (var animal in adults)
        {
            try
            {
                if (!animal.IsValid() || !IsFarFromPlayers(animal.GetPosition()))
                    continue;
                // Never simulate animals from arbitrary locations while a
                // pen is configured. This is a dedicated, bounded ranch.
                if (confineToPen && (!validPen || !WithinPen(animal.GetPosition(), penMin, penMax)))
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

                if (hungry && TryFeed(log, animal, worldObjects, now, settings,
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
                    if (!enableUnprotectedBirthGrowth) continue;
                    if ((now - pregnant) / (double)TimeSpan.TicksPerSecond < procreation.m_pregnancyDuration)
                        continue;
                    if (CountNearby(animal.GetPosition(), adults, young, procreation.m_totalCheckRange) >=
                        maxPopulation)
                        continue;
                    var birthPos = animal.GetPosition() + new Vector3(0.5f, 0.8f, 0.5f);
                    if (confineToPen)
                    {
                        // Inset prevents placing a baby directly on the walls.
                        if (!validPen || !TryConstrainBirth(birthPos, penMin, penMax, out birthPos))
                        {
                            blockedOutsidePen++;
                            continue;
                        }
                    }
                    // Clear pregnancy and save birth marker BEFORE creating the baby;
                    // this chooses possible lost birth over duplicate birth on a crash.
                    animal.Set(ZDOVars.s_pregnant, 0L);
                    animal.Set(LastBirthKey, now);
                    var baby = CreateBaby(animal, childPrefab, childView, now, procreation.m_minOffspringLevel, birthPos);
                    young.Add(baby);
                    births++; affected++;
                    continue;
                }

                if (!enableUnprotectedBirthGrowth) continue;
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
        // Offline juvenile maturation uses the prefab's vanilla Growup age.
        // Never touch a client-owned, active or outside-pen juvenile.
        // Only the experimental write mode calls this adapter.
        if (enableUnprotectedBirthGrowth && growup != null && growup.m_growTime > 0f)
        {
            foreach (var baby in young)
            {
                if (matured >= 16) break; // bounded work per tick
                try
                {
                    if (baby == null || !baby.IsValid() ||
                        !IsFarFromPlayers(baby.GetPosition()) ||
                        scene.FindInstance(baby) != null ||
                        (confineToPen && !WithinPen(baby.GetPosition(), penMin, penMax)))
                        continue;
                    long bornAt = baby.GetLong(ZDOVars.s_spawnTime, 0L);
                    if (bornAt <= 0L || bornAt > now ||
                        (now - bornAt) / (double)TimeSpan.TicksPerSecond < growup.m_growTime)
                        continue;
                    // If the original client ownership is still associated
                    // with an active peer, skip rather than stealing it.
                    if (net.GetPeers().Any(peer => peer.m_uid == baby.GetOwner()))
                        continue;

                    var position = baby.GetPosition();
                    var level = baby.GetInt(ZDOVars.s_level, 1);
                    var wasTamed = baby.GetBool(ZDOVars.s_tamed, false);
                    int adultHash = StableHash(prefab.name);
                    var adult = zdoMan.CreateNewZDO(position, adultHash);
                    adult.SetPrefab(adultHash);
                    adult.Persistent = adultView.m_persistent;
                    adult.Type = adultView.m_type;
                    adult.Distant = adultView.m_distant;
                    adult.Set(ZDOVars.s_level, level);
                    adult.Set(ZDOVars.s_tamed, wasTamed);
                    adult.Set(ZDOVars.s_spawnTime, now);
                    // Prefer a leaked juvenile over duplicate future adult
                    // creation if deletion fails; report all errors.
                    EnsureOwner(baby);
                    zdoMan.DestroyZDO(baby);
                    matured++;
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Experimental offline juvenile growth skipped after error: {ex.Message}");
                }
            }
        }
        if (affected > 0 || births > 0 || fed > 0 || blockedOutsidePen > 0 || matured > 0)
            log.LogInfo($"Experimental unloaded boars: state updates={affected}, feeds={fed}, births={births}, matured={matured}, blockedBirthsOutsidePen={blockedOutsidePen}");
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

    private static bool TryFeed(ManualLogSource log, ZDO animal, IReadOnlyList<ZDO> all, long now,
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
                var count = food.GetInt("stack", 1);
                // Keep the final ground item intact: destroying its ZDO
                // cannot be safely undone from a stack-only snapshot.
                if (count <= 1) continue;
                if (OfflineFeedJournal.TryCommitStack(log, animal, food, now, count))
                    return true;
                if (!OfflineFeedJournal.Ready) return false;
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
            // A slot may not be deleted while the offline WAL only carries
            // an inventory byte snapshot and no independently saved prefab.
            if (slot.m_stack <= 1) continue;
            slot.m_stack--;
            var package = new ZPackage();
            inventory.Save(package);
            if (OfflineFeedJournal.TryCommit(log, animal, food, now, raw, package.GetArray()))
                return true;
            if (!OfflineFeedJournal.Ready) return false;
        }
        return false;
    }

    private static bool WithinPen(Vector3 position, Vector3 min, Vector3 max)
        => position.x >= min.x && position.x <= max.x &&
           position.z >= min.z && position.z <= max.z;

    private static bool TryConstrainBirth(Vector3 desired, Vector3 min, Vector3 max, out Vector3 result)
    {
        result = desired;
        if (max.x - min.x <= 2f || max.z - min.z <= 2f)
            return false;
        result.x = Mathf.Clamp(desired.x, min.x + 1f, max.x - 1f);
        result.z = Mathf.Clamp(desired.z, min.z + 1f, max.z - 1f);
        return true;
    }

    private static ZDO CreateBaby(ZDO mother, GameObject prefab, ZNetView view, long now, int minLevel, Vector3 position)
    {
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
