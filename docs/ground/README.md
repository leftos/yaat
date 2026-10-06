# Ground Stack — Taxi Geometry, Routing & Following

The **ground stack** turns an airport's raw GeoJSON into a graph an aircraft can taxi, resolves a controller's `TAXI`/`TAXIAUTO` clearance into a route over that graph, and physically steers the aircraft along it tick by tick. It is three layers, each with its own design doc. **Read the relevant doc before touching that layer.**

```
GeoJSON ─► TaxiwayGraphBuilder ─► [1] Fillet generator ─► filleted ground graph
                                       (corner arcs + edge-split)        │
                                                                         ▼
        TAXI / TAXIAUTO command ─────────────────────► [2] Pathfinder ─► TaxiRoute
                                                          (edge sequence + hold-shorts)
                                                                         │
                                                                         ▼
                                            per tick ◄── [3] Navigator follows the route
                                                          (steers heading + speed)
```

| # | Layer | Role | Doc | Core code |
|---|-------|------|-----|-----------|
| 1 | **Fillet generator** | builds the graph geometry — smooth corner arcs + order-independent junction connectivity | [`fillet-generator.md`](./fillet-generator.md) | `FilletArcGenerator` · `Data/Airport/Fillet/*` |
| 2 | **Pathfinder** | resolves a clearance into a `TaxiRoute` over that graph | [`pathfinder.md`](./pathfinder.md) | `TaxiPathfinder` · `Data/Airport/Pathfinding/*` |
| 3 | **Navigator** | follows the route + arc geometry per tick (heading/speed) | [`navigator.md`](./navigator.md) | `GroundNavigator` (in `TaxiingPhase`) |

**Pushback and tug moves** are a separate ground-movement mechanism (a tug pushing tail-first or pulling nose-first along a planned chain of moves, not a taxi route) — see [`pushback.md`](./pushback.md) · `TugKinematics`, `TugMovePlanner`, `TugPathCheck`, `PushbackPhase`.

**Runway hold-short bars** are seated at graph-build time, *before* the fillet generator — the constant perpendicular standoff from the runway centerline, angle-independent. See [`hold-short-placement.md`](./hold-short-placement.md) · `RunwayCrossingDetector`.

**Ground follow (`FOLLOWG`)** is checked when issued: the follower must be able to join the lead's taxi path (its trail, the edge it is on, its remaining route) through the pathfinder's goal-set search, and must not stand ahead of the lead on it. See [`navigator.md`](./navigator.md#followg-joining-the-leads-taxi-path) · `FollowRoutePlanner`, `TaxiPathfinder.FindRouteToNearestGoal`. `FollowingPhase` then drives that plan over the taxiways through its own navigator, giving way at the merge and stopping at runway bars it has no crossing for: [`navigator.md`](./navigator.md#followg-driving-the-follow-route).

## Decided ground-movement rules not built yet

- **Air taxi across runways (`ATXI`).** `AirTaxiPhase` flies direct from the ramp to the target and coordinates no runway crossing on the way, though an air taxi is a ground movement that needs an explicit clearance for every runway it crosses (AIM 4-3-18.a.5 via 4-3-17.b.3; 7110.65 §3-7-2.a.3, carried by §3-11-1.c's `VIA (route)` clause). The rule: keep the direct path, and at issue time warn the instructor naming each runway the direct path crosses.
- **Taxi append.** A taxi command that adds taxiways, and optionally a new destination, after the part of the current taxi route the aircraft has not yet covered, keeping its stored clearances instead of replacing the route. A new destination replaces the old one; a remaining route never falls back to a plain `TAXI`; an append that does not connect to the uncovered route is refused. The open decisions are in [plans/taxi-append.md](../plans/taxi-append.md).

## Design principle

When a consumer trips on the ground geometry, remember: **the graph is correct-but-different, not broken** — it faithfully mirrors the source data (coincident edges, taxiways that connect only via a third connector, membership-named junction arcs). **Adapt the consumer; do not "fix" the graph.**

## Tooling

`tools/Yaat.LayoutInspector` is the workhorse for all three layers — `--fillet-mode none|standard` builds the graph with or without fillet arcs, `--node`/`--dump`/`--html` inspect topology and routes, `--ticks`/`--tick-table` analyze a recorded aircraft trajectory, and `--debug-fillets` enables verbose fillet logging. See the per-layer docs for which flags matter where.
