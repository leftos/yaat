# Fillet V2 — divergence and triage log

Track every Legacy vs V2 mismatch during parity work. Classify each entry as **accepted improvement**, **V2 bug**, or **legacy-only quirk**.

**V2 algorithm:** [`v2-implementation.md`](./v2-implementation.md)

## Shipped interface (steps 1–2 — do not replan)

Implemented under `src/Yaat.Sim/Data/Airport/`: `IFilletArcGenerator`, `FilletMode`, `FilletGeneratorFactory`, `FilletArcGeneratorRouter`, `FilletArcGeneratorRegistry.All` = `none` + `legacy` until V2 works, `GeoJsonParser.Parse(..., FilletMode)` default `Legacy`. Comparison: `tests/Yaat.Sim.Tests/Helpers/LayoutCloner.cs`, `FilletComparison.cs`.

## Step 1 follow-ups (addressed or scheduled)

| Item | Status | Notes |
|------|--------|-------|
| `FilletStatistics.cs` in `Fillet/` with `Yaat.Sim.Data.Airport` namespace | **Fixed** | Moved to `src/Yaat.Sim/Data/Airport/FilletStatistics.cs` |
| `FilletArcGeneratorV2` in `Registry.All` while `Apply` throws | **Fixed** | V2 omitted from `All` until step 3; factory still exposes `FilletMode.V2` |
| V2 `NotImplementedException` message | **Fixed** | Single clear sentence |
| LayoutInspector `--fillet` / `--fillet-diff` | **Open** | Step 4 |

## Step 2 harness gaps (step 3f)

| Gap | Status | Notes |
|-----|--------|-------|
| Corner-bucket min-radius diff | **Fixed (soft)** | `FilletComparisonGates` ±10% bucket compare; triage in airport sections below |
| Runway centerline bearing diff | **Fixed (soft)** | Indexed; not hard-fail in `Compare_LegacyVsV2_MeetsHardGates` |
| Structural validity checks | **Fixed** | `ValidateStructural` on all generators |
| Connectivity gates | **Fixed (metric)** | Stable pre-fillet node IDs reachable from hold shorts + parking→hold-short set equality |
| `LayoutCloner` `GroundRunway` fields | **Open** | Fillet-scoped clone; extend if reused outside fillet compare |

## Parity runs (3f)

V2 is **runnable** (step 3e). Pass **6** adds plan ops (`ArmBypassOp`, `StraightConnectorOp`, `ReconnectEdgeOp`) — see [`v2-pass-6-connectivity-ops.md`](./v2-pass-6-connectivity-ops.md).

`Compare_LegacyVsV2_MeetsHardGates` **fails** OAK/SFO/FLL as expected until the planner contract is complete. Do not patch execute/normalizer to green the test.

| Airport | V2 structural | Hold-short stable match | Parking match | Notes |
|---------|---------------|-------------------------|---------------|-------|
| FLL | FAIL (missing node refs on some edges) | 21 only-legacy | OK | After pass-6 execute; investigate **post-merge edge refs** or missing `TangentChainEdgeOp` |
| OAK | FAIL (degenerate + missing refs) | 92 only-legacy | FAIL | Merged-cut self-edges reduced; gaps remain |
| SFO | FAIL (degenerate self-edges) | 113 only-legacy | FAIL | Same class as OAK |

**Next op candidates (name before patching):** `TangentChainEdgeOp` for arc-only tangents after merge; replan after `TangentMergeOp` so straight connectors use survivor cut ids only.

### Expected comparison deltas (not bugs)

| Topic | Legacy | V2 | Triage |
|-------|--------|-----|--------|
| Plan-time radius gate | Cubic `MinRadiusOfCurvatureFt` from tangent positions | `EffectiveMinRadiusFt` = `min(ta,tb)/tan(turn/2)` (geometric) | **Accepted** for gating; execution still uses `BuildBezier` for `GroundArc.MinRadiusOfCurvatureFt`. Symmetric cuts match; asymmetric junctions may show corner-bucket deltas under the ±10% harness — interpret as plan-vs-bezier divergence, not connectivity failure. |

### Template

```markdown
### [AIRPORT] — short description

- **Class:** accepted improvement | V2 bug | legacy-only quirk
- **Metric:** e.g. arc count, corner bucket, BFS reachability
- **Legacy:** …
- **V2:** …
- **Action:** …
```
