using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A taxiway hold-short target inside a TAXI command must steer the route, not just
/// annotate it. The controller cleared N45912 <c>TAXI D C HS E RWY 28R</c> at OAK; the
/// pathfinder ignored <c>E</c> as a routing waypoint and detoured through the un-named
/// taxiways A and B, flagging each "not in the route issued". Naming E in the path
/// (<c>TAXI D C E HS E RWY 28R</c>) already routes cleanly — the fix makes the embedded
/// <c>HS &lt;taxiway&gt;</c> form behave identically.
///
/// Recording: S1-OAK-P (A) | S1 Rating Practical Exam. N45912 (C172) parks on the OAK
/// north field and is cleared to taxi at sim-time t=1364.
/// </summary>
public class OakHsDirectionalHintTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-hs-directional-hint-recording.zip";
    private const string Callsign = "N45912";
    private const int JustBeforeTaxi = 1363;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("an HS taxiway steering the route");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    private static bool HasUnauthorizedWarning(TaxiRoute route) =>
        route.Warnings.Any(w => w.Contains("not in the route issued", StringComparison.OrdinalIgnoreCase));

    private static List<string> StraightSegmentTaxiways(TaxiRoute route) =>
        [.. route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase)];

    private void LogRoute(string label, CommandResult result, TaxiRoute? route)
    {
        output.WriteLine($"[{label}] success={result.Success} message={result.Message}");
        if (route is null)
        {
            output.WriteLine($"[{label}] route=<null>");
            return;
        }

        output.WriteLine($"[{label}] taxiways=[{string.Join(", ", StraightSegmentTaxiways(route))}]");
        output.WriteLine($"[{label}] warnings=[{string.Join(" | ", route.Warnings)}]");
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            output.WriteLine($"[{label}]   HS node={hs.NodeId} target={hs.TargetName} reason={hs.Reason}");
        }
    }

    /// <summary>
    /// <c>TAXI D C HS E RWY 28R</c> as the owner reads it: runway 28R, taxi via D C, hold short of E for traffic
    /// exiting 28R on E. The route never drives E, E carries an explicit hold short, and nothing warns that E is
    /// driven outside the route issued. D and C reach 28R only over E, so the route ends at the hold short of E.
    /// </summary>
    [Fact]
    public void EmbeddedHsE_RunwayAhead_HoldsShortOfE_RouteAvoidsE() =>
        AssertHoldsShortOfEWithoutDrivingE(
            "TAXI D C HS E RWY 28R",
            "Taxi via D C HS E",
            "runway 28R, taxi via D C, hold short of taxiway E",
            reachesRunway: false
        );

    /// <summary>
    /// The same clearance with the runway first and the way past E named: <c>RWY 28R TAXI D C B HS E</c> holds short of E
    /// on C and goes on along C and B to 28R.
    /// </summary>
    [Fact]
    public void RunwayFirst_TaxiDCBHsE_HoldsShortOfE_RouteAvoidsE() =>
        AssertHoldsShortOfEWithoutDrivingE(
            "RWY 28R TAXI D C B HS E",
            "Taxi via D C B HS E RWY 28R",
            "runway 28R, taxi via D C, hold short of taxiway E",
            reachesRunway: true
        );

    private void AssertHoldsShortOfEWithoutDrivingE(string clearance, string echo, string readback, bool reachesRunway)
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, JustBeforeTaxi);
        Assert.NotNull(engine.FindAircraft(Callsign));

        CommandResult result = engine.SendCommand(Callsign, clearance);
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        LogRoute(clearance, result, route);
        Assert.True(result.Success, $"{clearance} refused: {result.Message}");
        Assert.NotNull(route);
        output.WriteLine($"[{clearance}] readback={result.PilotReadback?.Speech.Terminal}");
        output.WriteLine($"[{clearance}] segments=[{string.Join(", ", route.Segments.Select(s => $"{s.FromNodeId}>{s.ToNodeId}:{s.TaxiwayName}"))}]");

        Assert.DoesNotContain(route.Segments, s => string.Equals(s.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(RouteMaterialiser.NotInRouteIssuedWarning("E"), route.Warnings);
        HoldShortPoint holdE = Assert.Single(
            route.HoldShortPoints,
            hs => (hs.Reason == HoldShortReason.ExplicitHoldShort) && string.Equals(hs.TargetName, "E", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(echo, result.Message);
        Assert.Equal(readback, result.PilotReadback?.Speech.Terminal);

        bool atRunway = route.HoldShortPoints.Exists(hs => hs.Reason == HoldShortReason.DestinationRunway);
        Assert.Equal(reachesRunway, atRunway);
        if (!reachesRunway)
        {
            Assert.Equal(holdE.NodeId, route.Segments[^1].ToNodeId);
        }
    }

    /// <summary>
    /// Pins the target: naming E in the path (<c>TAXI D C E HS E RWY 28R</c>) is already clean.
    /// Passes on current code and after the fix.
    /// </summary>
    [Fact]
    public void PathFormWithE_IsAlreadyClean()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, JustBeforeTaxi);
        Assert.NotNull(engine.FindAircraft(Callsign));

        CommandResult result = engine.SendCommand(Callsign, "TAXI D C E HS E RWY 28R");
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        LogRoute("path E", result, route);
        Assert.NotNull(route);

        List<string> taxiways = StraightSegmentTaxiways(route);
        Assert.False(HasUnauthorizedWarning(route), $"Unexpected unauthorized-path warning: [{string.Join(" | ", route.Warnings)}]");
        Assert.DoesNotContain("A", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("B", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("E", taxiways, StringComparer.OrdinalIgnoreCase);
    }
}
