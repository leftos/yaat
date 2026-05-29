using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>
/// Diagnostics for the fillet-V2 full-suite sweep failures (root causes ① and ② in
/// docs/plans/filletv2/v2-sim-validation.md), kept as the repro/evidence for the
/// findings folded into docs/plans/pathfinderv2/default-flip-triage.md.
///
/// <list type="bullet">
/// <item>Junction edge dumps (FLL B/C1, SFO A 43/1160) compare the V2 vs Legacy
///   tangent-node layout where the pathfinder walk picks a membership-matching
///   junction arc / short edge and produces an `X→Y→X` reversal (root cause ①).</item>
/// <item>The AMX669 V2 trajectory dump shows the GroundNavigator freezing at a tight
///   V2 arc near the route start — synthesis skipped, no forward motion (root cause ②).</item>
/// </list>
///
/// Not assertion gates — pure diagnostic output (run with -v detailed). Node ids are
/// V2-parse-specific; the junction dumps key off position, the SFO dump off ids 43/1160.
/// </summary>
public class FilletV2ReversalStubDiagnosticTests(ITestOutputHelper output)
{
    private const string TestDataDir = "TestData";

    // The B/B1/C/C1 junction at the west end of taxiway B (from the V2 route dump).
    private static readonly LatLon JunctionCenter = new(26.075715, -80.166200);
    private const double RadiusFt = 120.0;

    [Fact]
    public void Fll_BC1Junction_V2_vs_Legacy_EdgeDump()
    {
        string path = Path.Combine(TestDataDir, "fll.geojson");
        if (!File.Exists(path))
        {
            output.WriteLine("fll.geojson not found — skipping");
            return;
        }

        string geo = File.ReadAllText(path);
        var legacy = GeoJsonParser.Parse("FLL", geo, null, FilletMode.Legacy);
        var v2 = GeoJsonParser.Parse("FLL", geo, null, FilletMode.V2);

        output.WriteLine("=== LEGACY ===");
        DumpJunction(legacy);
        output.WriteLine("");
        output.WriteLine("=== V2 ===");
        DumpJunction(v2);
    }

    [Fact]
    public void Sfo_1160_43_Junction_V2_vs_Legacy_EdgeDump()
    {
        string path = Path.Combine(TestDataDir, "sfo.geojson");
        if (!File.Exists(path))
        {
            output.WriteLine("sfo.geojson not found — skipping");
            return;
        }

        string geo = File.ReadAllText(path);
        var legacy = GeoJsonParser.Parse("SFO", geo, null, FilletMode.Legacy);
        var v2 = GeoJsonParser.Parse("SFO", geo, null, FilletMode.V2);

        // Nodes 43 / 1160 are the V2 reversal pair (Issue166 route to F14).
        if (!v2.Nodes.TryGetValue(43, out var n43) || !v2.Nodes.TryGetValue(1160, out var n1160))
        {
            output.WriteLine("V2 nodes 43/1160 not present — layout changed");
            return;
        }

        output.WriteLine($"V2 #43   at ({n43.Position.Lat:F6},{n43.Position.Lon:F6})");
        output.WriteLine($"V2 #1160 at ({n1160.Position.Lat:F6},{n1160.Position.Lon:F6})");
        var center = new LatLon((n43.Position.Lat + n1160.Position.Lat) / 2, (n43.Position.Lon + n1160.Position.Lon) / 2);

        output.WriteLine("");
        output.WriteLine("=== LEGACY (near junction) ===");
        DumpNear(legacy, center);
        output.WriteLine("");
        output.WriteLine("=== V2 (near junction) ===");
        DumpNear(v2, center);
    }

    [Fact]
    public void Amx669_V2_TaxiTrajectory_Diagnostic()
    {
        const string recordingPath = "TestData/issue-amx-taxi-overshoot-recording.yaat-bug-report-bundle.zip";
        var recording = RecordingLoader.Load(recordingPath);
        TestVnasData.EnsureInitialized();
        if (recording is null || TestVnasData.NavigationDb is null)
        {
            output.WriteLine("recording or navdata unavailable — skipping");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData(FilletMode.V2));
        engine.Replay(recording, 200);

        double prevLat = 0;
        double prevLon = 0;
        for (int t = 1; t <= 300; t++)
        {
            engine.ReplayOneSecond();
            var amx = engine.FindAircraft("AMX669");
            if (amx is null)
            {
                continue;
            }
            var route = amx.Ground?.AssignedTaxiRoute;
            int segIdx = route?.CurrentSegmentIndex ?? -1;
            int segTotal = route?.Segments.Count ?? 0;
            int target = (route is not null && segIdx >= 0 && segIdx < segTotal) ? route.Segments[segIdx].ToNodeId : -1;
            string phase = amx.Phases?.CurrentPhase?.Name ?? "(none)";
            double movedFt = prevLat == 0 ? 0 : GeoMath.DistanceNm(new LatLon(prevLat, prevLon), amx.Position) * GeoMath.FeetPerNm;
            prevLat = amx.Position.Lat;
            prevLon = amx.Position.Lon;
            if (phase == "Holding Short 1L")
            {
                output.WriteLine($"t={t}: REACHED Holding Short 1L at seg {segIdx}/{segTotal}");
                break;
            }
            // Log every 5s plus any near-stall second.
            if (t % 5 == 0 || amx.IndicatedAirspeed < 4.0)
            {
                output.WriteLine(
                    $"t={t, 3} ias={amx.IndicatedAirspeed, 5:F1} moved={movedFt, 5:F1}ft phase={phase, -20} seg={segIdx}/{segTotal}->#{target} hdg={amx.TrueHeading.Degrees:F0} pos=({amx.Position.Lat:F6},{amx.Position.Lon:F6})"
                );
            }
        }
    }

    private void DumpJunction(AirportGroundLayout layout) => DumpNear(layout, JunctionCenter);

    private void DumpNear(AirportGroundLayout layout, LatLon center)
    {
        var near = layout
            .Nodes.Values.Where(n => GeoMath.DistanceNm(center, n.Position) * GeoMath.FeetPerNm <= RadiusFt)
            .OrderBy(n => n.Position.Lon)
            .ToList();

        output.WriteLine($"{near.Count} nodes within {RadiusFt:F0}ft of {center.Lat:F6},{center.Lon:F6}");
        foreach (var n in near)
        {
            output.WriteLine($"  #{n.Id} {n.Type} ({n.Position.Lat:F6},{n.Position.Lon:F6}) origin={n.Origin}");
            foreach (var e in n.Edges)
            {
                var other = e.OtherNode(n);
                double brg = GeoMath.BearingTo(n.Position, other.Position);
                double distFt = GeoMath.DistanceNm(n.Position, other.Position) * GeoMath.FeetPerNm;
                string kind = e is GroundArc ? "arc" : "edge";
                output.WriteLine(
                    $"      {kind} twy='{e.TaxiwayName}' → #{other.Id} ({other.Position.Lat:F6},{other.Position.Lon:F6}) brg={brg:F0}° {distFt:F0}ft rwyCL={e.IsRunwayCenterline}"
                );
            }
        }
    }
}
