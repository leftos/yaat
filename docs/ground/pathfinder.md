# Taxi Pathfinder — Design & Architecture

> Read this before touching anything under `src/Yaat.Sim/Data/Airport/Pathfinding/`, `TaxiPathfinder.cs`, or `GroundCommandHandler.cs`'s taxi-resolution paths. The pathfinder turns a `TAXI`/`TAXIAUTO` command into a `TaxiRoute` (edge sequence + hold-shorts). It is one of three ground-stack components — see the **fillet generator** (`./fillet-generator.md`) that builds the graph it walks, and the **navigator** (`./navigator.md`) that physically follows the route it produces. Index: `./README.md`.

## Status — V2 is the pathfinder

**V2 (`TaxiPathfinder` + `Data/Airport/Pathfinding/*`) is the only pathfinder.** The V1 `TaxiPathfinder` (~3200 lines), the `ITaxiPathfinder` / `TaxiPathfinderV1Adapter` / `TaxiPathfinderRouter` selector seam, and the V1-only `TaxiVariantResolver` were deleted at the joint flip. `TaxiPathfinder`'s four entry points (`ResolveExplicitPath`, `FindRoute`, `FindRoutes`, `FindFullLengthLineupHoldShort`) are now **static**; production calls them directly. The other two ground-stack layers — [fillet generator](./fillet-generator.md) and [navigator](./navigator.md) — are likewise V2-only.

**Caveat for any agent working on the pathfinder:** membership-matched junction arcs, V-shaped taxiways, and tighter V2 fillet arcs are *correct-but-different* geometry. When something breaks on the graph, **adapt the pathfinder — do not "fix" the graph** (membership junction arcs are legitimate turn-connectors).

> The `V2` suffix has been dropped from the type names (the class is now `TaxiPathfinder`, its internals live under `Data/Airport/Pathfinding/`). The body below still describes V2 relative to the now-deleted V1 in places; a fuller body-prose refresh remains.

---

## Where it sits & entry points

A `TAXI`/`TAXIAUTO` command reaches the pathfinder through the command pipeline (see [`../command-pipeline.md`](../command-pipeline.md) for the full input → dispatch walk). The ground-specific tail:

1. `GroundCommandHandler.TryTaxi` / `TryTaxiAuto` (`src/Yaat.Sim/Commands/GroundCommandHandler.cs:15`, `:283`) is the dispatch target. `TryTaxiAuto` just builds an empty-path `TaxiCommand` and calls `TryTaxi` — so `TAXIAUTO RWY`/`TAXIAUTO @PARKING` is "a TAXI with no named taxiways". There is no separate auto pathfinder entry.
2. `TryTaxi` resolves the start node from `(position, heading)` — `groundLayout.FindNearestNodeForTaxi(...)` (heading-aligned endpoint of the nearest edge) with `FindNearestNode` as the off-graph fallback (`GroundCommandHandler.cs:37`). **The pathfinder's contract is strictly node-to-node**; mid-segment snapping happens here, upstream.
3. `TryTaxi` branches on destination kind into `ResolveParkingRoute` (`@parking`/`$spot`) or `ResolveStandardRoute` (runway / implicit endpoint), and computes the aircraft category via `AircraftCategorization.Categorize` (`GroundCommandHandler.cs:66`).
4. Those resolvers call **`TaxiPathfinder`**'s static methods directly:

| `TaxiPathfinder` method | Purpose | Driver |
|---|---|---|
| `ResolveExplicitPath` | named taxiway sequence (explicit mode) | `SegmentExpander.Run` |
| `FindRoute` | single best node→node route (FewestTurns) | `AutoRouter.Run` |
| `FindRoutes` | up to N distinct node→node routes per preference | `AutoRouter.Run` (×3 when preference null) |
| `FindFullLengthLineupHoldShort` | pick the lineup hold-short bar for a runway | `RouteMaterialiser.FindFullLengthLineupHoldShort` |

