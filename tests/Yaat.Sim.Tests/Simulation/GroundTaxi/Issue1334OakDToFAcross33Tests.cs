using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #1334 (YAAT-850): OAK <c>TAXI C D F @OLD1 HS 33</c> from the E exit of 28R. D and F share no node: D ends on runway
/// 15/33's centreline near its 15 end and F begins about 100 ft further along it, so the clearance means "D up to 33, hold
/// short, cross, F to OLD1". The resolver found no D/F junction, bridged D→F by the cheapest connector, which is back along
/// C to the C/F junction, and so dropped D and held the aircraft short of 33 on C ("Taxi via E C F HS 15/33 at C").
/// </summary>
[Collection("Acceptance")]
public class Issue1334OakDToFAcross33Tests(ITestOutputHelper output)
{
    /// <summary>N36936's heading at bundle t=1100, holding after exiting 28R on E: nosed 337.7° true.</summary>
    private static readonly TrueHeading AfterEExitHeading = new(337.6650135475534);

    /// <summary>How far past E's 28R bar, outside it along <see cref="AfterEExitHeading"/>, the aircraft holds after the exit.</summary>
    private const double AfterEExitPastBarFt = 12.0;

    /// <summary>OAK D/J junction: D leaves it north toward 15/33.</summary>
    private static GroundNode OakDJJunctionNode(AirportGroundLayout layout) => GroundNodeLookup.Junction(layout, "D", "J");

    internal const string Callsign = "N36936";

