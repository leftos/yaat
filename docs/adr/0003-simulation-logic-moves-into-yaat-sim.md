---
status: accepted
date: 2026-09-02
---

# Tick-reachable simulation logic moves into Yaat.Sim

`TickProcessor.cs` is 1,974 lines, of which roughly 1,315 are undelegated ATC decision logic —
handoff auto-accept, three auto-track passes, pointout auto-acknowledgement, coordination timers,
the TDLS clearance lifecycle, strip auto-print, ASDE-X alerting, delayed handoffs. None of it has a
replay-path equivalent, and none of it records an action, so a replayed world reconstructs track
ownership, handoff state and coordination expiry only when a snapshot restore happens to overwrite
them. Between snapshots a replay drifts and then snaps back.

All tick-reachable logic moves into `Yaat.Sim`. The server keeps the broadcast and wire projections
and nothing else. Where a state model is built on the CRC wire contract, it is re-modelled as a
simulation core with a server-side wire projection rather than dragging wire records into `Yaat.Sim`
— `TdlsState` holds `ClearanceDto` and `TdlsStatus`, pinned by `CrcWireContractTests` against CRC's
own assembly metadata, and those types must stay where that test can see them. Surface coast splits
the same way: the lifecycle is simulation, the cached track DTO is a display cache.

**Which coast merges.** There are two lifecycles wearing one word, and only one of them merges.
*Disconnect coast* — the aircraft is gone and a DTO is the only residue, held until it expires or the
track re-associates — exists for ERAM, ASDE-X and SAID, and those three become one. *Coverage-loss
coast* — the aircraft is still in the world, still owned, dead-reckoned along its last vector and
reacquired if it climbs back above the floor — is a different subject and stays separate. The merged
disconnect lifecycle must carry both behaviours that live in the same sentence of the ASDE-X
reference as the 45-second interval: the re-association escape, and the *dropped* variant an aircraft
enters instead of *coasted* when the facility is its filed destination. A duration-only
parameterisation silently loses both.

Attendance is the exception that is not an exception. `PositionRegistry` describes live socket
connections; it stays server-side and cannot be otherwise. The server supplies the *derived*
attendance set, `Yaat.Sim` records it as scenario state, and replay reproduces the same decisions by
replaying the same input rather than re-deriving it.

## Consequences

- Roughly 1,750 lines of state and mutation types cross the repo boundary, `StripMutations` at 1,041
  lines being the largest.
- Strip and TDLS state gain `Yaat.Sim` snapshot coverage; yaat-server's session-persistence DTOs for
  the same state collapse into it rather than remaining a synced duplicate.
- `ArtccConfigService` is not a blocker: every call these methods make forwards onto an extension
  method already in `Yaat.Sim`, and `SimScenarioState.ArtccConfig` already holds the resolved config.
  Only the HTTP-fetch and cache shell stays server-side.
