using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>
/// Issue #880: an aircraft resting mid-way along a long straight taxiway starts its taxi on that taxiway, at the edge's
/// endpoint ahead, or turns about to the endpoint behind when the route from the one ahead would come back over the edge.
/// The overlay reconstructs the route client-side from the taxiway the aircraft is on, else it draws the route from the
/// nearest node, which at KOAK mid-C west of H is on the parallel D; it draws exactly the turn about the simulation sends
/// (<see cref="AircraftModel.TaxiTurnAboutShape"/>, toward <see cref="AircraftModel.TaxiTurnAboutTargetNodeId"/>), and
/// never decides or re-infers one itself.
/// </summary>
public class GroundViewModelMidEdgeStartOverlayTests
{
    private const double MinLongEdgeFt = 1000.0;

    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    [Fact]
    public void MidC_FacingH_OverlayStartsOnC()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "B738", "C B", "28R", TaxiTurnAboutShape.None, null));

        Assert.NotNull(drawn);
        TaxiRouteSegment firstOnGraph = drawn!.Segments.First(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge));
        Assert.True(
            firstOnGraph.Edge.Edge.MatchesTaxiway("C"),
            $"the overlay joins the graph on {firstOnGraph.TaxiwayName} ({firstOnGraph.FromNodeId}->{firstOnGraph.ToNodeId}), not C"
        );
        Assert.DoesNotContain(drawn.Segments, s => s.Edge.Edge.MatchesTaxiway("D"));
    }

    /// <summary>
    /// A C172 mid-C facing H, cleared via C J: J branches off C behind it, so the route from the C node ahead would come
    /// straight back over the edge it stands on. The simulation turns it about to the edge's far node and sends
    /// <see cref="TaxiTurnAboutShape.FromFarEnd"/> toward it, and the overlay draws the same: the free-space leg back to that
    /// node, and no segment on the edge.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_OverlayTurnsAboutToTheFarNode()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "C172", "C J", "", TaxiTurnAboutShape.FromFarEnd, farEnd.Id));

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(drawn.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));
    }

    /// <summary>
    /// The same C172 pose and clearance with no turn about pending (the simulation did not turn it about, or it has
    /// finished the leg): the overlay never decides a turn about itself, so it draws no leg back to the far node.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_NoTurnAboutPending_OverlayDrawsNoTurnAbout()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "C172", "C J", "", TaxiTurnAboutShape.None, null));

        Assert.DoesNotContain(drawn?.Segments ?? [], s => VirtualNode.IsVirtualEdge(s.Edge.Edge) && (s.ToNodeId == farEnd.Id));
    }

    /// <summary>
    /// A B738 at the same spot angled 45° across C: a jet not lined up with its taxiway turns about like any other
    /// aircraft in the sim, which sends <see cref="TaxiTurnAboutShape.FromFarEnd"/>, so the overlay draws the same turn about
    /// to the far node.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_AngledJetOverlayTurnsAboutToTheFarNode()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        AircraftModel jet = MidCFacingH(tangentCut, farEnd, "B738", "C J", "", TaxiTurnAboutShape.FromFarEnd, farEnd.Id);
        jet.Heading = new TrueHeading(jet.Heading.Degrees + AcrossEdgeDeg);

        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));

        TaxiRoute? drawn = vm.ResolveRemainingRoute(jet);

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(drawn.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));
    }

    /// <summary>
    /// A B738 lined up along C at the same spot, sent <see cref="TaxiTurnAboutShape.InPlace"/>: the simulation turns a
    /// lined-up jet about only on a clearance no controller issued, and then keeps the route from the C node ahead, whose
    /// first segment reverses over the C edge to the far node. The overlay draws that route, not a leg back from the far node.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_LinedUpJetOverlayKeepsTheRouteAheadReversingOverC()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "B738", "C J", "", TaxiTurnAboutShape.InPlace, farEnd.Id));

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.True(ReferenceEquals(first.Edge.Edge, occupied), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(tangentCut.Id, first.FromNodeId);
        Assert.Equal(farEnd.Id, first.ToNodeId);
    }

    /// <summary>How far the angled jet's heading is turned off C, past the alignment within which a jet refuses to turn about.</summary>
    private const double AcrossEdgeDeg = 45.0;

    /// <summary>
    /// A C172 mid-C facing H, cleared via C B to 28R: the route from the C node ahead already leaves the edge it stands on.
    /// A turn about from the far end is still sent, so the overlay draws the route from the far node it is sent, not the
    /// route ahead it would infer from the pose.
    /// </summary>
    [Fact]
    public void MidC_RouteAheadLeavesTheEdge_FromFarEndSent_OverlayDrawsFromTheTarget()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "C172", "C B", "28R", TaxiTurnAboutShape.FromFarEnd, farEnd.Id));

        Assert.NotNull(drawn);
        TaxiRouteSegment firstOnGraph = drawn!.Segments.First(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge));
        Assert.Equal(farEnd.Id, firstOnGraph.FromNodeId);
    }

    /// <summary>
    /// The C172 turned about on C from the far end, its heading rotated 120° from facing H, past 90° toward the far node:
    /// the start its heading now picks is no longer the C node ahead, yet the overlay still draws the route from the far
    /// node it is sent, and no segment on the edge it stands on.
    /// </summary>
    [Fact]
    public void MidC_FromFarEnd_HeadingRotatedTowardTheFarNode_OverlayStillDrawsFromTheTarget()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        AircraftModel turning = MidCFacingH(tangentCut, farEnd, "C172", "C J", "", TaxiTurnAboutShape.FromFarEnd, farEnd.Id);
        turning.Heading = new TrueHeading(turning.Heading.Degrees + RotatedTowardFarEndDeg);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(turning);

        Assert.NotNull(drawn);
        TaxiRouteSegment firstOnGraph = drawn!.Segments.First(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge));
        Assert.Equal(farEnd.Id, firstOnGraph.FromNodeId);
        Assert.DoesNotContain(drawn.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));
    }

    /// <summary>How far the turning aircraft's heading has rotated from facing H: past 90°, toward the far node.</summary>
    private const double RotatedTowardFarEndDeg = 120.0;

    /// <summary>
    /// A B738 lined up along C facing H, sent a turn about from the far end: the simulation sends that shape to a lined-up
    /// jet when a scenario preset's route resolves from no node ahead (the M2 pose at SFO). The overlay draws the route from
    /// the far node it is sent, not the route from the C node ahead its lined-up heading would suggest.
    /// </summary>
    [Fact]
    public void MidC_LinedUpJet_FromFarEndSent_OverlayDrawsTheFarEndRoute()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "B738", "C J", "", TaxiTurnAboutShape.FromFarEnd, farEnd.Id));

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(drawn.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));
    }

    /// <summary>
    /// A C172 mid-way along a straight KOAK taxi edge whose end behind it is a runway holding position, cleared along a
    /// route drawn through that holding position: the simulation takes the route from the holding position and sends a
    /// turn about from the far end, though no free-space leg is driven up to a holding position. The overlay draws the
    /// route from that node, with no leg to it, and does not fall back to the route from the node ahead.
    /// </summary>
    [Fact]
    public void RunwayHoldShortStub_FromFarEndSent_OverlayDrawsFromTheTargetWithNoLeg()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode ahead, GroundNode holdShort, GroundEdge stub) =
            FirstStubToARunwayHoldShort(layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends at a runway holding position");
        GroundNode beyond = holdShort.Edges.First(e => !ReferenceEquals(e, stub)).OtherNode(holdShort);
        AircraftModel aircraft = SimulatedTaxi(
            layout,
            Along(holdShort.Position, ahead.Position, 0.5),
            new TrueHeading(GeoMath.BearingTo(holdShort.Position, ahead.Position)),
            ("C172", stub.TaxiwayName),
            $"TAXI #{holdShort.Id} #{beyond.Id}"
        );
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.TaxiTurnAboutShape);
        Assert.Equal(holdShort.Id, aircraft.TaxiTurnAboutTargetNodeId);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(aircraft);

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.False(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is a free-space leg {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(holdShort.Id, first.FromNodeId);
    }

    /// <summary>
    /// The same C172 after it has turned about and crossed the holding position: with no leg back, segment 0 runs from the
    /// holding position to the node beyond it, and the simulation sends no turn about once the aircraft has reached the
    /// holding position. The overlay draws nothing behind the aircraft: no segment from the holding position, and none on
    /// the edge it left.
    /// </summary>
    [Fact]
    public void RunwayHoldShortStub_PastTheTargetOnSegment0_OverlayDrawsNothingBehindTheAircraft()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode ahead, GroundNode holdShort, GroundEdge stub) =
            FirstStubToARunwayHoldShort(layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends at a runway holding position");
        GroundNode beyond = holdShort.Edges.First(e => !ReferenceEquals(e, stub)).OtherNode(holdShort);
        AircraftModel aircraft = SimulatedTaxi(
            layout,
            Along(holdShort.Position, ahead.Position, 0.5),
            new TrueHeading(GeoMath.BearingTo(holdShort.Position, ahead.Position)),
            ("C172", stub.TaxiwayName),
            $"TAXI #{holdShort.Id} #{beyond.Id}"
        );
        aircraft.Position = Along(holdShort.Position, beyond.Position, 0.5);
        aircraft.Heading = new TrueHeading(GeoMath.BearingTo(holdShort.Position, beyond.Position));
        aircraft.TaxiTurnAboutShape = TaxiTurnAboutShape.None;
        aircraft.TaxiTurnAboutTargetNodeId = null;
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(aircraft);

        Assert.DoesNotContain(drawn?.Segments ?? [], s => s.FromNodeId == holdShort.Id);
        Assert.DoesNotContain(drawn?.Segments ?? [], s => ReferenceEquals(s.Edge.Edge, stub));
    }

    /// <summary>
    /// The B738 sent an in-place turn about on C, its heading rotated 120° mid-turn from facing H: the overlay reads the
    /// node the route reverses from off the sent target, not off the heading, so it draws the same route as before the
    /// turn began.
    /// </summary>
    [Fact]
    public void MidC_InPlace_HeadingRotatedMidTurn_OverlayDrawIsUnchanged()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        AircraftModel jet = MidCFacingH(tangentCut, farEnd, "B738", "C J", "", TaxiTurnAboutShape.InPlace, farEnd.Id);
        TaxiRoute? beforeTurn = vm.ResolveRemainingRoute(jet);
        jet.Heading = new TrueHeading(jet.Heading.Degrees + RotatedTowardFarEndDeg);

        TaxiRoute? midTurn = vm.ResolveRemainingRoute(jet);

        Assert.NotNull(beforeTurn);
        Assert.NotNull(midTurn);
        Assert.Equal(GraphLegs(beforeTurn!), GraphLegs(midTurn!));
        Assert.Equal(tangentCut.Id, midTurn!.Segments[0].FromNodeId);
    }

    /// <summary>
    /// The C172 sent a turn about from the far end toward a node the layout lacks: the overlay cannot draw the sent shape,
    /// so it draws what it draws with no turn about sent.
    /// </summary>
    [Fact]
    public void MidC_FromFarEnd_TargetMissingFromTheLayout_OverlayDrawsAsNoTurnAbout()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        Assert.False(layout.Nodes.ContainsKey(MissingNodeId));
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? none = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "C172", "C J", "", TaxiTurnAboutShape.None, null));
        TaxiRoute? missing = vm.ResolveRemainingRoute(
            MidCFacingH(tangentCut, farEnd, "C172", "C J", "", TaxiTurnAboutShape.FromFarEnd, MissingNodeId)
        );

        Assert.Equal(none is null ? [] : GraphLegs(none), missing is null ? [] : GraphLegs(missing));
        Assert.DoesNotContain(missing?.Segments ?? [], s => VirtualNode.IsVirtualEdge(s.Edge.Edge) && (s.ToNodeId == farEnd.Id));
    }

    /// <summary>A node id no layout holds.</summary>
    private const int MissingNodeId = int.MaxValue;

    /// <summary>The layout-edge legs of <paramref name="route"/>, from node to node: its free-space legs carry fresh node ids each draw.</summary>
    private static List<(int From, int To)> GraphLegs(TaxiRoute route) =>
        [.. route.Segments.Where(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge)).Select(s => (s.FromNodeId, s.ToNodeId))];

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
    /// The aircraft the server would broadcast after the simulation dispatched <paramref name="command"/> to a stopped
    /// aircraft of <paramref name="typeAndTaxiway"/>'s type on its taxiway at <paramref name="position"/>: the taxiway
    /// sequence, destination and pending turn about of the route it assigned.
    /// </summary>
    private static AircraftModel SimulatedTaxi(
        AirportGroundLayout layout,
        LatLon position,
        TrueHeading heading,
        (string Type, string Taxiway) typeAndTaxiway,
        string command
    )
    {
        AircraftState aircraft = StoppedAircraft(layout, position, heading, typeAndTaxiway);
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.Dispatch(Assert.IsType<TaxiCommand>(parsed.Value), aircraft, TaxiDispatchContext(layout, aircraft));
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"'{command}' left no route");

        return new AircraftModel
        {
            Callsign = aircraft.Callsign,
            AircraftType = aircraft.AircraftType,
            Position = position,
            Heading = heading,
            CurrentTaxiway = typeAndTaxiway.Taxiway,
            TaxiRoute = route.FormatTaxiwaySequence(),
            TaxiDestination = route.DestinationParking is { } parking ? "@" + parking : "",
            AssignedRunway = "",
            HasActiveTaxiRoute = true,
            TaxiTurnAboutShape = aircraft.Ground.TaxiTurnAboutShape,
            TaxiTurnAboutTargetNodeId = aircraft.Ground.TaxiTurnAboutTargetNodeId,
            IsOnGround = true,
        };
    }

    /// <summary>A stopped aircraft of <paramref name="typeAndTaxiway"/>'s type holding in position on its taxiway.</summary>
    private static AircraftState StoppedAircraft(
        AirportGroundLayout layout,
        LatLon position,
        TrueHeading heading,
        (string Type, string Taxiway) typeAndTaxiway
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = "DAL880",
            AircraftType = typeAndTaxiway.Type,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = layout.AirportId, Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(
            new PhaseContext
            {
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                DeltaSeconds = 0,
                GroundLayout = layout,
                Logger = NullLogger.Instance,
            }
        );
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = typeAndTaxiway.Taxiway;
        return aircraft;
    }

    /// <summary>The dispatch of a controller's TAXI to <paramref name="aircraft"/>.</summary>
    private static DispatchContext TaxiDispatchContext(AirportGroundLayout layout, AircraftState aircraft) =>
        new()
        {
            GroundLayout = layout,
            Rng = new Random(1),
            Weather = null,
            FindAircraft = null,
            ListAircraft = () => [aircraft],
            ValidateDctFixes = false,
            AutoCrossRunway = false,
            SoloTrainingMode = false,
            SoloRpoCommandsAllowed = false,
            RpoShowPilotSpeech = false,
            TerminalEmitter = null,
            ArtccConfig = null,
            ScenarioElapsedSeconds = 0,
            SessionStartUtc = DateTime.UnixEpoch,
            PreserveConditionals = false,
            IsScenarioScripted = false,
            FacilityHint = null,
        };

    private static LatLon Along(LatLon from, LatLon to, double fraction) =>
        new(from.Lat + ((to.Lat - from.Lat) * fraction), from.Lon + ((to.Lon - from.Lon) * fraction));

    /// <summary>
    /// A stopped <paramref name="type"/> 35% of the way along C from the tangent cut to the far end, facing the tangent cut,
    /// with the turn-about shape and target the simulation sent.
    /// </summary>
    private static AircraftModel MidCFacingH(
        GroundNode tangentCut,
        GroundNode farEnd,
        string type,
        string taxiRoute,
        string runway,
        TaxiTurnAboutShape turnAboutShape,
        int? turnAboutTargetNodeId
    ) =>
        new()
        {
            Callsign = "DAL880",
            AircraftType = type,
            Position = Along(tangentCut.Position, farEnd.Position, 0.35),
            Heading = new TrueHeading(GeoMath.BearingTo(farEnd.Position, tangentCut.Position)),
            CurrentTaxiway = "C",
            TaxiRoute = taxiRoute,
            AssignedRunway = runway,
            HasActiveTaxiRoute = true,
            TaxiTurnAboutShape = turnAboutShape,
            TaxiTurnAboutTargetNodeId = turnAboutTargetNodeId,
            IsOnGround = true,
        };

    /// <summary>The long straight C edge west of the C/H junction, away from C/B: its node nearer H, then its far node.</summary>
    private static (GroundNode TangentCut, GroundNode FarEnd) LongCEdgeWestOfH(AirportGroundLayout layout)
    {
        GroundNode? junctionCH = layout.FindIntersectionNode("C", "H");
        GroundNode? junctionCB = layout.FindIntersectionNode("C", "B");
        Assert.NotNull(junctionCH);
        Assert.NotNull(junctionCB);

        double awayFromB = (GeoMath.BearingTo(junctionCH.Position, junctionCB.Position) + 180.0) % 360.0;
        GroundNode tangentCut = NextAlongC(junctionCH, awayFromB);
        GroundNode farEnd = NextAlongC(tangentCut, GeoMath.BearingTo(junctionCH.Position, tangentCut.Position));
        double edgeFt = GeoMath.DistanceNm(tangentCut.Position, farEnd.Position) * GeoMath.FeetPerNm;
        Assert.True(edgeFt > MinLongEdgeFt, $"C edge {tangentCut.Id}-{farEnd.Id} is {edgeFt:F0} ft, expected a long edge");
        return (tangentCut, farEnd);
    }

    private static GroundNode NextAlongC(GroundNode node, double bearingDeg)
    {
        GroundNode? best = node
            .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C"))
            .Select(e => e.OtherNode(node))
            .MinBy(other => GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, other.Position), bearingDeg));
        Assert.NotNull(best);
        return best;
    }
}
