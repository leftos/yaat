# Taxi Pathfinder — Design & Architecture

> Read this before touching anything under `src/Yaat.Sim/Data/Airport/Pathfinding/`, `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs`, or `src/Yaat.Sim/Commands/GroundCommandHandler.cs`'s taxi-resolution paths. The pathfinder turns a `TAXI`/`TAXIAUTO` command into a `TaxiRoute` (edge sequence + hold-shorts).
>
> It is one of three ground-stack components — see the **fillet generator** (`./fillet-generator.md`) that builds the graph it walks, and the **navigator** (`./navigator.md`) that physically follows the route it produces. Index: `./README.md`.

## Architecture

The **`TaxiPathfinder`** (`src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:10`) is a `public static class` with nine static entry points: `ResolveExplicitPathDetailed`, `ResolveExplicitPath`, `FindRoute`, `FindRouteToNearestGoal`, `FindRampConfinedRoute`, `FindRunwayRoute`, `FindAdjacentRunwayRoute`, `FindRoutes`, and `FindFullLengthLineupHoldShort`. All production callers invoke these directly.

The implementation is unified — no dual-path selector, no variant adapter — and lives under `src/Yaat.Sim/Data/Airport/Pathfinding/`.

**Caveat for any agent working on the pathfinder:** membership-matched junction arcs, V-shaped taxiways, and tighter fillet arcs are *correct-but-different* geometry. When something breaks on the graph, **adapt the pathfinder — do not "fix" the graph** (membership junction arcs are legitimate turn-connectors).

---

## Where it sits & entry points

A `TAXI`/`TAXIAUTO` command reaches the pathfinder through the command pipeline (see [`../command-pipeline.md`](../command-pipeline.md) for the full input → dispatch walk). The ground-specific tail:

