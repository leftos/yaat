# Fillet Arc Generator — S-turn / short-connector blending (superseded for #236)

## Status: superseded by the pathfinder fix; graph-layer S-curve remains an optional future nicety

Issue #236 ("SFO A F1 B weirdness") was resolved **without** a fillet-generator change:

1. **Navigator speed-hold** (shipped `b029d2a5`) — steady low speed across the short F1 connector.
2. **Pathfinder transition-arc exemption** — routes the A→F1 turn over the *existing* `[A,F1]` fillet
   corner arc instead of pivoting square at the junction node (`SegmentExpander` membership-penalty
   exemption; see [`../../ground/pathfinder.md`](../../ground/pathfinder.md)).
3. **Navigator corner-arc flat cap** — a corner arc is never flown faster than its safe cornering
   speed (see [`../../ground/navigator.md`](../../ground/navigator.md) "Current-arc flat cap").

Together these give the smooth-and-slow lane change the reporter wanted, using geometry the fillet
generator already produces.

## Why the graph-layer S-blend was NOT pursued

Investigation of the current SFO graph showed the A/F1/B complex is a **dense 3-junction cluster**,
not the clean "two parallel taxiways + one perpendicular connector" the original plan assumed:

- A/F1 junction = node 67, F1/B junction = node 153, and **taxiway AF crosses F1 straight through at
  node 827** — only ~18 ft east of the A/F1 corner.
- A single fillet S-arc from A to B would **bypass node 827 and sever the F1/AF crossing**, and would
  **fabricate curved geometry that is not in the source GeoJSON** (F1 is straight segments there).

So a graph-layer S-curve here is both topologically blocked (the AF crossing must remain on the path)
and higher-risk (replay-critical junction geometry, guarded by `FilletCornerSpanGuardTests` /
`GroundArcBezierPlaybackGuardTests`). The pathfinder + navigator fix achieves the visible result at
much lower risk.

## If revisited (future nicety)

The motivating observation still stands: SFO's *actual painted* centerlines around A/F1/B curve as a
smooth S. A future graph-layer blend (matching the pavement design so the *pinned/rendered* route line
also reads as a smooth S) would live in `src/Yaat.Sim/Data/Airport/Fillet/` — `SharedArmTangentPass`
(`ApplyCrossJunction` is the only cross-junction pass today) and `FilletPlanExecutor`. It must preserve
the AF crossing at node 827 and stay within the paved connector+fillet envelope. Read
[`../../ground/fillet-generator.md`](../../ground/fillet-generator.md) first. Not scheduled.
