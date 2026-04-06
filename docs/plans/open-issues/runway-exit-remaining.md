# Runway Exit — Remaining Issues

## What We Built

Rewrote `RunwayExitPhase` from a centerline-node-walking state machine to an analog approach:

1. **Analog rolling**: `TickRolling` steers along the runway heading (no node-to-node walking) and continuously searches for exits ahead via `FindExitFromCenterline`
2. **Virtual approach nodes**: When an exit is found, a virtual segment `[aircraft position → branch node]` is prepended to the exit path, giving the `GroundNavigator` the full route `[virtual → branch → ... → hold-short]` with proper turn anticipation at the branch
3. **Exit-aware braking in LandingPhase**: LandingPhase finds the first reachable exit ahead and plans deceleration to reach the turn-off speed by the branch point. Hands off at the target speed while still ahead of the exit
4. **Comfortable vs firm braking**: Explicit exit commands (EXIT T) use firm braking (5kts/s). Default (no preference) uses comfortable braking (1.5× default rollout rate), so the pilot picks a natural exit — not the first one requiring maximum effort
5. **Unable broadcasts**: When an exit requires more than firm braking, the pilot broadcasts "unable" and stops planning for it
6. **Unable-replan**: After "unable", LandingPhase clears the failed taxiway but preserves the side from EL/ER commands. Replans with comfortable braking to find the next natural exit — same behavior as the default case for that side
7. **Inferred exit side**: `InferPreferredExitSide` uses high-speed exit distribution (validated by parking proximity), parallel runway HS inheritance, and parking proximity as a fallback to determine the default exit side for each runway
8. **High-speed exit scoring**: `FindAdjacentHoldShort` gives a 0.15nm bonus to exits ≤45°, so T (19°) beats E (70°) at the same centerline node
9. **Soft side tiebreaker**: Taxiway-only commands (EXIT K) try the inferred side first, falling back to taxiway-only if the exit doesn't exist on that side
10. **Degenerate exit prevention**: Standard exits (>45°) at/past the branch point are rejected, and branch points declared "unable" are excluded from future searches via `_unableBranchPoints`

### Commits

- `bed1454` — Inferred side as soft tiebreaker for taxiway-only commands, TotalSeconds in test output
- `3a4fbce` — Degenerate exit fix (handoff distance check, unable branch-point exclusion)
- `5740e3a` — Unable-replan, inferred exit side, high-speed exit scoring
- `d6e31f3` — Exit-aware braking, comfortable defaults, OAK/SFO E2E exit tests
- `1158c65` — Main rewrite (virtual nodes, analog rolling, LandingPhase simplification)

### Test Coverage

- `Sfo28rAllExitsTests` — All 13 SFO 28R exits + default, B738 at 145kts on 1nm final
- `OakAllExitsTests` — All 7 OAK 30 exits (B738 at 130kts) + all 7 OAK 28R exits (C172 at 70kts)
- `OakSpeedProfileTests` — Tick-by-tick speed/heading/distance visualization (W5, W6, H)
- `IssueSfo28rExitTests` — Original bug recording test

### What Works Well

- All exits tested produce smooth monotonic turns (no heading reversals)
- Default exits go to the correct side: SFO 28R Left, SFO 28L Left (inherited), OAK 30 Right, OAK 28R Right
- Unable exits replan to the same exit as the default case (W2→W5 at OAK, L→T at SFO)
- Taxiway-only commands (EXIT K) prefer the inferred side as a tiebreaker
- High-speed exits are strongly preferred over steep exits at the same centerline node

### LayoutInspector

`--exits` now shows side, high-speed tags, parking proximity per side, reachable parking counts, parallel runway detection, and inferred default side summary.

## Resolved Issues

### ~~1. SFO L instant exit (1s, 4° turn)~~ — FIXED

**Was**: EXIT L produced an instant exit with no turn. LandingPhase braked to 15kts right at L's branch point, then RunwayExitPhase created a near-zero virtual segment.

**Fix**: Standard exits (>45°) at/past the branch point are rejected even if at the correct speed (the virtual segment would be too short for a turn arc). Branch points declared "unable" are excluded from future `FindExitFromCenterline` searches. EXIT L now replans to T (19° high-speed, 43s smooth turn).

### ~~2. SFO default picks E (111° / 140° turn)~~ — FIXED

**Was**: Without an exit command, the B738 on SFO 28R exited at E with a 140° heading change.

