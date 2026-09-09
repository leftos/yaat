# Tick Animation — Visualizing Aircraft Movement in Tests

Animate tick-by-tick aircraft state over an airport ground layout to eyeball how aircraft move during a test —
landings, runway exits, taxi paths, conflicts. The standalone `Yaat.TickAnimator` GIF tool was removed; the
animated overlay now lives in LayoutInspector's interactive HTML render (invoke it through the `layout-inspect`
skill rather than composing flags by hand).

## 1. Record ticks in a test

`TickRecorder` (`tests/Yaat.Sim.Tests/Helpers/TickRecorder.cs`) writes a JSON document (`TickRecording`) with one
event per attached aircraft per recorded second, plus the aircraft metadata (type, wingspan, length, colour) the
renderer needs:

```csharp
using Yaat.Sim.Tests.Helpers;

var recorder = new TickRecorder(aircraft);          // or new TickRecorder(n152sp, n569sx) for several
for (int t = 1; t <= 300; t++)
{
    engine.TickOneSecond();
    recorder.Record(t);
}
recorder.WriteJson(Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "my-test-ticks.json"));
```

Or attach to an engine and let it write on dispose:

```csharp
using var _ = TickRecorder.Attach(engine, ".tmp/scenario.json", "N152SP", "N569SX");
```

Only record ticks matching a condition with `Filter = ac => ac.IsOnGround`.

## 2. Animate

```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/oak.geojson     --ticks .tmp/my-test-ticks.json --html .tmp/my-test.html 2>&1 | tee .tmp/li-html.log
```

Open the HTML: it renders the layout (runways, taxiways, hold-short nodes) with an animation player over the
recorded path — to-scale aircraft shape, trail, and a per-tick overlay of time, heading, groundspeed, phase and
taxiway. Pan/zoom state persists in `location.hash`. Add `--html-taxiway`, `--html-runway`, `--html-node` or
`--html-annotate` to highlight what the test is about.

## 3. Read the numbers instead

The same recording feeds the text analyses:

```bash
timeout 30 dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson     --ticks .tmp/my-test-ticks.json --tick-table --tick-ref SFO/28L --tick-hold-shorts K,D,Q 2>&1 | tee .tmp/li-ticks.log
```

`--tick-table` prints one row per tick (position, groundspeed, phase, the navigator's `NavTickDiag` speed caps,
signed cross-track and heading error against `--tick-ref`, distances to the named hold-shorts); `--tick-summary`
collapses it to one row per navigation target; `--tick-range LO-HI` and `--tick-callsign` filter. Flag reference:
[`tools/Yaat.LayoutInspector/CLAUDE.md`](../tools/Yaat.LayoutInspector/CLAUDE.md).
