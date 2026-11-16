using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Runs TameAssist's existing, server-side FeedFromContainer pipeline in the
/// Taming branch. The upstream code handles container ownership release,
/// inventory persistence, in-use checks and item reservation. No client mod.
/// </summary>
internal static class TameAssistTamingBridge
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static ManualLogSource _log;
    private static MethodInfo _feed;
    private static FieldInfo _containerIndex;
    private static bool _enabled;
    private static int _eligible, _attempts, _fed, _noIndex, _errors;

    internal static bool Install(ManualLogSource log)
    {
        _log = log;
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

            var process = type.GetMethod("Process", Flags);
            _feed = type.GetMethod("FeedFromContainer", Flags);
            _containerIndex = type.GetField("_containersByItemName", Flags);
            var postfix = typeof(TameAssistTamingBridge).GetMethod(nameof(AfterProcess),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (process == null || _feed == null || _containerIndex == null || postfix == null)
            {
                log.LogError("Taming bridge: upstream signatures changed; refusing to patch.");
                return false;
            }

            // No takeover of client-owned boars or containers. TameAssist
            // owns the operation and requests container release normally.
            var harmony = new Harmony("CursedKarma.Valheim.TameAssistAddon.TamingBridge");
            harmony.Patch(process, postfix: new HarmonyMethod(postfix));
            _enabled = true;
            log.LogInfo("Taming bridge active: using TameAssist's own container feeding and ownership workflow.");
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
        if (!_enabled || __args == null || __args.Length < 3 || ZNet.instance == null ||
            !ZNet.instance.IsServer()) return;
        try
        {
            // PrefabInfo.Tameable is the vanilla prefab component (not a live
            // client-owned object), so this works even in unloaded sectors.
            var prefabInfo = __args[2];
            var tameable = prefabInfo?.GetType().GetProperty("Tameable")?.GetValue(prefabInfo) as Tameable;
            if (tameable == null || tameable.gameObject.name != "Boar") return;

            var wrapper = __args[0];
            var zdo = wrapper?.GetType().GetProperty("ZDO")?.GetValue(wrapper) as ZDO;
            if (zdo == null || !zdo.IsValid() || zdo.GetBool(ZDOVars.s_tamed, false))
                return;

            float fullTime = zdo.GetFloat("tamingTime", tameable.m_tamingTime);
            float timeLeft = zdo.GetFloat(ZDOVars.s_tameTimeLeft, fullTime);
            if (fullTime <= 0 || timeLeft < 0 || timeLeft >= fullTime)
                return; // completely wild, no taming progress yet

            _eligible++;
            if (_containerIndex.GetValue(__instance) == null)
            {
                _noIndex++;
                return; // TameAssist's FeedFromContainers setting is off
            }

            long before = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            _attempts++;
            _feed.Invoke(__instance, __args);
            long after = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            if (after > before) _fed++;
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
        log.LogInfo($"Taming bridge: eligibleProcessorCalls={_eligible}, feedingAttempts={_attempts}, " +
            $"fed={_fed}, containerRegistryUnavailable={_noIndex}, errors={_errors}. " +
            "TameAssist handles chest ownership; calls are not distinct animals.");
        _eligible = _attempts = _fed = _noIndex = _errors = 0;
    }
}