**Fix**: Two changes: (1) High-speed exit bonus (0.15nm) in `FindAdjacentHoldShort` scoring ensures T (19°) beats E (70°) at the same centerline node despite shorter path distance. (2) Inferred Side=Left preference constrains default selection to left-side exits where both T and Q are high-speed options.

### ~~3. Relaxed exits go too far down the runway~~ — FIXED

**Was**: EXIT W1/W2/W3 at OAK 30 relaxed to W6 (far), not W5 (closer high-speed). EXIT C/C2/N/P at SFO relaxed to D, not T.

**Fix**: After "unable", LandingPhase clears the failed taxiway preference (keeping side from EL/ER) and replans immediately. `ResolveNextCandidate` fires on the next tick with the relaxed preference and finds the next comfortable exit — same as the default case. W2→W5, C→T, N→T, etc.

## Remaining Issues

### 4. Far exits cause long coast periods

**Symptom**: EXIT W6 at OAK 30 — LandingPhase hands off at t=77 with speed 15kts, but the aircraft is still **0.53nm from W6's branch point**. RunwayExitPhase accelerates to coast speed (40kts) and rolls straight at 40kts for 43+ seconds before reaching W6. Total exit time: 54s vs 8s for W5.

**Root cause**: LandingPhase targets `Math.Max(turnOffSpeed, coastSpeed)` = 40kts for default exits (no explicit preference). The comfortable braking rate can reach 40kts well before the branch point. Once `speed ≤ targetSpeed`, the handoff fires — even though the aircraft is far from the exit.

The aircraft hands off to RunwayExitPhase while still far from the exit. RunwayExitPhase then rolls at coast speed for a long time before reaching the branch. This is the "boring coast period" described in the original plan.

**Tick-by-tick comparison (OAK 30, B738 at 130kts):**

| | W5 (28°, high-speed) | W6 (90°, standard) |
|---|---|---|
| Handoff time | t=68 | t=77 |
| Speed at handoff | 30 kts | 15→17 kts |
| Dist to branch at handoff | 0.003nm (at branch) | 0.528nm (far) |
| Coast period in RunwayExitPhase | 0s | ~43s at 40kts |
| Turn duration | 8s | ~11s |
| Total exit phase time | 8s | 54s |

**Desired behavior**: For far exits, the pilot should brake softer than default — just enough to reach turn-off speed at the branch point. The aircraft rolls faster than coast, decelerating gently over the full distance. This eliminates the long coast period.

**Possible fix**: Compute the decel rate that exactly reaches turn-off speed at the branch point. If it's below the default rate, use it (softer braking). This spreads deceleration over the full distance instead of braking to coast speed early and then coasting.

## Architecture Notes

### Phase handoff flow

```
LandingPhase (rollout)
  → finds candidate exit via FindExitFromCenterline
  → plans braking to reach turn-off speed at branch
  → hands off when speed ≤ targetSpeed AND enough distance for turn
  → after "unable": keeps side, drops taxiway, replans

RunwayExitPhase
  → OnStart: clears any ResolvedExit, infers side if none set
  → TickRolling: steers along runway heading, searches for exits
  → TryFindExitAhead: FindExitFromCenterline + soft side tiebreaker + preference relaxation
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
| `HighSpeedExitBonus` | 0.15nm | `AirportGroundLayout` | Scoring bonus for ≤45° exits |
| `VirtualNodeId` | -1 | `RunwayExitPhase` | Sentinel for virtual segments |
| `-0.005nm` | 30ft | `FindExitFromCenterline` | Behind-node tolerance |
| `0.02nm` | 120ft | `LandingPhase` | Min distance to branch for standard exit handoff |

### Key files

- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — analog rolling, virtual nodes, TryFindExitAhead, inferred side
- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` — exit-aware braking, unable-replan, degenerate exit prevention
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — FindExitFromCenterline, FindAdjacentHoldShort, InferPreferredExitSide, high-speed bonus, angle penalty
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — steering, turn anticipation, arrival detection
- `tests/Yaat.Sim.Tests/Simulation/Sfo28rAllExitsTests.cs` — SFO 28R comprehensive exit tests
- `tests/Yaat.Sim.Tests/Simulation/OakAllExitsTests.cs` — OAK 30 + 28R exit tests
- `tests/Yaat.Sim.Tests/Simulation/OakSpeedProfileTests.cs` — speed visualization (W5, W6, H)
- `tools/Yaat.LayoutInspector/` — `--exits` with side, high-speed, parking, parallel runway analysis
