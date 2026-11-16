# TameAssistAddon

An independent **dedicated-server-only** BepInEx addon for Valheim, intended to work alongside [ServersideQoL TameAssist](https://github.com/ArgusMagnus/ValheimServersideQoL) without modifying or linking against TameAssist's internal classes.

> **Status: experimental v0.4.0, NOT production-tested.** An opt-in Boar-only implementation for unloaded pens and separate taming-time chest feeding have been added, but it has not been validated in a disposable Valheim world or with a PS5 client. World writes are **OFF by default**. Do not enable on a valuable world.

## Capabilities

- Standalone, deterministic simulation planner and automated regression tests.
- Server-side ZDO boar diagnostics (read only).
- Experimental unloaded **boar** taming, ground feeding, chest feeding, love points, pregnancy, birth and vanilla baby timestamp/level writes.
- Optional feeding from *loaded, server-owned chests* for wild boars in the taming phase (a TameAssist 2.1.0 limitation). See [taming chest feed guide](docs/TAMING_CHEST_FEED.md).
- Reads documented TameAssist config keys from the standard BepInEx config path, without referencing the TameAssist assembly.
- Conservative client-exclusion radius (256 m) and no live-object writes.
- No custom network RPCs, prefabs or client DLLs. **PS5 vanilla support is a design goal, not yet verified.**
- Vanilla `Growup` is intended to handle baby growth after a zone is loaded, using the saved birth timestamp. Online growth and handed-off state still require integration tests.

### Known limitations

- Taming-phase feeding deliberately skips boars or chests whose ZDO is client-owned; this is particularly relevant on crossplay servers.
- The optional simulation currently supports **Boar** only; not wolves, lox, egg-layers or mixed-parent species.
- Does not support ContainerSigns per-chest feeding ranges, UnifiedConfig, or world-specific TameAssist configs.
- Pregnancy approximates vanilla's random love-point mechanic and is not yet parity-tested.
- Child position currently uses a conservative offset, but has not been terrain-height validated.
- World writes are not transactional across multiple ZDOs: a crash between the parent and food/baby update can cause lost food/birth or an inconsistent state; this must be fixed before production release.
- The 256 m player exclusion is conservative; it does not prove zone inactivity in every networking edge case.
- **No gameplay or console test has been performed yet**.

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
