# Pushback

Pushback is a **separate ground-movement mechanism** from the taxi pipeline (fillet → pathfinder → navigator). It is a tug reversing the aircraft tail-first away from a gate. It does **not** use `GroundNavigator` or the taxiway graph route — movement is a bespoke per-tick reverse steered by `AircraftGroundOps.PushbackTrueHeading`.

Core code: `src/Yaat.Sim/Phases/Ground/PushbackPhase.cs`, `GroundCommandHandler.TryPushback` / `TryPushbackToSpot`, and `FlightPhysics.UpdatePosition` (which displaces the aircraft along `Ground.PushbackTrueHeading` — tail-first — whenever that field is set).

## Why a pushback is not a taxi route

A pushback happens entirely in the **nonmovement area** (ramp/apron), under the tug/ramp-control, along ramp lead lines — not along ATC-controlled taxiway centerlines (7110.65 §3-7-2 NOTE 2; PCG *movement area* excludes ramps). Modeling it as a tail-first A\* taxi over the movement-area graph is wrong: it can route the aircraft onto a real taxiway and back to reach a ramp spot. A pushback must reverse **directly** to the target.

> This was GitHub issue #233: `PUSH $5A` at SFO gate D2 graph-routed ~999 ft down taxiway T5 onto taxiway Alpha (the only graph path from D2's ramp to spot 5A's lane) and reversed back — instead of the 529 ft direct reverse. The fix routes all `PUSH @parking` / `PUSH $spot` through `PushbackPhase`'s targeted mode.

## `PushbackPhase` — three modes

`PushbackPhase` (a `Phase`) covers all pushbacks. The mode is chosen by which fields are set:

| Mode | Set fields | Behavior |
|------|-----------|----------|
| **Simple** | neither `TargetHeading` nor `TargetLat/Lon` | Push straight back `CategoryPerformance.SimplePushbackDistanceNm` (≈1.3× aircraft length) to clear the gate, then stop. |
| **Heading-only** | `TargetHeading` only | Push back along a curved arc while rotating the nose to `TargetHeading`. Used by `PUSH FACE/TAIL <cardinal>` and `PUSH <taxiway> <facing>`. |
| **Targeted position** | `TargetLatitude`/`TargetLongitude` (+ optional `TargetHeading`) | Reverse directly to the target point along a pursuit arc, then rotate the nose to the final heading. Used by `PUSH <taxiway>` (target = a point on the taxiway) **and** `PUSH @parking` / `PUSH $spot` (target = the parking/spot node's position). |

### Lifecycle (targeted mode)

1. **Align** (`OnStart` / alignment stage): compute the alignment heading (nose faces *away* from the target so the tail points at it). If the nose is within `AlignmentThresholdDeg` (20°) of it, start reversing immediately; otherwise rotate the nose in place first. Gate pushbacks are typically already aligned (the tail points down the alley), so no in-place rotation occurs.
2. **Reverse arc** (`TickTargetedPushback`): each tick, steer `PushbackTrueHeading` toward the bearing-to-target at `CategoryPerformance.PushbackTurnRate` — a smooth pursuit arc (the tug curving the tail), not a straight-line slide — at `PushbackSpeed` (≈5 kt). Nose rotation to `TargetHeading` is delayed until `NoseRotationProgressThreshold` (60%) of the push is covered, then completes while still moving (no stationary nose pivot).
3. **Reached target** (`_reachedTarget`, within `TargetReachedThresholdNm` ≈3 ft): stop translating; finish rotating the nose to `TargetHeading` in place if not already there, then the phase completes and `AtParkingPhase` (queued after it) takes over.

## Mid-push face amendment (`TryUpdateTargetHeading`, issue #167)

A heading-only `PUSH FACE C` / `TAIL C` while a pushback is active amends the target facing in place — no new phase. Accepted until the nose has begun rotating to the prior target:

- **Simple mode**: until alignment completes (`_isAligned`).
- **Heading-only / targeted**: until 60% of the push distance is covered (or `_reachedTarget`).

After that it is rejected with `Unable, pushback turn in progress`. Non-heading-only `PUSH` commands during an active pushback are rejected (`only face/tail amendment accepted during pushback`).

## Notes / footguns

- The direct reverse is **not obstacle-aware** — it has no taxiway-graph guidance. `GroundConflictDetector` still speed-limits/holds a pushing aircraft near moving traffic (parked/held neighbors are passable — see #222), but the reverse path itself is a straight/curved line to the target. This is correct for a ramp pushback (short, in open ramp pavement).
- Snapshot: `PushbackPhaseDto` stores only scalars (`TargetHeading`, `TargetLatitude/Longitude`, `StartLat/Lon`, `ReachedTarget`, `IsAligned`, …) — there is no `TaxiRoute` to reconstruct, so restore is trivial.