So there are **two modes**, both reaching the same internal machinery:

- **Explicit-path mode** — the controller named taxiways (`TAXI A E B B3`). `ResolveExplicitPath` → `SegmentExpander` stitches the declared sequence together and verifies drivability. Authorization is hard: named letter-only taxiways are a boundary.
- **Auto-route mode** — no named path (`TAXI 28L`, `TAXI @D8`, mid-route reroute, parking extension). `FindRoute`/`FindRoutes` → `AutoRouter` A* chooses the taxiways too. There is also a `ResolveRunwayRouteByAStar` shortcut for `TAXI <RWY>` with an empty path: it picks the lineup hold-short via `FindFullLengthLineupHoldShort`, then `FindRoute`s to it (`GroundCommandHandler.cs:341`).

After the route returns, `GroundCommandHandler` owns everything the pathfinder does **not**: dynamic hold-short stop positions (`HoldShortAnnotator.ComputeHoldShortPositions`), implicit first-crossing clearance, `TaxiRouteAutoCross`, `CROSS`-keyword pre-clearing, runway auto-detection, and phase handoff to `TaxiingPhase`. The pathfinder produces the *unauthorized-baseline* route; clearance flagging is downstream.

### Output shape (shared with V1)

V2 returns the **same `TaxiRoute` class** as V1 (`src/Yaat.Sim/Data/Airport/TaxiRoute.cs`) so every downstream consumer (`TaxiingPhase`, `GroundNavigator`, snapshot/replay via `TaxiRouteDto`) is unchanged. A route is a `List<TaxiRouteSegment>` (each a `DirectionalEdge` + taxiway name) plus a `List<HoldShortPoint>`, `Warnings`, `DestinationParking`/`DestinationSpot`, and `CurrentSegmentIndex` (init 0). The pathfinder is **idempotent and side-effect-free**: identical `(layout, inputs, category)` → identical route. This is mandatory for recording/replay — routes are snapshotted at TAXI-issue time and must re-resolve identically.

---

## Key design decisions & requirements

### Why V2 exists

V1 is a greedy forward walk that commits to each step before knowing whether downstream steps are viable. Once a wrong edge is taken nothing unwinds — the root cause of orbit/spin bugs (SKW3404 SFO `A E B B3 A B1 Z S`, OAK north-field). V1 layered local heuristics (multiple bridge strategies, post-walk arc rewriting, a *conditional* look-ahead that only ran when a destination hint was present) that don't compose and can re-introduce the bugs they defend against. Fillet V2's tighter, cleaner arcs then exposed these faults further (V1 tripped on zero-distance / reverse edges V1 fillets emitted). V2 is a clean-room rewrite around a single principle.

### Core principle: candidate edge sequences, one cost function, evaluate before committing

A route is a sequence of directed edges, and every quality criterion — drivability, cost, compliance — is evaluated on that sequence by **one** cost function (`RouteCostFunction.IncrementalCost`), at **every** decision point. No per-decision scorers, no mutate-then-repair, no committing a local choice without knowing a global continuation exists.

### Geometric correctness is a constraint, not a post-filter

An inadmissible junction is simply not an edge in the search graph from that arrival direction. `GeometricAdmissibility.IsAdmissible` (`src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs:52`) **hard-rejects** any edge whose heading change from the current arrival bearing exceeds the per-category limit:

| Category | Max heading change at a node |
|---|---|
| Jet | 135° |
| Turboprop | 145° |
| Piston | 155° |
| Helicopter | 175° |

(`CategoryLimits.MaxHeadingChangeDeg`, `GeometricAdmissibility.cs:12`.) Reverse-traversed arcs are **not** separately excluded — they pass the gate if their heading delta is within the limit and are *penalised* by cost (`ReverseArcCostNm = 0.8`). The SKW3404 reverse arc is still caught because its delta is ~180°, excluded regardless of direction. This is design decision §3, revised after real layouts (SFO/OAK) showed legitimate within-limit reverse arcs at parking exits.

