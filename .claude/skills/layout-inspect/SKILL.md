---
name: layout-inspect
description: "Query airport ground graph topology via LayoutInspector CLI tool"
---

# Layout Inspector

Query the airport ground graph for debugging ground/taxi/exit bugs. Wraps `tools/Yaat.LayoutInspector`.

GeoJSON airport files are at: `X:\dev\vzoa\training-files\atctrainer-airport-files\`

## Usage

When the user asks about airport topology, runway exits, taxiway routing, node connectivity, or ground graph issues, use LayoutInspector to answer.

### Common Queries

**Find all exits from a runway:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --exits <RWY> --json 2>&1 | tee .tmp/li-exits.log
```

**Trace a multi-hop exit path from a node through a taxiway:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --path <NodeID> <Taxiway> --json 2>&1 | tee .tmp/li-path.log
```

**Inspect a specific node's connectivity:**
```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --node <NodeID> --json 2>&1 | tee .tmp/li-node.log
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
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- <geojson> --svg .tmp/airport.html 2>&1 | tee .tmp/li-svg.log
```

### Flags Reference

| Flag | Purpose |
|------|---------|
| `--node N` | Single node detail with edges |
| `--taxiway T` | All nodes/edges on a taxiway |
| `--runway 28R` | Centerline + hold-short nodes |
| `--exits 28R` | BFS-discovered exits with angle/side/high-speed classification |
| `--path N T` | BFS trace from node N through taxiway T to hold-short |
| `--pf N T [T2...]` | Pathfinder route from node through taxiway sequence |
| `--parking` | Show parking positions |
| `--spots` | Show spot/deice positions |
| `--dump` | Full airport JSON |
| `--json` | JSON output mode |
| `--no-fillets` | Parse without fillet arcs |
| `--debug-fillets` | Enable fillet debug logging |
| `--validate` | Run graph validation checks |
| `--svg path.svg` | Static SVG render |
| `--svg path.html` | Interactive HTML render |

### Tips

- Always use `--json` for machine-readable output when analyzing programmatically.
- Use `--debug-fillets` when investigating fillet arc issues.
- Combine flags: `--exits 28R --json` gives structured exit data.
- For KOAK: `X:\dev\vzoa\training-files\atctrainer-airport-files\KOAK.geojson`
