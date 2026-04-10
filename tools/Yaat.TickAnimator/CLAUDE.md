# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Yaat.TickAnimator renders animated GIFs/videos of aircraft movement over an airport ground layout. It takes a tick data CSV (time, lat, lon, heading, groundspeed, phase, taxiway) and an airport GeoJSON file, then produces per-frame PNGs and optionally stitches them via ffmpeg.

Part of the YAAT monorepo. References `Yaat.Sim` for airport layout parsing (`GeoJsonParser`), aircraft dimensions (`FaaAircraftDatabase`), and nav data (`TestVnasData`).

## Build & Run

```bash
dotnet build                            # Build
dotnet run --project tools/Yaat.TickAnimator -- --layout <geojson> --ticks <csv>
```

Requires ffmpeg on PATH for GIF/MP4 output. Without it, individual PNG frames are saved to `<output-dir>/frames/`.

## Architecture

Two files, no tests:

- **Program.cs** — CLI arg parsing, tick CSV loading, nav data bootstrap, orchestration. `Options` record holds all CLI params. `TickRecord` holds one row of the CSV.
- **FrameRenderer.cs** — SkiaSharp rendering. Equirectangular projection with cos(lat) correction. Draws layout (runways, taxiway edges, fillet arcs, hold-short nodes), aircraft trail dots, aircraft shape (fuselage/wings/nose/tail), and HUD overlay. GIF creation uses two-pass ffmpeg palettegen; MP4 uses libx264.

Viewport fits either the tick data bounds or entire airport layout (`--fit-layout`). Aircraft dimensions come from `FaaAircraftDatabase` lookup by ICAO type code.

## Tick CSV Format

```
t,lat,lon,hdg,gs,phase,twy
0,37.615000,-122.390000,280.0,15.0,TaxiPhase,A
```

Header line starting with `t,` is skipped. Columns beyond `gs` (index 4) are optional.
