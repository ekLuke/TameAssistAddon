using BepInEx;
using BepInEx.Configuration;
using TheLair.TameAssistAddon.Game;
using UnityEngine;
using TheLair.TameAssistAddon.Core;

namespace TheLair.TameAssistAddon;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "thelair.valheim.tameassistaddon";
    public const string PluginName = "TameAssistAddon";
    public const string PluginVersion = "0.2.0";

    private ConfigEntry<bool> _enabled = null!;
    private ConfigEntry<float> _tickSeconds = null!;
    private float _nextTick;

    private void Awake()
    {
        _enabled = Config.Bind("Diagnostics", "Enabled", false,
            "Enable read-only boar ZDO diagnostics on the server. No world writes.");
        _tickSeconds = Config.Bind("Diagnostics", "TickIntervalSeconds", 60f,
            new ConfigDescription("Read-only ZDO scan interval in seconds.",
                new AcceptableValueRange<float>(15f, 3600f)));
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded (diagnostic-only; world changes disabled).");
        var tameAssistPath = System.IO.Path.Combine(Paths.ConfigPath, "ArgusMagnus.ServersideQoL.TameAssist.cfg");
        try
        {
            if (System.IO.File.Exists(tameAssistPath))
            {
                var settings = TameAssistConfiguration.Read(tameAssistPath);
                Logger.LogInfo($"TameAssist settings found: chest-feeding={settings.FeedFromContainers}, " +
                    $"range={settings.FeedFromContainersRange}, reserve={settings.FeedFromContainersLeaveAtLeast}. " +
                    "Settings are read-only; remote feeding is NOT yet implemented.");
            }
            else Logger.LogWarning("Dedicated TameAssist config not found; unified/per-world configs need a separate adapter.");
        }
        catch (System.Exception ex) { Logger.LogWarning($"Could not read TameAssist configuration: {ex.Message}"); }
    }

    private void Update()
    {
        if (!_enabled.Value || Time.unscaledTime < _nextTick)
            return;
        _nextTick = Time.unscaledTime + _tickSeconds.Value;
        if (ZNet.instance == null || !ZNet.instance.IsServer())
            return;
        try { BoarDiagnostics.LogSweep(Logger); }
        catch (System.Exception ex) { Logger.LogError($"Diagnostic scan failed: {ex}"); }
    }
}
