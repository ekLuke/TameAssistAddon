# Automatic multi-species fenced ranch (planned; fail closed)

> **Current version: 0.5.12 (2026-11-17).** The obsolete manual-coordinate
> offline boar writer was removed along with [ExperimentalPen] and its
> [Experimental] toggles. AutoPenProbe remains **read-only** and does not
> yet authorize all-species offline feeding, taming, births or growth.
> 2D candidate polygons are not conclusive proof of a closed 3D pen.
> The upstream TameAssist bridge works only in loaded zones and now uses
> `[TamingChestFeed] ConfirmBackedUpTestWorld=true` to opt in on a test world.
> The durable offline feeding WAL recovery code is retained solely to
> reconcile transactions created by earlier versions. Replace the old
> BepInEx config to remove historical stale entries. The full config is
> maintained at `config/CursedKarma.Valheim.TameAssistAddon.cfg`.
>

## Requested behavior

The addon shall manage *every* vanilla tameable/breeding creature inside an
automatically detected closed enclosure, without per-pen coordinate rectangles
or requiring PC/PS5 client mods. Animal species must be discovered from
`Tameable`, `Procreation`, `Growup`, `EggGrow` and the prefabs referenced
by these components. Mixed-species pens are legitimate, but population and
partner checks must remain species-specific. Eggs are not ordinary newborn
creatures and require separate temperature, cover, and incubation handling.

## Pen discovery is NOT implemented in 0.5.10

A nearby chest, proximity to a tamed animal, an axis-aligned rectangle or a
world ZDO count does not demonstrate enclosure. Inactive-world ZDO records lack
live physics/collision and fence pieces may have irregular transforms. The
algorithm must not authorize world writes until it can verify the real
closed perimeter.

Proposed validation:

1. Discover *candidate* animal groups from tamed/partly-tamed creature ZDOs
   and map the surrounding build-piece ZDOs; do not scan all world colliders
   every game frame.
2. Classify physical fence/wall/gate pieces using prefab collider information,
   applied ZDO rotations/transforms and relevant collision layers. Test
   terrain elevation, open gates, gaps, stacked/overlapping walls and roofs
   separately. Mere nearest-fence proximity is not sufficient.
3. Flood-fill a bounded world-space grid from the animals' walkable positions
   using species-specific capsule clearance. Reject any contour that reaches
   the search boundary or any uninspectable edge. Use conservative inside
   polygons, not inferred rectangle bounds.
4. Persist the validated pen's stable fingerprint of barrier ZDO identities,
   transforms and open/closed states. Invalidate it on construction/removal,
   damage/destruction and gate changes. On a reloaded zone revalidate before
   simulation.
5. Resolve membership for each tameable or growing creature from its ZDO
   position against the validated polygon. Uncontained/unverifiable animals
   remain vanilla-managed.
6. Extend the offline feeding journal per species and introduce a separate
   crash-consistent journal for birth, egg creation/hatching and growth.
   Implement idempotent offspring identities before enabling writes.
7. Test with all players disconnected, rejoin from PC/PS5 vanilla, force
   process termination, world save/restart, mixed pens, and multiplayer
   ownership handoff. Do not run the same animal in two simulators.

The old 0.5.9-0.5.11 boar-only adapter and manual rectangle were removed
in 0.5.12. They were **not** a correct implementation of the new
all-animals-inside-fence requirement.
Never enable world writes on valuable saves until this specification is
implemented and validated. The new optional `[Diagnostics] LogAllSpecies`
performs a read-only world-wide species and offspring census; this does not
claim pen detection. All existing DLL filenames stay unchanged.

## Research references verified 2026-11-17

- **BaseTerritory 0.9.65**: https://valheim.hexium.gg/mods/HanonHeim/BaseTerritory
  Public documentation states that fenced enclosure detection uses fences,
  walls, closed doors, and some cliffs; a feeding chest is a controller, and
  multiple chests in the same closed pen share membership. Separate pens
  sharing a wall are not merged. Mixed species are recognized, although its
  own breeding feature pauses when multiple species share one pen. This mod
  requires both server and client, so it is a design reference only.
