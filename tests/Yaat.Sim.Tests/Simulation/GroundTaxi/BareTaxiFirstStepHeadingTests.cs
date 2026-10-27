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
/// A bare <c>TAXI B</c> (one taxiway, no destination, no hint) starts the way the aircraft faces. At KOAK, an aircraft on
/// B at the south 28L/10R bar, facing away from the runways (about 248°), taxis west along B. Starting just on the runway
/// side of the bar, the start node is the B node 12 ft past it; the step back over the bar paid the 0.3 nm runway-crossing
/// penalty and lost to the step east (behind the aircraft, 172° off its heading), and the route crossed 28L and 28R.
/// </summary>
public class BareTaxiFirstStepHeadingTests(ITestOutputHelper output)
{
    /// <summary>The first route step departs within this many degrees of the aircraft's heading.</summary>
    private const double MaxFirstStepOffHeadingDeg = 90.0;

    /// <summary>How long an aircraft holding short is run after the bare taxi.</summary>
    private const int HoldingShortTickSeconds = 60;

    /// <summary>
    /// How far past the bar an aircraft holding short there may sit: it starts on the bar, and crossing 28L would carry it
    /// hundreds of feet.
    /// </summary>
    private const double MaxHoldingShortCreepFt = 10.0;

    /// <summary>
    /// <paramref name="pastBarFt"/> feet from the bar toward 28L (0 = on the bar), facing away from the runway along B.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void BareTaxiB_At28LBarFacingAway_TaxisAlongItsHeading(double pastBarFt)
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

        TaxiRoute route = IssueBareTaxiB(groundData, layout, position, heading, new HoldingInPositionPhase()).Route;
        AssertFirstStepAlongHeading(layout, route, heading);

