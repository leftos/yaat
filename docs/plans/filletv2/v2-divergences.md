# Fillet V2 — divergence and triage log

Track every Legacy vs V2 mismatch during parity work. Classify each entry as **accepted improvement**, **V2 bug**, or **legacy-only quirk**.

**V2 algorithm:** [`v2-implementation.md`](./v2-implementation.md)

## Edge-split rewrite — current status (supersedes the pass-6 connectivity sections below)

The connectivity layer is now a single order-independent **global edge-split**
(`FilletEdgeSplitPlanner`): each original edge is split once by the cuts that land on it,
only the stub incident to a removed junction is dropped, every other sub-segment is kept,
and a degenerate corner arc (radius < floor) degrades to a straight chord so connectivity
never depends on a fragile arc. `BuildBezier` control points now project toward the junction
(into the corner) instead of back out along the arm.

`Compare_LegacyVsV2_MeetsHardGates` **passes** on FLL/OAK/SFO. The gate is now a
**no-true-disconnection** gate: structural valid + repair counters zero + parking→hold-short
reachability match + **no node present in both layouts reachable in only one**. Exact
hold-short stable-set equality is reported but not required.

### Accepted classification divergences (not connectivity bugs)

Each generator dissolves (fillets away) a slightly different set of marginal junctions. Every
divergent node is present in exactly one layout — there are zero true disconnections, and
parking reachability is identical (100%). **Class: accepted — V2 clean-room eligibility.**

| Airport | Nodes | Direction | Meaning |
|---------|-------|-----------|---------|
| FLL | 105, 106, 357 | only-legacy (`inLegacy`, not `inV2`) | V2 fillets these (B×B12 short X-crossings; J8 runway crossing); legacy keeps the vertex |
| OAK | 204, 217, 222 | only-v2 (`inV2`, not `inLegacy`) | V2 preserves these; legacy fillets them away |
| SFO | 104, 535 | only-v2 | V2 preserves these; legacy fillets them away |

Decode harness: `FilletReachabilityDiagnosticTests`. The `Fillet:phase-d-*` "gap-next" nodes in
the decode are legacy tangent artifacts — ignore them; the gate compares pre-fillet node ids.

### Runway-bearing parity (Workstream B) — resolved

`CompareRunwayBearings` reports **0 mismatches** on FLL/OAK/SFO. The edge-split preserves each
source edge's `(Nodes[0]→Nodes[1])` orientation on every sub-segment, and `BuildBezier` now
projects control points toward the junction — together these fixed the former FLL
`RWY10L/28R #39↔#644` reversal without a dedicated change.

### Corner-radius (Workstream C) — accepted policy difference

**Class: accepted — different radius policy, not a bug. Soft gate.**

Fixed the one real defect: `BuildBezier` used to size control-point depth for the *requested*
radius (50/75/100/150 ft) while the tangent cuts sat at distances implying a smaller radius,
producing an over-bulged, internally-inconsistent arc whose stored `MinRadiusOfCurvatureFt`
(taxi turn-speed input) was distorted. The executor now builds each arc as a clean circular arc
sized to `min(requested, EffectiveMinRadiusFt(tangent geometry))`. This cut `CompareCornerBuckets`
mismatches FLL 134→88 / OAK 95→85 / SFO 180→160.

The residual is a **bidirectional policy difference**, not pursued: at RAMP junctions legacy goes
tight (e.g. 15 ft) while V2 applies `RampRadiusFt = 50`; at taxiway corners V2's conservative cut
placement (caps that prevent overrunning adjacent intersections) yields tighter arcs than legacy.
Matching legacy exactly would mean cloning its radius computation + relaxing the cut caps that
exist to prevent overrun — which fights the clean-room goal and risks the bug class the rewrite
eliminated. Hard gate (connectivity/structural) is unaffected; revisit only if taxi-physics
testing shows a specific corner's turn speed is wrong.

## Shipped interface (steps 1–2 — do not replan)

Implemented under `src/Yaat.Sim/Data/Airport/`: `IFilletArcGenerator`, `FilletMode`, `FilletGeneratorFactory`, `FilletArcGeneratorRouter`, `FilletArcGeneratorRegistry.All` = `none` + `legacy` until V2 works, `GeoJsonParser.Parse(..., FilletMode)` default `Legacy`. Comparison: `tests/Yaat.Sim.Tests/Helpers/LayoutCloner.cs`, `FilletComparison.cs`.

## Remaining follow-ups (not blocking)

| Item | Notes |
|------|-------|
| LayoutInspector `--fillet` / `--fillet-diff` | Not wired; would let `--fillet=v2` render/diff from the CLI. |
| `LayoutCloner` `GroundRunway` fields | Fillet-scoped clone; extend if reused outside fillet compare. |

> The pass-6 chain-planner approach (`FilletArmChainPlanner`, `FilletConnectivityPlanner`,
> `ArmChainEdgeOp`/`ArmBypassOp`/`ReconnectEdgeOp`, the `NoOwningCut`/`JunctionPromoteToPreserveOp`
> policy notes) was **superseded and removed** by the global edge-split. Its history lives in git.

### Template

```markdown
### [AIRPORT] — short description

- **Class:** accepted improvement | V2 bug | legacy-only quirk
- **Metric:** e.g. arc count, corner bucket, BFS reachability
- **Legacy:** …
- **V2:** …
- **Action:** …
```
