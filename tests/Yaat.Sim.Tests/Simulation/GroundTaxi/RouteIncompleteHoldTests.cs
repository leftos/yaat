using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The end of a route that could not reach its destination as cleared (issue #461): where it may stop, what it carries
/// through a snapshot, and which starts it never applies to.
/// </summary>
public class RouteIncompleteHoldTests(ITestOutputHelper output)
{
    /// <summary>
    /// SFO <c>TAXI A2 @B1</c> from the A1/A2 junction: B1 needs M1, and A2 becomes M1 on the runway 01L threshold (node
    /// 420), so the first node on M1 is runway pavement. The route ends at A2's 01L bar instead, holding short of the
    /// runway, with the note naming M1.
    /// </summary>
    [Fact]
    public void RouteIncompleteHold_NeverOnRunway_Sfo420() =>
        AssertHoldsAtNearSideRunwayBar(("A1", "A2"), "TAXI A2 @B1", "A2", "Holding short of M1: route to @B1 needs M1, not in clearance");

    /// <summary>
    /// SFO <c>TAXI F RWY 28R</c> from the A/F junction: 28R needs C, and F becomes C at the 28L threshold (node 299). The
    /// route crosses 01L and 01R and ends at F's 28L bar, holding short of the runway.
    /// </summary>
    [Fact]
    public void RouteIncompleteHold_NeverOnRunway_Sfo299() =>
        AssertHoldsAtNearSideRunwayBar(("A", "F"), "TAXI F RWY 28R", "F", "Holding short of C: route to RWY 28R needs C, not in clearance");

    /// <summary>
    /// OAK GA16 sits 127 ft from F, but an aircraft on a stand occupies no taxiway: F is not read back or routed as the
    /// taxiway it stands on. <c>TAXI C</c> from the stand needs F to reach C, so it is refused naming F.
    /// </summary>
    [Fact]
    public void TaxiFromStandNearTaxiway_DoesNotOccupyIt_OakGA16()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        GroundNode stand = Assert.IsType<GroundNode>(layout.FindParkingByName("GA16"));
        AircraftState aircraft = MakeParked(layout, stand);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand(Path: ["C"], HoldShorts: []), layout);
        output.WriteLine($"TAXI C from GA16: {result.Success} — {result.Message}");

        Assert.DoesNotContain("Taxi via F", result.Message ?? "");
        Assert.False(result.Success, result.Message);
        Assert.StartsWith("Unable, route to C needs F, not in clearance", result.Message);
    }

    /// <summary>A route ending held short of a missing taxiway keeps that hold's reason through a snapshot.</summary>
    [Fact]
    public void TaxiRoute_RoundTrip_PreservesRouteIncompleteHold()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        var route = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 999,
                    Reason = HoldShortReason.RouteIncomplete,
                    TargetName = "C",
                },
            ],
            CurrentSegmentIndex = 0,
        };

        TaxiRoute restored = Assert.IsType<TaxiRoute>(TaxiRoute.FromSnapshot(route.ToSnapshot(), layout));

        HoldShortPoint hold = Assert.Single(restored.HoldShortPoints);
        Assert.Equal(HoldShortReason.RouteIncomplete, hold.Reason);
        Assert.Equal("C", hold.TargetName);
    }

    /// <summary>A holding-short phase at the end of an incomplete route restores as one, still refusing RES and CROSS.</summary>
    [Fact]
    public void HoldingShortPhase_RoundTrip_PreservesRouteIncompleteHold()
    {
        var phase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 5,
                Reason = HoldShortReason.RouteIncomplete,
                TargetName = "C",
            }
        );

        var restored = HoldingShortPhase.FromSnapshot((HoldingShortPhaseDto)phase.ToSnapshot());

        Assert.Equal(HoldShortReason.RouteIncomplete, restored.HoldShort.Reason);
        Assert.Equal("C", restored.HoldShort.TargetName);
        Assert.True(restored.CanAcceptCommand(CanonicalCommandType.Resume).IsRejected);
        Assert.True(restored.CanAcceptCommand(CanonicalCommandType.CrossRunway).IsRejected);
    }

    /// <summary>
    /// The implied lead-in boundary on real pavement: the OAK <c>TAXI G @SIG1</c> route drives D (about 635 ft) and then
    /// only apron. The real D edges beyond the stand's lead-off are added to that run one at a time: while the run on D is at most
    /// <see cref="SegmentExpander.MaxImpliedLeadInFt"/> D is implied; the first edge that takes it over is not.
    /// </summary>
    [Fact]
    public void ImpliedLeadIn_JustOverMaxRun_IsNotImplied_OakSig1ViaD()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        AircraftState aircraft = MakeAt(layout, layout.Nodes[508], new TrueHeading(0.0));
        CommandResult result = GroundCommandHandler.TryTaxi(
            aircraft,
            new TaxiCommand(Path: ["G"], HoldShorts: [], DestinationParking: "SIG1"),
            layout
        );
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int firstD = route.Segments.FindIndex(s => s.Edge.Edge.MatchesTaxiway("D"));
        Assert.True(firstD >= 0, route.FormatTaxiwaySequence());
        List<IGroundEdge> tail = [.. route.Segments.Skip(firstD).Select(s => s.Edge.Edge)];
        double runFt = RunOnDFt(tail);
        Assert.Equal("D", Implied(tail));

        TaxiRouteSegment lastStraightD = route.Segments.Last(s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("D"));
        GroundNode head = layout.Nodes[lastStraightD.ToNodeId];
        var used = new HashSet<int> { lastStraightD.FromNodeId, head.Id };
        while (runFt <= SegmentExpander.MaxImpliedLeadInFt)
        {
            Assert.Equal("D", Implied(tail));
            IGroundEdge back = Assert.Single(
                head.Edges.Where(e => (e is not GroundArc) && e.MatchesTaxiway("D") && !used.Contains(e.OtherNode(head).Id)).Take(1)
            );
            head = back.OtherNode(head);
            used.Add(head.Id);
            tail.Insert(0, back);
            runFt = RunOnDFt(tail);
        }

        output.WriteLine($"run on D {runFt:F0} ft (last edge {tail[0].DistanceNm * GeoMath.FeetPerNm:F0} ft)");
        Assert.Null(Implied(tail));
    }

    private static string? Implied(List<IGroundEdge> edges) => SegmentExpander.ImpliedDestinationLeadIn(edges, "SIG1", null, n => n == "D");

    private static double RunOnDFt(List<IGroundEdge> edges) =>
        edges.Where(e => SegmentExpander.EdgeNames(e).Contains("D")).Sum(e => e.DistanceNm) * GeoMath.FeetPerNm;

    private void AssertHoldsAtNearSideRunwayBar((string Along, string Onto) junction, string command, string runwayTaxiway, string note)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "N461RW", "A320", junction.Along, junction.Onto);
        aircraft.Ground.CurrentTaxiway = junction.Along;
        CommandResult result = ground.Engine.SendCommand("N461RW", command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);

        int endNode = route.Segments[^1].ToNodeId;
        HoldShortPoint endHold = Assert.Single(route.HoldShortPoints, h => h.NodeId == endNode);
        Assert.Equal(HoldShortReason.RunwayCrossing, endHold.Reason);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Contains(note, route.Warnings);
        Assert.DoesNotContain(ground.Layout.Nodes[endNode].Edges, e => e.IsRunwayCenterline);

        HoldingShortPhase hold = TickToHoldAtRouteEnd(ground, aircraft, endNode);
        Assert.Equal(HoldShortReason.RunwayCrossing, hold.HoldShort.Reason);
        Assert.True(hold.CanAcceptCommand(CanonicalCommandType.FollowGround).IsAllowed, "a runway bar arms FOLLOWG behind the hold");

        var runway = RunwayIdentifier.Parse(Assert.IsType<string>(endHold.TargetName));
        GroundNode bar = ground.Layout.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.RunwayHoldShort)
            && (n.RunwayId is { } id)
            && id.Overlaps(runway)
            && n.Edges.Any(e => e.MatchesTaxiway(runwayTaxiway))
            && (GeoMath.DistanceNm(n.Position, ground.Layout.Nodes[endNode].Position) < 0.05)
        );
        GroundNode runwayNode = ground
            .Layout.Nodes.Values.Where(n => n.Edges.Any(e => e.IsRunwayCenterline) && n.Edges.Any(e => e.MatchesTaxiway(runwayTaxiway)))
            .MinBy(n => GeoMath.DistanceNm(n.Position, bar.Position))!;
        double aircraftFt = GeoMath.DistanceNm(aircraft.Position, runwayNode.Position) * GeoMath.FeetPerNm;
        double barFt = GeoMath.DistanceNm(bar.Position, runwayNode.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"stopped {aircraftFt:F0} ft from runway node {runwayNode.Id}; bar {bar.Id} is {barFt:F0} ft from it");
        Assert.True(aircraftFt >= barFt - 5.0, $"stopped {aircraftFt:F0} ft from the runway, inside its bar ({barFt:F0} ft)");
    }

    /// <summary>Ticks until the aircraft holds at <paramref name="endNode"/>, clearing each runway crossing it holds at on the way.</summary>
    private static HoldingShortPhase TickToHoldAtRouteEnd(SfoGround ground, AircraftState aircraft, int endNode)
    {
        for (int second = 0; second < 900; second++)
        {
            ground.Engine.TickOneSecond();
            if ((aircraft.Phases?.CurrentPhase is not HoldingShortPhase hold) || (aircraft.GroundSpeed >= SfoGroundHarness.StationarySpeedKts))
            {
                continue;
            }

            if (hold.HoldShort.NodeId == endNode)
            {
                return hold;
            }

            string runway = Assert.IsType<string>(hold.HoldShort.TargetName).Split('/')[0];
            CommandResult cross = ground.Engine.SendCommand(aircraft.Callsign, $"CROSS {runway}");
            Assert.True(cross.Success, cross.Message);
        }

        throw new Xunit.Sdk.XunitException($"{aircraft.Callsign} never held at node {endNode}");
    }

    private static AircraftState MakeParked(AirportGroundLayout layout, GroundNode stand)
    {
        AircraftState aircraft = MakeAt(layout, stand, stand.TrueHeading ?? new TrueHeading(0));
        aircraft.Phases!.Clear(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.ParkingSpot = stand.Name;
        return aircraft;
    }

    private static AircraftState MakeAt(AirportGroundLayout layout, GroundNode node, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N461RI",
            AircraftType = "A320",
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
