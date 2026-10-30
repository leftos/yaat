# Runway Hold-Short Placement

How YAAT decides where the **runway holding-position** (hold-short) bars sit on every taxiway that
meets a runway. This runs once at graph-build time, **before** the [fillet generator](./fillet-generator.md),
inside `GeoJsonParser.Parse`. Read this before touching `RunwayCrossingDetector` or debugging a
hold-short that sits at the wrong distance.

## What these nodes are

Every taxiway↔runway boundary gets a `GroundNodeType.RunwayHoldShort` node — the point an aircraft
holds so it is clear of the runway (AIM 2-3-5.a.1: the marking identifies the **runway safety area
(RSA) boundary**). They are **generated**: the vNAS airport-map GeoJSON has *no* painted-hold-line feature type (only `parking`, `helipad`, `spot`, `taxiway`, `runway`), so each position is computed geometrically by the standoff rule, then snapped onto the painted bar the airport's tower-cab videomap draws on that taxiway when one pairs with it ([Seating on the painted bar](#seating-on-the-painted-bar)). An airport with no videomap keeps every bar at the rule's position.

Core code: `src/Yaat.Sim/Data/Airport/RunwayCrossingDetector.cs` (`PlanRunways`, `DetectRunwayCrossings`, `SeatCrossing`), the rule itself in `HoldShortStandoff.cs` and the paint pairing in `RunwayPaintPairing.cs`. The bars are drawn client-side by `GroundRenderer.DrawHoldShortBar` — a perpendicular tick through the node, oriented to the taxiway; the wire contract carries only the node point, never a line segment.

## The standoff distance

The bar is placed by the reviewed standoff rule (`HoldShortStandoff`; AIM 2-3-5.a.1, with the distances from AC 150/5300-13B, which is not in the local references). It has two parts: a **base separation S** per runway, and a **centerline point D** per crossing.

**S** is the distance from the runway centerline to the *nearest point* of the hold line, resolved once per runway by `HoldShortStandoff.BaseSeparationFt` (`RunwayCrossingDetector.PlanRunways`, through `ResolveStandoff`) and stored as `GroundRunway.HoldShortStandoffFt` (nullable, omitted from the JSON when null, so no layout format bump). `RunwayRectangle.HoldShortNm` and `MarkShortBars` hold S, never D. By first match:

1. **Paint calibration** (`calibratedFt`, `HoldShortStandoff.CalibratedFt`): with a videomap, the median of the near-end cross-tracks (each bar's drawn end nearer the runway centerline, `PaintPair.NearEndFt`) of the runway's paint-paired bars, both ends and sides pooled, each bar once, rounded to 0.1 ft; the mean of the middle two for an even count. It needs at least `HoldShortStandoff.MinCalibrationBars` (3) bars, else the runway falls through to the next match. There is no elevation term: the paint is measured. Every reader follows the calibrated S, and a parse with no videomap is unchanged. OAK calibrates 28R/10L to 252.1 ft, 28L/10R 258.5, 30/12 280.8 and 15/33 164.9.
2. **Authored**: the runway feature's `holdShortDistance` (feet from centerline), stored as `GroundRunway.HoldShortDistanceFt`, unless it is more than 15 ft below the rule value for a runway in the 250 ft class (a rule value of 250 ft or more); then the rule value is used and `WarnWhenRuleOverAuthored` logs one warning per runway per parse naming the airport, runway, authored and rule values. It does not fire for a calibrated runway, where the paint decided S.
3. **Rule** (`RunwayStandoffInputs`: width, length, ILS category, elevation): width 200 ft or more, or 150 ft or more with an ILS CAT II or III, gives 280; a 100 ft or wider runway gives 250 when it is 5,000 ft long or longer or has any ILS, else 200; 75 ft or wider gives 150 (175 with an ILS); narrower gives 125 (175 with an ILS). A result of 250 or more adds 1 ft per 100 ft of the runway's higher threshold elevation.

   The ILS category is the highest over the runway's two ends from the CIFP localizer records, and only an ILS moves a runway to the instrument rows; a LOC, LDA or LPV approach does not ([navigation-database.md](../navigation-database.md)). A runway with no navdata reads as 150 ft wide, at least 5,000 ft long, no ILS, elevation 0.

**D** is where the taxiway's *centerline* meets the hold line: `D = S + (W_twy / 2) · |cos θ|` (`HoldShortStandoff.CenterlineStandoffFt`). The marking is painted square to the taxiway centerline with its nearest point at S, so on a taxiway at θ to the runway the centerline point sits farther out; a perpendicular taxiway adds nothing.

θ is the bearing, against the runway heading, of the walk segment where the taxiway centerline reaches cross-track S (`MeasureCrossingAngleDeg`, measured at parse time before the fillets exist). W_twy is a proxy from the *crossed* runway's width, as the layout carries no taxiway width: 75 ft from a 150 ft runway up, 50 from 100, 35 from 75, else 25 (`TaxiwayWidthProxyFt`). D is threaded to the placement as a per-crossing target and never stored on the runway.

D is the rule's standoff, D_rule. D is not taken, and the bar is seated at S, when the taxiway path from the crossing to the D point, or the D point itself, comes within another runway's S plus 5 ft (`OtherRunwayAreaMarginFt`) of its centerline alongside its length (`TouchesHoldingArea`); a taxiway between two close runways keeps its bar out of the other's holding area.

A **short bar** is a hold-short node inside S (a dead-end fallback), a **far bar** the crossed runway's bar on the side an aircraft leaves by (both in [CONTEXT.md](../../CONTEXT.md)). Because an angled bar is farther out than S, code that must reach a runway's bars bounds its walk by the largest cross-track among the runway's hold-line bars (`AirportGroundLayout.OutwardWalkBoundFt`), not by S.

The RSA boundary's nearest point is a line parallel to the runway centerline, so S is the same for every taxiway at a runway; the stop point on the taxiway centerline is not. Both `HoldShortDistanceForWidth` and `AirplaneDesignGroup.FromRunwayWidth` still exist for their other callers; `HoldShortDistanceForWidth` is read only by `BuildRunwayRectangle(GroundRunway)` for a runway of a layout stored before S, which has neither S nor an authored value.
(Tail clearance for a landing aircraft rolling *out* of the runway is a separate concern, handled by
the aircraft-length setback in [`RunwayExitPhase`](../landing-and-runway-exit.md) via
`VirtualNode.OffsetPast` — see below.)

The precompute key carries a `StandoffInputHash` over the runway inputs the rule reads, so a navdata or CIFP change that moves S stales an airport's entry ([`PrecomputeCache/README.md`](../../src/Yaat.Sim/Data/PrecomputeCache/README.md)).

## How the node is placed on the taxiway

For each boundary edge (one endpoint on the runway, one off), `ProcessBoundaryEdge` seats the node at
the crossing's ideal cross-track, D (S itself for a perpendicular taxiway or where D is not taken):

- **Interpolate** on the boundary edge when its off-node lies beyond the ideal (the common case) — an
  exact linear interpolation at the target cross-track fraction.
- **Walk** outward (`FindHoldShortInsertionPoint`, capped at `HoldShortWalkMaxHops`) when the off-node
  is still inside the standoff band, following the straightest same-taxiway continuation until a
  segment straddles the ideal, then interpolate there. Needed for wide runways where the ideal exceeds
  a single boundary-edge length.
- **Reuse in place** an existing shape-point node only when it already sits within `HoldShortSnapFt`
  (= `FilletConstants.CoincidentNodeThresholdFt`, **5 ft**) of the ideal — a minted node that close
  would be collapsed into it by the later fillet coincident-node merge anyway. A bar seated on paint measures that distance straight to its snap point and refuses a node more than `PaintRunwaySideToleranceFt` (1 ft) on the runway side of it.
- **Dead-end fallback** (`DeadEndFallback`) when the taxiway genuinely terminates or hits a junction
  before reaching the ideal: it seats the bar at the farthest reachable point and logs a warning. This
  is the one path that legitimately lands short of the standoff.

### The reuse tolerance is deliberately tight

`HoldShortSnapFt` is **5 ft**, not a loose window. A generous reuse tolerance snaps the bar to whatever
GeoJSON shape-point happens to sit nearby, scattering the standoff by up to the tolerance — which pulls
**acute-exit** bars *inside* the RSA (their shape points fall short) and pushes **right-angle** bars
out past it. Tightening to the coincident-node threshold forces a node at the exact standoff whenever
no shape-point is essentially already there, so the scatter is the standoff rule's (the angle term), not the data's.

A bar on paint is the exception: `TrySeatOnNearbyNode` merges a plain taxiway shape point (`CanTakePaintBar`: a `TaxiwayIntersection` on two edges of one taxiway, not yet on paint) within `PaintNodeMergeFt` (**6 ft**) of the snap point into the bar, so no node is minted beside it. On the taxiway side of the paint, or on it, the node stays where it is; on the runway side it is moved onto the snap point (`MoveNode`, which updates its edges' lengths and the coordinate index), so the nose never ends past the line. The move is refused when it would leave the node within `CoincidentNodeThresholdFt` (5 ft) of a neighbour on one of its edges (`SnapCrowdsNeighbour`): the edge between them would read as a zero-length no-op to the pathfinder and block every route that starts on the bar. The refusal leaves the reuse-or-mint logic above to run. A node the 5 ft reuse tolerance alone would have refused still consumes one node id, as the mint it replaces would have.

## Seating on the painted bar

`GeoJsonParser.BuildLayout` passes the airport's `TowerCabVideoMap` (`LayoutInputs.VideoMap`, null when it has none) to `DetectRunwayCrossings`, which seats each crossing through `SeatCrossing`. The tower-cab videomap draws the real painted hold bars as two-point segments ([CONTEXT.md](../../CONTEXT.md), *Tower-cab videomap*); a crossing whose taxiway carries one is seated on it, not at D_rule.

**Pairing** (`RunwayPaintPairing.Candidates`). `RunwayCrossingDetector.PlanRunways` pairs every runway's crossings once, in a read-only pre-pass on the layout before any bar is seated, against D_rule from the uncalibrated S (rule or authored) of every runway. The pairs ride on `RunwayPlan.PaintPairs` by the crossing's node pair, and the near ends of the first-ranked bars calibrate S ([The standoff distance](#the-standoff-distance)). Seating reuses those pairs (`CandidatesFor`); a crossing the pre-pass never saw is paired live at its seat-time D_rule, logged at Debug. A segment is a candidate bar for a crossing when:

- It meets the crossing's own taxiway centerline: the boundary edge, then the placement walk's continuations (`PickWalkContinuation`) through junctions while the next edge keeps the taxiway's name, with the segment extended `BarExtensionFt` (10 ft) at either end. Paint on another taxiway's branch past a junction never pairs.
- It is square to the taxiway there (within 30° of perpendicular) or within 15° of parallel to the runway; it crosses no runway centerline; and it does not lie along a drawn taxiway or runway centerline (both ends within 15 ft of it and within 20°).
- Its snap point, where it meets the taxiway centerline, lies on the crossing's side of the runway inside the **snap band**: D_paint from D_rule less `MaxInsideRuleFt` (60 ft) to D_rule plus `MaxOutsideRuleFt` (100 ft), and at least the runway's half-width plus `MinBeyondRunwayEdgeFt` (50 ft) from the centerline.

Candidates rank innermost first: the nearer snap point along the taxiway walk from the runway (an aircraft leaving the runway is clear at the first marking it crosses, AIM 2-3-5.a.1), then the smaller D_paint, then the lower file index.

`PairWithPaint` takes the first candidate the placement can seat (`CanSeatOn`): one is passed over when the walk toward its D_paint dead-ends, runs out of `HoldShortWalkMaxHops`, or meets D_paint more than `HoldShortSnapFt` from the snap point (a chain that turns back toward the runway). With no candidate left, the bar is seated by the rule.

**Snap.** The bar's target becomes D_paint with the snap point (`BarTarget.SnapPoint`), and the placement above runs against it, with the paint merge first ([The reuse tolerance](#the-reuse-tolerance-is-deliberately-tight)).

**Seating on the painted bar.** A node reused or moved onto the paint becomes the bar, and it is the end of the approach segment that reaches it.

A minted node's edge fraction is clamped to `MinEdgeFraction` (0.01) … `MaxEdgeFraction` (0.99), off the endpoints. `MintFraction` is the one exception: when the bar is on paint and the raw fraction lies inside that clamp beside an endpoint refused for reuse (one on the runway side of the paint), the node is minted at the raw fraction, on the snap point, so the endpoint moves onto the paint instead of staying off it. A snap that moves a bar more than `LargeSnapFt` (40 ft) from where the rule would seat it is logged at Debug.

**End clearance.** A minted node is also kept clear of the edge ends it splits: one that would land within `GeometricAdmissibility.NoOpEdgeThresholdNm` (≈1.2 ft) of an end moves to 0.01 ft past that threshold, because the pathfinder reads a shorter edge as a zero-length no-op (`ClearOfEdgeEnds`); an edge too short to hold a point clear of both ends keeps its fraction, logged at Debug. A far-end move seats the bar toward the runway, so on an edge whose far node sits on the paint it can end up up to about 1.2 ft runway-side of the paint.

**What a paint-paired bar changes.** The node records its segment in `GroundNode.PaintSegmentIndex` (the index among the videomap's segments in file order; omitted from the JSON when null, so no layout format bump).

`MarkShortBars` always reads a paint-paired bar as its runway's hold line, whatever its cross-track. `DropRedundantFallbackBars` judges a fallback junction against a ceiling raised to the farthest D_paint among its runway's paint-paired bars (`RaiseCeilingsToSnappedBars`), so a route out of the junction that reaches such a bar still counts as reaching one.

The videomap rides in the precompute key (`VideoMapHash`, [`PrecomputeCache/README.md`](../../src/Yaat.Sim/Data/PrecomputeCache/README.md)). `RunwayPaintSnapTests` pins the pairing and the placement on real layouts.

## Stopping short of a taxiway — the wingtip-clearance floor

Where no intersecting-taxiway marking is painted (the layout carries none), AIM 2-3-5.b.3 has the pilot stop "at a point which provides adequate clearance from an aircraft on the intersecting taxiway". `HoldShortAnnotator.ComputeHoldShortPositions` does that for every hold-short whose target is a **taxiway** (explicit `HS <twy>`, router-added, or `HS <x>@<twy>` naming a taxiway):

- **Which branch.** The half-length "nose at the mark" stop is keyed on the target — a runway crossing or destination runway, a `RunwayHoldShort` node, or a spot target — never on the node's type. SFO's B/T bar node is Spot "32", and keying on the node type stopped a B744 holding short of T with its nose 34.5 ft from T's centreline (#458).
- **The floor.** The nose stays at least `max(L/2 + 30, floor)` from the crossed taxiway's centreline, measured perpendicular to its straight edges (fillet arcs excluded), never to the bar node.

  `floor` comes from the airport's widest runway as a whole-airport worst case (the layout has no taxiway width or design group): < 75 ft → 49.5, < 100 → 64.5, < 150 → 84, < 200 → 132 (150 ft maps to ADG V: OAK 30 carries MD-11s), ≥ 200 → 156 ft (half the ADG span ceiling + 25 ft; the 25 ft is a judgement call). Placement walks back along the route from where it meets the crossed centreline until the nose clears; the centre is L/2 further back.
- **Caps.** A hold short less than a fuselage length past the hold line of a runway the route crossed takes the just-past-runway stop (no floor, no new warning there); one a fuselage length or more past it is held to the floor like any other, with that runway's bar as its walk-back limit. The floor's early returns (no target, no centreline) hold the stop to the walk-back limit too. Otherwise no taxiway junction behind caps the walk-back: it goes back past any intersection and stops only (`WalkBackLimitCentreFt`) where the aircraft stands along the route (at most the bar's own node), or at a runway hold-short bar the route crossed, the tail staying `RunwayTailClearance.MarginFt` (10 ft) clear of that bar (AIM 4-3-21.b).

  Those two limits bound the length setback too, so a stop they put forward of `length + 30` is kept there. A stop the setback would put behind the aircraft is placed where the aircraft stands (`NoFurtherBackThanTheAircraft`; how far back the setback put it is carried from the placement, never found again by projecting the point onto the route, which a route that doubles back passes twice).

  It never moves forward of the old `length + 30` stop, except as those two limits hold it. When the clamp leaves the nose short of the floor, the route carries `holding short of TWY T — wingtip clearance from T not assured (N ft)`; a recompute replaces that warning rather than adding another.
- **Just past a runway.** A taxiway hold short less than a fuselage length past the hold line of a runway the route crosses takes the nose-at-line setback (half a length), clamped at the runway hold line, and leaves the tail over the bars (`holding short of TWY T leaves the tail over RWY 28R — unable to clear the runway`); from a fuselage length on, the floor places it, with the tail at least `RunwayTailClearance.MarginFt` (10 ft) past the runway hold line even when that puts the nose a few feet past the hold-short node (AIM 4-3-21.b lets the aircraft protrude toward an intersecting taxiway to clear the runway), and the wingtip warning when the nose falls short of the floor.

  The stop records its real setback, the gap to the runway hold line when that is shorter than half a length, so an aircraft standing between the bar less half a length and the hold line keeps the stop at the hold line instead of having it moved to where it stands, and a rolling aircraft is not answered unable.
- **A target straight ahead.**

  When the target taxiway continues the one the aircraft is on (it leaves the bar node within 20° of the arriving heading, as T continues TE at OAK junction #136), nothing on it can pass abeam the nose, so the floor is measured against the junction's **other** branches instead.

  Those are the branches not within 20° of straight ahead or straight behind (U and TC at #136), each traced along its own edges for 500 ft from the node, never looked up by name (SFO node 133, where a second B forks off as B runs into Z, would otherwise measure the nose against its own line). The warning names them: `holding short of TWY T — wingtip clearance from TC/U not assured (92 ft)`.

  A branch named for the target that leaves straight behind is dropped like any other straight-behind branch, so a junction left with no other branch (the taxiway only changes name, OAK C→A) gets the plain `length + 30` stop and no warning.

  A target-named edge behind that fans off the arriving line by less than 1° is the same line drawn under two names (FLL A/A1, MIA Q2/M2, IAH WA), not a merge, and takes this path too. 7110.65 §3-7-2a.2(b) makes `HS T` valid even though T is a later segment of the issued route ("stop where TE becomes T").
- **A shallow merge.**

  A bar node is a shallow merge when the target continues straight ahead and another straight edge named for the target leaves within 20° of straight behind, off the arriving line by at least 1° (OAK D into C at 5.7°, W5 into W). An aircraft on the target's leg behind then passes alongside the holder's nose, so the floor is held against the **target's own centreline**, never the node's other branches. Other taxiways merging from behind are not considered.

  No intersection behind caps the walk-back, and the aircraft holds short of the target itself: the stop is wherever the floor against the target's centreline is met, which can be hundreds of feet back where the arriving taxiway diverges at a few degrees.

  When that stop is behind the `length + 30` setback and meets the floor, the route says so, since nothing else would explain the early stop: `holding short of TWY W 203 ft back (W5 merges into W)`, the nose's distance back of the merge node. A stop short of the floor carries the wingtip warning instead; the readback stays plain.
- **The stop can sit several segments before the junction.** Where shape points are dense (OAK TE) the painted stop lands segments before the bar node's own segment: 153 ft back at #136, four segments before its 33 ft segment.

  Every runtime consumer measures the **along-route** distance to the painted stop (`TaxiRoute.HoldShortSetbackNm` and `StopLiesOnSegment` — the segment the stop lies closest to, so a stop just before a bend counts as set back — and `AlongRouteDistanceToHoldShortFt`), never the distance to the bar node: the navigator brakes for it and the taxi phase takes the hold there (see [navigator.md](./navigator.md)).

  On a route that doubles back over its own stop, the stop is measured on the leg that drives into the bar: `HoldShortSetbackNm` walks back from the bar's segment only as far as the segment in progress, and reaches the segments behind it only when none ahead carries the stop.

  The unmakeable judgement in `TaxiingPhase`, the navigator's braking-curve constraint and `StopLiesOnSegment` all share it, so an aircraft rolling on the leg back toward a stop it passed on the way out is not judged unable to hold short of it.

  Runway bars share this: their half-length setback (65 ft for a B738) is longer than the 7–31 ft last segment at many OAK and SFO bars.
- **The setback walks free-space legs too.** `VirtualNode.OffsetBefore` steps back through the route's own segments (`FindApproachNode` takes each segment's from-node, virtual or not), so a bar at the end of a free-space approach leg is set back along the leg exactly as along a graph edge.

  For a **taxiway** bar, a leg shorter than the setback ends the walk at the leg's virtual start, where the aircraft stands: the stop goes there, never projected on behind the aircraft (`HoldShortAnnotatorTests`, SFO B short of T).

  A **runway** bar (a crossing, a destination runway, a `RunwayHoldShort` node or a spot target) is never clamped there: its setback is projected on behind the aircraft by the real overrun.

  A stop placed on the aircraft reads as zero distance ahead, and which side of zero it falls on is rounding, so the taxi phase's lost-line check (`toStopFt < 0`) would read the line as made on one platform and lost on another (`HoldInsideStoppingDistanceOfBarTests.LineAlreadyLost_RunwayBar_ReRoutedStopStaysBehindTheAircraft`).
- A stop that lands behind an aircraft already rolling (an `HS` armed mid-taxi) is unmakeable in the usual way (`TaxiingPhase.IsHoldShortUnmakeable`: the bar lies inside the firm-rate flown stop plus the set-back margin).

  A bar flagged unable while another phase flew the aircraft (a follow) is judged again when a fresh taxi phase is about to move its stop, and keeps its painted stop if the firm rate makes it. A stopped aircraft (2 kt or less) cannot overrun a bar, so it never reads one as unmakeable: it reads the hold short back and holds where it stands.

## Hold line or short bar — `GroundNode.IsShortBar`

`RunwayCrossingDetector.MarkShortBars` runs as the last build step (`GeoJsonParser.BuildLayout` step 9, after the fillets have settled every node's position) and sets `GroundNode.IsShortBar` on every `RunwayHoldShort` node whose runway is in the layout: false for the runway's hold line, true for a short fallback bar inside the holding distance. A paint-paired bar is always false. Otherwise it decides by the bar's cross-track from the runway centerline against the holding distance, within `HoldingDistanceToleranceFt` (`AirportGroundLayout.IsAtHoldingDistanceByCrossTrack`: at or beyond S less the tolerance, so an angled bar at D is a hold line). Every other node reads null.

`AirportGroundLayout.IsAtRunwayHoldingDistance`, the short-bar resolution and the exit search's outward walk read the flag first, and fall back to the cross-track measurement only when it is null (a node whose runway is not in the layout, or a layout serialized before the field). The flag is serialized only when non-null, so it rides in the layout JSON of recording archives and the precompute store without a layout format bump ([snapshots-and-replay.md](../snapshots-and-replay.md)).

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

The runtime half of the same case: a bar on the route's **own start node** (`segments[0].FromNodeId`) is invisible to `ArriveAtNode` — it is no segment's ToNode — so `TaxiingPhase` takes that stop itself, in one of two ways.

With the nose short of the bar's marking, `TaxiingPhase.TryHoldAtRouteStartNode` takes it. It is checked **every tick** until the hold binds or stops applying, not once: a re-route can arrive with the aircraft still rolling toward the bar from beyond the 150 ft parked radius (a runway-exit hand-off on a sparse stretch whose nearest node is the bar), and a one-shot early check would let it sail across the runway uncleared.

While approaching, the navigator's speed is clamped to a braking curve that reaches zero 15 ft short of the node; the stop is taken within 150 ft of it at 3 kt or less. `StartNodeHoldShortArmingTests` pins both the parked and the rolling approach.

**Behind the painted stop, the route does not start on the bar.** An aircraft on the straight taxi edge leading to a runway bar, its centre more than half its length plus `GroundNavigator.SetBackStopMarginFt` behind the node measured along the occupied edge (`TaxiApproachLeg.BehindBarAlongEdgeFt`, not along the first leg, so a bar at a bend does not shorten it by the bend's cosine) while also behind it along the first leg, gets a `TaxiApproachLeg` to the bar like any other start: the bar is then the leg's far end, set back half a fuselage by the usual setback, so the aircraft holds there or crosses only under a clearance (`TaxiFromShortOfARunwayBarTests`).

An aircraft within that stop, past the bar on its runway side, or off the edge keeps the bar as its start node and gets the two stops here. A vacated aircraft on the stub with a route away from the runway gets a leg too and is not held at the bar (the next rule).

With the nose already at or past the marking of an uncleared **runway** bar on the start node (a TAXI starts the route on a bar's node when the aircraft is not behind the bar's painted stop on the edge leading to it, so this is common), the line is lost. `TaxiingPhase.TryHoldPastStartBar` takes that hold instead: the aircraft brakes at the firm rate as soon as it can, to keep off the runway's pavement, never stopped dead from taxi speed, and holds once it is down to one sub-tick of that braking, wherever that leaves it.

The finding is latched on the bar's node (`TaxiingPhaseDto.PassedStartBarNodeId`), so the stop is not let go part-way as it carries the aircraft away from the node; how the nose is judged past the line is in [navigator.md](./navigator.md) § Stopping at an uncleared bar.

`HoldInsideStoppingDistanceOfBarTests` pins an `HS` that re-arms the start bar, live and after a rewind. A re-route with no `HS` that leaves the aircraft partway across that runway keeps the crossing cleared and finishes it, so it never reaches this stop ([navigator.md](./navigator.md) § A re-route mid-crossing keeps the crossing).

**A start bar is a hold or a runway entry only for a route leading toward its runway.** The holding position marking stops traffic not cleared onto the runway; for traffic leaving the runway it marks the point where the aircraft is clear (AIM 2-3-5.a.1, 4-3-21.a). So a start bar whose route's first leg leads away from the runway (its far node farther from the runway centreline than the bar; `HoldShortAnnotator.FirstLegLeadsTowardRunway` is the test for the opposite) is neither a hold nor a crossing.

An arrival's `TAXI` off the runway adds no "(cross 28R/10L)" for the exit bar it leaves by, and a second `TAXI` given off the pavement short of the far marking holds at no 28R point.

The bar also has to be still ahead of the aircraft along that first leg (`StartBarIsAhead`): no part of the body (nose, tail, either wingtip, from `StartPose.PartPositions`) lies more than `HoldLineFoulToleranceFt` past the bar in the leg's direction, so an aircraft facing the bar and cleared back the other way, whose tail is furthest along the route, has the bar behind it.

`HoldShortAnnotator.StartBarHolds` is the one test, shared by `AddImplicitRunwayHoldShorts` (as a hold) and `FollowingPhase.RunwayEntries` (as a runway entry). A layout with no ground runway of the bar's name leaves the bar a hold, the safe side, with one warning per airport and node.

The caller passes the aircraft's `StartPose` (main gear position, heading, body from the type; `StartPose.Of(aircraft)` or `Of(position, heading, type)`); a route that starts elsewhere than at the aircraft, an extension past another route's end, passes none and its start bar is never a hold. `TaxiPathfinder.FindAdjacentRunwayRoute` requires a pose.

The start-node hold is taken by the phase (`TaxiingPhase.FirstRunwayBarAhead` returns it with index -1 while the route has not advanced, so a crossing clearance the aircraft already holds carries over to a new `TAXI`), and when the navigator holds an unsaid turn-about unable, the unable is said before the hold.

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

`GroundCommandHandler.TryHoldShort` / `TryAddExplicitHoldShorts` end by calling `TaxiingPhase.NotifyHoldShortsChanged()`; the next `OnTick` runs `ReaimAtChangedHoldShort`: when the current segment's target node carries an uncleared bar with a position, the navigator is re-aimed at the bar (`OverrideTargetPosition`) and the speed profile re-planned (`RefreshSpeedConstraints`), the same two steps `SetupCurrentSegment` takes at segment start.

Without it the segment in progress keeps the junction node as its target and the bar binds only at `ArriveAtNode`, so SKW5416 (CRJ7, 28 kt) told `HS T` on SFO's B stopped 78 ft from T's centreline with its nose in the intersection (GC 28/01 bundle).

**Feasibility.** `IsHoldShortUnmakeable` compares `AlongRouteDistanceToHoldShortFt` (the remainder of the current segment plus whole segments to the bar's node, less the bar's setback) with the stop the aircraft flies at the firm rate: `MovedStopDistanceFt` (v²/2a at `CategoryPerformance.ExpediteExitDecelRate`, plus v·dt/2 for the sub-ticked physics) plus `GroundNavigator.SetBackStopMarginFt` (2 ft), the margin the firm curve onto a painted stop reaches zero short of it.

A bar judged makeable is one the firm rate does stop at. It applies only above `StoppedGroundSpeedKts` (2 kt): a stopped aircraft whose stop is already behind it holds where it stands.

No reaction time is added: a pilot's response time is the scenario's command-run delay, applied before the clearance reaches the aircraft, so a term here would count it twice. The in-progress leg is measured straight to its node (the navigator publishes no arc remainder), which reads short on a fillet — also conservative.

**Unable.** The handler decides it once at dispatch (`MarkUnmakeableHoldShort` sets `HoldShortPoint.Unable`, the result is `Unable to hold short of T — stopping`, and the pilot answers "unable to hold short of tango, stopping" — P/CG UNABLE; the instruction refused is AIM 2-3-5.c's "the pilot MUST STOP so that no part of the aircraft extends beyond the holding position marking").

The phase then `MoveBarToBrakingDistance`, exactly once per bar, latched as `HoldShortPoint.StopMoved` (on the bar, not the phase, so a fresh taxi phase over the same route, a follow resuming its assigned route, or a restore plus a second `HS` cannot ratchet the stop forward): the bar's Lat/Lon move along the route (`PointAheadAlongRoute`: each leg by its own length, an arc walked as an arc, not along the heading or the node chord) by `MovedStopDistanceFt`, clamped at the node the bar protects.

The stop is never moved backward: a painted stop at or past the flown stop is kept.

The taxi then brakes onto the stop at the firm rate on the sub-ticked curve (`GroundNavigator.PlansStopsOnSubTicks`, `CurveKts`), and `UnableStopCapKts` caps the published speed at that curve. `ApplyStopBrakeRate` takes the firm rate on the tick the stop moves and keeps it until the taxi rate makes the stop outright, going back to it only when the taxi rate misses by more than `TaxiCurveSlackFt`, so an aircraft slowed by a HOLD or `RES` rides the taxi curve.

The clamp makes the modelled overrun a lower bound: an aircraft that cannot make a runway bar does not stop at the junction either (AIM 2-3-5.a.1 makes the marking the runway safety area boundary). `Unable` and `StopMoved` round-trip as `HoldShortPointDto.Unable` and `HoldShortPointDto.StopMoved` (false on legacy snapshots) and `HoldShortAnnotator.ComputeHoldShortPositions` skips an unable bar, so a restore keeps the moved stop instead of putting the bar back on the painted line.
Measured (B738 on B westbound at 30 kt): issued 584 ft out → holds 271 ft from the junction node (the bar binds a fillet split node ~111 ft east of `FindIntersectionNode("B","T")`, plus the length+30 setback); issued 178 ft out → "unable", rolls 74 ft and stops 105 ft short of the junction. The field crawl at 5 kt in the bundle was the conflict detector's cap for SWA2644 on T (a crossing pair), not the hold-short.

**Inside the stopping distance without `HS`.** A bar the aircraft is already inside the taxi-rate stopping distance of without an `HS` (a `TAXI` re-route that drops a crossing clearance short of the runway's entry marking, or a `HOLD` or `GIVEWAY` given close to the bar) is never marked unable: the stop stays on the painted line, centre at the stop and nose at the marking, and `TaxiingPhase` brakes harder for it.

It takes the firm rate when only that makes the stop and, at a runway bar whose line is not yet lost, a last-resort dead stop with the nose short of the marking. A taxiway bar is never stopped dead: the hold is taken where the firm rate stops the aircraft. The braking ladder is in [navigator.md](./navigator.md) § Stopping at an uncleared bar.

**Releasing an explicit taxiway or spot bar before the aircraft arrives.** A bare `RES` to a rolling aircraft driving a route pre-clears the next uncleared taxiway or spot bar (`HS B`, `HS $17`) ahead, the way a bare `CROSS` does: `GroundCommandHandler.TryPreClearNextTaxiwayHoldShort` sets `IsCleared` on that one `HoldShortPoint` and answers `Continue taxi past B`. Later bars stay armed.

"Next" is route order (`TaxiRoute.NextUnclearedHoldShortAhead`, by the segment that reaches the bar's node), not the order of `HoldShortPoints`, which appends a bar added mid-taxi.

`CROSS` stays the clearance for a runway bar: when the next bar is a runway one (crossing, explicit `HS 28R` — `TaxiRoute.IsTaxiwayClassHoldShort` is false), `RES` is refused with `Next hold short is runway 28R/10L; use CROSS` and the bar stays armed; the destination runway's bar is refused `Next hold short is departure runway 28R/10L; issue CTO or LUAW`.

A bar that reads taxiway-class but stands at a layout `RunwayHoldShort` node, or shares its node with another uncleared runway-class bar, is refused as a runway bar too (`GroundCommandHandler.GuardsARunway`).

The route read is the one the aircraft drives (`FollowingPhase.DrivenRouteOf`), so a `FOLLOWG` follower, whose follow route has no hold short, answers `No upcoming hold short to clear`. `RES` at a runway bar the aircraft is already holding short of is unchanged: it releases the bar, with no held-bar refusal.

## Route-incomplete holds

A `HoldShortReason.RouteIncomplete` point ends a route that could not reach its destination as cleared. The TAXI handler cuts the route before the taxiway X the destination needs and the clearance did not name (the fallback is described in [pathfinder.md](./pathfinder.md#incomplete-clearances--the-route-incomplete-hold-issue-461)).

Its `TargetName` is X, a taxiway, so `ComputeHoldShortPositions` places it on the **taxiway setback** and wingtip floor above, not on the half-length runway stop. The one exception is a hold node that is itself a `RunwayHoldShort` node.

- **Past a bar the route leaves its runway by.** A no-destination or missing-taxiway cut (`GroundCommandHandler.CutBeforeTaxiway` → `PastBarLeavingRunway`) and an explicit `HS <taxiway>` (`HoldShortAnnotator.PlanExplicitHoldShort`) bind past a runway bar the route leaves its runway by (`HoldShortAnnotator.IsBarLeavingRunway`), judged by `HoldShortAnnotator.BindsPastLeavingBar`: a segment follows, no runway crossing or destination hold sits on the bar, the next segment ends on neither a runway bar nor a runway centreline, and it is not a straight `GroundEdge` of the target taxiway. Otherwise the hold binds the bar as before. The cut logs `[CutBeforeTaxiway] … bar #N leaves its runway; next #M … : <verdict>` at Debug.
- **Never on runway pavement.** A cut that would end on a runway centreline or between a runway's two bars moves back to the near-side runway bar (`KeepOffRunwayPavement`). That bar keeps its own runway reason, uncleared, and no `RouteIncomplete` point is added there, so the aircraft holds short of the runway, not of X.
- **Not a runway bar.** `HoldingShortPhase.ProtectsARunway` is false for it. The aircraft reports "holding short of X at <taxiway>, request further taxi". `RES` and `CROSS` are rejected with `holding short of X where the route issued ends — issue a TAXI that includes X`, and a new `TAXI` replaces it.
- **Re-armed by `HS X`.** A later `HS X` there turns it into an ordinary explicit hold. `RES` then releases that hold, but the route ends at the hold, so the aircraft does not move until a new `TAXI` names X.
- The point round-trips through `TaxiRoute` and `HoldingShortPhase` snapshots like any other reason.

### Holds that end a no-destination route

`HoldShortPoint.EndsNoDestinationRoute` marks the hold that ends a clearance with no destination at an explicit hold short of its last taxiway: a bare final taxiway (`TAXI C B`, a bare `TAXI B` on C) and an `HS E` target the route would drive only on its way past (`TAXI C HS B`, `TAXI D C HS E RWY 28R`).

The aircraft never stops inside the junction. The cut is described in [pathfinder.md](./pathfinder.md) under "Final named token — junction stop or terminus walk"; a missing link (`ApplyMissingTaxiwayFallbacks`) leaves the flag false.

An `HS E` target driven on the way to a destination is cut too (`GroundCommandHandler.HeldShortOfDrivenTarget` → `HoldShortBeforeTaxiway`): the route ends held short of E, the hold keeps `ExplicitHoldShort` and the route has no `DestinationRunway` hold. A `CTO` given there is an implicit taxi onto E for an intersection departure ([commands](../../COMMANDS.md): "`CTO` while holding short of a taxiway"), not a stored clearance for a runway bar.

- **No release.** The phase refuses `RES`, `CROSS` and every other command it would refuse at a route-incomplete hold with `holding short of X where the route issued ends — issue a TAXI that includes X`, whether the aircraft is holding or still taxiing toward the hold, and a bare `RES` to a rolling aircraft never pre-clears it. Only a new `TAXI` moves the aircraft on.
- **A bare TAXI to the same taxiway is refused**, since it names no way along X and would only end at the same hold: `holding short of B: give a direction (>B or <B) or a destination`, or `holding short of B: B lies behind, give a destination` when no direction onto X is admissible from the hold (OAK C/B, where B runs into C at 14.5°). The refusal is sim-side only and never spoken.
- **Placement.** The stop is the taxiway setback, except that a hold given when the aircraft's nose is already inside the setback (`max(L/2 + 30, wingtip floor)` from X's centreline) and the placed stop lies ahead of it moves to where the aircraft stands (`HoldInsideNoDestinationTerminusSetback`), so it holds there rather than taxiing on into the setback.
- **A rolling aircraft that cannot make it.**

  `GroundCommandHandler.UnableNoDestinationEndHold` runs `MarkUnmakeable` on the hold as an `HS` armed inside the stopping distance does: the hold is flagged `Unable`, the answer is `Unable to hold short of X — stopping` (the TAXI's usual notes follow in brackets) with the pilot readback "unable to hold short of X, stopping", no route readback is built, and the taxi phase moves the stop to the firm-rate stopping distance and brakes at the firm rate (see Unable above).

  An aircraft that stands a few feet short of the route's first node places that stop its firm stopping distance from itself, not from the node, and an aircraft stopping for an unable hold short never speeds up on the first sub-ticks after the TAXI raises its speed cap. A stopped aircraft (2 kt or less) is never unable.

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

  A trailing `#node` pin is where an explicit path ends: an explicit runway hold short before it (`TAXI F C B #808 HS 15`, the ground view's Taxi here with a crossing) is en route, so the route is never cut short of the pin at the crossing's far bar (`RouteMaterialiser.TrailingNodePinIndex`); the aircraft holds at the near bar and continues on a CROSS.
- **Short taxiways land short.** A taxiway that terminates or merges before reaching the standoff hits `DeadEndFallback` and seats the bar at the farthest reachable point (logged as a warning). For an aircraft holding short inbound that bar is the farthest it can hold; for a runway exit it is not where the aircraft is clear.

  After every bar is placed, `GeoJsonParser` runs `DropRedundantFallbackBars`: a fallback bar is removed when every route out of its junction (other than back down the branch) reaches a bar of the same runway within the holding distance — the joining taxiway's marking then serves both (OAK P → J's #374, MIA T8 → S's bar; LAX A4 and SEA D likewise).

  Fallback bars never count as the bar a route reaches, so the verdict does not depend on processing order, and a removed split node's edge goes back to its old place in the edge list so node ids and route choice stay as they were. A route that passes the holding distance without a bar keeps the fallback bar (ATL R3/R7/R11, C, A4, SG2).

  `RunwayCrossingDetectorTests` sweeps every TestData layout for routes that reach a runway without crossing a bar and pins the pre-existing ones (`KnownUnbarredEscapes`) as a ratchet. See `docs/landing-and-runway-exit.md`, "Exits whose bar sits inside the holding distance", for how exits treat the remaining short bars.
- **A standoff bar can coincide with a fillet start and then carries the crossing taxiway's arcs** (KCOS A2's 17R/35L bar at A), so anything that finds "the first node on taxiway X" by its edges stops on the bar.
- **`holdShortDistance` is from centerline, not from the runway edge.** Do not add half-width.

## Verify

```bash
# Every hold-short's cross-track distance from centerline, per exit:
dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --exits <RWY> --json
# Placement path taken per boundary (reuse / walker / interpolate / dead-end):
dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --debug-fillets --dump
```

Regression coverage: `tests/Yaat.Sim.Tests/Data/Airport/HoldShortStandoffTests.cs` pins the rule (by width, length and ILS, the elevation term, calibrated and authored precedence, the 15 ft warning, the taxiway width proxy, the angle term).

`tests/Yaat.Sim.Tests/RunwayCrossingDetectorTests.cs` pins it on real layouts: `DetectRunwayCrossings_Oak_AngledHoldShortsSitOutByTheAngleTerm` (perpendicular bars at S, angled bars at D), `BuildRunwayRectangle_Oak_UsesTheRuleOverAStaleAuthoredValue_ElseTheRule` (OAK 28R authors 225 ft, the rule gives 250 ft), `DetectRunwayCrossings_Oak_StoresTheResolvedStandoff` and `EveryCommittedLayout_NoAngledBarSitsInsideAnotherRunwaysHoldingArea`.

`tests/Yaat.Sim.Tests/Data/Airport/RunwayHoldBarsMarkingTests.cs` pins the painted-segment depth ([runway-fouling.md](../runway-fouling.md)), and `LocalizerCategoryTests` the ILS category read.