- **PenPosts 1.3.15**: https://thunderstore.io/c/valheim/p/Einherjer/PenPosts/
  Traces walkable pen floor from a sign, handles irregular walls/rocks/steps
  and detects gaps, invalidates after construction changes, and excludes
  adjacent pens. It intentionally allows fallback circle/square on failure;
  **TameAssistAddon must NOT adopt that fallback for offline writes**.
  It also requires client installation. The linked public GitHub repository
  https://github.com/Anwo91/valheim-mods currently exposes only a README
  and issue templates, not the PenPosts C# implementation.
- **ServerBasedRanch 1.0.3**:
  https://valheim.hexium.gg/mods/bruceirons-team/ServerBasedRanch
  Server-only unloaded ZDO simulation; differentiates active player areas
  and offline areas, processes feeding/taming/pregnancy/birth, uses bounded
  catch-up. Its description does **not** establish geometric recognition
  of closed pens: proximity-based population is not a substitute.
- **BreedingInPen 1.0.1**:
  https://github.com/Shuiei/valheim-breeding-in-pen
  Public C# source for server-only vanilla ZDO component field overrides,
  newborn relocation and replacement. Helps with client compatibility,
  but does not detect physical enclosure contours.

### Recommended synthesis

Use BaseTerritory's shared-pen grouping and PenPosts' navigation/fence
perimeter validation, implemented independently in the dedicated server.
A player-built feeding chest or sign may serve as a **seed** for bounded
flood fill without manual coordinates, but the final validation must
require a closed polygon. Starting instead from clustered tamed animals
may eliminate marker requirements; verify that search complexity stays
bounded, and never infer closure merely from nearby fences. Record explicit
reason codes: `closed`, `open-gap`, `missing-collider`,
`invalid-terrain`, `too-large`, `inactive`. No writes for anything
but `closed`. Inspired by ServerBasedRanch's offline time handling, but
use per-species vanilla config and journaling; never simulate animals
inside an active client-owned area. No external mods installed or
their source code copied into TameAssistAddon.

## Implemented foundation (current repository)

`src/TameAssistAddon.Core/PenGeometry.cs` now contains a deterministic,
read-only **2D barrier flood-fill** that accepts explicit, externally
verified solid wall segments and a point inside a candidate pen. It rejects
open fences/gates, incomplete barriers, unverified segments, invalid
configurations and unbounded geometry. It reports the bounded interior,
estimated floor area and point membership; it does **not** query Unity or
ZDOs itself. Core tests cover a closed rectangular pen, open gate,
broken fence, unverified wall and two adjacent independent enclosures.

**This is a geometry prototype, not yet verified closed-pen detection in
Valheim.** The engine reports `closed-2d-candidate`, which is deliberately
insufficient to authorize world writes: verified 3D collider extraction,
terrain/level checks, fence destruction/open state, animal-specific
navigation clearance and ZDO invalidation must still be implemented and
tested on Valheim l-1.0.21. An enclosing fence can appear closed in a
top-down projection but be climbable, have a gap under it or be open on
uneven ground.

The plugin still exclusively loads in BepInEx on the dedicated server.
No client DLL, custom network prefab, UI, RPC or PlayStation mod has been
added. The unsafe Boar-only offline writer was retired in 0.5.12, not reenabled
without boundaries.

## 0.5.11: real-world ZDO / prefab probe (read-only)

The optional `[Diagnostics] AutoPenProbe=true` now runs a bounded,
server-only *candidate* extraction from persisted tameable creature ZDOs.
For each of up to 12 tamed animal seeds, it enumerates nearby built pieces
whose prefab names identify likely walls/fences, inspects non-trigger
`BoxCollider` geometry in the server's prefab hierarchy, transforms the
solid horizontal segments with the ZDO's saved world rotation and position,
and sends the segments to `PenGeometry.Detect`.

It logs `candidate`, `reason`, `area2D`, `segments`,
`gatesUnverified`, and `collidersUnverified`. Unknown prefabs and
mesh-only walls are **not** assumed solid. All doors/gates are left
unverified regardless of their saved state, so an enclosure depending on
a gate cannot yet be reported as closed. Height/terrain and species-specific
collision clearance remain unverified, and this feature NEVER enables
feeding, pregnancy, growing or birth writes. `closed-2d-candidate` is not
a verified enclosure. The old manual-rectangle boar simulator was removed in 0.5.12.

