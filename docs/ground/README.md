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
| 1 | **Fillet generator** | builds the graph geometry — smooth corner arcs + order-independent junction connectivity | [`fillet-generator.md`](./fillet-generator.md) | `FilletArcGeneratorV2` · `Data/Airport/Fillet/V2/*` |
| 2 | **Pathfinder** | resolves a clearance into a `TaxiRoute` over that graph | [`pathfinder.md`](./pathfinder.md) | `TaxiPathfinderV2` · `Data/Airport/V2/*` |
| 3 | **Navigator** | follows the route + arc geometry per tick (heading/speed) | [`navigator.md`](./navigator.md) | `GroundNavigator` (in `TaxiingPhase`) |

## The V1 → V2 transition (read this first)

The stack is mid-migration from V1 to V2, and **all three layers flip together in one change**. Each was co-tuned against V1 geometry, so flipping one alone leaves the stack mismatched.

- **Fillet generator** and **pathfinder** each have full V1 and V2 implementations. **V1 is the runtime default today; V2 is the target** and where all new work goes. V1 of each is deleted after the joint flip. The docs describe **V2 as the architecture** and flag V1 as legacy-being-removed.
- The **navigator** is *shared* and not rewritten — it is an incremental **v1.1**. The transition that matters for it is the **geometry it consumes**: it was tuned against Legacy fillet arcs, and V2's tighter, cleaner arcs (fewer tangent nodes, collapsed junctions) expose latent issues. Several navigator features are Legacy-fillet compensations under review.

**Guiding principle when a consumer trips on V2 geometry:** the V2 graph is *correct-but-different*, not broken — it faithfully mirrors the source data (coincident edges, taxiways that connect only via a third connector, membership-named junction arcs). **Adapt the consumer; do not "fix" the graph.**

The flip is sequenced fillet → pathfinder → navigator-review, all shipped together once green. Live roadmap and open work: [`../plans/ground-graph-v2.md`](../plans/ground-graph-v2.md) (and the `../plans/filletv2/` · `../plans/pathfinderv2/` sub-plans, which these docs supersede for durable reference).

## Tooling

`tools/Yaat.LayoutInspector` is the workhorse for all three layers — `--fillet-mode none|legacy|v2` builds the graph each way, `--node`/`--dump`/`--html` inspect topology and routes, `--ticks`/`--tick-table` analyze a recorded aircraft trajectory, and `--debug-fillets` enables verbose fillet logging. See the per-layer docs for which flags matter where.
