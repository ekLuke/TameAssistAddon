# Experimental chest feeding while taming (v0.5.0)

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

## Diagnostics in 0.4.1

The periodic boar diagnostics intentionally exclude completely wild boars. It reports only `tamed` and `taming` and the count of other wild ZDOs excluded. The feed sweep reports how many partially-tamed boars were processed and why they could not be fed: `notLoaded`, `clientOwnedBoars`, `satiated`, `noEdiblePrefab`, `noChestInRange`, `chestsNotLoaded`, `clientOwnedChests`, `chestsInUse` and `missingFoodOrReserve`. These are per-sweep diagnostic counters, not unique animals.

**Completely wild animals without prior taming progress are intentionally excluded.** To begin taming a new wild boar, first let it eat some food placed on the ground; future chest feeding can then be attempted while taming progresses. Original TameAssist continues to manage already-tamed boars.

## Version 0.5.0: original TameAssist feeding bridge (experimental)

Version 0.5.0 **replaces the previous standalone live-chest feeding path** with a Harmony postfix on the original TameAssist 2.1.0 `TameableProcessor.Process` method. For partially-tamed vanilla Boar ZDOs, it calls TameAssist's original private `FeedFromContainer` implementation. In particular, upstream can request release of client-owned *containers* and retries through its own scheduling. The boar ZDO feeding update is performed by the upstream code, including when the boar ZDO is client-owned; the addon does not call `ZDO.SetOwner` to seize the boar. No client installation is needed.

This is dependent on TameAssist 2.1.0's private signatures. If those signatures change, patch installation fails safely and logs an error. There is no fallback to the earlier standalone feeding implementation. Since the original processor may unregister itself depending on its settings, **the bridge depends on upstream processing cadence**; leave `TamingProgressMessageType = InWorld` in the upstream config for the initial test. The old `TamingChestFeed.Tick` routine is no longer invoked.

At startup look for `Taming bridge active`. On each diagnostic interval look for `Taming bridge: eligibleProcessorCalls=..., feedingAttempts=..., fed=...`. These counters count processor calls, not distinct animals. With zero eligible calls, the upstream processor may not be invoking its processing callback for those boars; with nonzero attempts and zero feeds, inspect upstream container indexing, ownership and food.

Keep `[Experimental] EnableWorldWrites=false` to disable the unrelated unloaded-ranch simulation. For safety, only use the bridge on a **disposable backed-up test world**. Successful compilation does not demonstrate correctness in game.
