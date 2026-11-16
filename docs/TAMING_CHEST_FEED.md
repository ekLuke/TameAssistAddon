# Experimental chest feeding while taming (v0.4.0)

## Problem addressed

ServersideQoL TameAssist 2.1.0 calls `FeedFromContainer` for animals in the `Tamed` state, not the `Taming` state. This add-on provides an **independent** implementation for wild boars that are being tamed, without altering the TameAssist DLL.

## Behavior

- Server-side only; no custom RPC or client modification.
- Searches nearby *loaded* vanilla containers for food accepted by the live boar's `MonsterAI.m_consumeItems`.
- Uses `FeedFromContainers`, `FeedFromContainersRange`, `FeedFromContainersLeaveAtLeast` from the standard TameAssist config file.
- Requires both the boar and the chest to be **server-owned**, and the chest to not be in use. Does not take ownership away from PS5/PC clients.
- Consumes a food item via the live inventory and calls the container's save routine, then updates the boar's last-feeding timestamp.
- Does not handle unloaded animals; those are part of the separately gated experimental unloaded-ranch feature.
- Does not support per-chest ContainerSigns overrides or unified/world-specific TameAssist config files.

## Safety and limitations

**Not tested on Pelican / PS5.** The feature is disabled by default and requires two explicit settings on a disposable world. Player-controlled sectors can cause a boar or container to have client ownership, so the feature may do nothing in a populated vanilla crossplay server until ownership is handed to the server. Logs show skipped client-owned boars at Debug level.

The implementation is not transactional across both container and animal ZDO saves, so do not enable it on a valuable world without confirming a tested backup. Gameplay behavior is not guaranteed by successful compilation.

### Example config

```ini
[TamingChestFeed]
Enabled = false

[Experimental]
IHaveBackedUpTheWorld = false
EnableWorldWrites = false

[General]
TickIntervalSeconds = 60
```

To test on a disposable world: enable `TamingChestFeed/Enabled` and `Experimental/IHaveBackedUpTheWorld`; leave `Experimental/EnableWorldWrites` false if you do not want unloaded-ranch simulation. You also need `FeedFromContainers=true` and positive `FeedFromContainersRange` in your regular TameAssist config.

Log entry on successful consumption: `Taming chest feed: fed 1 wild boars.`

If the boar is client-owned, the addon deliberately skips it. This is not a client-side-compatible workaround for all ownership conditions.
