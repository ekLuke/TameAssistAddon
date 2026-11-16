# Architecture and rollout

## Independence from TameAssist
Keep TameAssist unmodified. This addon does not link against its DLL or private APIs. If compatibility settings are implemented, parse a small documented subset with safe defaults and avoid depending on its internal class layout.

## Authority
Only the authoritative dedicated server may alter persisted animal or container ZDOs. Ownership and zone activity must be checked immediately before each write. Do not assume that an unloaded object has a live Unity component.

## Planned pipeline
1. Locate relevant ZDO records; confirm species and persistent state.
2. Filter out active zones; no overlapping work with vanilla or TameAssist.
3. Use server-time checkpoints to calculate bounded progress.
4. Simulate feed consumption conservatively; validate inventory serialization and atomicity.
5. Apply taming/growth/breeding changes only after verification.
6. Persist checkpoints and idempotency identifiers.
7. Recheck on zone activation and hand authority back to vanilla.

## Failure modes to test
- Players approaching or leaving during a simulation tick
- Two-player and PlayFab ownership transitions
- Crash between decrementing food and child creation
- World save/restart/rollback
- Animals at different star levels and crowded enclosures
- TameAssist update that renames config keys
- PS5 vanilla connection and visibility of born animals

## Stages
**M0:** Compile and load safely, no writes (current).
**M1:** Read-only ZDO diagnostics.
**M2:** Deterministic simulation with unit tests and dry-run.
**M3:** Safe feeding/taming/growing writes.
**M4:** Idempotent breeding and real-server integration testing.
