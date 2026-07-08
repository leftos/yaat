# Runway Hold-Short Placement

How YAAT decides where the **runway holding-position** (hold-short) bars sit on every taxiway that
meets a runway. This runs once at graph-build time, **before** the [fillet generator](./fillet-generator.md),
inside `GeoJsonParser.Parse`. Read this before touching `RunwayCrossingDetector` or debugging a
hold-short that sits at the wrong distance.

## What these nodes are

Every taxiway↔runway boundary gets a `GroundNodeType.RunwayHoldShort` node — the point an aircraft
holds so it is clear of the runway (AIM 2-3-5.a.1: the marking identifies the **runway safety area
(RSA) boundary**). They are **entirely synthetic**: the vNAS airport-map GeoJSON has *no*
painted-hold-line feature type (only `parking`, `helipad`, `spot`, `taxiway`, `runway`), so the
positions are computed geometrically, not read from data.

Core code: `src/Yaat.Sim/Data/Airport/RunwayCrossingDetector.cs` (`DetectRunwayCrossings`). The bars
are drawn client-side by `GroundRenderer.DrawHoldShortBar` — a perpendicular tick through the node,
oriented to the taxiway; the wire contract carries only the node point, never a line segment.

## The standoff distance

The bar sits at a **constant perpendicular (cross-track) distance from the runway centerline**,
`RunwayRectangle.HoldShortNm`, resolved in `BuildRunwayRectangle`:

1. **Authoritative**: the runway feature's `holdShortDistance` (feet from centerline) from the vNAS
   map, when authored. Stored as `GroundRunway.HoldShortDistanceFt`. e.g. OAK authors 250/225/175 ft
   for 28L·10R / 28R·10L / 15·33.
2. **Fallback**: `HoldShortDistanceForWidth(widthFt)` — the FAA AC 150/5300-13B Table 3-2 setback
   (125/150/200/250/280 ft) keyed on runway width as an ADG proxy, used only when the map authors no
   value (e.g. OAK 30/12 → 250 ft).

The distance is **the same regardless of the exit taxiway's angle** — the RSA boundary is a line
parallel to the runway centerline, so an acute high-speed exit and a right-angle exit at the same
runway hold at the same perpendicular distance. Nothing about exit geometry changes the standoff.
(Tail clearance for a landing aircraft rolling *out* of the runway is a separate concern, handled by
the aircraft-length setback in [`RunwayExitPhase`](../landing-and-runway-exit.md) via
`VirtualNode.OffsetPast` — see below.)

## How the node is placed on the taxiway

For each boundary edge (one endpoint on the runway, one off), `ProcessBoundaryEdge` seats the node at
exactly `HoldShortNm` cross-track:

- **Interpolate** on the boundary edge when its off-node lies beyond the ideal (the common case) — an
  exact linear interpolation at the target cross-track fraction.
- **Walk** outward (`FindHoldShortInsertionPoint`, capped at `HoldShortWalkMaxHops`) when the off-node
  is still inside the standoff band, following the straightest same-taxiway continuation until a
  segment straddles the ideal, then interpolate there. Needed for wide runways where the ideal exceeds
  a single boundary-edge length.
- **Reuse in place** an existing shape-point node only when it already sits within `HoldShortSnapFt`
  (= `FilletConstants.CoincidentNodeThresholdFt`, **5 ft**) of the ideal — a minted node that close
  would be collapsed into it by the later fillet coincident-node merge anyway.
- **Dead-end fallback** (`DeadEndFallback`) when the taxiway genuinely terminates or hits a junction
  before reaching the ideal: it seats the bar at the farthest reachable point and logs a warning. This
  is the one path that legitimately lands short of the standoff.

### The reuse tolerance is deliberately tight

`HoldShortSnapFt` is **5 ft**, not a loose window. A generous reuse tolerance snaps the bar to whatever
GeoJSON shape-point happens to sit nearby, scattering the standoff by up to the tolerance — which pulls
**acute-exit** bars *inside* the RSA (their shape points fall short) and pushes **right-angle** bars
out past it. Tightening to the coincident-node threshold forces a node at the exact standoff whenever
no shape-point is essentially already there, making placement angle-independent.

## Footguns

- **Node IDs are ephemeral.** They are assigned by mint order and regenerated on every parse; any
  change that mints or removes a node (including a standoff-distance change that flips reuse↔mint)
  renumbers everything created afterward. **Never hardcode a hold-short (or downstream fillet) node ID
  in a test** — resolve it from the graph (by taxiway/runway/side, by bearing, or from the resolved
  route). Regenerate any unavoidable literal with `Yaat.LayoutInspector --exits <RWY>`.
- **Moving a bar moves the graph, which ripples into routing.** A hold-short node splits a taxiway
  edge, so changing its position (reuse↔mint flip, or a different standoff) shifts where the split
  lands, which the pathfinder is sensitive to. A hold-short is a degree-2 pass-through the graph-build
  inserts on the taxiway — consumers must treat it as such: it must not count as a bridge hop
  (`SegmentExpander.CollectBridgeCandidates`), and a route that crosses it en route to a further
  cleared taxiway must not be truncated at it (`RouteMaterialiser.FindLastClearedTaxiwayEntry` /
  `RouteReachesTaxiway`, which recognise the crossed taxiway via node-incidence, not just a labelled
  segment). Emergent multi-aircraft timing also shifts with any node-position change — recording-replay
  tests that depend on tight timing can desync.
- **Short taxiways land short.** A taxiway that terminates or merges before reaching the standoff hits
  `DeadEndFallback` and seats the bar at the farthest reachable point (logged as a warning). This is
  correct — the aircraft physically cannot hold further out — not a placement bug.
- **`holdShortDistance` is from centerline, not from the runway edge.** Do not add half-width.

## Verify

```bash
# Every hold-short's cross-track distance from centerline, per exit:
dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --exits <RWY> --json
# Placement path taken per boundary (reuse / walker / interpolate / dead-end):
dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --debug-fillets --dump
```

Regression coverage: `tests/Yaat.Sim.Tests/RunwayCrossingDetectorTests.cs` —
`DetectRunwayCrossings_Oak_Runway30_12_HoldShortsAreAngleIndependent` (every exit at the same standoff
regardless of angle) and `BuildRunwayRectangle_Oak_UsesAuthoredHoldShortDistance_ElseWidthFallback`
(authored value wins, width heuristic is the fallback).
