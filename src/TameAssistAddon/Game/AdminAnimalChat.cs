using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Receives unmodified vanilla chat through authenticated RPCs. No PC or
/// PlayStation plugin is required. "taa ..." must be typed WITHOUT a slash
/// because vanilla clients process slash commands locally.
/// </summary>
internal static class AdminAnimalChat
{
    private static ManualLogSource? _log;
    private static bool _installed;
    private static string? _serverId;
    private static readonly int SayHash = StableHash("Say");
    private static readonly int ChatHash = StableHash("ChatMessage");
    private static long _lastSender;
    private static string _lastMessage = "";
    private static float _lastTime = -10f;

    private static int StableHash(string name)
    {
        unchecked
        {
            int a = 5381, b = 5381;
            for (int i = 0; i < name.Length; i += 2)
            {
                a = ((a << 5) + a) ^ name[i];
                if (i == name.Length - 1) break;
                b = ((b << 5) + b) ^ name[i + 1];
            }
            return a + b * 1566083941;
        }
    }

    internal static bool Install(ManualLogSource log)
    {
        if (_installed) return true;
        _log = log;
        try
        {
            var harmony = new Harmony("CursedKarma.Valheim.TameAssistAddon.AdminAnimalChat");
            var receive = AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC");
            var playerInfo = AccessTools.Method(typeof(ZNet), "WritePlayerInfo");
            if (receive == null || playerInfo == null)
            {
                log.LogError("AdminAnimalChat: missing RPC_RoutedRPC or WritePlayerInfo. No spawn commands installed.");
                return false;
            }
            harmony.Patch(receive, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(AdminAnimalChat), nameof(BeforeIncoming))));
            harmony.Patch(playerInfo, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(AdminAnimalChat), nameof(AfterPlayerList))));
            _installed = true;
            log.LogInfo("Admin animal chat installed. Syntax: taa spawn Boar 2 (no slash). Admins are verified from authenticated socket and adminlist.txt.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("AdminAnimalChat install failed: " + ex);
            return false;
        }
    }

    private static bool IsCommand(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) &&
        (raw.Trim().Equals("taa", StringComparison.OrdinalIgnoreCase) ||
         raw.Trim().StartsWith("taa ", StringComparison.OrdinalIgnoreCase));

    private static bool BeforeIncoming(ZRpc rpc, ZPackage pkg)
    {
        if (pkg == null || rpc == null || ZNet.instance == null ||
            !ZNet.instance.IsServer()) return true;
        try
        {
            var packet = new ZRoutedRpc.RoutedRPCData();
            packet.Deserialize(new ZPackage(pkg.GetArray()));
            if (packet.m_methodHash != SayHash && packet.m_methodHash != ChatHash)
                return true;

            // Authenticate the transport at the earliest inbound entry.
            // Never trust sender or userInfo solely from RPC payload.
            var peer = ZNet.instance.GetPeers().FirstOrDefault(p =>
                p.m_rpc == rpc && p.IsReady() && p.m_uid == packet.m_senderPeerID);
            if (peer == null) return true;

            var body = new ZPackage(packet.m_parameters.GetArray());
            if (packet.m_methodHash == ChatHash) body.ReadVector3();
            body.ReadInt(); // Talker.Type
            var info = new UserInfo();
            info.Deserialize(ref body);
            var message = body.ReadString();
            if (!IsCommand(message)) return true;

            // Multiple recipient packets for the same chat line must never
            // spawn the same animal repeatedly.
            if (peer.m_uid == _lastSender &&
                string.Equals(message, _lastMessage, StringComparison.Ordinal) &&
                Time.unscaledTime - _lastTime < 2f)
                return false;
            _lastSender = peer.m_uid;
            _lastMessage = message;
            _lastTime = Time.unscaledTime;

            string answer = AdminAnimalCommands.Execute(peer.m_uid, message, _log!);
            _log?.LogInfo($"AdminAnimalChat command sender={peer.m_uid}, reply={answer}");
            Reply(peer.m_uid, peer.GetRefPos(), answer);
            return false; // Do not broadcast privileged commands as chat.
        }
        catch (Exception ex)
        {
            _log?.LogError("AdminAnimalChat incoming packet failure: " + ex);
            return true;
        }
    }

    private static void Reply(long sender, Vector3 at, string message)
    {
        if (ZRoutedRpc.instance == null) return;
        try
        {
            // ShowMessage is a vanilla RPC and does not require a custom
            // chat sender UserId or a client-side plugin.
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ShowMessage",
                (int)MessageHud.MessageType.Center, message);
        }
        catch (Exception ex)
        {
            _log?.LogError("AdminAnimalChat could not send reply: " + ex);
        }
    }

    private static bool TryServerUser(out string id)
    {
        id = "";
        try
        {
            if (_serverId == null)
            {
                var type = Type.GetType(
                    "Steamworks.SteamGameServer, com.rlabrecque.steamworks.net", false);
                var method = type?.GetMethod("GetSteamID", BindingFlags.Public |
                    BindingFlags.Static);
                var value = method?.Invoke(null, null)?.ToString();
                if (string.IsNullOrEmpty(value) || value == "0") return false;
                _serverId = "Steam_" + value;
            }
            id = _serverId;
            return id.Length > 8;
        }
        catch { return false; }
    }

    private static void AfterPlayerList(ZNet __instance, ref ZPackage __result)
    {
        if (!__instance.IsServer() || __result == null ||
            !TryServerUser(out var user)) return;
        try
        {
            // On Valheim 1.x the server is omitted from the recipient list.
            // By advertising one pseudo-player we receive normal Say RPCs
            // even if a single vanilla PS5 user is connected.
            var players = Traverse.Create(__instance).Field("m_players")
                .GetValue<ICollection>();
            int realCount = players?.Count ?? 0;
            var read = new ZPackage(__result.GetArray());
            int count = read.ReadInt();
            if (count > realCount) return; // already added by another mod
            int end = __result.GetPos();
            __result.SetPos(0);
            __result.Write(count + 1);
            __result.SetPos(end);
            __result.Write("TameAssistAddon");
            __result.Write(new ZDOID(ZDOMan.GetSessionID(), uint.MaxValue));
            __result.Write(user);
            __result.Write("TameAssistAddon");
            __result.Write("TameAssistAddon");
            __result.Write("");
            __result.Write(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AdminAnimalChat could not append server chat bridge: " + ex.Message);
        }
    }
}
