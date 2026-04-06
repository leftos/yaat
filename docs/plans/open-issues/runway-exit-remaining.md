# Runway Exit — Remaining Issues

## What We Built

Rewrote `RunwayExitPhase` from a centerline-node-walking state machine to an analog approach:

1. **Analog rolling**: `TickRolling` steers along the runway heading (no node-to-node walking) and continuously searches for exits ahead via `FindExitFromCenterline`
2. **Virtual approach nodes**: When an exit is found, a virtual segment `[aircraft position → branch node]` is prepended to the exit path, giving the `GroundNavigator` the full route `[virtual → branch → ... → hold-short]` with proper turn anticipation at the branch
3. **Exit-aware braking in LandingPhase**: LandingPhase finds the first reachable exit ahead and plans deceleration to reach the turn-off speed by the branch point. Hands off at the target speed while still ahead of the exit
4. **Comfortable vs firm braking**: Explicit exit commands (EXIT T) use firm braking (5kts/s). Default (no preference) uses comfortable braking (1.5× default rollout rate), so the pilot picks a natural exit — not the first one requiring maximum effort
5. **Unable broadcasts**: When an exit requires more than firm braking, the pilot broadcasts "unable" and stops planning for it

### Commits

- `1158c65` — Main rewrite (virtual nodes, analog rolling, LandingPhase simplification)
- Previous: `1eb1d24` (exit rewrite to GroundNavigator), `8debc6d` (taxi overshoot fix)

### Test Coverage

- `Sfo28rAllExitsTests` — All 13 SFO 28R exits, B738 at 145kts on 1nm final
- `OakAllExitsTests` — All 7 OAK 30 exits (B738 at 130kts) + all 7 OAK 28R exits (C172 at 70kts)
- `OakSpeedProfileTests` — Tick-by-tick speed/heading/distance visualization
- `IssueSfo28rExitTests` — Original bug recording test

### What Works Well

All exits tested produce smooth monotonic turns (no heading reversals). The virtual node approach gives clean 70-85° arcs for 90° exits and 17-37° arcs for high-speed exits. Speed planning decelerates naturally — the aircraft doesn't brake hard until kinematically needed.

## Remaining Issues

### 1. SFO L instant exit (1s, 4° turn)

**Symptom**: `EXIT L` at SFO 28R produces an instant exit with no turn.