        List<int> runwayBars = [.. route.Segments.Select(s => s.ToNodeId).Where(id => layout.Nodes[id].Type == GroundNodeType.RunwayHoldShort)];
        Assert.False(
            runwayBars.Any(id => (layout.Nodes[id].RunwayId is { } rwy) && rwy.Contains("28R")),
            $"the route crosses bars [{string.Join(", ", runwayBars)}], 28R/10L among them"
        );
    }

    /// <summary>
    /// <paramref name="shortOfBarFt"/> feet from the bar away from the runway (0 = on the bar), facing 28L along B: a bare
    /// <c>TAXI B</c> either holds short of 28L at the bar, uncleared, or does not enter 28L at all.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void BareTaxiB_AtOrJustShortOfTheBarFacingTheRunway_HoldsShortOrStaysOff28L(double shortOfBarFt)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, GroundNode farBar, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        double awayFromRunwayDeg = MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0);
        LatLon position = GeoMath.ProjectPoint(bar.Position, new TrueHeading(awayFromRunwayDeg), shortOfBarFt / GeoMath.FeetPerNm);
        output.WriteLine($"bar {bar.Id}, toward 28L {towardRunwayDeg:F1}, start {shortOfBarFt:F0} ft short of it, heading {heading.Degrees:F1}");

        TaxiRoute route = IssueBareTaxiB(groundData, layout, position, heading, new HoldingInPositionPhase()).Route;

        bool heldAtBar = route.GetHoldShortAt(bar.Id) is { IsCleared: false };
        bool enters28L = route.Segments.Any(s => s.ToNodeId == farBar.Id);
        Assert.True(
            heldAtBar || !enters28L,
            $"the route enters 28L (reaches far bar {farBar.Id}) with no uncleared hold at bar {bar.Id}; holds: {SfoGroundHarness.FormatActual(route.HoldShortPoints)}"
        );
    }

    /// <summary>
    /// Holding short of 28L at the south B bar, facing the runway, the aircraft is given a bare <c>TAXI B</c>: it must not
    /// take that as a clearance across 28L.
    /// </summary>
    [Fact(Skip = "YAAT-1282: a bare TAXI from holding short implicitly clears the held runway (pre-existing on main)")]
    public void BareTaxiB_HoldingShortOf28LFacingIt_DoesNotCross()
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
        var holding = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = bar.Id,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = bar.RunwayId?.ToString(),
            }
        );
        output.WriteLine($"bar {bar.Id} ({bar.RunwayId}), toward 28L {towardRunwayDeg:F1}, holding short on it, heading {heading.Degrees:F1}");

        (SimulationEngine engine, AircraftState aircraft, TaxiRoute route) = IssueBareTaxiB(groundData, layout, bar.Position, heading, holding);
        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double pastBarFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, bar.Position, heading) * GeoMath.FeetPerNm;
        output.WriteLine($"after {HoldingShortTickSeconds} s: {pastBarFt:F1} ft past the bar, phase {aircraft.Phases?.CurrentPhase?.GetType().Name}");
        List<HoldShortPoint> cleared28L = [.. route.HoldShortPoints.Where(h => (h.TargetName is { } rwy) && rwy.Contains("28L") && h.IsCleared)];
        Assert.True(cleared28L.Count == 0, $"the route clears 28L; holds: {SfoGroundHarness.FormatActual(route.HoldShortPoints)}");
        Assert.True(pastBarFt <= MaxHoldingShortCreepFt, $"the aircraft is {pastBarFt:F1} ft past the 28L bar after {HoldingShortTickSeconds} s");
    }

    /// <summary>
    /// 40 ft short of the same bar on the side away from the runway, facing the runway along B: a bare <c>TAXI B</c>
    /// continues ahead and holds short at the bar instead of turning around.
    /// </summary>
    [Fact]
    public void BareTaxiB_ShortOfTheBarFacingTheRunway_ContinuesAheadAndHoldsAtTheBar()
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

        TaxiRoute route = IssueBareTaxiB(groundData, layout, position, heading, new HoldingInPositionPhase()).Route;
        AssertFirstStepAlongHeading(layout, route, heading);

        HoldShortPoint? hold = route.GetHoldShortAt(bar.Id);
        Assert.True(
            hold is { IsCleared: false },
            $"the route does not hold short at bar {bar.Id}; holds: {SfoGroundHarness.FormatActual(route.HoldShortPoints)}"
        );
    }

    private (SimulationEngine Engine, AircraftState Aircraft, TaxiRoute Route) IssueBareTaxiB(
        TestAirportGroundData groundData,
        AirportGroundLayout layout,
        LatLon position,
        TrueHeading heading,
        Phase startPhase
    )
    {
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-bare-taxi-b-heading",
                ScenarioName = "OAK bare TAXI B heading",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        AircraftState aircraft = SpawnAt(layout, position, heading, startPhase);
        engine.World.AddAircraft(aircraft);

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        return (engine, aircraft, route);
    }

    private static void AssertFirstStepAlongHeading(AirportGroundLayout layout, TaxiRoute route, TrueHeading heading)
    {
        TaxiRouteSegment first = route.Segments[0];
        double firstStepDeg = GeoMath.BearingTo(layout.Nodes[first.FromNodeId].Position, layout.Nodes[first.ToNodeId].Position);
        double offHeadingDeg = GeoMath.AbsBearingDifference(firstStepDeg, heading.Degrees);
        Assert.True(
            offHeadingDeg <= MaxFirstStepOffHeadingDeg,
            $"the route starts {first.FromNodeId} -> {first.ToNodeId} at {firstStepDeg:F1}, {offHeadingDeg:F1} off the {heading.Degrees:F1} heading"
        );
    }

    /// <summary>
    /// B's 28L/10R bar south of the runway (the one farther from 28R/10L), the bar across 28L from it, and the bearing of
    /// the south bar's B edge toward 28L.
    /// </summary>
    private static (GroundNode Bar, GroundNode FarBar, double TowardRunwayDeg) Bars28LOnB(AirportGroundLayout layout)
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

    private static AircraftState SpawnAt(AirportGroundLayout layout, LatLon position, TrueHeading heading, Phase startPhase)
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
