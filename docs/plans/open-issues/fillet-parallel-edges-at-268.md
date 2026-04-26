# SFO @268: parallel E-NE edges produced 6 fillet arcs instead of 4

**Status:** RESOLVED via post-fillet `RemoveDuplicateCornerArcs` cleanup pass.
`FilletDiagnosticTests.SFO_FilletArcs_Node268_HasExactlyFourArcs` passes.
This document is kept for context — the underlying build-time redundant-edge
issue is still present, just papered over by the cleanup. A real fix would
also need to remove the redundant TANGENT NODES (1745, 1746, 1752, 1240's
extra connections, etc.), which still appear in the navigator's view and
contribute to the SKW3078 spin via dead-ends in `WalkTaxiway`.

## Symptom (pre-fix)

The SFO E/F 4-way crossing at node #268 emitted six `phase-c-arc@268` fillet
arcs instead of the expected four (one per corner). Two of the extras were
the 9 ft / 13 ft tight arcs that contribute to the SKW3078 spin captured by
`Skw3078FixComparisonCapture`.

## Root cause

When `FilletArcGenerator.PhaseD1_ShortenEdges` "standard walk" branch places a
tangent that walks past an intermediate intersection onto an existing edge, it
does *not* consume that landed-on edge. Instead it adds a parallel
`ptNode↔farNode` passthrough edge alongside the original. By the time a later
intersection processes its own fillet, it sees both the original edge and the
upstream-injected duplicates as separate inputs to the pair iterator.

Concretely at @268:

| Edge                | Length | Origin                                    |
|---------------------|--------|-------------------------------------------|
| `E(268↔1240)`       | 54 ft  | `Fillet:phase-d-passthrough@57`           |
| `E(268↔141)`        | 216 ft | `Fillet:phase-d-passthrough@57` (was original `TaxiwayGraphBuilder:edge`, but the original was consumed by @141; the passthrough survives) |
| `E(268↔1483)`       | 135 ft | `Fillet:phase-d-shorten@141`              |

All three go NE on E and represent the same physical centerline split by upstream
tangent nodes. @268 sees them as three separate "E-NE" inputs and pairs each
against the two F-side edges → 3 × 2 = 6 arcs (vs the expected 1 × 2 = 2).

## Failed fix attempts

1. **Consume the landed-on edge in `InterpolateAlongWalk` + remove the
   `ptNode↔farNode` add (lines 672–681).** Broke 7 tests including
   `OAK_TaxiD_ToNEW1_FromG_UsesNorthwardArc`,
   `ResolveExplicitPath_SfoM2_UsesSameTaxiwayArcAtA1Apex`,
   `SFO_FilletArcs_TTerminalStubs_EachHaveTwoNonDegenerateArcs`. The
   `ptNode↔farNode` edge is the *only* connectivity in some cases (single-edge
   walks where the original was already consumed); removing it leaves orphans.

2. **`RemoveDuplicateEdges` per-intersection.** No effect — the parallel edges
   go to *different* endpoints (`268↔1240`, `268↔1483`, `268↔141`), so the
   `(min, max, twy)` dedup key doesn't match.

3. **Collapse same-taxiway+bearing edges to the shortest in `FilletNode`.**
   Broke `OAK_TaxiD_ToNEW1_FromG_UsesNorthwardArc`,
   `OAK_TaxiC_ToJSX1_FromG_UsesDirectArc`,
   `ResolveExplicitPath_SfoM2_UsesSameTaxiwayArcAtA1Apex`, and a few more
   because some same-taxiway+similar-bearing edges are genuine distinct paths
   at Y-junctions and ramp branches, not bypass duplicates.

4. **Consume-as-walked guarded by `passthrough.Count > 0`** (the variant a
   subagent recommended after fresh review). Idea: in `InterpolateAlongWalk`,
   when the target lands mid-edge AND at least one passthrough intersection
   was crossed, also `consumed.Add(step.Edge)`. Keeps the line-672
   `ptNode↔farNode` re-add intact, so single-passthrough cases (OAK, SFO M2)
   stay connected via the explicit re-add. **Result: same 7 OAK/SFO failures
   as attempt 1, AND @268 still emits 6 arcs.** Two reasons it failed:
   - At @268 the line-672 re-add immediately re-creates `268↔141` as a fresh
     edge object. `RemoveDuplicateEdges` collapses the two `268↔141` later, but
     by then `@141`'s separate `phase-d-shorten` (the `268↔1483` edge) has
     already been added through a different code path that doesn't hit
     `InterpolateAlongWalk`'s consume at all. So even with attempt 4 we go
     from 3 → 2 parallel E-NE edges, not 3 → 1, and the pair iterator still
     emits 6 arcs.
   - The OAK regressions surface even with the guard, suggesting some OAK
     walks DO have non-empty passthrough lists where the consume strands
     downstream nodes despite the explicit re-add. Need to instrument
     `InterpolateAlongWalk` per-walk to confirm which OAK pair triggers.

