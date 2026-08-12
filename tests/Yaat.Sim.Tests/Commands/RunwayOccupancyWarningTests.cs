using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// 7110.65 3-9-4 runway-occupancy advisories (issue #346). At airports without a full
/// safety logic system (no vNAS ASDE-X runway configurations), clearing an aircraft for a
/// full-stop, touch-and-go, stop-and-go, low approach, or option while another aircraft is
/// holding in position (or taxiing to LUAW) on the same runway warns the RPO — and the
/// reverse: authorizing LUAW while an arrival holds one of those clearances warns too.
/// The warnings are non-blocking advisories on <see cref="AircraftState.PendingWarnings"/>;
/// the command itself still succeeds.
/// </summary>
[Collection("NavDbMutator")]
public class RunwayOccupancyWarningTests
{
    public RunwayOccupancyWarningTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RunwayInfo Rwy(string designator = "28R", string airportId = "OAK") =>
        TestRunwayFactory.Make(designator: designator, airportId: airportId, heading: 280, elevationFt: 6);

    /// <summary>Airborne arrival established for <paramref name="runway"/> with a pending landing — accepts CLAND/COPT/TG/SG/LA/CLANDF.</summary>
    private static AircraftState MakeArrival(string callsign, RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = new LatLon(37.75, -122.15),
            TrueHeading = new TrueHeading(280),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(new LandingPhase());
        return ac;
    }

