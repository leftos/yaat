# Taxi Pathfinder — Design & Architecture

> Read this before touching anything under `src/Yaat.Sim/Data/Airport/Pathfinding/`, `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs`, or `src/Yaat.Sim/Commands/GroundCommandHandler.cs`'s taxi-resolution paths. The pathfinder turns a `TAXI`/`TAXIAUTO` command into a `TaxiRoute` (edge sequence + hold-shorts). It is one of three ground-stack components — see the **fillet generator** (`./fillet-generator.md`) that builds the graph it walks, and the **navigator** (`./navigator.md`) that physically follows the route it produces. Index: `./README.md`.

## Architecture

The **`TaxiPathfinder`** (`src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:10`) is a `public static class` with five static entry points: `ResolveExplicitPath`, `FindRoute`, `FindRunwayRoute`, `FindRoutes`, and `FindFullLengthLineupHoldShort`. All production callers invoke these directly. The implementation is unified — no dual-path selector, no variant adapter — and lives under `src/Yaat.Sim/Data/Airport/Pathfinding/`.

**Caveat for any agent working on the pathfinder:** membership-matched junction arcs, V-shaped taxiways, and tighter fillet arcs are *correct-but-different* geometry. When something breaks on the graph, **adapt the pathfinder — do not "fix" the graph** (membership junction arcs are legitimate turn-connectors).

---

## Where it sits & entry points

A `TAXI`/`TAXIAUTO` command reaches the pathfinder through the command pipeline (see [`../command-pipeline.md`](../command-pipeline.md) for the full input → dispatch walk). The ground-specific tail:

1. `GroundCommandHandler.TryTaxi` / `TryTaxiAuto` (`src/Yaat.Sim/Commands/GroundCommandHandler.cs:15`, `:291`) is the dispatch target. `TryTaxiAuto` just builds an empty-path `TaxiCommand` and calls `TryTaxi` — so `TAXIAUTO RWY`/`TAXIAUTO @PARKING` is "a TAXI with no named taxiways". There is no separate auto pathfinder entry.
2. `TryTaxi` resolves the start node from `(position, heading)` — `groundLayout.FindNearestNodeForTaxi(...)` (heading-aligned endpoint of the nearest edge) with `FindNearestNode` as the off-graph fallback (`GroundCommandHandler.cs:38`). **The pathfinder's contract is strictly node-to-node**; mid-segment snapping happens here, upstream.
3. `TryTaxi` infers the taxiway the aircraft is already on and prepends it to the named path when the controller omitted it (`GroundCommandHandler.cs`, guarded by `SharesDirectJunction`). The current taxiway (`aircraft.Ground.CurrentTaxiway`) is prepended only when the start node lies on it, the path doesn't already begin with it, and it shares a direct junction node with the first cleared taxiway — so `TAXI E` from C resolves as `TAXI C E` and `TAXI W` from W5 as `TAXI W5 W`, without the occupied taxiway tripping the unauthorized-taxiway flag or the bridge's hop cap. When the two meet only across a runway (no shared node) it is left to the runway-crossing bridge, so the crossing still needs an explicit clearance.
4. `TryTaxi` branches on destination kind into `ResolveParkingRoute` (`@parking`/`$spot`) or `ResolveStandardRoute` (runway / implicit endpoint), and computes the aircraft category via `AircraftCategorization.Categorize` (`GroundCommandHandler.cs:66`).
5. Those resolvers call **`TaxiPathfinder`**'s static methods directly (`src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs`):

| Method | Signature | Purpose |
|---|---|---|
| `ResolveExplicitPath` | `(AirportGroundLayout, int fromNodeId, List<string> taxiwayNames, out string? failReason, ExplicitPathOptions, AircraftCategory)` | Controller-named taxiway sequence (explicit mode) → `TaxiRoute` or failure message. Implemented via `SegmentExpander`. |
| `FindRoute` | `(AirportGroundLayout, int fromNodeId, int toNodeId, AircraftCategory)` | Single best node→node route using FewestTurns preference; A* over full layout. |
| `FindRunwayRoute` | `(AirportGroundLayout, GroundNode startNode, string runwayId, AircraftCategory)` | Empty-path runway destination route. Tries full-length hold-short candidates in threshold order and returns the first route that reaches a real destination runway hold-short without traversing that runway surface. |
| `FindRoutes` | `(AirportGroundLayout, int fromNodeId, int toNodeId, RoutePreference?, int maxRoutes, IReadOnlySet<string>? authorizedTaxiways, AircraftCategory)` | Up to N distinct node→node routes; when `preference` is null, runs all three strategies (FewestTurns, Shortest, Fastest) and deduplicates. |
| `FindFullLengthLineupHoldShort` | `(AirportGroundLayout, GroundNode startNode, string runwayId, List<GroundNode> holdShortNodes)` | Pick the canonical lineup hold-short closest to the runway threshold. |

