using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using TheLair.TameAssistAddon.Core;
using TheLair.TameAssistAddon.Game;
using UnityEngine;

namespace TheLair.TameAssistAddon;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "CursedKarma.Valheim.TameAssistAddon";
    public const string PluginName = "TameAssistAddon";
    public const string PluginVersion = "0.5.25";

    private ConfigEntry<bool> _diagnostics = null!;
    private ConfigEntry<bool> _allSpeciesDiagnostics = null!;
    private ConfigEntry<bool> _autoPenProbe = null!;
    private ConfigEntry<bool> _barrierSigns = null!;
    private ConfigEntry<bool> _barrierSignDebug = null!;
    private ConfigEntry<float> _tickSeconds = null!;
    private ConfigEntry<bool> _slaughterPreview = null!;
    private ConfigEntry<int> _minBreeders = null!;
    private ConfigEntry<float> _usableAreaPercent = null!;
    private ConfigEntry<string> _speciesSquareMeters = null!;
    private ConfigEntry<int> _slaughterPerCycle = null!;

    private ConfigEntry<bool> _tamingFeed = null!;
    private ConfigEntry<float> _tamingRetrySeconds = null!;
    private ConfigEntry<bool> _wildFirstFeed = null!;
    private ConfigEntry<bool> _backedUpTestWorld = null!;

    private ConfigEntry<bool> _birthAtParent = null!;
    private ConfigEntry<bool> _placeNewborn = null!;
    private ConfigEntry<float> _newbornSeconds = null!;
    private ConfigEntry<bool> _birthDebug = null!;

    private float _nextBirthTick;
    private float _nextTick;
    private bool _bridgeInstalled;
    private bool _adminCommandsInstalled;
    private PrivateFileLog? _fileLog;
    private ManualLogSource _diagnosticLog = null!;
    private bool _signProtectionInstalled;

    private void Awake()
    {
        try
        {
            _fileLog = new PrivateFileLog(Logger);
            _diagnosticLog = _fileLog.Source;
        }
        catch (System.Exception ex)
        {
            Logger.LogError("TameAssistAddon cannot initialize its config file logger: " + ex);
            // Avoid flooding console even when file logging is unavailable.
            _diagnosticLog = new ManualLogSource("TameAssistAddon.Unregistered");
            _diagnosticLog.LogEvent += (sender, args) =>
            {
                if ((args.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
                    Logger.LogError(args.Data);
            };
        }

        _diagnostics = Config.Bind("Diagnostics", "Enabled", false,
            "Log diagnostics for tamed and partly-tamed boars.");
        _allSpeciesDiagnostics = Config.Bind("Diagnostics", "LogAllSpecies", false,
            "Read-only count of vanilla tameable and breeding species (including wild animals).");
        _autoPenProbe = Config.Bind("Diagnostics", "AutoPenProbe", false,
            "READ ONLY: scan animal and fence ZDOs for 2D candidate pens. Closed 3D enclosures are not yet verified; does not authorize offline writes.");

        _barrierSigns = Config.Bind("BarrierSigns", "Enabled", false,
            "EXPERIMENTAL TEST: create nonpersistent vanilla diagnostic signs on detected fence candidates. Player-placed signs take precedence; genuinely empty manual signs are populated if the server can safely own them.");
        _barrierSignDebug = Config.Bind("BarrierSigns", "DebugLog", false,
            "Extra logs for each auto sign created, updated, removed, or replaced by a player sign.");

        _tickSeconds = Config.Bind("General", "TickIntervalSeconds", 60f,
            new ConfigDescription("Interval for diagnostic sweeps in seconds (15-3600).",
                new AcceptableValueRange<float>(15f, 3600f)));

        _slaughterPreview = Config.Bind("RanchSlaughter", "PreviewEnabled", true,
            "READ ONLY: calculate surplus adults per detected 2D pen. NEVER kills animals or creates drops.");
        _minBreeders = Config.Bind("RanchSlaughter", "MinBreedersPerSpecies", 2,
            new ConfigDescription("Minimum adult tamed breeding stock preserved per species (2-20).",
                new AcceptableValueRange<int>(2, 20)));
        _usableAreaPercent = Config.Bind("RanchSlaughter", "UsableAreaPercent", 80f,
            new ConfigDescription("Usable interior area, allowing free space for movement (10-100 percent).",
                new AcceptableValueRange<float>(10f, 100f)));
        _speciesSquareMeters = Config.Bind("RanchSlaughter", "SquareMetersPerAdult",
            "Boar:6,Wolf:9,Lox:28,Hen:2,Asksvin:18,Moose:25",
            "Approximate minimum floor area for each adult species, in m2. Read-only diagnostic policy; shared area across all animals.");
        _slaughterPerCycle = Config.Bind("RanchSlaughter", "MaxSlaughtersPerCycle", 2,
            new ConfigDescription("Maximum proposed adult slaughter actions in one diagnostics sweep (0-20).",
                new AcceptableValueRange<int>(0, 20)));

        _tamingFeed = Config.Bind("TamingChestFeed", "Enabled", false,
            "Experimental: use the existing ServersideQoL.TameAssist feeding pipeline for partly-tamed boars in loaded areas.");
        _tamingRetrySeconds = Config.Bind("TamingChestFeed", "RetryIntervalSeconds", 5f,
            new ConfigDescription("Minimum seconds between upstream feed attempts per partly-tamed boar.",
                new AcceptableValueRange<float>(1f, 60f)));
        _wildFirstFeed = Config.Bind("TamingChestFeed", "ExperimentalWildFirstFeed", false,
            "TEST WORLD ONLY: allow first chest meal for wild boars in freshly detected closed 2D pen candidates with no open/unknown gates. Not authoritative 3D enclosure detection; never use on production worlds.");
        _backedUpTestWorld = Config.Bind("TamingChestFeed", "ConfirmBackedUpTestWorld", false,
            "Required to enable the experimental taming bridge. Set true only after backing up a disposable TEST world.");

        _birthAtParent = Config.Bind("BoarBirths", "BirthAtParent", true,
            "For already-tamed boars in loaded zones, override vanilla birth offset; a parent may briefly reload.");
        _placeNewborn = Config.Bind("BoarBirths", "PlaceNewbornBetweenParents", true,
            "Move very recent boar piglets between nearby tame parents; a newborn may briefly reload.");
        _newbornSeconds = Config.Bind("BoarBirths", "NewbornSeconds", 20f,
            new ConfigDescription("Only reposition piglets this many seconds after birth.",
                new AcceptableValueRange<float>(1f, 60f)));
        _birthDebug = Config.Bind("BoarBirths", "DebugLog", false,
            "Log individual boar birth corrections.");

        if (_tamingFeed.Value && _backedUpTestWorld.Value)
            _bridgeInstalled = TameAssistTamingBridge.Install(_diagnosticLog, _tamingRetrySeconds.Value,
                _wildFirstFeed.Value);
        else if (_tamingFeed.Value)
            _diagnosticLog.LogWarning("Taming bridge disabled: [TamingChestFeed] ConfirmBackedUpTestWorld=true is required on a disposable test world.");

        if (_barrierSigns.Value)
            _signProtectionInstalled = BarrierSignProtection.Install(_diagnosticLog);

        _adminCommandsInstalled = AdminAnimalChat.Install(_diagnosticLog);

        _diagnosticLog.LogInfo($"{PluginName} {PluginVersion} loaded. " +
            $"AutoPen probe={_autoPenProbe.Value}; barrier signs={_barrierSigns.Value}; taming bridge active={_bridgeInstalled}; " +
            "legacy rectangle/coordinate-based offline writes removed.");
    }

    private void Update()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
            return;

        // Recovery remains enabled for pending WAL files created by 0.5.9-0.5.11,
        // even though the unsafe coordinate-based offline writer was removed.
        // Never discard historical food journals automatically.
        OfflineFeedJournal.EnsureRecovered(_diagnosticLog);
        if (!OfflineFeedJournal.Ready)
            return;

        if ((_birthAtParent.Value || _placeNewborn.Value) && Time.unscaledTime >= _nextBirthTick)
        {
            _nextBirthTick = Time.unscaledTime + 2f;
            try
            {
                BoarPenBirths.Tick(_diagnosticLog, _birthAtParent.Value,
                    _placeNewborn.Value, _newbornSeconds.Value, _birthDebug.Value);
            }
            catch (System.Exception ex)
            {
                _diagnosticLog.LogWarning("Boar pen birth tick skipped: " + ex);
            }
        }

        if (_barrierSigns.Value && !_signProtectionInstalled)
            _signProtectionInstalled = BarrierSignProtection.Install(_diagnosticLog);

        // Poll already identified blank signs without repeating the heavy
        // whole-world pen scan. The 12-second grace permits client text edits.
        if (_barrierSigns.Value && _signProtectionInstalled)
            BarrierSigns.PollPending(_diagnosticLog, true, _barrierSignDebug.Value);

        if ((!_diagnostics.Value && !_allSpeciesDiagnostics.Value &&
            !_autoPenProbe.Value && !_barrierSigns.Value && !_bridgeInstalled && !_wildFirstFeed.Value) ||
            Time.unscaledTime < _nextTick)
            return;

        _nextTick = Time.unscaledTime + _tickSeconds.Value;
        try
        {
            if (_diagnostics.Value)
                BoarDiagnostics.LogSweep(_diagnosticLog);
            if (_allSpeciesDiagnostics.Value)
                RanchAnimalDiagnostics.LogSweep(_diagnosticLog);
            if (_autoPenProbe.Value || _barrierSigns.Value || _wildFirstFeed.Value)
                AutomaticPenProbe.LogSweep(_diagnosticLog,
                    _barrierSigns.Value && _signProtectionInstalled,
                    _barrierSignDebug.Value,
                    BuildRanchPolicy());
            if (_bridgeInstalled)
                TameAssistTamingBridge.LogAndReset(_diagnosticLog);
            if (_signProtectionInstalled)
                BarrierSignProtection.LogAndReset(_diagnosticLog);
        }
        catch (System.Exception ex)
        {
            _diagnosticLog.LogError($"TameAssistAddon diagnostics tick failed: {ex}");
        }
    }
    private RanchPolicy.Settings BuildRanchPolicy()
    {
        var settings = new RanchPolicy.Settings
        {
            AllowSlaughter = _slaughterPreview.Value,
            MinBreedersPerSpecies = _minBreeders.Value,
            UsableAreaFraction = _usableAreaPercent.Value / 100.0,
            MaxSlaughtersPerCycle = _slaughterPerCycle.Value
        };
        if (!RanchAreaConfiguration.TryParseSpeciesAreas(
            _speciesSquareMeters.Value, settings.SquareMetersPerAdult))
        {
            settings.AllowSlaughter = false;
            _diagnosticLog.LogWarning(
                "Invalid [RanchSlaughter] SquareMetersPerAdult; preview disabled (no animals modified).");
        }
        return settings;
    }

    private void OnDestroy()
    {
        _fileLog?.Dispose();
    }
}
