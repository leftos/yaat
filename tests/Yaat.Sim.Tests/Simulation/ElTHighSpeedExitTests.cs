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
    /// SKW3398 is given EL T before landing on 28R. After exit, the aircraft
    /// should be on taxiway T, not D.
    ///
    /// Note: The recording was made with the old code where the dispatcher
    /// dropped the Taxiway argument, so the recorded canonical command is "EL"
    /// (not "EL T"). We replay to just before the recorded EL command, then
    /// send "EL T" manually with the fixed code.
    /// </summary>
    [Fact]
    public void SKW3398_ExitsAtTaxiwayT_NotD()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=274 — just before the recorded EL command (t=275)
        engine.Replay(recording, 274);

        var ac = engine.FindAircraft("SKW3398");
        Assert.NotNull(ac);

        // Send EL T manually (the recording only has "EL" due to old describer bug)
        var result = engine.SendCommand("SKW3398", "EL T");
        output.WriteLine($"EL T result: success={result.Success}, message={result.Message}");
        Assert.True(result.Success, $"EL T command failed: {result.Message}");

        output.WriteLine(
            $"t=274: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
        );

        // Tick forward until the aircraft exits the runway or 600 seconds elapse
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("SKW3398");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            if (t % 30 == 0)
            {
                output.WriteLine(
                    $"t+{t}: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0}"
                        + $" taxiway={ac.CurrentTaxiway ?? "(none)"} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
                );
            }

            // CurrentTaxiway is set when RunwayExitPhase completes
            if (ac.CurrentTaxiway is not null)
            {
                output.WriteLine($"t+{t}: exited runway at taxiway {ac.CurrentTaxiway} hdg={ac.TrueHeading.Degrees:F0}");

                Assert.Equal("T", ac.CurrentTaxiway.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
                return;
            }
        }

        Assert.Fail("SKW3398 never exited the runway within 600 seconds");
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
            if (!string.Equals(edge.TaxiwayName, "T", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var from = edge.Nodes[0];
            var to = edge.Nodes[1];
            if (from is null || to is null)
            {
                continue;
            }

            tEdgeSegments.Add((from.Latitude, from.Longitude, to.Latitude, to.Longitude));
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
                double dist = PointToSegmentDistNm(ac.Latitude, ac.Longitude, seg.Lat1, seg.Lon1, seg.Lat2, seg.Lon2);
                if (dist < minDistNm)
                {
                    minDistNm = dist;
                    nearestSeg = s;
                }
            }

            double minDistFt = minDistNm * GeoMath.FeetPerNm;

            output.WriteLine(
                $"t+{t, -3} | {ac.Latitude, 11:F6} | {ac.Longitude, 12:F6} | {ac.TrueHeading.Degrees, 5:F1} | {ac.GroundSpeed, 5:F1} | {phaseName, -18} | {minDistFt, 12:F1} | seg {nearestSeg}"
            );

            if (ac.CurrentTaxiway is not null)
            {
                output.WriteLine($"--- EXITED at taxiway {ac.CurrentTaxiway}, hdg={ac.TrueHeading.Degrees:F0} ---");
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
