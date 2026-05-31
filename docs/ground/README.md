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

## The V1 → V2 transition (complete)

The stack has fully migrated from V1 to V2. The **fillet generator**, **pathfinder**, and **navigator** are each **V2-only** — the V1/Legacy implementations and their selector seams (`FilletArcGeneratorRouter`, `ITaxiPathfinder` / `TaxiPathfinderRouter`, `IGroundNavigator` / `GroundNavigatorRouter`) were deleted layer by layer at the joint flip. The navigator was a *shared* incremental **v1.1** (not a clean rewrite); it was tuned against Legacy fillet arcs and several of its features were Legacy-fillet compensations, now resolved against V2's tighter geometry (see [navigator.md](./navigator.md)).

**Guiding principle when a consumer trips on V2 geometry:** the V2 graph is *correct-but-different*, not broken — it faithfully mirrors the source data (coincident edges, taxiways that connect only via a third connector, membership-named junction arcs). **Adapt the consumer; do not "fix" the graph.**

The cross-layer rename has landed: the three layers are now `FilletArcGenerator` / `TaxiPathfinder` / `GroundNavigator` (no `V2` suffix), with their internals under `Data/Airport/Fillet/` and `Data/Airport/Pathfinding/`. Still pending: a doc-body prose refresh of the V1-era detail sections in these docs. Live roadmap and open work: [`../plans/ground-graph-v2.md`](../plans/ground-graph-v2.md) (and the `../plans/filletv2/` · `../plans/pathfinderv2/` sub-plans, which these docs supersede for durable reference).

## Tooling

`tools/Yaat.LayoutInspector` is the workhorse for all three layers — `--fillet-mode none|legacy|v2` builds the graph each way, `--node`/`--dump`/`--html` inspect topology and routes, `--ticks`/`--tick-table` analyze a recorded aircraft trajectory, and `--debug-fillets` enables verbose fillet logging. See the per-layer docs for which flags matter where.
