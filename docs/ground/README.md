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

**Pushback** is a separate ground-movement mechanism (tail-first tug reverse, not a taxi route) — see [`pushback.md`](./pushback.md) · `PushbackPhase`.

**Runway hold-short bars** are seated at graph-build time, *before* the fillet generator — the constant perpendicular standoff from the runway centerline, angle-independent. See [`hold-short-placement.md`](./hold-short-placement.md) · `RunwayCrossingDetector`.

## Design principle

When a consumer trips on the ground geometry, remember: **the graph is correct-but-different, not broken** — it faithfully mirrors the source data (coincident edges, taxiways that connect only via a third connector, membership-named junction arcs). **Adapt the consumer; do not "fix" the graph.**

## Tooling

`tools/Yaat.LayoutInspector` is the workhorse for all three layers — `--fillet-mode none|standard` builds the graph with or without fillet arcs, `--node`/`--dump`/`--html` inspect topology and routes, `--ticks`/`--tick-table` analyze a recorded aircraft trajectory, and `--debug-fillets` enables verbose fillet logging. See the per-layer docs for which flags matter where.
