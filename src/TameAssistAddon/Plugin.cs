using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace TheLair.TameAssistAddon;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "thelair.valheim.tameassistaddon";
    public const string PluginName = "TameAssistAddon";
    public const string PluginVersion = "0.1.0";

    private ConfigEntry<bool> _enabled = null!;
    private ConfigEntry<float> _tickSeconds = null!;
    private float _nextTick;

    private void Awake()
    {
        _enabled = Config.Bind("General", "Enabled", false,
            "Enable diagnostic ticks. This early version never mutates world data.");
        _tickSeconds = Config.Bind("General", "TickIntervalSeconds", 30f,
            new ConfigDescription("Diagnostic interval in seconds.",
                new AcceptableValueRange<float>(5f, 3600f)));
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded (scaffold only; no simulation).");
    }

    private void Update()
    {
        if (!_enabled.Value || Time.unscaledTime < _nextTick)
            return;

        _nextTick = Time.unscaledTime + _tickSeconds.Value;
        if (ZNet.instance == null || !ZNet.instance.IsServer())
            return;

        Logger.LogDebug("Dedicated server tick; unloaded simulation not implemented.");
    }
}
