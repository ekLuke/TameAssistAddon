using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// Server-authoritative, allowlisted animal spawn commands. Chat arguments
/// are never treated as prefab IDs beyond this explicit list. The caller's
/// privileged status is checked from the authenticated socket, not UserInfo.
/// </summary>
internal static class AdminAnimalCommands
{
    private static readonly Dictionary<string, string> Allowed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["boar"] = "Boar",
            ["cinghiale"] = "Boar",
            ["wolf"] = "Wolf",
            ["lupo"] = "Wolf",
            ["lox"] = "Lox",
            ["hen"] = "Hen",
            ["gallina"] = "Hen",
            ["asksvin"] = "Asksvin"
        };
    private static readonly string[] Display = { "Boar", "Wolf", "Lox", "Hen", "Asksvin" };

    internal static string Execute(long sender, string message, ManualLogSource log)
    {
        var net = ZNet.instance;
        var man = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (net == null || !net.IsServer() || man == null || scene == null)
            return "Server non pronto.";

        var peer = net.GetPeer(sender);
        if (peer == null || !peer.IsReady() || peer.m_socket == null)
            return "Connessione non autenticata.";

        // Account is taken from the verified transport socket. Never trust
        // userInfo.UserId or names supplied inside the chat RPC payload.
        string account = peer.m_socket.GetHostName();
        if (string.IsNullOrWhiteSpace(account) || !net.IsAdmin(account))
        {
            log.LogWarning($"AdminAnimalCommands denied for authenticated peer={sender}.");
            return "Non autorizzato: devi essere presente in adminlist.txt.";
        }

        var parts = message.Trim().Split(new[] { ' ' },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1 || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
            return "TAA: taa animals | taa spawn <Boar|Wolf|Lox|Hen|Asksvin> [1-5]";
        if (parts[1].Equals("animals", StringComparison.OrdinalIgnoreCase))
            return "Animali: " + string.Join(", ", Display);
        if (!parts[1].Equals("spawn", StringComparison.OrdinalIgnoreCase))
            return "Comando sconosciuto. Usa: taa help";

        if (parts.Length < 3 || parts.Length > 4 ||
            !Allowed.TryGetValue(parts[2], out string prefabName))
            return "Uso: taa spawn <Boar|Wolf|Lox|Hen|Asksvin> [1-5]";
        int amount = 1;
        if (parts.Length == 4 && (!int.TryParse(parts[3], out amount) ||
            amount < 1 || amount > 5))
            return "La quantita' deve essere tra 1 e 5.";

        var prefab = scene.GetPrefab(prefabName);
        var view = prefab != null ? prefab.GetComponent<ZNetView>() : null;
        var character = prefab != null ? prefab.GetComponent<Character>() : null;
        if (prefab == null || view == null || character == null)
            return "Prefab animale non disponibile sul server: " + prefabName;

        Vector3 center = peer.GetRefPos();
        if (center.sqrMagnitude < 1f || float.IsNaN(center.x) ||
            float.IsNaN(center.y) || float.IsNaN(center.z))
            return "Posizione del giocatore non pronta.";

        // A small, fixed offset prevents spawning inside the player's
        // body. No arbitrary coordinates or unsafe N-prefab spawn.
        int made = 0;
        for (int i = 0; i < amount; i++)
        {
            double angle = i * Math.PI * 2.0 / Math.Max(amount, 3);
            Vector3 point = center + new Vector3(
                (float)Math.Cos(angle) * 3f, 0.5f,
                (float)Math.Sin(angle) * 3f);
            try
            {
                var zdo = man.CreateNewZDO(point, prefabName.GetStableHashCode());
                zdo.SetPrefab(prefabName.GetStableHashCode());
                zdo.Persistent = true;
                zdo.Type = view.m_type;
                zdo.Distant = view.m_distant;
                zdo.SetOwner(ZDOMan.GetSessionID());
                zdo.SetRotation(Quaternion.Euler(0f, (float)(angle * 180 / Math.PI), 0f));
                zdo.Set(ZDOVars.s_tamed, false);
                made++;
            }
            catch (Exception ex)
            {
                log.LogError($"TAA animal spawn failed: prefab={prefabName}, amount={amount}, spawned={made}: {ex}");
                return $"Spawn parziale: {made}/{amount}. Vedi log errori.";
            }
        }
        log.LogInfo($"AdminAnimalCommands spawned {made}x {prefabName}; account={account}, peer={sender}, at={center}.");
        return $"Spawn completato: {made}x {prefabName} (selvatici).";
    }
}
