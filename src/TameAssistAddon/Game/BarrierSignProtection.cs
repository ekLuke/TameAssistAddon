using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Dedicated-server enforcement for addon-tagged vanilla diagnostic signs.
/// This protects owner-side vanilla RPCs and prevents server-side material
/// refunds. Vanilla clients are additionally given per-ZDO nonremovable and
/// high-health component overrides by BarrierSigns.Create.
/// </summary>
internal static class BarrierSignProtection
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static ManualLogSource? _log;
    private static bool _installed;
    private static int _refusedRemoval, _refusedDamage, _suppressedRefunds;

    internal static bool Install(ManualLogSource log)
    {
        if (_installed) return true;
        _log = log;
        try
        {
            // Explicitly verify game API signatures before enabling creation
            // of player-visible auto-signs. No client patch is installed.
            var targets = new[]
            {
                (Find(typeof(WearNTear), "RPC_Remove"), nameof(BlockRemoval)),
                (Find(typeof(WearNTear), "Remove"), nameof(BlockRemoval)),
                (Find(typeof(WearNTear), "RPC_Damage"), nameof(BlockDamage)),
                (Find(typeof(WearNTear), "ApplyDamage"), nameof(BlockDamage)),
                (Find(typeof(WearNTear), "Destroy"), nameof(BlockDamage)),
                (Find(typeof(Piece), "DropResources"), nameof(BlockResourceDrop))
            };
            if (targets.Any(p => p.Item1 == null))
            {
                log.LogError("BarrierSigns protection unavailable: a required vanilla damage/removal/refund method is missing. Auto-sign creation DISABLED.");
                return false;
            }
            var harmony = new Harmony("CursedKarma.Valheim.TameAssistAddon.BarrierSignsProtection");
            foreach (var pair in targets)
            {
                var patch = typeof(BarrierSignProtection).GetMethod(pair.Item2,
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (patch == null) throw new InvalidOperationException("Missing protection patch: " + pair.Item2);
                harmony.Patch(pair.Item1, prefix: new HarmonyMethod(patch));
            }
            _installed = true;
            log.LogInfo("BarrierSigns server protections active: removal, damage, destruction and resource refunds blocked for addon-tagged signs.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("BarrierSigns protections failed; refusing auto-sign creation: " + ex);
            return false;
        }
    }

    private static MethodInfo? Find(Type type, string name)
    {
        var methods = type.GetMethods(Flags).Where(m => m.Name == name).ToArray();
        return methods.Length == 1 ? methods[0] : null;
    }

    private static bool IsOurs(Component component)
    {
        var view = component.GetComponent<ZNetView>();
        var zdo = view != null ? view.GetZDO() : null;
        return zdo != null && zdo.IsValid() &&
            zdo.GetBool(BarrierSigns.MarkerKey, false);
    }

    private static bool BlockRemoval(WearNTear __instance)
    {
        if (!IsOurs(__instance)) return true;
        _refusedRemoval++;
        // A player cannot remove the automatic sign through a hammer RPC.
        // BarrierSigns.RemoveOwned intentionally deletes its ZDO directly
        // when the parent barrier vanishes; this patch does not block that.
        return false;
    }

    private static bool BlockDamage(WearNTear __instance)
    {
        if (!IsOurs(__instance)) return true;
        _refusedDamage++;
        return false;
    }

    private static bool BlockResourceDrop(Piece __instance)
    {
        if (!IsOurs(__instance)) return true;
        _suppressedRefunds++;
        // Even a future vanilla path that reaches Piece.DropResources must
        // not generate Wood/Coal from a zero-cost addon-created ZDO.
        return false;
    }

    internal static void LogAndReset(ManualLogSource log)
    {
        if (!_installed || (_refusedRemoval == 0 && _refusedDamage == 0 &&
            _suppressedRefunds == 0)) return;
        log.LogInfo($"BarrierSigns protection: removalsBlocked={_refusedRemoval}, " +
            $"damageBlocked={_refusedDamage}, refundsBlocked={_suppressedRefunds}.");
        _refusedRemoval = _refusedDamage = _suppressedRefunds = 0;
    }
}
