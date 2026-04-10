# Missing fillet arc at OAK G/D intersection (north of 28R)

## Context

With the FAA AC 150/5300-13B hold-short distance changes (this session), the
exit-side inference for OAK 28R now correctly infers Right (north). Aircraft
exiting 28R on G heading north reach HS#361, get a taxi command like
`TAXI G @SIG1` (route: G → D → RAMP → SIG1), and need to turn left (west)
from G onto D at the G/C/D junction (#1208).

**The problem:** No fillet arc exists from G-northbound into D-westbound at
#1208. The pathfinder routes through a `RAMP · G` arc (#1208 → #1234) whose
bezier tangent starts heading **south** (190°) — the opposite of the aircraft's
heading (0°/north). The navigator computes a 180° turn angle at #1208 and sets
`speed=0`, stalling the aircraft permanently.

In the old layout (pre-FAA-distance changes), the aircraft exited on H (not G),
so this junction was never traversed in this direction. The missing arc existed
before but was harmless; now it's on a critical taxi path.

## Observed topology at #1208

From the interactive HTML inspector (`.tmp/oak-n569-route.html`):

```
Node 1208: TaxiwayIntersection (37.728563, -122.212760)
  Edges (6):
    -> #1207 via G (133ft)           [straight, north toward HS#361]
    -> #1206 via C · G (74ft)        [arc]
    -> #1209 via G · C (115ft)       [arc]
    -> #1233 via D · G (43ft)        [arc — goes south-east into D]
    -> #1237 via D · G (60ft)        [arc — goes south-west into D]
    -> #1234 via RAMP · G (212ft)    [arc — starts heading SOUTH then loops to ramp]
```

There are two `D · G` arcs (#1233 and #1237) — both curve **southward** from
#1208 into D. These serve aircraft arriving from the south on G and turning
onto D. There is **no arc** for aircraft arriving from the **north** on G
turning left onto D. The expected arc would curve from G-northbound
(tangent ~0°) into D-westbound (tangent ~210°).

## Route that stalls

N569SX route after `TAXI G @SIG1`:

```
361(HS) → 1208 → 1234 → 1237 → 1300 → 1301 → 1245 → 1248 → 1305 → 1302 → 638(SIG1)
```

Segment 0: #361 → #1208 via G straight, heading ~11° (north)
Segment 1: #1208 → #1234 via `RAMP · G` arc, tangent starts at ~191° (south)

Navigator sees: inbound=10.7°, outbound=190.5°, turn angle=179.8° → speed=0.

## Relationship to other fillet-arc issues

This is the same class of bug as:

- **`fillet-arcs-sfo-at6-t6b.md`**: Missing fillet arcs at SFO A/T6 and A/T6B
  T-intersections. Hypothesis H4 (pair never generated because an earlier fillet
  consumed the straight edge) is the leading candidate there too.

- **`sfo-b10-taxi-stall.md`**: SFO taxi stall on an arc segment at node 1235
  where all 4 edges are arcs. The stall mechanism (navigator speed=0 due to
  arc geometry) matches what we see here at OAK #1208.

- **`fillet-arc-taxi-misbehavior-wja1508.md`**: WJA1508 28R exit overshoot,
  same bundle as the B10 stall. Also suspected fillet-arc regression.

All four issues share the pattern: **fillet generation at a multi-edge
intersection fails to produce arcs for all valid turn pairs, forcing the
pathfinder to route through arcs whose tangent directions are wrong for the
aircraft's actual heading, causing the navigator to compute ~180° turns and
stall.**

## What we've confirmed

- The OAK G/D/C junction (#1208) has 6 edges but no arc from G-north into
  D-west. The `FilletArcGenerator` should have produced one.
- The `RAMP · G` arc's bezier starts heading south (190°) — correct for
  G-southbound traffic entering the ramp, wrong for G-northbound traffic.
- The navigator correctly refuses to execute a 180° turn at taxi speed.
- The exit-side inference fix (parking proximity > parallel HS) is correct
  and should be kept; the fillet arc is the missing piece.

## Suggested investigation

Same approach as the SFO T6/T6B plan:

1. **Dump #1208 topology with and without fillets** (`--no-fillets` flag).
   Before fillets, #1208 should be a 3-or-4-way intersection with straight
   edges for G, C, D, and RAMP. After fillets, count arcs per pair and
   identify which pair is missing.

2. **Add targeted logging in `FilletNode`** for the OAK #1208 node ID.
   Track which edge pairs are generated, which are classified as collinear
   merges vs arc pairs, and which are dropped.

3. **Check processing order.** If G/C is filleted first and consumes the
   G-north straight edge, then when G/D is processed, the G-north edge no
   longer exists as a `GroundEdge` (it's now an arc endpoint) and the pair
   is silently dropped (H4 from the SFO plan).

## Skipped tests to re-enable after fix

These tests are skipped with `Skip = "Blocked by missing fillet arc at OAK G/D
junction"` and must be re-enabled (remove the Skip) once the fillet arc is fixed:

- `OakGroundE2ETests.OAK_FullGroundSequence_NoOverlapAndSIG1Reached` — N569SX
  stalls at #1208 because the G→D route takes the wrong arc (190° tangent).
- `OakCross28RHoldShortTests.RerouteFrom28R_ExitSideHoldShort_NotAddedAsCrossing`
  — downstream of the stall; route through D/G picks up a spurious 28R crossing
  hold-short because the pathfinder detours through the ramp.

## Related defensive fix needed

The GroundNavigator computes `speed=0` for 180° turns, permanently stalling the
aircraft. Even when fillet arcs are missing, aircraft should never stall to 0 on
a taxi route — they should keep moving at a minimum speed (1 kt) so the sim
doesn't deadlock. This is a separate fix in `GroundNavigator.cs` (floor
`_currentNodeRequiredSpeed` to a minimum taxi crawl speed).

## Non-goals

- Not fixing the fillet algorithm in this plan — this documents the OAK
  instance of the systemic fillet issue for coordinated investigation.
- The exit-side inference fix and FAA hold-short distances are correct and
  stay as-is.
- The two remaining test failures (`OAK_FullGroundSequence`,
  `OakCross28RHoldShortTests`) are both downstream of this missing arc.
  Once the arc exists, the route won't stall and won't traverse unexpected
  crossing hold-shorts.

## Critical files

Same as `fillet-arcs-sfo-at6-t6b.md`:

- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` — fillet generation
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — graph model
- `tests/Yaat.Sim.Tests/TestData/oak.geojson` — test data
- `tools/Yaat.LayoutInspector/` — topology inspection + interactive HTML
