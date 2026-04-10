# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Yaat.LayoutInspector is a CLI tool for querying and visualizing airport ground graphs parsed from GeoJSON. It loads an airport layout via `GeoJsonParser` from `Yaat.Sim` and exposes queries over nodes, edges, taxiways, runways, exits, and BFS paths. Primary use: debugging ground/taxi/exit bugs in the YAAT simulator.

## Build & Run

```bash
dotnet build
dotnet run -- <geojson-path> [flags]
```

GeoJSON files live in `X:\dev\vzoa\training-files\atctrainer-airport-files\`. NavData is auto-discovered from `tests/Yaat.Sim.Tests/TestData/` (walks up to `yaat.slnx`); override with `--navdata <dir>`.

## Architecture

```
Program.cs          CLI entry point, arg parsing, dispatches to LayoutAnalyzer/renderers
LayoutAnalyzer.cs   Core query engine over AirportGroundLayout (from Yaat.Sim)
QueryResults.cs     Record DTOs for all query results (OverviewResult, NodeInfo, ExitsResult, etc.)
IFormatter.cs       Output interface: text vs JSON
TextFormatter.cs    Human-readable stdout output
JsonFormatter.cs    JSON stdout output
SvgRenderer.cs      Static SVG file rendering with highlights/annotations/route overlay
HtmlRenderer.cs     Interactive HTML+Canvas rendering (pan/zoom/tooltips); uses inspector-template.html
```

**Data flow:** GeoJSON file -> `GeoJsonParser.Parse()` -> `AirportGroundLayout` -> `LayoutAnalyzer` wraps it -> queries return `QueryResults` records -> `IFormatter` or renderer outputs them.

**Output modes are mutually exclusive:** `--svg`/`.html` output returns immediately; `--dump` returns full JSON; otherwise queries go through `IFormatter` (text or `--json`).

## Key Flags

| Flag | Purpose |
|------|---------|
| `--node N` | Single node detail with edges |
| `--taxiway T` | All nodes/edges on a taxiway |
| `--runway 28R` | Centerline + hold-short nodes |
| `--exits 28R` | BFS-discovered exits with angle/side/high-speed classification |
| `--path N T` | BFS trace from node N through taxiway T to hold-short |
| `--dump` | Full airport JSON (pipe to file for grepping) |
| `--no-fillets` | Parse without fillet arcs for comparison |
| `--svg path.svg` | Static SVG render |
| `--svg path.html` | Interactive HTML render (same flag, extension decides) |
| `--svg-taxiway`, `--svg-runway`, `--svg-node`, `--svg-annotate`, `--svg-route` | Highlight/overlay options (repeatable) |

## Dependencies

Single project reference to `Yaat.Sim`. Uses `Yaat.Sim.Testing.TestVnasData` to optionally load NavData for accurate runway widths. No other dependencies.
