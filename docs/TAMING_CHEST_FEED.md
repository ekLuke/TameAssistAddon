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

## Version 0.5.1: original TameAssist feeding bridge (experimental)

Version 0.5.0 **replaces the previous standalone live-chest feeding path** with a Harmony postfix on the original TameAssist 2.1.0 `TameableProcessor.Process` method. For partially-tamed vanilla Boar ZDOs, it calls TameAssist's original private `FeedFromContainer` implementation. In particular, upstream can request release of client-owned *containers* and retries through its own scheduling. The boar ZDO feeding update is performed by the upstream code, including when the boar ZDO is client-owned; the addon does not call `ZDO.SetOwner` to seize the boar. No client installation is needed.

This is dependent on TameAssist 2.1.0's private signatures. If those signatures change, patch installation fails safely and logs an error. There is no fallback to the earlier standalone feeding implementation. Since the original processor may unregister itself depending on its settings, **the bridge depends on upstream processing cadence**; leave `TamingProgressMessageType = InWorld` in the upstream config for the initial test. The old `TamingChestFeed.Tick` routine is no longer invoked.

At startup look for `Taming bridge active`. On each diagnostic interval look for `Taming bridge: eligibleProcessorCalls=..., feedingAttempts=..., fed=...`. These counters count processor calls, not distinct animals. With zero eligible calls, the upstream processor may not be invoking its processing callback for those boars; with nonzero attempts and zero feeds, inspect upstream container indexing, ownership and food.

Keep `[Experimental] EnableWorldWrites=false` to disable the unrelated unloaded-ranch simulation. For safety, only use the bridge on a **disposable backed-up test world**. Successful compilation does not demonstrate correctness in game.

### 0.5.1 startup fix

Fixes `System.Reflection.AmbiguousMatchException` in 0.5.0 by choosing the declared three-parameter `Process(ServersideQoLZDO, ..., PrefabInfo)` overload explicitly. Startup must now print `Taming bridge active`; `taming bridge active=False` means the feeding feature is not running. The 0.5.0 artifact should not be used for taming chest-feed testing.

## 0.5.2: per-boar retry scheduling and diagnostics

The bridge now has a bounded **per-ZDO retry interval** rather than invoking the upstream container feeding method on every TameAssist processor call. Configure `[TamingChestFeed] RetryIntervalSeconds = 5` (range 1–60 seconds). Each candidate boar can run at most one upstream feed attempt in that interval; the original TameAssist method remains responsible for deciding whether it is hungry, checking chest range/reserves and requesting client-owned containers to release ownership. A retry interval may delay a newly hungry boar's feeding by up to the configured interval, so do not use very large values on time-sensitive test worlds. Cache entries unused for 20 minutes are pruned during diagnostic logging.

Log counters: `eligibleCalls` (eligible processing calls that passed retry scheduling), `rateLimitedCalls` (calls avoided by scheduler), `feedingAttempts` (actual upstream invocations), `fed` (feeding ZDO timestamp changes), `noFeedChange` (invocations without feeding), `containerRegistryUnavailable`, `wildOrNotStarted`, `otherPrefabs`, `errors` and `cachedBoars`. **`noFeedChange` is not a diagnosis**: upstream can return without feeding for many reasons, including satiety, no accessible chest or insufficient food. We do not pretend to distinguish these reasons without deeper upstream instrumentation.

### Exact hunger precheck (0.5.2)

Before invoking upstream `FeedFromContainer`, the bridge now checks `ZDOVars.s_tameLastFeeding` against the **effective `Tameable.m_fedDuration` ZDO component-field value** (the same field read by TameAssist). It skips still-satiated animals and schedules a new check after at most 30 seconds, so it does not scan their chests repeatedly. The log includes `satiatedSkipped`; `rateLimitedCalls` includes the follow-up processor calls suppressed by the per-boar scheduler. Remaining reasons inside upstream `FeedFromContainer` are not directly observable and appear as `noFeedChange`. The upstream method is still authoritative for hunger, container ownership, food eligibility, reservation, and saving.
