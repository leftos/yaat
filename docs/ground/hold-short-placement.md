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
- **The floor.** The nose stays at least `max(L/2 + 30, floor)` from the crossed taxiway's centreline, measured perpendicular to its straight edges (fillet arcs excluded), never to the bar node.

  `floor` comes from the airport's widest runway as a whole-airport worst case (the layout has no taxiway width or design group): < 75 ft → 49.5, < 100 → 64.5, < 150 → 84, < 200 → 132 (150 ft maps to ADG V: OAK 30 carries MD-11s), ≥ 200 → 156 ft (half the ADG span ceiling + 25 ft; the 25 ft is a judgement call). Placement walks back along the route from where it meets the crossed centreline until the nose clears; the centre is L/2 further back.
- **Caps.** The just-past-runway stop still wins (no floor, no new warning there). Otherwise no taxiway junction behind caps the walk-back: it goes back past any intersection and stops only (`WalkBackLimitCentreFt`) where the aircraft stands along the route (at most the bar's own node), or at a runway hold-short bar the route crossed, the tail staying `RunwayCrossingFarSideGate.MarginFt` (10 ft) clear of that bar (AIM 4-3-21.b).

  Those two limits bound the length setback too, so a stop they put forward of `length + 30` is kept there. A stop the setback would put behind the aircraft is placed where the aircraft stands (`NoFurtherBackThanTheAircraft`; how far back the setback put it is carried from the placement, never found again by projecting the point onto the route, which a route that doubles back passes twice).

  It never moves forward of the old `length + 30` stop, except as those two limits hold it. When the clamp leaves the nose short of the floor, the route carries `holding short of TWY T — wingtip clearance from T not assured (N ft)`; a recompute replaces that warning rather than adding another.
- **Just past a runway.** A taxiway hold short within a fuselage length past a runway the route crosses takes the nose-at-line setback (half a length) and is clamped at the runway hold line, with the tail over the bars when the gap is shorter than the whole fuselage (`holding short of TWY T leaves the tail over RWY 28R — unable to clear the runway`).

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

`RunwayCrossingDetector.MarkShortBars` runs as the last build step (`GeoJsonParser.BuildLayout` step 9, after the fillets have settled every node's position) and sets `GroundNode.IsShortBar` on every `RunwayHoldShort` node whose runway is in the layout: false for the runway's hold line, true for a short fallback bar inside the holding distance. It decides by the bar's cross-track from the runway centerline against the holding distance, within `HoldingDistanceToleranceFt` (`AirportGroundLayout.IsAtHoldingDistanceByCrossTrack`). Every other node reads null.

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

With the nose already at or past the marking of an uncleared **runway** bar on the start node (a TAXI starts the route on a bar's node only once the aircraft is close to it, so this is common), the line is lost. `TaxiingPhase.TryHoldPastStartBar` takes that hold instead: the aircraft brakes at the firm rate as soon as it can, to keep off the runway's pavement, never stopped dead from taxi speed, and holds once it is down to one sub-tick of that braking, wherever that leaves it.

The finding is latched on the bar's node (`TaxiingPhaseDto.PassedStartBarNodeId`), so the stop is not let go part-way as it carries the aircraft away from the node; how the nose is judged past the line is in [navigator.md](./navigator.md) § Stopping at an uncleared bar.

`HoldInsideStoppingDistanceOfBarTests` pins an `HS` that re-arms the start bar, live and after a rewind. A re-route with no `HS` that leaves the aircraft partway across that runway keeps the crossing cleared and finishes it, so it never reaches this stop ([navigator.md](./navigator.md) § A re-route mid-crossing keeps the crossing).

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
- **Short taxiways land short.** A taxiway that terminates or merges before reaching the standoff hits `DeadEndFallback` and seats the bar at the farthest reachable point (logged as a warning). For an aircraft holding short inbound that bar is the farthest it can hold; for a runway exit it is not where the aircraft is clear.

  After every bar is placed, `GeoJsonParser` runs `DropRedundantFallbackBars`: a fallback bar is removed when every route out of its junction (other than back down the branch) reaches a bar of the same runway within the holding distance — the joining taxiway's marking then serves both (OAK P → J's #374, MIA T8 → S's bar; LAX A4 and SEA D likewise).

  Fallback bars never count as the bar a route reaches, so the verdict does not depend on processing order, and a removed split node's edge goes back to its old place in the edge list so node ids and route choice stay as they were. A route that passes the holding distance without a bar keeps the fallback bar (ATL R3/R7/R11, C, A4, SG2).

  `RunwayCrossingDetectorTests` sweeps every TestData layout for routes that reach a runway without crossing a bar and pins the pre-existing ones (`KnownUnbarredEscapes`) as a ratchet. See `docs/landing-and-runway-exit.md`, "Exits whose bar sits inside the holding distance", for how exits treat the remaining short bars.
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
