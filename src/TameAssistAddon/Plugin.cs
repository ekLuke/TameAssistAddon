using BepInEx;
using BepInEx.Configuration;
using TheLair.TameAssistAddon.Game;
using UnityEngine;

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
