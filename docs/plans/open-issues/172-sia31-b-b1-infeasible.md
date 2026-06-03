# Issue #172 (split) — SIA31 "transition infeasible B→B1" / JBU2435 "S3 unreachable"

> **Status:** deferred from #172 (2026-06-03). Needs LayoutInspector topology analysis to decide code-vs-data before any change.
> **Source:** SFO recording `issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip` (in TestData). **Labels:** bug, ground-cmds.

## Symptom

- `SIA31: TAXI B B1 Z S S3 10R` rejected: **"No valid path from B to B1 — transition infeasible from
  node 117."** The controller's workaround `TAXI A Q B1 Z S S3 10R` (approaching B1 from A/Q instead
  of B) routed fine.
- `JBU2435: TAXI M2 B Z S3 10R` rejected **"Cannot taxi via S3 … unreachable"**; `TAXI M2 A E B Z S
  S3 10R` (inserting `S` before `S3`) worked. May share root with the deferred terminus/fork issues.

## Reproduction

- Bundle in TestData. SIA31 command at the moment it was on taxiway B (client log:
  `SIA31: No valid path from B to B1 — transition infeasible from node 117`).
- The error string is emitted by `SegmentExpander.TryDetour` when `FindJunctionCandidates(B, B1)`
  yields no candidates and the bounded detour AutoRouter (cap `MaxDetourExpansions = 5000`) fails
  from `head.HeadNodeId`.

## Open question — code vs SFO ground-graph data

The seed plan flagged this: confirm with LayoutInspector whether a **≤ max-heading-change** (135° for
Jet) admissible B→B1 path actually exists at node 117 before relaxing any gate.

```bash
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson \
  --node 117 --node-depth 1
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --taxiway B
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --taxiway B1
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --intersection B B1
```

- If B and B1 share no node with edges on both (no real junction in the graph) → likely a **data**
  issue (B↔B1 geometry / missing connectivity), not a code bug.
- If a junction exists but the arrival bearing at node 117 makes every B→B1 departure exceed the
  135° admissibility limit → it's the `GeometricAdmissibility` gate
  (`src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs`), and relaxing it risks
  fabricating un-taxiable hairpins elsewhere. The detour inherits the prior `head.LastEdge`, so the
  first detour edge is heading-gated against the B arrival bearing — a likely culprit.

## Suspected fix area

`SegmentExpander.TryDetour` / `GeometricAdmissibility` — but **only after** confirming a feasible
path exists. If it's data, fix the SFO ground-graph source instead. Decide code-vs-data first.

## TDD target

Once root cause is known: a direct `ResolveExplicitPath` test for `B B1 Z S S3` (SIA31's node) and
`M2 B Z S3` (JBU2435) on `sfo.geojson` that currently fails with the reported errors and passes
after the fix; re-check the JBU2435 case after the deferred terminus/fork fixes land — it may close.
