# Fillet V2 — active status

**Owner:** Claude (direct implementation; the `claude-response.md` ↔ `cursor-progress.md` handoff loop has ended — those two files are historical).
**Spec + learnings:** [`v2-implementation.md`](./v2-implementation.md) ("Connectivity rewrite" section).
**Gate:** `Compare_LegacyVsV2_MeetsHardGates` (FLL/OAK/SFO) in `tests/Yaat.Sim.Tests/Fillet/`.

## Strategy

1. **Reach structural-green first**, commit a checkpoint (working V2 + comparison baseline as a safety net).
2. **Then** do the connectivity-layer rewrite (global edge-split, order-independent) against that baseline.

Keep: geometry layer (`TaxiwayArmBuilder`, `JunctionClassifier`, `CornerPlanner`, `ArmCutResolver`, `FilletGeometry`, `TaxiwayWalk`), gate harness, diagnostics.
Rewrite later: `FilletPlanExecutor` edge construction + `FilletArmChainPlanner` + reconnect/bypass/side-branch passes → single order-independent edge-split.

## Done so far (uncommitted)

- Reverted dead `IsNamedTaxiwayIntersection` node-retention in `FilletPlanBuilder` (no behavior change; removed complexity).
- Removed the `MaxPreserveToCutSpanFt = 5 ft` cap in `FilletPlanExecutor` preserve-to-cut → preserved junctions now connect to their nearest tangent per arm. Fixed all 7 isolated-node structural failures (FLL 301, OAK 28/155/305/387, SFO 538/823).
- Added degenerate/self-loop **edge** removal to `FilletGraphNormalizer` (HEAD version only removed degenerate *arcs*) → targets SFO `887→887` self-loop.

## Gate progression

| Run | FLL | OAK | SFO |
|-----|-----|-----|-----|
| post-recovery (broken) | FAIL / 6 / 0 | FAIL / 5 / 3 | FAIL / 9 / 2 |
| after cap removal | **ok** / 5 / 0 | **ok** / 1 / 3 | FAIL(self-loop) / 4 / 2 |
| + self-loop edge removal + adjacency rebuild + isolated-node sweep | **ok** / 5 / 0 | **ok** / **10** / 3 | **ok** / **25** / 2 |

(format: structural / only-legacy / only-v2)

**Structural-green achieved on all three.** Remaining gate failures are reachability only.

### Stale-adjacency finding (important)

The HEAD `FilletGraphNormalizer` removed degenerate *arcs* without rebuilding adjacency, so the gate ran on **stale adjacency that masked real disconnections** and inflated reachability. The fix (rebuild after removals + isolated-node sweep) reveals the TRUE only-legacy: OAK 1→10, SFO 4→25 (FLL unchanged at 5). Every prior round's only-legacy number was understated. The corrected numbers are the real target for the rewrite.

## Next

1. Confirm self-loop fix → structural-green on all three. Commit checkpoint.
2. only-legacy decode: FLL 105/106/357/501/468, OAK 600, SFO 661/660/1101/1043.
3. only-v2 decode: OAK 3, SFO 2.
4. Then the global edge-split rewrite.

## Known non-structural divergences (defer)

- Corner-radius mismatches at RAMP junctions (J119 FLL, J473 OAK, J796 SFO): V2 radii 6–22 ft vs legacy 40–50 ft. Radius-policy difference, triage later.
- FLL runway bearing `RWY10L/28R #39↔#644`: v2 270° vs legacy 90° (reversed edge). Investigate.
