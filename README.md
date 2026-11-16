# TameAssistAddon

An independent BepInEx add-on project for Valheim dedicated servers with vanilla PC/PS5 clients and ServersideQoL TameAssist.

> **Status: v0.2.0 development / diagnostic-only.** The standalone C# simulator produces deterministic, non-mutating plans for feeding, taming, growth and breeding. The BepInEx plugin currently scans boar ZDOs read-only. **Unloaded pen breeding and container feeding do not yet work in-game. Do not install this expecting a working ranch.**

## Repository contents

- `src/TameAssistAddon.Core/`: pure C# side-effect-free simulation planner.
- `tests/TameAssistAddon.Core.Tests/`: executable regression tests.
- `src/TameAssistAddon/`: server-side BepInEx diagnostic plugin.
- `.github/workflows/core-ci.yml`: hosted runner builds and runs core tests on push / PR.
- `.github/workflows/plugin-build.yml`: on-demand self-hosted plugin build with locally installed game assemblies.
- `docs/PELican.md`: deployment and rollback instructions.
- `docs/COMPATIBILITY.md`: risks and constraints.

## Test the simulation engine

Install .NET SDK 8:

```sh
dotnet run --project tests/TameAssistAddon.Core.Tests/TameAssistAddon.Core.Tests.csproj --configuration Release
```

The planner intentionally does not emulate all vanilla mechanics yet, including random love points and game-specific ZDO baby creation. Its numeric defaults are placeholders until verified against the current game assemblies.

## Build BepInEx plugin

Set `ValheimDir` to your local dedicated-server installation (with `valheim_server_Data/Managed/` and `BepInEx/core/`). Game DLLs are not included in this repository.

```sh
dotnet build src/TameAssistAddon/TameAssistAddon.csproj -c Release -p:ValheimDir=/path/to/valheim/server
```

The plugin assembly, once successfully built, belongs in `BepInEx/plugins/`. See [Pelican guide](docs/PELican.md). Its configuration is `BepInEx/config/thelair.valheim.tameassistaddon.cfg`; set `[Diagnostics] Enabled = true` for read-only logging.

## Roadmap

1. ✅ Independent project, GPL-3.0-only, standalone planner and regression tests.
2. ✅ Conservative diagnostic discovery of saved boar ZDOs; does not mutate the world.
3. ✅ Automated core test workflow; manual plugin workflow requires local game assemblies.
4. ⬜ Verify current Valheim API signatures by building against the exact dedicated-server version.
5. ⬜ Safe container and ground-food reading/writing with ownership checks.
6. ⬜ Persistent, idempotent wild taming, adult breeding and baby growth.
7. ⬜ Test no double processing when players enter the active zone, on world reload, and with PS5 players.
8. ⬜ Release a verified production DLL.

## Relationship to TameAssist

TameAssist stays installed and unmodified. This addon does **not** depend on TameAssist private types or require client installations. Settings integration may be implemented later through public documented configuration with safe defaults. Future game updates and crossplay compatibility must be integration-tested.

## License

GPL-3.0-only; see [LICENSE](LICENSE). GPL permits commercial use; redistributed covered derivatives must respect GPL source-sharing requirements.
