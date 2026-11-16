using BepInEx;
using BepInEx.Configuration;
using TheLair.TameAssistAddon.Core;
using TheLair.TameAssistAddon.Game;
using UnityEngine;

namespace TheLair.TameAssistAddon;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "CursedKarma.Valheim.TameAssistAddon";
    public const string PluginName = "TameAssistAddon";
    public const string PluginVersion = "0.5.7";

    private ConfigEntry<bool> _diagnostics = null!;
    private ConfigEntry<bool> _experimental = null!;
    private ConfigEntry<bool> _tamingFeed = null!;
    private ConfigEntry<float> _tamingRetrySeconds = null!;
    private ConfigEntry<bool> _acknowledgeRisk = null!;
    private ConfigEntry<float> _tickSeconds = null!;
    private ConfigEntry<float> _speed = null!;
    private ConfigEntry<float> _catchup = null!;
    private ConfigEntry<float> _groundFeed = null!;
    private ConfigEntry<float> _chestFeed = null!;
    private ConfigEntry<int> _population = null!;
    private ConfigEntry<bool> _penEnabled = null!;
    private ConfigEntry<float> _penMinX = null!;
    private ConfigEntry<float> _penMaxX = null!;
    private ConfigEntry<float> _penMinZ = null!;
    private ConfigEntry<float> _penMaxZ = null!;
    private float _nextTick;
    private bool _bridgeInstalled;

    private void Awake()
    {
        _diagnostics = Config.Bind("Diagnostics", "Enabled", false,
            "Log only tamed and taming boar ZDO information; wild boars are excluded.");
        _tamingFeed = Config.Bind("TamingChestFeed", "Enabled", false,
            "EXPERIMENTAL: reuse TameAssist upstream chest feeding for partly-tamed boars, including client-owned ones. Test only on a backed-up disposable world.");
        _tamingRetrySeconds = Config.Bind("TamingChestFeed", "RetryIntervalSeconds", 5f,
            new ConfigDescription("Minimum seconds between TameAssist feed attempts per partly-tamed boar (1-60).",
                new AcceptableValueRange<float>(1f, 60f)));
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
        _penEnabled = Config.Bind("ExperimentalPen", "ConfineOfflineRanch", false,
            "Only simulate boars inside the configured rectangular pen and clamp unloaded births to its interior. Does not constrain births while a player owns the active zone.");
        _penMinX = Config.Bind("ExperimentalPen", "MinX", 0f,
            "World-coordinate west edge of offline ranch rectangle, meters.");
        _penMaxX = Config.Bind("ExperimentalPen", "MaxX", 0f,
            "World-coordinate east edge of offline ranch rectangle, meters.");
        _penMinZ = Config.Bind("ExperimentalPen", "MinZ", 0f,
            "World-coordinate south edge of offline ranch rectangle, meters.");
        _penMaxZ = Config.Bind("ExperimentalPen", "MaxZ", 0f,
            "World-coordinate north edge of offline ranch rectangle, meters.");
        _population = Config.Bind("Experimental", "MaxAnimalsNearby", 4,
            new ConfigDescription("Maximum adult and young boars within the vanilla population radius",
                new AcceptableValueRange<int>(2, 32)));

        if (_tamingFeed.Value && _acknowledgeRisk.Value)
            _bridgeInstalled = TameAssistTamingBridge.Install(Logger, _tamingRetrySeconds.Value);
        else if (_tamingFeed.Value)
            Logger.LogWarning("Taming bridge disabled: set Experimental/IHaveBackedUpTheWorld=true on a disposable test world.");

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Unloaded simulation disabled by default; taming bridge active={_bridgeInstalled}.");
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
            TameAssistTamingBridge.LogAndReset(Logger);

            string tameConfigPath = System.IO.Path.Combine(Paths.ConfigPath,
                "ArgusMagnus.ServersideQoL.TameAssist.cfg");
            var tameSettings = System.IO.File.Exists(tameConfigPath)
                ? TameAssistConfiguration.Read(tameConfigPath)
                : TameAssistConfiguration.Parse("");
            // Upstream bridge executes while TameAssist processes each partly-tamed boar.
            // Never run the older, client-owner-filtered feeding implementation.

            if (!_experimental.Value)
                return;
            if (!_acknowledgeRisk.Value)
            {
                Logger.LogWarning("Experimental writes blocked: IHaveBackedUpTheWorld must be true.");
                return;
            }


            float chestRadius = _chestFeed.Value > 0
                ? _chestFeed.Value : tameSettings.FeedFromContainersRange;
            // Fail closed: unbounded offline simulation is not safe to enable
            // on a shared world. Require an explicit pen and valid coordinates.
            if (!_penEnabled.Value || _penMaxX.Value - _penMinX.Value <= 2f ||
                _penMaxZ.Value - _penMinZ.Value <= 2f)
            {
                Logger.LogWarning("Experimental unloaded writes blocked: configure [ExperimentalPen] ConfineOfflineRanch and valid MinX/MaxX/MinZ/MaxZ.");
                return;
            }
            ExperimentalBoarRanch.Tick(Logger, tameSettings, _speed.Value,
                _catchup.Value, _groundFeed.Value, chestRadius, _population.Value,
                _penEnabled.Value,
                new Vector3(_penMinX.Value, 0f, _penMinZ.Value),
                new Vector3(_penMaxX.Value, 0f, _penMaxZ.Value));
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"TameAssistAddon tick failed: {ex}");
        }
    }
}
