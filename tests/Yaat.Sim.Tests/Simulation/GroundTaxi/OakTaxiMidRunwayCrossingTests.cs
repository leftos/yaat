using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-710: N462NX (C56X) was cleared across 28L and 28R on B from the south, then given <c>TAXI C @SIG7</c> while it
/// was crossing 28R. The new route bound the crossing to 28R's north bar, so it drove over the runway and held there with
/// its body between the runway and the bar.
/// </summary>
public sealed class OakTaxiMidRunwayCrossingTests
{
    private const string Callsign = "N462NX";
    private const string TaxiToSig7 = "TAXI C @SIG7";

    /// <summary>How close (ft) to the 28R centreline the aircraft is when each TAXI is given: on the runway, between its bars.</summary>
    private const double OnTheRunwayFt = 100.0;

    public OakTaxiMidRunwayCrossingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiGivenWhileCrossing28R_FinishesClearingTheRunwayInsteadOfHoldingAtItsFarBar()
    {
        if (StartCrossing28R(OnThe28R) is not { } rig)
        {
            return;
        }

        CommandResult taxi = rig.Engine.SendCommand(Callsign, TaxiToSig7);
        Assert.True(taxi.Success, taxi.Message);

        AssertParksAtSig7WithoutHoldingAt28R(rig);
    }

    /// <summary>
    /// The same TAXI given twice: the second a second after the first, the aircraft still on 28R between its bars and now
    /// taxiing on the first TAXI's route, whose only 28R bar is the far one, already cleared. The second route keeps 28R
    /// cleared too, and the aircraft parks at SIG7 without holding short of the runway it is on.
    /// </summary>
    [Fact]
    public void SecondTaxiGivenStillOn28R_FinishesClearingTheRunwayInsteadOfHoldingAtItsFarBar()
    {
        if (StartCrossing28R(OnThe28R) is not { } rig)
        {
            return;
        }

        CommandResult first = rig.Engine.SendCommand(Callsign, TaxiToSig7);
        Assert.True(first.Success, first.Message);
        rig.Engine.TickOneSecond();

        double crossTrackFt = Abs28RCrossTrackFt(rig.Layout, rig.Aircraft);
        Assert.True(crossTrackFt < OnTheRunwayFt, $"{Callsign} was {crossTrackFt:F0} ft from the 28R centreline at the second TAXI");
        Assert.IsType<TaxiingPhase>(rig.Aircraft.Phases?.CurrentPhase);
        CommandResult second = rig.Engine.SendCommand(Callsign, TaxiToSig7);
        Assert.True(second.Success, second.Message);

        AssertParksAtSig7WithoutHoldingAt28R(rig);
    }

    /// <summary>
    /// The TAXIs given later in the crossing: the first once the C56X's nose is off the 28R pavement, so its route starts off the
    /// runway; the second as it leaves on that route, nose and tail both off the pavement, its tail still short of the north 28R
    /// bar's marking (AIM 2-3-5.a.1.a: not yet clear of the runway), the bar still ahead. The second route keeps 28R cleared, and
    /// the aircraft parks at SIG7 without holding at that bar.
    /// </summary>
    [Fact]
    public void SecondTaxiGivenOffThe28RPavementShortOfTheNorthMarking_FinishesClearingTheRunwayInsteadOfHoldingAtItsFarBar()
    {
        if (StartCrossing28R((layout, aircraft) => NoseOffThe28RPavement(aircraft) && NorthOf28R(layout, aircraft)) is not { } rig)
        {
            return;
        }

        CommandResult first = rig.Engine.SendCommand(Callsign, TaxiToSig7);
        Assert.True(first.Success, first.Message);
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("no OAK 28R in navdata");
        TaxiRoute route = Assert.IsType<TaxiRoute>(rig.Aircraft.Ground.AssignedTaxiRoute);
        Assert.DoesNotContain(route.HoldShortPoints, h => (h.TargetName?.Contains("28R") == true) && !h.IsCleared);
        GroundNode northBar = route
            .Segments.Select(s => s.FromNodeId)
            .Append(route.Segments[^1].ToNodeId)
            .Where(id => id >= 0)
            .Select(id => rig.Layout.Nodes[id])
            .First(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Contains("28R"));
        int barSegment = (northBar.Id == route.Segments[0].FromNodeId) ? 0 : route.Segments.FindIndex(s => s.ToNodeId == northBar.Id) + 1;

        Assert.True(
            TickUntil(
                rig.Engine,
                120,
                () => (route.CurrentSegmentIndex <= barSegment) && IsOffThePavementShortOfTheMarking(rig.Aircraft, runway, northBar.Position)
            ),
            $"{Callsign} never stood off the 28R pavement short of the north bar's marking with the bar ahead"
        );
        Assert.IsType<TaxiingPhase>(rig.Aircraft.Phases?.CurrentPhase);
        CommandResult second = rig.Engine.SendCommand(Callsign, TaxiToSig7);
        Assert.True(second.Success, second.Message);

        AssertParksAtSig7WithoutHoldingAt28R(rig);
    }

