using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A TAXI clearance is resolved as cleared from where the aircraft stands; the taxiway it is on is prepended only when
/// the cleared path cannot start there, and a taxiway it holds short of counts as where it stands (S1-SFO-2 bundle).
/// Prepending the occupied taxiway unconditionally forced the route's entry onto the first cleared taxiway through the
/// junction the two share: THY9WC's re-issued <c>TAXI A F 28L HS 1L</c> became <c>B A F</c>, entered A southbound and
/// U-turned across 01L/19R (#457); SKW3398 holding short of K on B, cleared <c>TAXI A @D1</c>, joined A across the
/// field instead of via K (#455).
/// </summary>
public class SfoTaxiCurrentTaxiwayPrependTests(ITestOutputHelper output)
{
    /// <summary>THY9WC at bundle t=180..195: pushed off G10 onto B, nosed 225°, stationary.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) Thy9wcPose = (
        new LatLon(37.620185907173685, -122.39339233256204),
        new TrueHeading(225.07996254948034)
    );

    /// <summary>SKW3398 at bundle t=530: taxiing north-west on B towards K, nosed 298°.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) Skw3398OnBPose = (
        new LatLon(37.62101418273054, -122.38350328658599),
        new TrueHeading(297.93675266880064)
    );

    /// <summary>The K/B junction centre on B, the node a hold-short of K on B protects.</summary>
    private const int KOnBJunctionNode = 135;

    /// <summary>SKW5416 (CRJ7) at bundle t=1221: stationary on E, nosed 228°, about to be cleared <c>TAXI B</c>.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) Skw5416OnEPose = (
        new LatLon(37.61954385945439, -122.37947519487116),
        new TrueHeading(228.1)
    );

    /// <summary>The F/A junction centre on F, the node a hold-short of A on F protects.</summary>
    private const int AOnFJunctionNode = 54;

    /// <summary>An aircraft on F, 40 ft short of the F/A junction (node 54), nosed 298° towards it.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) OnFShortOfAPose = (
        new LatLon(37.61906270023921, -122.38060898289163),
        new TrueHeading(297.9)
    );

    /// <summary>The A/T6B junction centre on A: its straight T6B edge makes it a junction of the two taxiways.</summary>
    private const int T6BOnAJunctionNode = 53;

    [Fact]
    public void Taxi_ReissuedAF28L_OnB_CrossesRunway01L19ROnce()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOffGraph(ground, "THY9WC", "B789", Thy9wcPose, new HoldingAfterPushbackPhase());
        aircraft.Ground.ParkingSpot = "G10";

        CommandResult first = ground.Engine.SendCommand("THY9WC", "TAXI A F 28L HS 1L");
        output.WriteLine($"first: {first.Success} — {first.Message}");
        Assert.True(first.Success, first.Message);
        TaxiRoute firstRoute = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, firstRoute);
        string firstSequence = firstRoute.FormatTaxiwaySequence();
        output.WriteLine($"first sequence: {firstSequence}");

        // The bundle's state at the re-issue (t=195): not moved, IAS 0, CurrentTaxiway B.
        aircraft.Ground.CurrentTaxiway = "B";
        CommandResult second = ground.Engine.SendCommand("THY9WC", "TAXI A F 28L HS 1L");
        output.WriteLine($"second: {second.Success} — {second.Message}");
        Assert.True(second.Success, second.Message);
        TaxiRoute secondRoute = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, secondRoute);
        output.WriteLine($"second sequence: {secondRoute.FormatTaxiwaySequence()}");

        Assert.Equal(1, CrossingsOf(secondRoute, "1L"));
        Assert.Equal(firstSequence, secondRoute.FormatTaxiwaySequence());
    }

    [Fact]
    public void Taxi_A_AfterHoldingShortOfK_JoinsAViaK()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = HoldShortOfKOnB(ground);

        CommandResult toD1 = ground.Engine.SendCommand("SKW3398", "TAXI A @D1");
        output.WriteLine($"TAXI A @D1: {toD1.Success} — {toD1.Message}");
        Assert.True(toD1.Success, toD1.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}");

        // The route opens on the B→K fillet the aircraft turns over from its bar on B (the readback names it
        // "B K A …"); the taxiway legs it follows start K, then A.
        Assert.Equal(["K", "A"], StraightTaxiwayLegs(route).Take(2));
        Assert.DoesNotContain(
            route.Segments,
            s => s.Edge.Edge.MatchesTaxiway("A1") || s.Edge.Edge.MatchesTaxiway("A2") || s.Edge.Edge.MatchesTaxiway("M1")
        );
    }

    /// <summary>
    /// Holding short of K on B, a bare <c>TAXI A</c> (no destination) turns onto K and ends where K meets A: the route
    /// never enters A, and that still counts as joining it.
    /// </summary>
    [Fact]
    public void Taxi_BareA_AfterHoldingShortOfK_EndsAtTheKAJunction()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = HoldShortOfKOnB(ground);

        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXI A");
        output.WriteLine($"TAXI A: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}");

        Assert.Equal("K", StraightTaxiwayLegs(route)[^1]);
        Assert.Contains(route.Segments[^1].Edge.ToNode.Edges, e => e.MatchesTaxiway("A"));
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("B") && (s.Edge.Edge is not GroundArc) && (s.FromNodeId == 135));
    }

    /// <summary>
    /// Holding short of K on B, <c>TAXI B T</c> continues along B across K: the aircraft already stands on the first
    /// cleared taxiway, so the taxiway it holds short of is not put in front of the path.
    /// </summary>
    [Fact]
    public void Taxi_BT_AfterHoldingShortOfK_ContinuesAlongB()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = HoldShortOfKOnB(ground);

        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXI B T");
        output.WriteLine($"TAXI B T: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}");

        Assert.Equal("B", StraightTaxiwayLegs(route)[0]);
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("K"));
    }

    /// <summary>
    /// Holding short of K on B, <c>TAXI Q</c>: K meets Q only across 28L/10R, so turning onto K would cross a runway
    /// to reach the first cleared taxiway. The taxiway held short of is not prepended; the clearance resolves as
    /// cleared, along B to Q.
    /// </summary>
    [Fact]
    public void Taxi_Q_AfterHoldingShortOfK_DoesNotCrossTheRunwayOnK()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = HoldShortOfKOnB(ground);

        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXI Q");
        output.WriteLine($"TAXI Q: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}");

        Assert.NotEqual("K", StraightTaxiwayLegs(route)[0]);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway);
    }

    /// <summary>
    /// SKW5416 stationary on E, cleared <c>TAXI B</c>: the route bridges along E onto B. E is where the aircraft
    /// stands, not a deviation from the clearance, so no "not in the route issued" note names it.
    /// </summary>
    [Fact]
    public void Taxi_B_FromE_HasNoNotInRouteIssuedNote()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        (CommandResult result, TaxiRoute route) = TaxiSkw5416B(ground);

        Assert.DoesNotContain("not in the route issued", result.Message);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in the route issued"));
    }

    /// <summary>
    /// SKW5416's bare <c>TAXI B</c> runs along B to its end. B has no runway hold-short bar anywhere, so the route
    /// holds short of no runway and crosses none.
    /// </summary>
    [Fact]
    public void Taxi_B_FromE_CrossesNoRunway()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        (_, TaxiRoute route) = TaxiSkw5416B(ground);

        Assert.Equal("B", StraightTaxiwayLegs(route)[^1]);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway);
    }

    /// <summary>THY9WC's re-issued <c>TAXI A F 28L HS 1L</c> bridges B → B1 onto A; B is where it stands, not a deviation.</summary>
    [Fact]
    public void Taxi_ReissuedAF28L_OnB_HasNoNoteForB()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOffGraph(ground, "THY9WC", "B789", Thy9wcPose, new HoldingAfterPushbackPhase());
        aircraft.Ground.ParkingSpot = "G10";
        aircraft.Ground.CurrentTaxiway = "B";

        CommandResult result = ground.Engine.SendCommand("THY9WC", "TAXI A F 28L HS 1L");
        output.WriteLine($"TAXI A F 28L HS 1L: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}; warnings: {string.Join(" | ", route.Warnings)}");

        Assert.Equal("B", StraightTaxiwayLegs(route)[0]);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("taxiing via B "));
        Assert.DoesNotContain("taxiing via B ", result.Message);
    }

    /// <summary>
    /// Which hold-short counts as holding short of a taxiway: an explicit hold-short of a taxiway name, whatever
    /// letters it has (L, C and R are taxiways at SFO too); never a runway, a spot, or a runway bar the route itself
    /// put in.
    /// </summary>
    [Theory]
    [InlineData("K", HoldShortReason.ExplicitHoldShort, "K")]
    [InlineData("L", HoldShortReason.ExplicitHoldShort, "L")]
    [InlineData("C", HoldShortReason.ExplicitHoldShort, "C")]
    [InlineData("28L", HoldShortReason.ExplicitHoldShort, null)]
    [InlineData("1L", HoldShortReason.ExplicitHoldShort, null)]
    [InlineData("10L/28R", HoldShortReason.ExplicitHoldShort, null)]
    [InlineData("$5A", HoldShortReason.ExplicitHoldShort, null)]
    [InlineData("K", HoldShortReason.RunwayCrossing, null)]
    [InlineData("K", HoldShortReason.DestinationRunway, null)]
    public void HeldShortTaxiway_CountsOnlyAnExplicitTaxiwayTarget(string target, HoldShortReason reason, string? expected)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var hold = new HoldShortPoint
        {
            NodeId = KOnBJunctionNode,
            Reason = reason,
            TargetName = target,
        };
        AircraftState aircraft = SpawnOffGraph(ground, "SKW3398", "E75L", Skw3398OnBPose, new HoldingShortPhase(hold));

        Assert.Equal(expected, GroundCommandHandler.HeldShortTaxiway(aircraft));
    }

    /// <summary>
    /// The fallback stays: taxiing north-west on B, cleared <c>TAXI K</c> (K meets B further along), the path as
    /// cleared cannot start where the aircraft stands, so the taxiway it is on is prepended and it continues on B
    /// to K. (<c>TAXI A</c> from here resolves as cleared by bridging B → D → A, so it cannot pin the fallback.)
    /// </summary>
    [Fact]
    public void Taxi_K_WhileTaxiingOnB_StillPrependsB()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode? start = ground.Layout.FindNearestNodeForTaxi(Skw3398OnBPose.Position, Skw3398OnBPose.Heading);
        Assert.NotNull(start);
        TaxiRoute? asCleared = TaxiPathfinder.ResolveExplicitPath(
            ground.Layout,
            start.Id,
            ["K"],
            out string? asClearedFailure,
            new ExplicitPathOptions { OccupiedTaxiway = null, StartHeadingTrue = Skw3398OnBPose.Heading.Degrees },
            AircraftCategory.Jet
        );
        output.WriteLine($"as cleared from node {start.Id}: {asClearedFailure}");
        Assert.Null(asCleared);

        AircraftState aircraft = SpawnOffGraph(ground, "SKW3398", "E75L", Skw3398OnBPose, new HoldingInPositionPhase());
        aircraft.Ground.CurrentTaxiway = "B";

        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXI K");
        output.WriteLine($"TAXI K: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);

        // With no destination the route ends where B meets K ("holding at the B/K intersection").
        Assert.Equal(["B"], StraightTaxiwayLegs(route));
        Assert.Contains(route.Segments[^1].Edge.ToNode.Edges, e => e.MatchesTaxiway("K"));
    }

    /// <summary>
    /// Holding short of A on F, <c>TAXI T6B</c>: turning onto A and following it to T6B would run through the A/T6B
    /// junction at node 53 and join T6B only further along A (with no destination, it runs on past 53 and stops at a
    /// later A/T6B intersection without ever entering T6B). The held-short prepend is rejected ("passes the T6B
    /// junction"), and the clearance resolves as cleared: onto A and off it through the A → T6B fillet short of 53.
    /// </summary>
    [Fact]
    public void Taxi_T6B_AfterHoldingShortOfAOnF_TurnsOntoT6BAtTheFirstJunction()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var hold = new HoldShortPoint
        {
            NodeId = AOnFJunctionNode,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "A",
        };
        AircraftState aircraft = SpawnOffGraph(ground, "UAL1", "A320", OnFShortOfAPose, new HoldingShortPhase(hold));
        aircraft.Ground.CurrentTaxiway = "F";
        Assert.Equal("A", GroundCommandHandler.HeldShortTaxiway(aircraft));

        CommandResult result = ground.Engine.SendCommand("UAL1", "TAXI T6B");
        output.WriteLine($"TAXI T6B: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}");

        Assert.Equal(["A", "T6B"], StraightTaxiwayLegs(route).Take(2));
        Assert.DoesNotContain(
            route.Segments,
            s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("A") && (s.ToNodeId == T6BOnAJunctionNode)
        );
    }

    /// <summary>
    /// The taxiways the route's straight segments follow, in order with repeats collapsed: fillet arcs (which bear
    /// both taxiways' names) and ramp pavement are left out.
    /// </summary>
    private static List<string> StraightTaxiwayLegs(TaxiRoute route)
    {
        var legs = new List<string>();
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            if ((segment.Edge.Edge is GroundArc) || segment.TaxiwayName.Equals("RAMP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((legs.Count == 0) || !legs[^1].Equals(segment.TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                legs.Add(segment.TaxiwayName);
            }
        }

        return legs;
    }

    /// <summary>Distinct hold-short bars on <paramref name="runway"/> the route stops at or crosses.</summary>
    private static int CrossingsOf(TaxiRoute route, string runway) =>
        route.HoldShortPoints.Where(h => SfoGroundHarness.HoldShortMatches(h, runway)).Select(h => h.NodeId).Distinct().Count();

    /// <summary>SKW3398 spawned on B, cleared <c>TAXI B K HS K</c> and ticked until it holds short of K.</summary>
    private AircraftState HoldShortOfKOnB(SfoGround ground)
    {
        AircraftState aircraft = SpawnOffGraph(ground, "SKW3398", "E75L", Skw3398OnBPose, new HoldingInPositionPhase());
        aircraft.Ground.CurrentTaxiway = "B";

        CommandResult toK = ground.Engine.SendCommand("SKW3398", "TAXI B K HS K");
        output.WriteLine($"TAXI B K HS K: {toK.Success} — {toK.Message}");
        Assert.True(toK.Success, toK.Message);
        int held = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase, 300, null);
        Assert.True(held > 0, "SKW3398 never came to hold short of K");
        output.WriteLine(
            $"holding short after {held}s at ({aircraft.Position.Lat}, {aircraft.Position.Lon}); CurrentTaxiway={aircraft.Ground.CurrentTaxiway}"
        );
        return aircraft;
    }

    /// <summary>SKW5416 spawned stationary on E and cleared <c>TAXI B</c>; the result and the route it was given.</summary>
    private (CommandResult Result, TaxiRoute Route) TaxiSkw5416B(SfoGround ground)
    {
        AircraftState aircraft = SpawnOffGraph(ground, "SKW5416", "CRJ7", Skw5416OnEPose, new HoldingInPositionPhase());
        aircraft.Ground.CurrentTaxiway = "E";

        CommandResult result = ground.Engine.SendCommand("SKW5416", "TAXI B");
        output.WriteLine($"TAXI B: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}; warnings: {string.Join(" | ", route.Warnings)}");
        return (result, route);
    }

    private static AircraftState SpawnOffGraph(
        SfoGround ground,
        string callsign,
        string type,
        (LatLon Position, TrueHeading Heading) pose,
        Phase startPhase
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pose.Position,
            TrueHeading = pose.Heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
