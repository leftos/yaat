# Nightly Taxi Grid — 10L Failure Cluster

After calibrating the `TaxiBudgetDeriver` to use per-segment turn overhead instead of a flat multiplier, the SFO nightly grid still has **12 failing pairs** — 10 of which target runway **10L**. This document captures what we know and where to look.

## Status

- Grid: **1209/1221 passing** (98.7%)
- Smoke: 25/25 passing (per-PR baseline)
- All remaining failures are **"did NOT arrive in time budget"**; zero turn-budget failures
- All 12 are **SFO jets (B738)**. OAK is now 100% green.

## Three buckets

### Bucket A — Slow-creep parking-exit spin (3 cases, real bugs)

Aircraft barely leaves the parking spot after 87-110s; gs stays ~2 kts; accumulated turn 1632°-1913°. Matches the "endless spin" pattern the suite was designed to catch.

| Pair | Path / Optimal | Turn | Seg | gs |
|---|---|---|---|---|
| `SFO_42-4-to-10L_jet` | 542 / 2037 ft | 1632° | 0/11 | 1.9 |
| `SFO_CG2-to-10L_jet`  | 757 / 2173 ft | 1890° | 5/21 | 1.9 |
| `SFO_CG3-to-10L_jet`  | 837 / 2307 ft | 1913° | 7/22 | 2.7 |

**Common features:**
- B738 (jet) on a ramp parking spot
- Route starts with a `Fillet:phase-d-shorten` RAMP edge, followed by tight fillet arcs (~50ft radius, maxSafe ~10kt)
- 42-4's first arc: node `2718 → 2719`, 50ft radius, 46° sweep
- CG2: heading 346° → first edge bearing 52° (66° turn at exit)
- CG3: heading 346° → first edge bearing 132° (146° turn at exit!)

**Hypotheses (in order of likelihood):**
1. Slow-turn synthesis + tight arc geometry causes the aircraft to creep with maximum turn rate but minimal forward progress
2. Fillet arc tangent computed wrong direction at parking endpoint (the `Fillet arcs have a natural-forward bezier direction` memory drawer)
3. B738 nose-wheel-min clamping interacts with the 50ft arc radius

**Next investigation:** Add `TickRecorder` capture for one pair, visualize with `LayoutInspector --ticks --html`, look for the `[NavV2] Synth geometry tight` warnings.

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
