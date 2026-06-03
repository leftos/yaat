# Issue #175 — Better parallel-runway crossing after landing

> **Status:** initial investigation (seed plan; not yet implemented).
> **Labels:** enhancement, ground-cmds. **Source:** Discord thread, filed 2026-06-03.

## Symptom

Two linked behaviors around aircraft that land and must cross a parallel runway:

- **(A)** After landing and vacating (e.g. SFO 19L at G) with no pending instructions, the
  controller should clear the parallel runway by just `CROSS` or `CROSS 19R` — **without** first
  issuing a `TAXI`.
- **(B)** When an aircraft vacates **between** two parallel runways and there is **no intersecting
  taxiway** between its present position and the parallel runway's hold-short on the same taxiway,
  it should **auto-pull-up** to the parallel runway's entry hold-short (e.g. OAK 28L exit right on G
  or H → hold short of 28R) instead of stopping at the landing-runway hold-short.

## Root cause (confirmed)

`GroundCommandHandler.TryCrossRunway` / `TryCrossNextHoldShort`
(`src/Yaat.Sim/Commands/GroundCommandHandler.cs:1071,1163`) succeed only when the aircraft is in a
`HoldingShortPhase` **or** has an `AssignedTaxiRoute`; otherwise they return "No taxi route
assigned". So **(A) hinges on (B)**: if, after vacating, the aircraft already ends in a
`HoldingShortPhase` short of the **parallel** runway, then bare `CROSS` / `CROSS 19R` already works
through the existing `holdPhase` branch (lines 1083–1092 / 1165–1174). The gap is the post-exit
stopping behavior, not the CROSS command itself.

## Key files

- `src/Yaat.Sim/Commands/GroundCommandHandler.cs:1071,1163` — CROSS handlers + preconditions.
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — where the aircraft decides where to stop after
  vacating (see `docs/landing-and-runway-exit.md`).
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — exit-route segment targeting / hold-short
  positioning.

## Approach

Implement **(B)** in the runway-exit behavior: after vacating, if the exit taxiway continues to a
parallel-runway hold-short with no intervening intersecting taxiway, extend the exit path and
terminate in a `HoldingShortPhase` at that parallel hold-short. **(A)** then falls out for free via
the existing CROSS `holdPhase` branch.

**Mandatory `aviation-sim-expert` review** — auto-advancing to a parallel hold-short must respect
7110.65 runway-crossing / "vacate and hold" expectations: do not auto-enter the ILS critical area
and do not imply a crossing clearance was given (cite the local FAA refs; do not web-search).

## Verification

- Recording-/layout-driven tests at SFO (19L→19R at G) and OAK (28L→28R at G/H): assert the aircraft
  auto-holds short of the **parallel** runway after exit, and that `CROSS` / `CROSS <rwy>` with no
  prior TAXI clears it.
- `pwsh tools/test-all.ps1`.

## Open questions

- Confirm the precise trigger: "no intersecting taxiway between present position and the parallel
  hold-short on the same taxiway." Ensure it never auto-advances **across** an intervening taxiway
  intersection (where the controller may want to route the aircraft elsewhere).
- Decide whether auto-pull-up should be unconditional or gated by an existing
  auto-cross / auto-cleared-to-land-style preference.
