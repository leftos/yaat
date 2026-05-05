# EF: smart intercept distance for close-in aircraft

## Context

`EnterFinalCommand` always picks the entry point at the **standard glideslope-TPA intercept distance** (~3.2 NM from threshold for a piston on a 3° glideslope, derived in `PatternCommandHandler.GetEntryPoint` lines 1108–1117 of `src/Yaat.Sim/Commands/PatternCommandHandler.cs`). When the aircraft is already inside that distance — short final, low altitude, on or near the FAC — the resulting `PatternEntryPhase` waypoint is *behind* the aircraft, forcing a wide turn out and back to reach it. The user-visible symptom is a teardrop / 360.

The parallel-runway case is handled separately by the EF sidestep work (see commit landing this issue — sibling under same investigation). This one tracks the **non-parallel general case**: aircraft is close to the runway and well-aligned, but with no parallel runway to swap to. EF should still pick a sensible intercept point instead of one behind the aircraft.

S2-OAK-3 (1) | VFR Sequencing bundle (May 5, 2026) is the originating recording. Aircraft N42416 was at ~640 ft AGL on 28R FAC when EF 28L was issued. After the sidestep fix lands, the parallel-runway case is solved — but if the user had instead issued `EF 30` (non-parallel runway at OAK), the same teardrop behavior would occur and isn't covered by sidestep semantics.

## Goals

- For `EF <runway>` issued to an aircraft inside the standard glideslope-TPA intercept point, choose a closer entry point on the new extended centerline that the aircraft can reach without a teardrop.
- Preserve current behavior when the aircraft is at or beyond the standard intercept distance.
- No behavior change for the parallel-runway sidestep case (handled separately).

## Sketch

- In `PatternCommandHandler.TryEnterPattern` (entryLeg == Final branch, around line 116):
  - After computing the standard `entryPoint` via `GetEntryPoint`, also compute the aircraft's projected along-track on the new FAC (using `GeoMath.AlongTrackDistanceNm` against the threshold along the FAC reciprocal, mirroring the ERB logic at lines 240–277).
  - If the aircraft's along-track is *less than* the standard intercept distance AND the aircraft is on the approach side of the threshold AND the angle-off is shallow (<= 30°), prefer an entry point at the aircraft's current along-track (mirroring `useAircraftPositionAsEntry` for ERB).
  - The resulting `FinalApproachPhase.AnchorLat/Lon` should still be the standard threshold so glideslope tracking is unchanged.
- The existing loop-check (lines 110–193) already rejects infeasible cases — keep it. Sidestep semantics handle the parallel-runway short-cut. This work fills the remaining gap.

## Tasks

- [ ] Add an "already aligned, inside standard intercept" detection in `TryEnterPattern` for `entryLeg == Final` (mirror the structure of the existing ERB case).
- [ ] When detected, override the entry point to the aircraft's current position projected onto the new FAC.
- [ ] Confirm `FinalApproachPhase` still gets the correct threshold + glideslope (the entry point change shouldn't bypass glideslope tracking).
- [ ] Tests in `tests/Yaat.Sim.Tests/PatternCommandHandlerTests.cs` (or a new file) covering: aircraft inside intercept on FAC → close intercept; aircraft outside standard distance → unchanged; aircraft mis-aligned → still uses standard intercept (or rejected by loop check).
- [ ] Aviation review with `aviation-sim-expert` against AIM 5-4-x and 7110.65 visual-approach phraseology.

## Risk / Open Questions

- For non-parallel runways the aircraft still has to *turn* onto the new FAC. The "close intercept" only helps when the aircraft is mostly aligned. For meaningfully different headings (e.g. 28L vs 30 = ~16° split), this fix is marginal — most users would vector first anyway.
- Should the threshold for "close intercept" be a hard cutoff or a graceful interpolation? Hard cutoff is simpler and matches the ERB precedent.

## Related: post-sidestep go-around missed-approach reference

Surfaced by the sidestep-fix aviation review. Per AIM 5-4-19.3, if a go-around occurs *after* a side-step maneuver, the missed-approach procedure is the **original** approach's MAP — the IAP that authorized the sidestep. Today `ApplySidestep` (in `PatternCommandHandler.cs`) clears `Phases.ActiveApproach`, which is correct for landing-minima semantics but discards the MAP fixes a subsequent `GoAroundPhase` would need to fly the proper missed approach. Today the GA path falls back to vector-MAP for VFR / cleared-approach traffic, so this is dormant for typical scenarios — but it's wrong for an IFR side-step from a published approach. Track here; fix when implementing the IFR-side-step polish.

## References

- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:116-193` — existing EF loop check (rejects 360-class maneuvers).
- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:240-277` — existing ERB "use aircraft position as entry" logic to mirror.
- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:1108-1117` — standard glideslope-TPA intercept distance computation.
- `src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs` — phase the entry feeds into.
