# Rebuilding the YAAT-1282 implicit-crossing mock

The page's media (eight stills and the LayoutInspector animation, about 1.4 MB) stay out of git. These steps rebuild them under `.tmp/mocks/yaat-1282-implicit-crossing-src/` and make the self-contained review copy. Run from the repo root (main checkout or worktree) in Git Bash. The recordings come from today's sim, so a rebuild after YAAT-1282 is fixed shows the fixed behaviour in the TODAY lane.

Sources: `tests/Yaat.Sim.Tests/TestData/oak.geojson` (layout), the staging of `BareTaxiB_HoldingShortOf28LFacingIt_DoesNotCross` in `tests/Yaat.Sim.Tests/Simulation/GroundTaxi/BareTaxiFirstStepHeadingTests.cs`, and the two scratch files below.

1. Save the recorder below as `tests/Yaat.Sim.Tests/Simulation/GroundTaxi/Yaat1282MockScratch.cs` (scratch, never committed) and run it on this machine, so `TickRecorder` writes into the local `.tmp/` (a routed run leaves the files on the builder):

   `pwsh -NoProfile -File tools/gate.ps1 run --log .tmp/mock.log --timeout 900 --slot heavy --local --reason "TickRecorder output must land in .tmp" -- dotnet test tests/Yaat.Sim.Tests -c Release -- --filter-class "*.Yaat1282MockScratch"`

   It writes `.tmp/yaat1282-{today,explicit,holdthentaxi}.json` and a per-second text log beside each (distance past the bar along the starting heading, ground speed, phase). Delete the scratch file afterwards.
2. Render the two-run animation:

   `pwsh -NoProfile -File tools/gate.ps1 run --log .tmp/li-html.log --timeout 300 --slot heavy --local --reason "HTML render must land in .tmp" -- dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/oak.geojson --airport-code OAK --ticks TODAY=.tmp/yaat1282-today.json --ticks EXPLICIT=.tmp/yaat1282-explicit.json --html-runway 28L --html-taxiway B --html-annotate 501 "hold-short bar 28L (B)" --html .tmp/yaat1282-animation.html`
3. Assemble the source folder: `mkdir -p .tmp/mocks/yaat-1282-implicit-crossing-src && cp docs/plans/canvases/yaat-1282-implicit-crossing/index.html .tmp/yaat1282-animation.html .tmp/mocks/yaat-1282-implicit-crossing-src/ && mv .tmp/mocks/yaat-1282-implicit-crossing-src/yaat1282-animation.html .tmp/mocks/yaat-1282-implicit-crossing-src/animation.html`
4. Save the shooter below as `.tmp/shoot.cjs` and take the stills headlessly (Playwright from `~/.claude/tools/mock-three/node_modules`):

   `node .tmp/shoot.cjs .tmp/yaat1282-animation.html .tmp/mocks/yaat-1282-implicit-crossing-src 10 37.72268 -122.2059 0,20,40,60 TODAY=.tmp/yaat1282-today.json EXPLICIT=.tmp/yaat1282-explicit.json`
5. Make the review copy: `uv run --no-project python ~/.claude/tools/mock-embed/embed_media.py .tmp/mocks/yaat-1282-implicit-crossing-src/index.html .tmp/mocks/yaat-1282-implicit-crossing`, then open `.tmp/mocks/yaat-1282-implicit-crossing/index.html`.

The captions' distances are measured along the taxi path from the bar (summed per-second position steps in the JSON). Node ids 501 (start bar), 184 (far-side 28L bar) and 506 (28R bar) are those of `TestAirportGroundData`'s OAK build; check them with `--node 501,184,506` if the layout is refreshed.

## Recorder

