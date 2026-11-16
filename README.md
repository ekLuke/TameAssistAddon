# TameAssistAddon

A dedicated-server BepInEx plugin project for **Valheim**. The goal is to simulate taming, feeding, breeding and offspring growth while a zone is not loaded, without requiring mods on PC or PlayStation clients.

> **Status: early scaffold.** No unloaded-zone simulation is implemented yet. Do not deploy this as a working ranch mod.

## Design goals

- Keep [ServersideQoL TameAssist](https://thunderstore.io/c/valheim/p/ArgusMagnus/ServersideQoL_TameAssist/) installed and unmodified.
- Avoid referencing TameAssist internal classes, so TameAssist updates are less likely to break this addon.
- Simulate only *unloaded* areas to prevent double processing by vanilla and TameAssist.
- Ensure ZDO ownership, persistent checkpoints, inventory writes and child creation are safe before enabling world changes.
- Preserve vanilla networking and PlayFab crossplay (PS5 support must be validated in a real test).
- Expose independent BepInEx configuration, and treat compatibility with TameAssist configuration as an optional adapter.

## Planned features

1. Animal discovery and zone-activity detection.
2. Read-only diagnostics and dry-run simulations.
3. Container and ground-item feeding with safe inventory ownership handling.
4. Catch-up taming and growing, with timestamps.
5. Unloaded breeding and persistent offspring creation, with deduplication and vanilla population rules.
6. PlayFab/PS5 and dedicated-server integration tests.

## Build

Requires **.NET SDK** and local assemblies from a legally installed Valheim dedicated server with BepInEx 5.x. Set `VALHEIM_DIR` to the installation root that contains `valheim_server_Data/Managed` and `BepInEx/core`.

```bash
dotnet build src/TameAssistAddon/TameAssistAddon.csproj -c Release -p:ValheimDir=/path/to/valheim/server
```

On Windows, use e.g. `-p:ValheimDir=C:\\ValheimServer`. No proprietary game DLLs are committed.

The compiled addon DLL belongs in `BepInEx/plugins/`. **Currently it only starts and logs server status; it does not automate animal handling.**

## Safety and compatibility

Always back up the world before testing. The plugin should remain disabled for world writes until the ZDO, inventory and authority model has been verified against the target game build. No client-side assets or custom network RPCs should be introduced.

## License

GPL-3.0-only. See [LICENSE](LICENSE). GPL permits commercial usage; it requires GPL-compatible source availability when covered derivatives are distributed.