### Cost function dimensions

All costs are nm-equivalent so the A* heuristic (straight-line distance, `RouteCostFunction.Heuristic`) stays admissible. Constants are hardcoded at the top of `RouteCostFunction.cs`; calibrate by running OAK/SFO grids and editing in code (no tunable config).

| Component | Weight | Notes |
|---|---|---|
| Segment distance | 1.0 | identity (`DistanceNm`) |
| Turn budget | 0.0005 nm/deg | heading change at the head node |
| Taxiway transition | 0.05 nm | per differently-named-taxiway hop |
| Runway crossing | 0.3 nm | per cross of an *unrelated* runway's hold-short (skipped when it IS the destination lineup) |
| Direction reversal | 0.5 nm | **SegmentExpander local searches only** (see below) |
| Reverse arc | 0.8 nm | arc traversed against `Nodes[0]→Nodes[1]` |
| Unauthorized taxiway | 0.2 nm | first use of a letter-only taxiway not in the authorized set |
| Runway centerline | ×10 distance | makes on-runway transit ~10× worse — backtaxi fallback only |

Preference adjusts weights: `FewestTurns` ×5 on turn + transition; `Shortest` zeroes all non-distance terms; `Fastest` adds a `distance / maxSafeSpeed` time term (note: this mixes a seconds term into the nm scalar — a known unit smell, `RouteCostFunction.cs:80`).

**Direction-reversal lives in SegmentExpander only** (design decision §7, `RouteCostFunction.cs:146`). Applying it inside `AutoRouter`'s A* would break heuristic admissibility — any edge whose bearing is >90° off the start→destination direction would inflate the g-score and cause exponential expansion across the airport (cross-field routes legitimately go "backward" to cross runways). `SegmentExpander.ComputeDirectionReversalPenalty` applies it in its bounded local searches where the small search space makes it safe. Consequence: auto-routes can be more zig-zaggy than explicit ones.

### Authorized-taxiway policy: letter-only = boundary, numbered = free

`SearchContext.IsLetterOnlyTaxiway` (`src/Yaat.Sim/Data/Airport/Pathfinding/SearchContext.cs:104`) classifies by name: a name with **no digit** is letter-only (`A`, `Y`, `F`). The authorized set is the letter-only names the controller named (`BuildAuthorizedTaxiwaySet`). Numbered names (`A1`, `AY1`, `M1`) are always free for bridging/parking access. Letter-only taxiways the controller did **not** name are not hard-excluded — they cost `0.2 nm` first-use and surface a `Warning`. (Caveat: `IsLetterOnlyTaxiway` currently returns true for `RAMP`, so RAMP edges can attract the unauthorized penalty/warning; the materialiser separately suppresses warnings for leading/trailing parking-bridge RAMP — see `RouteMaterialiser.BuildWarnings`.)

### Hold-short handling & truncation