```csharp
using System.Text;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>Scratch recorder for the YAAT-1282 mock; never committed.</summary>
public class Yaat1282MockScratch(ITestOutputHelper output)
{
    private const int RunSeconds = 90;
    private const int SecondCommandAt = 30;

    [Fact]
    public void Record_Today() => Record("today", ["TAXI B"], null);

    [Fact]
    public void Record_Explicit() => Record("explicit", ["TAXI B HS 28L"], "CROSS 28L");

    [Fact]
    public void Record_HoldThenTaxi() => Record("holdthentaxi", [], "TAXI B");

    private void Record(string name, string[] firstCommands, string? secondCommand)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(groundData.GetLayout("OAK"));

        (GroundNode bar, GroundNode farBar, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        var holding = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = bar.Id,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = bar.RunwayId?.ToString(),
            }
        );
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "yaat-1282-mock",
                ScenarioName = "YAAT-1282 mock",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        AircraftState aircraft = SpawnAt(layout, bar.Position, heading, holding);
        engine.World.AddAircraft(aircraft);

        var log = new StringBuilder();
        log.AppendLine($"bar {bar.Id} ({bar.RunwayId}) farBar {farBar.Id} toward {towardRunwayDeg:F1}");
        foreach (string cmd in firstCommands)
        {
            Issue(engine, aircraft, cmd, log);
        }

        var recorder = new TickRecorder(aircraft);
        recorder.Record(0);
        LogRow(log, 0, aircraft, bar, heading);
        for (int t = 1; t <= RunSeconds; t++)
        {
            if ((secondCommand is not null) && (t == SecondCommandAt + 1))
            {
                Issue(engine, aircraft, secondCommand, log);
            }

            engine.TickOneSecond();
            recorder.Record(t);
            LogRow(log, t, aircraft, bar, heading);
        }

        string root = TickRecorder.FindRepoRoot();
        recorder.WriteJson(Path.Combine(root, ".tmp", $"yaat1282-{name}.json"));
        File.WriteAllText(Path.Combine(root, ".tmp", $"yaat1282-{name}.txt"), log.ToString().Replace("\r\n", "\n"));
        output.WriteLine(log.ToString());
    }

    private static void Issue(SimulationEngine engine, AircraftState aircraft, string cmd, StringBuilder log)
    {
        CommandResult r = engine.SendCommand(aircraft.Callsign, cmd);
        log.AppendLine($"CMD '{cmd}' success={r.Success} msg={r.Message}");
        if (aircraft.Ground.AssignedTaxiRoute is TaxiRoute route)
        {
            log.AppendLine($"  holds: {SfoGroundHarness.FormatActual(route.HoldShortPoints)}");
        }
    }

    private static void LogRow(StringBuilder log, int t, AircraftState aircraft, GroundNode bar, TrueHeading heading)
    {
        double pastBarFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, bar.Position, heading) * GeoMath.FeetPerNm;
        log.AppendLine($"t={t} past={pastBarFt:F1} gs={aircraft.GroundSpeed:F1} phase={aircraft.Phases?.CurrentPhase?.GetType().Name}");
    }

    private static (GroundNode Bar, GroundNode FarBar, double TowardRunwayDeg) Bars28LOnB(AirportGroundLayout layout)
    {
        List<GroundNode> bars =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } rwy) && rwy.Contains("28L"))
                .Where(n => n.Edges.Any(e => e.MatchesTaxiway("B"))),
        ];
        Assert.True(bars.Count == 2);
        GroundNode south = bars.MinBy(n => n.Position.Lat)!;
        GroundNode north = bars.MaxBy(n => n.Position.Lat)!;
        double towardNorthBar = GeoMath.BearingTo(south.Position, north.Position);
        double towardRunwayDeg = Assert.Single(
            south
                .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("B"))
                .Select(e => GeoMath.BearingTo(south.Position, e.OtherNode(south).Position)),
            b => GeoMath.AbsBearingDifference(b, towardNorthBar) < 90.0
        );
        return (south, north, towardRunwayDeg);
    }

    private static AircraftState SpawnAt(AirportGroundLayout layout, LatLon position, TrueHeading heading, Phase startPhase)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N1259",
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "B";
        return aircraft;
    }
}
```

## Shooter