**Root cause**: LandingPhase detects "unable" for L (aircraft at 87kts, turn-off 15kts). It stops planning and decelerates to coast speed (40kts). By the time it reaches 40kts, the aircraft is near node 327 (L's branch). RunwayExitPhase starts, `FindExitFromCenterline` finds L at node 327 within the -0.005nm behind-tolerance. The virtual segment has ~0 distance. The exit completes instantly.

**Attempted fixes**:
- Positive `minAheadNm` threshold in `FindExitFromCenterline` → breaks T exits where the aircraft decelerated correctly and is right at the branch
- Speed check in `TryFindExitAhead` → too aggressive, filters valid exits at coast speed
- Immediate handoff after "unable" → hands off at 87kts, breaks other tests

**Core tension**: The same function (`FindExitFromCenterline`) serves both LandingPhase (needs to find exits even when close, for braking planning) and RunwayExitPhase (needs to reject exits the aircraft blew past). The tolerance that allows T (correctly decelerated, right at the branch) also allows L (blown past, at the branch by coincidence).

**Possible approaches**:
- `FindExitFromCenterline` parameterized minimum along-track distance. LandingPhase passes 0 (find everything), RunwayExitPhase passes positive (must be ahead). But T needs ~0 tolerance since LandingPhase decelerated right to the branch
- LandingPhase hands off earlier (before reaching the branch), not at the branch point. RunwayExitPhase always has room for the virtual segment. But this changes the T exit behavior — the aircraft would be further from T when exit phase starts
- Track whether LandingPhase planned for this exit. If LandingPhase decelerated for it (speed ≈ turn-off), accept it. If LandingPhase didn't (speed = coast), reject exits within tolerance

### 2. SFO default picks E (111° / 140° turn)

**Symptom**: Without an exit command, the B738 on SFO 28R exits at E with a 140° heading change.

**Root cause**: The "always resolve" change makes LandingPhase find a candidate exit even without a preference. E at node 230 has a computed angle of ~70° in the `FindExitFromCenterline` result (not 111° — the angle depends on which hold-short the BFS picks). The comfortable braking threshold allows it, so the aircraft decelerates for E.

**The E angle confusion**: E has two hold-shorts — 836 (south/left, toward terminal) and 837 (north/right). The BFS in `FindAdjacentHoldShort` returns whichever scores best. The exit angle depends on which hold-short is returned. Left E might be ~70° (reasonable), right E might be 111° (backward). The angle penalty in `FindAdjacentHoldShort` penalizes >100° exits, so the BFS returns the left E at ~70°.

**But**: even at 70°, a 140° turn from the runway heading is a lot. The aircraft turns far past perpendicular. This suggests the hold-short position or the angle computation is producing unexpected geometry.

**Possible fix**: The default exit should prefer high-speed exits (T at 19°, Q at 20°) over moderate/steep exits (E at 70°). The comfortable braking threshold already biases toward exits reachable with gentle braking — T should score better than E since it needs less braking. Need to investigate why T isn't picked as default.

### 3. Relaxed exits go too far down the runway

**Symptom**: EXIT W1/W2/W3 at OAK 30 relax to W6 (far), not W5 (closer high-speed). EXIT C/C2/N/P at SFO relax to D, not T.

**Root cause**: When LandingPhase says "unable", it stops planning (`_exitResolutionEnabled = false`) and coasts to 40kts. RunwayExitPhase then searches ahead with the relaxed preference. By then the aircraft has rolled past the earlier exits (W5, T) and finds the next 90° exit (W6, D).

**Core issue**: The "unable" path doesn't replan — it just gives up and coasts. A real pilot would immediately start thinking about the next exit. The relaxation should try the next reachable exit with comfortable braking, not just coast.

**Possible fix**: After "unable", instead of setting `_exitResolutionEnabled = false`, clear the preference (no taxiway, no side) and re-resolve. LandingPhase would then find the next comfortable exit (W5, T) and decelerate for it.

### 4. Far exits cause long coast periods

**Symptom**: EXIT R at SFO (far end) causes the aircraft to brake to coast speed then roll at 40kts for 60+ seconds.

**Root cause**: LandingPhase plans braking but the turn-off speed for a 90° exit is 15kts. The aircraft decelerates to 15kts but `targetSpeed` only goes below coast when `requiredDecel > defaultDecel`. For far exits, the required decel is very low (gentle coast), so targetSpeed stays at coastSpeed (40kts). The aircraft coasts at 40kts for a long time.

**Desired behavior**: For far exits, the pilot should brake softer than default — just enough to reach turn-off speed in time. The aircraft rolls faster than coast, decelerating gently over the full distance. This reduces the boring coast period.

**Possible fix**: Compute the decel rate that exactly reaches turn-off speed at the branch point. If it's below the default rate, use it (softer braking). This spreads the deceleration over the full distance instead of braking hard then coasting.

## Architecture Notes

### Phase handoff flow

```
LandingPhase (rollout)
  → finds candidate exit via FindExitFromCenterline
  → plans braking to reach turn-off speed at branch
  → hands off when speed ≤ targetSpeed

RunwayExitPhase
  → OnStart: clears any ResolvedExit, calls TryFindExitAhead
  → TickRolling: steers along runway heading, searches for exits
  → TryFindExitAhead: FindExitFromCenterline + preference relaxation
  → StartExitNavigation: builds [virtual → branch → ... → holdShort] route
  → GroundNavigator: handles approach, turn anticipation, arrival
```

### Key constants

| Constant | Value | Where | Purpose |
|----------|-------|-------|---------|
| `RolloutDecelRate` | 2.5 kts/s (jet) | `CategoryPerformance` | Default landing decel |
| `ReasonableBrakingRateKtsPerSec` | 5.0 | `LandingPhase` | Max for explicit EXIT commands |
| `ComfortableBrakingMultiplier` | 1.5 | `LandingPhase` | Default exit: 1.5× default rate |
| `RolloutCoastSpeed` | 40 kts (jet) | `CategoryPerformance` | Speed at LandingPhase handoff |
| `HighSpeedExitSpeed` | 30 kts (jet) | `CategoryPerformance` | Turn-off for exits ≤45° |
| `StandardExitSpeed` | 15 kts (jet) | `CategoryPerformance` | Turn-off for exits >45° |
| `VirtualNodeId` | -1 | `RunwayExitPhase` | Sentinel for virtual segments |
| `-0.005nm` | 30ft | `FindExitFromCenterline` | Behind-node tolerance |

### Key files

- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — analog rolling, virtual nodes, TryFindExitAhead
- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` — exit-aware braking, unable handling
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — FindExitFromCenterline, FindAdjacentHoldShort, angle penalty
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — steering, turn anticipation, arrival detection
- `tests/Yaat.Sim.Tests/Simulation/Sfo28rAllExitsTests.cs` — SFO 28R comprehensive exit tests
- `tests/Yaat.Sim.Tests/Simulation/OakAllExitsTests.cs` — OAK 30 + 28R exit tests
- `tests/Yaat.Sim.Tests/Simulation/OakSpeedProfileTests.cs` — speed visualization
