# Issue #172 (split) — SKW3359 wrong-way / A→B fork detour

> **Status:** deferred from #172 (2026-06-03). Repro confirmed; fix is a core-pathfinder change, deferred for dedicated treatment.
> **Source:** SFO recording `issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip` (in TestData). **Labels:** bug, ground-cmds.

## Symptom

`SKW3359: TAXI A B Z S S3 10R` resolved to a long southern detour
(`T5A A A1 A2 M1 B Z S S3` in the recording echo) instead of the direct `A E B …`. The controller
re-issued `TAXI A E B Z S S3 10R` (naming the E connector explicitly), which routed cleanly. The
aircraft "turned the wrong way" following the detour.

## Reproduction

- Bundle: `tests/Yaat.Sim.Tests/TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip`.
- SKW3359 pushed onto T5A spot (node 0); command at sim **t=2325**.
- CLI repro from its start node (node 0):
  ```bash
  dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson \
    --pathfinder 0 A B Z S S3 --pf-dest-rwy 10R
  ```
  Produces a **172-segment** wandering route (walks A ~45 edges south to node 93, then onto A1/A2…).

## Root-cause findings

- A and B **do not directly cross** near SKW3359; their only direct A/B junction nodes are 93, 1264,
  1265 — all far **south**. The sensible route uses the **E connector** (`A E B`), but
  `FindJunctionCandidates(A, B)` only considers nodes where A and B share an edge — it never proposes
  E as a bridge unless E is named.
- In `SegmentExpander.RouteNamedToNamed`, the A→B junction look-ahead probe
  (`ProbeTailCost`) returns `TailUnresolvablePenaltyNm` (1000) for **both** direct junctions
  (93 and 1264) — the tail `B Z S S3 10R` can't be resolved cleanly from either. With every
  candidate penalised equally, selection falls back to cheapest cost-to-reach (node 93), producing
  the garbage 172-segment route.

## Suspected fix area

`src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs` — junction-candidate generation and the
detour/connector-insertion path. The fix likely needs to **propose short connectors** (like E)
between two named taxiways that don't directly cross, and prefer them over walking the source
taxiway to a far direct crossing. This is core junction-scoring/bridging logic — **high risk** of
regressing the broad ground-routing test suite; treat carefully with the full `Pathfinding`/`Taxi`/
`Oak`/`Sfo`/`Skw` regression set.

## TDD target

Direct pathfinder test (no recording needed): `ResolveExplicitPath(node 0, [A,B,Z,S,S3],
destRwy=10R)` on `sfo.geojson` should resolve a route that bridges A→B via the nearby E connector
(not a 45-edge south walk to node 93), with a sane segment count. Compare against the known-good
`TAXI A E B Z S S3 10R` route.