1. `GroundCommandHandler.TryTaxi` / `TryTaxiAuto` (`src/Yaat.Sim/Commands/GroundCommandHandler.cs:15`, `:291`) is the dispatch target. `TryTaxiAuto` just builds an empty-path `TaxiCommand` and calls `TryTaxi` — so `TAXIAUTO RWY`/`TAXIAUTO @PARKING` is "a TAXI with no named taxiways". There is no separate auto pathfinder entry.
2. `TryTaxi` resolves the start node from `(position, heading)` with `AirportGroundLayout.PickTaxiStart(position, heading, TaxiStartTraffic)` (`FindTaxiStartNode` is its node), over the other ground aircraft `options.ListAircraft` lists; `FindNearestNode` is the off-graph fallback.

   `FOLLOWG`'s planner falls back to it ([navigator.md](./navigator.md#followg-joining-the-leads-taxi-path)). **The pathfinder's contract is strictly node-to-node**; mid-segment snapping happens here, upstream. The pick takes the first of:
   - at a parking or helipad node (within `AtNodeToleranceFt`, 15 ft): that node, or a non-parking neighbour co-located with it;
   - within 15 ft of no taxi node and on a straight taxi edge (`FindOccupiedTaxiEdge`: the nearest straight edge, within `OnTaxiEdgeMaxOffsetFt` (25 ft) of its centreline, the perpendicular falling inside it): the edge's endpoint ahead, or the nearer endpoint when the heading crosses the edge.

     No distance bound applies, so the start is on the taxiway the aircraft is on however long the edge, and a nearer node on a parallel taxiway (KOAK C/D, KSFO B/A) never takes it;
   - within 15 ft of no taxi node, on no straight edge but on a fillet arc (past a straight edge's end): the arc's end node nearest the heading, never the node it entered the arc by;
   - otherwise the nearest non-parking node within 100 ft that is not behind the aircraft and has a taxi edge aligned with the heading, else the absolute nearest node.

   **A start through a parked aircraft is replaced.** The last bullet's start is a free-space leg.

   When it is an occupied stand's parking node, or its leg passes a parked or held aircraft or the stand one occupies (`RampLaneReposition.FindLegObstacle`), the first clear taxi node within `OccupiedStandStartSearchFt` (300 ft) is the start instead: never a parking, helipad or runway node; nodes ahead of the nose before nodes behind it; then nearest first; and its leg must cross no runway centreline and no non-RAMP taxi edge (`IsClearStartLeg`).

   A stand is occupied only while its aircraft stands within `AtNodeToleranceFt` or half its length of the parking node, so one holding beside its stand after pushback is an obstacle only where it stands. When no node is clear the usual start is kept and the TAXI response ends " (blocked by stand 23, SWA2436)"; the conflict detector's yield label then names the aircraft.

   **The start on an occupied edge is route-aware.** When the aircraft stands mid-way along a straight taxi edge (`AirportGroundLayout.FindMidEdgeTaxiStart`) and the start is one of that edge's ends, `GroundCommandHandler.ResolveTaxiRouteFromStart` resolves the whole clearance (steps 3 and 4 below, `ResolveTaxiRouteFrom`) from either end (`ResolveFromEitherEnd`).

   The route from the endpoint ahead stands unless it resolves nothing or drives back over the occupied edge before it first leaves the edge's end nodes (`DrivesOccupiedEdge`).

   In either case the clearance is re-planned from the far end, and that route wins only when it resolves and does not drive the edge. The endpoint ahead wins every tie: when the far end is no better, the aircraft keeps the route ahead, even one that runs back over its edge.

   That route's segment 0 is the occupied edge driven backwards from the node ahead, so the aircraft reverses where it stands: a **turn about in place** (`KeepRouteAhead` reports `TaxiTurnAboutShape.InPlace`). A controller's TAXI to a type whose gear does not fit a turn about gets the turn-around refusal below instead (see [the approach leg](#approach-leg--a-free-space-drive-from-where-the-aircraft-stands-to-the-routes-first-node-taxiapproachleg)).

   A far-end route that wins always **turns the aircraft about from the far end** (`TaxiTurnAboutShape.FromFarEnd`), whether or not it opens with the approach leg back along the edge to the far node: a route that holds short at the far node, one with a runway centreline between, and the route of a scripted type that does not fit, when the node ahead resolved none, report it too. A type whose gear fits turns about this way on any clearance, at any heading.

   Either shape is reported by `ReportTurnAbout`, which sets `TaxiRoute.TurnAboutShape` and `TaxiRoute.TurnAboutTargetNodeId` (the occupied edge's far node; both snapshotted). `TaxiRoute.PendingTurnAboutShape` / `AircraftGroundOps.TaxiTurnAboutShape` read the shape, and `AircraftGroundOps.TaxiTurnAboutTargetNodeId` the node, until segment 0 is flown; then `TaxiTurnAboutShape.None` and null.

   A far-end route with no leg back (segment 0 starts at the target) ends sooner: `TaxiingPhase.EndTurnAboutAtItsTarget` clears both fields once the aircraft has turned about and reached the target (centre past the line through the target square to segment 0, heading within `StartBarAlignedDeg` of it), since it stays on segment 0 past the target. The clear is snapshotted with the fields.

   **A type whose gear does not fit a turn about refuses a controller's clearance that would turn it about at a reversal** (`GroundCommandHandler.TurnAboutVerdictFor`): its heading 135° or more (`GroundNavigator.ReversalEntryThresholdDeg`) off the bearing to the far end of the edge it stands on. A turn toward the far end of less than 135° is a corner, which any type takes on any clearance.

   An aircraft holding after a pushback (`HoldingAfterPushbackPhase`) or still under tow (`PushbackPhase`) is never refused: a tug kept it on pavement the whole way, so the ramp it was pushed over is pavement to turn on. A bare `PUSH` stays straight back. The Debug line `[TryTaxi] … turn about {Verdict} ({VerdictReason})` names which of these applied.

   The fit is the type's gear fit, `TurnAboutFit.Evaluate`, from its FAA aircraft characteristics record (`FaaAircraftDatabase`): the pivot radius (the main-gear axle midpoint's path at the 65° nose-wheel steering limit, or half the main-gear width MGW when that is larger) is R = max(MGW/2, 0.466 × wheelbase), and the type fits when its outer main tyre (R + MGW/2) and its nose gear (√(R² + wheelbase²)) both stay within half the width of a taxiway of its own taxiway design group.

   Two non-jet cases come first. A wheelbase of 0.9 of the type's length or more is an unusable FAA row (`BrokenRowWheelbaseRatio`), judged by category. A wheelbase of 0.48 to 0.9 of its length is a taildragger (`TaildraggerWheelbaseRatio`; the record marks no tailwheel, and a taildragger's wheelbase runs main gear to tailwheel): it pivots on a braked main wheel, R = MGW/2, and fits when its tail-swing radius √(wheelbase² + (MGW/2)²) plus MGW is within the taxiway's full width.

   A row correction (BT36's main-gear width) comes from `AircraftProfileOverrides.json` (`mainGearWidthFt`, applied by `FaaAircraftDatabase.ApplyOverrides`).

   Those half-widths are AC 150/5300-13B Table 4-2's: 12.5 ft for TDG 1A/1B, 17.5 for 2A/2B, 25 for 3/4, 37.5 for 5/6. A record with a TDG but no main-gear width or wheelbase fits only as a TDG 1A type that is not a jet; a type with no record, or with a TDG outside the table, fits only as a piston or helicopter.

   A C172 and a C208 fit (the C208 pivots on its inner main wheel: R 5.85 ft, outer tyre 11.7 ft, nose 9.7 ft, inside 12.5 ft). A C25A (nose 19.6 ft against TDG 2A's 17.5), an AT76 (TDG 1B but a 35 ft wheelbase), a B738, a DH8D and a CRJ2 do not.

   Where the far-end route would win, the type answers `Unable, no room to turn around on <taxiway>, request a route ahead` (`NoRoomToTurnAroundReason`, spoken through `PilotResponder.BuildUnableNoRoomToTurnAround` as `CommandResult.PilotUnable`) and its state is untouched.

   It refuses this way whether the route from the endpoint ahead reversed over the edge or resolved nothing; when neither end resolves a route, it gives the first attempt's own refusal. A 737/A320 needs about 75–80 ft of pavement to turn 180°, more than its 50 ft design taxiway, so the pilot asks for a route ahead (AIM 4-3-18.b).

   The line sits at the navigator's reversal threshold because a type angled across its edge saves little of a full 180's room (a 150° turn still needs about 93% of it); under 135° the navigator flies a corner, not a turn about.

   **A clearance no controller issued is never refused that way** (`TaxiCoreOptions.ControllerIssued` is false for `TAXIAUTO` and for a scenario preset or AI controller, `DispatchContext.IsScenarioScripted`): no one is there to re-issue it. A type that does not fit keeps the route from the endpoint ahead, at a reversal, and takes the far-end route, turning about, only when the endpoint ahead resolves nothing, so no unattended aircraft is left stuck.

   Both attempts log their failures at Debug, so a first-attempt failure the re-plan recovers never reaches Warning; a refusal is logged once, at Warning.
3. `TryTaxi` picks where the named path starts (`GroundCommandHandler.ResolveFromBestStart`, #455/#457). It tries three starts in order and takes the first that resolves:
   1. **The held-short taxiway.** An aircraft in `HoldingShortPhase` for an explicit taxiway target X (`HeldShortTaxiway`; runway-shaped and `$spot` targets excluded) starts on X, unless the start node already lies on the first cleared taxiway.

      Holding short of K on B, `TAXI A` → `K A`, while `TAXI B T` carries straight on along B. The X leg must join the first cleared taxiway at X's first junction with it, reached from the bar, with no runway hold-short or crossing and no reversal of 150° or more (`HeldShortLegRejection`). A rejected leg falls through to the next start.
   2. **The path as cleared.**
   3. **The current taxiway prepended** (`aircraft.Ground.CurrentTaxiway`), as a fallback. This applies only when the start node lies on it, the path doesn't already begin with it, and it shares a direct junction with the first cleared taxiway (`SharesDirectJunction`). When the two meet only across a runway it is left to the runway-crossing bridge, so the crossing still needs an explicit clearance.

   Before the three starts, `AnchorToFirstClearedTaxiway` moves a start node that is not on the first cleared taxiway to the nearest node on it within 100 ft, when that node is at least as close to the aircraft (a stopped aircraft pushed back onto a parallel taxiway, WJA1521).

   It never does so for a rolling aircraft (above `TaxiingPhase.StoppedGroundSpeedKts`) whose start node lies on its current taxiway (`RollsOnTheStartNodesTaxiway`): at OAK, where B merges into C at 14.5°, a B node beside an aircraft rolling on C would turn a bare `TAXI B` into a drive along B's last segments into the C/B junction.

   A failure reported to the controller is always the as-cleared one. When the as-cleared route bridges along the taxiway the aircraft occupies to reach the first cleared taxiway, that taxiway is exempt from the "taxiing via X — not in the route issued" note for those leading segments. The occupied taxiway reaches `RouteMaterialiser` as `ExplicitPathOptions.OccupiedTaxiway`, set only when the start node lies on it.

   Mid-way along a straight taxi edge the aircraft occupies that edge's taxiway, whatever a stale `CurrentTaxiway` says (`OccupiedTaxiway`; SFO: on F, its current taxiway still E).
4. `TryTaxi` branches on destination kind into `ResolveParkingRoute` (`@parking`/`$spot`) or `ResolveStandardRoute` (runway / implicit endpoint), and computes the aircraft category via `AircraftCategorization.Categorize` and its wake class via `WakeTurbulenceData.WakeClassForType` (the wake class selects which one-way constraints bind, see [One-way taxiways](#one-way-taxiways-per-artcc)).

   A plain `TAXI` naming only a gate or spot from inside the ramp takes neither branch; it goes to the ramp-confined route instead (see [A bare TAXI to a gate or spot from the ramp stays in the ramp](#a-bare-taxi-to-a-gate-or-spot-from-the-ramp-stays-in-the-ramp)).
5. Those resolvers call **`TaxiPathfinder`**'s static methods directly (`src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs`). Every public method takes the aircraft's `WakeTurbulenceData.WakeClass` after its `AircraftCategory`; a caller with no aircraft behind it passes `OneWayResolver.AircraftlessWakeClass` (`Large`).

| Method | Signature | Purpose |
|---|---|---|
| `ResolveExplicitPathDetailed` | `(AirportGroundLayout, int fromNodeId, List<string> taxiwayNames, out PathfindingFailure? failure, ExplicitPathOptions, AircraftCategory, WakeClass)` | `ResolveExplicitPath` with the structured `PathfindingFailure` (kind, offending taxiway, message), so a handler can react to why the path failed. |
| `ResolveExplicitPath` | `(AirportGroundLayout, int fromNodeId, List<string> taxiwayNames, out string? failReason, ExplicitPathOptions, AircraftCategory, WakeClass)` | Controller-named taxiway sequence (explicit mode) → `TaxiRoute` or failure message. Implemented via `SegmentExpander`. |
| `FindRoute` | `(AirportGroundLayout, int fromNodeId, int toNodeId, AircraftCategory, WakeClass)` | Single best node→node route using FewestTurns preference; A* over full layout. |
| `FindRouteToNearestGoal` | `(AirportGroundLayout, int fromNodeId, AircraftCategory, WakeClass, NearestGoalRequest)` | The route to whichever goal node is cheapest to reach, in one A* pass (`AutoRouter.RunToGoals`), under `FindRoute`'s context, cost model and two avoidance passes. The `NearestGoalRequest` carries the goal ids, a forbidden first move, the onward edges a goal must be turnable onto, and the moves never taken. Returns a `GoalRoute` (goal reached, route, search `Cost`); null for an empty goal set, a start not in the layout, or no reachable goal. `FOLLOWG`'s merge search. See [Goal-set search](#goal-set-search-autorouterruntogoals). |
| `FindRampConfinedRoute` | `(AirportGroundLayout, int fromNodeId, int toNodeId, AircraftCategory, WakeClass)` | Node→node route that stays inside the ramp: ramp taxilanes, RAMP and unnamed pavement, at most one implied one-way lane, never a runway holding position (`SegmentExpander.FindRampConfinedRoute`). Null when there is none. |
| `FindRunwayRoute` | `(AirportGroundLayout, GroundNode startNode, string runwayId, AircraftCategory, WakeClass)` | Empty-path runway destination route (`TAXIAUTO 28L`). Tries the runway's hold-shorts in `RouteMaterialiser.OrderFullLengthLineupBars` order (see [Full-length lineup bar order](#full-length-lineup-bar-order)); an aircraft already at the first returns a zero-segment hold there. Otherwise returns the first route that reaches a real destination runway hold-short without traversing that runway surface. |
| `FindAdjacentRunwayRoute` | `(AirportGroundLayout, GroundNode startNode, LatLon position, TrueHeading heading, string runwayId, AircraftCategory, WakeClass)` | Bare `TAXI 28L` (runway only, no taxiways): the aircraft must already be at that runway. Within `AtRunwayHoldShortRadiusFt` (100 ft, any side) of one of its bars → a zero-segment route with one `DestinationRunway` hold-short on that bar; else a straight, single-taxiway, ≤`AdjacentRunwayHoldShortMaxFt` (600 ft) run ahead to a bar; else null and `GroundCommandHandler` refuses the command (issue #393). |
| `FindRoutes` | `(AirportGroundLayout, int fromNodeId, int toNodeId, RoutePreference?, int maxRoutes, IReadOnlySet<string>? authorizedTaxiways, AircraftCategory, WakeClass)` | Up to N distinct node→node routes; when `preference` is null, runs all three strategies (FewestTurns, Shortest, Fastest) and deduplicates. |
| `FindFullLengthLineupHoldShort` | `(AirportGroundLayout, GroundNode startNode, string runwayId, List<GroundNode> holdShortNodes, AircraftCategory, WakeClass)` | Pick the canonical lineup hold-short: the first of `RouteMaterialiser.OrderFullLengthLineupBars`, with `startNode` giving the aircraft's side of the runway; the category and wake class drive the shadow test (`AutoRouter` passes `ctx.Category`/`ctx.WakeClass`, LayoutInspector's `auto-route` Jet/Large). The `RouteMaterialiser` version takes the same parameters. |

So there are **two modes**, both reaching the same internal machinery:

- **Explicit-path mode** — the controller named taxiways (`TAXI A E B B3`). `ResolveExplicitPath` → `SegmentExpander` stitches the declared sequence together and verifies drivability. Authorization is hard: named letter-only taxiways are a boundary.
- **Auto-route mode** — no named path (`TAXIAUTO 28L`, `TAXI @D8`, mid-route reroute, parking extension, `FOLLOWG`'s merge onto the lead's path). A bare `TAXI 28L` is **not** an auto-route: the parser promotes the lone runway to the destination, but `TryTaxi` only honours it via `FindAdjacentRunwayRoute` when the aircraft is already at that runway, and refuses it otherwise (issue #393 — an under-specified clearance must never taxi an aircraft across the airport on a guessed route).

  `FindRoute`/`FindRoutes`/`FindRouteToNearestGoal` → `AutoRouter` A* chooses the taxiways too.

  Empty-path runway destinations use `FindRunwayRoute`: it evaluates runway hold-shorts in the full-length lineup order (below), materializes each as a runway destination, and prefers the first route that stops at a real near-side destination hold-short without traversing the destination runway surface. This prevents the full-length target choice from crossing the departure runway to a geometrically closer opposite-side hold-short.

After the route returns, `GroundCommandHandler` owns everything the pathfinder does **not**: dynamic hold-short stop positions (`HoldShortAnnotator.ComputeHoldShortPositions`), implicit first-crossing clearance (only for a directed `TAXI`: a bare `TAXI` naming just the taxiway the aircraft is on is refused before the route is built, `IsBareTaxiOnOwnTaxiway`), `TaxiRouteAutoCross`, `CROSS`-keyword pre-clearing, runway auto-detection, and phase handoff to `TaxiingPhase`.

The pathfinder produces the *unauthorized-baseline* route; clearance flagging is downstream.

### Output shape

The pathfinder returns `TaxiRoute` (`src/Yaat.Sim/Data/Airport/TaxiRoute.cs`), a `List<TaxiRouteSegment>` (each a `DirectionalEdge` + taxiway name) plus a `List<HoldShortPoint>`, `Warnings`, `ImpliedLanes` (the one-way lanes the resolver implied into the route, uncleared; read when the TAXI is issued and not snapshotted, like `Warnings`), `ClearanceTaxiways` (the taxiways the TAXI clearance named, in order, set by `GroundCommandHandler` once the route is final, null for a `TAXIAUTO` or a route restored without them; snapshotted), optional `DestinationParking`/`DestinationSpot`, and `CurrentSegmentIndex` (init 0).

Consumers are `TaxiingPhase`, `GroundNavigator`, and snapshot/replay via `TaxiRouteDto`. The pathfinder is **idempotent and side-effect-free**: identical `(layout, inputs, category)` → identical route. This is mandatory for recording/replay — routes are snapshotted at TAXI-issue time and must re-resolve identically.

---

## Key design decisions & requirements

### Core principle: unified cost function, no post-walk repair

The pathfinder uses a single cost function (`RouteCostFunction.IncrementalCost`) evaluated at every decision point, with no per-decision scorer hacks or post-walk geometry fixes. This prevents the orbit/spin bugs (e.g., SKW3404 at SFO on route `A E B B3 A B1 Z S`, issue #165) that arose when local greedy choices were made without knowing downstream viability — and when per-node heuristics then tried to repair the wrong choice after the fact.

The design evaluates the full sequence's viability before committing to an edge. Issue #165's regression test `Skw3404_DoesNotOrbitDuringTaxi` (`tests/Yaat.Sim.Tests/Simulation/Issue165SkwTaxiSpinTests.cs:201`) is un-skipped and passes under the current pathfinder.


### Avoided taxiways (per-ARTCC)

An ARTCC can mark taxiways an airport's **auto** routes should avoid via the `avoidTaxiways` section of the unified per-airport sidecar `Data/ARTCCs/{ARTCC}/Airports/{airport}.json` (loaded into `NavigationDatabase.AirportSidecars`; see `Data/ARTCCs/README.md`).

`SearchContext.Compile` resolves the set for `Layout.AirportId` via `AirportSidecars.GetAvoidedTaxiways` and sets `AvoidMode = HardExclude` **only for auto routes** (empty waypoint sequence); an explicit named-taxiway path keeps `AvoidMode = Off`, so `SegmentExpander` and controller `TAXI` commands are never re-routed.

`TaxiPathfinder.FindRoute`/`FindRoutes`/`FindRouteToNearestGoal` run a **two pass** search via `RunWithAvoidance<T>`, which takes the search to run once per pass (`AutoRouter.Run` or `AutoRouter.RunToGoals`), so the single-goal and goal-set searches share one relax:

1. **Pass 1 — hard exclude.** `AutoRouter.RunAstar` skips any edge whose `ResolveTaxiwayName` is in `ctx.AvoidedTaxiways` (the edge is never expanded — a reachability gate, not a cost). If a route is found, it is returned and the avoided taxiway is guaranteed unused.
2. **Pass 2 — soft penalty (fallback).** Only when pass 1 finds no route and the context has a hard gate (`SearchContext.HasHardGates`), the search re-runs on `SearchContext.RelaxHardGates()`, which turns `AvoidMode = HardExclude` into `SoftPenalty` and `OneWayMode = HardExclude` into `Warn` and keeps any mode that is not hard.

   Under `SoftPenalty`, avoided edges are permitted but charged `RouteCostFunction.AvoidedTaxiwayFirstUseCostNm` (5.0 nm-equivalent, first-use only, finite). This keeps a destination reachable only through the avoided taxiway (e.g. a parking spot that hangs off it) resolvable while minimising the avoided mileage.

Exclusion is by the **resolved** taxiway name: a junction arc that *continues along* the avoided taxiway is excluded, while one that merely *crosses* it (continuing another taxiway) is not. When the avoided set is empty or the airport is unconfigured, `AvoidMode = Off`; with no hard one-way gate either (`HasHardGates` false), `RunWithAvoidance` is a single, unchanged search — no second pass, no added cost.

### One-way taxiways (per-ARTCC)

The `oneWayEdges` section of the sidecar declares taxiway spans that may only be taxied one direction (see `Data/ARTCCs/README.md` for the coordinate-polyline authoring form).

`OneWayResolver.Resolve` snaps each authored waypoint to the nearest graph node and converts the constraint into a set of **forbidden directed moves** `(fromId, toId)` — the reverse of each edge along the allowed-direction span (both directions when `block: "both"`). The set is cached per `AirportGroundLayout` via a `ConditionalWeakTable`, so a re-downloaded map re-resolves against its new node ids.

**Wake-class exemptions.** A constraint can exempt wake classes (`OneWayConstraint.ExemptWakeClasses`, from the sidecar's `exemptWakeClasses`), so the forbidden set depends on the aircraft: `OneWayResolver.GetForbiddenMoves(layout, wakeClass)` resolves and caches one set per layout per wake class, skipping every constraint that exempts that class.

`SearchContext.WakeClass` carries the class into every search, and `TaxiPathfinder`'s public methods all take it. A search with no aircraft behind it (the runway-exit walk, LayoutInspector, client route fallbacks) uses `OneWayResolver.AircraftlessWakeClass`, `Large`: a supers-only closure does not bind it, and a constraint binding everyone, or everyone but supers, does.

SFO's Terminal 1 south ramp is the worked case: M1's ramp portion is one-way inbound (NW) with supers exempt, M2 is one-way outbound (SE), and a second M2 entry with `block: both` exempting Small, Large and Heavy keeps supers off M2 entirely, so a super uses M1 both ways.

`OneWayResolver.GetOneWayLaneTaxiways(layout)` names every taxiway carried by any constraint span, whatever its direction or exemptions; these are the airport's **one-way lanes**, the only uncleared taxiways the resolver may imply (see *The destination extension is confined* below, step 2's start leg, and the ramp-confined route).

`SearchContext.Compile` resolves the set for `Layout.AirportId` and picks a mode: `OneWayMode.HardExclude` for **auto routes** (empty waypoint sequence) and `OneWayMode.Warn` for **explicit** named-taxiway paths. Enforcement has two chokepoints, mirroring the avoid/two-pass split:

1. **Hard gate (auto).** `AutoRouter.RunAstar` skips any edge whose `(headNode, nextNode)` move satisfies `ctx.IsForbiddenMove` — true only in `HardExclude`. `RunWithAvoidance` relaxes this to `Warn` on pass 2 when a destination is reachable only against the one-way, so it still resolves (with a warning) rather than failing.
2. **Warn detector (explicit + fallback).** `RouteMaterialiser.BuildWarnings` scans the materialised segments and emits `"Taxiing X against one-way direction"` for any segment whose directed move is in `ctx.ForbiddenOneWayMoves` while `OneWayMode == Warn`. Every route funnels through `Materialise`, so explicit paths (and the auto fallback) are flagged regardless of which `SegmentExpander` search produced the segment — no per-expansion-site wiring needed.

### Geometric correctness is a constraint, not a post-filter

**Fillets tighter than any category's main-gear turn radius are not pavement.** `GeometricAdmissibility.MinSteerableArcRadiusFt` — the smallest category main-gear turn radius (`CategoryPerformance.MainGearTurnRadiusFt`, 10 ft) — rejects every `GroundArc` whose `MinRadiusOfCurvatureFt` is below it, for every category and before the heading gate.

Such arcs are generator noise at cramped junctions (the fillet generator's `RadiusFloorFt` admits them): OAK's RWY 15/33 → D fillet at the F junction has a 6 ft minimum radius, and once fillets stopped carrying the old reverse-arc penalty `TAXI F 33 D` resolved through it instead of the 33 ft of centerline between the two junctions.

The route goes through the junction nodes instead, which the navigator rounds at the main-gear turn radius (OAK 24 / SFO 64 / FLL 7 arcs of ~3–7k each). The test guard `RouteGeometryAsserts` exempts these arcs for the same reason.

A category-aware floor (25 ft jet, 18 turboprop, 15 piston) is the honest next step — the navigator plays a 22 ft fillet for a jet by writing position from the curve, tighter than its gear can track — but reroutes ~9 % of jet fillets at OAK/SFO, so it is tracked in the plan rather than folded in here.

An inadmissible junction is simply not an edge in the search graph from that arrival direction. `GeometricAdmissibility.IsAdmissible` (`src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs`) **hard-rejects** any edge whose heading change from the current arrival bearing exceeds the per-category limit (the turn test itself is `GeometricAdmissibility.AdmitsTurn(arrivalBearing, onward, departureBearing, category)`, which the goal-set search's onward-edge check shares):

| Category | Max heading change |
|---|---|
| Jet | 135° |
| Turboprop | 145° |
| Piston | 155° |
| Helicopter | 175° |

(`CategoryLimits.MaxHeadingChangeDeg` in `src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs:12`.) An arc's traversal direction carries no gate or cost of its own: a fillet is a symmetric curve, its end tangents are direction-aware (`GetDepartureBearing`), and the navigator plays a reversed arc exactly (`PathPrimitiveBuilder.BuildBezier` flips the control points).

A reverse-arc penalty (0.8 nm) used to exist here; since half of all corner traversals run against the arc's stored node order, it made the painted fillet dearer than a square pivot through the junction centre at every other corner (the S2-OAK-2 U→W turn was resolved `694→17→691` and rounded at the main-gear turn radius at 3 kt), so it is gone — `FilletCornerRoutingTests` and `RouteGeometryAsserts` guard the corner routing.

The SKW3404 reverse arc is still caught because its delta is ~180°, excluded regardless of direction.

### Cost function dimensions

All costs are nm-equivalent so the A* heuristic (straight-line distance) stays admissible. Constants are hardcoded in `RouteCostFunction.cs`; calibrate by running OAK/SFO grids and editing in code (no tunable config).

| Component | Weight | Notes |
|---|---|---|
| Segment distance | 1.0 | identity |
| Turn budget | 0.0005 nm/deg | heading change at node |
| Taxiway transition | 0.05 nm | per differently-named-taxiway hop |
| Runway crossing | 0.3 nm | per cross of an unrelated runway's hold-short (skipped for destination) |
| Direction reversal | 0.5 nm | SegmentExpander explicit-mode local searches only |
| Unauthorized taxiway | 0.2 nm | first use of a letter-only taxiway not in authorized set |
| Membership arc continuation | 0.5 nm | taxiway-junction arc ("X - Y") used as continuation vs turn; exempt when it is the intended turn from the previous cleared taxiway (see Caveats, issue #236) |
| Runway centerline | ×10 distance | makes on-runway transit ~10× worse |

**Uncleared along-runway travel is a hard constraint, not just a cost.**

Beyond the ×10 multiplier, `SearchContext` compiles `AllowedCenterlineNames` — runways named as path waypoints plus any runway whose centerline passes through the start node (post-landing / lined-up starts) — and every search caps consecutive centerline pavement on any *other* runway at `MaxUnclearedCenterlineRunNm` (0.15 nm — enough to CROSS a runway whose crossing is stitched through centerline nodes, e.g. MIA taxiway S over 12/30, never enough to back-taxi one).

`AutoRouter.Run` additionally validates the finished path against `HasSameSideCenterlineRun` — a run that enters and exits on the *same side* of the runway is an along-travel shortcut between two exits, not a crossing (OAK: onto 10L at G, off at the adjacent E exit) — and re-runs with that run's centerline moves banned rather than judging it per-expansion (which would poison the `(node, bearing-bucket, taxiway)` closed set with path-dependent dead ends).

There is no soft fallback: a destination unreachable without taxiing along an uncleared runway fails. On a parking/spot destination, `GroundCommandHandler.TryTaxi` then retries with each single named taxiway dropped and keeps the shortest resolution, warning e.g. `[D dropped — no route via D reaches @GA1]` — the S1-OAK-7 `TAXI C D @GA1` case, where D leads away from GA1 and the old resolver satisfied the contradiction by back-taxiing the full length of 10L.

Preference multipliers: `FewestTurns` ×5 on turn + transition; `Shortest` zeroes all non-distance terms.

`Fastest` adds seconds to the nm scalar, priced the way the navigator flies the route: a straight at the category's taxi speed, a fillet along its local cornering-speed profile (`GroundArc.TraversalSeconds`), plus at each corner the speed dip down to the cornering speed and back (`SpeedDipSeconds` = Δv²/(2·v)·(1/a_decel + 1/a_accel)) and, for a straight-to-straight bend sharper than `GroundNavigator.EntryAlignmentThresholdDeg`, the nose-wheel pivot's sweep (turn/ω).

Without the corner terms two free straights through a junction centre out-priced the painted fillet under Fastest. The Fastest term dominates and provides little heuristic guidance, so Fastest searches are slower but still correct.

**Direction-reversal lives in explicit mode only.** Applying it to auto-route A* would break heuristic admissibility — any edge whose bearing is >90° off the start→destination direction would inflate the g-score and cause exponential expansion across the airport (cross-field routes legitimately go "backward" to cross runways). Explicit mode's bounded local searches can safely apply the penalty. Consequence: auto-routes may be more zig-zaggy than explicit ones.

### Authorized-taxiway policy: letter-only = boundary, numbered = free

`SearchContext.IsLetterOnlyTaxiway` classifies by name: a name with **no digit** is letter-only (`A`, `Y`, `F`). The authorized set is the letter-only names the controller named. Numbered names (`A1`, `AY1`, `M1`) are always free for bridging/parking access. Letter-only taxiways the controller did **not** name cost `0.2 nm` on first use and surface a `Warning` — not hard-excluded.

The extension from the last named taxiway to a gate or spot is the exception: there every uncleared movement-area taxiway is hard-excluded (see *The destination extension is confined* below). (Caveat: `IsLetterOnlyTaxiway` currently returns true for `RAMP`, so RAMP edges can attract the unauthorized penalty/warning; the materialiser separately suppresses warnings for leading/trailing parking-bridge RAMP — see `RouteMaterialiser.BuildWarnings`.)

### Hold-short handling & truncation

`RouteMaterialiser.AnnotateHoldShorts` tags every `RunwayHoldShort` node with `RunwayCrossing` (or `ExplicitHoldShort` when the runway is in the command's `HS` list). Multi-runway bars (`28L/28R`) add one point with the full string.

**A runway the controller cleared the aircraft to taxi *along*** (named as a path waypoint, collected into `clearedRunways` from `ctx.WaypointSequence`) is exempt — no hold-short is placed at its boundary, so the aircraft taxis straight onto and along it — **unless the same runway is also in the command's `HS` list**.

Then the explicit hold-short overrides the straight-on entry and arms an `ExplicitHoldShort` bar at every entry-side boundary of that runway on the route (S1-OAK-7 `TAXI F 33 D C B RWY 28R HS 33` silently dropped the HS and rolled onto 15/33 unimpeded before this). A *different* runway the route merely crosses still pairs and annotates as before, and the destination runway (handled first) keeps its terminus stop.

The materialiser also annotates `HS` targets that name a **taxiway** (not a runway): it scans the resolved segments and adds one `ExplicitHoldShort` at the first node adjacent to that taxiway.

A **located** target (`HoldShortTarget.OnTaxiway`, the `C@J` / `28R@J` command form from issue #358) additionally requires the binding node to sit on the location taxiway — for both the taxiway scan and the runway-bar match (`MatchesExplicitHoldShort` takes the node) — so only the crossing the controller named binds when a looping route meets the target more than once.

A **spot** target (`HoldShortTarget.IsSpot`, the `$17` command form from issue #394 — vNAS scenario presets like `TAXI T421 C Z B M1 1L HS $17`) binds the `GroundNodeType.Spot` node with that name when the route passes through it (`IsSpotNode`), never a taxiway edge; its `HoldShortPoint.TargetName` is the canonical `$17` (`HoldShortTarget.MatchKey`) so it can't be mistaken for runway or taxiway `17` anywhere the name is displayed or matched.

The route is then **truncated** by `FindTruncationIndex`, discarding trailing pavement. `IsCleared`/`ClearedByAutoCross` start false (downstream owns them).

**Entry/exit pairing, and the bar the aircraft is already parked on.** For each crossed runway the first
`RunwayHoldShort` node encountered is the crossing's **entry** side and gets the point; the second
distinct node for that runway is the **exit** side and is dropped, so the aircraft never stops on the far
side of a runway it is cleared through. A route can also *begin* on a bar — the common case is an
aircraft holding short of its departure runway that gets re-routed to a different one. `AnnotateStartCrossing`
handles that: `segments[0].FromNodeId` is never any segment's `ToNodeId`, so the main loop cannot see it,
and without this pass the *far* bar becomes the "entry" and the aircraft taxis over the runway to reach
its own hold-short (issue #316 — SFO `TAXI F C HS 10R RWY 28R` sent a departure across 28L with another
aircraft lined up on it). Pairing is decided by `HoldShortAnnotator.RouteCrossesRunwayAfterStart`: the
route must actually pass over the runway between the two bars. It must **not** be decided by taxiway name
— where a crossing point doubles as a taxiway junction the two bars carry different names (SFO 10R/28L:
near bar on `F`, far bar on `C`), which is exactly what the old same-taxiway walk missed. That predicate is
shared with `HoldShortAnnotator.AddImplicitRunwayHoldShorts`, the older twin that annotates appended
parking extensions and runway-exit routes — keep them on one definition, they have drifted before.

Truncation keys hold-shorts on segment `ToNodeId`s **plus** `segments[0].FromNodeId`, so a start-node bar
survives. `TaxiingPhase.TryHoldAtRouteStartNode` takes the stop, since `ArriveAtNode` never fires for a
node the aircraft is already standing on; it binds only while the aircraft is still within
`StartNodeHoldRadiusFt` of that node.

A taxiway `HS` target can be more than a post-hoc annotation — it can also **steer** the route, but only as a fallback.

`GroundCommandHandler.HoldShortTaxiwaysToFold` collects the taxiway-named `HS` targets the path doesn't already name. With a **destination** (runway / parking / spot), `TryTaxi`'s `ResolveRoute` first resolves the clearance **as cleared** and keeps that route when it honors every such target (`AsClearedRejectionReason` returns null).

It honors one when the destination is reached (a runway destination has its `DestinationRunway` stop; a parking/spot route is only non-null when reached), the target is bound (`RouteMaterialiser.FindBoundHoldShort` — the same predicate behind the "HS … not applied" warning), **and the route continues on a cleared taxiway past the bound bar** (`ContinuesOnClearedTaxiwayPast`, exact segment names — a `"C - E"` junction arc is the turn *off* the cleared taxiway, not travel along it).

Only when the as-cleared route fails one of those does `AugmentPathWithHoldShortTaxiways` fold the targets' taxiways into the waypoint sequence and re-resolve.

So SFO `TAXI T7A A A1 1R HS H` (issue #395) stays `T7A A A1` and holds short of H where A crosses it — appending H after A1 sent the route back through A2/M1/A to the 1R bar on H, across 01L/19R twice — while OAK `TAXI D C HS E RWY 28R` still routes `D → C → E`: as cleared, C reaches E but the route ends short of 28R (the runway-destination fallback A* is confined to the cleared taxiways), so the fold applies and E is threaded exactly as if it were named.

The continuation test is what separates the two — merely *touching* the target's taxiway at a junction and then reaching the runway over free numbered pavement is not the cleared route.

Without a destination the `HS` taxiway is the only direction cue, so it is always folded (`TAXI A HS H` ends held short of H on A). A **located** target folds its `OnTaxiway` instead of the target (`HS C@J` folds `J` — the aircraft travels J and holds short *of* C, so C must not join the cleared path).

Runway `HS` targets are never folded (they have no taxiway nodes); they stay in `ExplicitHoldShorts` and match `RunwayHoldShort` nodes only. Spot targets (`HS $17`) are never folded either — `HoldShortTaxiwaysToFold` skips them: a spot is a point the cleared route already passes through, not a taxiway to steer onto; a route that misses it gets the `HS $17 not applied` warning.

A located target also gives the final-taxiway walk its direction. `SegmentExpander.ResolveTerminusBias` already aimed the walk at a runway `HS` bar on the final taxiway (`TAXI B K HS 10R`); a located target whose `OnTaxiway` **is** the final taxiway extends that: a taxiway target biases toward `FindIntersectionNode(final, target)` (the C∩J crossing), a runway target toward its bar on that taxiway.

Without the bias, `TAXI C D J HS C@J` never commits down J at all — the no-destination/no-bias gate stops the route at the D/J transition junction, which is exactly the issue #358 symptom ("taxis C D, holds short J"). The bias bypasses that gate, the walk runs J, and truncation then ends the route one segment past the bound bar.

#### Hold-shorts issued *after* the route is resolved

`HS <target>` and `RES HS <target>` land on an already-resolved, possibly already-cleared route, so they take a different path: `HoldShortAnnotator.PlanExplicitHoldShort` (classify, pure) + `ApplyExplicitHoldShort` (commit). It is the **only** post-hoc writer — `GroundCommandHandler.TryHoldShort` and `TryAddExplicitHoldShorts` both funnel through it, and planning every target before committing any keeps a multi-target `RES HS 28R 09L` atomic.

Because `AnnotateHoldShorts` keeps exactly one `HoldShortPoint` per crossed runway, on the **entry side**, an existing point *is* the authoritative near-side bar — never re-derive it by walking segments for a `RunwayHoldShort` node, which finds the far-side bar once the aircraft is across. Rules:

- An existing point for the target is **re-armed**: `ExplicitHoldShort`, `IsCleared = false`, `ClearedByAutoCross = false`. The clearance is revoked whatever set it (AutoCross via `TaxiRouteAutoCross.Apply`, an earlier `CROSS`, the implicit first-crossing clearance) — the controller's hold-short is the most recent instruction. Re-arming only the `Reason` and leaving `IsCleared` set is what let an aircraft taxi straight across a runway it had just been told to hold short of.
- A bar is **behind** the aircraft only if `IsCleared && segmentIndex < CurrentSegmentIndex`. Testing `IsCleared` first is load-bearing: `TaxiingPhase.BuildResumePhases` bumps `CurrentSegmentIndex` past the bar the aircraft is *stopped at*, so the index alone would read a legitimate hold as "already passed". If every candidate bar is behind, the outcome is `AlreadyEntered` and the command is rejected.
- A `DestinationRunway` point is a **no-op** — re-arming it as a plain `ExplicitHoldShort` would strip the LUAW/CTO departure gate.
- Only when no point exists yet does it walk the remaining segments to add one (runway bar first, then a taxiway edge match; a spot target matches only the named `Spot` node).
- A **located** target (`HS C@J`) filters both steps by node incidence with the location taxiway — existing candidate points off `J` are ignored (so the located command adds/re-arms only the crossing it names), and the segment walk skips nodes not on `J`. With no layout to check incidence against, a located target fails closed (`NotOnRoute`) rather than binding an unverifiable crossing.
- The phase is told (`TaxiingPhase.NotifyHoldShortsChanged`) so the segment in progress is re-aimed at the new bar at once, and a bar inside the aircraft's firm-rate flown stop is flagged `Unable` at dispatch — see [hold-short-placement.md](hold-short-placement.md) § A hold-short armed under a taxi in progress.

**Truncation rules** (`src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:233`):

- **Runway destination → the route ends *exactly* at its lineup hold-short** (the **first** hold-short of the destination runway the route reaches — the near-side bar), with **no `+1` buffer** and overriding every other rule.

  A departure taxis *up to* its runway and holds; it never crosses its own departure runway, so first-match is the lineup. Taking the *last* match instead would extend the route across the runway to the far-side bar of the same crossing (both physical sides share the combined `28R/10L` id) — which gridlocks following traffic.

  Proceeding onto / across the runway is clearance-gated by Line-Up / Crossing phases, never baked into the taxi route. If a graph route reaches a destination runway centerline segment before encountering a typed `RunwayHoldShort`, the materialiser adds a destination hold at the surface-entry node and truncates before the centerline as a fallback guard; `FindRunwayRoute` still prefers candidate routes that terminate at a real typed hold-short.
- **An en-route explicit hold-short crossed before reaching the last cleared taxiway** (`TAXI G C HS 28R`, no further runway destination) stops the route *one segment onto* that last cleared taxiway (`crossHoldTruncateAt` / `FindLastClearedTaxiwayEntry`) — the aircraft crosses and settles just past the junction, not stranded on the near side. This rule is **moot for a runway destination**, where the runway lies beyond the last cleared taxiway and the lineup-terminus rule wins.
- **A single through-taxiway that both crosses AND continues past a cleared (non-destination) runway** (`TAXI G HS 28R` where G runs beyond the crossing, no different taxiway named beyond it) hits `FindPairedFarSideRunwayHoldShortIndex` instead: it extends the route through the crossing to the runway's **far-side (exit) hold-short** (matching the same combined `28R/10L` id), mirroring `TaxiingPhase.FindRunwayCrossingExitNode` (see [navigator.md](navigator.md)).

  Truncating one segment past the near bar instead would strand the aircraft mid-runway between the two hold-short bars once a later `CROSS` clears it.
- **Node / parking / spot destination** → one segment past the destination node (the `+1` buffer gives the navigator somewhere to aim).
- **No destination, last taxiway not driven** (`TAXI C D`, a bare `TAXI B` given on C) → the route is cut before the junction with that taxiway and ends at an explicit hold short of it, not on the junction node. The cut is `TaxiPathfinder.HoldShortOfNoDestinationTerminus`, applied after the materialiser (step 5 below).
- Otherwise → the natural terminus (last segment).

### Runway-destination fallback across crossings (greedy-walk escape hatch)

A departure runway can lie *beyond* one or more runway crossings on the cleared taxiways — e.g. KMIA `RWY 9 TAXI P S HS 12`, where taxiway S crosses runway 12 then hairpins to runway 9's hold-short. The greedy `WalkToNaturalTerminus` (one-step, see Caveats) can dead-end on a hold-area spur one turn short of the lineup bar, where the U-turn back to it fails the admissibility gate, and the route then truncates at the en-route crossing instead of reaching the runway.

When the explicit walk + variant extension do **not** reach a hold-short for the destination runway, `SegmentExpander.Run` runs a last-resort flat A* (`AutoRouter`) from the start to the runway, which explores all branches and reaches the lineup hold-short across the crossings.

From a head that never reached the last named taxiway, the fallback is tried *before* the variant extension (step 6 of the explicit walk) and replaces it when it reaches the bar; SFO `TAXI C 28R` takes C's own full-length 28R bar this way, not the C2 stub 582 ft up the runway. The deferred variant edges apply only if the fallback misses.

The fallback is **hard-constrained to the cleared taxiways**: every letter-only taxiway the controller did not name is added to `AvoidedTaxiways` with `AvoidMode.HardExclude` (numbered connectors and RAMP stay free), so it cannot detour onto an un-named taxiway — it must cross every runway the cleared taxiways cross (e.g. `RWY 30 TAXI C B W HS 28R` crosses 28R then 28L).

It only runs on the failing path, so it never changes a route that already reaches its runway (a deferred variant extension is not yet a route that reaches it). An explicit `HS <rwy>` on a crossed runway is a hold/authorization marker, not a routing terminus.

### Node-reference paths (`TAXI #1124 #352 …`) — the drawn-route contract

The ground draw tool commits a **dense** node list — every node along the previewed route, one `#id`
per segment endpoint (`GroundViewModel.BuildDenseNodeRefPath`). Consecutive pairs are therefore one
edge apart, and that is the contract: the resolver must reproduce the drawn geometry exactly, not
re-derive a route between the nodes.

Three pieces implement it:

1. **`SegmentExpander.ResolveExplicit` routes to waypoint\[0].** `ExpandSegment` dispatches on the
   *next* token, so a leading node-ref would otherwise never be a routing target — the walk would jump
   straight to the second node by whatever path the A\* liked. The node-ref prologue mirrors
   `BridgeStartToTaxiway`: it routes start → waypoint\[0] before `ResolveSequence` runs.
2. **`RouteToSpecificNode` takes the direct edge when there is one** (`TryStepToAdjacentNode`). It
   applies the same `IsBlockedTurnEdge` and `GeometricAdmissibility` gates as the searches — an
   inadmissible drawn turn still falls through to the `AutoRouter` hop — and breaks parallel edges
   (fillet duplicate corner arcs) by `IncrementalCost`. A dense route is then O(N) lookups instead of
   N full-layout A\* runs.
3. **`GroundCommandHandler.TrimPassedNodeRefPrefix` drops the stale head of the list.** The draw tool
   anchors at the aircraft's node when the controller *starts* drawing, so by dispatch time the first
   nodes are usually behind the aircraft. They are unreachable — a taxiing aircraft cannot reverse and
   `IsAdmissible` hard-rejects the ~180° turn — so routing to them makes each hop loop a whole block to
   turn around. Two exact tests, no geometry: drop through the first token equal to the resolved start
   node (the aircraft is standing on it), then drop any further leading token the aircraft has already
   traversed on its live `AssignedTaxiRoute`. A free-space "behind my nose" test must **not** be used:
   a stopped aircraft's heading says nothing about where its next clearance goes, which is exactly why
   `IsAdmissible` exempts the first edge.

**Why the loop happens without (3).** Hop 0 may legally go backwards — `IsAdmissible` returns true
unconditionally while `LastEdge is null`, and only the soft first-hop heading bias resists. That stamps
a backward arrival bearing onto the head, after which *every* forward waypoint needs a reversal the
gate rejects, and `ResolveSequence` resets `VisitedNodeIds` per hop so nothing notices the
concatenation circling. OAK N16390 resolved a 48-node drawn route into 544 segments across ten laps of
`C → J → K → F → C` before this was fixed; the same route now resolves to 44 segments.

**Loud failure.** `GroundCommandHandler.IsPlausibleNodeRefResolution` rejects a *dense* node path (all
tokens nodes, all consecutive pairs edge-adjacent) that resolves to more than `2 × nodes + 20`
segments. Such a path spells out its own geometry, so a resolution several times longer did not follow
it. Sparse or hand-typed node paths carry no size expectation and always pass. Nothing else bounds
route length: `AutoRouter.MaxExpansions` is per hop, and the materialiser has no cycle detection.

### Look-ahead defeats first-match hairpins (bounded to one level)

At a `T_i → T_{i+1}` transition there are usually multiple junction candidates (parallel crossings). Picking the first/closest can strand the route on the wrong leg of a **V-shaped taxiway** (one LineString, two legs meeting at an apex), forcing a hairpin U-turn after the transition. So `SegmentExpander` scores each junction candidate by the **cost of resolving the remaining sequence from it** — a recursive probe.

The recursion is **bounded to one level**: the probe runs with `enableLookahead: false`, which also suppresses the whole-airport detour fallback, so a continuation that *needs* a detour becomes a strong negative signal against the candidate that led there. When there is no meaningful tail (final transition), a geometric anchor heuristic is used instead.

**Final transition into a runway destination is runway-anchored.** When the last named taxiway has a runway destination, the destination runway's own hold-short *on that taxiway* is the de-facto next waypoint. The pathfinder sets the look-ahead anchor to that hold-short, so junction selection steers toward the junction whose taxiway side leads to the runway.

Without it the cheapest (nearest-along-the-previous-taxiway) junction can commit the final walk to the *wrong end* of the taxiway, after which the correct direction fails the U-turn admissibility gate and the route detours the long way around (e.g., OAK `TAXI D J C 33`: C reached at the near junction only continues toward A, forcing a loop via B / 28L-10R / P back to 33 — 131 segments collapsed to ~36 once anchored).

The anchor is null when no hold-short for the runway sits on the taxiway — the runway is then reached via a numbered variant (`TryVariantExtension`, step 6) or, once the whole walk has failed, via a short numbered stub off the taxiway (`TryRunwayConnectorFallback`, below).

**Runway reached through a short numbered stub off the last taxiway.** Some runways have no bar on the taxiway that runs past them: SFO's B passes the south end of 01L/19R, and the 1L bars sit on the M1 and A2 stubs. Controllers clear "…B, runway 1L" and pilots take the stub, so `Run` treats the variant step's `DestinationUnreachable` failure (blaming the last named taxiway) as its cue to re-resolve rather than refuse.

`FindRunwayConnectorsOffTaxiway` collects, from every hold-short bar of the destination runway, the straight non-runway edges' taxiway names, keeping a name only when it is numbered (never a letter-only taxiway or RAMP — an unnamed letter taxiway is a deviation the controller must name; numbered stubs are free, the same policy the connector detour uses), is not the last taxiway, and is not already in the clearance.

Each candidate is then walked from the bar along its own edges (fillet arcs count, so a `B - M1` tangent-cut node qualifies) until a node incident to the last taxiway is reached, capped at `RunwayConnectorStubMaxFt = TaxiPathfinder.AdjacentRunwayHoldShortMaxFt` (600 ft, issue #393's rule that a short run to the bar counts as being *at* the runway).

The walk refuses to expand through another hold-short bar, a runway surface (centreline or `:link` edge), or a node carrying a straight edge of a third taxiway.

Candidates are ordered shortest stub first, name breaking ties.

`TryRunwayConnectorFallback` re-runs `ResolveExplicit` with the candidate appended as the final waypoint — the ordinary `B M1 1L` path, so junction selection, the runway anchor and the honor-named-taxiway check all apply.

It accepts the first route whose `DestinationRunway` hold-short is *the bar the stub led to* (appending a taxiway authorises all of it, and a long one could otherwise route the long way to another bar of the same runway; the designator is checked too, because the hold-short node set is a substring match and `1L` also matches `11L`).

It then inserts `via M1 — B reaches 1L through M1` at the head of `route.Warnings` (on the route; the `with` copy gets its own `ResolutionAdvisories` list so a failed candidate's advisories never ride out on the accepted route).

This is a tolerance for local practice, not a 7110.65 rule: §3-7-2.b tells the controller to issue the route and holding instructions explicitly, and the SFO ATCT SOP's own examples name the stub (`… Z, B1, A`) — the fold keeps a "…B, runway 1L" clearance flowing the way an SFO pilot flies it, while the echo tells the trainee which stub the aircraft took so the omission is visible.

With no candidate the original failure is returned unchanged, so a runway the taxiway genuinely does not serve (`TAXI A F1 B 1R`, `… 28L`) still reports `Taxiway B does not reach runway 1R — specify a connecting taxiway.` The fallback runs before the last-resort constrained A\* would have, and unlike that A\* it honours the cleared sequence (a flat A\* from spot 2 would route `A M1 1L`, skipping F1 and B).

The pilot's spoken readback stays the clearance as issued; only the controller's echo and the resolved route name the stub.

**Final transition into a parking/spot/helipad destination is reach-probed.** The geometric anchor for a parking destination (`ComputeLookaheadPenalty` against the parking node's position) is a coarse binary test — it clears any junction that has *some* toTaxiway edge pointing toward the parking, ignoring whether the aircraft can *admissibly* turn onto that edge from its arrival bearing.

At an airport where the last taxiway meets the previous one at more than one junction and the parking hangs off a RAMP spur (SFO `TAXI B K A @F10`, issue #235), both junctions pass the coarse test, the tie breaks on distance-along-the-previous-taxiway, and the cheaper-to-reach junction can leave the aircraft facing *away* from the gate.

After that the destination-aware terminus search can't reach it without a U-turn and the append-only extension loops the long way around (`D B K A Q1 B D A RAMP @F10`).

So — exactly as the runway case treats the runway hold-short as the de-facto next waypoint — the final transition into a parking/spot/helipad destination replaces the coarse penalty with a **destination-reach probe** (`ProbeParkingReachCost`).

Each junction candidate is scored by the realized cost of resolving the last taxiway to the destination from it (`LocalSearchToJunction` direct-to-dest, else `SelectBestParkingStop` walk+extend, capped at `ProbeStopCandidateCap = 4` stops), returning `TailUnresolvablePenaltyNm` when the destination is not reachable staying on the taxiway from that junction.

The junction from which the gate is admissibly reachable wins (SFO node 46: probe cost ~0.04 nm vs the wrong junction's ~1.4 nm). Bounded to the top level (`enableLookahead`) so it never fires inside a `ProbeTailCost` recursion, and only for the final transition (no meaningful tail) — runway, node, and end-of-taxiway destinations keep the runway anchor / geometric heuristic.

**Intermediate transitions are destination-reach-scored.** The tail probe scores a junction by the cost of *resolving the remaining tail tokens* from it — but "resolved the tail tokens" is not "reached the destination".

At a collapsed multi-way interchange a junction can land the aircraft on the next taxiway facing the wrong way, where the onward turn toward the destination is an inadmissible U-turn: the tail's destination-aware terminus walk then dead-ends *short* of the destination and the probe returns a deceptively cheap cost, so that junction out-scores the one from which the destination is genuinely reachable.

`ProbeTailCost` therefore adds the true remaining reach cost — a bounded `AutoRouter` from the tail terminus to the destination node (capped at `MaxDetourExpansions`, inheriting the tail's arrival bearing) — whenever the tail terminates short of a concrete-node destination (`Parking`/`Spot`/`Helipad`/`Node`).

The reach route is priced with the search's own accumulated cost (`AutoRouter.RunWithCost` — turns, transitions, crossings, the centerline multiplier), not bare distance, because it competes against other candidates' tails that carry every one of those terms: priced by distance alone, the wrong-way junction's way out (a loop back down C, E, across 28R and G) scored 0.2 nm *under* the straight G→D junction once fillets stopped carrying the reverse-arc penalty that had been hiding the gap.

It only *adds* the real remaining cost, so it can only re-rank a junction that pretends to reach the destination below one that actually does — it never distorts an already-correct pick.

(e.g. OAK `TAXI C D @NEW1`: naming C makes G→C an intermediate transition, unlike the destination-anchored final C→D of `TAXI G D @NEW1`; without the reach term the wrong-way-onto-C junction — whose tail dead-ends where turning onto D is a ~185° U-turn — out-scored the correct junction, producing a 91-segment loop across runway 28R before reaching NEW1.)

**Single named taxiway to a ramp-hung gate joins the implied ramp connector.** The reach-probe above only fires at a *junction transition* (≥2 named taxiways). A single-named-taxiway clearance to a parking/spot/helipad that hangs off a numbered ramp connector (SFO `TAXI B @F1`: F1 is on a RAMP reached from B via connector T9) has no transition, so `ExpandLastWaypoint` relies on `SelectBestParkingStop`.

Two failures compounded there: (1) its stop-node candidates were the taxiway nodes *nearest the gate by straight line*, but a ramp connector can loop away from the gate before curving back — so the B/T9 junction was ~11th-nearest and crowded out by ten dead-end B nodes, leaving no reachable stop → the greedy terminus walk went the wrong way and the append-only extension looped cross-field (`B A L C Z B1 Q A T9 RAMP`, 232 segments).

(2) even once the connector junction is a candidate, the unrestricted extension takes the geometrically-shorter path that threads *uncleared* letter taxiways (`B Q A T9 RAMP`).

Fixed on both fronts: `SelectBestParkingStop` seeds its candidate pool with **one junction per distinct numbered-connector / RAMP name** off the cleared taxiway (`RampConnectorNames`, guaranteed in regardless of the straight-line cap), and `ExtendToDestination` **confines the extension to the cleared taxiways, the ramp taxilanes and RAMP** (below). The join stays on the cleared taxiway until a ramp lane branches off (`B T9 RAMP`, ~1.1 nm) instead of threading the movement area.

**A stop on the named taxiway must drive it (issue #454).** The candidate pool above includes the junction where the route arrived on the last taxiway, because that node carries the taxiway's edges. A stop reached only through that junction — by a junction arc, or no edge at all — never drives the taxiway the controller named, and SFO `TAXI B K A T5A @D1` resolved as "B K A T5B": the A/T5A junction counted as a stop on T5A and the extension ran down the sibling lane T5B.

So `SelectBestParkingStop` keeps a candidate only when the walk to it drives a **straight edge** of the named taxiway, or the extension from it stays on apron and cleared pavement (`ExtensionStaysOnClearedOrApron`: every name is RAMP, blank, the destination's own name, or a taxiway of the clearance). SFO `TAXI B K A @F10` still stops at the K/A junction, since F10 hangs straight off it by RAMP. A skipped candidate is logged as `[beststop] … skipped` on `ExplicitPathOptions.DiagnosticLog`.

**The destination extension is confined, with one implied lead-in (issue #461).** `ExtendToDestination` hard-excludes every movement-area taxiway the clearance did not name (`UnclearedMovementAreaTaxiways`: not in the waypoint sequence, not RAMP, not a ramp taxilane by `MovementAreaClassification`, not the destination's own name, never a runway name), computed once per search.

There is no unconstrained retry. When the confined search fails, `TryExtendViaImpliedLeadIn` admits one uncleared taxiway X from the apron around the destination (`LeadInCandidates`: the nodes reached from the stand along RAMP and its own lane).

Each X is tried with every other uncleared taxiway still excluded, and a route counts only when `ImpliedDestinationLeadIn` names X on it: X is the only uncleared name on the last non-apron edge, only apron follows it, and the edges on X total at most `SegmentExpander.MaxImpliedLeadInFt` (1,000 ft, a leniency with no FAA figure behind it).

OAK `TAXI G @SIG1` drives 635 ft of D this way. A lead-in route that reaches a runway holding position or runway pavement is refused: a lead-in is never implied across a runway's holding position (SFO spot 35 sits 7 ft past the 01L bar on M1).

When no lead-in qualifies either, `TryExtendViaOneWayLane` admits one **implied one-way lane**: an uncleared taxiway that is one of the airport's one-way lanes (`OneWayResolver.GetOneWayLaneTaxiways`), tried with every other uncleared taxiway still excluded.

Since the aircraft's forbidden one-way moves are hard-excluded, the lane that works is the one the one-way rules leave it: SFO `TAXI T A @B2` enters the Terminal 1 ramp on M1, because M2 is one-way outbound. A lane route reaching a runway holding position or runway pavement is refused (M1 runs on past the ramp to the 01L bar), and a lane has no length cap.

The best route by `IsBetterRoute` wins; the lane is recorded on `TaxiRoute.ImpliedLanes` with the warning `M1 not in clearance` (`RouteMaterialiser.NotInClearanceWarning`), and the readback names it. The extension is the pathfinder's own choice, not the controller's, so it always runs with one-way constraints in `HardExclude`, even when the named legs before it run in `Warn`.

When nothing qualifies the extension fails, and `GroundCommandHandler` holds the aircraft short of the missing taxiway instead (see [Incomplete clearances](#incomplete-clearances--the-route-incomplete-hold-issue-461) below). `IsUnclearedMovementAreaName` is the one predicate the extension, the readback warning and that fallback share.

### Mandatory-connector insertion + notification

When two consecutive cleared taxiways have **no direct junction** (zero junction candidates), the resolver bridges them and records a `ConnectorInsertion`.

The bridge is a bounded A* (capped at `MaxDetourExpansions = 5_000`) that **inherits the authorized set** (soft policy): numbered connectors and RAMP are free, an unnamed letter taxiway carries the `0.2 nm` unauthorized penalty so it loses to a numbered connector when ranking. `PickDetourEntry` ranks only the bridges the bridge rules allow (next paragraphs).

The materialiser surfaces the inserted connector as an informative notification — *"A and B do not connect directly — taxi via A1"* — **not** an "unauthorized taxiway" warning, and suppresses the generic warning for those connector names. A runway centreline is never a connector: `RecordConnectorInsertion` skips it, so the notice never reads "taxi via" a runway.

**A bridge never runs along a runway, and between two named taxiways it silently uses only a digit-named stub or RAMP.**

A candidate bridge is dropped when it drives a runway centreline edge, turns at a centreline node of a runway the clearance does not name, or drives an arc ending on a centreline node (the turn onto the runway), however short the run; the diagnostic says `runs along`, `turns at` or `turns onto` the runway. The only allowance is up to `MaxStartRunwayExitFt` (150 ft) along the runway the aircraft stands on, to leave it.

A turn at an uncleared runway (`SearchContext.IsTurnAtUnclearedRunway`) is an arc whose corner is that node, as the C3/A3 fillet on KCOS 17R/35L, or leaving the node other than straight across (`SegmentExpander.IsStraightRunwayCrossing`): in on one side of the runway line and out on the other, neither edge along it, the route bending at most `MaxStraightCrossingTurnDeg` (45°, a sim convention: OAK C over the 33 end bends 0.3°, SFO H's own bend over 1L/19R 25°, KCOS C3 onto A3 64°).

A high-speed exit in and another taxiway out is a runway entry or reversal.

A straight crossing, whether or not the taxiway changes name at the runway as SFO Z1 does onto S3 at #662 on 10R/28L, is the route's own runway crossing and is kept, and so is leaving the centreline node by a stitched `RWY…:link` crossing edge whatever the arrival angle (SFO H reaches 1R/19L's node #358 at 25° and crosses by the link onto GL).

Between two named taxiways it is also dropped once its connector (the part from `fromTaxiway` on) drives a letter-only taxiway (no digit) that is not a ramp taxilane (`MovementAreaClassification.IsRampTaxilane`) and that the clearance left out, so the connector is silent only through digit-named stubs, RAMP, non-movement taxilanes and taxiways the clearance names.

These count as named (`CountsAsNamed`): the transition's two ends, the first mention of a taxiway in the clearance before the transition's second taxiway (taxiways are flown in clearance order, so KCOS `TAXI C2 A2 A` may not bridge C2 to A2 along A; an adjacent one in the clearance wins), and the pair's curated `implicitConnectors` connector.

On the clearance's first transition only, the taxiway the aircraft occupies (`SearchContext.OccupiedTaxiway`) and every taxiway an edge at the route's start node carries (`StartIsOnTaxiway`).

The first transition is decided by its index (`BridgeRulesFor`'s `transitionIndex` 0), not by the taxiway's name, so a lead taxiway repeated later in the clearance gets no exemption (OAK `TAXI F K J C W` from C refuses).

**Every bridge rule is one judgement, `SearchContext.BridgeMoveRefusal`.** It takes the move and the inputs `BridgeRules` (start runway, `fromTaxiway`, whether the head is on it, the excluded letter taxiways; built by `BridgeRulesFor`) and `BridgeProgress` (the edge the bridge arrives on, feet driven along the start runway, whether the connector has begun), and returns why a move is refused or null.

The A* search calls it per move, carrying the progress on `PartialRoute.BridgeProgress` and keying its closed set on `(node, bearing bucket, taxiway, bridge progress)` in a bridge search, so two arrivals that differ in what the bridge has driven are not merged. The post-hoc filter (`BridgeDropReason`) calls it over a finished bridge's edges with the same inputs, so the two agree on every bridge.

The restricted search runs first; when it finds no bridge an unrestricted pass runs only to classify the refusal, and a bridge only that pass finds is refused with a Warning in the server log naming the search and filter verdicts.

The connector is judged from `fromTaxiway` on. An aircraft that has not reached `fromTaxiway` yet (on a stand or ramp), and the occupied taxiway the handler prepended in front of a clearance that did not name it first (`ExplicitPathOptions.ImpliedLeadTaxiway`, set by `GroundCommandHandler.StartTaxiwaysOf`), is on its start leg, which is noted as a taxiway not in the route issued, never a connector. A node-reference transition obeys the runway rule alone.

The ramp-lane recoveries (`RampLaneReposition`) get no letter allowance: the remainder they re-resolve is judged as any clearance is.

**When no legal bridge exists for two named taxiways, the clearance is refused.** `TryDetour` fails with a pair refusal (`PathfindingFailure.RefusesPair`, carrying `UnconnectedFrom`/`UnconnectedTo`) when bridges existed and all were dropped, or the restricted search found none and the unrestricted pass classified the refusal; with no bridge at all it stays `TransitionInfeasible`. `PairRefusal` picks the kind.

`FailureKind.TaxiwaysDoNotConnect` when the two taxiways share no node of the layout: *"Unable, {A} and {B} do not connect, request taxi instructions"* (`SegmentExpander.TaxiwaysDoNotConnectReason`).

`FailureKind.NoRouteAhead` when they do share nodes but the walk reached none ahead: *"Unable, no route from {A} onto {B} ahead, request taxi instructions"* (`NoRouteAheadReason`), with *"(intersection only directly behind, would require turn-about)"* appended when every junction lies more than 90° off the aircraft's pose (`PathfindingFailure.IntersectionOnlyBehind`; with no pose no junction counts as behind, and the dead-end form has no parenthesis).

`FailureKind.NoRoomToClearRunway` when a bare crossing's final taxiway has no room to end clear of the runway (see *Bare crossing end* below): *"Unable, no room on {twy} to clear {rwy}, request taxi instructions"* (`NoRoomToClearRunwayReason`; `PathfindingFailure.UnclearedRunway` names the runway). Like the others it refuses the pair as issued.

`FailureKind.CannotMakeTurn` is the third pair refusal, raised by the declared-crossing path (below).

`GroundCommandHandler.PairRefused` returns whichever it is as the refusal before any recovery can rewrite the clearance, for a controller-issued, scripted and `TAXIAUTO` taxi alike: the aircraft stays put, the pilot says `PilotResponder.BuildUnableTaxiwaysDoNotConnect`, `BuildUnableNoRouteAhead` or `BuildUnableCannotMakeTurn` (wording in [`pilot-phraseology.md`](../pilot-phraseology.md)), and the server log gets a `[TryTaxi] {Callsign}: TAXI {Taxi} refused` entry with the reason.

`ExplicitPathOptions.DiagnosticLog` gets `[detour] drops A→B bridge via #n: runs along RWY17R/35L` (or `turns at RWY17R/35L (#14 onto A3)`, `turns onto RWY17R/35L`) or `needs C, not in the clearance` per dropped bridge, and `[detour] judged …` lines with the search and filter verdicts.

**Two taxiways that meet only across a runway are one runway crossing, tried before the detour.** In `RouteNamedToNamed`'s zero-candidate branch `TryRouteAcrossOffsetRunway` runs first, so a clearance like OAK `TAXI C D F @OLD1 HS 33` (D and F both join 15/33, from opposite sides, 100 ft apart along it) routes D, across 15/33, onto F instead of taking a connector detour that silently dropped D.

`FindOffsetRunwayCrossing` finds the runway: one whose centreline `fromTaxiway` joins, with a join of `fromTaxiway` and a join of `toTaxiway` at most `MaxOffsetRunwayCrossingFt = 300` apart along it (a sim convention of about twice a runway's width; no FAA figure) and on **opposite sides** of the runway line (`SideOfRunway`).

A join is a `RunwayJoin`, a (taxiway node `Via`, centreline node `Join`) pair. A **direct join** is one node (`Via == Join`, the taxiway edge meets the centreline). A **stitched join** (`StitchedRunwayJoins`) is a taxiway end node on the runway pavement joined to a centreline node by a `RWY…:link` edge (CMH E4 and F3 over 10L/28R), and the route hops that link on and off the centreline.

The 300 ft is measured along the centreline between the two centreline nodes, never link to link; side (`SideOfRunway`) and obliqueness (`MeetsRunwayObliquely`) are read on each taxiway's own edge at its own `Via` node.

The closest qualifying pair wins, and the answer (null included) is cached per `SearchContext` and taxiway pair (`OffsetRunwayCrossingCache`), since every look-ahead probe of a zero-junction transition asks again.

A pair never qualifies when:

- **Same side.** Both joins on one side is taxiing along the runway, which the controller clears by naming it.
- **No side.** A taxiway that runs through the runway or lies within 5° of its line is on neither side.
- **Rule A2: a square crossing already exists.** A straight non-centreline edge leaves either join's centreline node toward the opposite side (`HasStraightTaxiwayToward`; a stitched join's own link does not count), so a taxiway continues across the runway from that join and is the way over (SFO C3/S2 over 28R, where S1 crosses straight from C3's join).
- **Rule B1: oblique join.** A straight edge of either taxiway meets the runway less than 60° off its line (`MeetsRunwayObliquely`): a high-speed exit, whose use is a reversal onto or off the runway, never a crossing (7110.65 §3-7-2.c NOTE; KCOS A2/C2 over 17R/35L, C2 30° off: a bare `TAXI A2 C2` is refused as not connecting, and `TAXI A2 17R C2 C` names the runway and reads back "Taxi via A2 on 17R C2 C"). The 60° is a sim convention.

  YAAT ships no declared pair; an ARTCC's airport sidecar may declare one (`crossingPairs`, e.g. `{ "taxiways": ["A2", "C2"], "runway": "17R/35L" }`; `CrossingPair.Matches`, resolved into `SearchContext.CrossingPairs`), and that pair across that runway alone is exempt from this rule; every other rule still holds.

  The declared crossing runs along the runway centreline between the two joins and ends in a turn onto the second taxiway, which `TurnOntoBeyondLimit` checks against `CategoryLimits.MaxHeadingChangeDeg` for the aircraft's type: KCOS A2 onto C2 is a 150° reversal, so a B738 gets `FailureKind.CannotMakeTurn` (*"Unable, can't make the turn from A2 onto C2, request taxi instructions"*, `CannotMakeTurnReason`) while a C172 crosses. For a direct pair the turn is measured from the bearing the centreline run arrives on; for a stitched pair, which drives straight from the first taxiway onto the second with the centreline run only standing in for that, it is the first taxiway's heading at its own end onto the second's (CMH E4 onto F3, 3°).

  KCOS ships no `crossingPairs` entry, so a bare `TAXI A2 C2` is refused as not connecting for every type.
- **Longer offset.** The joins are further apart than 300 ft.

A runway token as either end of the pair is skipped.

The route is the two legs of the explicit `X RWY Y` form, but each is a **pinned local search** (`LocalSearchToJunction`) to the join node the check found: along fromTaxiway to its `Via` node, across a stitched join's link onto the centreline, along the runway's centreline to toTaxiway's centreline node, and across that join's link (`AcrossLink`) onto toTaxiway's `Via` node, where the next transition's search starts. A direct join has no link hop.

Neither leg can detour or depends on look-ahead, so a probe resolves the crossing exactly as the top level does. The centreline run actually walked must also stay within 300 ft (`CenterlineLengthFt`: centreline edges only, so fillet arcs and taxiway edges do not count).

The runway never enters the clearance's waypoint sequence. The materialiser therefore reads a crossing, not a runway cleared to taxi along: the route holds short at X's bar (with or without `HS`), and a later `CROSS` carries the aircraft across onto Y without stopping.

The step runs at the top level and inside a look-ahead probe alike (it is tried before the `!enableLookahead` suppression, so a continuation that needs the crossing is not a negative signal). It returns null, leaving the detour or `DetourSuppressedFailure` as before, when no runway qualifies or either leg fails.

`ExplicitPathOptions.DiagnosticLog` gets `[runway-crossing] X→Y across RWYnn/nn #a→#b offset=Nft` (the centreline nodes) when a crossing is chosen, and `[runway-crossing] X→Y turn D.D° onto Y at #n` on every attempt that reaches the turn check. A rejected crossing logs `[runway-crossing] X→RWYnn/nn failed: no route along X to #a` (the onto leg), `RWYnn/nn→Y failed: no route along RWYnn/nn to #b` (the off leg), or `RWYnn/nn→Y failed: centreline run Nft` (over 300 ft).

`TaxiRouteFormatter.TaxiwayLegs` drops a runway leg the route only crosses (`IsCrossingLeg`), so every readback and route summary reads OAK as `E C D F HS 15/33 at D @OLD1`, never "on 33". A stitched crossing's `RWY…:link` edges carry their runway's name (`RunwayCenterlineName` strips `:link`), so link, centreline and link read as one crossing leg, never "on 10L", "on 28R" or ":link"; the side test of the bars uses the centreline run between the links.

A leg counts as a crossing only when all hold: its centreline run is at most `MaxOffsetRunwayCrossingFt` (measured with the expander's own `CenterlineLengthFt`, so a far-side fillet the walk keeps under the runway's name does not count); the last hold-short bar on the previous leg and the first bar on the next leg are both this runway's, each scan bounded to those neighbouring legs' own segments; the entry bar carries one of the route's hold-short points; and the two bars stand on opposite sides of the leg.

A runway cleared to taxi along keeps "on (runway)" even with an explicit entry hold short (`TAXI A 28L B HS 28L`), as does a leg that leaves on the side it entered from.

**The bridge's landing node is ranked by pavement cost + a reversal charge, not by bridge length.** `PickDetourEntry` runs the bounded A\* once per candidate node on the far taxiway and scores each resulting bridge as `BridgePavementCost` + `ReversalAgainstPoseCost`.

The pavement cost is the cost function's own view of the pavement the bridge threads — its length, a runway centreline ten times over (`RunwayCenterlineDistanceMultiplier`), and the `0.2 nm` `UnauthorizedTaxiwayFirstUseCostNm` for each unnamed letter taxiway it uses (first use only) — and *deliberately not* the turn budget, the per-transition charge or the per-crossing charge.

Those count junction geometry rather than route quality and outweigh thousands of feet of taxiing (with them, OAK `TAXI F 33 D C B RWY 28R` prices a 4,015 ft bridge down D and along 15/33 under the correct 2,810 ft ramp bridge, and the route that follows can no longer make the turn onto D).

The unauthorized term has to stay in: without it SFO's Y→A bridge takes taxiway H, which the controller never cleared, over the AY3 connector.

`ReversalAgainstPoseCost` adds `DirectionReversalCostNm` when the bridge's first edge is more than `CategoryLimits.MaxHeadingChangeDeg` (135° for a jet) off the aircraft's pose — the prior segment's arrival bearing, or `StartHeadingTrue` when the bridge starts where the aircraft stands — because a bridge's first edge is the search's own first edge, where the turn-budget term is skipped and an about-face would otherwise be free.

The motivating case is SFO taxilane Yankee: an E75L resting on Y facing 208° after `PUSH Y A1` has AY2 261 ft *behind* its nose and AY3 1,383 ft ahead, and on raw bridge length `TAXI Y A A1 1R` began with a 180° about-face in a ramp alley; charged for the turn-around, the connector ahead wins (`Y, Y - AY3, AY3, A, A - A1, A1`, first segment departing on the nose).

With no pose to rank by — a route resolved from a bare node with neither a prior edge nor a start heading — the order is the shortest bridge, as before.

One do-no-harm guard rides on top: when the bridge starts *off* the from-taxiway (it is how the route gets onto it), the ranked pick does not reach that taxiway and the shortest bridge does, the shortest bridge is kept — SFO gate G3's `TAXI A Q B F 28L HS 1L` would otherwise arrive on Q having never touched A and the honor-named-taxiway check would refuse the clearance.

It is only that, never "always prefer a candidate that reaches the from-taxiway": where *no* bridge reaches it the clearance is one the resolver is meant to fail (gate B20S's `TAXI M5 …` across the ramp), and that failure is exactly the cue issue #396's ramp-lane free-space cut keys on.

**Curated connectors are threaded as named tokens and preferred over a blind detour.** The blind bridge above minimizes cost with no notion of *the* real connector, so it can pick a numbered ramp lane (which escapes the letter penalty) over the physically-correct letter connector.

For pairs where a specific taxiway is the real-world bridge, curate it per-airport in the ARTCC sidecar as an `ImplicitConnectorEntry { connector, between }` (`src/Yaat.Sim/Data/ARTCCs/<id>/Airports/<icao>.json`) — SFO `L`/`F` bridge via `LF`, and `A`/`B1` (B1 is a stub off `Q`) bridge via `Q`. It is authorized only when the controller clears the two `between` taxiways adjacent.

`SegmentExpander.Run` resolves the clearance both as-cleared and with each applicable connector threaded in (`BuildConnectorVariants`), then keeps the route that honors the clearance with the **fewest mandatory blind-detour insertions** (`IsBetterRoute`; `TaxiRoute.MandatoryConnectorCount`, distance breaks ties).

So `TAXI A B1` on a map where A/B1 don't meet routes `A Q B1` (0 insertions) instead of a blind bypass that silently drops A and B1 — the bug where UAL194 at spot T9 got `T9 B K A F` because the blind detour preferred the numbered `T9` over the letter `Q`. The curated connector is inert when the pair connects directly (the as-cleared route then wins on distance).

---

## Step-by-step walkthrough — explicit mode (`SegmentExpander.Run`)

`SegmentExpander.Run` (`src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:65`) returns exactly one of `(TaxiRoute, null)` or `(null, PathfindingFailure)`.

**1. Waypoint resolution.** `ResolveWaypoints` turns each token into a `WaypointToken`. `#NNNN` tokens become node-refs (`IsNodeRef = true`) — see **Node-reference paths** above for how they resolve; everything else is a named taxiway.

A token that resolves to a runway — `AirportGroundLayout.TryGetRunwayCenterlineName("28R", …)` returns the canonical centerline edge name `RWY28R/10L` — is **rewritten to that canonical name** and flagged `IsRunway`, so the rest of the name-keyed walk routes *along the runway centerline* exactly as it would along a taxiway (the centerline straight edges are indexed under the `RWY…` name, and runway-taxiway crossings are direct shared nodes).

This is how `TAXI 28R G D` taxis along 28R then turns off onto G, D. A turn glyph on a runway token is dropped (travel direction along the runway is fixed by the adjacent waypoints). If a runway and an adjacent taxiway have no direct junction, `TryDetour` fails cleanly ("Taxiway W does not intersect runway 28R") rather than fabricating a connector onto the runway surface — you cannot bridge onto a runway.

The search starts with `head = PartialRoute.StartAt(ctx.StartNodeId)` — an immutable linked-list node (`PartialRoute.cs`) carrying head node id, arrival bearing, last edge, accumulated cost, depth, and a `VisitedNodeIds` set.

**2. Parking→taxiway bridge** (`BridgeStartToTaxiway`, `:1241`, called from `:325`). If the start node has no edge on the first named taxiway (e.g. parked on a RAMP-only spot), a bounded BFS (≤ `MaxBridgeHops = 3`) collects every node within reach that carries that taxiway, then picks the best access node. Without it the first per-segment search finds no on-taxiway edge from the start and degrades to a failing detour.

**Admissible bridges first.** `PickBridgeCandidate` builds each candidate's path with `BuildBridgePath` and prefers a bridge whose every hop is an admissible turn for the type (`GeometricAdmissibility`), the first hop judged against the start heading; a parking or helipad start is exempt on the first hop.

The strict pass searches at 3 hops, then at 6 (`MaxBridgeHopsDeep`) when every shallow bridge onto the taxiway is inadmissible (`[bridge] strict pass: deepened to 6 hops: every shallow bridge onto {taxiway} is inadmissible`). A candidate rejected for a first hop that turns back logs `[bridge] rejected {start} → {target}: first hop {from} → {to} turns back`, and one rejected for a later hop logs `[bridge] {strict|lenient} pass: rejected {start} → {target}: inadmissible turn onto {from} → {to}`.

Only when no admissible bridge exists does the lenient pass run: it allows a first hop that turns back, while every later hop must still be admissible (`[bridge] lenient pass: start={id} → {id} onto {taxiway}: no strict bridge is admissible`). SFO spot 10 `TAXI A` routes without T9 (`SfoSpot10JoinATests`).

**Known limits of the candidate search (YAAT-862, YAAT-863).** The search ignores heading and keeps one parent per node, so an admissible path to the same access node can be hidden by an inadmissible one found first. With no bias the pick is the nearest by cost, so a dead end can outrank a continuation. A node whose only edges on the taxiway are fillets counts as an access node. A turn-back bridge from a node start is not gated by `TurnAboutFit`.

**Adaptive deepening (issue #396):** when every access node within the shallow reach is a dead end — no admissible onward edge on the taxiway from the bridged arrival bearing (`AdmissibleOnwardNeighbors` is empty, the same test that earns `BridgeNoOnwardPenaltyNm`) — the BFS is re-run at `MaxBridgeHopsDeep = 6` and a candidate *with* a continuation wins if one exists.

SFO gate B4 → `TAXI M3 M2 …`: the only M3 node within three hops is a corner-arc entry whose sole M3 edge is a 180° turn, while the M3/RAMP junction sits four hops down the straight gate lead-out; the shallow pick committed the route to the U-turn and the M3→M2 segment then failed "transition infeasible".

Gates whose shallow bridge already has a continuation never reach the deep pass, so their routes are unchanged; a start with *no* access node within three hops still falls through to the per-segment detour as before. **Candidate scoring is A\* `f = g + h`:** the bridge path's own accumulated cost `g` (distance + turn budget) **plus** `ScoreBridgeCandidate`'s bias-proximity probe `h` (how close the candidate's on-taxiway continuation gets to the next-junction / destination bias).

Scoring on `h` alone let a spot whose only link onto the taxiway was a doubling-back ramp cross-connector win purely because its taxiway *entry* sat nearest the bias — the OAK GA9 `TAXI D C B` spin (issue #240), where the aircraft zigzagged through a 19 ft / 3 kt fillet before reaching D. Folding `g` back in keeps the smooth, shorter straight bridge ahead of the zigzag with no magic constant (the turn cost dwarfs the few-foot bias-proximity edge).

With no bias (single-taxiway with no known destination node) the score falls back to nearest-access `g`.

   **Start leg.** When the bridge reaches no node of the first cleared taxiway (and the start is not already on it, and it is not a runway), and the clearance fails without it, `ResolveExplicit` re-resolves the clearance behind an auto-routed **start leg** (`TryAutoRoutedStartLeg`). SFO: pushed off B2 onto M5, `TAXI A` has no A node within the bridge's hops.

   Movement on a nonmovement area is the pilot's responsibility (7110.65 §3-7-2 NOTE 2), so the leg is auto-routed, one-way lanes hard-excluded, to the nearest node carrying a straight edge of the first taxiway, over nonmovement pavement (ramp taxilanes, RAMP, unnamed edges) and that taxiway alone.

   Failing that, it may imply one one-way lane under the extension's rule (a lane the clearance does not name, every other movement-area taxiway still excluded), recorded in `ImpliedLanes` with its `not in clearance` warning.

   A B738 off B2 leaves along M2, a ramp taxilane, with no advisory; a super, for which M2 is closed, leaves on M1 with `M1 not in clearance`. A leg that reaches a runway holding position or runway pavement, or is longer than `SegmentExpander.MaxStartLegFt` (4,000 ft, a sanity bound with no FAA figure behind it; SFO B2's push point on M5 out to A over M2 is about 3,000 ft), is refused.

   Candidates rank by an onward continuation on the taxiway first, then by path length. The leg runs only where the bridge has failed, so every route that resolved through the bridge resolves the same; when the leg does not help, the bridged failure is reported. The `[startleg]` lines on `ExplicitPathOptions.DiagnosticLog` trace it.

**3. `ResolveSequence` with recursive look-ahead** (`:585`). Walks consecutive token pairs. For each non-final pair it calls `ExpandSegment`; the final token goes to `ExpandLastWaypoint`. After each segment it **resets `VisitedNodeIds` to just the new head node**, so a route may intentionally revisit a taxiway (`A E B B3 A B1`) — cycle prevention is per-segment, not global. `ExpandSegment` dispatches:
   - node-ref → named: `RouteFromNodeRefToTaxiway`
   - named / node-ref → node-ref: `RouteToNodeRef` → `RouteToSpecificNode` (direct edge, else `AutoRouter`)
   - named → named: `RouteNamedToNamed` (the common case)

**4. `RouteNamedToNamed`** (`:1877`). `FindJunctionCandidates` finds every node on `fromTaxiway` with an edge matching `toTaxiway`. For each candidate, `LocalSearchToJunction` runs a bounded best-first search (A*) **constrained to edges on `fromTaxiway`** (plus the direct junction edge), capped at `MaxLocalExpansions = 500`. Each candidate's score is `cost-to-reach + continuationCost`:
   - `continuationCost` = recursive one-level probe when there is a meaningful tail and look-ahead is on — the probe runs with `enableLookahead: false` to bound recursion.
   - else geometric anchor heuristic — penalizes junctions whose `toTaxiway` edges point away from the next-next taxiway's anchor.
   - **turn-direction hint** (issue #172 W7): when the entered taxiway carries a `>`/`<` hint (`SearchContext.WaypointTurnHints`), `TurnHintOntoTaxiwayPenalty` adds a finite penalty to candidates whose onward edge on it doesn't turn the hinted way from the arrival bearing.

     A fillet arc is judged by its net turn, i.e. its arrival bearing (`HintJudgedBearing`, #459), because an arc departs tangent to the incoming taxiway and its departure bearing reads as "no turn"; for the first taxiway, `FirstTaxiwayTurnHintPenalty` penalizes a candidate whose initial edge direction doesn't match the hint relative to `SearchContext.StartHeadingTrue` (the aircraft's heading).

     Both penalties are `< TailUnresolvablePenaltyNm`, so they only re-rank otherwise-feasible candidates — an unrealisable hint never strands the route (best-effort). The single-taxiway case biases `WalkToNaturalTerminus` via `ResolveTurnHintBias` instead. When the committed junction couldn't honor a hint, the top-level resolution records an advisory into `SearchContext.TurnHintAdvisories`, which `RouteMaterialiser` copies onto `route.Warnings` so the TAXI echo reports the unhonored turn.
   The cheapest survivor is committed. Zero junction candidates → the offset runway crossing when the pair meets across a runway (see *Mandatory-connector insertion*), else the mandatory-connector detour at the top level, or `DetourSuppressedFailure` inside a probe.

**5. Final named token — junction stop or terminus walk.**

A **bare final taxiway** — no destination of any kind, no `>`/`<` turn hint on it, no hold-short bias, reached by transitioning from a preceding *taxiway* (not a runway token) — makes the route **end at the transition junction** (`TerminateAtTransitionJunction`), and the route is then cut before it: the aircraft never stops inside the intersection.

The clearance gave no onward direction, so the aircraft stops short of where it meets the final taxiway (OAK `TAXI C D` holds short of D on C, whether D extends one way or both ways from it).

`TaxiPathfinder.HoldShortOfNoDestinationTerminus` makes the cut, in the explicit resolve every consumer shares (the issued TAXI and the client's route previews alike): `SegmentExpander.NoDestinationTerminusTaxiway` names the final taxiway when the route stops where it meets that taxiway without driving it, which also covers a bare `TAXI B` given on C (the bridge from the occupied taxiway ends where the walk along B would begin).

The route is cut before the junction (`TerminusJunctionRunStart`: the end node and the run of route nodes before it that touch the taxiway, never an earlier junction with the same taxiway that the route runs past) and ends with a `HoldShortReason.RouteIncomplete` hold of that taxiway marked `HoldShortPoint.EndsNoDestinationRoute` (`GroundCommandHandler.HoldShortBeforeTaxiway`), placed on the taxiway setback ([hold-short-placement.md](./hold-short-placement.md#route-incomplete-holds)).

The advisory `holding short of D: route ends at D, no destination given` (`SegmentExpander.NoDestinationTerminusAdvisory`) is copied onto the route's warnings for the TAXI echo.

When the cut leaves nothing to taxi (the aircraft already stands at the junction), the route stands uncut, without the advisory, and `[TaxiPathfinder] no-destination cut before {Taxiway} from node {StartNode} leaves nothing to taxi; keeping the route uncut` is logged at Debug. An `HS E` target a route with a destination drives only on its way to that destination (`TAXI D C HS E RWY 28R`) ends the same way, held short of E (`GroundCommandHandler.HeldShortOfDrivenTarget`).

A preceding **runway** token is exempt — stopping at the runway/taxiway "junction" would end the route on the runway surface with no crossing bar (MIA `TAXI M1 RWY08R/26L L1`), so the final taxiway is walked clear of the runway as before.

**Bare crossing end.** A bare final taxiway reached *across a runway* (an offset crossing, or a pair the sidecar declares, KCOS `TAXI A2 C2`, KOAK `TAXI D F`) does not stop at the centreline join: `EndClearOfCrossedRunway` ends it where the whole aircraft is clear of the runway's holding position (AIM 4-3-21.b, "all parts of the aircraft").

Bare means `BareCrossingRunway`/`IsBareCrossingClearance`: no destination, no turn hint, no further taxiway, every hold short either of the runway crossed or already passed (an `HS` before the crossing still counts as bare; `TAXI … HS 35L` or a destination keeps the junction stop).

The walk runs straight ahead from the far join along the final taxiway to the first point where the tail and the nose (half the aircraft's length behind and ahead of its centre, along its heading there) both stand `RunwayTailClearance.MarginFt` (10 ft) past the far hold line, measured square to that line (`FuselagePastLineFt`).

On a straight taxiway that is half the length plus 10 ft past the bar, and a straight edge is cut at that point (a virtual end node); an arc is taken whole. Same-taxiway bends are followed up to 90° (`BendMaxTurnDeg`), forks and carry-ins into another taxiway capped at 30° (`StraightAheadMaxTurnDeg`), both sim conventions. The far bar is searched along the final taxiway only, bounded across the runway rather than along the taxiway (`FindFarBar`): the walk ends at the first node whose cross-track from the crossed runway's centreline exceeds `AirportGroundLayout.OutwardWalkBoundFt` plus `FarBarSearchMarginFt` (50 ft), at the taxiway's first junction, or at another runway. A taxiway that meets the runway at a shallow angle reaches its bar far along from the join, and a node carrying a fillet arc does not end the search; with no far bar found the old junction stop stands.

It is refused (`NoRoomToClearRunway`) when a T, a runway centreline or another runway's bar comes first or lies too close ahead of the end. `ClearOfRunwayEnds` records each node the search ended a bare crossing on, shared across every probe, and only the route that ends on a recorded node carries the `ClearOfRunwayEnd` (`TaxiRoute.ClearOfRunwayEnd`) and its advisory *"route ends on C2 clear of 17R/35L, no destination given"*.

`GroundCommandHandler.PrefixClearOfRunwayAdvisory` prefixes `holding short of 17R/35L at A2 — ` while the crossing's near bar stands, `crossing 17R/35L at A2 — ` once CROSS or auto-cross has cleared it; the landing-rollout `TAXI` reads back the same.

`CrossingRunwayPhase.AppendTailClearance` (the crossing phase's own half-length tail clearance, YAAT-944) is a separate path and unchanged.

Everything else falls to `WalkToNaturalTerminus`: walks forward along the taxiway one admissible edge at a time until none remain (`TerminusCandidates` lists the admissible steps, `CostRankedTerminusStep` ranks them, `HeadingLedTerminusStep` may override the first). **Requirement ①:** an exact **single-name** edge ranks *strictly above* a membership-taxiway junction arc (`IsMembershipTaxiwayJunctionArc: true` — multi-name and not a runway-crossing arc).

A multi-name junction arc is a turn *off* the taxiway, not a continuation; single-name wins regardless of cost, cost only breaks ties within a tier, except for a bare taxi's first step (below). Runway-crossing arcs (`IsRunwayJunction`, e.g. `H - RWY01L/19R`) *continue* the taxiway across a runway and are not excluded. (This greedy one-step walk is a known gap vs. multi-candidate discipline — see Caveats.)

**Heading-led first step.** A bare single-taxiway TAXI (no destination, no `>`/`<` turn hint on the walked taxiway, known `StartHeadingTrue`) takes its first step along the aircraft's heading when that step is clearly ahead. Within the winning tier the closest step must depart within 90° of the heading (`FirstStepHeadingAheadDeg`) and every other step at least 45° further off (`FirstStepHeadingMarginDeg`); zero-length edges are left out of the comparison.

Otherwise the cost ranking decides as before. The rule holds under every `RoutePreference`, `Shortest` included, and logs `[terminus] twy=<name> first step <from>-><to> follows heading <deg> (cost ranking chose <from>-><to>)`.

**6. Variant resolution** (`TryVariantExtension`, `:1683`). When the destination is a runway and the walked route hasn't already reached a hold-short for it, the expander looks for **numbered variants** of the last named taxiway whose hold-short serves the runway (`B` → `B1`). Exactly one → auto-extend. More than one (e.g. `W1`…`W7` off `W` to rwy 30) → **auto-pick the variant whose hold-short comes first in the full-length lineup order** (below), with the side taken from where the route has reached when it picks. It falls back to `TransitionAmbiguous` only when `NavigationDatabase` cannot resolve the runway.

The step's extension runs at once only when the walk reached the last named taxiway — it traversed an edge of it, or the head stands on a node carrying one.

From a head that never reached it, the extension is deferred: the runway-destination fallback A* (see *Runway-destination fallback across crossings* above) runs first, and the deferred edges apply only if that fallback left the route short of the bar.

The step's `DestinationUnreachable` is still raised and still hands over to the connector step. `ExplicitPathOptions.DiagnosticLog` gets `[variant] deferred: head #n not on X` when the extension is deferred and `[variant] deferred extension applied` when the fallback missed the bar and the edges were applied.

Zero variants → try same-name hold-shorts, else raise `DestinationUnreachable` blaming the last taxiway — which `Run` answers with the **connector fallback** (`TryRunwayConnectorFallback`: a numbered stub ≤ 600 ft off the last taxiway carrying a bar for the runway, threaded as a final waypoint and re-resolved; see *Runway reached through a short numbered stub* above) before the failure reaches the controller. When threshold is unavailable (no navdata) a failure is raised naming the candidates.

**7. Parking/spot extension** (`ExtendToDestination`, `:2154`). For `@parking`/`$spot`/helipad destinations, runs `AutoRouter` from the named-path terminus to the destination node, confined to the cleared taxiways, ramp taxilanes and RAMP, with the implied lead-in and, failing it, one implied one-way lane as the exceptions (see *The destination extension is confined* above).

**8. `RouteMaterialiser.Materialise`** (`:15`). One forward pass: build segments → annotate hold-shorts → truncate (runway destination = exactly at its lineup hold-short; otherwise one past the last required stop) → build warnings (mandatory-connector notifications + unauthorized-letter-taxiway warnings, with junction arcs and parking-bridge RAMP exempt). Returns the `TaxiRoute`.

**9. Honor-named-taxiway check** (after materialise). Every named taxiway in the clearance must be **reached** by the resolved route — either *traversed* (an edge labeled for it, membership arcs count) or at least *touched* (the route passes through a node incident to it).

Touching without traversing is normal: when two cleared taxiways meet at the same junction the route turns from one onto the next through that node without walking a labeled edge of either (e.g. `TE T U` where `TE` and `U` share a junction on `T`), and a more direct connector can reach the junction a named taxiway serves.

The check runs on the **materialised route**, not the pre-materialise edge walk — the latter can over-run the destination hold-short and reach the taxiway only on the far side of a runway the final route never crosses.

The command **fails** (`TaxiwayNotConnected`) only when a named taxiway is never reached at all: the aircraft could not get to it from its start without leaving the movement area. This is distinct from the soft mandatory-connector policy, which inserts a connector *between* named taxiways while keeping every named taxiway reachable.

### Ramp-lane reposition — a free-space cut onto a parallel taxilane (`RampLaneReposition`, issue #396)

The pathfinder only ever walks graph edges, and some ramps have parallel taxilanes with **no painted connector between them**: SFO's Terminal 1 ramp has M3 / M4 / M5 side by side, and M4 joins the rest of the field only at M1. So `TAXI M4 M1 …` from a gate on M3's alley — or from an aircraft already rolling on M3 — resolves to "M4 is not connected".

Ramps and aprons are normally nonmovement area — ATC does not specify the path there and routing is the pilot's discretion in coordination with ramp control (7110.65 §3-7-2 NOTE 2; AIM 4-3-20.g.7), and the apron between parallel taxilanes is aircraft-usable pavement (AIM 2-3-4.c.2) — so `GroundCommandHandler.TryTaxi` hands that failure to `RampLaneReposition.TryPlan` (`Data/Airport/RampLaneReposition.cs`) before its drop-and-warn ladders.

The plan is a normal `TaxiRoute` whose first segment is a `VirtualNode` leg from the aircraft's live position straight to a node on the named lane, followed by `ResolveExplicitPathDetailed` from that node — so `GroundNavigator`, `TaxiingPhase`, the conflict detector and the client overlay need nothing special.

Guards, all required:
- the resolver failed to get onto the **first** cleared taxiway (`TaxiwayNotConnected` blaming it, or — mid-lane, where the expander first tries a connector detour around the missing leg — `DestinationUnreachable` that blames no other taxiway);
- the lane is a **ramp taxilane** (`RampLaneReposition.IsRampTaxilane`) in the same **family** as the aircraft's current lane. Whether a name is a ramp taxilane (non-movement) is decided per layout by `MovementAreaClassification`. The rules are tried in order and the first match wins:
  1. A single-letter taxiway, a runway, or `RAMP` is never a lane. `RAMP` counts as apron.
  2. A name carrying a **runway holding position** on its own straight edges is movement area: OAK `W3`, SFO `A1`, `GL`, `M1`, `Z1`.
  3. A name with a **parking gate one edge off** it is a ramp taxilane. The gate may sit on the lane's own edge, or on a lead-in named `RAMP`, left blank, or named after the gate. This covers SFO `T5`–`T9`, `B5`, `BC`, `CG`, `Z2` and OAK `TE`, `TC`, `B1`. This rule outranks rule 4: TE and TC join both T and U.
  4. A name joined to **two or more distinct single-letter taxiways** is movement area: SFO `AF`, `AY1`–`AY4`, `B1`–`B4`, `CZ`, `Q1`, `ZS`.
  5. Anything else, a multi-character name joined to at most one single-letter taxiway, is a ramp taxilane: SFO `M4`, `T6`, `T7`, `SBC`, OAK `B2`.

  An airport sidecar (`Data/ARTCCs/{ARTCC}/Airports/*.json`) can override the result either way with `movementAreaTaxiways` / `nonMovementTaxilanes` (`[{ "name", "notes" }]`). These lists are only for one-off exceptions checked against the real airport. Anything the layout can show belongs in the rules, and no airport carries entries today.

  The same sidecar's `standDeparture` section (stand name → `PushBack` / `TaxiOut`) is a different override: it decides which stands get precomputed push targets, not which pavement is movement area ([pushback.md](pushback.md), "The push-target cache").

  `PavementClassifier` reads the same classification, so the push planner and the router agree. Siblings share their leading letter (`AreSiblingLanes`: TE ↔ TC, M3 ↔ M5); the current lane may also *be* the cleared lane (an aircraft on M4's stub still needs the cut). The current lane is `Ground.CurrentTaxiway` when set, else the lane a parked aircraft's RAMP lead-out opens onto, else the nearest straight taxi edge within 150 ft;
- the target is a node carrying a straight edge of that lane within `MaxCrossingFt = 450` — a sanity bound, not a regulatory one (no rule limits apron transit; ≈2× a B777 span, ≈1.8× the widest SFO M-lane spacing; B20S → M4 is ≈410 ft). Reachable candidates are tried nearest first until the graph route from one resolves; a rolling aircraft moves candidates within 50 ft of the nearest that lie no more than 100° off its nose to the front, so the cut is a lane change rather than a reversal;
- the straight line to it crosses **no runway holding-position marking** (any edge touching a `RunwayHoldShort` node, family or not — AIM 2-3-5.a.1 / 4-3-18.a.5), **no runway centerline** (`AirportGroundLayout.RunwayCenterlineBetween`) and **no edge outside the lane family ∪ RAMP** — crossing M4 to reach M5 is fine (one ramp), a lettered taxiway or runway edge in between is not; `TAXI A …` from 41-15 across the runways stays a hard rejection.

  The GeoJSON carries no continuous-vs-dashed edge markings (AIM 2-3-4.c), so family ∪ RAMP is the usable-pavement proxy.

**Destination-end cut** (`RampLaneReposition.TryPlanDestinationCut`, issue #400). The mirror image: OAK's TE and TC are parallel ramp lanes off T that only join at their southern end, and spot 22 hangs off TC's northern ramp cluster ~330 ft across open apron from TE's northern end.

`TAXI V T TE @22` resolves along V, T and TE, then the parking extension from TE's terminus fails (`DestinationUnreachable`, no taxiway blamed), and the old ladder dropped TE and taxied TC instead ("taxiing via TC — not in the route issued; unable via TE"). Now `TryTaxiCore` hands that exact failure shape — with the `@parking` / `$spot` node — to `TryPlanDestinationCut` before the drop-and-warn ladders.

Guards: the **last** cleared lane and the stand's lane (the first named taxiway the stand's RAMP lead-out reaches) are both ramp taxilanes and siblings; origins are the lane's straight-edge nodes nearest the stand (up to 4 tried); targets are the stand lane's straight-edge nodes plus the RAMP nodes of the stand's lead-in, within `MaxCrossingFt`, with the same runway-centerline / hold-short / family-∪-RAMP pavement checks as the start cut.

The head is the clearance re-resolved with the origin as `DestinationHintNode` (truncated there if the expander overshoots), the crossing is a `VirtualNode.CreateSegment` between two **layout** nodes named `RAMP` — apron, not the lane, so `FormatTaxiwaySequence` and the readback stay the clearance as issued ("Taxi via V T TE @22", no warning) — and the tail is `FindRoute` from the target to the stand.

Nothing else changes: the same failure with a non-sibling lane still falls through to `TryDropContradictoryVia`.

**A stand is cut to directly from the named lane, rolling in on its heading** (issue #454). Before the sibling-lane shape above, `TryPlanDestinationCut` tries `TryPlanDirectStandCut` for any destination that is not a spot. The aircraft leaves the lane it was cleared along and crosses the apron to the stand itself, never onto the sibling lane: SFO `TAXI B K A T5A @D1` drives T5A and cuts 321 ft to D1, not down T5B.

The crossing does not aim at the stand node. It ends at the **roll-in point** (`RollInApproachNode`): a virtual node one fuselage length (`AircraftLengthFt`) out from the stand along the reciprocal of its heading. A second straight leg runs from there in to the stand on the stand heading, so the aircraft parks lined up with the stand instead of at whatever angle the crossing arrived.

Both legs are `VirtualNode.CreateSegment` legs named `RAMP` (`RollInLegs`), so the broadcast sequence and the readback stay the clearance as issued. The origins are the lane's straight-edge nodes off any runway centreline, shortest total drive first. A node qualifies when the whole drive is within `MaxCrossingFt` (450 ft) and `IsRollInCuttable` passes: the node carries a straight edge of the lane family, and neither leg crosses a runway centreline or pavement outside family ∪ RAMP.

Up to `MaxCutOrigins` (4) are tried until the clearance head resolves to one. A stand with no heading, or no qualifying origin, falls back to the sibling-lane cut (SFO `TAXI B4 T8 @F1`: F1 is 521 ft from T8, so the aircraft crosses onto T9 and follows F1's lead-in). A spot keeps the lane-then-lead-in shape, because it is entered along its own lane (next paragraph).

**The resolved-route cut rolls in the same way.** `TryPlanResolvedRouteCut` (a route that already reaches the stand, but only the long way round) takes the aircraft's length and prices each candidate by the drive through the same roll-in point (`RollInDriveFt`), judges it by `IsRollInCuttable`, and ends in the same two `RAMP` legs.

It returns null for a stand without a heading. Its `no node on the route to {Dest} is worth cutting to the stand from` debug line is this method's, not the destination-cut planner's. The client overlay passes the aircraft's length (`FaaAircraftDatabase`, else the CWT fallback) so it draws the same roll-in.

**A spot is entered along its own lane** (SFO GC 28/01 bundle).

A `$spot` destination ends with the aircraft facing along the lane the spot node sits on, in or out, never across it — a cut that landed on the spot node (or a node short of it) from the side left SKW3396 on 5A at 45° across a 118/298 lane and SKW5564 on 7A/7B at 132°/118° across a 27/207 lane, with its nose over the neighbouring lead-in lane (#234's stop-nose-at-spot is a distance stop and freezes whatever heading the arc reached).

So `TryPlanDestinationCut` accepts a landing target for a spot only when the tail's last edge is an edge of the spot's lane into the spot node and the tail runs at least `SpotAlignmentRunFt` — one fuselage length of the type, 100 ft minimum, so a widebody is never given a cut that joins the lane less than a fuselage short of its spot (`EntersSpotAlongItsLane`).

`TryPlanResolvedRouteCut` never re-cuts a spot at all (its crossing always lands on the destination node), so a spot is never reached by a cut that lands on it; and the start cut `TryPlan` skips a landing node that is a spot with nothing left to taxi past it.

Gates are unchanged — a stand is entered on its own heading.

**A `$spot` before an `@gate` is a via** (same bundle). `ParseTaxiTokens` collects `$` tokens into the path in order and pops only a trailing one into `DestinationSpot`; with an `@gate` present every spot stays a via, so a `TaxiCommand` never carries both destinations (`SetDestination` throws if it does).

`GroundCommandHandler.ResolveSpotVias` rewrites each via to the spot node's `#id` token at the point the clearance first meets the layout, so the resolver routes through it like any node reference; the readback still names the lanes ("Taxi via A T7B @E2").

A gate the graph cannot reach from the via is refused by name — `Cannot reach parking 'E2' via 7B` (SFO: T7B's ramp end has no edge toward E2; only T7's does) — instead of the old outcome, where the spot routed as the destination, the gate label rode along on the route, and `TaxiingPhase.CompleteRoute` parked the aircraft "at E2" 500 ft from it.

`CompleteRoute` now installs `AtParkingPhase` only when the route's last node is the named stand's node (`EndsAtParkingStand`); any other end is `HoldingInPositionPhase`.

The client overlay (`GroundViewModel.ResolveRemainingRoute`) rebuilds the same cut from the broadcast `TaxiDestination` (`@22`) — from the aircraft's own end of the lane the graph can still "reach" the stand by doubling back down the lane, so a rebuilt route that starts more than 135° off the nose is replaced by the cut when one plans.

What it is not: no graph mutation (no crossover edges — a layout refresh cannot lose them and other airports need no data), no pushback model (a gate departure pivots out of the stand as before and drives the diagonal — for a heavy at a nose-in stand the first fuselage-length should really be a tug push; pre-existing gap, now visible here).

`GroundConflictDetector.ClassifyPair` keys `SameEdge*` / `Converging` on node ids; two aircraft both mid-crossing carry unique negative ids and classify as `Crossing` (position/heading based) — coarser, never wrong. Snapshot restore of the virtual leg is covered in [`snapshots-and-replay.md`](../snapshots-and-replay.md) (`TaxiSegmentDto` coordinates).

**A spot cleared from the ramp is a line-up to leave it** (#456).

`TryPlanSpotLineUp` re-plans the route so the aircraft arrives from the ramp side and stops nose on the mark facing out along the lane toward the movement-area taxiway it joins (SFO `TAXI T7A $7A` ends facing ~027° toward A).

It does so when a `TAXI … $spot` goes to an aircraft off the movement area (`RampLaneReposition.StartsOffMovementArea`: at parking, or its position is off the movement area — a pushed-back aircraft counts only by position), the clearance names nothing but the spot's own lane or its family, and the aircraft is not already on that lane between the spot and the movement area.

The shape: a free-space RAMP crossing to an approach point 3 nose-wheel turn radii off the lane (`LineUpApproachRadii`), a straight run-in, one ~90° turn onto the lane on the ramp side of the spot, then a lined-up pull of at least half a fuselage held at `TaxiingPhase.SpotLineUpPullSpeedKts` (5 kt; `TaxiRoute.SpotLineUpPullFromSegment`, carried in the snapshot) until the 4 kt spot crawl takes over.

A join is rejected when either free-space leg passes within the wingtip clearance of another aircraft on the ground or an occupied stand, or leaves the free-space bound, crosses a runway centreline or holding position, or ends on movement area; with no join left the resolved route stands. The resolved route's explicit hold-shorts and warnings carry over.

An aircraft on the movement area keeps the resolved route (`TAXI A $7A @F8` enters the ramp through 7A heading in). `TAXI $5A` from D2 crosses ~394 ft of apron to T5A's ramp-side end and ends facing A, never touching A. The client overlay rebuilds the same line-up at issuance; partway across the apron or early on the run-in it still re-plans from the nearest node (backlog).

### A bare TAXI to a gate or spot from the ramp stays in the ramp

A plain `TAXI` that names no taxiway, only a gate, helipad or spot (`TAXI $1`, `TAXI @B2`), issued to an aircraft inside the ramp (`RampLaneReposition.StartsOffMovementArea`), gets a **ramp-confined route**. 7110.65 §3-7-2 has the controller issue the route to follow on the movement area, so a clearance naming none leaves the aircraft only the ramp, where its movement is the pilot's (§3-7-2 NOTE 2). `GroundCommandHandler.ResolveRampConfinedRoute` tries, in order:

1. **The ramp graph** (`TaxiPathfinder.FindRampConfinedRoute` → `SegmentExpander.FindRampConfinedRoute`): an auto-route over ramp taxilanes, RAMP and unnamed pavement, with every movement-area taxiway and the aircraft's forbidden one-way moves hard-excluded; failing that, the same with one of the airport's one-way lanes implied (`TryExtendViaOneWayLane`'s rule, recorded in `ImpliedLanes` with its `not in clearance` warning).

   A route reaching a runway holding position or runway pavement is refused.
2. **One cut across the ramp** (`RampLaneReposition.TryPlanRampConfinedRoute` → `TryPlanRampConfinedCut`), where the graph alone does not join start and destination: a ramp-confined head from the start to a ramp node, one straight `RAMP`-named free-space drive across the apron, and a ramp-confined tail into the destination.

   The landing node carries only ramp pavement or one-way lanes and lies within `MaxCrossingFt` (450 ft) of the destination; a spot's tail enters it along one of its own lanes with a fuselage of run (`SpotAlignmentRunFt`).

   The drive is at most `MaxCrossingFt`, crosses no runway centreline and nothing but apron and ramp taxilanes (the other ramp cuts' `CrossesForeignPavement` rule), turns off the head and onto the tail within the category's `CategoryLimits.MaxHeadingChangeDeg`, and passes no other ground aircraft or occupied stand closer than both half-spans plus `GroundOutlineSweep.WingtipBufferFt` (`FindLegObstacle`, shared with the spot line-up).

   Head and tail together imply at most one one-way lane. The shortest head + drive + tail wins, with at most `MaxRampCutTargets` (24) landing nodes and `MaxRampCutHeads` (12) cut origins tried. SFO: pushed off B2 onto M5, `TAXI $1` runs out along M2 and crosses the ramp onto M1 short of spot 1, instead of taxiing A.
3. **The spot line-up**, for a spot only: the unconfined graph route re-planned by `TryPlanSpotLineUp`, kept only when every edge it drives stays inside the ramp (`StaysInsideTheRamp`: no movement-area taxiway, no runway, no holding position). SFO `TAXI $6B` mid-push off a D gate.

When none of these plans, the TAXI is refused, and no recovery in the failure ladder runs (each would drive a taxiway the clearance never named): `Unable, need a route to spot 1. To auto-route it: TAXIAUTO $1` (`@X` for a gate or helipad, `RampConfinedRefusal`).

From the movement area a bare gate or spot TAXI routes as before, unconfined, and the readback adds `taxiing via A — not in the route issued` for each uncleared movement-area taxiway it drives. `TAXIAUTO` and `TAXIALL` (`TaxiCoreOptions.IsTaxiAuto`) are the RPO's unrestricted auto-route: never ramp-confined, and no advisory.

### Incomplete clearances — the route-incomplete hold (issue #461)

The last step of the TAXI failure ladder in `GroundCommandHandler.TryTaxiCore`, after the resolved-route cut and every other recovery, is `ApplyMissingTaxiwayFallbacks`. It handles a clearance whose taxiways do not join up, and it replaced the unconstrained extension retry. It runs two checks, start first.

- **The start** (`CheckStartReachesFirstCleared`). Take the clearance's first taxiway the aircraft is not already on. If the start cannot reach a straight edge of it over edges carrying no uncleared movement-area name (`ReachesTaxiwayWithout`, never along a runway centreline), the missing link X is the first uncleared name on the resolved route, or else on the unconstrained route to that taxiway's nearest node.

  With an occupied taxiway, and when the clearance with X put ahead of its first taxiway reaches what it clears the aircraft to (`ReachesClearance`), the aircraft holds short of X on the taxiway it occupies (N9225L `TAXI D @NEW1` from the E exit holds short of C).

  Otherwise, when no route resolved, the TAXI is refused with `Unable, route to D from E needs C, not in clearance` (`UnresolvedTaxiMessage` appends the resolver's own reason in parentheses when it has one). A start that never reaches the first taxiway is refused rather than held at the end, because that hold would drive the missing link silently.
- **The end** (`TryHoldShortOfMissingTaxiway`). This runs when no route resolved, or when a runway route resolved without reaching a hold for its runway (`IsRunwayRouteShortOfItsRunway`).

  The candidates are the uncleared movement-area taxiways meeting the last cleared taxiway, nearest the destination first, at most `MaxMissingTaxiwayCandidates` (6), since each is a full resolution on the shared tick thread. A numbered variant of the last taxiway is never a candidate for a runway, because the clearance implies it.

  Each candidate is appended to the path and resolved as usual, so the resolver's destination-reach probe picks the junction. A route that reaches the destination is cut before X (`HoldShortBeforeTaxiway`). A runway destination prefers the X whose entry is nearest the threshold (beyond 100 ft), then the better route (`IsBetterRoute`); a gate or spot prefers the better route. OAK `RWY 28R TAXI F C HS 33` from OLD1 holds short of B.

The cut never ends on runway pavement (`KeepOffRunwayPavement`). A taxiway can change name on the runway, so the first node on X can be a centreline or end node (SFO `TAXI A2 @B1` becomes M1 on the 01L threshold). A cut past a runway's near-side bar and short of its far-side bar moves back to that bar, which stays the route's hold, uncleared. A cut on a centreline with no bar before it leaves nothing to taxi.

Otherwise the route ends at a `HoldShortReason.RouteIncomplete` hold of X (placement: [`hold-short-placement.md`](./hold-short-placement.md#route-incomplete-holds)). It also carries the note `Holding short of X: route to <dest> needs X, not in clearance`, where `<dest>` is `RWY 28R`, `@OLD1` or `$9`, or with no destination the last taxiway. Warnings about pavement past the cut are dropped. A route held this way skips the spot line-up, since it never reaches the spot.

**The readback is the clearance as issued.** `BuildTaxiReadback` renders `route.ToSummary` through `ReadbackTaxiwayFilter`, which names only the clearance's taxiways and the occupied one. A drawn route of `#node` tokens alone names nothing, so every driven taxiway shows. Added ramp taxilanes, RAMP and the implied names are silent.

The implied names are the numbered runway-entry connector (`ImpliedRunwayEntryConnector`: an `IsNumberedVariant` of the last cleared taxiway on the final straight segment of a route holding short of its destination runway, OAK `TAXI U W RWY 30` → W1) and the gate's or spot's lead-in (`ImpliedDestinationLeadIn`, whose resolver warning is withdrawn).

`WarnUnclearedMovementAreaTaxiways` adds `taxiing via X — not in the route issued` (`RouteMaterialiser.NotInRouteIssuedWarning`) for any other uncleared movement-area taxiway the route drives, unless another warning already names it as a whole token.

A junction arc between two taxiways, a runway centreline and a free-space leg are not a taxiway driven. A clearance naming no taxiway at all (a bare `TAXI @B2` from the movement area) names none of those it drives, so each is warned.

The one-way lanes the resolver implied (`TaxiRoute.ImpliedLanes`) are the exception both ways: the readback names them, since the aircraft drives a lane the clearance left out (SFO `TAXI T A @B2` reads back M1), and they carry their own `M1 not in clearance` note instead of the not-in-the-route-issued one. A `TAXIAUTO` readback carries no advisory at all (`TaxiReadbackInputs.AdviseUnclearedTaxiways`).

The pilot's spoken readback is built from the resolved route (`GroundCommandHandler.RouteReadback`) with the clearance's taxiways and the implied lanes as the names it may say, so it names the same taxiways; it is cut at the first hold short that stops the aircraft ([pilot-phraseology.md](../pilot-phraseology.md)).

**The occupied taxiway.** `OccupiedTaxiway` is `Ground.CurrentTaxiway` when the start node lies on it. Otherwise it is the movement-area taxiway of the nearest taxi edge (`FindNearestTaxiEdge`) within `RampLaneReposition.CurrentLaneMaxFt` (150 ft), with a blank name or a ramp taxilane counting as none. An aircraft on a stand (a parking start node, or still recorded as parked) occupies no taxiway, however close one runs (OAK GA16 `TAXI C` is refused because it needs F).

**Debugging the expander.** The `SegmentExpander` SimLog category is nearly silent. Its decisions (`[beststop]`, `[extend]`, lead-in tries) go to the `ExplicitPathOptions.DiagnosticLog` callback, so pass one when a test needs to see why a stop or an extension was rejected.

### Approach leg — a free-space drive from where the aircraft stands to the route's first node (`TaxiApproachLeg`)

The resolver starts every route at a graph node, and that node can be far from the aircraft: a plain `PUSH` reverses ~1.3 fuselage lengths straight back along the gate's ramp lead line and stops with the nose still toward the gate, so `TAXI` then resolves from the ramp/taxiway fillet node ~100 ft behind the tail (OAK gate 15 → node 763, whose first segment is the `T - RAMP` fillet arc).

Nothing in the route drove the aircraft to that node; the navigator's entry-alignment slow-turn fixed the heading and the fillet's closed-form playback then wrote the aircraft onto the arc start in one tick — the "rotated in place, then snapped to T" report.

`TaxiApproachLeg.Prepend` (`Data/Airport/TaxiApproachLeg.cs`) runs in `GroundCommandHandler.TryTaxiCore` once the recovery ladder has settled a route and before hold-short annotation (and on the zero-segment parking re-route), and prepends a `VirtualNode` segment from the aircraft's live position to `Segments[0].Edge.FromNode`.

Same machinery as the ramp-lane cut above: the navigator, `HoldShortAnnotator`, `TaxiSegmentDto` (`IsFreeSpace` + coordinates) and the client overlay need nothing new. The leg is named `RAMP` — `TaxiRouteFormatter.TaxiwayMembers` drops it, so the readback and the broadcast `TaxiRoute` string are unchanged and `CurrentTaxiway` reads `RAMP` during the drive, as a gate lead-out does today.

The drive itself is apron transit at the pilot's discretion — 7110.65 §3-7-2 NOTE 2 is the authority ("movement of aircraft or vehicles on nonmovement areas is the responsibility of the pilot, the aircraft operator, or the airport management"); AIM 4-3-18.a.1 (approval is needed only to enter the movement area) supports it — over aircraft-usable pavement (AIM 2-3-4.c.2).

Aviation-reviewed: the leg is accepted; the reviewer's remaining objection is to the *pose* a plain `PUSH` leaves (nose toward the gate, so the leg is a ~196° minimum-radius reversal a tug would normally have spared the crew) — a plain `PUSH` ending nose-out along the lead-out is filed as a follow-up in `docs/plans/MAIN.md`, not adopted.

Guards, all required (the `[ApproachLeg]` debug line names the one that refused):
- the route has segments;
- the aircraft is more than `AirportGroundLayout.AtNodeToleranceFt` (15 ft) from the node — closer and it is standing on it;
- the node is not a holding position the aircraft is waiting at or already over (`HoldingPositionRefusal`). The route must hold short nowhere at it, and a runway bar on it is refused unless the aircraft stands on the straight taxi edge leading to it (`OccupiedTaxiEdgeLeadingTo`) and behind the node along the route's first leg, with its centre more than half its length plus `GroundNavigator.SetBackStopMarginFt` behind the node measured along that occupied edge (`BehindBarAlongEdgeFt`; along the first leg a bar at a bend would shorten the distance by the bend's cosine). `Prepend` takes the aircraft's length for this.

  A TAXI starts the route on a bar's node from anywhere on a bar-ended edge, so an aircraft hundreds of feet out is driven to it, and the bar, now the leg's far end, gets its half-fuselage setback (AIM 2-3-5.a.1). Nearer than that, or past the bar on its runway side, the aircraft is waiting at the bar or has its nose over the marking, and `TaxiingPhase.TryHoldAtRouteStartNode` / `TryHoldPastStartBar` hold it by the real node id. The `[ApproachLeg]` line for a granted bar leg gives the bar's distance, the distance behind it along the edge and the painted stop;
- the node lies within 90° of the route's departure bearing — a node behind the aircraft with the route continuing ahead means it has already driven past the start, and pure pursuit converges onto the line from where it is — but only while the line is within reach.

  The "past it" refusal (`PastStartRefusal`) needs the aircraft within `MaxOffLineRatio` (0.5, a ≤ 27° intercept) × the first segment's remaining length of that segment's line, or beyond the segment's end; an aircraft stopped well abeam a short lane's start gets the leg (#456).

  An aircraft mid-way along a straight taxi edge has passed neither of its ends, so a route that leaves one of them by another edge gets the leg however sharply it turns there. A route whose segment 0 runs along that edge itself is the past-the-start case: the aircraft is already on segment 0 and pure pursuit closes onto it from where it stands.

  When that segment 0 runs back over the edge (a kept route ahead that reverses, above), `[ApproachLeg]` logs the node as past it, `0 ft abeam the line` for an aircraft on the centreline, and the navigator turns the aircraft about in place onto segment 0;
- the drive is within `RampLaneReposition.MaxCrossingFt` (450) and crosses no runway centerline — a free-space leg is not obstacle-aware.

  The bound is waived for a leg to an end node of the straight taxi edge the aircraft is on (`OccupiedTaxiEdgeLeadingTo`): the leg follows that taxiway's painted centreline, however long the edge (an aircraft mid-B at SFO, 600 ft from the next node). The runway-centerline check still applies.

  Another exception: when the node carries a runway-centerline edge and the aircraft is *on* that runway — within half the runway's width of the centerline and heading along it within 15° — it is rolling toward the exit (a `TAXI G …` issued on 28R after landing, 300 ft short of the exit fillet — the OAK `Issue213` fixture; 7110.65 §3-10-9 RUNWAY EXITING, AIM 4-3-21.a), the runway itself is the guide, and the leg is allowed up to that runway's length with the crossing check skipped.

  A bearing-only test was rejected in review: at long range it admits an aircraft hundreds of feet abeam on a parallel taxiway and would lay a leg across a holding-position marking (AIM 4-3-18.a.5). Before this exception the first `TickBezier` wrote the aircraft 314 ft onto the fillet start.

A mid-edge re-clearance starts at an end of the edge the aircraft is on (see [Where it sits & entry points](#where-it-sits--entry-points)). From the endpoint ahead, with a straight first segment, it passes the guards and gets a leg collinear with the edge — the same line pure pursuit drove before.

From the far end it gets the turn-about leg back along the edge to the far node, and the route reports a turn about from the far end (`TaxiRoute.TurnAboutShape` = `FromFarEnd`), as it does when the guards give it no leg.

Every `VirtualNode` id is a function of its position (FNV-1a over the 1e-7° cell), so same-seed runs and a snapshot restore serialise the same route; the counter it replaced made the determinism suite diverge the moment a leg appeared in a snapshot. How the navigator flies the leg (arc aimed at the node, line re-anchored at the arc exit) is in [`./navigator.md`](./navigator.md#entry-alignment-threshold).

### Full-length lineup bar order

`RouteMaterialiser.OrderFullLengthLineupBars(airportId, runwayId, LatLon? start, bars, Func<Func<GroundNode,bool>?>? shadowTest)` ranks a runway's hold-short bars as the full-length lineup point for a departure from the requested end, best first. `FindRunwayRoute` (TAXI/TAXIAUTO to a runway), `FindFullLengthLineupHoldShort`, the numbered-variant auto-pick in `SegmentExpander` and `GroundCommandHandler.ResolveRunwayHoldShortNode` (ATXI to a runway, and the client's Air taxi rows and chips) all use it, so they agree on the bar. It returns null when `NavigationDatabase` cannot resolve the runway's threshold and far end; each caller then falls back to distance from the start.

- **Key.** Each bar is measured along the runway centreline from the requested threshold (so a displaced or reciprocal end is the right one), and by its side of the centreline. The **full-length band** is the bars within 200 ft along-track of the smallest.
- **Start's side.** The sign of the start's cross-track. There is none (0) when `start` is null, within half the runway width + 50 ft of the centreline, or when the start is before the threshold or past the far end.
- **With a side.** The band's own-side bars first, then the rest of the band, then every other bar, each group by along-track then node id. With no own-side bar in the band, the own-side bar with the smallest along-track comes first and stands in as the departure point. With no bar on the start's side at all, the order is straight-line distance from the threshold, then node id (SFO 28R from the south holds at C's south bar, not the north one).
- **Without a side.** Along-track then node id, except that a **shadowed** bar goes last: one whose shortest taxi route from the start meets another of the runway's bars first (`TaxiPathfinder.LineupBarShadowTest`). The shadow test applies only when the side is missing because the start is before the threshold or inside the centreline dead band; a start past the far end, or a null start, gets none (TAXIAUTO 15 from OAK's west C bar taxis to a 15-end bar). A start off the graph gives no test.
- **Unreachable bars.** A bar the aircraft cannot taxi to ranks with the shadowed bars, after every reachable bar, for TAXIAUTO, `AutoRouter` and the variant pick; Air taxi treats an unreachable bar as clear (`TaxiPathfinder.RanksAsShadowed(LineupBarReach, unreachableIsShadowed)`, where `LineupBarReach` is Clear, Shadowed or Unreachable).
- **Cost.** Shadow results are cached per layout in `TaxiPathfinder.LineupBarShadowCache` (a `ConditionalWeakTable` keyed by start node, runway, category and wake class, holding 256 keys with least-recently-used eviction). The test is built lazily through the `shadowTest` factory, so no route search runs unless it applies; this keeps the Air taxi flyout's per-second refill cheap.
- **Already there.** `FindRunwayRoute` returns `HoldAtBarRoute` (zero segments, a `DestinationRunway` hold short) when the first bar is the start node, so `TAXIAUTO` to the bar the aircraft stands at holds there ("Already at runway 33 — holding short (runway 33 assigned)").

### Auto-route walkthrough (`AutoRouter.Run`)

`AutoRouter.Run` (`src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:40`) is a flat A* over the whole layout, used by `FindRoute`/`FindRoutes`, the explicit-mode parking extension, the detour fallback, and node-ref routing.

- Resolves the destination node (runway → `FindFullLengthLineupHoldShort`, the first bar of the full-length lineup order; parking/spot/node → the resolved id).
- A* with a `PriorityQueue<PartialRoute, double>`, a `(nodeId, arrival-bearing-bucket, arrival-taxiway)`-keyed `bestGScore` map for state-aware duplicate pruning, and the `GeometricAdmissibility` hard gate on every edge. `IncrementalCost` prices each edge. Heuristic = straight-line nm. Cap: **`MaxExpansions = 200_000`** → `SearchExhausted` (SFO cross-field routes legitimately explore 100k+).
- `startOverride` lets callers (detour, extension) seed the search with a prior `PartialRoute` so admissibility fires on the first expanded edge against the inherited heading — without it the first edge could U-turn.

  For a **cold start** (no `startOverride`), `GeometricAdmissibility.IsAdmissible` still admits the first edge in *any* direction (its `LastEdge is null` bypass) and the turn-budget term is skipped.

  So `RouteCostFunction.IncrementalCost` applies a soft **first-hop heading bias** (`FirstHopHeadingBiasNmPerDeg`, turn-budget scale) toward `SearchContext.StartHeadingTrue` when the heading is known and no `>`/`<` turn hint governs the first taxiway — so a taxi doesn't begin with an unmotivated turn away from where the aircraft is facing.

  It is finite, g-score-only (never in the heuristic), and applied equally to every first-edge candidate, so a genuinely-required reversal (the only route goes backward) still wins.
- Zero-distance edges (`< NoOpEdgeThresholdNm ≈ 1.2 ft`) are no-ops: admitted unconditionally, and downstream propagates the prior arrival bearing through them rather than reading the edge's meaningless stored bearing (fillet emits these at co-located nodes).

### Goal-set search (`AutoRouter.RunToGoals`)

`AutoRouter.RunToGoals(ctx, goals, onwardEdges, forbiddenMoves)` runs the same A* toward a set of goal nodes and stops at the first goal it pops that it can stop at (below), which is the cheapest to reach. It is one pass, not one search per goal.

`TaxiPathfinder.FindRouteToNearestGoal` is its entry point: it drops ids not in the layout, orders the rest by id, compiles `FindRoute`'s node context against the first goal (a node context names one destination), and runs `RunToGoals` through `RunWithAvoidance<T>`. `FOLLOWG`'s planner uses it to find where a follower joins its lead's path (see [navigator.md](./navigator.md#followg-joining-the-leads-taxi-path)).

- **Goals.** `GoalSet` holds the goal nodes; a node satisfies the destination on an exact id match, and, when the request carries onward edges (`GoalOnwardEdges`), only on an arrival from which the goal's onward edge is turnable (`GeometricAdmissibility.AdmitsTurn`): an arrival that could not turn onto it passes through the goal and the search goes on.

  A goal with no onward edge is reached on any arrival. A start that is itself a goal returns at once, when its onward edge is turnable from the aircraft's heading: that goal, a zero-segment route, cost 0.
- **Heuristic.** The least `RouteCostFunction.Heuristic` over the goals. It never exceeds the heuristic to the optimal goal, which never exceeds the cost of reaching it, so it stays admissible. With more than one goal it is memoised per node id, since each evaluation is a pass over the goals; a single goal computes it directly.
- **Materialisation.** `RunAstar` returns the edge list, and `SearchToGoals` materialises it against the context re-targeted at the goal reached (`MaterialiseContext`: a node destination gets that goal as its `TargetNodeId`; any other destination keeps its descriptor, since a runway route truncates at its first lineup hold-short). The same-side runway-centerline check and its banned-move re-run (up to three attempts) run on the materialised route.
- **Forbidden first move.** `NearestGoalRequest.ForbiddenFirstMove` is a directed move `(From, To)` the route may not start with, or null for none; it sets `SearchContext.ForbiddenFirstMove`. `RunAstar` rejects that move only as the route's first edge (`SearchContext.IsForbiddenFirstMove`: out of `StartNodeId` at depth 0), so a later edge of the route may still take it. It is hard on both passes: `RelaxHardGates` keeps it, so a start whose only way out is that move has no route.

  `FOLLOWG`'s re-plan passes the move back along the edge the follower came in on, so its new route never turns it about ([navigator.md](./navigator.md#followg-driving-the-follow-route)); every other caller passes null.
- **Forbidden moves.** `NearestGoalRequest.ForbiddenMoves` are directed moves the route never takes anywhere along it (`NearestGoalRequest.NoForbiddenMoves`, one shared empty set, for none). They seed the same banned-move set the same-side runway-centerline re-run extends. `FOLLOWG`'s merge search forbids running head-on down the lead's path and either way along the lead's edge or route ahead of it, so a follower never merges head-on or ahead of its lead.
- **One search for both.** `Run` (through `RunWithCost`) is `SearchToGoals` over a one-goal set, so a goal-set route to a goal matches `FindRoute`'s route to that goal edge for edge.
- **Result.** A `GoalRoute(GoalNodeId, Route, Cost)`. `Cost` is the search's own cost of the route — every `IncrementalCost` term, not just its length. `RunToGoals` throws `InvalidOperationException` when the materialised route does not end at the goal the search reached.

---

## Caveats & gotchas

Verify each against current code before relying on it — several are open work items, not finished behavior.

- **Membership junction arcs ("X - Y").** `GroundArc.MatchesTaxiway` matches if the queried name is *any* of the arc's `TaxiwayNames`. Fillets retain junction arcs named e.g. `C1 - B`, `A - RAMP`, `A - Q1`. A bare-taxiway walk continuing `B` therefore *sees* a `C1 - B` arc as a valid `B` step.

  **Requirement ① (single-name continuation beats a membership-only taxiway-junction arc) is enforced in both walkers**: `WalkToNaturalTerminus` (hard tier) and `LocalSearchToJunction` (soft cost penalty `MembershipJunctionArcContinuationCostNm = 0.5` nm, applied to a membership-arc *continuation*). The predicate is `GroundArc.IsMembershipTaxiwayJunctionArc` (`TaxiwayNames.Length >= 2 && !IsRunwayJunction`); runway-crossing arcs (`H - RWY...`) are continuations and are not penalised.

  **Transition-arc exemption (issue #236):** the `LocalSearchToJunction` penalty is skipped for an `[X,Y]` arc whose *other* name equals the taxiway just arrived on (`current.LastTaxiwayName`) — that arc is the *intended smooth turn from* the previous cleared taxiway *onto* this one (a lane change / corner like SFO `A → F1`), not a spurious turn-off, so flying the fillet is preferred over pivoting square through the junction node.

  This is orthogonal to req ①, which only guards an arc flanked by the *same* taxiway on both sides (a turn-off-and-return); a transition arc is flanked by two *different* cleared taxiways.

  **Residual:** fillets emit *parallel-duplicate* corner arcs — an identical single-name twin alongside the membership arc — so a segment can still carry the membership label even though the physical path is identical; the soft penalty can't always win because the `(node,bearing-bucket)` state-aware closed set lets the twin claim the slot. Behaviourally benign; the real fix is fillet-generator dedup of duplicate corner arcs.

- **Collapsed-junction geometry.** Edge-split collapses each junction into fewer tangent nodes with *larger* per-corner bearing steps. Straightest-continuation heuristics tuned for denser node grids can pick the wrong edge → dead-end spur / `X→Y→X` reversal (FLL B/C1 765↔767, SFO A 1160↔43). The graph is *correct but different* — do not "fix" it.

- **V-shaped taxiways.** A V-shaped taxiway is one LineString with two legs meeting at an apex. First-match junction selection can strand the route on the wrong leg. The bounded recursive look-ahead defeats this — e.g., FLL `T T4 B B1 HS 10L` enters T4 at the apex and walks the correct arm. The probe is load-bearing; don't reduce it to a fixed-horizon or one-shot geometric check.

- **Look-ahead is bounded to one level.** Probes run with `enableLookahead: false`, which both bounds recursion and suppresses the detour inside probes. A pathological sequence needing two levels of look-ahead to disambiguate will not get it.

  This is intentional (recursion bound) but is a real limit. (The specific failure where a one-level probe's tail dead-ends *short of a concrete-node destination* is caught separately by `ProbeTailCost`'s destination-reach term — see "Intermediate transitions are destination-reach-scored" above.)

- **`IsNumberedVariant` must reject digit-bearing bases.** `B10` is **not** a variant of `B1` — `B1` and `B10` are both siblings under base `B`. `IsNumberedVariant` (`SegmentExpander.cs:1279`) returns false when the base contains any digit, preventing the false positive that made a `B1`→runway hold-short look ambiguous against `B10`/`B11`. A digit-bearing base is a leaf connector with no further numbered variants.

- **A* pruning is state-aware (`(nodeId, bearing-bucket, arrival-taxiway)`-keyed).** Both `AutoRouter` and `SegmentExpander`'s local searches close the open set by `GeometricAdmissibility.PruningStateKey` (1° buckets plus the taxiway the state arrived on — `PartialRoute.LastTaxiwayName`), not node id alone.

  Bearing: onward-edge admissibility depends on arrival bearing, so a cheaper dead-end arrival must not suppress the only admissible different-bearing arrival; node-id keying alone produced false `DestinationUnreachable` (e.g., every runway from OAK `S8B`) and sub-optimal routes.

  Taxiway: `TaxiwayTransitionCostNm` is charged on the edge *after* an arrival, so two same-bearing arrivals on different taxiways have different onward costs and the cheaper one is not the cheaper way onward.

  At OAK's A/B junction (node 805) the A→B fillet arrives at B's bearing a hair cheaper than straight-B traffic and then pays the A→B transition; keyed without the taxiway it pruned the straight-B state and `TAXIAUTO 30` from the north field detoured around the east end of 10L/28R (D C A B) although D C B scored cheaper under the same cost function (`AutoRouterPruningTests`).

  The heuristic is bearing- and taxiway-independent, so A* optimality is preserved within the `(node, bucket, taxiway)` state space.

- **Auto-route 200k expansion cap.** `AutoRouter.MaxExpansions = 200_000`. SFO cross-field routes legitimately explore 100k+, so this is a real ceiling, not a safety margin — a latency risk on degenerate/disconnected layouts. There is no CI latency budget yet.

- **`WalkToNaturalTerminus` is one-step greedy.** It picks the best immediate next edge per step (with the requirement-① tier rule), not a multi-candidate search. Acceptable on forced-next-edge topologies; risky where the terminus branches.

  The first step of an unbiased walk is ranked by `IncrementalCost`, which charges the 0.3 nm runway-crossing penalty for stepping out over a bar away from the runway, while the first-hop heading bias tops out at 0.09 nm (`FirstHopHeadingBiasNmPerDeg` × 180°). Cost alone could therefore send an aircraft facing away from a runway back across it, which is why a bare taxi's first step is heading-led (see the heading-led first step under `WalkToNaturalTerminus`).

- **Validate work with current geometry.** Use `LayoutInspector --fillet-mode standard` to inspect the live graph. The full ground-stack acceptance suite is a final gate, not a discovery tool (it can hang on ground deadlocks / latency spikes); drive work with targeted scoped tests.

---

## Input & configuration types

- **`RoutePreference`** (enum: `FewestTurns`, `Shortest`, `Fastest`) — passed to `FindRoutes` to request a specific cost strategy; `null` = run all three and deduplicate.
- **`ExplicitPathOptions`** (class) — holds optional hints for `ResolveExplicitPath`. `ExplicitHoldShorts` holds `HoldShortTarget` values from the command's `HS` list — a runway id or taxiway name, optionally located with an `OnTaxiway` (`C@J`); taxiway names are folded into the waypoint sequence upstream by `GroundCommandHandler`, runway ids match `RunwayHoldShort` nodes only.

  The other hints are `DestinationRunway`, `DestinationHintNode`, `DiagnosticLog` (callback for troubleshooting), `AirportId`, `PathTurnHints` (per-taxiway `>`/`<` turn hints, index-aligned with the taxiway sequence), `StartHeadingTrue` (the aircraft's true heading, used as the turn reference for a hint on the first taxiway).
- **`AircraftCategory`** (enum) — affects heading-delta limits and cost preferences. Resolved per aircraft via `AircraftCategorization.Categorize`.

Both live in `src/Yaat.Sim/Data/Airport/ExplicitPathOptions.cs`.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` | Entry point: static methods (`ResolveExplicitPathDetailed`, `ResolveExplicitPath`, `FindRoute`, `FindRouteToNearestGoal`, `FindRampConfinedRoute`, `FindRunwayRoute`, `FindAdjacentRunwayRoute`, `FindRoutes`, `FindFullLengthLineupHoldShort`) that compile `SearchContext` (category and wake class) and delegate to drivers; `RunWithAvoidance<T>` is the two-pass relax `FindRoute`, `FindRoutes` and `FindRouteToNearestGoal` share. |
| `src/Yaat.Sim/Data/Airport/ExplicitPathOptions.cs` | `RoutePreference` enum + `ExplicitPathOptions` input class. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs` | Explicit-mode driver: junction selection with look-ahead, variant resolution, mandatory-connector insertion. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs` | Auto-mode A*: flat best-first search over the full layout, to one destination (`Run`, `RunWithCost`) or to the cheapest of a goal set (`RunToGoals` → `GoalRoute`, with `GoalOnwardEdges` and forbidden moves). |
| `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs` | Unified cost function + straight-line heuristic. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs` | Heading-delta gate (`CategoryLimits`, `IsAdmissible`, and the shared turn test `AdmitsTurn`). |
| `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs` | Edges → `TaxiRoute`: hold-short annotation, truncation, warning generation. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/SearchContext.cs` | Compiled per-call context: start node, destination, authorized taxiways, category, wake class (which one-way constraints bind), preferences, diagnostics; `HasHardGates` / `RelaxHardGates()` for the two-pass relax. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/PartialRoute.cs` | Immutable linked-list search state: head node, arrival bearing, cumulative cost, `VisitedNodeIds`. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/VisitedNodeSet.cs` | Per-route visited-node set: immutable sorted `int[]`, copy-on-`Add`, binary-search `Contains` (replaced `ImmutableHashSet<int>`, whose tree nodes were the search's dominant allocation). |
| `src/Yaat.Sim/Data/Airport/Pathfinding/PathfindingFailure.cs` | Structured failure: `FailureKind` enum + human message. |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | Graph types: `GroundNode`, `GroundArc`, `IGroundEdge`, `DirectionalEdge`, taxiway/runway lookup helpers. |
| `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` | Output type: segments, hold-shorts, warnings, implied one-way lanes, destination, current index. |
| `src/Yaat.Sim/Commands/GroundCommandHandler.cs` | `TryTaxi`/`TryTaxiAuto` entry point (`GroundCommandHandler.cs:15`, `:291`). |

**Tooling:** `tools/Yaat.LayoutInspector` with `--fillet-mode standard` to inspect the graph. See `docs/e2e-tdd-issue-debugging.md` for detailed layout-inspector usage.
