# Tick Animator — Visualizing Aircraft Movement in Tests

Animates tick-by-tick aircraft state over an airport ground layout. Use it to
visually evaluate how aircraft move during unit tests — landings, runway exits,
taxi paths, etc.

## Quick Start

### 1. Record ticks in a test

Add `TickRecorder` to any test that runs a simulation loop:

```csharp
using Yaat.Sim.Tests.Helpers;

// After setting up engine + aircraft...
var recorder = new TickRecorder(aircraft);

for (int t = 1; t <= 300; t++)
{
    engine.TickOneSecond();
    recorder.Record(t);

    // Break when done (e.g. after exit completes)
    if (aircraft.Phases?.CurrentPhase?.Name?.Contains("Hold") == true)
    {
        break;
    }
}

// Write CSV to repo .tmp/ directory
string repoRoot = TickRecorder.FindRepoRoot();
string csvPath = Path.Combine(repoRoot, ".tmp", "my-test-ticks.csv");
recorder.WriteCsv(csvPath);
output.WriteLine($"Wrote {recorder.Count} ticks to {csvPath}");
```

### 2. Optional: filter ticks

Only record ticks matching a condition:

```csharp
var recorder = new TickRecorder(aircraft)
{
    Filter = ac => ac.IsOnGround,  // only record ground movement
};
```

### 3. Generate the animation

```bash
dotnet run --project tools/Yaat.TickAnimator -- \
  --layout tests/Yaat.Sim.Tests/TestData/oak.geojson \
  --ticks .tmp/my-test-ticks.csv \
  --aircraft B738 \
  --output .tmp/my-test.gif
```

## CLI Options

| Option | Default | Description |
|--------|---------|-------------|
| `--layout <geojson>` | (required) | Airport GeoJSON file |
| `--ticks <csv>` | (required) | Tick data CSV |
| `--output <path>` | `.tmp/ticks.gif` | Output file (.gif or .mp4) |
| `--aircraft <type>` | `B738` | ICAO aircraft type for dimensions |
| `--padding <nm>` | `0.05` | Padding around tick bounds |
| `--width <px>` | `800` | Frame width in pixels |
| `--fps <n>` | `10` | Frames per second |
| `--trail <n>` | `30` | Number of trail dots |
| `--start <t>` | (all) | Start at tick t (re-fits viewport) |
| `--end <t>` | (all) | End at tick t |
| `--fit-layout` | off | Fit viewport to entire airport layout |

## Examples

### Zoom into the exit portion of an OAK runway 30 W6 exit

```bash
dotnet run --project tools/Yaat.TickAnimator -- \
  --layout tests/Yaat.Sim.Tests/TestData/oak.geojson \
  --ticks .tmp/oak-30-W6-ticks.csv \
  --aircraft B738 \
  --start 80 \
  --padding 0.03 \
  --width 1000 \
  --output .tmp/oak-30-W6-exit.gif
```

### Full airport view of approach + landing + exit

```bash
dotnet run --project tools/Yaat.TickAnimator -- \
  --layout tests/Yaat.Sim.Tests/TestData/oak.geojson \
  --ticks .tmp/oak-30-W6-ticks.csv \
  --aircraft B738 \
  --fit-layout \
  --width 1200 \
  --output .tmp/oak-30-W6-full.gif
```

## CSV Format

Column order: `t,lat,lon,hdg,gs,phase,twy`

```csv
t,lat,lon,hdg,gs,phase,twy
1,37.69114631,-122.19874017,310.12,132.61,FinalApproach,
2,37.69154562,-122.19933901,310.12,134.60,FinalApproach,
...
99,37.71955092,-122.24026324,67.61,11.26,Runway Exit,W6
100,37.71956049,-122.24022673,72.40,0.00,Holding After Exit,W6
```

## How It Works

1. Parses the airport GeoJSON via `GeoJsonParser` (same as the sim engine)
2. Reads tick CSV
3. Computes viewport bounds from tick positions (or layout if `--fit-layout`)
4. Renders each tick as a frame using SkiaSharp:
   - Airport layout: runways (thick gray), taxiways (blue lines), hold-short nodes (red dots)
   - Aircraft: to-scale fuselage + wings, green nose indicator, red tail
   - Trail: fading blue dots of previous positions
   - Overlay: time, heading, groundspeed, phase, taxiway, position
5. Uses ffmpeg (if available) to combine frames into GIF/MP4

## Dependencies

- **SkiaSharp 3.119.4** (transitive via Avalonia 12) — frame rendering
- **Yaat.Sim** — `GeoJsonParser`, `FaaAircraftDatabase`, `GeoMath`, `NavigationDatabase`
- **ffmpeg** (optional) — GIF/MP4 encoding. If not available, individual frames are saved to `.tmp/frames/`

## Existing Diagnostic Tests

`OakAllExitsTests.Diagnostic_DumpTickCsv` generates tick CSVs for specific OAK exits.
Run it to generate CSV data, then animate:

```bash
dotnet test tests/Yaat.Sim.Tests --filter 'DisplayName~Diagnostic_DumpTickCsv'
# Generates .tmp/oak-30-W6-ticks.csv, oak-30-W4-ticks.csv, oak-28R-default-ticks.csv
```

## Key Files

- `tests/Yaat.Sim.Tests/Helpers/TickRecorder.cs` — Test helper for recording ticks
- `tools/Yaat.TickAnimator/Program.cs` — CLI entry point
- `tools/Yaat.TickAnimator/FrameRenderer.cs` — SkiaSharp frame rendering
