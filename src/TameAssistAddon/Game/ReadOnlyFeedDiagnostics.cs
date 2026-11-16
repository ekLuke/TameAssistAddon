using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using TheLair.TameAssistAddon.Core;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Bounded, read-only snapshot of vanilla container ZDOs around a taming Boar.
/// Diagnostic estimates do not change TameAssist state or consume inventory.
/// Upstream's per-container sign overrides and indexed inventory are authoritative.
/// </summary>
internal static class ReadOnlyFeedDiagnostics
{
    private const float RefreshChestIndexSeconds = 60f;
    private const float ProbeCooldownSeconds = 30f;
    private const float StaleProbeSeconds = 1200f;

    private static readonly Dictionary<ZDOID, float> NextProbe = new Dictionary<ZDOID, float>();
    private static readonly List<ZDO> Chests = new List<ZDO>();
    private static float _nextRefresh;
    private static int _probed, _diagnosticErrors, _inRange, _inUse, _otherOwned;
    private static int _inventoryReadable, _inventoryUnreadable, _foodPresent, _foodAboveReserve;
    private static int _noChest, _noFood, _maybeAvailable;

    internal static void Probe(ZDO boar, MonsterAI? monsterAi, ManualLogSource? log)
    {
        if (ZNetScene.instance == null || ZDOMan.instance == null || monsterAi == null)
            return;

        var time = Time.unscaledTime;
        if (NextProbe.TryGetValue(boar.m_uid, out var next) && time < next)
            return;
        NextProbe[boar.m_uid] = time + ProbeCooldownSeconds;

        try
        {
            string path = System.IO.Path.Combine(Paths.ConfigPath, "ArgusMagnus.ServersideQoL.TameAssist.cfg");
            var settings = System.IO.File.Exists(path)
                ? TameAssistConfiguration.Read(path)
                : TameAssistConfiguration.Parse("");
            if (!settings.FeedFromContainers || settings.FeedFromContainersRange <= 0f)
            {
                _probed++;
                log?.LogInfo("Taming feed probe: upstream config FeedFromContainers disabled or default range is zero (or config file missing).");
                return;
            }
            RefreshIndex(time);
            var edible = new HashSet<string>(monsterAi.m_consumeItems
                .Where(item => item != null)
                .Select(item => item.name), StringComparer.OrdinalIgnoreCase);
            if (edible.Count == 0)
                return;

            var position = boar.GetPosition();
            float rangeSquared = settings.FeedFromContainersRange * settings.FeedFromContainersRange;
            int nearby = 0, foodStacks = 0, available = 0, unreadable = 0;
            int inUse = 0, remoteOwned = 0;
            foreach (var chest in Chests)
            {
                if (chest == null || !chest.IsValid() ||
                    (chest.GetPosition() - position).sqrMagnitude > rangeSquared)
                    continue;
                var prefab = ZNetScene.instance.GetPrefab(chest.GetPrefab())?.GetComponent<Container>();
                if (prefab == null || prefab.m_privacy == Container.PrivacySetting.Private)
                    continue;
                nearby++;
                if (chest.GetBool(ZDOVars.s_inUse, false))
                {
                    inUse++;
                    continue;
                }
                if (!chest.IsOwner()) remoteOwned++;

                // This reads the serialized ZDO snapshot; it does not create
                // or interact with the live chest and never writes inventory.
                byte[] data = chest.GetByteArray(ZDOVars.s_items);
                if (data == null || data.Length == 0)
                    continue;
                try
                {
                    var inventory = new Inventory(prefab.m_name, prefab.m_bkg,
                        prefab.m_width, prefab.m_height);
                    inventory.Load(new ZPackage(data));
                    int edibleCount = inventory.GetAllItems()
                        .Where(item => item.m_dropPrefab != null && edible.Contains(item.m_dropPrefab.name))
                        .Sum(item => item.m_stack);
                    if (edibleCount > 0) foodStacks++;
                    if (edibleCount > settings.FeedFromContainersLeaveAtLeast)
                        available++;
                }
                catch (Exception)
                {
                    unreadable++;
                }
            }

            _probed++;
            _inRange += nearby;
            _inUse += inUse;
            _otherOwned += remoteOwned;
            _inventoryReadable += nearby - inUse - unreadable;
            _inventoryUnreadable += unreadable;
            _foodPresent += foodStacks;
            _foodAboveReserve += available;
            if (nearby == 0) _noChest++;
            else if (available == 0 && unreadable == 0) _noFood++;
            else if (available > 0) _maybeAvailable++;

            // Summarize once per animal per 30 s, not every processor call.
            log?.LogInfo($"Taming feed probe (read-only): boar={boar.m_uid}, " +
                $"chestsWithinDefaultRange={nearby}, inUse={inUse}, clientOwned={remoteOwned}, " +
                $"unreadableInventory={unreadable}, withAcceptedFood={foodStacks}, " +
                $"withFoodAboveReserve={available}. " +
                "ZDO snapshots only; a matching chest is NOT proof TameAssist can use it.");
        }
        catch (Exception ex)
        {
            _diagnosticErrors++;
            if (_diagnosticErrors <= 3)
                log?.LogWarning("Read-only taming feed probe failed: " + ex.Message);
        }
    }

    private static void RefreshIndex(float now)
    {
        if (now < _nextRefresh) return;
        _nextRefresh = now + RefreshChestIndexSeconds;
        Chests.Clear();
        foreach (var prefab in ZNetScene.instance.m_prefabs)
        {
            if (prefab == null) continue;
            var container = prefab.GetComponent<Container>();
            if (container == null) continue;
            var result = new List<ZDO>();
            int index = 0;
            for (int i = 0; i < 1024; i++)
                if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab.name, result, ref index))
                    break;
            Chests.AddRange(result);
        }
    }

    internal static void LogAndReset(ManualLogSource log)
    {
        if (_probed == 0 && _diagnosticErrors == 0) return;
        log.LogInfo($"Taming feed probe summary: probes={_probed}, nearbyChests={_inRange}, " +
            $"chestsInUse={_inUse}, clientOwnedChests={_otherOwned}, " +
            $"unreadableInventories={_inventoryUnreadable}, chestsWithFood={_foodPresent}, " +
            $"chestsAboveReserve={_foodAboveReserve}, withoutNearbyChest={_noChest}, " +
            $"withoutAvailableFood={_noFood}, potentialFoodAvailable={_maybeAvailable}, " +
            $"probeErrors={_diagnosticErrors}. Only read-only ZDO snapshots.");
        _probed = _diagnosticErrors = _inRange = _inUse = _otherOwned = 0;
        _inventoryReadable = _inventoryUnreadable = _foodPresent = _foodAboveReserve = 0;
        _noChest = _noFood = _maybeAvailable = 0;
        float now = Time.unscaledTime;
        foreach (var id in NextProbe.Where(x => x.Value < now - StaleProbeSeconds)
                     .Select(x => x.Key).ToArray())
            NextProbe.Remove(id);
    }
}
