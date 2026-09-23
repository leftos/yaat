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

## Stopping short of a taxiway — the wingtip-clearance floor

Where no intersecting-taxiway marking is painted (the layout carries none), AIM 2-3-5.b.3 has the pilot stop "at a point which provides adequate clearance from an aircraft on the intersecting taxiway". `HoldShortAnnotator.ComputeHoldShortPositions` does that for every hold-short whose target is a **taxiway** (explicit `HS <twy>`, router-added, or `HS <x>@<twy>` naming a taxiway):

- **Which branch.** The half-length "nose at the mark" stop is keyed on the target — a runway crossing or destination runway, a `RunwayHoldShort` node, or a spot target — never on the node's type. SFO's B/T bar node is Spot "32", and keying on the node type stopped a B744 holding short of T with its nose 34.5 ft from T's centreline (#458).
- **The floor.** The nose stays at least `max(L/2 + 30, floor)` from the crossed taxiway's centreline, measured perpendicular to its straight edges (fillet arcs excluded), never to the bar node. `floor` comes from the airport's widest runway as a whole-airport worst case (the layout has no taxiway width or design group): < 75 ft → 49.5, < 100 → 64.5, < 150 → 84, < 200 → 132 (150 ft maps to ADG V: OAK 30 carries MD-11s), ≥ 200 → 156 ft (half the ADG span ceiling + 25 ft; the 25 ft is a judgement call). Placement walks back along the route from where it meets the crossed centreline until the nose clears; the centre is L/2 further back.
- **Caps.** The just-past-runway stop still wins (no floor, no new warning there). Otherwise the walk-back stops at the previous junction on the route (the last node that is a runway hold-short or has more than two non-ramp edges off the target), tail at that junction. It never moves forward of the old `length + 30` stop. When the clamp leaves the nose short of the floor, the route carries `holding short of TWY T — wingtip clearance from T not assured (N ft)`; a recompute replaces that warning rather than adding another.
- A stop that lands behind an aircraft already rolling (an `HS` armed mid-taxi) is unmakeable in the usual way (`TaxiingPhase.IsHoldShortUnmakeable`).

## Placement is not selection