    [Fact]
    public void TaxiCDF_DrivesDAndHoldsShortOf33OnD()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeN36936(layout);
        var taxi = new TaxiCommand(Path: ["C", "D", "F"], HoldShorts: [HoldShortTarget.Parse("33")], Expedite: false, DestinationParking: "OLD1");

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            output.WriteLine($"hold short: node {hs.NodeId} {hs.TargetName} ({hs.Reason})");
        }

        GroundNode old1 = Assert.IsType<GroundNode>(layout.FindParkingByName("OLD1"));
        Assert.Equal(old1.Id, route.Segments[^1].ToNodeId);

        // D is driven, not merely touched at the C/D junction.
        Assert.Contains(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("D"));

        // 15/33 is crossed from D to F: the HS 33 bar is D's, and the route never reaches C's 15/33 bar.
        HoldShortPoint hs33 = Assert.Single(route.HoldShortPoints, h => h.TargetName?.Contains("33") == true);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs33.Reason);
        Assert.Contains(layout.Nodes[hs33.NodeId].Edges, e => e.MatchesTaxiway("D"));
        Assert.DoesNotContain(
            route.Segments,
            s => IsRunway33BarOnTaxiway(layout, s.FromNodeId, "C") || IsRunway33BarOnTaxiway(layout, s.ToNodeId, "C")
        );
    }

    /// <summary>
    /// The same clearance without <c>HS 33</c>: crossing 15/33 from D to F is still a runway crossing the aircraft holds short
    /// of until cleared, at D's bar. The controller never cleared it to taxi along 33, so the runway the route jogs along must
    /// not count as a cleared runway.
    /// </summary>
    [Fact]
    public void TaxiCDF_WithoutHs_HoldsShortOf33OnDForTheCrossing()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeN36936(layout);
        var taxi = new TaxiCommand(Path: ["C", "D", "F"], HoldShorts: [], Expedite: false, DestinationParking: "OLD1");

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");

        HoldShortPoint crossing = Assert.Single(route.HoldShortPoints, h => h.TargetName?.Contains("33") == true);
        Assert.Equal(HoldShortReason.RunwayCrossing, crossing.Reason);
        Assert.True(IsRunway33BarOnTaxiway(layout, crossing.NodeId, "D"), $"15/33 hold short at node {crossing.NodeId} is not D's bar");
    }

    /// <summary>The readback names D and places the 15/33 hold on D, and never reads the runway jog as "on 33".</summary>
    [Fact]
    public void TaxiCDF_ReadbackNamesDAndTheHoldAtD()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeN36936(layout);
        var taxi = new TaxiCommand(Path: ["C", "D", "F"], HoldShorts: [HoldShortTarget.Parse("33")], Expedite: false, DestinationParking: "OLD1");

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Taxi via E C D F HS 15/33 at D @OLD1", result.Message);
    }

    /// <summary>
    /// Driven end to end: the aircraft holds at D's 15/33 bar, and <c>CROSS 33</c> carries it across the runway jog onto F
    /// without stopping between D's bar and F's bar on the far side.
    /// </summary>
    [Fact]
    public void TaxiCDF_Cross33_CarriesTheAircraftAcrossOntoF()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = MakeN36936(layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-issue1334-oak-d-f-across-33",
            ScenarioName = "OAK D to F across 33",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Callsign, "TAXI C D F @OLD1 HS 33");
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);

        AircraftState? holding = TickUntil(engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900);
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(holding?.Phases?.CurrentPhase);
        output.WriteLine($"holding short of {hold.HoldShort.TargetName} at node #{hold.HoldShort.NodeId}");
        Assert.True(IsRunway33BarOnTaxiway(layout, hold.HoldShort.NodeId, "D"), $"holding at node {hold.HoldShort.NodeId}, not D's 15/33 bar");

        int farBarSegment = route.Segments.FindIndex(s => IsRunway33BarOnTaxiway(layout, s.FromNodeId, "F"));
        Assert.True(farBarSegment >= 0, "the route never leaves F's 15/33 bar");

        CommandResult cross = engine.SendCommand(Callsign, "CROSS 33");
        output.WriteLine($"cross: {cross.Success} — {cross.Message}");
        Assert.True(cross.Success, cross.Message);

        bool stoppedBetweenBars = false;
        AircraftState? past = TickUntil(
            engine,
            ac =>
            {
                stoppedBetweenBars |= ac.Phases?.CurrentPhase is HoldingShortPhase;
                return ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex > farBarSegment;
            },
            300
        );

        Assert.False(stoppedBetweenBars, "the aircraft held short again between D's bar and F's bar");
        Assert.NotNull(past);
        output.WriteLine(
            $"past F's bar: twy={past.Ground.CurrentTaxiway} gs={past.GroundSpeed:F1} phase={past.Phases?.CurrentPhase?.GetType().Name}"
        );
        Assert.IsType<TaxiingPhase>(past.Phases?.CurrentPhase);
        Assert.True(past.GroundSpeed > 0, "the aircraft stopped past F's bar");
        Assert.Equal("F", past.Ground.CurrentTaxiway);
    }

    /// <summary>
    /// The reverse clearance, <c>TAXI F D J</c>: F joins 15/33 about 100 ft along it from D's join and from its far side, so
    /// the route crosses the runway the other way along its centreline, holding short of 15/33 on F's bar, and drives D down
    /// to the D/J junction. J names where D ends; a bare final D would hold at the F/D junction itself.
    /// </summary>
    [Fact]
    public void TaxiFDJ_CrossesTheOffsetTheOtherWayOntoD()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        string runway = RunwayCenterline(layout, "33");
        (GroundNode start, GroundNode join) = StartAwayFromRunwayJoin(layout, "OAK", "F", runway);
        AircraftState aircraft = MakeAircraftAt(layout, "OAK", start, GeoMath.BearingTo(start.Position, join.Position));
        var taxi = new TaxiCommand(Path: ["F", "D", "J"], HoldShorts: [], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");

        Assert.Contains(route.Segments, s => s.ToNodeId == OakDJJunctionNode(layout).Id);
        Assert.Contains(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("D"));
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        List<TaxiRouteSegment> chord = [.. route.Segments.Where(s => VirtualNode.IsVirtualEdge(s.Edge.Edge))];
        Assert.Equal(3, chord.Count);
        double chordBearing = GeoMath.BearingTo(chord[1].Edge.FromNode.Position, chord[1].Edge.ToNode.Position);
        Assert.InRange(RouteCostFunction.HeadingDelta(chord[0].Edge.DepartureBearing, chordBearing), 52.8, 53.0);
        Assert.InRange(RouteCostFunction.HeadingDelta(chordBearing, chord[2].Edge.DepartureBearing), 54.2, 54.4);
        HoldShortPoint crossing = Assert.Single(route.HoldShortPoints, h => h.TargetName?.Contains("33") == true);
        Assert.Equal(HoldShortReason.RunwayCrossing, crossing.Reason);
        Assert.True(IsRunway33BarOnTaxiway(layout, crossing.NodeId, "F"), $"15/33 hold short at node {crossing.NodeId} is not F's bar");
    }

    /// <summary>
    /// <c>TAXI D F @OLD1</c> from the D/J junction, nosed north up D: the route drives D, holds short of 15/33 on D's bar for
    /// the crossing and carries on along F to OLD1, instead of bridging back along J and C to the C/F junction.
    /// </summary>
    [Fact]
    public void TaxiDF_FromTheDJJunction_CrossesOntoF()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode start = OakDJJunctionNode(layout);
        Assert.Contains(start.Edges, e => e.MatchesTaxiway("D"));
        Assert.Contains(start.Edges, e => e.MatchesTaxiway("J"));
        AircraftState aircraft = MakeAircraftAt(layout, "OAK", start, 330.0);
        var taxi = new TaxiCommand(Path: ["D", "F"], HoldShorts: [], Expedite: false, DestinationParking: "OLD1");

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");

        GroundNode old1 = Assert.IsType<GroundNode>(layout.FindParkingByName("OLD1"));
        Assert.Equal(old1.Id, route.Segments[^1].ToNodeId);
        Assert.Contains(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("D"));
        HoldShortPoint crossing = Assert.Single(route.HoldShortPoints, h => h.TargetName?.Contains("33") == true);
        Assert.Equal(HoldShortReason.RunwayCrossing, crossing.Reason);
        Assert.True(IsRunway33BarOnTaxiway(layout, crossing.NodeId, "D"), $"15/33 hold short at node {crossing.NodeId} is not D's bar");
        Assert.DoesNotContain(
            route.Segments,
            s => IsRunway33BarOnTaxiway(layout, s.FromNodeId, "C") || IsRunway33BarOnTaxiway(layout, s.ToNodeId, "C")
        );
    }

    /// <summary>
    /// The look-ahead change itself: scoring each C→D junction, the tail probe resolves D→F across 15/33 instead of failing
    /// on the suppressed detour, so no C→D candidate carries the unresolvable-tail penalty (1000) in its continuation.
    /// </summary>
    [Fact]
    public void TaxiCDF_LookAheadProbe_ResolvesTheTailAcrossTheRunway()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode old1 = Assert.IsType<GroundNode>(layout.FindParkingByName("OLD1"));

        // The probe starts beyond E's 28R bar, on E's straight edge toward C, so the C→D segment it resolves begins at a node
        // on C instead of reaching C only through the bar's C - E fillet arc.
        GroundNode start = GroundNodeLookup.OnTaxiway(layout, "E", OakE28RBar(layout), 1.0, GroundNodeLookup.Junction(layout, "C", "E"));
        Assert.Contains(start.Edges, e => (e is not GroundArc) && (e.TaxiwayName == "E"));
        Assert.Contains(start.Edges, e => e.TaxiwayName is "C" or "C - E");
        GroundNode bar = OakE28RBar(layout);

        var diag = new List<string>();
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["C", "D", "F"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = "E",
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("C172"),
                DestinationHintNode = old1,
                StartHeadingTrue = GeoMath.BearingTo(bar.Position, start.Position),
                DiagnosticLog = diag.Add,
            },
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small
        );
        Assert.Null(failReason);
        Assert.NotNull(route);

        List<string> picks = [.. diag.Where(line => line.StartsWith("[junction-pick] C→D ", StringComparison.Ordinal))];
        foreach (string pick in picks)
        {
            output.WriteLine(pick);
        }

        Assert.NotEmpty(picks);
        Assert.All(picks, pick => Assert.True(ContinuationCost(pick) < 1000.0, pick));
    }

    /// <summary>
    /// FLL J2 and J3 both join 10R/28L from the same side, about 196 ft apart along it, with no junction of their own. Going
    /// from one to the other over the runway is taxiing along it, never a crossing, so the route never uses its centreline and
    /// keeps the connector detour along J it took before the crossing rule.
    /// </summary>
    [Fact]
    public void FllJ2J3_SameSideOfTheRunway_IsNotRoutedAlongIt() => AssertNotRoutedAlongRunway("FLL", "J2", "J3", "10R", "J");

    /// <summary>
    /// CMH C8 and C9 join 10R/28L from opposite sides, but about 333 ft apart along it: past the crossing bound, so the pair is
    /// not a crossing and the route never uses the runway's centreline. The only other way between them is lettered C, which
    /// the clearance leaves out, so the two do not connect (YAAT-899 R6): the taxi is refused and the aircraft stays put.
    /// </summary>
    [Fact]
    public void CmhC8C9_OffsetPastTheCrossingBound_IsNotRoutedAlongTheRunway()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("CMH");
        if (layout is null)
        {
            return;
        }

        string runway = RunwayCenterline(layout, "10R");
        (GroundNode start, GroundNode join) = StartAwayFromRunwayJoin(layout, "CMH", "C8", runway);
        AircraftState aircraft = MakeAircraftAt(layout, "CMH", start, GeoMath.BearingTo(start.Position, join.Position));
        LatLon before = aircraft.Position;

        var taxi = new TaxiCommand(Path: ["C8", "C9"], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.False(result.Success);
        Assert.Equal("Unable, C8 and C9 do not connect, request taxi instructions", result.Message);
        Assert.Equal(before, aircraft.Position);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.True(
            (route is null) || !route.Segments.Any(s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesTaxiway(runway)),
            "the refused taxi left a route along the runway"
        );
    }

    /// <summary>
    /// YAAT-921: CMH E4 and F3 cross 10L/28R square from opposite sides, about 123 ft apart along its centreline, but neither
    /// touches the centreline: each ends on the runway pavement and reaches it through a stitched <c>RWY10L/28R:link</c> edge.
    /// <c>TAXI E4 F3</c> is one crossing of an uncleared runway: it holds short at E4's bar, never stops before F3's bar, ends
    /// on F3 clear of the runway, and reads back as a crossing, never "on 10L".
    /// </summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("B738")]
    [InlineData("A321")]
    public void CmhE4F3_StitchedCrossing_Resolves(string aircraftType)
    {
        if (CmhE4Start() is not { } cmh)
        {
            return;
        }

        AircraftState aircraft = MakeAircraftAt(cmh.Layout, "CMH", cmh.Start, cmh.HeadingDeg);
        aircraft.AircraftType = aircraftType;
        var taxi = new TaxiCommand(Path: ["E4", "F3"], HoldShorts: [], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, cmh.Layout);
        output.WriteLine($"{aircraftType}: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            output.WriteLine($"hold short: #{hs.NodeId} {hs.TargetName} ({hs.Reason})");
        }

        double centerlineFt = SegmentExpander.CenterlineLengthFt(route.Segments.Select(s => s.Edge.Edge));
        output.WriteLine($"centreline run {centerlineFt:F0} ft");
        Assert.Equal(0.0, centerlineFt);
        List<TaxiRouteSegment> chord = [.. route.Segments.Where(s => VirtualNode.IsVirtualEdge(s.Edge.Edge))];
        double chordBearing = GeoMath.BearingTo(chord[1].Edge.FromNode.Position, chord[1].Edge.ToNode.Position);
        Assert.InRange(RouteCostFunction.HeadingDelta(chord[0].Edge.DepartureBearing, chordBearing), 44.9, 45.1);
        Assert.InRange(RouteCostFunction.HeadingDelta(chordBearing, chord[2].Edge.DepartureBearing), 42.1, 42.3);

        HoldShortPoint crossing = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing);
        Assert.Equal(cmh.E4Bar.Id, crossing.NodeId);
        Issue905BareCrossingEndTests.AssertEndsClearPast(output, route, cmh.Layout, ("10L", "F3"), aircraftType);

        Assert.DoesNotContain("on 10L", result.Message);
        Assert.DoesNotContain("on 28R", result.Message);
        Assert.DoesNotContain(":link", result.Message);

        List<string> diag = ResolveCmhE4F3(cmh, aircraftType);
        Assert.Contains(diag, line => line.StartsWith("[runway-crossing] E4→F3 across RWY10L/28R ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The chord's turns are the corners flown, 45.0° onto it and 42.2° off it, and every look-ahead probe of the transition
    /// measures the same two.
    /// </summary>
    [Fact]
    public void CmhE4F3_ChordCornersAreTheTurnsFlown()
    {
        if (CmhE4Start() is not { } cmh)
        {
            return;
        }

        List<string> diag = ResolveCmhE4F3(cmh, "B738");
        List<string> legLines = [.. diag.Where(line => line.StartsWith("[runway-crossing] E4→F3 straight-over ", StringComparison.Ordinal))];
        Assert.NotEmpty(legLines);
        Assert.DoesNotContain(
            diag,
            line => line.Contains("refused", StringComparison.Ordinal) && line.StartsWith("[runway-crossing]", StringComparison.Ordinal)
        );

        foreach (string legLine in legLines)
        {
            output.WriteLine(legLine);
            Assert.EndsWith("corners 45.0° then 42.2°", legLine);
        }
    }

    /// <summary>
    /// Driven end to end: the aircraft holds at E4's 10L/28R bar, and <c>CROSS 10L</c> carries it across onto F3 without
    /// stopping between E4's bar and F3's bar, and it comes to rest on F3 past F3's bar.
    /// </summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("B738")]
    public void CmhE4F3_Cross10L_CrossesOntoF3WithoutStoppingBetweenTheBars(string aircraftType)
    {
        TestVnasData.EnsureInitialized();
        if ((TestVnasData.NavigationDb is null) || (CmhE4Start() is not { } cmh))
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = MakeAircraftAt(cmh.Layout, "CMH", cmh.Start, cmh.HeadingDeg);
        aircraft.AircraftType = aircraftType;
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-921-cmh-e4-f3",
            ScenarioName = "CMH E4 to F3 across 10L/28R",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "CMH",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Callsign, "TAXI E4 F3");
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int farBarSegment = route.Segments.FindIndex(s => s.ToNodeId == cmh.F3Bar.Id);
        Assert.True(farBarSegment >= 0, "the route never reaches F3's 10L/28R bar");

        AircraftState? holding = TickUntil(engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900);
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(holding?.Phases?.CurrentPhase);
        output.WriteLine($"holding short of {hold.HoldShort.TargetName} at node #{hold.HoldShort.NodeId}");
        Assert.Equal(cmh.E4Bar.Id, hold.HoldShort.NodeId);

        CommandResult cross = engine.SendCommand(Callsign, "CROSS 10L");
        output.WriteLine($"cross: {cross.Success} — {cross.Message}");
        Assert.True(cross.Success, cross.Message);

        bool heldBetweenBars = false;
        bool moving = false;
        bool stoppedBetweenBars = false;
        bool pastFarBar = false;
        AircraftState? atRest = TickUntil(
            engine,
            ac =>
            {
                pastFarBar |= (ac.Ground.AssignedTaxiRoute is null) || (ac.Ground.AssignedTaxiRoute.CurrentSegmentIndex > farBarSegment);
                heldBetweenBars |= (!pastFarBar) && (ac.Phases?.CurrentPhase is HoldingShortPhase);
                stoppedBetweenBars |= (!pastFarBar) && moving && (ac.GroundSpeed < 0.1);
                moving |= ac.GroundSpeed >= 0.1;
                return pastFarBar && (ac.GroundSpeed < 0.1);
            },
            300
        );

        Assert.False(heldBetweenBars, "the aircraft held short again between E4's bar and F3's bar");
        Assert.False(stoppedBetweenBars, "the aircraft came to a stop between E4's bar and F3's bar");
        Assert.NotNull(atRest);
        double pastFt = GeoMath.DistanceNm(cmh.F3Bar.Position, atRest.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"at rest {pastFt:F0} ft past F3's bar #{cmh.F3Bar.Id}, twy={atRest.Ground.CurrentTaxiway}");
        Assert.True(pastFt >= AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0, $"at rest only {pastFt:F0} ft past F3's bar");
        Assert.Equal("F3", atRest.Ground.CurrentTaxiway);
    }

    private sealed record CmhE4F3Start(AirportGroundLayout Layout, GroundNode Start, GroundNode E4Bar, GroundNode F3Bar, double HeadingDeg);

    /// <summary>CMH's E/E4 junction, nosed up E4 toward its 10L/28R bar; null when the CMH layout is absent.</summary>
    private CmhE4F3Start? CmhE4Start()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("CMH");
        if (layout is null)
        {
            return null;
        }

        GroundNode start = GroundNodeLookup.Junction(layout, "E", "E4");
        GroundNode e4Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "E4", start);
        GroundNode f3Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "F3", start);
        output.WriteLine($"CMH: start #{start.Id} on E/E4, E4's bar #{e4Bar.Id}, F3's bar #{f3Bar.Id}");
        return new CmhE4F3Start(layout, start, e4Bar, f3Bar, GeoMath.BearingTo(start.Position, e4Bar.Position));
    }

    /// <summary>The pathfinder's diagnostic lines for <c>E4 F3</c> from <paramref name="cmh"/>'s start, asserting it resolves.</summary>
    private List<string> ResolveCmhE4F3(CmhE4F3Start cmh, string aircraftType)
    {
        var diag = new List<string>();
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            cmh.Layout,
            cmh.Start.Id,
            ["E4", "F3"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt(aircraftType),
                StartHeadingTrue = cmh.HeadingDeg,
                DiagnosticLog = diag.Add,
            },
            AircraftCategorization.Categorize(aircraftType),
            WakeTurbulenceData.WakeClassForType(aircraftType, AircraftCategorization.Categorize(aircraftType))
        );
        Assert.True(route is not null, failReason);
        return diag;
    }

    /// <summary>
    /// The 300 ft crossing bound counts runway centreline only. No surveyed pair that qualifies lies between 250 and 300 ft,
    /// so the shared measure is pinned on COS's real 17R/35L centreline between A2's and C2's joins (about 255 ft) together
    /// with C2's fillet arc onto the runway: the arc is not centreline and never counts toward the bound.
    /// </summary>
    [Fact]
    public void CenterlineLength_CountsOnlyCenterlineEdges_OnARealLayout()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("COS");
        if (layout is null)
        {
            return;
        }

        string runway = RunwayCenterline(layout, "35L");
        GroundNode a2Join = StartAwayFromRunwayJoin(layout, "COS", "A2", runway).Join;
        GroundNode c2Join = StartAwayFromRunwayJoin(layout, "COS", "C2", runway).Join;
        var centerline = new HashSet<IGroundEdge>();
        var fillets = new HashSet<IGroundEdge>();
        foreach (GroundNode node in layout.Nodes.Values.Where(n => IsAlongRunwayBetween(n, a2Join, c2Join)))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.IsRunwayCenterline && edge.MatchesTaxiway(runway) && IsAlongRunwayBetween(edge.OtherNode(node), a2Join, c2Join))
                {
                    centerline.Add(edge);
                }
                else if ((edge is GroundArc) && edge.MatchesTaxiway("C2") && edge.MatchesTaxiway(runway))
                {
                    fillets.Add(edge);
                }
            }
        }

        double centerlineFt = centerline.Sum(e => e.DistanceNm) * GeoMath.FeetPerNm;
        double withFilletsFt = (centerline.Sum(e => e.DistanceNm) + fillets.Sum(e => e.DistanceNm)) * GeoMath.FeetPerNm;
        output.WriteLine($"centreline {centerlineFt:F0} ft over {centerline.Count} edges; with {fillets.Count} C2 fillet(s) {withFilletsFt:F0} ft");

        Assert.NotEmpty(fillets);
        Assert.InRange(centerlineFt, 250.0, 300.0);
        Assert.Equal(centerlineFt, SegmentExpander.CenterlineLengthFt([.. centerline, .. fillets]), 6);
    }

    internal static bool IsAlongRunwayBetween(GroundNode node, GroundNode a, GroundNode b)
    {
        double viaNodeFt = (GeoMath.DistanceNm(a.Position, node.Position) + GeoMath.DistanceNm(node.Position, b.Position)) * GeoMath.FeetPerNm;
        return viaNodeFt - (GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm) < 1.0;
    }

    private void AssertNotRoutedAlongRunway(string airportId, string fromTaxiway, string toTaxiway, string runwayDesignator, string detourTaxiway)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        if (layout is null)
        {
            return;
        }

        string runway = RunwayCenterline(layout, runwayDesignator);
        (GroundNode start, GroundNode join) = StartAwayFromRunwayJoin(layout, airportId, fromTaxiway, runway);
        AircraftState aircraft = MakeAircraftAt(layout, airportId, start, GeoMath.BearingTo(start.Position, join.Position));

        var taxi = new TaxiCommand(Path: [fromTaxiway, toTaxiway], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");

        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesTaxiway(runway));
        Assert.Contains(route.Segments, s => s.Edge.ToNode.Edges.Any(e => e.MatchesTaxiway(detourTaxiway)));
    }

    internal static string RunwayCenterline(AirportGroundLayout layout, string designator)
    {
        Assert.True(layout.TryGetRunwayCenterlineName(designator, out string? centerline), $"no centreline for runway {designator}");
        return centerline;
    }

    private (GroundNode Start, GroundNode Join) StartAwayFromRunwayJoin(
        AirportGroundLayout layout,
        string airportId,
        string taxiway,
        string runway
    ) => StartAwayFromRunwayJoin(output, layout, airportId, taxiway, runway);

    /// <summary>The node where <paramref name="taxiway"/> joins <paramref name="runway"/>'s centreline, and its node farthest from there.</summary>
    internal static (GroundNode Start, GroundNode Join) StartAwayFromRunwayJoin(
        ITestOutputHelper output,
        AirportGroundLayout layout,
        string airportId,
        string taxiway,
        string runway
    )
    {
        List<GroundNode> nodes = layout.GetNodesOnTaxiway(taxiway);
        GroundNode join = Assert.Single(nodes, n => n.Edges.Any(e => (e is GroundEdge { IsRunwayCenterline: true }) && e.MatchesTaxiway(runway)));
        GroundNode start = nodes.OrderByDescending(n => GeoMath.DistanceNm(n.Position, join.Position)).First();
        output.WriteLine($"{airportId}: start #{start.Id} on {taxiway}, joins {runway} at #{join.Id}");
        return (start, join);
    }

    private static double ContinuationCost(string junctionPick)
    {
        const string Marker = "continuation+=";
        int from = junctionPick.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        int to = junctionPick.IndexOf(' ', from);
        return double.Parse(junctionPick[from..to], System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static AircraftState MakeAircraftAt(AirportGroundLayout layout, string airportId, GroundNode start, double headingTrue)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = start.Position,
            TrueHeading = new TrueHeading(headingTrue),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = airportId },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private AircraftState? TickUntil(SimulationEngine engine, Func<AircraftState, bool> until, int maxSeconds) =>
        TickUntil(output, engine, until, maxSeconds);

    internal static AircraftState? TickUntil(ITestOutputHelper output, SimulationEngine engine, Func<AircraftState, bool> until, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                return null;
            }

            if (until(aircraft))
            {
                output.WriteLine(
                    $"t={t}: phase={aircraft.Phases?.CurrentPhase?.GetType().Name} seg={aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}"
                );
                return aircraft;
            }
        }

        output.WriteLine($"gave up after {maxSeconds}s");
        return null;
    }

    private static bool IsRunway33BarOnTaxiway(AirportGroundLayout layout, int nodeId, string taxiway) =>
        layout.Nodes.TryGetValue(nodeId, out GroundNode? node)
        && (node.Type == GroundNodeType.RunwayHoldShort)
        && (node.RunwayId?.Contains("33") == true)
        && node.Edges.Any(e => e.MatchesTaxiway(taxiway));

    private static AircraftState MakeN36936(AirportGroundLayout layout) => MakeHoldingAfterEExit(layout, Callsign, "C150");

    /// <summary>OAK 28R's hold-short bar on E, north side: the bar an aircraft exiting 28R on E crosses.</summary>
    internal static GroundNode OakE28RBar(AirportGroundLayout layout)
    {
        GroundNode bar = Assert.IsType<GroundNode>(TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "28R", "E"));
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar.Type);
        return bar;
    }

    /// <summary>
    /// An aircraft holding after exiting 28R on E: <see cref="AfterEExitPastBarFt"/> past E's 28R bar along
    /// <see cref="AfterEExitHeading"/>, outside the bar, on taxiway E.
    /// </summary>
    internal static AircraftState MakeHoldingAfterEExit(AirportGroundLayout layout, string callsign, string type)
    {
        GroundNode bar = OakE28RBar(layout);
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = GeoMath.ProjectPoint(bar.Position, AfterEExitHeading, AfterEExitPastBarFt / GeoMath.FeetPerNm),
            TrueHeading = AfterEExitHeading,
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterExitPhase("28R", "E", bar.Id, heldShortOfRunway: null));
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "E";
        return aircraft;
    }
}
