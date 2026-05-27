# Nightly Taxi Grid — 10L Failure Cluster

After calibrating the `TaxiBudgetDeriver` to use per-segment turn overhead instead of a flat multiplier, the SFO nightly grid still has **12 failing pairs** — 10 of which target runway **10L**. This document captures what we know and where to look.

## Status

- Grid: **1210/1221 passing** (99.1%)
- Smoke: 25/25 passing (per-PR baseline)
- All remaining failures are **"did NOT arrive in time budget"**; zero turn-budget failures
- All 11 are **SFO jets (B738)**. OAK is now 100% green.

## Three buckets

### Bucket A — Slow-creep parking-exit spin (root cause identified, 1 of 3 fixed)

**Root cause:** `AirportGroundLayout.FindNearestNodeForTaxi` unconditionally skipped Parking/Helipad nodes when resolving the TAXI startNode. For aircraft parked at SFO 42-4 (node 1047), this forced startNode to fillet vertex 801 (~92ft away) — putting the route's first segment off-line from the aircraft's actual position. Combined with a 77° entry-alignment slow-turn and the short 10L route's low downstream speed cap, the aircraft couldn't pure-pursuit-converge onto segment 0 and orbited node 2719 indefinitely (1632° accumulated turn at ~2 kts).

**Fix landed (commit pending):** `FindNearestNodeForTaxi` now considers parking nodes when the aircraft is essentially at one (≤15ft). It prefers a co-located non-parking neighbor (e.g. the phase-d-shorten endpoint at near-zero distance) when one exists, otherwise returns the parking node itself so the route's first segment IS the parking-exit RAMP edge.

| Pair | Status | Notes |
|---|---|---|
| `SFO_42-4-to-10L_jet` | **FIXED** | No co-located neighbor → uses parking node 1047 directly; route correctly starts at the parking-exit RAMP. |
| `SFO_CG2-to-10L_jet`  | partial — still fails | Now uses co-located 2726 (2 ft away). Aircraft now reaches seg 8/20 at gs=30 instead of slow-creeping at seg 5/21. Failure mode shifted from spin to wander. |
| `SFO_CG3-to-10L_jet`  | partial — still fails | Now uses co-located 2730 (6 ft away). Aircraft still slow-creeps at gs=2.7. The pathfinder's natural route from 1051 went via 2733 (84ft) skipping 2730 entirely — forcing startNode to 2730 may route the aircraft through a different (sub-optimal) path. |

**CG2/CG3 outstanding:** the co-located neighbor strategy that fixed 42-4 doesn't cleanly fix CG2/CG3 — the natural A* route from the parking node skips the co-located fillet endpoint entirely, but using the co-located endpoint as startNode forces routing through it. Possible next steps:
1. Use the parking node directly even when a co-located neighbor exists (let the pathfinder include or skip the parking-exit edge as it sees fit).
2. Investigate why the pathfinder picks the longer 84ft edge (1051→2733) over the shorter 6ft edge (1051→2730) — A* should prefer the shorter combined cost.
3. Look at whether the slow-turn-synthesis lookahead is over-engaging on the first arc after parking exit.

### Bucket B — Active wandering, 10L destinations (5 cases)

Aircraft moves at 25-30 kts but accumulates 4-15× the optimal turn, exhausts time budget before arrival. The route choice itself looks suspect (path much longer than A* optimal).

| Pair | Path / Optimal | Turn / Optimal | Seg |
|---|---|---|---|
| `SFO_E10U-to-10L_jet` | 5493 / 6125 ft  | 2469 / 470 (5.3×) | 43/50 |
| `SFO_E2-to-10L_jet`   | 5737 / 6210 ft  | 2033 / 368 (5.5×) | 42/47 |
| `SFO_F21-to-10L_jet`  | 6291 / 6232 ft  | 2221 / 568 (3.9×) | 58/61 |
| `SFO_F20-to-10L_jet`  | 6018 / 6234 ft  | 2265 / 543 (4.2×) | 54/60 |
| `SFO_G9-to-10L_jet`   | 6721 / 7062 ft  | 2527 / 659 (3.8×) | 75/82 |

All five originate from north-side parking (E10U, E2, F20, F21, G9) targeting 10L (south runway). All are ~90% along their route when time runs out, suggesting the navigator's actual route is significantly longer than the deriver's A* — either it picks a different hold-short or wanders en route.

### Bucket C — Active wandering, non-10L (2 cases — D11)

| Pair | Path / Optimal | Turn / Optimal | gs |
|---|---|---|---|
| `SFO_D11-to-28R_jet` | 5475 / 8267 ft | 3416 / 230 (15×!) | 28.2 |
| `SFO_D11-to-28L_jet` | 5129 / 8017 ft | 3398 / 230 (15×!) | 30.0 |

D11 is the only non-10L destination still failing. The 15× turn ratio at full taxi speed is extreme — aircraft is genuinely wandering. Only 66% progress in the full time budget. D11 has only 1 edge from parking; the next edge may have a problematic fillet.

### Bucket D — Nearly-there (2 cases)

Aircraft is at seg N-1 of N with gs <23 when time runs out. May be a small budget under-allocation, may be navigator dawdling on the last segment.

| Pair | Path / Optimal | Seg | gs |
|---|---|---|---|
| `SFO_E10U-to-28R_jet` | 10477 / 9906 ft | 84/85 | 6.7 |
| `SFO_E10U-to-28L_jet` | 10154 / 9655 ft | 83/84 | 22.6 |

## Calibration that landed

`tests/Yaat.Sim.Tests/Helpers/TaxiBudgetDeriver.cs`:
- **Turn budget:** `optimalTurnDeg × multiplier + slack` → `optimalTurnDeg + segCount × 30° + 60°`. Empirically, navigator pure-pursuit adds 15-25°/segment on healthy routes; per-segment overhead scales correctly with route length.
- **Time budget:** Added `StartupOverheadSec = 15.0` to cover parking-exit acceleration from gs=0.

Result: 36 → 12 failures, zero turn-budget false positives.

## What's next

1. **Pick one Bucket A case** (suggest `SFO_42-4-to-10L_jet` — simplest geometry). Add tick capture, visualize, identify root cause.
2. **Check the navigator's chosen route for one Bucket B case** vs the deriver's A*. If they differ, the deriver's "optimal" doesn't match what the sim actually picks.
3. **Investigate D11's first-edge geometry** — single-edge parking spot with extreme wandering on both runways.
4. **Don't touch Bucket D yet** — these may resolve themselves once Bucket B is understood (both are E10U).

## Re-run locally

```pwsh
# Per-PR smoke (~3s)
dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj `
  --filter "FullyQualifiedName~TaxiCoverage&Category!=Nightly" 2>&1 | tee .tmp/test-smoke.log

# Nightly grid (~10s)
dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj `
  --filter "Category=Nightly" 2>&1 | tee .tmp/test-grid.log

# One pair with diagnostics
dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj `
  --filter "FullyQualifiedName~TaxiCoverageSfoGridTests&Category=Nightly" `
  --logger "console;verbosity=detailed" 2>&1 | tee .tmp/test-one.log
```
