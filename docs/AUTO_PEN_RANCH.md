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
