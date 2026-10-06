using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #880: an aircraft resting mid-edge on a long straight taxiway, far from any graph node, had its TAXI start
/// node picked by distance to nodes alone. At KOAK, an aircraft on C's centreline west of H was 333 ft from a node on
/// the parallel D and 376 ft from the nearest C node, so its route began with a free-space leg onto D. At KSFO the
/// same pose mid-B started the route on A and "TAXI B" was refused. A taxi starts on the taxiway the aircraft is on.
/// </summary>
public class TaxiStartsOnOccupiedTaxiwayTests(ITestOutputHelper output)
{
    private const double MinLongEdgeFt = 1000.0;
    private const int TaxiTickSeconds = 20;
    private const double MaxTurnFromStartDeg = 30.0;
    private const double MinTaxiSpeedKts = 10.0;

    /// <summary>How far past a straight edge's end, into the fillet arc that continues it, the aircraft stands.</summary>
    private const double IntoFilletFt = 20.0;

    /// <summary>Fillet arcs from this long: the arc's far end is clear of the at-node tolerance of the aircraft 20 ft into it.</summary>
    private const double MinFilletFt = 40.0;

    /// <summary>Fillet arcs up to this long: a junction fillet, not a long high-speed exit curve.</summary>
    private const double MaxFilletFt = 400.0;

    /// <summary>How many C nodes the walk behind the aircraft visits looking for a taxiway off C.</summary>
    private const int MaxWalkNodes = 30;

