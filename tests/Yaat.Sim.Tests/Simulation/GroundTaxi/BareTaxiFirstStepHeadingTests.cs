using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A bare <c>TAXI B</c> (only the taxiway the aircraft is already on, at most <c>EXP</c> / <c>NODEL</c>) names no destination
/// and no further taxiway, so it does nothing: it is refused with a reason, never spoken, and the aircraft stays put, wherever
/// it stands on B. At KOAK, on B at the south 28L/10R bar (facing either way, on it or just short of it, or holding short of
/// 28L), the refusal keeps it from taking the bare taxi as a route along B, across 28L and 28R.
/// </summary>
public class BareTaxiFirstStepHeadingTests(ITestOutputHelper output)
{
    /// <summary>How long an aircraft is run after the refused taxi.</summary>
    private const int HoldingShortTickSeconds = 60;

    /// <summary>
    /// How far from its start a refused aircraft may sit: it starts still, and taxiing anywhere would carry it hundreds of
    /// feet.
    /// </summary>
    private const double MaxHoldingShortCreepFt = 10.0;

    private const string RefusedTaxiB = "TAXI B rejected: already on B; give a destination or a further taxiway";

    /// <summary>
    /// <paramref name="pastBarFt"/> feet from the bar toward 28L (0 = on the bar), facing away from the runway along B.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void BareTaxiB_At28LBarFacingAway_IsRefused(double pastBarFt)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0));
        LatLon position = GeoMath.ProjectPoint(bar.Position, new TrueHeading(towardRunwayDeg), pastBarFt / GeoMath.FeetPerNm);
        output.WriteLine($"bar {bar.Id}, toward 28L {towardRunwayDeg:F1}, start {pastBarFt:F0} ft past it, heading {heading.Degrees:F1}");

