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
    }

    private static readonly Dictionary<ZDOID, RetryState> RetryStates = new Dictionary<ZDOID, RetryState>();
    private static ManualLogSource? _log;
    private static MethodInfo? _feed;
    private static FieldInfo? _containerIndex;
    private static bool _enabled;
    private static float _retrySeconds = DefaultRetrySeconds;

    private static int _eligible, _attempts, _fed, _noIndex, _errors;
    private static int _rateLimited, _unchanged, _noProgress, _notBoar, _satiated;

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
            var postfix = typeof(TameAssistTamingBridge).GetMethod(nameof(AfterProcess),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (process == null || feed == null || containerIndex == null || postfix == null)
            {
                log.LogError($"Taming bridge: unexpected upstream signatures (Process matches={processCandidates.Length}, FeedFromContainer matches={feedCandidates.Length}); refusing to patch.");
                return false;
            }

            var harmony = new Harmony("CursedKarma.Valheim.TameAssistAddon.TamingBridge");
            harmony.Patch(process, postfix: new HarmonyMethod(postfix));
            _feed = feed;
            _containerIndex = containerIndex;
            _enabled = true;
            log.LogInfo($"Taming bridge active: upstream TameAssist feeding; per-boar retry interval={_retrySeconds:0.##}s. No forced client ownership.");
            return true;
        }
        catch (Exception ex)
        {
            _enabled = false;
            log.LogError("Taming bridge installation failed: " + ex);
            return false;
        }
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
            if (RetryStates.TryGetValue(zdo.m_uid, out var existing) && now < existing.NextAttempt)
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
            feed.Invoke(__instance, __args);
            long after = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            if (after > before)
                _fed++;
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
            $"feedingAttempts={_attempts}, fed={_fed}, noFeedChange={_unchanged}, " +
            $"satiatedSkipped={_satiated}, containerRegistryUnavailable={_noIndex}, wildOrNotStarted={_noProgress}, " +
            $"otherPrefabs={_notBoar}, errors={_errors}, cachedBoars={RetryStates.Count}. " +
            "Counts are per interval, not unique animals; upstream does not reveal why noFeedChange occurred.");
        _eligible = _attempts = _fed = _noIndex = _errors = 0;
        _rateLimited = _unchanged = _noProgress = _notBoar = _satiated = 0;
    }
}
