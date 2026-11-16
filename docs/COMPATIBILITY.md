# Compatibility boundary

Target: dedicated Valheim server + BepInEx, vanilla PC/PS5 clients, with ServersideQoL TameAssist installed independently.

TameAssist feed from containers is not automatically available in unloaded areas. Its own current implementation uses registries, ownership requests and inventory serialization. For compatibility, avoid calling private classes or assuming that reading its config file is enough to reproduce those writes.

The plugin should only operate server-side. Do not add custom prefabs, UI, RPCs, synced custom items or client-only components.

Current mode: **read-only diagnostics** plus tested deterministic simulation *plans* in the standalone core. Actual world writes are intentionally not implemented.

Game API signatures and world persistence behavior may change after a Valheim update; server compatibility requires recompilation and an integration test.

Useful references:
- https://github.com/ArgusMagnus/ValheimServersideQoL
- https://valheim.hexium.gg/mods/bruceirons-team/ServerBasedRanch