5. **Origin-aware filter in `FilletNode`** (group same-taxiway+bearing edges,
   drop those with `Fillet:phase-d-passthrough@<other>` or
   `Fillet:phase-d-shorten@<other>` origin in favor of non-bypass survivors).
   @268 dropped to 4 arcs cleanly. **But the same OAK G/D and SFO M2/A1
   pathfinder tests still failed** — at OAK G/D apex the pathfinder relies
   on a `phase-d-shorten@<other>` edge that *isn't* a bypass artifact, it's
   a legitimate post-fillet connection. The provenance signal isn't reliable
   enough to discriminate.

## Resolution: post-fillet duplicate-corner-arc cleanup

Stopped fighting the build logic and added a cleanup pass after all fillets
finish: `RemoveDuplicateCornerArcs` in `FilletArcGenerator`. Groups arcs by
`(intersectionId, normalized-taxiway-pair, sorted approach-bearings rounded
to 5°)`, which uniquely identifies a physical corner regardless of how many
times the pair iterator emitted an arc for it. When two or more arcs share a
corner key, keep the one with the largest `MinRadiusOfCurvatureFt` and drop
the rest — the largest-radius arc is the one whose tangent placement had the
most edge available, i.e. the geometry the fillet generator would have
chosen if no bypass edges existed.

@268 drops from 6 to 4 arcs, all 3313 tests pass.

## Limitations of the resolution

The cleanup only removes ARCS, not the underlying tangent nodes and edges
that those arcs were attached to. So node 1746 (the tangent E-NE node
@268's pair iterator created and that got merged into 1240), the F-side
tangent nodes 1745, 1752, etc., all still exist in the graph. The
GroundNavigator and TaxiPathfinder still see them and walk through them.
SKW3078's E→A taxi route still hops onto F (segments `1753→1752→1755`)
because `WalkTaxiway` dead-ends at 1753 with no E continuation. That F-hop
is what triggers the spin captured by `Skw3078FixComparisonCapture` — the
arc-count fix doesn't address it. A more complete fix would also remove
those redundant tangent nodes during `MergeCoincidentNodes`, but that's a
larger change and a separate concern from the arc-count bug.

## Direction for a real fix

The bypass edges are distinguishable from genuine multi-branch edges by their
**provenance** — they carry `Fillet:phase-d-passthrough@<X>` origin tags where
X is *not* the intersection currently being filleted. Two candidate approaches:

- **Origin-aware filter** in `FilletNode`: when the intersection has multiple
  same-taxiway edges leaving in nearly the same direction, drop edges whose
  `Origin` starts with `Fillet:phase-d-passthrough@` and references an
  intersection ID different from the current one (i.e., bypass leftovers from a
  neighbor's processing). Keep the shortest survivor.

- **Consume-as-walked**: in `PhaseD1_ShortenEdges` standard branch, when the
  walk lands on an edge mid-way, mark that edge for removal (add to
  `ConsumedEdges`) — *and* keep the `ptNode↔farNode` passthrough add as the
  replacement. This way the original is gone and only the new chain remains.
  The earlier attempt (1) failed because it removed both the original and the
  add; this variant keeps the add.

Both need a regression sweep against:
- `OAK_TaxiD_ToNEW1_From*` (NEW1 routing through G/D apex)
- `OAK_TaxiC_ToJSX1_From*` (JSX1 routing)
- `OAK_FullGroundSequence_NoOverlapAndSIG1Reached` (E2E)
- `ResolveExplicitPath_SfoM2_UsesSameTaxiwayArcAtA1Apex` (SFO M2/A1)
- `SFO_FilletArcs_TTerminalStubs_EachHaveTwoNonDegenerateArcs` (T6/T6A/T6B)

## Downstream effect this fix unblocks

Issue 2 (SKW3078 spinning at @268 t=825..963 in
`Skw3078FixComparisonCapture` `-after.json`) is the visible cost of this bug.
The route built by the pathfinder hops onto F via `1753→1752→1755→1750`
because `1753` is a dead-end on E for `WalkTaxiway`, and the bridge pulled in
F edges that exist *because* the tight arcs and extra tangent nodes are there.
Cutting the arc count to 4 should also collapse the F-side tangent cluster to
two nodes, making `1753` a normal pass-through on E and removing the F detour
from the route.
