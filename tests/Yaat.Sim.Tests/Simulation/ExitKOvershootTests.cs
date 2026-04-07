using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for EXIT K overshoot on SFO runway 28L.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — DAL2581 (A319/L) lands on 28L
/// with EXIT K at t=783. Old code: aircraft overshot K, rolled past it on the
/// centerline, and did a ~135° turn to reach the hold-short. New code should
/// have LandingPhase resolve K ahead, commit a ResolvedExitInfo, and hand off
/// to RunwayExitPhase which follows the exit path smoothly.
/// </summary>
[Collection("NavDbMutator")]
public class ExitKOvershootTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/09304e0c727e.zip";

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
    /// DAL2581 is given EXIT K at t=783 while on final approach to 28L.
    /// After exit, the aircraft should be on taxiway K with no heading reversal
    /// (heading change from runway heading should be ≤90°).
    /// </summary>
    [Fact]
    public void DAL2581_ExitsAtK_NoHeadingReversal()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=782 — just before the EXIT K command at t=783
        engine.Replay(recording, 782);

        var ac = engine.FindAircraft("DAL2581");
        Assert.NotNull(ac);

        // Send EXIT K manually to ensure the current code's dispatch handles it
        var result = engine.SendCommand("DAL2581", "EXIT K");
        output.WriteLine($"EXIT K result: success={result.Success}, message={result.Message}");
        Assert.True(result.Success, $"EXIT K command failed: {result.Message}");

        double runwayHeading = ac.TrueHeading.Degrees;
        output.WriteLine($"t=782: hdg={runwayHeading:F1} gs={ac.GroundSpeed:F0} phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        // Tick forward until exit completes or 600 seconds elapse
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("DAL2581");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            if (t % 30 == 0)
            {
                output.WriteLine(
                    $"t+{t}: gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0}"
                        + $" taxiway={ac.CurrentTaxiway ?? "(none)"} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
                );
            }

            // CurrentTaxiway is set when RunwayExitPhase completes
            if (ac.CurrentTaxiway is not null)
            {
                double finalHeading = ac.TrueHeading.Degrees;
                double headingChange = new TrueHeading(finalHeading).AbsAngleTo(new TrueHeading(runwayHeading));

                output.WriteLine(
                    $"t+{t}: exited at taxiway {ac.CurrentTaxiway} hdg={finalHeading:F0}"
                        + $" (change={headingChange:F0}° from rwy hdg {runwayHeading:F0})"
                );

                // Aircraft should exit at K
                Assert.Equal("K", ac.CurrentTaxiway, StringComparer.OrdinalIgnoreCase);

                // No near-180 reversal — heading change should be ≤100°.
                // K at SFO is a ~90° perpendicular taxiway, so 90° is expected.
                // The old broken behavior was ~135° (overshoot + reversal).
                Assert.True(headingChange <= 100, $"Heading change {headingChange:F0}° exceeds 100° — aircraft likely overshot and reversed");
                return;
            }
        }

        Assert.Fail("DAL2581 never exited the runway within 600 seconds");
    }

    /// <summary>
    /// Diagnostic: tick-by-tick logging of rollout and exit with distance to K
    /// edge segments. Shows the aircraft path relative to taxiway K geometry.
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

        engine.Replay(recording, 782);

        var ac = engine.FindAircraft("DAL2581");
        Assert.NotNull(ac);

        var result = engine.SendCommand("DAL2581", "EXIT K");
        Assert.True(result.Success);

        // Collect K edge segments from the ground layout
        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        Assert.NotNull(layout);

        var kEdgeSegments = new List<(double Lat1, double Lon1, double Lat2, double Lon2)>();
        foreach (var edge in layout.Edges)
        {
            if (!string.Equals(edge.TaxiwayName, "K", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var from = edge.Nodes[0];
            var to = edge.Nodes[1];
            if (from is null || to is null)
            {
                continue;
            }

            kEdgeSegments.Add((from.Latitude, from.Longitude, to.Latitude, to.Longitude));
        }

        output.WriteLine($"Taxiway K: {kEdgeSegments.Count} edge segments");
        output.WriteLine("");
        output.WriteLine("t    | lat         | lon          | hdg   | gs    | phase              | dist_to_K_ft | taxiway");
        output.WriteLine("-----|-------------|--------------|-------|-------|--------------------|--------------|--------");

        double runwayHeading = ac.TrueHeading.Degrees;
        bool inExitPhase = false;
        int ticksSinceExit = 0;

        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("DAL2581");
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

            // Compute minimum distance to any K edge segment
            double minDistNm = double.MaxValue;
            for (int s = 0; s < kEdgeSegments.Count; s++)
            {
                var seg = kEdgeSegments[s];
                double dist = PointToSegmentDistNm(ac.Latitude, ac.Longitude, seg.Lat1, seg.Lon1, seg.Lat2, seg.Lon2);
                if (dist < minDistNm)
                {
                    minDistNm = dist;
                }
            }

            double minDistFt = minDistNm * GeoMath.FeetPerNm;

            output.WriteLine(
                $"t+{t, -3} | {ac.Latitude, 11:F6} | {ac.Longitude, 12:F6} | {ac.TrueHeading.Degrees, 5:F1} | {ac.GroundSpeed, 5:F1} | {phaseName, -18} | {minDistFt, 12:F1} | {ac.CurrentTaxiway ?? "(none)"}"
            );

            if (ac.CurrentTaxiway is not null)
            {
                double finalHdg = ac.TrueHeading.Degrees;
                double hdgChange = new TrueHeading(finalHdg).AbsAngleTo(new TrueHeading(runwayHeading));
                output.WriteLine(
                    $"--- EXITED at taxiway {ac.CurrentTaxiway}, hdg={finalHdg:F0} (change={hdgChange:F0}° from rwy {runwayHeading:F0}) ---"
                );
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
