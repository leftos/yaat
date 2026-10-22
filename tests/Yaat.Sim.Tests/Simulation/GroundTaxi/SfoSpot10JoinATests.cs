using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-717: an aircraft held at SFO spot 10 on B5, facing east, and cleared <c>TAXI A</c> turned 180° at the spot onto the
/// B5–T9 fillet before joining A. The start bridge onto A (<c>SegmentExpander.BridgeStartToTaxiway</c>) searches three hops
/// first, and the only A entry in that reach from the B5 node west of the spot runs through that fillet, leaving spot 10 at
/// 252.8° after arriving at 72.8°; the bridge rejects it for turning back off the start heading. The correct entry, B5 east
/// onto B4–B5 and A, lies a hop further. Each case pins its own start pose so it does not depend on where an aircraft stops
/// at the spot.
/// </summary>
public class SfoSpot10JoinATests
{
    private const double StartHeadingTrue = 72.7;

    private readonly ITestOutputHelper _output;

    public SfoSpot10JoinATests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout? LoadSfo() => TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("SFO");

    /// <summary>UAL58 (B77W) on B5 just west of spot 10: the case that turned about onto the B5–T9 fillet.</summary>
    [Fact]
    public void TaxiA_FromB5WestOfSpot10_JoinsAWithoutTurningAbout() => AssertJoinsAWithoutTurningAbout(175, WakeTurbulenceData.WakeClass.Heavy);

    /// <summary>UAL1620 (A320) stopped on spot 10 itself: a regression guard, the bridge already leaves it eastbound.</summary>
    [Fact]
    public void TaxiA_FromSpot10_JoinsAWithoutTurningAbout() => AssertJoinsAWithoutTurningAbout(11, WakeTurbulenceData.WakeClass.Large);

    /// <summary>
    /// The poses and clearances of the issue's recording (S1-SFO-2 GC 28/01 High Intensity, client 0.16.2), each aircraft
    /// stopped on B5 short of spot 10 after <c>TAXI B B5 $10</c>, cleared through the engine's own TAXI handler.
    /// </summary>
    [Theory]
    [MemberData(nameof(RecordedCallsigns))]
    public void RecordedPoseOnB5_TaxiA_JoinsAWithoutTurningAbout(string callsign)
    {
        if (SfoGroundHarness.Build(_output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        RecordedPose pose = RecordedPoses[callsign];
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            callsign,
            pose.Type,
            (ground.Layout.Nodes[175], new TrueHeading(pose.Heading)),
            new HoldingInPositionPhase()
        );
        aircraft.Position = pose.Position;
        aircraft.Ground.CurrentTaxiway = "B5";

        CommandResult result = ground.Engine.SendCommand(callsign, pose.Command);
        _output.WriteLine($"{pose.Command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(_output, route);

        Assert.Contains(route.Segments, s => s.Edge.Edge.MatchesTaxiway("A"));
        AssertNoTurnAbout(route, pose.Heading);
    }

    /// <summary>The callsigns of <see cref="RecordedPoses"/>, one theory case each.</summary>
    public static TheoryData<string> RecordedCallsigns => [.. RecordedPoses.Keys];

    /// <summary>A recorded aircraft's type, position, true heading and the clearance it was given.</summary>
    private sealed record RecordedPose(string Type, LatLon Position, double Heading, string Command);

    private static readonly Dictionary<string, RecordedPose> RecordedPoses = new()
    {
        ["UAL1620"] = new RecordedPose("A320", new LatLon(37.62012711341592, -122.39306643038891), 72.74767109094034, "TAXI A A1 1R"),
        ["UAL58"] = new RecordedPose("B77W", new LatLon(37.62008340263422, -122.3932438571435), 72.69386460680437, "TAXI A F 28L"),
        ["DLH455"] = new RecordedPose("B744", new LatLon(37.620153882497746, -122.39295753420788), 72.7581862180133, "TAXI A F 28L"),
    };

    private void AssertJoinsAWithoutTurningAbout(int startNodeId, WakeTurbulenceData.WakeClass wakeClass)
    {
        AirportGroundLayout? layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.ContainsKey(startNodeId), $"start node {startNodeId} not found in the SFO layout");

        var diag = new List<string>();
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNodeId,
            ["A"],
            out string? failReason,
            new ExplicitPathOptions
            {
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                DestinationRunway = null,
                StartHeadingTrue = StartHeadingTrue,
                DiagnosticLog = diag.Add,
            },
            AircraftCategory.Jet,
            wakeClass
        );
        foreach (string line in diag.Where(l => l.StartsWith("[bridge]", StringComparison.Ordinal)))
        {
            _output.WriteLine(line);
        }

        Assert.True(route is not null, $"TAXI A from #{startNodeId} must resolve: {failReason}");
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => $"{s.TaxiwayName}:{s.FromNodeId}->{s.ToNodeId}")));

        Assert.True(layout.Nodes[route.Segments[^1].ToNodeId].Edges.Any(e => e.MatchesTaxiway("A")), "the route must end on A");
        AssertNoTurnAbout(route, StartHeadingTrue);
    }

    /// <summary>The route uses no T9 edge, leaves the start heading and turns at every junction within the jet limit.</summary>
    private static void AssertNoTurnAbout(TaxiRoute route, double startHeading)
    {
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("T9"));

        double limit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet);
        double startDelta = RouteCostFunction.HeadingDelta(startHeading, route.Segments[0].Edge.DepartureBearing);
        Assert.True(startDelta <= limit, $"first segment departs {startDelta:F0}° off the start heading, over the {limit:F0}° limit");
        for (int i = 1; i < route.Segments.Count; i++)
        {
            DirectionalEdge prev = route.Segments[i - 1].Edge;
            DirectionalEdge cur = route.Segments[i].Edge;
            if (GeometricAdmissibility.IsNoOpEdge(prev.Edge) || GeometricAdmissibility.IsNoOpEdge(cur.Edge))
            {
                continue;
            }

            double delta = RouteCostFunction.HeadingDelta(prev.ArrivalBearing, cur.DepartureBearing);
            Assert.True(delta <= limit, $"segment {i} ({cur.TaxiwayName} {cur.FromNodeId}->{cur.ToNodeId}) turns {delta:F0}° > {limit:F0}°");
        }
    }
}