The probe scans only 28 m around at most 12 adults, and is disabled by
default because prefab-indexed scanning can be expensive on large worlds.
Start with `TickIntervalSeconds=60` or more and inspect server frame time.
The current scan is appropriate for **instrumenting and calibrating** fence
prefab detection against actual test-world objects. A subsequent version
must recognize saved gate open/closed state, vertical and terrain clearance,
mesh colliders, mixed-species animals, structural change invalidation and
safe idempotent birth/growth before enabling automatic offline management.

```ini
[Diagnostics]
AutoPenProbe = true
LogAllSpecies = true

[Experimental]
EnableWorldWrites = false
AllowUnsafeOfflineBirthGrowth = false
```

## Wild animals inside pens (experimental 2D probe)

Wild, partly-tamed and fully tamed creatures are now all considered in the
read-only candidate analysis. The first 8 seeds favor tame/taming animals and
4 additional seeds cover wild-only enclosures. When a candidate 2D polygon
appears closed, all detected adult species within it are counted separately
(`wildInside2D`, `tamingInside2D`, `tamedInside2D`). Wild creatures
outside any verified enclosure must not receive feeding, progression or
breeding writes. This 2D candidate count **does not** prove confinement:
all 3D perimeter/gate/terrain checks remain outstanding. No offline-world
writes are enabled by this change. On large worlds seed sampling can miss
some wild-only pens; broaden candidate discovery in a later iteration.

## 0.5.13 gate-state diagnostic enhancement

The 2026-11-18 Pelican test world log confirmed two partly-tamed Boars,
46-47 candidate wall segments, three previously unverified gates, and
`reason=open-gap` for both animals despite 51 nearby barrier pieces.
No player was connected: `Taming bridge fed=0` is expected and is not
evidence of feeding failure.

The read-only probe now uses the standard vanilla `Door` component
plus saved ZDO `ZDOVars.s_state` (0 = closed; nonzero = open). Only a
recognized closed Door with an extractable rigid BoxCollider contributes
a candidate **2D** barrier segment. Unknown door prefabs and open doors
still contribute no wall. Additional diagnostics: `gatesClosed2D`,
`gatesOpen`, `gatesUnverified`, `gateDetails`, and
`rejectedPrefabs`. This should distinguish a real gap from one made by
the earlier policy of omitting all doors. It DOES NOT certify the true
animated door collider or any vertical escape path; these remain
mandatory prerequisites for any automatic offline world writes.
No changes to the configuration keys and no client dependencies.

## 0.5.14: optional server-only barrier diagnostic signs (testing)

The optional `[BarrierSigns] Enabled=true` creates a bounded set of up to
12 **vanilla nonpersistent** signs near selected fence pieces, using only the
existing `sign` prefab and its standard `ZDOVars.s_text` field. No custom
client component, RPC, or asset bundle is shipped, so PC and PS5 vanilla
clients can receive the signs normally. There is at most one generated sign
per selected barrier ZDO, even when several animals sample that barrier.

Text includes the barrier prefab name, approximate BoxCollider dimensions,
current 2D enclosure result, segment count, closed/open/unverified door
counts, and wild/taming/tamed animal counts within the *candidate* region.
Those diagnostics are **not proof** of a physically closed 3D enclosure.

If the server detects a player-built vanilla sign within 1.8 metres of
that selected barrier, it removes **only its own tagged automatic sign**.
The player's sign is never deleted or overwritten when it contains text.
If the manually placed sign is empty or whitespace-only and the sign has
no connected client owner, the server fills its ordinary vanilla text
with the diagnostics. While a client still owns the sign, filling is deferred
to avoid a live ownership conflict. When a target disappears, tagged
automatically generated signs are removed; generated signs are not saved
through restarts.

`[BarrierSigns] DebugLog=true` adds per-sign created/updated/manual override/
empty fill/deferred ownership logs. A compact `BarrierSigns sweep` summary
is emitted every diagnostics interval. This feature creates/updates ordinary
world **sign ZDOs** but never edits animals, inventory, fences or terrain.
Use only on disposable/test worlds until validated in-game. For a fully
autonomous all-species offline ranch, 3D enclosure validation and separate
food/birth/growth journals are still outstanding.

## 0.5.15: exterior signs and protection against harvesting

