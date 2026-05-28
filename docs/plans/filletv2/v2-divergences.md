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

## Step 2 harness gaps (before step 4 parity declaration)

Must land in `FilletComparison` (or dedicated validators) before declaring V2 ready — not blockers for step 3 implementation.

| Gap | Status | Notes |
|-----|--------|-------|
| Corner-bucket min-radius diff | **Open** | Consensus: `(region, taxiway-pair, bearings ~5°)` → min radius within ±10% Legacy vs V2 |
| Runway centerline bearing diff | **Open** | Consensus: ±1° per runway centerline segment |
| Structural validity checks | **Open** | No missing node refs, self-loops, or zero-length edges (all generators) |
| Connectivity gate wording | **Open** | Harness uses hold-short–seeded BFS reachability set equality; consensus also mentions parking → hold-short BFS — pick one (or both) at step 4 |
| `LayoutCloner` `GroundRunway` fields | **Open** | Clone copies `Name`, `Coordinates`, `WidthFt` only; omits `TurnoffByEnd`, `PatternAltitudeAglFt`, `PatternSizeNm`, `NoTurnoffByEnd`. OK for fillet compare; document as fillet-scoped or extend clone if reused elsewhere |

## Parity runs

V2 is **runnable** (step 3e). OAK/SFO/FLL comparison runs are step **3f** — not started yet.

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
