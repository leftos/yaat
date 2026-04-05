using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for AMX669 taxi turn overshoot at SFO.
///
/// Bug 1: AMX669 overshoots taxiway turns because it doesn't slow down enough
/// for sharp corners, ending up holding short of 1L at a weird angle.
///
/// Bug 2: When on M2 given "TAXI B M1 RWY 1L", the pathfinder added taxiway A
/// unnecessarily instead of staying on M2 to reach B.
///
/// Recording: S1-SFO-2 Ground Control 28_01
/// </summary>
[Collection("NavDbMutator")]
public class IssueAmxTaxiOvershootTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue-amx-taxi-overshoot-recording.yaat-bug-report-bundle.zip";

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
    /// AMX669 replays the recording, gets "TAXI B M1 RWY 1L" at t=146, and should
    /// hold short of 1L on M1 with a heading within 30° of the M1 bearing near 1L.
    /// No overshoot or weird angle.
    /// </summary>
    [Fact]
    public void AMX669_HoldsShortOf1L_WithReasonableHeading()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past all commands; AMX669 taxi command at t=146
        engine.Replay(recording, 200);

        var amx = engine.FindAircraft("AMX669");
        if (amx is null)
        {
            return;
        }

        // Tick until holding short or 300s
        for (int t = 1; t <= 300; t++)
        {
            engine.ReplayOneSecond();
            amx = engine.FindAircraft("AMX669");
            if (amx is null)
            {
                break;
            }

            if (amx.Phases?.CurrentPhase?.Name == "Holding Short 1L")
            {
                // M1 at SFO near 1L runs roughly SE (bearing ~120-160°).
                // The heading should be within 30° of a reasonable M1 bearing.
                double hdg = amx.TrueHeading.Degrees;
                output.WriteLine($"Hold-short heading: {hdg:F0}° at ({amx.Latitude:F6}, {amx.Longitude:F6})");

                // Verify heading is roughly along M1 (not perpendicular or reversed)
                // M1 near 1L has a SE bearing of approximately 120-160°
                bool headingReasonable = (hdg >= 90) && (hdg <= 190);
                Assert.True(headingReasonable, $"AMX669 heading {hdg:F0}° at 1L hold-short is outside expected M1 bearing range [90-190°]");

                // Verify aircraft is near the hold-short node (#882)
                var layout = new TestAirportGroundData().GetLayout("SFO");
                if (layout is not null && layout.Nodes.TryGetValue(882, out var hsNode))
                {
                    double dist = GeoMath.DistanceNm(amx.Latitude, amx.Longitude, hsNode.Latitude, hsNode.Longitude);
                    double distFt = dist * GeoMath.FeetPerNm;
                    output.WriteLine($"Distance to hold-short node #882: {distFt:F0}ft");
                    Assert.True(distFt < 150, $"Aircraft is {distFt:F0}ft from hold-short node, expected < 150ft");
                }

                return;
            }
        }

        Assert.Fail("AMX669 never reached hold-short of 1L within 300 seconds");
    }

    /// <summary>
    /// Resolving "TAXI B M1" from spot 14 (on M2) at SFO should route via M2 to B,
    /// not via taxiway A. The multi-candidate bridge scoring should prefer staying
    /// on M2 over BFS-hopping through A.
    /// </summary>
    [Fact]
    public void PathFromM2Spot_TaxiBM1_DoesNotUseTaxiwayA()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // Node 14 = Spot 2 on M2 (where AMX669 parks)
        int fromNodeId = 14;

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId,
            ["B", "M1"],
            out string? failReason,
            destinationRunway: "1L",
            airportId: "SFO",
            diagnosticLog: msg => output.WriteLine(msg)
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        output.WriteLine($"Route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            output.WriteLine($"  {seg.FromNodeId} → {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        // No segments should be on taxiway A
        bool hasA = route.Segments.Any(s => string.Equals(s.TaxiwayName, "A", StringComparison.OrdinalIgnoreCase));
        Assert.False(hasA, "Route should not include taxiway A — pathfinder should stay on M2 to reach B");
    }

    /// <summary>
    /// CornerSpeedForAngle should return correct values at key angles for jets.
    /// </summary>
    [Theory]
    [InlineData(0, 30)] // Straight — TaxiSpeed
    [InlineData(30, 30)] // Threshold — still TaxiSpeed
    [InlineData(60, 22.5)] // Midpoint of first segment
    [InlineData(90, 15)] // TaxiCornerSpeed
    [InlineData(120, 11.5)] // Midpoint of second segment
    [InlineData(150, 8)] // TaxiTightCornerSpeed
    [InlineData(180, 8)] // Beyond 150° — clamped at TaxiTightCornerSpeed
    public void CornerSpeedForAngle_JetValues(double angle, double expectedSpeed)
    {
        double speed = CategoryPerformance.CornerSpeedForAngle(AircraftCategory.Jet, angle);
        Assert.Equal(expectedSpeed, speed, precision: 1);
    }
}
