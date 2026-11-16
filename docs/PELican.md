# Pelican installation, tests and rollback

## Important

**TameAssistAddon v0.3.0 is experimental.** It has a Boar-only unloaded ranch write adapter, but its runtime behavior is **not validated** on a disposable world or with PS5. Do not enable world writes on your primary world.

## Requirements

- Dedicated Valheim server running on Pelican with BepInEx already functional.
- Existing ServersideQoL + TameAssist can stay installed unchanged.
- A fresh complete world backup (not just BepInEx config).
- Compiled `TameAssistAddon.dll` AND `TameAssistAddon.Core.dll` from [GitHub Actions](https://github.com/ekLuke/TameAssistAddon/actions/workflows/plugin-build.yml) or a local build against your exact dedicated-server version.

### Installation

1. Stop Valheim in Pelican.
2. Back up the world via Pelican **Backups** and keep another off-server copy.
3. Open File Manager and upload **both DLLs** into `/home/container/BepInEx/plugins/`.
4. Start the server once. Read `BepInEx/LogOutput.log`; it should report `TameAssistAddon 0.3.0 loaded`.
5. Keep both experimental safety flags false; use only diagnostics initially.
6. In `BepInEx/config/CursedKarma.Valheim.TameAssistAddon.cfg`, set `[Diagnostics] Enabled = true`, restart (if needed), and check boar ZDO counts in the log.
7. Confirm PC and PS5 vanilla clients still connect.

### Disposable-world functional test only

The plugin has two independent safety switches. They are **OFF by default**:

```ini
[Experimental]
EnableWorldWrites = false
IHaveBackedUpTheWorld = false
UnloadedSpeedPercent = 50
CatchupLimitHours = 12
GroundFeedRadius = 8
ChestFeedRadiusOverride = 0
MaxAnimalsNearby = 4
```

To try an experimental run, create a **separate disposable world**, verify the backup and change both switches to true on that world. The addon attempts to simulate only **unloaded Boar** ZDOs when all connected players are at least 256 m away and no corresponding live object is found.

If `ChestFeedRadiusOverride = 0`, it uses the normal documented TameAssist `FeedFromContainersRange` value from `ArgusMagnus.ServersideQoL.TameAssist.cfg`; the TameAssist setting `FeedFromContainers` must be true. Per-container sign overrides and unified or world-specific TameAssist configs are not yet supported.

### Validation matrix

- Two tamed boars and accepted food on the ground; test births at distance and on return.
- Same setup with a nearby standard vanilla chest containing extra food; test inventory differences.
- Cinghiali of different levels; check offspring's inherited level.
- Leave and return with a vanilla PS5 player to verify visible baby ZDOs and no join errors.
- Move into the 256 m exclusion radius while the plugin is running; ensure no duplicate children.
- Restart the dedicated server during pregnancy, feeding or tame progress; verify state consistency.
- Empty food and full enclosure conditions.
- Restore world backup successfully before considering main-world use.

### Rollback

Stop the server; remove both addon DLLs (not TameAssist), then restore the pre-test world backup **if experimental writes were enabled**. Restart and check BepInEx logs. Removing the addon alone cannot undo world-state mutations.

## Known limitations

- Only Boar, not all species supported by ServerBasedRanch.
- No macellazione or auto storage of meat.
- Not crash-atomic across feeding/chest/birth updates.
- No verified PS5 crossplay test yet.
- Not certified compatible with future Valheim or TameAssist updates.

See [compatibility issues](https://github.com/ekLuke/TameAssistAddon/issues).
