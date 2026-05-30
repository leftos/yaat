# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Yaat.LayoutInspector is a CLI tool for querying and visualizing airport ground graphs parsed from GeoJSON, **and** for post-hoc analysis of per-tick aircraft state CSVs written by `Yaat.Sim.Tests.Helpers.TickRecorder`. It loads an airport layout via `GeoJsonParser` from `Yaat.Sim`, exposes queries over nodes/edges/taxiways/runways/exits/paths, renders interactive HTML maps, and prints tick-by-tick text tables. Primary use: debugging ground/taxi/exit bugs in the YAAT simulator.

## Build & Run

```bash
dotnet build
dotnet run -- <geojson-path> [flags]
```

GeoJSON files are sourced two ways: pass a positional `<geojson-path>` (e.g. `tests/Yaat.Sim.Tests/TestData/oak.geojson`) or `--airport <FAA>` (e.g. `--airport OAK`) which fetches from the vNAS training-airports API and caches under `%LOCALAPPDATA%/yaat/cache/airports/` via `AirportLayoutDownloader`. The two forms are mutually exclusive. NavData is auto-discovered from `tests/Yaat.Sim.Tests/TestData/` (walks up to `yaat.slnx`); override with `--navdata <dir>`.

## Architecture

```
Program.cs                  Thin entry point: parse args → bootstrap → dispatch ICommand
CliOptions.cs               Options record + TryParse (all arg parsing lives here)
UsageText.cs                --help output
Bootstrap.cs                NavData + debug logger wiring
Commands/
  ICommand.cs               int Execute(LayoutAnalyzer, CliOptions)
  HtmlRenderCommand.cs      --html <path>: render interactive HTML
  DumpCommand.cs            --dump: full layout JSON
  QueryCommand.cs           Default text/json query dispatch (--taxiway, --runway, etc.)
  TickTableCommand.cs       --tick-table / --tick-summary: CSV → stdout text table
Tick/
  TickDataRow.cs            Shared record for one TickRecorder CSV row
  TickCsvReader.cs          CSV → List<TickDataRow>
  RunwayReference.cs        Runway centerline for xteFt / hdgErr columns
  HoldShortResolver.cs      Resolve --tick-hold-shorts names → GroundNode + distance math
LayoutAnalyzer.cs           Core query engine over AirportGroundLayout (from Yaat.Sim)
LayoutValidator.cs          Airport-layout sanity checks (run via --validate)
QueryResults.cs             Record DTOs for all query results
IFormatter.cs               Output interface: text vs JSON
TextFormatter.cs            Human-readable stdout for query results
JsonFormatter.cs            JSON stdout for query results
TickTableFormatter.cs       Fixed-width stdout for tick-table / tick-summary
HtmlRenderer.cs             Interactive HTML+Canvas rendering; uses inspector-template.html
inspector-template.html     Client-side pan/zoom/tick-overlay; URL-hash persisted view
```

**Data flow:** GeoJSON file -> `GeoJsonParser.Parse()` -> `AirportGroundLayout` -> `LayoutAnalyzer` wraps it -> `ICommand.Execute` reads options and produces output through a formatter or renderer.

**Output modes are mutually exclusive:** `--html` → `HtmlRenderCommand`; `--dump` → `DumpCommand`; `--tick-table`/`--tick-summary` → `TickTableCommand`; otherwise → `QueryCommand` (text or `--json`).

## Key Flags

### Graph queries
| Flag | Purpose |
|------|---------|
| `--node N` | Single node detail with edges |
| `--taxiway T` | All nodes/edges on a taxiway |
| `--runway 28R` | Centerline + hold-short nodes |
| `--exits 28R` | BFS-discovered exits with angle/side/high-speed classification |
| `--bfs N T` | BFS trace from node N through taxiway T to hold-short |
| `--pathfinder N T1 T2 ...` | Resolve explicit taxi route with diagnostic trace |
| `--distance N1 N2` | Straight-line (great-circle) distance between two nodes (ft + nm) |
| `--path-distance N1 N2 ...` | Cumulative distance along a node sequence; per-leg uses the graph edge (arc-aware) where one exists, else great-circle |
| `--dump` | Full airport JSON (pipe to file for grepping) |
| `--no-fillets` | Parse without fillet arcs for comparison |
| `--fillet-mode <m>` | Fillet generator: `legacy` (default), `v2`, or `none` (alias for `--no-fillets`) |

### HTML render
| Flag | Purpose |
|------|---------|
| `--html <path>` | Interactive HTML render |
| `--html-taxiway`, `--html-runway`, `--html-node`, `--html-annotate`, `--html-route` | Highlight/overlay options (repeatable) |
| `--ticks <csv>` | Overlay a TickRecorder CSV as an animated aircraft path |

Pan/zoom state is persisted in `location.hash` — refreshing the page preserves the current view.

### Tick-table (text analysis of TickRecorder CSV)
| Flag | Purpose |
|------|---------|
| `--tick-table` | Compact per-tick text table to stdout (requires `--ticks`) |
| `--tick-summary` | Per-segment summary (one row per nav-target run) |
| `--tick-range START-END` | Filter ticks to an inclusive range |
| `--tick-ref ICAO/RWY` | Reference runway for signed cross-track (`xteFt`) and heading-error (`hdgErr`) columns |
| `--tick-hold-shorts K,D,Q` | Along-track + straight-line distance columns to named hold-shorts (requires `--tick-ref`) |

Example:
```bash
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson \
    --ticks .tmp/dal2581-rollout.csv --tick-table --tick-ref SFO/28L --tick-hold-shorts K,D,Q
```

## Dependencies

Single project reference to `Yaat.Sim`. Uses `Yaat.Sim.Testing.TestVnasData` to optionally load NavData for accurate runway widths and `--tick-ref` lookups. `Microsoft.Extensions.Logging` for `--debug-fillets`/`--debug-exits`. No other dependencies.
