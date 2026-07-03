# Fillet Arc Generator — S-turn / short-connector blending (follow-up to #236)

## Context

Issue #236 (SFO "A F1 B" weirdness) shipped a **navigator-layer speed fix**: when an aircraft
taxis a short cross taxiway between two parallel taxiways (SFO `A → F1 → B` — A and B parallel
~236 ft apart, F1 the ~perpendicular connector), the navigator now holds a steady low speed
across the connector instead of accelerating on the straight and braking hard for the second
turn. See `DetectShortConnector` in `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` and
`docs/ground/navigator.md` ("Short-connector transit").

That fix changed **speed only** — the aircraft still tracks the connector centerline and makes
two ~90° turns, and the rendered/pinned taxi route line still shows the arc→straight→arc kink.
The user accepted that for #236 but asked to pursue the **graph-layer** fix separately.

**The motivating observation:** SFO's *actual painted taxiway centerlines* around the A/F1/B
intersection complex are designed as smooth **S-curves**, not square corners (confirmed from
satellite imagery — the yellow centerlines curve continuously through the connector). YAAT's
fillet generator builds square-ish corners (two independent per-junction arcs + a straight
between) from the straight-segment GeoJSON, so it does not capture the real curved design. The
graph is "correct-but-different" per the source data, but here the source arguably *is* curved and
the generator is losing it.

## Goal

Detect a **short connector between two ~parallel taxiways** in the Fillet Arc Generator and emit a
single blended S-curve (or two arcs that meet with no aligned straight), so that BOTH the rendered
route line AND the flown path match the real pavement design. This is the higher-risk
"ground-graph blend" option deferred from #236.

## Why it's the harder/riskier layer

- The ground graph is **replay-critical**: routes are snapshotted at TAXI-issue time and must
  re-resolve identically. Changing junction geometry changes existing recordings' routes.
- The fillet generator is guarded by invariants that must stay green:
  `tests/Yaat.Sim.Tests/Fillet/FilletCornerSpanGuardTests.cs` (no corner span > 300 ft, no
  zero-distance edges, no duplicate node pairs, no coincident nodes) and
  `GroundArcBezierPlaybackGuardTests` (every arc ends within 2 ft of its node).
- The blend must stay **on paved surface** — drive it off the actual fillet/pavement envelope, not
  an abstract radius (aviation-reviewed for #236: keep the main gear on pavement, floor the blend
  radius at the taxi turn radius, never tighter than the individual corner arcs).

## Where it plugs in (from the #236 code-surface map)

- `src/Yaat.Sim/Data/Airport/Fillet/CornerPlanner.cs` — pairs arms **within one junction**; no
  cross-junction awareness today.
- `src/Yaat.Sim/Data/Airport/Fillet/SharedArmTangentPass.cs` — `ApplyCrossJunction` is the *only*
  cross-junction pass, and it only *scales cut positions down* to avoid collisions; it does not
  merge two opposite-direction corners into one curve. This (or a new sibling pass in
  `FilletPlanBuilder.Build`) is the natural insertion point.
- `src/Yaat.Sim/Data/Airport/Fillet/FilletEdgeSplitPlanner.cs` — produces the surviving straight
  sub-segment between the two junctions' cuts (the piece to replace/blend).
- `src/Yaat.Sim/Data/Airport/Fillet/FilletPlanExecutor.cs` — emits one `GroundArc`/`GroundEdge` per
  op; a merged S-curve op would materialize here.

Read `docs/ground/fillet-generator.md` first — especially the "correct-but-different, adapt the
consumer, don't fix the graph" principle, which cuts the other way here (the graph is genuinely
missing geometry the pavement has).

## Trigger sketch (narrow, to limit risk)

Two adjacent junctions connected by a single short cross-taxiway arm, where the two taxiways the
connector joins are roughly **parallel** (near-opposite bearings), the two corners turn in
**opposite** directions (S-shape / lane change), and the connector is short (~≤ the #236
`ShortConnectorMaxLenFt` scale). Emit one continuous blended curve that stays within the paved
connector + fillet envelope. Verify against `LayoutInspector --fillet-mode standard --html` on
SFO A/F1/B, OAK, FLL and re-run the guard + taxi-coverage suites.

## Verification

- Re-render SFO A/F1/B route: the pinned route line should read as a smooth S (matches the
  reporter's "expected" screenshot 131).
- `FilletCornerSpanGuardTests`, `GroundArcBezierPlaybackGuardTests`, SFO/OAK/FLL taxi-coverage
  sweeps stay green.
- The #236 replay test (`Issue236SfoAF1BConnectorTests`) still passes (the navigator speed-hold and
  the graph S-curve are complementary, not conflicting).
- Confirm existing recordings still replay (routes re-resolve sanely) or re-record affected
  fixtures.
