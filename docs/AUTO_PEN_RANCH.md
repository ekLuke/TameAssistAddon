# Automatic multi-species fenced ranch (planned; fail closed)

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

For now the 0.5.9 experimental boar-only adapter and manual rectangular
boundary are retained strictly for disposable-world tests. They are **not**
a correct implementation of the new all-animals-inside-fence requirement.
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
added. Existing Boar-only offline changes remain behind their
experimental configuration; they are not an all-species pen solution.

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
a verified enclosure. The old manual-rectangle boar simulator is still a
separate unsafe experimental path with explicit opt-in.

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
