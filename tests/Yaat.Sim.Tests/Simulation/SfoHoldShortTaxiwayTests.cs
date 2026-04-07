using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for taxiway hold-short positioning and departure clearance.
///
/// Recording: S1-SFO-2 Ground Control — N346G given TAXI C E 28R HS E at t=31s.
/// Bug 1: Aircraft stopped at C/SBE intersection (one node before C/E)
///         instead of a dynamic offset from the C/E intersection node.
/// Bug 2: LUAW/CTO from taxiway hold-short failed because "E" isn't a runway.
///
/// Later commands in the recording re-taxi N346G (t=108, t=234) and clear for
/// takeoff (t=250). Tests replay only to t=32 to capture the initial route with HS E.
/// </summary>
public class SfoHoldShortTaxiwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-hs-taxiway-recording.zip";
    private const string SeparateHsRecordingPath = "TestData/sfo-cto-taxiway-holdshort-recording.yaat-bug-report-bundle.zip";

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

    [Fact]
    public void Diagnostic_LogN346GTaxiRoute()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=32 — just after TAXI C E 28R HS E is issued at t=31
        engine.Replay(recording, 32);

        var aircraft = engine.FindAircraft("N346G");
        if (aircraft is null)
        {
            output.WriteLine("N346G not found at t=32");
            return;
        }

        output.WriteLine($"N346G: type={aircraft.AircraftType} pos=({aircraft.Latitude:F6}, {aircraft.Longitude:F6}), gs={aircraft.GroundSpeed:F1}");
        if (aircraft.AssignedTaxiRoute is { } route)
        {
            output.WriteLine($"Route: {route.ToSummary()}, segIdx={route.CurrentSegmentIndex}/{route.Segments.Count}");
            foreach (var hs in route.HoldShortPoints)
            {
                output.WriteLine(
                    $"  HS: node={hs.NodeId} target={hs.TargetName} reason={hs.Reason} cleared={hs.IsCleared} lat={hs.Latitude:F6} lon={hs.Longitude:F6}"
                );
            }

            foreach (var seg in route.Segments)
            {
                output.WriteLine($"  Seg: {seg.FromNodeId}->{seg.ToNodeId} on {seg.TaxiwayName}");
            }
        }

        if (aircraft.Phases is not null)
        {
            for (int i = 0; i < aircraft.Phases.Phases.Count; i++)
            {
                var phase = aircraft.Phases.Phases[i];
                string marker = i == aircraft.Phases.CurrentIndex ? " <<<" : "";
                output.WriteLine($"Phase[{i}]: {phase.Name} ({phase.Status}){marker}");
            }
        }

        // Tick forward until hold-short or 600s, logging position
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("N346G");
            if (aircraft is null)
            {
                break;
            }

            if (t % 30 == 0 || aircraft.Phases?.CurrentPhase is HoldingShortPhase)
            {
                output.WriteLine(
                    $"  t={t} pos=({aircraft.Latitude:F6}, {aircraft.Longitude:F6}) gs={aircraft.GroundSpeed:F1} phase={aircraft.Phases?.CurrentPhase?.Name ?? "null"}"
                );
            }

            if (aircraft.Phases?.CurrentPhase is HoldingShortPhase hsPhase)
            {
                output.WriteLine($"  Holding short of {hsPhase.HoldShort.TargetName} at node {hsPhase.HoldShort.NodeId}");
                break;
            }
        }
    }

    [Fact]
    public void N346G_HoldShortOfE_HasOffsetPosition()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=32 — just after TAXI C E 28R HS E at t=31
        engine.Replay(recording, 32);

        var aircraft = engine.FindAircraft("N346G");
        if (aircraft is null)
        {
            return;
        }

        var route = aircraft.AssignedTaxiRoute;
        Assert.NotNull(route);

        // Find the explicit hold-short for taxiway E
        HoldShortPoint? hsE = null;
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.ExplicitHoldShort && string.Equals(hs.TargetName, "E", StringComparison.OrdinalIgnoreCase))
            {
                hsE = hs;
                break;
            }
        }

        Assert.NotNull(hsE);

        // The hold-short must have computed lat/lon (dynamic position)
        Assert.NotNull(hsE.Latitude);
        Assert.NotNull(hsE.Longitude);

        // The hold-short node should be the C/E intersection node (has an edge on taxiway E)
        var layout = aircraft.GroundLayout;
        Assert.NotNull(layout);

        Assert.True(layout.Nodes.TryGetValue(hsE.NodeId, out var intersectionNode), $"Node {hsE.NodeId} not found in layout");

        // Verify this node has an edge on taxiway E (may be a junction arc after filleting)
        bool hasEdgeOnE = intersectionNode.Edges.Any(e => e.MatchesTaxiway("E"));
        Assert.True(hasEdgeOnE, $"Node {hsE.NodeId} should have an edge on taxiway E");

        // The hold-short position should be offset from the intersection node
        double distFromIntersection = GeoMath.DistanceNm(
            hsE.Latitude.Value,
            hsE.Longitude.Value,
            intersectionNode.Latitude,
            intersectionNode.Longitude
        );
        double distFt = distFromIntersection * 6076.12;

        output.WriteLine($"Hold-short node: {hsE.NodeId}");
        output.WriteLine($"Intersection pos: ({intersectionNode.Latitude:F6}, {intersectionNode.Longitude:F6})");
        output.WriteLine($"Hold-short pos: ({hsE.Latitude:F6}, {hsE.Longitude:F6})");
        output.WriteLine($"Offset from intersection: {distFt:F1} ft");

        // The offset should be > 0 (not at the node) and reasonable (< 200ft for small GA)
        Assert.True(distFt > 20.0, $"Hold-short offset ({distFt:F1}ft) too small — should be > 20ft");
        Assert.True(distFt < 200.0, $"Hold-short offset ({distFt:F1}ft) too large — should be < 200ft");
    }

    [Fact]
    public void N346G_LuawFromTaxiwayHoldShort_StoresClearanceAndResumes()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=32, then tick until N346G reaches the hold-short for E
        engine.Replay(recording, 32);

        var aircraft = engine.FindAircraft("N346G");
        if (aircraft is null)
        {
            return;
        }

        // Tick until N346G is in HoldingShortPhase for taxiway E
        HoldingShortPhase? holdingPhase = null;
        for (int t = 0; t < 600; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("N346G");
            if (aircraft is null)
            {
                break;
            }

            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hs
                && string.Equals(hs.HoldShort.TargetName, "E", StringComparison.OrdinalIgnoreCase)
            )
            {
                holdingPhase = hs;
                output.WriteLine($"N346G reached hold-short for E after {t}s of ticking");
                break;
            }
        }

        if (holdingPhase is null || aircraft is null)
        {
            output.WriteLine("N346G never reached HoldingShortPhase for E");
            return;
        }

        // Issue LUAW while holding short of taxiway E
        var result = engine.SendCommand("N346G", "LUAW");
        output.WriteLine($"LUAW result: success={result.Success}, message={result.Message}");

        Assert.True(result.Success, $"LUAW from taxiway hold-short should succeed: {result.Message}");

        // After LUAW, the departure clearance should be stored on the phase list
        Assert.NotNull(aircraft.Phases!.DepartureClearance);
        output.WriteLine($"Departure clearance stored: type={aircraft.Phases.DepartureClearance.Type}");
    }

    /// <summary>
    /// Reproduces the bug where CTO after a separate HS command fails with
    /// "Cannot resolve runway E". When HS E is issued as a separate command
    /// (not part of the taxi string), ExplicitHoldShort("E") is appended AFTER
    /// DestinationRunway("28R"), so the last-match loop picks "E" as the runway.
    ///
    /// Recording: S1-SFO-2 Ground Control — N346G given TAXI C E RWY 28R at t=31,
    /// then HS E at t=33 as a separate command. CTO at ~t=96 should succeed with 28R.
    /// </summary>
    [Fact]
    public void N346G_CtoFromSeparateHsCommand_ResolvesDestinationRunway()
    {
        var recording = RecordingLoader.Load(SeparateHsRecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=34 — after TAXI C E RWY 28R (t=31) and separate HS E (t=33)
        engine.Replay(recording, 34);

        var aircraft = engine.FindAircraft("N346G");
        if (aircraft is null)
        {
            return;
        }

        // Tick until N346G is in HoldingShortPhase for taxiway E
        HoldingShortPhase? holdingPhase = null;
        for (int t = 0; t < 600; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft("N346G");
            if (aircraft is null)
            {
                break;
            }

            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hs
                && string.Equals(hs.HoldShort.TargetName, "E", StringComparison.OrdinalIgnoreCase)
            )
            {
                holdingPhase = hs;
                output.WriteLine($"N346G reached hold-short for E after {t}s of ticking");
                break;
            }
        }

        if (holdingPhase is null || aircraft is null)
        {
            output.WriteLine("N346G never reached HoldingShortPhase for E");
            return;
        }

        // Verify the hold-short list has ExplicitHoldShort AFTER DestinationRunway
        // (this is the condition that triggers the bug)
        var route = aircraft.AssignedTaxiRoute;
        Assert.NotNull(route);
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  HS: node={hs.NodeId} target={hs.TargetName} reason={hs.Reason}");
        }

        // Issue CTO while holding short of taxiway E — should resolve runway 28R, not "E"
        var result = engine.SendCommand("N346G", "CTO");
        output.WriteLine($"CTO result: success={result.Success}, message={result.Message}");

        Assert.True(result.Success, $"CTO from taxiway hold-short should succeed: {result.Message}");
        Assert.Equal("28R", aircraft.DepartureRunway);
    }
}
