using BepInEx;
using BepInEx.Configuration;
using TheLair.TameAssistAddon.Core;
using TheLair.TameAssistAddon.Game;
using UnityEngine;

namespace TheLair.TameAssistAddon;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "thelair.valheim.tameassistaddon";
    public const string PluginName = "TameAssistAddon";
    public const string PluginVersion = "0.4.0";

    private ConfigEntry<bool> _diagnostics = null!;
    private ConfigEntry<bool> _experimental = null!;
    private ConfigEntry<bool> _tamingFeed = null!;
    private ConfigEntry<bool> _acknowledgeRisk = null!;
    private ConfigEntry<float> _tickSeconds = null!;
    private ConfigEntry<float> _speed = null!;
    private ConfigEntry<float> _catchup = null!;
    private ConfigEntry<float> _groundFeed = null!;
    private ConfigEntry<float> _chestFeed = null!;
    private ConfigEntry<int> _population = null!;
    private float _nextTick;

    private void Awake()
    {
        _diagnostics = Config.Bind("Diagnostics", "Enabled", false,
            "Log read-only boar ZDO information every diagnostic interval.");
        _tamingFeed = Config.Bind("TamingChestFeed", "Enabled", false,
            "EXPERIMENTAL: feed wild boars from live server-owned chests while taming. Test on a disposable world.");
        _experimental = Config.Bind("Experimental", "EnableWorldWrites", false,
            "DANGEROUS EXPERIMENTAL: enable server-side unloaded boar ZDO and inventory writes. " +
            "Never use on a valuable world without a tested backup.");
        _acknowledgeRisk = Config.Bind("Experimental", "IHaveBackedUpTheWorld", false,
            "Second required safety switch for experimental world writes.");
        _tickSeconds = Config.Bind("General", "TickIntervalSeconds", 60f,
            new ConfigDescription("Simulation/diagnostic interval seconds",
                new AcceptableValueRange<float>(15f, 3600f)));
        _speed = Config.Bind("Experimental", "UnloadedSpeedPercent", 50f,
            new ConfigDescription("Progress speed as percentage of elapsed real time",
                new AcceptableValueRange<float>(1f, 200f)));
        _catchup = Config.Bind("Experimental", "CatchupLimitHours", 12f,
            new ConfigDescription("Maximum time advanced in one sweep",
                new AcceptableValueRange<float>(0f, 24f)));
        _groundFeed = Config.Bind("Experimental", "GroundFeedRadius", 8f,
            new ConfigDescription("Radius to consume dropped accepted foods",
                new AcceptableValueRange<float>(0f, 64f)));
        _chestFeed = Config.Bind("Experimental", "ChestFeedRadiusOverride", 0f,
            new ConfigDescription("0 uses TameAssist's configured range; no automatic sign overrides",
                new AcceptableValueRange<float>(0f, 64f)));
        _population = Config.Bind("Experimental", "MaxAnimalsNearby", 4,
            new ConfigDescription("Maximum adult and young boars within the vanilla population radius",
                new AcceptableValueRange<int>(2, 32)));

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Experimental writes: OFF by default.");
    }

    private void Update()
    {
        if ((!_diagnostics.Value && !_experimental.Value && !_tamingFeed.Value) || Time.unscaledTime < _nextTick)
            return;

        _nextTick = Time.unscaledTime + _tickSeconds.Value;
        if (ZNet.instance == null || !ZNet.instance.IsServer())
            return;
        try
        {
            if (_diagnostics.Value)
                BoarDiagnostics.LogSweep(Logger);

            string tameConfigPath = System.IO.Path.Combine(Paths.ConfigPath,
                "ArgusMagnus.ServersideQoL.TameAssist.cfg");
            var tameSettings = System.IO.File.Exists(tameConfigPath)
                ? TameAssistConfiguration.Read(tameConfigPath)
                : TameAssistConfiguration.Parse("");
            if (_tamingFeed.Value)
            {
                if (!_acknowledgeRisk.Value)
                    Logger.LogWarning("TamingChestFeed blocked: set Experimental/IHaveBackedUpTheWorld = true on a disposable world.");
                else
                    TamingChestFeed.Tick(Logger, tameSettings, _chestFeed.Value);
            }

            if (!_experimental.Value)
                return;
            if (!_acknowledgeRisk.Value)
            {
                Logger.LogWarning("Experimental writes blocked: IHaveBackedUpTheWorld must be true.");
                return;
            }


            float chestRadius = _chestFeed.Value > 0
                ? _chestFeed.Value : tameSettings.FeedFromContainersRange;
            ExperimentalBoarRanch.Tick(Logger, tameSettings, _speed.Value,
                _catchup.Value, _groundFeed.Value, chestRadius, _population.Value);
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"TameAssistAddon tick failed: {ex}");
        }
    }
}