So there are **two modes**, both reaching the same internal machinery:

- **Explicit-path mode** — the controller named taxiways (`TAXI A E B B3`). `ResolveExplicitPath` → `SegmentExpander` stitches the declared sequence together and verifies drivability. Authorization is hard: named letter-only taxiways are a boundary.
- **Auto-route mode** — no named path (`TAXI 28L`, `TAXI @D8`, mid-route reroute, parking extension). `FindRoute`/`FindRoutes` → `AutoRouter` A* chooses the taxiways too. Empty-path runway destinations use `FindRunwayRoute`: it evaluates runway hold-shorts in threshold order, materializes each as a runway destination, and prefers the first route that stops at a real near-side destination hold-short without traversing the destination runway surface. This prevents the full-length target choice from crossing the departure runway to a geometrically closer opposite-side hold-short.

After the route returns, `GroundCommandHandler` owns everything the pathfinder does **not**: dynamic hold-short stop positions (`HoldShortAnnotator.ComputeHoldShortPositions`), implicit first-crossing clearance, `TaxiRouteAutoCross`, `CROSS`-keyword pre-clearing, runway auto-detection, and phase handoff to `TaxiingPhase`. The pathfinder produces the *unauthorized-baseline* route; clearance flagging is downstream.

### Output shape

The pathfinder returns `TaxiRoute` (`src/Yaat.Sim/Data/Airport/TaxiRoute.cs`), a `List<TaxiRouteSegment>` (each a `DirectionalEdge` + taxiway name) plus a `List<HoldShortPoint>`, `Warnings`, optional `DestinationParking`/`DestinationSpot`, and `CurrentSegmentIndex` (init 0). Consumers are `TaxiingPhase`, `GroundNavigator`, and snapshot/replay via `TaxiRouteDto`. The pathfinder is **idempotent and side-effect-free**: identical `(layout, inputs, category)` → identical route. This is mandatory for recording/replay — routes are snapshotted at TAXI-issue time and must re-resolve identically.

---

## Key design decisions & requirements

### Core principle: unified cost function, no post-walk repair