        AssertBareTaxiRefused(groundData, layout, new BareTaxiStart(position, heading, new HoldingInPositionPhase()), "TAXI B");
    }

    /// <summary>
    /// <paramref name="shortOfBarFt"/> feet from the bar away from the runway (0 = on the bar), facing 28L along B.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void BareTaxiB_AtOrJustShortOfTheBarFacingTheRunway_IsRefused(double shortOfBarFt)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        double awayFromRunwayDeg = MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0);
        LatLon position = GeoMath.ProjectPoint(bar.Position, new TrueHeading(awayFromRunwayDeg), shortOfBarFt / GeoMath.FeetPerNm);
        output.WriteLine($"bar {bar.Id}, toward 28L {towardRunwayDeg:F1}, start {shortOfBarFt:F0} ft short of it, heading {heading.Degrees:F1}");

        AssertBareTaxiRefused(groundData, layout, new BareTaxiStart(position, heading, new HoldingInPositionPhase()), "TAXI B");
    }

    /// <summary>
    /// Holding short of 28L at the south B bar, facing the runway, the aircraft is given a bare <c>TAXI B</c>: it must not
    /// take that as a clearance across 28L, so it is refused and stays on the bar.
    /// </summary>
    [Fact]
    public void BareTaxiB_HoldingShortOf28LFacingIt_DoesNotCross()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        AssertBareTaxiRefused(groundData, layout, HoldingShortOf28LOnB(layout, output), "TAXI B");
    }

    /// <summary>40 ft short of the same bar on the side away from the runway, facing the runway along B.</summary>
    [Fact]
    public void BareTaxiB_ShortOfTheBarFacingTheRunway_IsRefused()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        double awayFromRunwayDeg = MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0);
        LatLon position = GeoMath.ProjectPoint(bar.Position, new TrueHeading(awayFromRunwayDeg), 40.0 / GeoMath.FeetPerNm);
        output.WriteLine($"bar {bar.Id}, toward 28L {towardRunwayDeg:F1}, start 40 ft short of it, heading {heading.Degrees:F1}");

        AssertBareTaxiRefused(groundData, layout, new BareTaxiStart(position, heading, new HoldingInPositionPhase()), "TAXI B");
    }

    /// <summary>"Without delay" adds no destination: <c>TAXI B EXP</c> from holding short of 28L on B is refused as bare.</summary>
    [Fact]
    public void TaxiBExp_OnB_IsRefused()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        AssertBareTaxiRefused(groundData, layout, HoldingShortOf28LOnB(layout, output), "TAXI B EXP");
    }

    /// <summary>
    /// A hold short or a crossing is more than the own taxiway: <c>TAXI B HS C</c> and <c>TAXI B CROSS 28R</c> from holding
    /// short of 28L on B are not bare, so the aircraft is given a route and taxis.
    /// </summary>
    [Theory]
    [InlineData("TAXI B HS C")]
    [InlineData("TAXI B CROSS 28R")]
    public void TaxiBWithAHoldShortOrACrossing_OnB_Taxis(string command)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        SimulationEngine engine = BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        BareTaxiStart start = HoldingShortOf28LOnB(layout, output);
        AircraftState aircraft = SpawnAt(layout, start.Position, start.Heading, start.Phase);
        engine.World.AddAircraft(aircraft);

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, command);

        Assert.True(taxi.Success, $"'{command}' was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.NotEmpty(route.Segments);
    }

    /// <summary>
    /// A bare <c>TAXI B</c> from another taxiway is no refusal: on C, two nodes from where C meets B and facing that junction,
    /// the aircraft taxis along C to the B/C junction node and holds in position there.
    /// </summary>
    [Fact]
    public void BareTaxiB_FromC_TaxisToTheBJunction_NotRefused()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        GroundNode junction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("B", "C"));
        GroundNode onC = junction.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C")).Select(e => e.OtherNode(junction)).MinBy(n => n.Id)!;
        List<GroundNode> back =
        [
            .. TaxiStartsOnOccupiedTaxiwayTests.NodesAhead(onC, GeoMath.BearingTo(junction.Position, onC.Position), "C").Take(2),
        ];
        Assert.True(back.Count == 2, $"C runs no further than node {onC.Id} from the B junction {junction.Id}");
        GroundNode startNode = back[1];
        var heading = new TrueHeading(GeoMath.BearingTo(startNode.Position, onC.Position));
        output.WriteLine($"B/C junction {junction.Id}, start on C node {startNode.Id}, heading {heading.Degrees:F1}");

        SimulationEngine engine = BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        AircraftState aircraft = SpawnAt(layout, startNode.Position, heading, new HoldingInPositionPhase());
        aircraft.Ground.CurrentTaxiway = "C";
        engine.World.AddAircraft(aircraft);

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' from C was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.All(route.Segments, s => Assert.Equal("C", s.TaxiwayName));
        Assert.Equal(junction.Id, route.Segments[^1].ToNodeId);

        // The aircraft stops on the junction node, not short of the intersection; stopping short of B is YAAT-1314.
        double JunctionFt() => GeoMath.DistanceNm(aircraft.Position, junction.Position) * GeoMath.FeetPerNm;
        int held = SfoGroundHarness.TickUntil(
            engine,
            () => (aircraft.Phases?.CurrentPhase is HoldingInPositionPhase) && (JunctionFt() <= AirportGroundLayout.OnTaxiEdgeMaxOffsetFt),
            300,
            null
        );
        Assert.True(
            held > 0,
            $"not holding in position at the junction after 300 s: {JunctionFt():F0} ft off, {aircraft.Phases?.CurrentPhase?.Name}"
        );
    }

    /// <summary>
    /// Issues <paramref name="command"/> to an aircraft on B at <paramref name="start"/> and asserts the bare-taxi refusal: a
    /// failed result with the reason, no route change, nothing for the pilot to say, and the aircraft still where it started
    /// after <see cref="HoldingShortTickSeconds"/>.
    /// </summary>
    private void AssertBareTaxiRefused(TestAirportGroundData groundData, AirportGroundLayout layout, BareTaxiStart start, string command)
    {
        SimulationEngine engine = BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        AircraftState aircraft = SpawnAt(layout, start.Position, start.Heading, start.Phase);
        engine.World.AddAircraft(aircraft);
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, command);

        Assert.False(taxi.Success, $"'{command}' was accepted: {taxi.Message}");
        Assert.Equal(RefusedTaxiB, taxi.Message);
        Assert.Same(routeBefore, aircraft.Ground.AssignedTaxiRoute);
        Assert.Null(taxi.PilotUnable);
        Assert.Null(taxi.PilotReadback);
        Assert.Empty(aircraft.PendingPilotTransmissions);

        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(aircraft.Position, start.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"after {HoldingShortTickSeconds} s: {movedFt:F1} ft from the start, phase {aircraft.Phases?.CurrentPhase?.GetType().Name}");
        Assert.True(movedFt <= MaxHoldingShortCreepFt, $"the aircraft is {movedFt:F1} ft from its start after {HoldingShortTickSeconds} s");
    }

    /// <summary>Where and how an aircraft stands on B before the taxi.</summary>
    internal sealed record BareTaxiStart(LatLon Position, TrueHeading Heading, Phase Phase);

    /// <summary>Holding short of 28L on the south B bar, uncleared, facing the runway.</summary>
    internal static BareTaxiStart HoldingShortOf28LOnB(AirportGroundLayout layout, ITestOutputHelper output)
    {
        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        var holding = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = bar.Id,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = bar.RunwayId?.ToString(),
            }
        );
        output.WriteLine($"bar {bar.Id} ({bar.RunwayId}), toward 28L {towardRunwayDeg:F1}, holding short on it, heading {heading.Degrees:F1}");
        return new BareTaxiStart(bar.Position, heading, holding);
    }

    /// <summary>A KOAK engine in solo training, so every pilot readback is queued as a transmission.</summary>
    internal static SimulationEngine BuildOakEngine(TestAirportGroundData groundData, AutoCrossRunwayMode autoCross) =>
        new(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-bare-taxi-b",
                ScenarioName = "OAK bare TAXI B",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = autoCross,
                SoloTrainingMode = true,
            },
        };

    /// <summary>
    /// B's 28L/10R bar south of the runway (the one farther from 28R/10L), the bar across 28L from it, and the bearing of
    /// the south bar's B edge toward 28L.
    /// </summary>
    internal static (GroundNode Bar, GroundNode FarBar, double TowardRunwayDeg) Bars28LOnB(AirportGroundLayout layout)
    {
        List<GroundNode> bars =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } rwy) && rwy.Contains("28L"))
                .Where(n => n.Edges.Any(e => e.MatchesTaxiway("B"))),
        ];
        Assert.True(bars.Count == 2, $"B has {bars.Count} 28L/10R bars, expected two (one each side)");
        GroundNode south = bars.MinBy(n => n.Position.Lat)!;
        GroundNode north = bars.MaxBy(n => n.Position.Lat)!;
        double towardNorthBar = GeoMath.BearingTo(south.Position, north.Position);
        double towardRunwayDeg = Assert.Single(
            south
                .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("B"))
                .Select(e => GeoMath.BearingTo(south.Position, e.OtherNode(south).Position)),
            b => GeoMath.AbsBearingDifference(b, towardNorthBar) < 90.0
        );
        return (south, north, towardRunwayDeg);
    }

    /// <summary>A C172 on B at <paramref name="position"/>, running <paramref name="startPhase"/>.</summary>
    internal static AircraftState SpawnAt(AirportGroundLayout layout, LatLon position, TrueHeading heading, Phase startPhase)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N1259",
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "B";
        return aircraft;
    }
}
