using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Invokes the original TameAssist FeedFromContainer pipeline for partly-tamed
/// boars. Per-ZDO scheduling prevents expensive repeated inventory scans while
/// retaining the upstream ownership and persistence behavior.
/// </summary>
internal static class TameAssistTamingBridge
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const float DefaultRetrySeconds = 5f;
    private const float CacheExpirySeconds = 1200f;
    private const float MaxSatiatedSleepSeconds = 30f;
    // Same component field key used by ServersideQoL's ComponentFieldAccessor<Tameable>.
    private const string FedDurationKey = "Tameable.m_fedDuration";

    private sealed class RetryState
    {
        internal float NextAttempt;
        internal float LastSeen;
        internal ZDO? PendingChest;
        internal long RequestedOwner;
        internal bool WaitingForOwnership;
        internal int ConsecutiveOwnershipRetries;
    }

    private static readonly Dictionary<ZDOID, RetryState> RetryStates = new Dictionary<ZDOID, RetryState>();
    private static ManualLogSource? _log;
    private static MethodInfo? _feed;
    private static FieldInfo? _containerIndex;
    private static MethodInfo? _scheduleZdo;
    private static ZDO? _capturedChest;
    private static long _capturedOwner;
    private static bool _inFeedInvocation;
    private static long _modOwnerId;
    private static bool _enabled;
    private static float _retrySeconds = DefaultRetrySeconds;

    private static int _eligible, _attempts, _fed, _noIndex, _errors;
    private static int _rateLimited, _unchanged, _noProgress, _notBoar, _satiated, _ownershipRetries;
    private static int _ownershipCaptured, _ownershipReleased, _ownershipChangedClient, _ownershipStillHeld, _untrackedOwnershipRetries;

    internal static bool Install(ManualLogSource log, float retrySeconds = DefaultRetrySeconds)
    {
        _log = log;
        _retrySeconds = Mathf.Clamp(retrySeconds, 1f, 60f);
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(x => x.GetType("ServersideQoL.TameAssist.TameableProcessor", false))
                .FirstOrDefault(x => x != null);
            if (type == null)
            {
                log.LogError("Taming bridge: ServersideQoL.TameAssist.TameableProcessor not found.");
                return false;
            }

            // Avoid the inherited/bridge Process overloads.
            var processCandidates = type.GetMethods(Flags | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "Process" && m.GetParameters().Length == 3)
                .Where(m =>
                {
                    var p = m.GetParameters();
                    return p[0].ParameterType.Name == "ServersideQoLZDO"
                        && p[2].ParameterType.Name == "PrefabInfo";
                }).ToArray();
            var process = processCandidates.Length == 1 ? processCandidates[0] : null;
            var feedCandidates = type.GetMethods(Flags | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "FeedFromContainer" && m.GetParameters().Length == 3)
                .ToArray();
            var feed = feedCandidates.Length == 1 ? feedCandidates[0] : null;
            var containerIndex = type.GetField("_containersByItemName", Flags);
            // Upstream FeedFromContainer can return ScheduleReprocessing (flag 32)
            // after requesting release of a client-owned container. Since our
            // invocation occurs in a Harmony postfix, its return flags cannot
            // be merged into the already-computed ProcessResult. Explicitly
            // request a safe reprocessing pass through the upstream processor.
            var scheduleZdo = type.BaseType?.BaseType?.GetMethods(Flags)
                .SingleOrDefault(m => m.Name == "ScheduleReprocessing" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType.Name == "ServersideQoLZDO" &&
                    m.GetParameters()[1].ParameterType == typeof(float));
            var registryType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(x => x.GetType("ServersideQoL.Processors.ContainerRegistryProcessor", false))
                .FirstOrDefault(x => x != null);
            var ownershipCandidates = registryType?.GetMethods(Flags)
                .Where(m => m.Name == "RequestOwnership" && m.ReturnType == typeof(float))
                .Where(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2 &&
                        p[0].ParameterType.Name == "ServersideQoLZDO" &&
                        p[1].ParameterType.Name == "PlayerID";
                }).ToArray();
            var ownershipMethod = ownershipCandidates?.Length == 1 ? ownershipCandidates[0] : null;
            // ServersideQoL recognizes a synthetic mod owner ID as server
            // writable, even when vanilla ZDO.IsOwner() returns false.
            // Resolve that ID from the actual upstream assembly, not a guess.
            var playerIdType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(x => x.GetType("ServersideQoL.Utilities.PlayerID", false))
                .FirstOrDefault(x => x != null);
            var modOwnerGetter = playerIdType?.GetMethod("GetModPlayerID",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var playerIdValue = playerIdType?.GetProperty("Value",
                BindingFlags.Public | BindingFlags.Instance);
            long modOwnerId = modOwnerGetter != null && playerIdValue != null
                ? Convert.ToInt64(playerIdValue.GetValue(modOwnerGetter.Invoke(null, null)))
                : 0L;
            var ownershipPrefix = typeof(TameAssistTamingBridge).GetMethod(nameof(BeforeOwnershipRequest),
                BindingFlags.NonPublic | BindingFlags.Static);
            var postfix = typeof(TameAssistTamingBridge).GetMethod(nameof(AfterProcess),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (process == null || feed == null || containerIndex == null || postfix == null || scheduleZdo == null || ownershipMethod == null || ownershipPrefix == null || modOwnerId == 0L)
            {
                log.LogError($"Taming bridge: unexpected upstream signatures (Process matches={processCandidates.Length}, FeedFromContainer matches={feedCandidates.Length}); refusing to patch.");
                return false;
            }

            var harmony = new Harmony("CursedKarma.Valheim.TameAssistAddon.TamingBridge");
            harmony.Patch(process, postfix: new HarmonyMethod(postfix));
            harmony.Patch(ownershipMethod, prefix: new HarmonyMethod(ownershipPrefix));
            _modOwnerId = modOwnerId;
            _feed = feed;
            _scheduleZdo = scheduleZdo;
            _containerIndex = containerIndex;
            _enabled = true;
            log.LogInfo($"Taming bridge active: upstream TameAssist feeding; per-boar retry interval={_retrySeconds:0.##}s; upstream mod owner recognized. No forced client ownership.");
            return true;
        }
        catch (Exception ex)
        {
            _enabled = false;
            log.LogError("Taming bridge installation failed: " + ex);
            return false;
        }
    }

    // Observes exactly the chest TameAssist requests ownership for, rather
    // than guessing from nearby chests. No RPC is added or modified.
    private static void BeforeOwnershipRequest(object[] __args)
    {
        if (!_inFeedInvocation || __args == null || __args.Length < 1)
            return;
        var wrapper = __args[0];
        var chest = wrapper?.GetType().GetProperty("ZDO")?.GetValue(wrapper) as ZDO;
        if (chest == null || !chest.IsValid())
            return;
        _capturedChest = chest;
        _capturedOwner = chest.GetOwner();
        _ownershipCaptured++;
    }

    private static float OwnershipRetryDelay(int consecutiveRetries)
    {
        // 5, 10, 20, 40, 60 seconds with the default interval.
        double delay = _retrySeconds * Math.Pow(2d, Math.Min(5, Math.Max(0, consecutiveRetries - 1)));
        return (float)Math.Min(60d, delay);
    }

    private static void AfterProcess(object __instance, object[] __args)
    {
        var feed = _feed;
        var containerIndex = _containerIndex;
        if (!_enabled || feed == null || containerIndex == null ||
            __args == null || __args.Length < 3 ||
            ZNet.instance == null || !ZNet.instance.IsServer())
            return;

        try
        {
            // Read the ZDO first: use a cheap per-entity retry check before
            // reflecting prefab metadata or invoking upstream inventory scans.
            var wrapper = __args[0];
            var zdo = wrapper?.GetType().GetProperty("ZDO")?.GetValue(wrapper) as ZDO;
            if (zdo == null || !zdo.IsValid() || zdo.GetBool(ZDOVars.s_tamed, false))
                return;

            float now = Time.unscaledTime;
            RetryStates.TryGetValue(zdo.m_uid, out var existing);
            if (existing != null && existing.WaitingForOwnership)
            {
                var pending = existing.PendingChest;
                if (pending == null || !pending.IsValid())
                {
                    existing.WaitingForOwnership = false;
                    existing.PendingChest = null;
                    existing.ConsecutiveOwnershipRetries = 0;
                    existing.NextAttempt = now;
                }
                else if (!pending.HasOwner() || pending.IsOwner() || pending.GetOwner() == _modOwnerId)
                {
                    // A genuine ownership transition has occurred. Retry the
                    // upstream pipeline immediately, without claiming the ZDO.
                    existing.WaitingForOwnership = false;
                    existing.PendingChest = null;
                    existing.ConsecutiveOwnershipRetries = 0;
                    existing.NextAttempt = now;
                    _ownershipReleased++;
                    _log?.LogInfo($"Taming ownership available: boar={zdo.m_uid}, chest={pending.m_uid}, newOwner={pending.GetOwner()}; retrying feed.");
                }
                else if (pending.GetOwner() != existing.RequestedOwner)
                {
                    // A client-to-client transfer is not a successful release.
                    _log?.LogInfo($"Taming chest ownership changed between clients: boar={zdo.m_uid}, chest={pending.m_uid}, oldOwner={existing.RequestedOwner}, newOwner={pending.GetOwner()}.");
                    existing.RequestedOwner = pending.GetOwner();
                    _ownershipChangedClient++;
                }
            }
            if (existing != null && now < existing.NextAttempt)
            {
                existing.LastSeen = now;
                _rateLimited++;
                return;
            }

            var prefabInfo = __args[2];
            var tameable = prefabInfo?.GetType().GetProperty("Tameable")?.GetValue(prefabInfo) as Tameable;
            if (tameable == null || tameable.gameObject.name != "Boar")
            {
                _notBoar++;
                return;
            }

            float fullTime = zdo.GetFloat("tamingTime", tameable.m_tamingTime);
            float timeLeft = zdo.GetFloat(ZDOVars.s_tameTimeLeft, fullTime);
            if (fullTime <= 0 || timeLeft < 0 || timeLeft >= fullTime)
            {
                _noProgress++;
                return;
            }

            _eligible++;

            // Match TameAssist's hunger gate using its effective component-field
            // override (including FedDurationMultiplier when applied).
            // Do not scan inventories while the animal is still fed.
            long lastFed = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            long gameTicks = ZNet.instance.GetTime().Ticks;
            float duration = zdo.GetFloat(FedDurationKey, tameable.m_fedDuration);
            if (lastFed > 0 && duration > 0f && !float.IsNaN(duration))
            {
                double fedAge = (gameTicks - lastFed) / (double)TimeSpan.TicksPerSecond;
                if (fedAge <= duration)
                {
                    _satiated++;
                    double remaining = duration - fedAge;
                    float sleep = (float)Math.Max(1d, Math.Min(MaxSatiatedSleepSeconds, remaining));
                    var fedState = existing ?? new RetryState();
                    fedState.LastSeen = now;
                    fedState.NextAttempt = now + sleep;
                    RetryStates[zdo.m_uid] = fedState;
                    return;
                }
            }

            if (containerIndex.GetValue(__instance) == null)
            {
                _noIndex++;
                return;
            }

            // Schedule BEFORE invoking upstream to prevent nested callbacks
            // from running a second feeding pass in the same frame.
            var state = existing ?? new RetryState();
            state.LastSeen = now;
            state.NextAttempt = now + _retrySeconds;
            RetryStates[zdo.m_uid] = state;

            long before = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            _attempts++;
            // Only observe ownership calls issued inside this specific feed
            // invocation; other ServersideQoL processors remain untouched.
            _capturedChest = null;
            _capturedOwner = 0L;
            object? processResult;
            _inFeedInvocation = true;
            try
            {
                processResult = feed.Invoke(__instance, __args);
            }
            finally
            {
                _inFeedInvocation = false;
            }

            if (processResult != null && (Convert.ToInt32(processResult) & 32) != 0)
            {
                _ownershipRetries++;
                float delay = _retrySeconds;
                var requestedChest = _capturedChest;
                if (requestedChest != null && requestedChest.IsValid() &&
                    requestedChest.HasOwner() && !requestedChest.IsOwner() &&
                    requestedChest.GetOwner() != _modOwnerId)
                {
                    if (!state.WaitingForOwnership || state.PendingChest != requestedChest)
                        state.ConsecutiveOwnershipRetries = 0;
                    state.PendingChest = requestedChest;
                    state.RequestedOwner = _capturedOwner;
                    bool newRequest = !state.WaitingForOwnership || state.ConsecutiveOwnershipRetries == 0;
                    state.WaitingForOwnership = true;
                    state.ConsecutiveOwnershipRetries++;
                    delay = OwnershipRetryDelay(state.ConsecutiveOwnershipRetries);
                    state.NextAttempt = now + delay;
                    if (state.ConsecutiveOwnershipRetries > 1)
                        _ownershipStillHeld++;
                    if (newRequest || state.ConsecutiveOwnershipRetries == 3)
                        _log?.LogInfo($"Taming chest ownership pending: boar={zdo.m_uid}, chest={requestedChest.m_uid}, ownerAtRequest={_capturedOwner}, currentOwner={requestedChest.GetOwner()}, retry={state.ConsecutiveOwnershipRetries}, delay={delay:0.#}s.");
                }
                else if (requestedChest == null)
                {
                    // Keep legacy behavior if the upstream API changed.
                    _untrackedOwnershipRetries++;
                }
                else
                {
                    state.WaitingForOwnership = false;
                    state.PendingChest = null;
                    state.ConsecutiveOwnershipRetries = 0;
                }
                _scheduleZdo?.Invoke(__instance, new object[] { wrapper!, delay });
            }
            long after = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            if (after > before)
            {
                _fed++;
                state.WaitingForOwnership = false;
                state.PendingChest = null;
                state.ConsecutiveOwnershipRetries = 0;
            }
            else
            {
                _unchanged++; // upstream does not expose a specific skip reason
                var monsterAi = prefabInfo?.GetType().GetProperty("MonsterAI")?.GetValue(prefabInfo) as MonsterAI;
                ReadOnlyFeedDiagnostics.Probe(zdo, monsterAi, _log);
            }
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors <= 3)
                _log?.LogWarning("Taming bridge tick failed (up to 3 detailed errors): " +
                    (ex.InnerException ?? ex));
        }
    }

    internal static void LogAndReset(ManualLogSource log)
    {
        if (!_enabled) return;

        // Prevent long-lived worlds from retaining ZDO IDs for destroyed boars.
        float now = Time.unscaledTime;
        if (RetryStates.Count > 0)
        {
            var stale = RetryStates.Where(kv => now - kv.Value.LastSeen > CacheExpirySeconds ||
                                               now < kv.Value.LastSeen)
                                   .Select(kv => kv.Key).ToArray();
            foreach (var id in stale) RetryStates.Remove(id);
        }

        ReadOnlyFeedDiagnostics.LogAndReset(log);
        log.LogInfo($"Taming bridge: eligibleCalls={_eligible}, rateLimitedCalls={_rateLimited}, " +
            $"feedingAttempts={_attempts}, fed={_fed}, noFeedChange={_unchanged}, ownershipRetries={_ownershipRetries}, " +
            $"ownerRequestsSeen={_ownershipCaptured}, ownerReleased={_ownershipReleased}, ownerChangedClient={_ownershipChangedClient}, " +
            $"ownerStillHeld={_ownershipStillHeld}, ownerRetriesUntracked={_untrackedOwnershipRetries}, " +
            $"satiatedSkipped={_satiated}, containerRegistryUnavailable={_noIndex}, wildOrNotStarted={_noProgress}, " +
            $"otherPrefabs={_notBoar}, errors={_errors}, cachedBoars={RetryStates.Count}. " +
            "Counts are per interval, not unique animals; upstream does not reveal why noFeedChange occurred.");
        _eligible = _attempts = _fed = _noIndex = _errors = 0;
        _rateLimited = _unchanged = _noProgress = _notBoar = _satiated = _ownershipRetries = 0;
        _ownershipCaptured = _ownershipReleased = _ownershipChangedClient = _ownershipStillHeld = _untrackedOwnershipRetries = 0;
    }
}
