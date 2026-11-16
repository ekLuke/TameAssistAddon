# TameAssistAddon

An independent **dedicated-server-only** BepInEx addon for Valheim, intended to work alongside [ServersideQoL TameAssist](https://github.com/ArgusMagnus/ValheimServersideQoL) without modifying the TameAssist DLL. The optional bridge uses reflection and a Harmony patch of TameAssist 2.1.0 internals.

> **Status: experimental v0.5.2, NOT production-tested.** An opt-in Boar-only implementation for unloaded pens and separate taming-time chest feeding have been added, but it has not been validated in a disposable Valheim world or with a PS5 client. World writes are **OFF by default**. Do not enable on a valuable world.

## Capabilities

- Standalone, deterministic simulation planner and automated regression tests.
- Server-side ZDO boar diagnostics (read only).
- Experimental unloaded **boar** taming, ground feeding, chest feeding, love points, pregnancy, birth and vanilla baby timestamp/level writes.
- Optional feeding for **partly-tamed** boars by invoking TameAssist's original `FeedFromContainer` via a Harmony postfix, including its container ownership-request workflow (a TameAssist 2.1.0 limitation). See [taming chest feed guide](docs/TAMING_CHEST_FEED.md).
- Reads documented TameAssist config keys from the standard BepInEx config path, without compile-time referencing the TameAssist assembly; the 0.5.0 feeding bridge uses runtime reflection.
- Conservative client-exclusion radius (256 m) and no live-object writes.
- No custom network RPCs, prefabs or client DLLs. **PS5 vanilla support is a design goal, not yet verified.**
- Vanilla `Growup` is intended to handle baby growth after a zone is loaded, using the saved birth timestamp. Online growth and handed-off state still require integration tests.

### Known limitations

- The 0.5.2 bridge depends on private TameAssist 2.1.0 method signatures and its processor cadence; it has not been verified in-game. The previous live-object, server-owned-only feed path is no longer executed.
- The optional simulation currently supports **Boar** only; not wolves, lox, egg-layers or mixed-parent species.
- The experimental unloaded ranch does not support ContainerSigns per-chest feeding ranges, UnifiedConfig, or world-specific TameAssist configs. The bridge delegates range/sign handling to original TameAssist.
- Pregnancy approximates vanilla's random love-point mechanic and is not yet parity-tested.
- Child position currently uses a conservative offset, but has not been terrain-height validated.
- World writes are not transactional across multiple ZDOs: a crash between the parent and food/baby update can cause lost food/birth or an inconsistent state; this must be fixed before production release.
- The 256 m player exclusion is conservative; it does not prove zone inactivity in every networking edge case.
- **The earlier 0.4.1 build was tested on a cloned Pelican server and showed two client-owned taming boars being skipped. The 0.5.1 bridge was verified feeding two taming boars in a cloned Pelican test server; the 0.5.2 retry optimization is not yet game-tested.**

## Source and tests

- `src/TameAssistAddon.Core/`: simulation model and independent TameAssist config parser.
- `tests/TameAssistAddon.Core.Tests/`: portable regression tests.
- `src/TameAssistAddon/`: BepInEx runtime adapter, boar ZDO diagnostics and experimental boar simulation.
- `.github/workflows/core-ci.yml`: automated simulation tests.
- `.github/workflows/plugin-build.yml`: compiles against SteamCMD Valheim dedicated-server and Thunderstore BepInEx; publishes a downloadable DLL artifact.

### Run tests

```bash
dotnet run --project tests/TameAssistAddon.Core.Tests/TameAssistAddon.Core.Tests.csproj -c Release
```

### Compile locally

```bash
dotnet build src/TameAssistAddon/TameAssistAddon.csproj -c Release -p:ValheimDir=/path/to/valheim/server
```

Deploy BOTH `TameAssistAddon.dll` and `TameAssistAddon.Core.dll` from the build output into `BepInEx/plugins/`.

## BepInEx config

`BepInEx/config/CursedKarma.Valheim.TameAssistAddon.cfg`, generated at first startup:

```ini
[Diagnostics]
Enabled = true

[TamingChestFeed]
Enabled = false
RetryIntervalSeconds = 5

[Experimental]
EnableWorldWrites = false
IHaveBackedUpTheWorld = false
UnloadedSpeedPercent = 50
CatchupLimitHours = 12
GroundFeedRadius = 8
ChestFeedRadiusOverride = 0
MaxAnimalsNearby = 4

[General]
TickIntervalSeconds = 60
```

**Keep both experimental switches false on the main server.** Only enable them on a disposable world after verifying a full recovery backup.

See [Pelican guide](docs/PELican.md) and [compatibility notes](docs/COMPATIBILITY.md). Outstanding blockers are tracked in [Issues](https://github.com/ekLuke/TameAssistAddon/issues).

## License

GNU GPL-3.0-only. The GPL permits commercial use and requires compliant source-code distribution when derivatives are conveyed.