This file covers where the bars **are**. Which one a *route* binds its `HoldShortPoint` to is a separate
decision, made by `RouteMaterialiser.AnnotateHoldShorts` — see
[pathfinder.md](./pathfinder.md#hold-short-handling--truncation). Both bars of a crossing are placed
correctly and carry the same combined `RunwayId` (`10R/28L`), so a name match alone cannot tell them
apart; the route must bind the bar on the side the aircraft approaches from.

The shape that breaks a naive pairing: **the crossing point can also be a taxiway junction**, which puts
the two bars of one crossing on differently-named taxiways. At SFO the F/C junction sits exactly on the
28L centerline, so 10R/28L's near bar is on `F` and its far bar is on `C`. Any pairing walk that stops at
a taxiway change misses the pair — that was issue #316, where a departure told to hold short of 10R drove
over an occupied 28L to reach the Charlie bar. Pair by *"does the route pass over the runway in between"*
(`HoldShortAnnotator.RouteCrossesRunwayAfterStart`), never by taxiway name.

The runtime half of the same case: a bar on the route's **own start node** (`segments[0].FromNodeId`) is
invisible to `ArriveAtNode` — it is no segment's ToNode — so `TaxiingPhase.TryHoldAtRouteStartNode` takes
that stop instead. It is checked **every tick** until the hold binds or stops applying, not once: a
re-route can arrive with the aircraft still rolling toward the bar from beyond the 150 ft parked radius
(a runway-exit hand-off on a sparse stretch whose nearest node is the bar), and a one-shot early check
would let it sail across the runway uncleared. While approaching, the navigator's speed is clamped to a
braking curve that reaches ~0 just short of the bar; the instant stop is only taken at crawl speed.
`StartNodeHoldShortArmingTests` pins both the parked and the rolling approach.

## Full-length vs intersection entry — `RunwayEntryPoint`

`RunwayEntryPoint.Resolve(layout, holdShortNodeId, runwayDesignator, currentTaxiway)`
(`src/Yaat.Sim/Data/Airport/RunwayEntryPoint.cs`) names the intersection a hold short enters the runway at, or returns
**null for a full-length entrance**. It is display-only (the departure-queue ordinal and status text consume it). The
discriminator is **side** — not taxiway name, and not distance alone:

1. The hold short nearest the departing end along-track is full length.
2. A hold short on the **opposite side** of the runway is the *same* entrance — also full length — when it is within
   `OppositeSideBandFt` (150 ft) of the nearest along-track **or** on the same taxiway (a taxiway crossing the end at an
   angle puts its two bars a few hundred feet apart along-track).
3. A hold short on the **same side** is always a second entrance and is named by its taxiway, however close to the end it sits.

Distance alone and taxiway name alone both misclassify real pairs, and "nearest per side" credits a side whose first
access is far down the runway (KMIA 30 `T`, KSFO 19L `E`) — hence the band in rule 2. The band is calibrated from an
along-/cross-track sweep of OAK, SFO, FLL, AUS, IAH, MSY, SMF, and MIA: the widest genuine opposite-side pair on different
taxiways is 96 ft (KOAK 15 `F`/`D`) and the closest opposite-side pair that is really an intersection is 188 ft
(KMIA 08R `L1`/`M1`), so 150 ft sits between them. Same-taxiway pairs skip the band (KOAK 33 `C`, KMIA 27 `Q10`). The
side test is what keeps a taxiway that touches both ends of one runway (KAUS `C`, KMSY `S`, KSMF `D`) from reading full
length at the wrong threshold.

Geometry projects onto the **pavement centerline** — `GroundRunway.Coordinates` from the GeoJSON, which matches the
published length (the takeoff surface), unlike a `NavigationDatabase` runway record whose threshold can be displaced.
`Coordinates[0]` is `Id.End1`'s threshold. Along-track is `GeoMath.AlongTrackDistanceNm`; side is
`GeoMath.SignedCrossTrackDistanceNm`. The taxiway name comes from the node's **straight** edges only — an arc at a hold
short carries a joined fillet name no controller would say — and `currentTaxiway` only breaks a multi-name tie (it is
null when naming the nearest node, so that stays deterministic). `AirportGroundLayout.GetExitTaxiwayName` is deliberately
not reused: exit semantics, first-edge-arbitrary, includes arc names.

Testing it: select nodes by edge taxiway name, never node id (see Footguns), and remember that one taxiway name can match
bars at **both** runway ends — an `Assert.Contains` over all same-name nodes passes vacuously. Pin the specific node
(nearest to the full-length bar) and mutation-check the side clause (force `oppositeSide` true, then false) to prove it
is actually covered.

## A hold-short armed under a taxi in progress

`GroundCommandHandler.TryHoldShort` / `TryAddExplicitHoldShorts` end by calling `TaxiingPhase.NotifyHoldShortsChanged()`; the next `OnTick` runs `ReaimAtChangedHoldShort`: when the current segment's target node carries an uncleared bar with a position, the navigator is re-aimed at the bar (`OverrideTargetPosition`) and the speed profile re-planned (`RefreshSpeedConstraints`), the same two steps `SetupCurrentSegment` takes at segment start. Before this the segment in progress kept the junction node as its target and the bar only bound at `ArriveAtNode`, so SKW5416 (CRJ7, 28 kt) told `HS T` on SFO's B stopped 78 ft from T's centreline with its nose in the intersection (GC 28/01 bundle, 2026-09-22).

**Feasibility.** `IsHoldShortUnmakeable` compares `AlongRouteDistanceToHoldShortFt` (the remainder of the current segment plus whole segments to the bar's node, less the bar's setback) with `HoldShortBrakingDistanceFt` = v²/2a at `CategoryPerformance.TaxiDecelRate`. No reaction time is added: at the jet rate (5 kt/s) v²/2a from 28 kt (132 ft) already equals a 1 s reaction plus a 0.42 g stop; a piston at 2 kt/s over-reads by ~74 ft from 20 kt, which is conservative. No FAA document gives a taxi stopping distance, so both are judgement calls. The in-progress leg is measured straight to its node (the navigator publishes no arc remainder), which reads short on a fillet — also conservative.

**Unable.** The handler decides it once at dispatch (`MarkUnmakeableHoldShort` sets `HoldShortPoint.Unable`, the result is `Unable to hold short of T — stopping`, and the pilot answers "unable to hold short of tango, stopping" — P/CG UNABLE; the instruction refused is AIM 2-3-5.b.3's "the pilot MUST STOP so that no part of the aircraft extends beyond the holding position marking"). The phase then `MoveBarToBrakingDistance`, exactly once per bar (`_unableStopNodeId`, snapshotted as `TaxiingPhaseDto.UnableStopNodeId` so a restore plus a second `HS` cannot ratchet the stop forward): the bar's Lat/Lon slide forward along the aircraft's heading (the path tangent, not the chord to the node) by the braking distance, clamped at the node the bar protects, and the taxi brakes at the full rate onto it — no crawl. The clamp makes the modelled overrun a lower bound: an aircraft that cannot make a runway bar does not stop at the junction either (AIM 2-3-5.a.1 makes the marking the runway safety area boundary). `Unable` round-trips as `HoldShortPointDto.Unable` (false on legacy snapshots) and `HoldShortAnnotator.ComputeHoldShortPositions` skips an unable bar, so a restore keeps the moved stop instead of putting the bar back on the painted line.

Measured (B738 on B westbound at 30 kt): issued 584 ft out → holds 271 ft from the junction node (the bar binds a fillet split node ~111 ft east of `FindIntersectionNode("B","T")`, plus the length+30 setback); issued 178 ft out → "unable", rolls 74 ft and stops 105 ft short of the junction. The field crawl at 5 kt in the bundle was the conflict detector's cap for SWA2644 on T (a crossing pair), not the hold-short.

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
