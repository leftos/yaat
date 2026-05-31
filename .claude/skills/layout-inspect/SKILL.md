---
name: layout-inspect
description: "Query airport ground graph topology via LayoutInspector CLI tool"
---

# Layout Inspector

Query the airport ground graph for debugging ground/taxi/exit bugs. Wraps `tools/Yaat.LayoutInspector`.

GeoJSON airport files come from:
- The vNAS training-airports API, fetched on demand via `--airport <FAA>` (cached at `%LOCALAPPDATA%/yaat/cache/airports/`). Preferred — works in any worktree without sibling-repo dependencies.
- `tests/Yaat.Sim.Tests/TestData/<icao>.geojson` (committed subset for tests: oak, sfo, fll, fat, hwd, mer, rno, sjc). Pass these paths positionally when offline.

## Usage

When the user asks about airport topology, runway exits, taxiway routing, node connectivity, or ground graph issues, use LayoutInspector to answer.

In every command below `<geojson>` is either a positional path (e.g. `tests/Yaat.Sim.Tests/TestData/oak.geojson`) or `--airport <FAA>` (e.g. `--airport OAK`) which downloads and caches the layout under `%LOCALAPPDATA%/yaat/cache/airports/`. The two forms are mutually exclusive.

### Common Queries

**Find all exits from a runway:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --exits <RWY> --json 2>&1 | tee .tmp/li-exits.log
```

**Trace a multi-hop exit path from a node through a taxiway:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --bfs <NodeID> <Taxiway> --json 2>&1 | tee .tmp/li-bfs.log
```

**Resolve a full pathfinder route (preferred for taxi-bug investigation — matches what `GroundCommandHandler.TryTaxi` does at runtime):**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --pathfinder <NodeID> T1 T2 T3 --pf-dest-rwy 28R 2>&1 | tee .tmp/li-pf.log
```

**Inspect a specific node's connectivity:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --node <NodeID> --json 2>&1 | tee .tmp/li-node.log
```

**Inspect multiple nodes in one invocation (`--node` is repeatable):**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --node 619 --node 621 --node 1220 --node 1222 --node 1224 2>&1 | tee .tmp/li-cluster.log
```

**Inspect a node and everything within N graph hops (`--node-depth`):**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --node 621 --node-depth 1 2>&1 | tee .tmp/li-621-d1.log
# Useful for dumping a fillet cluster without listing every member id by hand.
```

**Diagnose junction corners — fan/turn angle of every edge pair + the bridging taxiway (`--node-angles`):**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --fillet-mode none --node 349 --node-angles 2>&1 | tee .tmp/li-angles.log
# Per pair: fan=included angle (0=parallel, 180=straight-through), turn=deflection (180-fan; high=sharp/un-filletable),
# and the shortest alternate path avoiding the node — "bridge via [G]" means another taxiway already joins the pair,
# so a direct corner-chord between them is redundant. Pairs sorted tightest-turn-first. Run on --fillet-mode none for
# the clean raw fan; on --fillet-mode standard to see the split tangent-cut nodes.
```

**Show all nodes on a taxiway:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --taxiway <T> --json 2>&1 | tee .tmp/li-twy.log
```

**Inspect runway centerline and hold-short nodes:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --runway <RWY> --json 2>&1 | tee .tmp/li-rwy.log
```

**Dump entire airport to JSON for grepping:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --dump 2>&1 | tee .tmp/li-dump.json
```

**Generate interactive HTML visualization:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --html .tmp/airport.html 2>&1 | tee .tmp/li-html.log
```

### Flags Reference

Most list-valued flags are **repeatable AND accept comma-separated values** — `--node 619 --node 621` and `--node 619,621` are equivalent. Mix freely.

| Flag | Repeatable / CSV | Purpose |
|------|------------------|---------|
| `--node N` | yes / yes | Node detail with edges |
| `--node-depth N` | no | Expand each `--node` to include neighbors within N graph hops |
| `--taxiway T` | yes / yes | All nodes/edges on a taxiway |
| `--runway 28R` | yes / yes | Centerline + hold-short nodes |
| `--exits 28R` | yes / yes | BFS-discovered exits with angle/side/high-speed classification |
| `--bfs N T` | no | BFS trace from node N through taxiway T to hold-short |
| `--walk-trace N T` | no | Detailed walker trace (developer diagnostic) |
| `--pathfinder N T1 T2 ...` | no (greedy) | Resolve explicit taxi route through taxiway sequence |
| `--pf-dest-rwy R` | no | Destination runway hint for `--pathfinder` |
| `--pf-hold-shorts HS` | yes / yes | Explicit hold-short targets for `--pathfinder` |
| `--pf-dest-parking P`, `--pf-dest-spot S`, `--pf-dest-node N` | no | Pathfinder destinations |
| `--exit-query RWY TWY [SIDE]` | yes | Repeated targeted exit-query diagnostic |
| `--intersection T1 T2` | no | Find taxiway intersection node |
| `--distance N1 N2` | no | Straight-line (great-circle) distance + bearing between two nodes (ft + nm + °true) |
| `--path-distance N1 N2 ...` | no (greedy) / yes | Cumulative distance + per-leg bearing along a node sequence; per-leg uses the graph edge (arc-aware) where one exists, else great-circle. Also reports heading range + total absolute turn (large = tracks a curve, near-zero = beeline) |
| `--parking` / `--spots` | flag | Show parking / spot positions |
| `--dump` | flag | Full airport JSON |
| `--json` | flag | JSON output mode |
| `--no-fillets` | flag | Parse without fillet arcs |
| `--debug-fillets` / `--debug-exits` | flag | Enable subsystem debug logging |
| `--validate` | flag | Run graph validation checks |
| `--html <path>` | no | Interactive HTML render (pan/zoom, URL-persisted view) |
| `--html-taxiway T` / `--html-runway R` / `--html-node N` / `--html-route N` | yes / yes | Highlight overlays for `--html` |
| `--html-annotate NODE TEXT` | yes | Add a labeled annotation at a node |
| `--ticks <csv>` | no | Overlay TickRecorder CSV as animated path |
| `--tick-table` / `--tick-summary` | flag | Text-table analysis of a TickRecorder CSV |
| `--tick-range LO-HI` | no | Filter tick analysis to inclusive range |
| `--tick-ref ICAO/RWY` | no | Reference runway for xte/hdgErr columns |
| `--tick-hold-shorts HS` | yes / yes | Distance columns to named hold-shorts |
| `--tick-callsign CS` | no | Filter tick CSV to one callsign |
| `--airport-code ICAO` | no | Override airport code (rare) |
| `--navdata <dir>` | no | Override NavData directory |

### Tips

- Always use `--json` for machine-readable output when analyzing programmatically.
- Use `--debug-fillets` when investigating fillet arc issues.
- Combine flags: `--exits 28R --json` gives structured exit data.
- For repeatable flags, prefer the inline CSV form for short lists (`--node 619,621,1222`) and repeated `--node` for long ones (cleaner shell history).
- For KOAK: `--airport OAK` (fetches and caches) or `tests/Yaat.Sim.Tests/TestData/oak.geojson` (committed test data).
