using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for EL T (Exit Left at taxiway T) on SFO runway 28R.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — SKW3398 (E75L) lands on 28R
/// with EL T (exit left onto high-speed taxiway T). The aircraft ignores T
/// and exits at D instead, going through grass. Taxiway T is a high-speed
/// exit with multiple intermediate nodes between the runway centerline and
/// the hold-short point — FindAdjacentHoldShort's single-hop search never
/// finds it.
/// </summary>
public class ElTHighSpeedExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/el-t-high-speed-exit-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: tick-by-tick logging of the exit maneuver, showing aircraft
    /// position relative to taxiway T edges. Logs lat/lon, heading, speed,
    /// phase, and minimum distance to any T edge segment each tick.
    /// </summary>
    [Fact]
    public void Diagnostic_LogExitPathDeviation()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just before EL T, send the command
        engine.Replay(recording, 274);

        var ac = engine.FindAircraft("SKW3398");
        Assert.NotNull(ac);

        var result = engine.SendCommand("SKW3398", "EL T");
        Assert.True(result.Success);

        // Collect all T edge segments from the ground layout
        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        Assert.NotNull(layout);

        var tEdgeSegments = new List<(double Lat1, double Lon1, double Lat2, double Lon2)>();
        foreach (var edge in layout.Edges)
        {
            if (!edge.MatchesTaxiway("T"))
            {
                continue;
            }

            var from = edge.Nodes[0];
            var to = edge.Nodes[1];
            if (from is null || to is null)
            {
                continue;
            }

            tEdgeSegments.Add((from.Position.Lat, from.Position.Lon, to.Position.Lat, to.Position.Lon));
        }

        output.WriteLine($"Taxiway T: {tEdgeSegments.Count} edge segments");
        output.WriteLine("");
        output.WriteLine("t    | lat         | lon          | hdg   | gs    | phase              | dist_to_T_ft | nearest_seg");
        output.WriteLine("-----|-------------|--------------|-------|-------|--------------------|--------------|------------");

        bool inExitPhase = false;
        int ticksSinceExit = 0;

        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("SKW3398");
            if (ac is null)
            {
                break;
            }

            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "none";
            bool isExiting = phaseName.Contains("Exit") || phaseName.Contains("Holding");

            if (isExiting && !inExitPhase)
            {
                inExitPhase = true;
                output.WriteLine($"--- EXIT PHASE STARTED at t+{t} ---");
            }

            if (inExitPhase)
            {
                ticksSinceExit++;
            }

            // Log every tick during exit, every 5 ticks during approach/landing
            if (!inExitPhase && t % 5 != 0)
            {
                continue;
            }

            // Compute minimum distance to any T edge segment
            double minDistNm = double.MaxValue;
            int nearestSeg = -1;
            for (int s = 0; s < tEdgeSegments.Count; s++)
            {
                var seg = tEdgeSegments[s];
                double dist = PointToSegmentDistNm(ac.Position.Lat, ac.Position.Lon, seg.Lat1, seg.Lon1, seg.Lat2, seg.Lon2);
                if (dist < minDistNm)
                {
                    minDistNm = dist;
                    nearestSeg = s;
                }
            }

            double minDistFt = minDistNm * GeoMath.FeetPerNm;

            output.WriteLine(
                $"t+{t, -3} | {ac.Position.Lat, 11:F6} | {ac.Position.Lon, 12:F6} | {ac.TrueHeading.Degrees, 5:F1} | {ac.GroundSpeed, 5:F1} | {phaseName, -18} | {minDistFt, 12:F1} | seg {nearestSeg}"
            );

            if (ac.Ground.CurrentTaxiway is not null)
            {
                output.WriteLine($"--- EXITED at taxiway {ac.Ground.CurrentTaxiway}, hdg={ac.TrueHeading.Degrees:F0} ---");
                break;
            }

            if (ticksSinceExit > 120)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Minimum distance from a point to a line segment (flat-earth approximation).
    /// </summary>
    private static double PointToSegmentDistNm(double pLat, double pLon, double aLat, double aLon, double bLat, double bLon)
    {
        double cosLat = Math.Cos(pLat * Math.PI / 180.0);
        double dx = (bLon - aLon) * cosLat;
        double dy = bLat - aLat;
        double px = (pLon - aLon) * cosLat;
        double py = pLat - aLat;

        double segLenSq = (dx * dx) + (dy * dy);
        if (segLenSq < 1e-20)
        {
            return GeoMath.DistanceNm(pLat, pLon, aLat, aLon);
        }

        double t = Math.Clamp(((px * dx) + (py * dy)) / segLenSq, 0.0, 1.0);
        double closestLat = aLat + (t * (bLat - aLat));
        double closestLon = aLon + (t * (bLon - aLon));

        return GeoMath.DistanceNm(pLat, pLon, closestLat, closestLon);
    }
}