```javascript
// Headless stills of the YAAT-1282 LayoutInspector animation, one set per lane.
// usage: node shoot.cjs <animation.html> <outDir> <scale> <centerLat> <centerLon> <times csv> <LABEL=recording.json>...
const fs = require("fs");
const path = require("path");
const { chromium } = require(path.join(process.env.USERPROFILE, ".claude/tools/mock-three/node_modules/playwright"));

const LABELS = [
  { node: 501, text: "28L hold bar on B (start)", dx: -20, dy: 26, align: "right" },
  { node: 184, text: "far-side 28L bar", dx: 16, dy: 18, align: "left" },
  { node: 506, text: "28R hold bar", dx: 16, dy: 18, align: "left" },
];

(async () => {
  const [html, outDir, scaleArg, latArg, lonArg, timesArg, ...runs] = process.argv.slice(2);
  const times = timesArg.split(",").map(Number);
  const lanes = runs.map((r) => {
    const [label, file] = r.split("=");
    const ticks = JSON.parse(fs.readFileSync(file, "utf8")).ticks;
    return { label, pos: Object.fromEntries(ticks.map((k) => [k.t, [k.lat, k.lon]])) };
  });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: 1 });
    await page.goto("file:///" + path.resolve(html).replace(/\\/g, "/"));
    await page.waitForTimeout(500);
    await page.addStyleTag({ content: "#hlpanel, #legend { display: none !important; }" });
    await page.evaluate(
      ([s, lat, lon, labels]) => {
        const x = ((lon - minLon) / (maxLon - minLon)) * W;
        const y = (1 - (lat - minLat) / (maxLat - minLat)) * H;
        scale = s;
        panX = W / 2 - x;
        panY = H / 2 - y;
        window.__focus = null;
        const base = draw;
        draw = function () {
          base();
          ctx.save();
          ctx.font = "600 15px system-ui, sans-serif";
          ctx.textBaseline = "middle";
          for (const l of labels) {
            const n = nodeMap[l.node];
            const [sx, sy] = toScreen(n.lat, n.lon);
            ctx.strokeStyle = "#ffd54f";
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.arc(sx, sy, 7, 0, Math.PI * 2);
            ctx.stroke();
            ctx.textAlign = l.align;
            ctx.fillStyle = "rgba(10,12,16,0.8)";
            const w = ctx.measureText(l.text).width;
            const bx = l.align === "right" ? sx + l.dx - w - 6 : sx + l.dx - 6;
            ctx.fillRect(bx, sy + l.dy - 12, w + 12, 24);
            ctx.fillStyle = "#ffd54f";
            ctx.fillText(l.text, sx + l.dx, sy + l.dy);
          }
          const a = nodeMap[501],
            b = nodeMap[184];
          const [ax, ay] = toScreen(a.lat, a.lon);
          const [bx2, by2] = toScreen(b.lat, b.lon);
          ctx.textAlign = "center";
          ctx.fillStyle = "#90caf9";
          ctx.font = "700 18px system-ui, sans-serif";
          ctx.fillText("RWY 28L/10R", (ax + bx2) / 2 - 230, (ay + by2) / 2 - 55);
          if (window.__focus) {
            const [fx, fy] = toScreen(window.__focus.lat, window.__focus.lon);
            ctx.strokeStyle = "#ffffff";
            ctx.lineWidth = 3;
            ctx.beginPath();
            ctx.arc(fx, fy, 26, 0, Math.PI * 2);
            ctx.stroke();
          }
          ctx.restore();
        };
        draw();
      },
      [parseFloat(scaleArg), parseFloat(latArg), parseFloat(lonArg), LABELS]
    );
    for (const lane of lanes) {
      for (const t of times) {
        const [flat, flon] = lane.pos[t];
        await page.evaluate(
          ([tt, la, lo]) => {
            window.__focus = { lat: la, lon: lo };
            const s = document.getElementById("scrub");
            s.value = String(tt);
            s.dispatchEvent(new Event("input"));
          },
          [t, flat, flon]
        );
        await page.waitForTimeout(150);
        const clock = await page.evaluate(() => document.getElementById("tickClock").textContent);
        const file = path.join(outDir, `${lane.label.toLowerCase()}-t${String(t).padStart(2, "0")}.png`);
        await page.screenshot({ path: file });
        console.log(lane.label, t, clock, file);
      }
    }
  } finally {
    await browser.close();
  }
})();
```