The pathfinder uses a single cost function (`RouteCostFunction.IncrementalCost`) evaluated at every decision point, with no per-decision scorer hacks or post-walk geometry fixes. This prevents the orbit/spin bugs (e.g., SKW3404 at SFO on route `A E B B3 A B1 Z S`, issue #165) that arose when local greedy choices were made without knowing downstream viability — and when per-node heuristics then tried to repair the wrong choice after the fact. The design evaluates the full sequence's viability before committing to an edge. Issue #165's regression test `Skw3404_DoesNotOrbitDuringTaxi` (`tests/Yaat.Sim.Tests/Simulation/Issue165SkwTaxiSpinTests.cs:201`) is un-skipped and passes under the current pathfinder.


### Avoided taxiways (per-ARTCC)

An ARTCC can mark taxiways an airport's **auto** routes should avoid via the `avoidTaxiways` section of the unified per-airport sidecar `Data/ARTCCs/{ARTCC}/Airports/{airport}.json` (loaded into `NavigationDatabase.AirportSidecars`; see `Data/ARTCCs/README.md`). `SearchContext.Compile` resolves the set for `Layout.AirportId` via `AirportSidecars.GetAvoidedTaxiways` and sets `AvoidMode = HardExclude` **only for auto routes** (empty waypoint sequence); an explicit named-taxiway path keeps `AvoidMode = Off`, so `SegmentExpander` and controller `TAXI` commands are never re-routed.

`TaxiPathfinder.FindRoute`/`FindRoutes` run a **two pass** search via `RunWithAvoidance`:

1. **Pass 1 — hard exclude.** `AutoRouter.RunAstar` skips any edge whose `ResolveTaxiwayName` is in `ctx.AvoidedTaxiways` (the edge is never expanded — a reachability gate, not a cost). If a route is found, it is returned and the avoided taxiway is guaranteed unused.
2. **Pass 2 — soft penalty (fallback).** Only when pass 1 finds no route, the search re-runs with `AvoidMode = SoftPenalty`: avoided edges are permitted but charged `RouteCostFunction.AvoidedTaxiwayFirstUseCostNm` (5.0 nm-equivalent, first-use only, finite). This keeps a destination reachable only through the avoided taxiway (e.g. a parking spot that hangs off it) resolvable while minimising the avoided mileage.

Exclusion is by the **resolved** taxiway name: a junction arc that *continues along* the avoided taxiway is excluded, while one that merely *crosses* it (continuing another taxiway) is not. When the avoided set is empty or the airport is unconfigured, `AvoidMode = Off` and `RunWithAvoidance` is a single, unchanged search — no second pass, no added cost.

### One-way taxiways (per-ARTCC)

The `oneWayEdges` section of the sidecar declares taxiway spans that may only be taxied one direction (see `Data/ARTCCs/README.md` for the coordinate-polyline authoring form). `OneWayResolver.Resolve` snaps each authored waypoint to the nearest graph node and converts the constraint into a set of **forbidden directed moves** `(fromId, toId)` — the reverse of each edge along the allowed-direction span (both directions when `block: "both"`). The set is cached per `AirportGroundLayout` via a `ConditionalWeakTable`, so a re-downloaded map re-resolves against its new node ids.

`SearchContext.Compile` resolves the set for `Layout.AirportId` and picks a mode: `OneWayMode.HardExclude` for **auto routes** (empty waypoint sequence) and `OneWayMode.Warn` for **explicit** named-taxiway paths. Enforcement has two chokepoints, mirroring the avoid/two-pass split:

1. **Hard gate (auto).** `AutoRouter.RunAstar` skips any edge whose `(headNode, nextNode)` move satisfies `ctx.IsForbiddenMove` — true only in `HardExclude`. `RunWithAvoidance` relaxes this to `Warn` on pass 2 when a destination is reachable only against the one-way, so it still resolves (with a warning) rather than failing.
2. **Warn detector (explicit + fallback).** `RouteMaterialiser.BuildWarnings` scans the materialised segments and emits `"Taxiing X against one-way direction"` for any segment whose directed move is in `ctx.ForbiddenOneWayMoves` while `OneWayMode == Warn`. Every route funnels through `Materialise`, so explicit paths (and the auto fallback) are flagged regardless of which `SegmentExpander` search produced the segment — no per-expansion-site wiring needed.

### Geometric correctness is a constraint, not a post-filter

An inadmissible junction is simply not an edge in the search graph from that arrival direction. `GeometricAdmissibility.IsAdmissible` (`src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs:86`) **hard-rejects** any edge whose heading change from the current arrival bearing exceeds the per-category limit:

| Category | Max heading change |
|---|---|
| Jet | 135° |
| Turboprop | 145° |
| Piston | 155° |
| Helicopter | 175° |

(`CategoryLimits.MaxHeadingChangeDeg` in `src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs:12`.) Reverse-traversed arcs are **not** separately excluded — they pass the gate if their heading delta is within the limit and are *penalised* by cost (`ReverseArcCostNm = 0.8`). The SKW3404 reverse arc is still caught because its delta is ~180°, excluded regardless of direction. Legitimate within-limit reverse arcs at parking exits are allowed.

### Cost function dimensions

All costs are nm-equivalent so the A* heuristic (straight-line distance) stays admissible. Constants are hardcoded in `RouteCostFunction.cs`; calibrate by running OAK/SFO grids and editing in code (no tunable config).

| Component | Weight | Notes |
|---|---|---|
| Segment distance | 1.0 | identity |
| Turn budget | 0.0005 nm/deg | heading change at node |
| Taxiway transition | 0.05 nm | per differently-named-taxiway hop |
| Runway crossing | 0.3 nm | per cross of an unrelated runway's hold-short (skipped for destination) |
| Direction reversal | 0.5 nm | SegmentExpander explicit-mode local searches only |
| Reverse arc | 0.8 nm | arc traversed against declared direction |
| Unauthorized taxiway | 0.2 nm | first use of a letter-only taxiway not in authorized set |
| Membership arc continuation | 0.5 nm | taxiway-junction arc ("X - Y") used as continuation vs turn |
| Runway centerline | ×10 distance | makes on-runway transit ~10× worse |

Preference multipliers: `FewestTurns` ×5 on turn + transition; `Shortest` zeroes all non-distance terms; `Fastest` adds `distance / maxSafeSpeed` (seconds) to the nm scalar. The Fastest term dominates and provides little heuristic guidance, so Fastest searches are slower but still correct.

**Direction-reversal lives in explicit mode only.** Applying it to auto-route A* would break heuristic admissibility — any edge whose bearing is >90° off the start→destination direction would inflate the g-score and cause exponential expansion across the airport (cross-field routes legitimately go "backward" to cross runways). Explicit mode's bounded local searches can safely apply the penalty. Consequence: auto-routes may be more zig-zaggy than explicit ones.

### Authorized-taxiway policy: letter-only = boundary, numbered = free

`SearchContext.IsLetterOnlyTaxiway` classifies by name: a name with **no digit** is letter-only (`A`, `Y`, `F`). The authorized set is the letter-only names the controller named. Numbered names (`A1`, `AY1`, `M1`) are always free for bridging/parking access. Letter-only taxiways the controller did **not** name cost `0.2 nm` on first use and surface a `Warning` — not hard-excluded. (Caveat: `IsLetterOnlyTaxiway` currently returns true for `RAMP`, so RAMP edges can attract the unauthorized penalty/warning; the materialiser separately suppresses warnings for leading/trailing parking-bridge RAMP — see `RouteMaterialiser.BuildWarnings`.)

### Hold-short handling & truncation

`RouteMaterialiser.AnnotateHoldShorts` tags every `RunwayHoldShort` node with `RunwayCrossing` (or `ExplicitHoldShort` when the runway is in the command's `HS` list). Multi-runway bars (`28L/28R`) add one point with the full string. The route is then **truncated** by `FindTruncationIndex`, discarding trailing pavement. `IsCleared`/`ClearedByAutoCross` start false (downstream owns them).

**Truncation rules** (`src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:233`):

- **Runway destination → the route ends *exactly* at its lineup hold-short** (the **first** hold-short of the destination runway the route reaches — the near-side bar), with **no `+1` buffer** and overriding every other rule. A departure taxis *up to* its runway and holds; it never crosses its own departure runway, so first-match is the lineup. Taking the *last* match instead would extend the route across the runway to the far-side bar of the same crossing (both physical sides share the combined `28R/10L` id) — which gridlocks following traffic. Proceeding onto / across the runway is clearance-gated by Line-Up / Crossing phases, never baked into the taxi route. If a graph route reaches a destination runway centerline segment before encountering a typed `RunwayHoldShort`, the materialiser adds a destination hold at the surface-entry node and truncates before the centerline as a fallback guard; `FindRunwayRoute` still prefers candidate routes that terminate at a real typed hold-short.
- **An en-route explicit hold-short crossed before reaching the last cleared taxiway** (`TAXI G C HS 28R`, no further runway destination) stops the route *one segment onto* that last cleared taxiway (`crossHoldTruncateAt` / `FindLastClearedTaxiwayEntry`) — the aircraft crosses and settles just past the junction, not stranded on the near side. This rule is **moot for a runway destination**, where the runway lies beyond the last cleared taxiway and the lineup-terminus rule wins.
- **Node / parking / spot destination** → one segment past the destination node (the `+1` buffer gives the navigator somewhere to aim).
- Otherwise → the natural terminus (last segment).

### Runway-destination fallback across crossings (greedy-walk escape hatch)

A departure runway can lie *beyond* one or more runway crossings on the cleared taxiways — e.g. KMIA `RWY 9 TAXI P S HS 12`, where taxiway S crosses runway 12 then hairpins to runway 9's hold-short. The greedy `WalkToNaturalTerminus` (one-step, see Caveats) can dead-end on a hold-area spur one turn short of the lineup bar, where the U-turn back to it fails the admissibility gate, and the route then truncates at the en-route crossing instead of reaching the runway. When the explicit walk + variant extension do **not** reach a hold-short for the destination runway, `SegmentExpander.Run` runs a last-resort flat A* (`AutoRouter`) from the start to the runway, which explores all branches and reaches the lineup hold-short across the crossings. The fallback is **hard-constrained to the cleared taxiways**: every letter-only taxiway the controller did not name is added to `AvoidedTaxiways` with `AvoidMode.HardExclude` (numbered connectors and RAMP stay free), so it cannot detour onto an un-named taxiway — it must cross every runway the cleared taxiways cross (e.g. `RWY 30 TAXI C B W HS 28R` crosses 28R then 28L). It only runs on the failing path, so it never changes a route that already reaches its runway. An explicit `HS <rwy>` on a crossed runway is a hold/authorization marker, not a routing terminus.

### Look-ahead defeats first-match hairpins (bounded to one level)

At a `T_i → T_{i+1}` transition there are usually multiple junction candidates (parallel crossings). Picking the first/closest can strand the route on the wrong leg of a **V-shaped taxiway** (one LineString, two legs meeting at an apex), forcing a hairpin U-turn after the transition. So `SegmentExpander` scores each junction candidate by the **cost of resolving the remaining sequence from it** — a recursive probe. The recursion is **bounded to one level**: the probe runs with `enableLookahead: false`, which also suppresses the whole-airport detour fallback, so a continuation that *needs* a detour becomes a strong negative signal against the candidate that led there. When there is no meaningful tail (final transition), a geometric anchor heuristic is used instead.

**Final transition into a runway destination is runway-anchored.** When the last named taxiway has a runway destination, the destination runway's own hold-short *on that taxiway* is the de-facto next waypoint. The pathfinder sets the look-ahead anchor to that hold-short, so junction selection steers toward the junction whose taxiway side leads to the runway. Without it the cheapest (nearest-along-the-previous-taxiway) junction can commit the final walk to the *wrong end* of the taxiway, after which the correct direction fails the U-turn admissibility gate and the route detours the long way around (e.g., OAK `TAXI D J C 33`: C reached at the near junction only continues toward A, forcing a loop via B / 28L-10R / P back to 33 — 131 segments collapsed to ~36 once anchored). The anchor is null when no hold-short for the runway sits on the taxiway — the runway is then reached via a numbered variant or connector.

### Mandatory-connector insertion + notification

When two consecutive cleared taxiways have **no direct junction** (zero junction candidates), the resolver bridges them and records a `ConnectorInsertion`. The bridge is a bounded A* (capped at `MaxDetourExpansions = 5_000`) that **inherits the authorized set** (soft policy): numbered connectors and RAMP are free, an unnamed letter taxiway carries the `0.2 nm` unauthorized penalty so it loses to a numbered connector but is still usable as a last resort — the detour never fails a resolvable clearance. The materialiser surfaces the inserted connector as an informative notification — *"A and B do not connect directly — taxi via A1"* — **not** an "unauthorized taxiway" warning, and suppresses the generic warning for those connector names.

---

## Step-by-step walkthrough — explicit mode (`SegmentExpander.Run`)

`SegmentExpander.Run` (`src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:36`) returns exactly one of `(TaxiRoute, null)` or `(null, PathfindingFailure)`.

**1. Waypoint resolution.** `ResolveWaypoints` turns each token into a `WaypointToken`. `#NNNN` tokens become node-refs (`IsNodeRef = true`); everything else is a named taxiway. The search starts with `head = PartialRoute.StartAt(ctx.StartNodeId)` — an immutable linked-list node (`PartialRoute.cs`) carrying head node id, arrival bearing, last edge, accumulated cost, depth, and a `VisitedNodeIds` set.

**2. Parking→taxiway bridge** (`BridgeStartToTaxiway`, `:554`). If the start node has no edge on the first named taxiway (e.g. parked on a RAMP-only spot), a bounded BFS (≤ `MaxBridgeHops = 3`) walks onto the nearest node carrying that taxiway. Without it the first per-segment search finds no on-taxiway edge from the start and degrades to a failing detour.

**3. `ResolveSequence` with recursive look-ahead** (`:245`). Walks consecutive token pairs. For each non-final pair it calls `ExpandSegment`; the final token goes to `ExpandLastWaypoint`. After each segment it **resets `VisitedNodeIds` to just the new head node**, so a route may intentionally revisit a taxiway (`A E B B3 A B1`) — cycle prevention is per-segment, not global. `ExpandSegment` dispatches:
   - node-ref → named: `RouteFromNodeRefToTaxiway`
   - named → node-ref: `RouteToNodeRef` (uses `AutoRouter`)
   - named → named: `RouteNamedToNamed` (the common case)

**4. `RouteNamedToNamed`** (`:833`). `FindJunctionCandidates` finds every node on `fromTaxiway` with an edge matching `toTaxiway`. For each candidate, `LocalSearchToJunction` runs a bounded best-first search (A*) **constrained to edges on `fromTaxiway`** (plus the direct junction edge), capped at `MaxLocalExpansions = 500`. Each candidate's score is `cost-to-reach + continuationCost`:
   - `continuationCost` = recursive one-level probe when there is a meaningful tail and look-ahead is on — the probe runs with `enableLookahead: false` to bound recursion.
   - else geometric anchor heuristic — penalizes junctions whose `toTaxiway` edges point away from the next-next taxiway's anchor.
   - **turn-direction hint** (issue #172 W7): when the entered taxiway carries a `>`/`<` hint (`SearchContext.WaypointTurnHints`), `TurnHintOntoTaxiwayPenalty` adds a finite penalty to candidates whose onward edge on it doesn't turn the hinted way from the arrival bearing; for the first taxiway, `FirstTaxiwayTurnHintPenalty` penalizes a candidate whose initial edge direction doesn't match the hint relative to `SearchContext.StartHeadingTrue` (the aircraft's heading). Both penalties are `< TailUnresolvablePenaltyNm`, so they only re-rank otherwise-feasible candidates — an unrealisable hint never strands the route (best-effort). The single-taxiway case biases `WalkToNaturalTerminus` via `ResolveTurnHintBias` instead. When the committed junction couldn't honor a hint, the top-level resolution records an advisory into `SearchContext.TurnHintAdvisories`, which `RouteMaterialiser` copies onto `route.Warnings` so the TAXI echo reports the unhonored turn.
   The cheapest survivor is committed. Zero junction candidates → mandatory-connector detour at the top level, or `DetourSuppressedFailure` inside a probe.

**5. `WalkToNaturalTerminus`** (`:1351`, the final named token). Walks forward along the taxiway one admissible edge at a time until none remain. **Requirement ①:** an exact **single-name** edge ranks *strictly above* a membership-taxiway junction arc (`IsMembershipTaxiwayJunctionArc: true` — multi-name and not a runway-crossing arc). A multi-name junction arc is a turn *off* the taxiway, not a continuation; single-name wins regardless of cost, cost only breaks ties within a tier. Runway-crossing arcs (`IsRunwayJunction`, e.g. `H - RWY01L/19R`) *continue* the taxiway across a runway and are not excluded. (This greedy one-step walk is a known gap vs. multi-candidate discipline — see Caveats.)

**6. Variant resolution** (`TryVariantExtension`, `:1683`). When the destination is a runway and the walked route hasn't already reached a hold-short for it, the expander looks for **numbered variants** of the last named taxiway whose hold-short serves the runway (`B` → `B1`). Exactly one → auto-extend. More than one (e.g. `W1`…`W7` off `W` to rwy 30) → **auto-pick the variant whose hold-short is nearest the runway threshold** (via `NavigationDatabase` threshold lookup). Zero variants → try same-name hold-shorts, else stop at natural terminus. When threshold is unavailable (no navdata) a failure is raised naming the candidates.

**7. Parking/spot extension** (`ExtendToDestination`, `:2154`). For `@parking`/`$spot`/helipad destinations, runs `AutoRouter` from the named-path terminus to the destination node.

**8. `RouteMaterialiser.Materialise`** (`:15`). One forward pass: build segments → annotate hold-shorts → truncate (runway destination = exactly at its lineup hold-short; otherwise one past the last required stop) → build warnings (mandatory-connector notifications + unauthorized-letter-taxiway warnings, with junction arcs and parking-bridge RAMP exempt). Returns the `TaxiRoute`.

**9. Honor-named-taxiway check** (after materialise). Every named taxiway in the clearance must be **reached** by the resolved route — either *traversed* (an edge labeled for it, membership arcs count) or at least *touched* (the route passes through a node incident to it). Touching without traversing is normal: when two cleared taxiways meet at the same junction the route turns from one onto the next through that node without walking a labeled edge of either (e.g. `TE T U` where `TE` and `U` share a junction on `T`), and a more direct connector can reach the junction a named taxiway serves. The check runs on the **materialised route**, not the pre-materialise edge walk — the latter can over-run the destination hold-short and reach the taxiway only on the far side of a runway the final route never crosses. The command **fails** (`TaxiwayNotConnected`) only when a named taxiway is never reached at all: the aircraft could not get to it from its start without leaving the movement area. This is distinct from the soft mandatory-connector policy, which inserts a connector *between* named taxiways while keeping every named taxiway reachable.

### Auto-route walkthrough (`AutoRouter.Run`)

`AutoRouter.Run` (`src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:31`) is a flat A* over the whole layout, used by `FindRoute`/`FindRoutes`, the explicit-mode parking extension, the detour fallback, and node-ref routing.

- Resolves the destination node (runway → `FindFullLengthLineupHoldShort`; parking/spot/node → the resolved id).
- A* with a `PriorityQueue<PartialRoute, double>`, a `(nodeId, arrival-bearing-bucket)`-keyed `bestGScore` map for state-aware duplicate pruning, and the `GeometricAdmissibility` hard gate on every edge. `IncrementalCost` prices each edge. Heuristic = straight-line nm. Cap: **`MaxExpansions = 200_000`** → `SearchExhausted` (SFO cross-field routes legitimately explore 100k+).
- `startOverride` lets callers (detour, extension) seed the search with a prior `PartialRoute` so admissibility fires on the first expanded edge against the inherited heading — without it the first edge could U-turn.
- Zero-distance edges (`< NoOpEdgeThresholdNm ≈ 1.2 ft`) are no-ops: admitted unconditionally, and downstream propagates the prior arrival bearing through them rather than reading the edge's meaningless stored bearing (fillet emits these at co-located nodes).

---

## Caveats & gotchas

Verify each against current code before relying on it — several are open work items, not finished behavior.

- **Membership junction arcs ("X - Y").** `GroundArc.MatchesTaxiway` matches if the queried name is *any* of the arc's `TaxiwayNames`. Fillets retain junction arcs named e.g. `C1 - B`, `A - RAMP`, `A - Q1`. A bare-taxiway walk continuing `B` therefore *sees* a `C1 - B` arc as a valid `B` step. **Requirement ① (single-name continuation beats a membership-only taxiway-junction arc) is enforced in both walkers**: `WalkToNaturalTerminus` (hard tier) and `LocalSearchToJunction` (soft cost penalty `MembershipJunctionArcContinuationCostNm = 0.5` nm, applied to a membership-arc *continuation*). The predicate is `GroundArc.IsMembershipTaxiwayJunctionArc` (`TaxiwayNames.Length >= 2 && !IsRunwayJunction`); runway-crossing arcs (`H - RWY...`) are continuations and are not penalised. **Residual:** fillets emit *parallel-duplicate* corner arcs — an identical single-name twin alongside the membership arc — so a segment can still carry the membership label even though the physical path is identical; the soft penalty can't always win because the `(node,bearing-bucket)` state-aware closed set lets the twin claim the slot. Behaviourally benign; the real fix is fillet-generator dedup of duplicate corner arcs.

- **Collapsed-junction geometry.** Edge-split collapses each junction into fewer tangent nodes with *larger* per-corner bearing steps. Straightest-continuation heuristics tuned for denser node grids can pick the wrong edge → dead-end spur / `X→Y→X` reversal (FLL B/C1 765↔767, SFO A 1160↔43). The graph is *correct but different* — do not "fix" it.

- **V-shaped taxiways.** A V-shaped taxiway is one LineString with two legs meeting at an apex. First-match junction selection can strand the route on the wrong leg. The bounded recursive look-ahead defeats this — e.g., FLL `T T4 B B1 HS 10L` enters T4 at the apex and walks the correct arm. The probe is load-bearing; don't reduce it to a fixed-horizon or one-shot geometric check.

- **Look-ahead is bounded to one level.** Probes run with `enableLookahead: false`, which both bounds recursion and suppresses the detour inside probes. A pathological sequence needing two levels of look-ahead to disambiguate will not get it. This is intentional (recursion bound) but is a real limit.

- **`IsNumberedVariant` must reject digit-bearing bases.** `B10` is **not** a variant of `B1` — `B1` and `B10` are both siblings under base `B`. `IsNumberedVariant` (`SegmentExpander.cs:1279`) returns false when the base contains any digit, preventing the false positive that made a `B1`→runway hold-short look ambiguous against `B10`/`B11`. A digit-bearing base is a leaf connector with no further numbered variants.

- **A* pruning is state-aware (`(nodeId, bearing-bucket)`-keyed).** Both `AutoRouter` and `SegmentExpander`'s local searches close the open set by `GeometricAdmissibility.PruningStateKey` (1° buckets), not node id alone — because onward-edge admissibility depends on arrival bearing, so a cheaper dead-end arrival must not suppress the only admissible different-bearing arrival. Using node-id keying alone produces false `DestinationUnreachable` (e.g., every runway from OAK `S8B`) and sub-optimal routes. The heuristic is bearing-independent, so A* optimality is preserved within the `(node,bucket)` state space.

- **Auto-route 200k expansion cap.** `AutoRouter.MaxExpansions = 200_000`. SFO cross-field routes legitimately explore 100k+, so this is a real ceiling, not a safety margin — a latency risk on degenerate/disconnected layouts. There is no CI latency budget yet.

- **`WalkToNaturalTerminus` is one-step greedy.** It picks the best immediate next edge per step (with the requirement-① tier rule), not a multi-candidate search. Acceptable on forced-next-edge topologies; risky where the terminus branches.

- **Validate work with current geometry.** Use `LayoutInspector --fillet-mode standard` to inspect the live graph. The full ground-stack acceptance suite is a final gate, not a discovery tool (it can hang on ground deadlocks / latency spikes); drive work with targeted scoped tests.

---

## Input & configuration types

- **`RoutePreference`** (enum: `FewestTurns`, `Shortest`, `Fastest`) — passed to `FindRoutes` to request a specific cost strategy; `null` = run all three and deduplicate.
- **`ExplicitPathOptions`** (class) — holds optional hints for `ResolveExplicitPath`: `ExplicitHoldShorts` (list of runway ids), `DestinationRunway`, `DestinationHintNode`, `DiagnosticLog` (callback for troubleshooting), `AirportId`, `PathTurnHints` (per-taxiway `>`/`<` turn hints, index-aligned with the taxiway sequence), `StartHeadingTrue` (the aircraft's true heading, used as the turn reference for a hint on the first taxiway).
- **`AircraftCategory`** (enum) — affects heading-delta limits and cost preferences. Resolved per aircraft via `AircraftCategorization.Categorize`.

Both live in `src/Yaat.Sim/Data/Airport/ExplicitPathOptions.cs`.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` | Entry point: five static methods (`ResolveExplicitPath`, `FindRoute`, `FindRunwayRoute`, `FindRoutes`, `FindFullLengthLineupHoldShort`) that compile `SearchContext` and delegate to drivers. |
| `src/Yaat.Sim/Data/Airport/ExplicitPathOptions.cs` | `RoutePreference` enum + `ExplicitPathOptions` input class. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs` | Explicit-mode driver: junction selection with look-ahead, variant resolution, mandatory-connector insertion. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs` | Auto-mode A*: flat best-first search over the full layout. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs` | Unified cost function + straight-line heuristic. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs` | Heading-delta gate (`CategoryLimits`, `IsAdmissible`). |
| `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs` | Edges → `TaxiRoute`: hold-short annotation, truncation, warning generation. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/SearchContext.cs` | Compiled per-call context: start node, destination, authorized taxiways, preferences, diagnostics. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/PartialRoute.cs` | Immutable linked-list search state: head node, arrival bearing, cumulative cost. |
| `src/Yaat.Sim/Data/Airport/Pathfinding/PathfindingFailure.cs` | Structured failure: `FailureKind` enum + human message. |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | Graph types: `GroundNode`, `GroundArc`, `IGroundEdge`, `DirectionalEdge`, taxiway/runway lookup helpers. |
| `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` | Output type: segments, hold-shorts, warnings, destination, current index. |
| `src/Yaat.Sim/Commands/GroundCommandHandler.cs` | `TryTaxi`/`TryTaxiAuto` entry point (`GroundCommandHandler.cs:15`, `:291`). |

**Tooling:** `tools/Yaat.LayoutInspector` with `--fillet-mode standard` to inspect the graph. See `docs/e2e-tdd-issue-debugging.md` for detailed layout-inspector usage.