    /// <summary>
    /// An aircraft that holds no clearance for 28R — stopped on B between the runways, its nose at the south 28R bar, not taxiing,
    /// holding or crossing — given <c>TAXI B C D @SIG7</c>: the route holds that bar, still ahead of the aircraft (the B shape
    /// point beside the paint became the bar), and the hold-short point there stays uncleared, so the aircraft holds short of 28R.
    /// </summary>
    [Fact]
    public void SecondTaxi_FromOutsideTheRunway_BarAtOrAheadOfTheStartStaysUncleared()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        GroundNode sig7 = layout.FindSpotByName("SIG7") ?? throw new InvalidOperationException("OAK has no SIG7");
        double sig7Side = Math.Sign(Signed28RCrossTrackFt(layout, sig7.Position));
        GroundNode southBar28R =
            layout.Nodes.Values.FirstOrDefault(n =>
                (n.Type == GroundNodeType.RunwayHoldShort)
                && (n.RunwayId is { } id)
                && id.Contains("28R")
                && n.Edges.Any(e => e.MatchesTaxiway("B"))
                && (Math.Sign(Signed28RCrossTrackFt(layout, n.Position)) != sig7Side)
            ) ?? throw new InvalidOperationException("OAK has no 28R hold-short bar on B south of the runway");
        GroundRunway runway28R = layout.FindRunway("28R") ?? throw new InvalidOperationException("OAK has no 28R");
        var end1 = new LatLon(runway28R.Coordinates[0].Lat, runway28R.Coordinates[0].Lon);
        var end2 = new LatLon(runway28R.Coordinates[^1].Lat, runway28R.Coordinates[^1].Lon);
        double alongDeg = GeoMath.BearingTo(end1, end2);
        var towardRunway = new TrueHeading((alongDeg + (sig7Side * 90.0) + 360.0) % 360.0);
        double halfLengthNm = AircraftLength.ResolveFt("C56X") / 2.0 / GeoMath.FeetPerNm;
        LatLon centre = GeoMath.ProjectPoint(southBar28R.Position, new TrueHeading((towardRunway.Degrees + 180.0) % 360.0), halfLengthNm);
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped()) { Scenario = NewScenario() };
        AircraftState aircraft = SpawnHolding(engine, layout, centre, towardRunway);

        CommandResult taxi = engine.SendCommand(Callsign, "TAXI B C D @SIG7");
        Assert.True(taxi.Success, taxi.Message);

        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.True(
            (route.Segments[0].FromNodeId == southBar28R.Id) || route.Segments.Any(s => s.ToNodeId == southBar28R.Id),
            $"the route never reaches the south 28R bar #{southBar28R.Id}: {route.ToSummary()}"
        );
        HoldShortPoint barHold = Assert.Single(route.HoldShortPoints, h => h.NodeId == southBar28R.Id);
        Assert.Contains("28R", barHold.TargetName);
        Assert.False(barHold.IsCleared, $"the 28R point at #{southBar28R.Id} was cleared with no crossing clearance: {route.ToSummary()}");
    }

    /// <summary>On 28R, within <see cref="OnTheRunwayFt"/> of its centreline.</summary>
    private static bool OnThe28R(AirportGroundLayout layout, AircraftState aircraft) => Abs28RCrossTrackFt(layout, aircraft) < OnTheRunwayFt;

    /// <summary>Leaving 28R: the nose off its pavement, so a route given now starts off the runway.</summary>
    private static bool NoseOffThe28RPavement(AircraftState aircraft)
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("no OAK 28R in navdata");
        double halfLengthNm = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        return !RunwayOccupancy.IsWithinPavement(GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, halfLengthNm), runway);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/>'s nose and tail are both off <paramref name="runway"/>'s pavement with its tail, on the
    /// bar's side, nearer the centreline than the bar at <paramref name="bar"/>.
    /// </summary>
    private static bool IsOffThePavementShortOfTheMarking(AircraftState aircraft, RunwayInfo runway, LatLon bar)
    {
        double halfLengthNm = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        LatLon nose = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, halfLengthNm);
        LatLon tail = GeoMath.ProjectPoint(aircraft.Position, new TrueHeading((aircraft.TrueHeading.Degrees + 180.0) % 360.0), halfLengthNm);
        if (RunwayOccupancy.IsWithinPavement(nose, runway) || RunwayOccupancy.IsWithinPavement(tail, runway))
        {
            return false;
        }

        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var along = new TrueHeading(GeoMath.BearingTo(end1, new LatLon(runway.Lat2, runway.Lon2)));
        double barFt = GeoMath.SignedCrossTrackDistanceNm(bar, end1, along) * GeoMath.FeetPerNm;
        double tailFt = GeoMath.SignedCrossTrackDistanceNm(tail, end1, along) * GeoMath.FeetPerNm;
        return (Math.Sign(tailFt) == Math.Sign(barFt)) && (Math.Abs(tailFt) < Math.Abs(barFt));
    }

    /// <summary>The engine, the KOAK layout and N462NX, crossing 28R on B from the south.</summary>
    private sealed record Rig(SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Aircraft);

    /// <summary>
    /// Spawns N462NX at the V/B junction, taxis it up B cleared across 28L and 28R, and ticks until it is crossing 28R within
    /// <see cref="OnTheRunwayFt"/> of the centreline. Null when the KOAK layout is unavailable.
    /// </summary>
    private static Rig? StartCrossing28R(Func<AirportGroundLayout, AircraftState, bool> ready)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped()) { Scenario = NewScenario() };

        GroundNode start = layout.FindIntersectionNode("V", "B") ?? throw new InvalidOperationException("OAK has no V/B junction");
        GroundNode southBar28L =
            layout
                .Nodes.Values.Where(n =>
                    (n.Type == GroundNodeType.RunwayHoldShort)
                    && (n.RunwayId is { } id)
                    && id.Contains("28L")
                    && n.Edges.Any(e => e.MatchesTaxiway("B"))
                )
                .MinBy(n => GeoMath.DistanceNm(n.Position, start.Position))
            ?? throw new InvalidOperationException("OAK has no 28L hold-short bar on B");
        AircraftState aircraft = SpawnHolding(
            engine,
            layout,
            start.Position,
            new TrueHeading(GeoMath.BearingTo(start.Position, southBar28L.Position))
        );

        Assert.True(engine.SendCommand(Callsign, "TAXI B @SIG7 HS 28L").Success);
        Assert.True(
            TickUntil(engine, 600, () => (aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: { } t }) && t.Contains("28L"))
        );
        Assert.True(engine.SendCommand(Callsign, "CROSS").Success);
        engine.TickOneSecond();
        Assert.True(engine.SendCommand(Callsign, "CROSS").Success);

        // Mid-crossing of 28R: in the crossing phase with the aircraft on the runway, between its two bars.
        Assert.True(
            TickUntil(
                engine,
                300,
                () => (aircraft.Phases?.CurrentPhase is CrossingRunwayPhase { RunwayId: { } r }) && r.Contains("28R") && ready(layout, aircraft)
            )
        );

        return new Rig(engine, layout, aircraft);
    }

    /// <summary>Ticks until N462NX parks or holds short of 28R, and asserts it parked at SIG7 without holding.</summary>
    private static void AssertParksAtSig7WithoutHoldingAt28R(Rig rig)
    {
        AircraftState aircraft = rig.Aircraft;
        HoldShortPoint? held28R = null;
        bool parked = TickUntil(
            rig.Engine,
            600,
            () =>
            {
                if ((aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort: { TargetName: { } t } hs }) && t.Contains("28R"))
                {
                    held28R = hs;
                    return true;
                }

                return aircraft.Phases?.CurrentPhase is AtParkingPhase;
            }
        );

        Assert.True(parked, $"{Callsign} neither parked nor held within 600 s of the TAXI");
        Assert.True(
            held28R is null,
            $"{Callsign} held short of 28R at node {held28R?.NodeId} with its centre {Abs28RCrossTrackFt(rig.Layout, aircraft):F0} ft from the "
                + "centreline, on the far side of a runway it was already crossing"
        );
        Assert.Equal("SIG7", aircraft.Ground.ParkingSpot);
    }

    private static double Abs28RCrossTrackFt(AirportGroundLayout layout, AircraftState aircraft) =>
        Math.Abs(Signed28RCrossTrackFt(layout, aircraft.Position));

    /// <summary>On SIG7's side of 28R: the aircraft's cross-track from the 28R centreline has the sign of SIG7's.</summary>
    private static bool NorthOf28R(AirportGroundLayout layout, AircraftState aircraft)
    {
        GroundNode sig7 = layout.FindSpotByName("SIG7") ?? throw new InvalidOperationException("OAK has no SIG7");
        return Math.Sign(Signed28RCrossTrackFt(layout, aircraft.Position)) == Math.Sign(Signed28RCrossTrackFt(layout, sig7.Position));
    }

    private static double Signed28RCrossTrackFt(AirportGroundLayout layout, LatLon point)
    {
        GroundRunway runway = layout.FindRunway("28R") ?? throw new InvalidOperationException("OAK has no 28R");
        var end1 = new LatLon(runway.Coordinates[0].Lat, runway.Coordinates[0].Lon);
        var end2 = new LatLon(runway.Coordinates[^1].Lat, runway.Coordinates[^1].Lon);
        return GeoMath.SignedCrossTrackDistanceNm(point, end1, new TrueHeading(GeoMath.BearingTo(end1, end2))) * GeoMath.FeetPerNm;
    }

    private static SimScenarioState NewScenario() =>
        new()
        {
            ScenarioId = "yaat-710",
            ScenarioName = "YAAT-710 TAXI mid-crossing",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

    private static AircraftState SpawnHolding(SimulationEngine engine, AirportGroundLayout layout, LatLon position, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C56X",
            Position = position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSNA",
                Destination = "KOAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(41000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static bool TickUntil(SimulationEngine engine, int maxSeconds, Func<bool> done)
    {
        for (int second = 0; second < maxSeconds; second++)
        {
            engine.TickOneSecond();
            if (done())
            {
                return true;
            }
        }

        return false;
    }
}