Visual testing on 2026-11-18 revealed that the sign was placed inside the pen (towards the animal), and game logs showed Wood/Coal being picked up after a sign was removed. Sign placement now computes a horizontal wall normal from the prefab BoxCollider and ZDO rotation, selects the side opposite the animal seed, and places the sign about 0.62 m outside the selected fence section, facing away from the animal. This is an exterior **estimate**, not a verified 3D inside/outside classification.

Generated signs are tagged with `thelair.tameassistaddon.barrierSign` and record their parent barrier ID. They retain their original association when the animal moves and are removed automatically only when that barrier is missing (or when replaced by a nearby manual sign / explicitly disabled). The server additionally sets `Piece.m_canBeRemoved=false`, `WearNTear.m_noSupportWear=true`, `WearNTear.m_noRoofWear=true`, and very high `WearNTear.m_health`/`s_health` for vanilla-client protection. A guarded Harmony install patches `WearNTear.RPC_Remove`, `Remove`, `RPC_Damage`, `ApplyDamage`, `Destroy` and `Piece.DropResources` on the server for **tagged** signs only; it refuses sign creation if signatures cannot be verified at startup. These guards aim to prevent material farming and ordinary demolition. They do not give a cryptographic guarantee against a malicious/modified client that becomes network owner of a sign. Test from vanilla PC/PS5 clients and verify refunds after hammer/weapon attempts. Runtime counters `removalsBlocked`, `damageBlocked`, and `refundsBlocked` aid diagnostics.

Auto-signs remain nonpersistent world objects and are not installed on clients. All protection, marker, placement and cleanup code is in the dedicated-server DLL.

## 0.5.16: bounded signs per animal group and quiet console

The 2026-11-18 in-game tests showed that the sign faces outside and cannot be
removed with an axe/hammer, but earlier logic created **another** sign every
time an animal moved and the nearest fence section changed. A manually placed
sign was also ignored unless it happened to be on the exact same selected
barrier piece. These observations are addressed in 0.5.16:

- Nearby sampled animal seeds within 12 metres are grouped for **diagnostic
  sign placement only**, without pretending to know the exact pen polygon.
- A group retains a single auto sign associated with its original barrier and
  its members' ZDO identities even when animals move. Previous duplicates in
  that group are deleted without refunds.
- A player sign within 2.4 m of **any** scanned barrier ZDO around the group
  (within the probe's 28 m sampling radius) supersedes the group's auto sign;
  player text is preserved, or an empty sign receives diagnostics when the
  server is permitted to write it. This is a conservative approximate
  association; adjacent penned groups can overlap until 3D polygon
  recognition is reliable.
- A generated sign is still removed when its original parent barrier is gone.
  The established no-removal/no-damage/no-refund protections remain in place.
- All internal `TameAssistAddon` Info/Debug/Warning output is written to
  `BepInEx/config/TameAssistAddon/TameAssistAddon.log`, with a rotated
  `.old` backup at 10 MiB. **Only errors from our addon** reach its BepInEx
  console logger; other plugins and Valheim's own console messages remain
  unaffected. The logging source is deliberately not registered with the
  global BepInEx listeners.
- **No new CFG options:** the v0.5.15 config remains compatible with v0.5.16,
  with `[BarrierSigns] Enabled=true` for these tests.

No 3D validated pen selection or offline animal writes have been enabled.

## 0.5.17: blank manual vanilla sign recognized and safely filled

The 2026-11-18 05:05–05:09 test log confirmed the 0.5.16
sign-group deduplication and the manual override, but showed
`BarrierSigns manual override preserved` and
`emptyManualFilled=0` for a user-intended empty sign. Valheim
can represent an unedited sign as its three-dot placeholder rather
than a zero-length string. The new pure `SignTextRules` module
recognizes whitespace, `...`, `…`, and a prefab's actual
`Sign.m_defaultText`, while preserving real custom text. Core
tests cover both cases.

For client-owned manual signs, only a stable blank may be claimed
by the server, after 12 seconds during which the raw text and owner
remain unchanged. A lightweight 2-second pending poll fills it
without repeating the expensive 60-second global probe. If the user
writes text before that, the pending fill is cancelled, and the
user's words are preserved. A detailed file-log entry includes
the raw/placeholder value and final outcome. The initial detection
still runs on the ordinary diagnostic scan; so a newly placed sign
may take one scan interval plus the grace period to fill.

No new configuration keys, no client plug-ins, no changes to offline
animal state. Use the existing 0.5.16 CFG without modification.
