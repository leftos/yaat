# GIVEWAY: smarter auto-release geometry

## Why this exists

`FlightPhysics.IsGiveWayMet` decides when a `GIVEWAY`-held aircraft can resume. Today it uses pure bearing-and-heading geometry between the held aircraft and the target:

```csharp
// FlightPhysics.cs:1304-1350
if (headingDiff > 120)  return diffToTarget > 90;   // opposite: target is now behind us
if (headingDiff < 60)   return diffToTarget < 90;   // same dir: target is now ahead
return true;                                         // crossing: no conflict
```

It works for the two-aircraft head-on and trailing cases, but has three known gaps the original handoff flagged:

1. **Route-blindness**: target may be "past" the held aircraft by raw bearing while still ahead on a shared upcoming taxiway. Geometry says "released"; the route says "they're still in your way at the next intersection".
2. **No safety timeout**: if the controller typo'd the target callsign and `aircraftLookup` returns null, today the geometry path is bypassed and `UpdateGiveWayResume` clears the hold (target is "gone"). Good. But if the typo *matches* a real but distant aircraft, the held aircraft can sit forever waiting for it to pass.
3. **Target-stationary stalemate**: if the target has been stopped for >30s (own GIVEWAY, parking, mechanical), the held aircraft can probably proceed when it has lateral clearance. Today neither aircraft moves.

## What's proposed

Three additive improvements to `IsGiveWayMet`, each gated and observable:

### 1. Route-intersection check

When both aircraft have an `AssignedTaxiRoute`, compare upcoming nodes. If the target's remaining route still intersects the held aircraft's remaining route within N segments, the geometry-pass result is overridden to `false` (stay held).

```csharp
if (aircraft.Ground.AssignedTaxiRoute is not null
    && target.Ground.AssignedTaxiRoute is not null
    && RoutesIntersectAhead(aircraft, target, lookaheadSegments: 3))
{
    return false;  // even if bearing says target is past, the route says wait
}
```

Use existing helper `ShareUpcomingNode` in `GroundConflictDetector` (line 244) — or extract a shared `TaxiRoute.IntersectsWithin(other, segments)` utility into `Yaat.Sim.Data.Airport`.

### 2. Safety timeout

Add a per-aircraft "hold start time" so the detector knows how long a `GIVEWAY` has been active. After **5 minutes** (operator-tunable later), auto-release with a warning log.

Two implementation choices:

a) **State field**: `AircraftGroundOps.HoldStartedAtTick` (long, sim-time tick). Set when `Hold` becomes non-null; null when it clears. `IsGiveWayMet` checks `tick - HoldStartedAtTick > 5min` and force-releases. Snapshot via DTO field.

b) **Pure derivation via DeferredDispatch**: extend `DeferredDispatch.RemainingSeconds` semantics to GIVEWAY-held aircraft. Conceptually the same — but tightly couples direct GIVEWAY to the deferred-dispatch loop.

Option (a) is cleaner. Cost: one DTO field. The 5-minute constant lives in `GroundConflictDetector` or a new `GiveWayConstants`.

### 3. Target-stationary fallback

Track per-aircraft `LastMovedAtTick` (already implicit via `GroundSpeed > 0` checks). When the held aircraft's `IsGiveWayMet` is evaluated and the target has been stationary for >30s **AND** the held aircraft has lateral wingspan clearance to bypass the target, return `true`.

The "lateral clearance" check is already what `GroundConflictDetector`'s wingspan-bypass logic does — extract a helper `HasLateralClearance(subject, obstacle, layout)` callable from `FlightPhysics`.

### Risks

- **Performance**: `RoutesIntersectAhead` runs per-tick per held aircraft. With <100 aircraft and <10 held at once, this is fine. Cap lookahead to 3 segments to keep it bounded.
- **30s target-stationary fallback**: if the target is stationary *because* it's waiting for the held aircraft (mutual-yield stalemate), the held aircraft could proceed first. That's the *fix* for stalemate, not a bug. But the deterministic tie-break should be by callsign so the same aircraft always wins in identical geometry.
- **Test churn**: existing `IsGiveWayMet` tests in `BehindGroundTaxiTests` and `WaitCommandDispatchTests` need to verify the new fallback paths don't fire under the old happy-path scenarios.

## Implementation checklist

- [ ] Add `HoldStartedAtTick: long?` to `AircraftGroundOps` (set when `Hold` becomes non-null, clear when `Hold` becomes null). Wire ToSnapshot/FromSnapshot.
- [ ] Extract `TaxiRoute.RemainingNodesAhead(int segments)` helper in `Yaat.Sim.Data.Airport`.
- [ ] Add `IntersectsAhead(other, lookaheadSegments)` on `TaxiRoute`.
- [ ] Extend `IsGiveWayMet` with the three checks in order: route-intersection (overrides true → false), safety timeout (forces true), target-stationary + lateral-clearance (forces true).
- [ ] Add `GiveWayConstants` (or stretch the existing constants in `FlightPhysics`) for `SafetyTimeoutSeconds = 300`, `TargetStationaryThresholdSeconds = 30`.
- [ ] Emit a `SimLog.LogInformation` line when safety timeout or target-stationary fallback fires, so the controller can see in logs that the auto-release wasn't the normal geometry path.
- [ ] Add `LastMovedAtTick` tracking on aircraft (likely already exists or can be derived from `GroundSpeed > 0` history).
- [ ] Tests:
  - Route-intersection: held aircraft + target both routed onto the same upcoming intersection → geometry says released, route says hold → stays held.
  - Safety timeout: typo'd-but-real target far away → after 5 min, auto-release with log.
  - Stalemate fallback: held aircraft + target stationary for 30s, lateral clearance available → released.
- [ ] CHANGELOG.md `### Fixed` entry.
- [ ] Delete this plan after merging.

## Verification

1. Build a scenario with two aircraft on intersecting taxi routes. Issue `GW` on one. Tick until raw geometry says "target is past". Confirm the held aircraft does NOT release (route intersection blocks it). Then move past the intersection — confirm the released state.
2. Issue `GW GHOST999` (intentional non-existent target). Confirm normal "target is gone" auto-release fires within one tick.
3. Mutual-yield stalemate: two aircraft both `GW`-ing each other with lateral clearance. Confirm the lower-callsign aircraft proceeds first after 30s.
4. Verify `yaat-server.log` contains the "GIVEWAY safety timeout fired for {callsign}" line when the timeout path runs.