`RouteMaterialiser.AnnotateHoldShorts` tags every `RunwayHoldShort` node with `RunwayCrossing` (or `ExplicitHoldShort` when the runway is in the command's `HS` list). Multi-runway bars (`28L/28R`) add one point with the full string. The route is then **truncated** by `FindTruncationIndex`, discarding trailing pavement. `IsCleared`/`ClearedByAutoCross` start false (downstream owns them).

**Truncation rules** (`FindTruncationIndex`):

- **Runway destination → the route ends *exactly* at its lineup hold-short** (the **first** hold-short of the destination runway the route reaches — the near-side bar), with **no `+1` buffer** and overriding every other rule. A departure taxis *up to* its runway and holds; it never crosses its own departure runway, so first-match is the lineup. Taking the *last* match instead would extend the route across the runway to the far-side bar of the same crossing (both physical sides share the combined `28R/10L` id) — which gridlocks following traffic. Proceeding onto / across the runway is clearance-gated by Line-Up / Crossing phases, never baked into the taxi route.
- **An en-route explicit hold-short crossed before reaching the last cleared taxiway** (`TAXI G C HS 28R`, no further runway destination) stops the route *one segment onto* that last cleared taxiway (`crossHoldTruncateAt` / `FindLastClearedTaxiwayEntry`) — the aircraft crosses and settles just past the junction, not stranded on the near side. This rule is **moot for a runway destination**, where the runway lies beyond the last cleared taxiway and the lineup-terminus rule wins.
- **Node / parking / spot destination** → one segment past the destination node (the `+1` buffer gives the navigator somewhere to aim).
- Otherwise → the natural terminus (last segment).

### Look-ahead defeats first-match hairpins (bounded to one level)

At a `T_i → T_{i+1}` transition there are usually multiple junction candidates (parallel crossings). Picking the first/closest can strand the route on the wrong leg of a **V-shaped taxiway** (one LineString, two legs meeting at an apex), forcing a hairpin U-turn after the transition. So `SegmentExpander` scores each junction candidate by the **cost of resolving the *remaining* sequence from it** — a recursive probe (`ProbeTailCost`), mirroring V1's `SelectBestStopNode`. The recursion is **bounded to one level**: the probe runs with `enableLookahead: false`, which also suppresses the whole-airport detour fallback so a continuation that *needs* a detour becomes a strong negative signal against the candidate that led there. When there is no meaningful tail (final transition), a cheaper geometric anchor heuristic is used instead.

**Final transition into a runway destination is runway-anchored.** When the last named taxiway has a runway destination there is no next named taxiway to anchor toward, but the destination runway's own hold-short *on that taxiway* is the de-facto next waypoint. `RouteNamedToNamed` sets the look-ahead anchor to that hold-short (`ResolveRunwayHoldShortAnchorOnTaxiway`), so `ComputeLookaheadPenalty` steers junction selection toward the junction whose taxiway side leads to the runway. Without it the cheapest (nearest-along-the-previous-taxiway) junction can commit the following `WalkToNaturalTerminus` to the *wrong end* of the final taxiway, after which the correct direction fails the U-turn admissibility gate and the route detours the long way around to the same runway (the OAK `TAXI D J C 33` regression: C reached at the near junction only continues toward A, forcing a loop via B / 28L-10R / P back to 33 — 131 segments collapsed to ~36 once anchored). The anchor is null (no change) when no hold-short for the runway sits on the taxiway — the runway is then reached via a numbered variant or connector, which `TryVariantExtension` handles.

### Mandatory-connector insertion + notification

When two consecutive cleared taxiways have **no direct junction** (zero junction candidates), the resolver bridges them (`TryDetour`) and records a `ConnectorInsertion`. The detour is a bounded `AutoRouter` (capped at `MaxDetourExpansions = 5_000`) that **inherits the authorized set** (soft policy): numbered connectors and RAMP are free, an unnamed letter taxiway carries the `0.2 nm` unauthorized penalty so it loses to a numbered connector but is still usable as a last resort — the detour never fails a resolvable clearance. The materialiser surfaces the inserted connector as an informative notification — *"A and B do not connect directly — taxi via A1"* — **not** an "unauthorized taxiway" warning, and suppresses the generic warning for those connector names (`RouteMaterialiser.BuildWarnings`).

---

## Step-by-step walkthrough — V2 explicit mode (`SegmentExpander.Run`)

`SegmentExpander.Run` (`src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:36`) returns exactly one of `(TaxiRoute, null)` or `(null, PathfindingFailure)`.

**1. Waypoint resolution.** `ResolveWaypoints` turns each token into a `WaypointToken`. `#NNNN` tokens become node-refs (`IsNodeRef = true`); everything else is a named taxiway. The search starts with `head = PartialRoute.StartAt(ctx.StartNodeId)` — an immutable linked-list node (`PartialRoute.cs`) carrying head node id, arrival bearing, last edge, accumulated cost, depth, and a `VisitedNodeIds` set.

**2. Parking→taxiway bridge** (`BridgeStartToTaxiway`, `:339`). If the start node has no edge on the first named taxiway (e.g. parked on a RAMP-only spot), a bounded BFS (≤ `MaxBridgeHops = 3`) walks onto the nearest node carrying that taxiway. Mirrors V1's `BfsToTaxiway`. Without it the first per-segment search finds no on-taxiway edge from the start and degrades to a failing detour. (This was the root of the cluster-C `OAK_TaxiFromParking*` failures.)

**3. `ResolveSequence` with recursive look-ahead** (`:157`). Walks consecutive token pairs. For each non-final pair it calls `ExpandSegment`; the final token goes to `ExpandLastWaypoint`. After each segment it **resets `VisitedNodeIds` to just the new head node**, so a route may intentionally revisit a taxiway (`A E B B3 A B1`) — cycle prevention is per-segment, not global. `ExpandSegment` dispatches:
   - node-ref → named: `RouteFromNodeRefToTaxiway`
   - named → node-ref: `RouteToNodeRef` (→ `RouteToSpecificNode`, which uses `AutoRouter`)
   - named → named: `RouteNamedToNamed` (the common case)

**4. `RouteNamedToNamed`** (`:447`). `FindJunctionCandidates` finds every node on `fromTaxiway` with an edge matching `toTaxiway`. For each candidate, `LocalSearchToJunction` runs a bounded best-first search (A* with the distance heuristic) **constrained to edges on `fromTaxiway`** (plus the direct junction edge), capped at `MaxLocalExpansions = 500`. Each candidate's total score is `cost-to-reach + continuationCost`:
   - `continuationCost = ProbeTailCost(...)` when there is a meaningful tail and look-ahead is on (`:236`) — the recursive one-level probe described above.
   - else `ComputeLookaheadPenalty(...)` — a `10 nm` penalty if the junction's `toTaxiway` edges all point *away* from the next-next taxiway's anchor (would force a U-turn).
   The cheapest survivor is committed. Zero junction candidates → mandatory-connector detour (`TryDetour` + `RecordConnectorInsertion`) at the top level, or `DetourSuppressedFailure` inside a probe.

**5. `WalkToNaturalTerminus`** (`:882`, the final named token). Walks forward along the taxiway one admissible edge at a time until none remain. This is one of the two places **requirement ①** lives: an exact **single-name** edge ranks *strictly above* a junction arc that matches the taxiway only by membership (`edge is GroundArc { IsMembershipTaxiwayJunctionArc: true }` — multi-name and not a runway-crossing arc). A multi-name junction arc is a turn *off* the taxiway, not a continuation; single-name wins regardless of cost, cost only breaks ties within a tier. Runway-crossing arcs (`IsRunwayJunction`, e.g. `H - RWY01L/19R`) *continue* the taxiway across a runway and are excluded. (This greedy one-step walk is a known V2 gap vs. its own multi-candidate discipline — see Caveats.)

**6. Variant resolution** (`TryVariantExtension`, `:1144`). When the destination is a runway and the walked route hasn't already reached a hold-short for it (`RouteReachesRunwayHoldShort`), the expander looks for **numbered variants** of the last named taxiway whose hold-short serves the runway (`B` → `B1`). Exactly one → auto-extend onto it (`ExtendToVariant`, via `LocalSearchToJunction` then `AutoRouter` fallback). More than one (e.g. `W1`…`W7` off `W` to rwy 30) → **auto-pick the variant whose hold-short is nearest the requested runway's threshold** (`RouteMaterialiser.ResolveRunwayThreshold` → `NavigationDatabase` runway threshold), the full-length lineup connector, mirroring V1's `TaxiVariantResolver.PickBestVariant`; only when the threshold is unavailable (no navdata) does it fall back to a `TransitionAmbiguous` failure naming the candidates. Zero variants → try same-name hold-shorts, else stop at natural terminus.

**7. Parking/spot extension.** For `@parking`/`$spot`/helipad destinations, `ExtendToDestination` runs `AutoRouter` from the named-path terminus to the destination node.

**8. `RouteMaterialiser.Materialise`** (`src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:15`). One forward pass: build segments → annotate hold-shorts → truncate (runway destination = exactly at its lineup hold-short; otherwise one past the last required stop — see **Hold-short handling & truncation** above) → build warnings (mandatory-connector notifications + unauthorized-letter-taxiway warnings, with junction arcs and parking-bridge RAMP exempt). Returns the shared `TaxiRoute`.

**9. Honor-named-taxiway check** (`SegmentExpander.Run`, after materialise — `RouteReachesTaxiway(route, name)`). Every named taxiway in the clearance must be **reached** by the resolved route — either *traversed* (an edge labeled for it, membership arcs count) or at least *touched* (the route passes through a node incident to it). Touching without traversing is normal and must not fail: when two cleared taxiways meet at the same junction the route turns from one onto the next through that node without walking a labeled edge of either (e.g. `TE T U` where `TE` and `U` share junction node 136, which is on `T`), and a more direct connector can reach the junction a named taxiway serves (e.g. OAK `G @SIG1`, where the north-side route reaches the G/C junction node 350 via C without crossing a runway). The check runs on the **materialised route**, not the pre-materialise edge walk — the latter can over-run the destination hold-short and reach the taxiway only on the far side of a runway the final route never crosses (which previously produced false rejections). The command **fails** (`TaxiwayNotConnected`) only when a named taxiway is never reached at all: the aircraft could not get to it from its start without leaving the movement area (e.g. SFO gate → taxiway `A` lies across active runways, reachable only via a runway-crossing RAMP bridge the resolver rejects). Distinct from the soft mandatory-connector policy, which inserts a connector *between* named taxiways while keeping every named taxiway present. (Guards: `SfoRampCrossesRunwayTests` for the genuine-failure case; `OakGroundE2ETests` + `RoomEngineE2ETests.OakS1_PushbackThenTaxi` for the reachable-bypassed cases.)

### Auto-route walkthrough (`AutoRouter.Run`)

`AutoRouter.Run` (`src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:27`) is a flat A* over the whole layout, used by `FindRoute`/`FindRoutes`, the explicit-mode parking extension, the detour fallback, and node-ref routing.

- Resolves the destination node (runway → `FindFullLengthLineupHoldShort`; parking/spot/node → the resolved id).
- A* with a `PriorityQueue<PartialRoute, double>`, a `(nodeId, arrival-bearing-bucket)`-keyed `bestGScore` map for state-aware duplicate pruning (`GeometricAdmissibility.PruningStateKey`), and the `GeometricAdmissibility` hard gate on every edge. `IncrementalCost` (the same cost function) prices each edge. Heuristic = straight-line nm. Cap: **`MaxExpansions = 200_000`** → `SearchExhausted` (raised from the design's 50k after SFO cross-field routes legitimately explored 100k+).
- `startOverride` lets callers (detour, extension) seed the search with a prior `PartialRoute` so admissibility fires on the *first* expanded edge against the inherited heading — without it the first edge could U-turn against the aircraft's existing direction.
- The f-score tie-breaker subtracts `Depth * 1e-9` (`AutoRouter.cs:253`); the comment says shallower routes win, but subtracting depth actually favors *deeper* routes in a min-queue — a known minor inconsistency (cosmetic; deterministic either way).

`GeometricAdmissibility` also treats zero-distance edges (`< NoOpEdgeThresholdNm ≈ 1.2 ft`) as no-ops: admitted unconditionally, and downstream propagates the *prior* arrival bearing through them rather than reading the edge's meaningless stored bearing (fillet V1 emits these "phase-d-shorten" pairs at co-located nodes).

---

## Caveats & gotchas

Verify each against current code before relying on it — several are open work items, not finished behavior.

- **Membership junction arcs ("X - Y").** `GroundArc.MatchesTaxiway` matches if the queried name is *any* of the arc's `TaxiwayNames` (`AirportGroundLayout.cs:312`). Fillet V2 retains junction arcs named e.g. `C1 - B`, `A - RAMP`, `A - Q1`. A bare-taxiway walk continuing `B` therefore *sees* a `C1 - B` arc as a valid `B` step. **Requirement ① (single-name continuation beats a membership-only taxiway-junction arc) is now enforced in BOTH walkers**: `WalkToNaturalTerminus` (hard tier) and `LocalSearchToJunction` (soft cost penalty `RouteCostFunction.MembershipJunctionArcContinuationCostNm`, applied to a membership-arc *continuation* — not the junction turn, not a runway-crossing arc). The predicate is `GroundArc.IsMembershipTaxiwayJunctionArc` (`TaxiwayNames.Length >= 2 && !IsRunwayJunction`); runway-crossing arcs (`H - RWY...`) are continuations and are not penalised. A dense two-token sweep across OAK/SFO/FLL (`Req1MembershipArcSweepTests`) guards against real physical diversions (was 29, now 0). **Residual (tracked separately):** fillet V2 emits *parallel-duplicate* corner arcs — an identical single-name twin alongside the membership arc (e.g. coincident `A` and `A - A8` at FLL J35) — so a segment can still carry the membership label even though the physical path is identical; the soft penalty can't always win that label because the `(node,bearing-bucket)` closed set lets the twin claim the slot. Behaviourally benign; the real fix is a fillet-generator dedup of duplicate corner arcs.

- **Collapsed-junction geometry from fillet V2.** V2's edge-split collapses each junction into fewer tangent nodes with *larger* per-corner bearing steps. V1's straightest-continuation heuristics were tuned for V1's denser, gentler nodes; on V2 they pick the wrong edge → dead-end spur / `X→Y→X` reversal (FLL B/C1 765↔767, SFO A 1160↔43). The graph is *correct but different* — do not "fix" it.

- **V-shaped taxiways.** A V-shaped taxiway is one LineString with two legs meeting at an apex. First-match junction selection can strand the route on the wrong leg. The bounded recursive look-ahead (`ResolveSequence` + `ProbeTailCost`) is what defeats this — e.g. FLL `T T4 B B1 HS 10L` now enters T4 at the apex and walks the correct arm. Don't reduce the look-ahead to a fixed-horizon or one-shot geometric check; the probe is load-bearing.

- **Look-ahead is bounded to one level.** Probes run with `enableLookahead: false`, which both bounds recursion and suppresses the detour inside probes. A pathological sequence needing two levels of look-ahead to disambiguate will not get it. This is intentional (recursion bound) but is a real limit.

- **`IsNumberedVariant` must reject digit-bearing bases.** `B10` is **not** a variant of `B1` — `B1` and `B10` are both siblings under base `B`. `IsNumberedVariant` (`SegmentExpander.cs:1279`) returns false when the base contains any digit, preventing the false positive that made a `B1`→runway hold-short look ambiguous against `B10`/`B11`. A digit-bearing base is a leaf connector with no further numbered variants.

- **A* pruning is state-aware (`(nodeId, bearing-bucket)`-keyed).** Both `AutoRouter.RunAstar` and `LocalSearchToJunction` close the open set by `GeometricAdmissibility.PruningStateKey(nodeId, arrivalBearing)` (1° buckets), not node id alone — because onward-edge admissibility depends on arrival bearing, so a cheaper dead-end arrival must not suppress the only admissible different-bearing arrival. Keying uses the *propagated* arrival bearing (no-op-edge-aware). A dense V2-fillet sweep (`StateAwarePruningNecessityTests`, OAK/SFO/FLL, 8,294 pairs) proved node-id keying produced 10 false `DestinationUnreachable` (every runway → OAK `S8B`, FLL `SHE4`) + 785 sub-optimal routes; the fix drives all three counts to zero. That sweep is now a standing guard (asserts HARD==0/ANOMALY==0 against an independent oracle, so a regression to node-id keying re-fails it). The heuristic is bearing-independent, so A* optimality is preserved within the `(node,bucket)` state space.

- **Auto-route 200k expansion cap.** `AutoRouter.MaxExpansions = 200_000`. SFO cross-field routes legitimately explore 100k+, so this is a real ceiling, not a safety margin — a latency footgun on degenerate/disconnected layouts. There is no CI latency budget yet.

- **`WalkToNaturalTerminus` is still one-step greedy.** It picks the best immediate next edge per step (with the req-① tier rule), not a multi-candidate search — the same lock-in pattern V2 was meant to eliminate, scoped to terminus walks. Acceptable on forced-next-edge topologies; risky where the terminus branches.

- **V2 was historically validated only on Legacy fillets.** Restated because it is the dominant risk: any green test you see may have run V2 over V1 geometry. Validate new V2 work with V2-pathfinder-on-V2-fillet (the ship config) — `LayoutInspector --fillet-mode v2` inspects the V2 graph directly. The full all-V2 suite is a final gate, not a discovery tool (it can hang on ground deadlocks / latency spikes); drive work with targeted scoped tests.

---

## V1 — removed

The V1 `TaxiPathfinder` (~3200 lines), its `TaxiPathfinderV1Adapter`, the `TaxiPathfinderRouter` / `ITaxiPathfinder` selector seam, and the V1-only `TaxiVariantResolver` (the `W` → `W1` variant walker — V2's `SegmentExpander` has its own variant handling) were deleted when V2 became the only pathfinder. `WalkOptions` and `ExplicitPathOptions.EnableLookahead` were V1-only and are gone. The shared input types `RoutePreference` and `ExplicitPathOptions` now live in their own `ExplicitPathOptions.cs` (V2 reads a subset of the option bag). `HoldShortAnnotator` survives (it is shared). Per project policy there are **no compatibility shims** — do not add V1 fixes; fix forward in V2.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` | pathfinder entry (static) — compiles `SearchContext`, delegates to drivers |
| `src/Yaat.Sim/Data/Airport/ExplicitPathOptions.cs` | `RoutePreference` enum + `ExplicitPathOptions` input bag |
| `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs` | explicit-mode driver (junctions, look-ahead, variants, detour) |
| `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs` | auto-mode A* over the full layout |
| `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs` | the single cost function + heuristic |
| `src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs` | heading-delta gate + bearing helpers |
| `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs` | edges → `TaxiRoute` (hold-shorts, truncation, warnings) |
| `src/Yaat.Sim/Data/Airport/Pathfinding/SearchContext.cs` | compiled per-call context, authorized-set + destination resolution |
| `src/Yaat.Sim/Data/Airport/Pathfinding/PartialRoute.cs` | immutable linked-list search state |
| `src/Yaat.Sim/Data/Airport/Pathfinding/PathfindingFailure.cs` | structured failure + `FailureKind` |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | graph types: `GroundNode`, `GroundArc`, `IGroundEdge`, `DirectionalEdge`, `MatchesTaxiway`, `GetNodesOnTaxiway`, `GetRunwayHoldShortNodes` |
| `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` | the shared output type |
| `src/Yaat.Sim/Commands/GroundCommandHandler.cs` | `TryTaxi`/`TryTaxiAuto` entry + downstream clearance handling |
| `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` (legacy) | V1 — being deleted |

**Plan docs (transient, this doc supersedes them):** `docs/plans/ground-graph-v2.md` (transition status), `docs/plans/pathfinderv2/{design,requirements,default-flip-triage}.md`. **Tooling:** `tools/Yaat.LayoutInspector` with `--fillet-mode legacy|v2|none` to inspect the V2 graph the pathfinder walks.
