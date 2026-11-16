# Pelican deployment and test plan

## Status
This is a **diagnostic-only** plugin. It does not make unloaded boars breed.
Core simulation planning is independent of gameplay; no changes are committed to ZDOs.

## Installation
1. Back up the world using Pelican Backups. Keep a separate copy of the original saves.
2. Confirm BepInEx 5.x is loading on the server and ServersideQoL TameAssist is working.
3. Build against the exact server version; see README.
4. Stop Valheim and put **TameAssistAddon.dll** in `/home/container/BepInEx/plugins/`.
5. Start Valheim; locate the plugin log entry in `BepInEx/LogOutput.log` or console.
6. Set `Enabled = true` in `BepInEx/config/thelair.valheim.tameassistaddon.cfg` for read-only boar diagnostics.
7. Confirm a PS5 vanilla client can still join. The diagnostics must not change gameplay.

## Validation before enabling any world writes in a later version
- Inspect ZDO fields and prefab parameters from the exact installed game build.
- Verify exact loaded/active-zone status, not approximate player proximity.
- Confirm client-ownership checks, including crossplay users, before editing food or animals.
- Verify server-side inventory deserialization/write-back, including leave-at-least.
- Validate creation of a baby ZDO and level inheritance on a disposable world.
- Add durable per-animal idempotency checkpoints to handle server crashes.
- Test with active players, empty pens, several chests, starving animals, world reload and server restart.
- Test PS5 connections before shipping.

## Rollback
Stop the server, remove the addon DLL, restore the pre-test world backup if game-data writing was enabled in a future build, and restart. Disabling the plugin does not reverse prior world mutations.
