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

## 0.5.3: read-only chest diagnostics for noFeedChange

When the upstream feeding routine returns without updating the taming boar's last-fed ZDO tick, the addon can now run a **read-only** snapshot probe, at most once per 30 seconds per affected boar. It scans vanilla Container ZDOs, using only serialized `ZDOVars.s_items` inventory data and prefab food names; it does not consume items, open chests, take ownership, change ZDO values or override the upstream feeding algorithm. Reported fields include `chestsWithinDefaultRange`, `inUse`, `clientOwned`, `unreadableInventory`, `withAcceptedFood`, and `withFoodAboveReserve`. Per-interval summary counters follow. This is a **diagnostic approximation**, not authoritative reconciliation: the upstream registry may have per-chest ContainerSigns range overrides or a different inventory index and may skip private chests. Empty/unloaded/unsynced serialized snapshots may not reflect instantaneous in-game inventory state. Keep `EnableWorldWrites=false` on the disposable test server.

## 0.5.4: client-owned chest reprocessing

Field logs from 0.5.3 consistently showed a container within range with accepted food above reserve, not in use, but client-owned. TameAssist's `FeedFromContainer` requests a normal ownership release and returns the `ScheduleReprocessing` flag. Previously our Harmony postfix discarded this flag. The bridge now recognizes it and requests a new processor pass using the original `Processor.ScheduleReprocessing(ServersideQoLZDO,float)` API after the configured retry interval. It does not claim ownership, send invented RPCs, or alter inventory. The `ownershipRetries` counter records returned flags, not successful owner transfers. Confirm `fed>0` in gameplay before declaring fixed. Use only the cloned backed-up test world.

## 0.5.5: tracked ownership and bounded backoff

The Harmony bridge now **observes the exact `ContainerRegistryProcessor.RequestOwnership(ServersideQoLZDO, PlayerID)` call** made inside the original TameAssist feeding operation. It records the chest ZDO and owner at the request, then monitors that same chest's owner on subsequent taming processor calls without taking ownership. Only a server-owned or unassigned chest counts as released; a client-to-client transfer is logged separately. On release, the bridge immediately retries upstream feeding. If the chest remains client-owned, it uses exponential retry intervals (default 5s, 10s, 20s, 40s, 60s maximum), handing scheduling to the original processor; no extra ownership RPC is sent by the addon. This may delay refeeding a persistently client-owned chest, but avoids repeated ownership requests every five seconds.

New diagnostic counters: `ownerRequestsSeen` (original ownership method reached, **not** confirmed RPC delivery), `ownerReleased` (owner became server/unassigned), `ownerChangedClient` (owner changed between non-server clients), `ownerStillHeld` (repeat ownership requests), and `ownerRetriesUntracked` (upstream scheduling flag without captured chest). Logs identify the chest and owner at the first pending request and on actual owner transitions. **Client cooperation is still required** to release actively client-owned chests. No owner seizure, inventory changes, custom client-side network packets, or modifications to experimental unloaded simulation were added.

Artifacts remain named `TameAssistAddon.dll` and `TameAssistAddon.Core.dll` (version is only in plugin metadata and the GitHub Actions archive label). Do not rename either DLL because existing assembly references depend on these names. Use only on a cloned/backed-up test world until a complete feeding cycle is confirmed.

## 0.5.6: fix for synthetic ServersideQoL owner

The 0.5.5 field log showed the same chest alternating between a vanilla client owner and a large negative owner ID. The upstream `ServersideQoLZDO.IsOwnerOrUnassigned()` explicitly treats `PlayerID.GetModPlayerID().Value` as accessible to the server, even when vanilla `ZDO.IsOwner()` is false. Our owner monitor previously misclassified that synthetic mod owner as another client and delayed feeding. 0.5.6 resolves `GetModPlayerID()` via reflection from the installed ServersideQoL assembly and treats it as usable (like unassigned/server ownership) for deciding whether to retry upstream feeding. It does **not** force-set ownership or write inventory. Validate `ownerReleased` / `fed` with the remaining taming boar on the cloned world. DLL filenames remain unchanged.

## 0.5.7: juvenile census + explicit offline-pen bounds (test build)

- Diagnostics enumerate `Boar_piggy` ZDOs separately from adult `Boar` (which still reports `tamed`, `taming`, and excluded wild adults).
- Optional offline adapter is now **fail-closed**: activating `[Experimental] EnableWorldWrites=true` **also requires** `[ExperimentalPen] ConfineOfflineRanch=true` and valid world coordinates `MinX,MaxX,MinZ,MaxZ` (span >2 m each). These are world X/Z coordinates, **not** chunk indices. The offline pass touches only adult boars with positions inside this rectangle. Offline births are clamped to an inset of 1 m inside the rectangle, never generated outside it by this adapter.
- The rectangle is an operator-defined **geometric approximation** of a pen, not a scan of actual fence pieces. For an irregular perimeter, configure an inner rectangle that is wholly inside the fence. It does not prevent the **vanilla client-owner birth routine** from spawning outside when a player has the pen loaded. That requires a client-side patch or a different game-authoritative approach.
- The old unloaded adapter still has unverified taming, chest-inventory and breeding writes. **Keep `EnableWorldWrites=false` for real saves**. Test only with a disposable restored world and backup. The addon never starts a synthetic Unity simulation merely because players leave.
- No client-side mods are introduced. DLL filenames remain `TameAssistAddon.dll` and `TameAssistAddon.Core.dll`.

### Experimental offline juvenile maturation (0.5.7)

The disabled-by-default offline adapter can also replace a `Boar_piggy` ZDO by a vanilla `Boar` ZDO after its prefab's `Growup.m_growTime` has elapsed. It preserves the tamed flag and level, only touches unloaded piglets inside the configured rectangle and more than 256 m from any connected player, skips piglets with active peer owners, and bounds conversions to 16 per tick. This is still **experimental**: ZDO destruction/creation is not transactional and may lose or duplicate an animal if interrupted. Test restart/rollback, piglet appearance and pen containment before using on a valuable world. The offline tick also has custom feeding/taming/breeding paths that are not proven to match all vanilla constraints. `EnableWorldWrites=false` keeps all of these mutations off.
