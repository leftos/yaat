using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A taxiway hold-short target inside a TAXI command must steer the route, not just
/// annotate it. The controller cleared N45912 <c>TAXI D C HS E RWY 28R</c> at OAK; the
/// pathfinder ignored <c>E</c> as a routing waypoint and detoured through the un-named
/// taxiways A and B, flagging each "not in authorized path". Naming E in the path
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

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static bool HasUnauthorizedWarning(TaxiRoute route) =>
        route.Warnings.Any(w => w.Contains("not in authorized path", StringComparison.OrdinalIgnoreCase));

    private static List<string> StraightSegmentTaxiways(TaxiRoute route) =>
        route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"[{label}]   HS node={hs.NodeId} target={hs.TargetName} reason={hs.Reason}");
        }
    }

    /// <summary>
    /// The bug: <c>HS E</c> as the only mention of E does not route through E. This asserts the
    /// FIXED behavior — route via E, no unauthorized-taxiway detour, hold-short annotated at E.
    /// RED against current code (route detours via A/B and warns).
    /// </summary>
    [Fact]
    public void EmbeddedHsE_RoutesThroughE_NoUnauthorizedDetour()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, JustBeforeTaxi);
        var aircraft = engine.FindAircraft(Callsign);
        if (aircraft is null)
        {
            return;
        }

        var result = engine.SendCommand(Callsign, "TAXI D C HS E RWY 28R");
        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        var route = aircraft.Ground.AssignedTaxiRoute;
        LogRoute("HS E", result, route);
        Assert.NotNull(route);

        var taxiways = StraightSegmentTaxiways(route);

        // No detour through taxiways the controller never named.
        Assert.False(HasUnauthorizedWarning(route), $"Unexpected unauthorized-path warning: [{string.Join(" | ", route.Warnings)}]");
        Assert.DoesNotContain("A", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("B", taxiways, StringComparer.OrdinalIgnoreCase);

        // The route is steered through E, and E is annotated as an explicit hold-short.
        Assert.Contains("E", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            route.HoldShortPoints,
            hs => (hs.Reason == HoldShortReason.ExplicitHoldShort) && string.Equals(hs.TargetName, "E", StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// Pins the target: naming E in the path (<c>TAXI D C E HS E RWY 28R</c>) is already clean.
    /// Passes on current code and after the fix.
    /// </summary>
    [Fact]
    public void PathFormWithE_IsAlreadyClean()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, JustBeforeTaxi);
        var aircraft = engine.FindAircraft(Callsign);
        if (aircraft is null)
        {
            return;
        }

        var result = engine.SendCommand(Callsign, "TAXI D C E HS E RWY 28R");
        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        var route = aircraft.Ground.AssignedTaxiRoute;
        LogRoute("path E", result, route);
        Assert.NotNull(route);

        var taxiways = StraightSegmentTaxiways(route);
        Assert.False(HasUnauthorizedWarning(route), $"Unexpected unauthorized-path warning: [{string.Join(" | ", route.Warnings)}]");
        Assert.DoesNotContain("A", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("B", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("E", taxiways, StringComparer.OrdinalIgnoreCase);
    }
}