    /// <summary>Ground aircraft claiming <paramref name="runway"/> via the given phase chain (first phase becomes current).</summary>
    private static AircraftState MakeOccupant(string callsign, RunwayInfo runway, params Phase[] phases)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        foreach (var phase in phases)
        {
            ac.Phases.Add(phase);
        }
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    private static AircraftState MakeDepartureHoldingShort(string callsign, string runway = "30/12")
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = runway,
                }
            )
        );
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    private static CommandResult Dispatch(string input, AircraftState target, params AircraftState[] world)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var ctx = TestDispatch.Context(Random.Shared) with { ListAircraft = () => world };
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        return CommandDispatcher.DispatchCompound(compound, target, ctx);
    }

    private static CommandResult DispatchWithConfig(string input, AircraftState target, ArtccConfigRoot artccConfig, params AircraftState[] world)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var ctx = TestDispatch.Context(Random.Shared, artccConfig: artccConfig) with { ListAircraft = () => world };
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        return CommandDispatcher.DispatchCompound(compound, target, ctx);
    }

    // -------------------------------------------------------------------------
    // Forward: landing-family clearance onto an occupied runway
    // -------------------------------------------------------------------------

    [Fact]
    public void Cland_LuawOnSameRunway_SucceedsAndWarnsInstructor()
    {
        var runway = Rwy();
        var occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy());

        var result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("3-9-4", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("COPT")]
    [InlineData("TG")]
    [InlineData("SG")]
    [InlineData("LA")]
    [InlineData("CLANDF")]
    public void OtherLandingFamilyClearances_LuawOnSameRunway_Warn(string command)
    {
        var runway = Rwy();
        var occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy());

        var result = Dispatch(command, arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantHoldingInPosition_Warns()
    {
        var runway = Rwy();
        var occupant = MakeOccupant("N100LU", runway, new HoldingInPositionPhase());
        var arrival = MakeArrival("N200AR", Rwy());

        Dispatch("CLAND", arrival, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantHoldingInPositionAwayFromRunway_NoWarning()
    {
        // HoldingInPositionPhase doubles as YAAT's generic ground-idle hold (post-WARPG anywhere,
        // far side of a crossing, CLRWY) — an aircraft holding somewhere else on the field, even
        // with this runway assigned, is not a 3-9-4 occupant.
        var runway = Rwy();
        var occupant = MakeOccupant("N100LU", runway, new HoldingInPositionPhase());
        occupant.Position = new LatLon(runway.ThresholdLatitude + 0.05, runway.ThresholdLongitude); // ~3 nm off the pavement
        var arrival = MakeArrival("N200AR", Rwy());

        var result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantTaxiingIntoPosition_Warns()
    {
        // "…or taxiing to LUAW" — a LineUpPhase aircraft en route to the centerline claims the
        // runway just as much as one already stopped on it. The realistic LUAW phase shape is
        // LineUp → LinedUpAndWaiting (with no takeoff clearance yet).
        var runway = Rwy();
        var occupant = MakeOccupant("N100LU", runway, new LineUpPhase(), new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy());

        Dispatch("CLAND", arrival, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantLinedUpWithTakeoffClearanceInHand_NoWarning()
    {
        // 3-9-4's restriction covers an aircraft *holding* in position — one that already has its
        // takeoff clearance is about to roll, and clearing an arrival behind it is ordinary
        // anticipated separation (7110.65 3-9-5 / 3-10-6).
        var runway = Rwy();
        var luaw = new LinedUpAndWaitingPhase();
        var occupant = MakeOccupant("N100LU", runway, luaw);
        luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);
        var arrival = MakeArrival("N200AR", Rwy());

        var result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantLiningUpForRollingTakeoff_NoWarning()
    {
        // A rolling CTO installs LineUp → Takeoff with no LUAW between: the clearance is in hand
        // while the aircraft taxis into position, so no advisory.
        var runway = Rwy();
        var occupant = MakeOccupant("N100LU", runway, new LineUpPhase(), new TakeoffPhase());
        var arrival = MakeArrival("N200AR", Rwy());

        var result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantOnTakeoffRoll_NoWarning()
    {
        // The 3-9-4 restriction ends when the aircraft in position "starts takeoff roll".
        var runway = Rwy();
        var occupant = MakeOccupant("N100LU", runway, new TakeoffPhase());
        var arrival = MakeArrival("N200AR", Rwy());

        var result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantOnDifferentRunway_NoWarning()
    {
        var occupant = MakeOccupant("N100LU", Rwy(designator: "33"), new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy());

        var result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantLinedUpOppositeEndOfSamePavement_Warns()
    {
        // Runway identity, not designator equality: an aircraft lined up on 10L occupies the
        // same pavement an arrival is being cleared onto as 28R.
        var occupant = MakeOccupant("N100LU", Rwy(designator: "10L"), new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy(designator: "28R"));

        Dispatch("CLAND", arrival, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantAtDifferentAirport_NoWarning()
    {
        var occupant = MakeOccupant("N100LU", Rwy(airportId: "SFO"), new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy(airportId: "OAK"));

        var result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Reverse: LUAW while an arrival holds a landing-family clearance
    // -------------------------------------------------------------------------

    [Fact]
    public void Luaw_ArrivalClearedToLandSameRunway_SucceedsAndWarnsInstructor()
    {
        var runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        var arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        var departure = MakeDepartureHoldingShort("N300DP");

        var result = Dispatch("LUAW", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.Contains(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(departure.PendingWarnings, w => w.Contains("3-9-4", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ClearanceType.ClearedForOption)]
    [InlineData(ClearanceType.ClearedTouchAndGo)]
    [InlineData(ClearanceType.ClearedStopAndGo)]
    [InlineData(ClearanceType.ClearedLowApproach)]
    public void Luaw_ArrivalHoldsOtherLandingFamilyClearance_Warns(ClearanceType clearance)
    {
        var runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        var arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = clearance;
        arrival.Phases.ClearedRunwayId = "30";
        var departure = MakeDepartureHoldingShort("N300DP");

        Dispatch("LUAW", departure, arrival, departure);

        Assert.Contains(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Luaw_ArrivalNotYetCleared_NoWarning()
    {
        var runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        var arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        var departure = MakeDepartureHoldingShort("N300DP");

        var result = Dispatch("LUAW", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Luaw_ArrivalLandedAndExiting_NoWarning()
    {
        // Nothing nulls PhaseList.LandingClearance at touchdown, so a full-stop arrival keeps it
        // while exiting and taxiing in — that stale clearance must not re-trigger the advisory on
        // every later LUAW for the runway.
        var runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        var arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        arrival.IsOnGround = true;
        arrival.Phases.Phases.Clear();
        arrival.Phases.Add(new HoldingAfterExitPhase());
        arrival.Phases.Start(CommandDispatcher.BuildMinimalContext(arrival));
        var departure = MakeDepartureHoldingShort("N300DP");

        var result = Dispatch("LUAW", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cto_ArrivalClearedToLandSameRunway_NoWarning()
    {
        // Takeoff clearance with an arrival cleared to land is anticipated separation — legal,
        // and outside the 3-9-4 LUAW restriction.
        var runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        var arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        var departure = MakeDepartureHoldingShort("N300DP");

        var result = Dispatch("CTO", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Safety-logic gate: airports whose vNAS ASDE-X config carries runway
    // configurations have a full safety logic system — no advisory there.
    // -------------------------------------------------------------------------

    [Fact]
    public void Cland_AtSafetyLogicAirport_NoWarning()
    {
        var zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return; // snapshot absent — skip silently, matching the TestVnasData pattern
        }

        var occupant = MakeOccupant("N100LU", Rwy(airportId: "SFO"), new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy(airportId: "SFO"));

        var result = DispatchWithConfig("CLAND", arrival, zoa, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_AtAirportWithoutSafetyLogicRunwayConfigs_StillWarns()
    {
        // OAK's facility carries no ASDE-X runway configurations (in the live ZOA config its
        // ASDE-X is display-only) — safety logic cannot run there, so the advisory still applies.
        var zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var occupant = MakeOccupant("N100LU", Rwy(airportId: "OAK"), new LinedUpAndWaitingPhase());
        var arrival = MakeArrival("N200AR", Rwy(airportId: "OAK"));

        DispatchWithConfig("CLAND", arrival, zoa, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SFO", true)]
    [InlineData("KSFO", true)] // ICAO form resolves via the leading-K strip
    [InlineData("OAK", false)] // facility present but no ASDE-X runway configurations in the snapshot
    [InlineData("KOAK", false)]
    [InlineData("XYZ", false)] // unknown facility — no positive evidence of safety logic
    [InlineData("", false)]
    public void AirportHasFullSafetyLogic_ResolvesFromAsdexRunwayConfigurations(string airportId, bool expected)
    {
        var zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        Assert.Equal(expected, zoa.AirportHasFullSafetyLogic(airportId));
    }
}