    [Fact]
    public void TaxiOnC_WestOfH_MidEdge_RouteStartsOnC()
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);

        LatLon position = Along(tangentCut.Position, farEnd.Position, 0.35);
        AirportGroundLayout.NearestTaxiEdge? onEdge = layout.FindNearestTaxiEdge(position);
        Assert.NotNull(onEdge);
        Assert.True(onEdge.Value.Edge.MatchesTaxiway("C"), $"nearest taxi edge is {onEdge.Value.Edge.TaxiwayName}, not C");

        double headingDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        AircraftState aircraft = MakeAircraft(layout, position, headingDeg, "OAK", "C");

        TaxiRoute route = Resolve(layout, aircraft, "TAXI C B RWY 28R HS H");

        GroundNode firstFrom = FirstLayoutSegment(route).Edge.FromNode;
        Assert.True(OnStraightEdgeOf(firstFrom, "C"), $"route joins the graph at node {firstFrom.Id}, not on C");
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("D"));
        Assert.Equal("C", route.FormatTaxiwaySequence().Split(' ')[0]);

        HoldShortPoint holdH = Assert.Single(route.HoldShortPoints, hs => string.Equals(hs.TargetName, "H", StringComparison.OrdinalIgnoreCase));
        Assert.True(OnStraightEdgeOf(layout.Nodes[holdH.NodeId], "C"), $"HS H is bound at node {holdH.NodeId}, which has no straight C edge");
    }

    /// <summary>
    /// A C172 mid-C at KOAK, facing toward H, cleared to a taxiway that branches off C behind it. The route from the C node
    /// ahead would come straight back over the edge the aircraft stands on, so it turns about on C: segment 0 is the
    /// free-space leg back to the edge's far node, and no segment drives that edge. Ticked through the turn about, it holds
    /// no more than the slow-turn speed and ends facing the far node.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnsAboutToTheFarNode()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"occupied C edge {tangentCut.Id}-{farEnd.Id}; {command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        TaxiRouteSegment first = route.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(route.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));

        double maxTurnSpeedKts = 0.0;
        bool FacesFarEnd() =>
            GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, farEnd.Position)) <= MaxTurnFromStartDeg;
        int facedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            FacesFarEnd,
            TurnAboutTickSeconds,
            _ => maxTurnSpeedKts = FacesFarEnd() ? maxTurnSpeedKts : Math.Max(maxTurnSpeedKts, aircraft.GroundSpeed)
        );
        output.WriteLine($"faced node {farEnd.Id} after {facedAt}s; max {maxTurnSpeedKts:F1} kt while turning");

        Assert.True(facedAt > 0, $"the aircraft did not face node {farEnd.Id} within {TurnAboutTickSeconds}s");
        double maxAllowedKts = CategoryPerformance.SlowTurnSpeedKts + TurnSpeedOvershootKts;
        Assert.True(
            maxTurnSpeedKts <= maxAllowedKts,
            $"the aircraft turned about at up to {maxTurnSpeedKts:F2} kt, above the {CategoryPerformance.SlowTurnSpeedKts:F0} kt slow-turn speed"
                + $" plus {TurnSpeedOvershootKts:F1} kt"
        );
    }

    /// <summary>
    /// The same pose in a B738: a jet has no room to turn about on a taxiway, so where only the far-node route avoids
    /// coming back over the edge it stands on, it refuses the TAXI and its state is untouched.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.EndsWith("no room to turn around on charlie, request a route ahead.", result.PilotUnable?.Tts, StringComparison.Ordinal);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The same B738 angled 45° across C: it is not lined up along the taxiway, so it is not refused and takes the route
    /// from the far node, turning about as any other category would.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetAngledAcrossCIsNotRefused()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");
        var angled = new TrueHeading(aircraft.TrueHeading.Degrees + AcrossEdgeDeg);
        aircraft.TrueHeading = angled;
        aircraft.TrueTrack = angled;

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command} at heading {angled.Degrees:F0}: {result.Success} — {result.Message}");

        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(farEnd.Id, route.Segments[0].ToNodeId);
    }

    /// <summary>How far the angled jet's heading is turned off C: past the alignment within which a jet refuses to turn about.</summary>
    private const double AcrossEdgeDeg = 45.0;

    /// <summary>
    /// The C172 turned about on C by a controller's clearance reports the turn about as pending (the overlay draws it
    /// from that) until it has driven the leg back to the far node, and no longer once that leg is done.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnAboutPendingUntilTheLegCompletes()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(farEnd.Id, route.Segments[0].ToNodeId);

        Assert.True(aircraft.Ground.TaxiTurnAboutPending, "the turn about is not pending right after the clearance");

        int legDoneAt = SfoGroundHarness.TickUntil(ground.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, null);
        output.WriteLine($"left the turn-about leg after {legDoneAt}s");

        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        Assert.False(aircraft.Ground.TaxiTurnAboutPending, "the turn about is still pending after its leg completed");
    }

    /// <summary>
    /// The C172 turned about on C, snapshotted right after the clearance and restored into a fresh engine: the turn about is
    /// still pending there, and no longer once the restored aircraft has driven the leg back to the far node.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnAboutPendingSurvivesASnapshotRestore()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        Assert.True(aircraft.Ground.TaxiTurnAboutPending, "the turn about is not pending right after the clearance");

        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restored = BuildOak()!.Value;
        restored.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restoredAircraft = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(aircraft.Callsign));
        Assert.True(restoredAircraft.Ground.TaxiTurnAboutPending, "the turn about is not pending after the snapshot is restored");

        TaxiRoute route = restoredAircraft.Ground.AssignedTaxiRoute!;
        int legDoneAt = SfoGroundHarness.TickUntil(restored.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, null);
        output.WriteLine($"restored: left the turn-about leg after {legDoneAt}s");

        Assert.True(legDoneAt > 0, $"the restored aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        Assert.False(restoredAircraft.Ground.TaxiTurnAboutPending, "the turn about is still pending after the restored leg completed");
    }

    /// <summary>A C172 mid-C facing H, cleared along C ahead of it: the clearance turns nothing about, so nothing is pending.</summary>
    [Fact]
    public void TaxiOnC_RouteAhead_NoTurnAboutPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        const string Command = "TAXI C B RWY 28R HS H";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, Command);
        Assert.True(result.Success, $"'{Command}' was refused: {result.Message}");
        Assert.NotEqual(farEnd.Id, aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);

        Assert.False(aircraft.Ground.TaxiTurnAboutPending, "a clearance along C ahead reports a turn about");
    }

    /// <summary>How long the leg back to the far C node may take, turn included.</summary>
    private const int TurnAboutLegTickSeconds = 300;

    /// <summary>
    /// A B738 lined up along C toward H, but 20 ft off the centreline and 30 ft short of the C node ahead: the bearing to
    /// that node is 34° off its nose, yet the jet is lined up with the taxiway, so it still refuses to turn about.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetLinedUpOffCentrelineNearTheNodeIsRefused()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        double alongDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        LatLon onCentreline = GeoMath.ProjectPoint(tangentCut.Position, new TrueHeading(alongDeg + 180.0), ShortOfNodeFt / GeoMath.FeetPerNm);
        LatLon position = GeoMath.ProjectPoint(onCentreline, new TrueHeading(alongDeg + 90.0), OffCentrelineFt / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, alongDeg, "OAK", "C");
        ground.Engine.World.AddAircraft(aircraft);
        double offNoseDeg = GeoMath.AbsBearingDifference(alongDeg, GeoMath.BearingTo(position, tangentCut.Position));

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}, node {tangentCut.Id} {offNoseDeg:F0} deg off the nose: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
    }

    /// <summary>How far short of the C node ahead the lined-up, off-centreline jet stands.</summary>
    private const double ShortOfNodeFt = 30.0;

    /// <summary>How far off C's centreline the lined-up jet stands.</summary>
    private const double OffCentrelineFt = 20.0;

    /// <summary>
    /// The same jet pose with the TAXI coming from a scenario preset: no controller is there to hear "unable" and re-issue,
    /// so the jet is not refused and takes the route from the C node ahead, as before the turn about existed.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_ScriptedJetTaxiIsNotRefused()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext scripted = ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null);
        CommandResult result = CommandDispatcher.Dispatch(Assert.IsType<TaxiCommand>(parsed.Value), aircraft, scripted);
        output.WriteLine($"scripted {command}: {result.Success} — {result.Message}");

        Assert.True(result.Success, $"the scripted '{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(tangentCut.Id, route.Segments[0].FromNodeId);
        Assert.True(route.StartsWithTurnAbout, "the kept route drives back over C from the node ahead, yet reports no turn about");
    }

    /// <summary>
    /// A C172 mid-C facing H, cleared along a route drawn to the C node behind it: from that node the drawn route is already
    /// taxied and resolves nothing, so the route from the node ahead is kept, and its first segment drives straight back
    /// over the edge the aircraft stands on. The aircraft turns about where it stands, and the route reports it.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_KeepsTheRouteAheadAndReportsTheTurnAbout()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI #{farEnd.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        TaxiRouteSegment first = route.Segments[0];
        Assert.True(ReferenceEquals(first.Edge.Edge, occupied), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.True(route.StartsWithTurnAbout, "the route drives back over C from the node ahead, yet reports no turn about");
    }

    /// <summary>
    /// The same drawn route from the controller to a B738 lined up along C: the only route is the one from the node ahead,
    /// which turns the jet about where it stands, so it refuses for want of room to turn around.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_LinedUpJetRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI #{farEnd.Id}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A C172 mid-way along a straight KOAK taxi edge whose end behind it is a runway holding position, facing the other
    /// end, cleared along a route drawn through that holding position: the route from the holding position wins, but no
    /// approach leg is driven up to a holding position, so that route opens with no leg back to it and reports no turn
    /// about.
    /// </summary>
    [Fact]
    public void DrawnRouteBackThroughARunwayHoldShort_NoLegToIt_ReportsNoTurnAbout()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode ahead, GroundNode holdShort, GroundEdge stub) =
            FirstStubToARunwayHoldShort(ground.Layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends at a runway holding position");
        LatLon position = Along(holdShort.Position, ahead.Position, 0.5);
        AircraftState aircraft = MakeAircraft(
            ground.Layout,
            position,
            GeoMath.BearingTo(holdShort.Position, ahead.Position),
            "OAK",
            stub.TaxiwayName
        );
        aircraft.AircraftType = "C172";
        ground.Engine.World.AddAircraft(aircraft);
        GroundNode beyond = holdShort.Edges.First(e => !ReferenceEquals(e, stub)).OtherNode(holdShort);

        string command = $"TAXI #{holdShort.Id} #{beyond.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine(
            $"{stub.TaxiwayName} edge {ahead.Id}-{holdShort.Id}, holding position for {holdShort.RunwayId}; "
                + $"{command}: {result.Success} — {result.Message}"
        );
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(holdShort.Id, route.Segments[0].FromNodeId);
        Assert.False(route.StartsWithTurnAbout, "a route with no leg back to the far node reports a turn about");
    }

    /// <summary>
    /// The first straight KOAK taxi edge (by its lower node id) from a taxi node to a runway holding position with another
    /// edge beyond it, half-way along which the aircraft stands mid-edge (<see cref="AirportGroundLayout.FindMidEdgeTaxiStart"/>);
    /// null when there is none.
    /// </summary>
    private static (GroundNode Ahead, GroundNode HoldShort, GroundEdge Stub)? FirstStubToARunwayHoldShort(AirportGroundLayout layout)
    {
        IEnumerable<GroundEdge> taxiEdges = layout
            .Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0))
            .OrderBy(e => Math.Min(e.Nodes[0].Id, e.Nodes[1].Id));
        foreach (GroundEdge edge in taxiEdges)
        {
            GroundNode[] holds = [.. edge.Nodes.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.Edges.Count > 1))];
            if (holds.Length != 1)
            {
                continue;
            }

            GroundNode ahead = edge.OtherNode(holds[0]);
            if (layout.FindMidEdgeTaxiStart(Along(holds[0].Position, ahead.Position, 0.5)) == edge)
            {
                return (ahead, holds[0], edge);
            }
        }

        return null;
    }

    /// <summary>
    /// A C172 on M2 cleared <c>TAXI @B26</c>: the route from the M2 node ahead resolves none, so the clearance is re-planned
    /// from the node behind, and the aircraft turns about on M2 toward it.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ReplansFromTheNodeBehind()
    {
        if (SpawnOnM2("C172") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: false);

        Assert.True(result.Success, $"'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);
    }

    /// <summary>
    /// A B738 lined up along M2, <c>TAXI @B26</c> from a scenario preset: the route from the M2 node ahead resolves none,
    /// so the scripted jet is not left without a route, and takes the one from the node behind.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ScriptedJetTakesTheRouteFromTheNodeBehind()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: true);

        Assert.True(result.Success, $"the scripted 'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);
    }

    /// <summary>
    /// A B738 lined up along M2, <c>TAXI @B26</c> from the controller: the route from the M2 node ahead resolves none and
    /// the one from the node behind needs a turn about on M2, so the jet refuses for want of room to turn around.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_LinedUpJetRefusesToTurnAround()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: false);

        Assert.False(result.Success, $"'TAXI @{M2Gate}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(spawned.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A B738 lined up along M2, cleared to a gate whose ramp-confined route resolves from neither end of M2: the jet
    /// gives the first attempt's own refusal, not the turn-about one.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromBothEnds_LinedUpJetGivesTheFirstRefusal()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{UnreachableGate}", isScenarioScripted: false);

        Assert.False(result.Success, $"'TAXI @{UnreachableGate}' was accepted: {result.Message}");
        Assert.NotEqual(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), result.Message);
        Assert.StartsWith($"Unable, need a route to gate {UnreachableGate}", result.Message, StringComparison.Ordinal);
        Assert.Null(spawned.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A B738 held after a push on M2, lined up toward <see cref="M2NodeAhead"/>, given the scenario preset
    /// <c>PUSH; TAXI @B26</c>: the push tows it straight back along M2 and it ends still lined up toward the node ahead, so
    /// the TAXI from the controller would be refused for want of room to turn around. A preset is not refused: it takes
    /// the route from the node behind. Snapshotted mid-push and restored, the queued TAXI still fires as the preset it is
    /// and takes the same route as the live run.
    /// </summary>
    [Fact]
    public void ScriptedPushThenTaxi_RestoredMidPush_TaxisAsLive()
    {
        if (SpawnHeldAfterPushOnM2() is not { } controller)
        {
            return;
        }

        // The pose: after the push, the controller's TAXI is refused.
        PushAndHold(controller, isScenarioScripted: false);
        ParseResult<ParsedCommand> parsedTaxi = CommandParser.Parse($"TAXI @{M2Gate}");
        Assert.True(parsedTaxi.IsSuccess, parsedTaxi.Reason);
        CommandResult refused = GroundCommandHandler.TryTaxi(
            controller.Aircraft,
            Assert.IsType<TaxiCommand>(parsedTaxi.Value),
            controller.Ground.Layout
        );
        output.WriteLine($"controller TAXI @{M2Gate} after the push: {refused.Success} — {refused.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), refused.Message);

        TaxiRoute live = PushThenTaxiFires(SpawnHeldAfterPushOnM2()!.Value, snapshotMidPush: false);
        TaxiRoute restored = PushThenTaxiFires(SpawnHeldAfterPushOnM2()!.Value, snapshotMidPush: true);

        Assert.Equal(M2NodeBehind, live.Segments[0].ToNodeId);
        Assert.Equal(live.Segments.Select(s => s.ToNodeId), restored.Segments.Select(s => s.ToNodeId));
        Assert.Equal(live.StartsWithTurnAbout, restored.StartsWithTurnAbout);
    }

    /// <summary>How far along M2 from <see cref="M2NodeBehind"/> to <see cref="M2NodeAhead"/> the aircraft held after a push stands.</summary>
    private const double HeldAfterPushFraction = 0.85;

    /// <summary>How long a push, or a push and the TAXI queued behind it, may take.</summary>
    private const int PushTickSeconds = 240;

    /// <summary>
    /// A stopped B738 in <see cref="HoldingAfterPushbackPhase"/> <see cref="HeldAfterPushFraction"/> of the way along SFO's
    /// M2 edge toward <see cref="M2NodeAhead"/> and lined up toward it, or null when the layout is unavailable.
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft)? SpawnHeldAfterPushOnM2()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        GroundNode ahead = ground.Layout.Nodes[M2NodeAhead];
        GroundNode behind = ground.Layout.Nodes[M2NodeBehind];
        LatLon position = Along(behind.Position, ahead.Position, HeldAfterPushFraction);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(behind.Position, ahead.Position), "SFO", "M2");
        aircraft.AircraftType = "B738";
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        ground.Engine.World.AddAircraft(aircraft);
        return (ground, aircraft);
    }

    /// <summary>Dispatches a bare <c>PUSH</c> and ticks until the aircraft is held after it.</summary>
    private void PushAndHold((SfoGround Ground, AircraftState Aircraft) spawned, bool isScenarioScripted)
    {
        AircraftState aircraft = spawned.Aircraft;
        CommandResult pushed = CommandDispatcher.DispatchCompound(
            ParseCompound("PUSH"),
            aircraft,
            spawned.Ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted, facilityHint: null)
        );
        Assert.True(pushed.Success, pushed.Message);
        int heldAt = SfoGroundHarness.TickUntil(
            spawned.Ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase,
            PushTickSeconds,
            null
        );
        output.WriteLine(
            $"push held after {heldAt}s at ({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) hdg {aircraft.TrueHeading.Degrees:F0}, "
                + $"mid-edge {spawned.Ground.Layout.FindMidEdgeTaxiStart(aircraft.Position)?.TaxiwayName ?? "none"}"
        );
        Assert.True(heldAt > 0, $"the push did not end within {PushTickSeconds}s");
    }

    /// <summary>
    /// Dispatches the preset <c>PUSH; TAXI @B26</c> and ticks until the queued TAXI has fired, after restoring a snapshot
    /// taken mid-push into a fresh engine when <paramref name="snapshotMidPush"/>; the route it took.
    /// </summary>
    private TaxiRoute PushThenTaxiFires((SfoGround Ground, AircraftState Aircraft) spawned, bool snapshotMidPush)
    {
        (SfoGround ground, AircraftState aircraft) = spawned;
        CommandResult dispatched = CommandDispatcher.DispatchCompound(
            ParseCompound($"PUSH; TAXI @{M2Gate}"),
            aircraft,
            ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null)
        );
        Assert.True(dispatched.Success, dispatched.Message);

        if (snapshotMidPush)
        {
            int movingAt = SfoGroundHarness.TickUntil(
                ground.Engine,
                () => (aircraft.Phases?.CurrentPhase is PushbackPhase) && (aircraft.GroundSpeed > 1.0),
                PushTickSeconds,
                null
            );
            Assert.True(movingAt > 0, "the push never got moving");
            StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
            ground = SfoGroundHarness.Build(output, autoCross: false)!.Value;
            ground.Engine.RestoreFromSnapshot(snapshot);
            aircraft = Assert.IsType<AircraftState>(ground.Engine.FindAircraft(aircraft.Callsign));
            Assert.IsType<PushbackPhase>(aircraft.Phases?.CurrentPhase);
        }

        AircraftState flown = aircraft;
        int firedAt = SfoGroundHarness.TickUntil(ground.Engine, () => flown.Ground.AssignedTaxiRoute is not null, PushTickSeconds, null);
        output.WriteLine($"{(snapshotMidPush ? "restored" : "live")}: TAXI fired after {firedAt}s, phase {flown.Phases?.CurrentPhase?.Name}");
        Assert.True(firedAt > 0, $"the queued TAXI took no route within {PushTickSeconds}s (phase {flown.Phases?.CurrentPhase?.Name})");
        SfoGroundHarness.DumpRoute(output, flown.Ground.AssignedTaxiRoute!);
        return flown.Ground.AssignedTaxiRoute!;
    }

    private static CompoundCommand ParseCompound(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return parsed.Value!;
    }

    /// <summary>The M2 node ahead of the aircraft on M2: no route inside the ramp to <see cref="M2Gate"/> resolves from it.</summary>
    private const int M2NodeAhead = 14;

    /// <summary>The M2 node behind the aircraft on M2: the route inside the ramp to <see cref="M2Gate"/> resolves from it.</summary>
    private const int M2NodeBehind = 15;

    /// <summary>The gate cleared from M2 with no taxiway named, so the route stays inside the ramp.</summary>
    private const string M2Gate = "B26";

    /// <summary>A gate in another terminal's ramp: no route inside the ramp reaches it from either end of M2.</summary>
    private const string UnreachableGate = "G10";

    /// <summary>
    /// A stopped <paramref name="type"/> 60% of the way along SFO's M2 edge from <see cref="M2NodeBehind"/> to
    /// <see cref="M2NodeAhead"/>, facing the node ahead and lined up along M2, or null when the layout is unavailable.
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft, GroundNode Behind)? SpawnOnM2(string type)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        GroundNode ahead = ground.Layout.Nodes[M2NodeAhead];
        GroundNode behind = ground.Layout.Nodes[M2NodeBehind];
        LatLon position = Along(behind.Position, ahead.Position, 0.6);
        GroundEdge? occupied = ground.Layout.FindMidEdgeTaxiStart(position);
        Assert.True(
            (occupied is not null) && occupied.MatchesTaxiway("M2") && occupied.Nodes.Contains(ahead) && occupied.Nodes.Contains(behind),
            $"the aircraft is not mid-way along the M2 edge {behind.Id}-{ahead.Id}"
        );

        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(behind.Position, ahead.Position), "SFO", "M2");
        aircraft.AircraftType = type;
        ground.Engine.World.AddAircraft(aircraft);
        return (ground, aircraft, behind);
    }

    /// <summary><paramref name="command"/> for the aircraft on M2, from the controller or from a scenario preset.</summary>
    private CommandResult TaxiOnM2((SfoGround Ground, AircraftState Aircraft, GroundNode Behind) spawned, string command, bool isScenarioScripted)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(parsed.Value);
        CommandResult result = isScenarioScripted
            ? CommandDispatcher.Dispatch(
                taxi,
                spawned.Aircraft,
                spawned.Ground.Engine.BuildDispatchContext(spawned.Aircraft, isScenarioScripted: true, facilityHint: null)
            )
            : GroundCommandHandler.TryTaxi(spawned.Aircraft, taxi, spawned.Ground.Layout);
        output.WriteLine(
            $"{(isScenarioScripted ? "scripted " : "")}{command} ({spawned.Aircraft.AircraftType}): {result.Success} — {result.Message}"
        );
        if (spawned.Aircraft.Ground.AssignedTaxiRoute is { } route)
        {
            SfoGroundHarness.DumpRoute(output, route);
        }

        return result;
    }

    /// <summary>How long the turn about at the far C node may take.</summary>
    private const int TurnAboutTickSeconds = 90;

    /// <summary>How far above the slow-turn speed the ground speed may read during the turn about: physics overshoot within one sub-tick.</summary>
    private const double TurnSpeedOvershootKts = 0.2;

    /// <summary>Builds an engine over the committed KOAK layout, or null when the layout or navdata is unavailable.</summary>
    private SfoGround? BuildOak()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return null;
        }

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-c-turn-about",
                ScenarioName = "OAK C Turn About",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = false,
            },
        };
        return new SfoGround(engine, layout);
    }

    /// <summary>A stopped <paramref name="type"/> 35% of the way along C edge tangent cut → far end, facing the tangent cut (toward H).</summary>
    private static AircraftState SpawnMidCFacingH(SfoGround ground, GroundNode tangentCut, GroundNode farEnd, string type)
    {
        LatLon position = Along(tangentCut.Position, farEnd.Position, 0.35);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(farEnd.Position, tangentCut.Position), "OAK", "C");
        aircraft.AircraftType = type;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// An aircraft mid-C at KOAK, a quarter of the edge short of one end and facing it, whose route leaves that node by
    /// another edge turning more than 90°: it has passed neither end of the edge it is on, so it drives the free-space leg
    /// to the node rather than being read as past it.
    /// </summary>
    [Fact]
    public void ApproachLeg_MidEdge_RouteTurnsSharplyAtTheNodeAhead_DrivesToTheNode()
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode node, GroundNode behind, IGroundEdge turn) =
            FirstSharpTurnOffAStraightEdge(layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends in a turn of more than 90°");
        LatLon position = Along(behind.Position, node.Position, 0.75);
        output.WriteLine($"on edge {behind.Id}-{node.Id}, route leaves node {node.Id} by {turn.TaxiwayName} to {turn.OtherNode(node).Id}");
        var heading = new TrueHeading(GeoMath.BearingTo(behind.Position, node.Position));
        var graphRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = turn.TaxiwayName, Edge = turn.Directed(node, turn.OtherNode(node)) }],
            HoldShortPoints = [],
        };

        TaxiRoute route = TaxiApproachLeg.Prepend(layout, position, heading, graphRoute);
        SfoGroundHarness.DumpRoute(output, route);

        TaxiRouteSegment first = route.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(node.Id, first.ToNodeId);
    }

    /// <summary>Straight taxi edges from this long: three quarters along one, the aircraft is clear of both end nodes.</summary>
    private const double MinSharpTurnEdgeFt = 300.0;

    /// <summary>
    /// The first (by edge order) straight taxi edge of at least <see cref="MinSharpTurnEdgeFt"/>, three quarters along which
    /// the aircraft is on it (<see cref="AirportGroundLayout.FindOccupiedTaxiEdge"/>), with an end that is no holding
    /// position and has an edge leaving it more than 90° off the direction of arrival (<see cref="SharpTurnAt"/>), or null.
    /// </summary>
    private static (GroundNode Node, GroundNode Behind, IGroundEdge Turn)? FirstSharpTurnOffAStraightEdge(AirportGroundLayout layout)
    {
        foreach (GroundEdge edge in layout.Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0)))
        {
            if ((edge.DistanceNm * GeoMath.FeetPerNm) < MinSharpTurnEdgeFt)
            {
                continue;
            }

            foreach ((GroundNode node, GroundNode behind) in new[] { (edge.Nodes[1], edge.Nodes[0]), (edge.Nodes[0], edge.Nodes[1]) })
            {
                bool onEdge = layout.FindOccupiedTaxiEdge(Along(behind.Position, node.Position, 0.75)) == edge;
                if (onEdge && (node.Type != GroundNodeType.RunwayHoldShort) && (SharpTurnAt(node, behind, edge) is { } found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An edge other than <paramref name="occupied"/> leaving <paramref name="node"/> more than 90° off the direction of
    /// arrival from <paramref name="behind"/> (no ramp connector, no runway centreline), or null.
    /// </summary>
    private static (GroundNode Node, GroundNode Behind, IGroundEdge Turn)? SharpTurnAt(GroundNode node, GroundNode behind, IGroundEdge occupied)
    {
        double arrivalDeg = GeoMath.BearingTo(behind.Position, node.Position);
        IGroundEdge? turn = node.Edges.FirstOrDefault(e =>
            (e != occupied)
            && !e.IsRamp
            && !e.IsRunwayCenterline
            && (GeoMath.AbsBearingDifference(e.Directed(node, e.OtherNode(node)).DepartureBearing, arrivalDeg) > 90.0)
        );
        return turn is null ? null : (node, behind, turn);
    }

    [Fact]
    public void TaxiOnB_MidEdge_Sfo_RouteStartsOnB()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        GroundEdge longestB = LongestStraightB(layout);
        GroundNode from = longestB.Nodes[0];
        GroundNode to = longestB.Nodes[1];
        output.WriteLine($"longest B edge {from.Id}-{to.Id}, {longestB.DistanceNm * GeoMath.FeetPerNm:F0} ft");

        LatLon position = Along(from.Position, to.Position, 0.5);
        AircraftState aircraft = MakeAircraft(layout, position, GeoMath.BearingTo(from.Position, to.Position), "SFO", "B");

        TaxiRoute route = Resolve(layout, aircraft, "TAXI B");

        GroundNode firstFrom = FirstLayoutSegment(route).Edge.FromNode;
        Assert.True(OnStraightEdgeOf(firstFrom, "B"), $"route joins the graph at node {firstFrom.Id}, not on B");
    }

    /// <summary>
    /// A bare <c>TAXI B</c> mid-B at SFO, more than the free-space bound from the B node ahead: the route opens with the
    /// approach leg along B, starting where the aircraft stands.
    /// </summary>
    [Fact]
    public void TaxiOnB_MidEdge_Sfo_ApproachLegStartsAtTheAircraft()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnMidLongestB(ground, out _);

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);

        TaxiRouteSegment first = route.Segments[0];
        Assert.True(
            VirtualNode.IsVirtualEdge(first.Edge.Edge),
            $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}, not the approach leg"
        );
        double startOffFt = GeoMath.DistanceNm(first.Edge.FromNode.Position, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(startOffFt <= 1.0, $"the approach leg starts {startOffFt:F1} ft from the aircraft");
    }

    /// <summary>
    /// A bare <c>TAXI B</c> mid-B at SFO, ticked: the aircraft rolls on along B at taxi speed rather than crawling at the
    /// navigator's reacquire speed toward a route start hundreds of feet ahead, and it does not turn around.
    /// </summary>
    [Fact]
    public void TaxiOnB_MidEdge_Sfo_KeepsTaxiSpeed()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnMidLongestB(ground, out double startHeadingDeg);

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        SfoGroundHarness.DumpRoute(output, aircraft.Ground.AssignedTaxiRoute!);

        SfoGroundHarness.TickUntil(ground.Engine, () => false, TaxiTickSeconds, null);

        double offBFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "B", aircraft.Position);
        double turnedDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, startHeadingDeg);
        output.WriteLine($"after {TaxiTickSeconds}s: {offBFt:F0} ft off B, turned {turnedDeg:F0} deg, GS {aircraft.GroundSpeed:F1} kt");
        Assert.True(offBFt <= AirportGroundLayout.OnTaxiEdgeMaxOffsetFt, $"the aircraft is {offBFt:F0} ft off B");
        Assert.True(turnedDeg <= MaxTurnFromStartDeg, $"the aircraft turned {turnedDeg:F0} deg from its start heading");
        Assert.True(aircraft.GroundSpeed > MinTaxiSpeedKts, $"ground speed is {aircraft.GroundSpeed:F1} kt after {TaxiTickSeconds}s");
    }

    /// <summary>
    /// An aircraft 20 ft into a fillet arc past a straight edge's end is not on that edge: the edge's nearest point is its
    /// end node, behind the aircraft. The taxi start must not be that node, nor any other node behind the aircraft: it is
    /// the fillet's own end ahead.
    /// </summary>
    [Fact]
    public void AircraftInAFilletPastAnEdgeEnd_DoesNotStartBehind()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        if (PoseIntoAFillet(layout) is not { } pose)
        {
            Assert.Fail("no fillet arc at SFO runs past the end of the straight taxi edge nearest it");
            return;
        }

        (GroundNode arcEntry, GroundNode arcExit, LatLon position, TrueHeading heading) = pose;
        output.WriteLine($"{IntoFilletFt:F0} ft into the fillet {arcEntry.Id}-{arcExit.Id}, heading {heading.Degrees:F0}");

        // The TAXI command's own start resolution: the heading-biased pick, else the nearest node.
        GroundNode? headingBiased = layout.FindNearestNodeForTaxi(position, heading);
        GroundNode? start = headingBiased ?? layout.FindNearestNode(position);
        output.WriteLine($"heading-biased start: {(headingBiased is null ? "none" : $"node {headingBiased.Id}")}");
        Assert.NotNull(start);
        double startFt = GeoMath.DistanceNm(position, start.Position) * GeoMath.FeetPerNm;
        double offHeadingDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, start.Position), heading.Degrees);
        output.WriteLine($"taxi start node {start.Id}: {startFt:F0} ft away, {offHeadingDeg:F0} deg off the heading");
        Assert.True(
            (startFt <= AirportGroundLayout.AtNodeToleranceFt) || (offHeadingDeg < 90.0),
            $"the taxi starts at node {start.Id}, {startFt:F0} ft away and {offHeadingDeg:F0} deg off the heading: behind the aircraft"
        );
        Assert.True(start == arcExit, $"the taxi starts at node {start.Id}, not at the fillet's end ahead, node {arcExit.Id}");
    }

    /// <summary>
    /// Spawns a B738 stopped half-way along SFO's longest straight B edge, on its centreline, facing along it. Half that
    /// edge is longer than the free-space bound, so the B node ahead is out of a free-space leg's reach.
    /// </summary>
    private AircraftState SpawnMidLongestB(SfoGround ground, out double startHeadingDeg)
    {
        GroundEdge longestB = LongestStraightB(ground.Layout);
        GroundNode from = longestB.Nodes[0];
        GroundNode to = longestB.Nodes[1];
        double halfFt = longestB.DistanceNm * GeoMath.FeetPerNm / 2.0;
        output.WriteLine($"longest B edge {from.Id}-{to.Id}, {longestB.DistanceNm * GeoMath.FeetPerNm:F0} ft");
        Assert.True(
            halfFt > RampLaneReposition.MaxCrossingFt,
            $"half the longest B edge is {halfFt:F0} ft, inside the {RampLaneReposition.MaxCrossingFt:F0} ft free-space bound"
        );

        startHeadingDeg = GeoMath.BearingTo(from.Position, to.Position);
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "DAL880",
            "B738",
            (from, new TrueHeading(startHeadingDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.Position = Along(from.Position, to.Position, 0.5);
        aircraft.TrueTrack = new TrueHeading(startHeadingDeg);
        aircraft.Ground.CurrentTaxiway = "B";
        return aircraft;
    }

    /// <summary>SFO's longest straight B edge (no ramp connector, no runway centreline).</summary>
    private static GroundEdge LongestStraightB(AirportGroundLayout layout)
    {
        GroundEdge? longest = layout.Edges.Where(e => e.MatchesTaxiway("B") && !e.IsRamp && !e.IsRunwayCenterline).MaxBy(e => e.DistanceNm);
        Assert.NotNull(longest);
        return longest;
    }

    /// <summary>
    /// KOAK's long straight C edge west of H: its end at the C/H tangent cut, and its far end away from B.
    /// </summary>
    private (GroundNode TangentCut, GroundNode FarEnd) LongCEdgeWestOfH(AirportGroundLayout layout)
    {
        GroundNode? junctionCH = layout.FindIntersectionNode("C", "H");
        GroundNode? junctionCB = layout.FindIntersectionNode("C", "B");
        Assert.NotNull(junctionCH);
        Assert.NotNull(junctionCB);

        double awayFromB = (GeoMath.BearingTo(junctionCH.Position, junctionCB.Position) + 180.0) % 360.0;
        GroundNode tangentCut = NextAlong(junctionCH, "C", awayFromB);
        GroundNode farEnd = NextAlong(tangentCut, "C", GeoMath.BearingTo(junctionCH.Position, tangentCut.Position));
        double edgeFt = GeoMath.DistanceNm(tangentCut.Position, farEnd.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"C/H {junctionCH.Id}, C/B {junctionCB.Id}, tangent cut {tangentCut.Id}, far end {farEnd.Id}, edge {edgeFt:F0} ft");
        Assert.True(edgeFt > MinLongEdgeFt, $"C edge {tangentCut.Id}-{farEnd.Id} is {edgeFt:F0} ft, expected a long edge");
        return (tangentCut, farEnd);
    }

    /// <summary>
    /// Walks C on from <paramref name="start"/>, away from <paramref name="cameFrom"/>, to the first node with an edge on
    /// another taxiway (no ramp connector, no runway centreline), and returns that taxiway's name.
    /// </summary>
    private static string FirstTaxiwayOffCBeyond(GroundNode start, GroundNode cameFrom)
    {
        GroundNode previous = cameFrom;
        GroundNode current = start;
        for (int i = 0; i < MaxWalkNodes; i++)
        {
            if (OtherTaxiwayAt(current, "C") is { } name)
            {
                return name;
            }

            GroundNode next = NextAlong(current, "C", GeoMath.BearingTo(previous.Position, current.Position));
            Assert.True(next != previous, $"C ends at node {current.Id} with no other taxiway off it");
            previous = current;
            current = next;
        }

        Assert.Fail($"no taxiway off C within {MaxWalkNodes} nodes of node {start.Id}");
        return "";
    }

    private static string? OtherTaxiwayAt(GroundNode node, string taxiway)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRamp || edge.IsRunwayCenterline)
            {
                continue;
            }

            string[] names = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
            string? other = names.FirstOrDefault(n => !string.Equals(n, taxiway, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>
    /// The first (by node id) fillet arc, entered from either end, where an aircraft <see cref="IntoFilletFt"/> into the arc
    /// is past the end of the straight taxi edge nearest it: that edge's nearest point clamps to an end node, and both of
    /// its ends lie behind the aircraft.
    /// </summary>
    private static (GroundNode ArcEntry, GroundNode ArcExit, LatLon Position, TrueHeading Heading)? PoseIntoAFillet(AirportGroundLayout layout)
    {
        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if ((edge is not GroundArc arc) || arc.IsRamp)
                {
                    continue;
                }

                CubicBezier curve = arc.ToBezier();
                if (arc.Nodes[1] == node)
                {
                    curve = new CubicBezier(curve.P3Lat, curve.P3Lon, curve.P2Lat, curve.P2Lon, curve.P1Lat, curve.P1Lon, curve.P0Lat, curve.P0Lon);
                }

                if (IntoFilletPose(layout, node, arc, curve) is { } pose)
                {
                    return pose;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The pose <see cref="IntoFilletFt"/> along <paramref name="curve"/> (the arc as entered at <paramref name="arcEntry"/>),
    /// heading along its tangent, or null when the arc does not fit the case: too short or too long, a node within the
    /// at-node tolerance, the nearest straight taxi edge beyond the on-taxiway offset, its nearest point inside the edge, or
    /// an end of it ahead of the aircraft.
    /// </summary>
    private static (GroundNode ArcEntry, GroundNode ArcExit, LatLon Position, TrueHeading Heading)? IntoFilletPose(
        AirportGroundLayout layout,
        GroundNode arcEntry,
        GroundArc arc,
        CubicBezier curve
    )
    {
        double arcFt = arc.DistanceNm * GeoMath.FeetPerNm;
        if ((arcFt < MinFilletFt) || (arcFt > MaxFilletFt))
        {
            return null;
        }

        double t = IntoFilletFt / arcFt;
        (double lat, double lon) = curve.Evaluate(t);
        var position = new LatLon(lat, lon);
        var heading = new TrueHeading(curve.TangentBearing(t));
        if (
            layout.Nodes.Values.Any(n =>
                (n.Edges.Count > 0) && ((GeoMath.DistanceNm(position, n.Position) * GeoMath.FeetPerNm) <= AirportGroundLayout.AtNodeToleranceFt)
            )
        )
        {
            return null;
        }

        if (
            (layout.FindNearestTaxiEdge(position) is not { } nearest)
            || ((nearest.DistNm * GeoMath.FeetPerNm) > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt)
        )
        {
            return null;
        }

        GroundEdge straight = nearest.Edge;
        (LatLon _, double _, bool clamped) = GeoMath.FootOfPerpendicular(position, straight.Nodes[0].Position, straight.Nodes[1].Position);
        bool bothEndsBehind = straight.Nodes.All(n => GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, n.Position), heading.Degrees) >= 90.0);
        return (clamped && bothEndsBehind) ? (arcEntry, arc.OtherNode(arcEntry), position, heading) : null;
    }

    private AirportGroundLayout? LoadLayout(string airportId)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout(airportId);
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, LatLon position, double headingDeg, string airportId, string taxiway)
    {
        var aircraft = new AircraftState
        {
            Callsign = "DAL880",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = airportId, Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = taxiway;
        return aircraft;
    }

    private TaxiRoute Resolve(AirportGroundLayout layout, AircraftState aircraft, string command)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(parsed.Value);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        for (int i = 0; i < Math.Min(4, route.Segments.Count); i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            output.WriteLine($"  [{i}] {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        return route;
    }

    private static TaxiRouteSegment FirstLayoutSegment(TaxiRoute route) => route.Segments.First(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge));

    private static bool OnStraightEdgeOf(GroundNode node, string taxiway) => node.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(taxiway));

    private static LatLon Along(LatLon from, LatLon to, double fraction) =>
        new(from.Lat + ((to.Lat - from.Lat) * fraction), from.Lon + ((to.Lon - from.Lon) * fraction));

    private static GroundNode NextAlong(GroundNode node, string taxiway, double bearingDeg)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;
        foreach (IGroundEdge edge in node.Edges)
        {
            if ((edge is not GroundEdge) || !edge.MatchesTaxiway(taxiway))
            {
                continue;
            }

            GroundNode other = edge.OtherNode(node);
            double diff = GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, other.Position), bearingDeg);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = other;
            }
        }

        Assert.NotNull(best);
        return best;
    }
}
