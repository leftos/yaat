using System.Collections.Immutable;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.Asdex;

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
            Phases = new PhaseList { AssignedRunway = runway },
        };
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
            Phases = new PhaseList { AssignedRunway = runway },
        };
        foreach (Phase phase in phases)
        {
            ac.Phases.Add(phase);
        }
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
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
            Phases = new PhaseList(),
        };
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
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        return ac;
    }

    private static CommandResult Dispatch(string input, AircraftState target, params AircraftState[] world)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext ctx = TestDispatch.Context(Random.Shared) with { ListAircraft = () => world };
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        return CommandDispatcher.DispatchCompound(compound, target, ctx);
    }

    private static CommandResult DispatchWithConfig(string input, AircraftState target, ArtccConfigRoot artccConfig, params AircraftState[] world) =>
        DispatchWithLiveConfigs(input, target, artccConfig, LoadSeed(artccConfig), world);

    private static CommandResult DispatchWithLiveConfigs(
        string input,
        AircraftState target,
        ArtccConfigRoot artccConfig,
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> liveConfigs,
        params AircraftState[] world
    )
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext ctx = TestDispatch.Context(Random.Shared, artccConfig: artccConfig, asdexSafetyLogicConfigs: liveConfigs) with
        {
            ListAircraft = () => world,
        };
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        return CommandDispatcher.DispatchCompound(compound, target, ctx);
    }

    /// <summary>The ASDE-X Safety Logic configuration a scenario load starts every facility on, with no active runways.</summary>
    private static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> LoadSeed(ArtccConfigRoot artccConfig) =>
        AsdexSafetyLogicSeed.Build(artccConfig, NavigationDatabase.Instance, ActiveRunways.Empty);

    // -------------------------------------------------------------------------
    // Forward: landing-family clearance onto an occupied runway
    // -------------------------------------------------------------------------

    [Fact]
    public void Cland_LuawOnSameRunway_SucceedsAndWarnsInstructor()
    {
        RunwayInfo runway = Rwy();
        AircraftState occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        CommandResult result = Dispatch("CLAND", arrival, occupant, arrival);

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
        RunwayInfo runway = Rwy();
        AircraftState occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        CommandResult result = Dispatch(command, arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantHoldingInPosition_Warns()
    {
        RunwayInfo runway = Rwy();
        AircraftState occupant = MakeOccupant("N100LU", runway, new HoldingInPositionPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        Dispatch("CLAND", arrival, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantHoldingInPositionAwayFromRunway_NoWarning()
    {
        // HoldingInPositionPhase doubles as YAAT's generic ground-idle hold (post-WARPG anywhere,
        // far side of a crossing, CLRWY) — an aircraft holding somewhere else on the field, even
        // with this runway assigned, is not a 3-9-4 occupant.
        RunwayInfo runway = Rwy();
        AircraftState occupant = MakeOccupant("N100LU", runway, new HoldingInPositionPhase());
        occupant.Position = new LatLon(runway.ThresholdLatitude + 0.05, runway.ThresholdLongitude); // ~3 nm off the pavement
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        CommandResult result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantTaxiingIntoPosition_Warns()
    {
        // "…or taxiing to LUAW" — a LineUpPhase aircraft en route to the centerline claims the
        // runway just as much as one already stopped on it. The realistic LUAW phase shape is
        // LineUp → LinedUpAndWaiting (with no takeoff clearance yet).
        RunwayInfo runway = Rwy();
        AircraftState occupant = MakeOccupant("N100LU", runway, new LineUpPhase(), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        Dispatch("CLAND", arrival, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantLinedUpWithTakeoffClearanceInHand_NoWarning()
    {
        // 3-9-4's restriction covers an aircraft *holding* in position — one that already has its
        // takeoff clearance is about to roll, and clearing an arrival behind it is ordinary
        // anticipated separation (7110.65 3-9-5 / 3-10-6).
        RunwayInfo runway = Rwy();
        var luaw = new LinedUpAndWaitingPhase();
        AircraftState occupant = MakeOccupant("N100LU", runway, luaw);
        luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        CommandResult result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantLiningUpForRollingTakeoff_NoWarning()
    {
        // A rolling CTO installs LineUp → Takeoff with no LUAW between: the clearance is in hand
        // while the aircraft taxis into position, so no advisory.
        RunwayInfo runway = Rwy();
        AircraftState occupant = MakeOccupant("N100LU", runway, new LineUpPhase(), new TakeoffPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        CommandResult result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantOnTakeoffRoll_NoWarning()
    {
        // The 3-9-4 restriction ends when the aircraft in position "starts takeoff roll".
        RunwayInfo runway = Rwy();
        AircraftState occupant = MakeOccupant("N100LU", runway, new TakeoffPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        CommandResult result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantOnDifferentRunway_NoWarning()
    {
        AircraftState occupant = MakeOccupant("N100LU", Rwy(designator: "33"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy());

        CommandResult result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantLinedUpOppositeEndOfSamePavement_Warns()
    {
        // Runway identity, not designator equality: an aircraft lined up on 10L occupies the
        // same pavement an arrival is being cleared onto as 28R.
        AircraftState occupant = MakeOccupant("N100LU", Rwy(designator: "10L"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy(designator: "28R"));

        Dispatch("CLAND", arrival, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_OccupantAtDifferentAirport_NoWarning()
    {
        AircraftState occupant = MakeOccupant("N100LU", Rwy(airportId: "SFO"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy(airportId: "OAK"));

        CommandResult result = Dispatch("CLAND", arrival, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Reverse: LUAW while an arrival holds a landing-family clearance
    // -------------------------------------------------------------------------

    [Fact]
    public void Luaw_ArrivalClearedToLandSameRunway_SucceedsAndWarnsInstructor()
    {
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("LUAW", departure, arrival, departure);

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
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = clearance;
        arrival.Phases.ClearedRunwayId = "30";
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        Dispatch("LUAW", departure, arrival, departure);

        Assert.Contains(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Luaw_ArrivalNotYetCleared_NoWarning()
    {
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("LUAW", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Luaw_ArrivalLandedAndExiting_NoWarning()
    {
        // Nothing nulls PhaseList.LandingClearance at touchdown, so a full-stop arrival keeps it
        // while exiting and taxiing in — that stale clearance must not re-trigger the advisory on
        // every later LUAW for the runway.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        arrival.IsOnGround = true;
        arrival.Phases.Phases.Clear();
        arrival.Phases.Add(new HoldingAfterExitPhase());
        arrival.Phases.Start(CommandDispatcher.BuildMinimalContext(arrival, ActiveRunways.Empty));
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("LUAW", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Arrival that holds <paramref name="clearance"/> for runway 30 and is now on the runway surface in
    /// <paramref name="onRunwayPhase"/> — touched down, but not yet exiting (the phase list still sits on
    /// the landing-family phase).
    /// </summary>
    private static AircraftState MakeTouchedDownArrival(string callsign, ClearanceType clearance, Phase onRunwayPhase)
    {
        AircraftState arrival = MakeArrival(callsign, TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = clearance;
        arrival.Phases.ClearedRunwayId = "30";
        arrival.Phases.Phases.Clear();
        arrival.Phases.Add(onRunwayPhase);
        arrival.Phases.Start(CommandDispatcher.BuildMinimalContext(arrival, ActiveRunways.Empty));
        arrival.IsOnGround = true;
        arrival.Altitude = 6;
        arrival.IndicatedAirspeed = 110;
        return arrival;
    }

    [Fact]
    public void Luaw_ArrivalTouchedDownStillInLandingPhase_NoWarning()
    {
        // 7110.65 3-9-4.c.1(b) restricts LUAW while an aircraft is CLEARED for a full-stop etc. on the
        // runway — i.e. still inbound. Once it has touched down it is the 3-9-4.a "aircraft which has
        // landed on ... the runway" that LUAW is explicitly authorized behind. The rollout keeps the
        // aircraft in LandingPhase until it commits to an exit, so the phase alone cannot decide.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeTouchedDownArrival("N200AR", ClearanceType.ClearedToLand, new LandingPhase());
        Assert.IsType<LandingPhase>(arrival.Phases!.CurrentPhase);
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("LUAW", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<object[]> TouchedDownLandingFamilyCases() =>
        [
            [ClearanceType.ClearedTouchAndGo, new TouchAndGoPhase()],
            [ClearanceType.ClearedStopAndGo, new StopAndGoPhase()],
            [ClearanceType.ClearedForOption, new TouchAndGoPhase()],
            [ClearanceType.ClearedToLand, new HelicopterLandingPhase()],
        ];

    [Theory]
    [MemberData(nameof(TouchedDownLandingFamilyCases))]
    public void Luaw_ArrivalTouchedDownOnOtherLandingFamilyClearance_NoWarning(ClearanceType clearance, Phase onRunwayPhase)
    {
        // A rolling touch-and-go or a stopped stop-and-go is 3-9-4.a's "aircraft which has landed on
        // ... the runway" like any other; 3-9-6.b at takeoff-clearance time is what then protects the
        // LUAW aircraft behind it.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeTouchedDownArrival("N200AR", clearance, onRunwayPhase);
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        Dispatch("LUAW", departure, arrival, departure);

        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Luaw_ArrivalInFlareStillAirborne_Warns()
    {
        // The boundary is touchdown, not the phase: an arrival already in LandingPhase but still
        // airborne over the runway (flare) is still cleared to land, and 3-9-4.c.1(b) still applies.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeTouchedDownArrival("N200AR", ClearanceType.ClearedToLand, new LandingPhase());
        arrival.IsOnGround = false;
        arrival.Altitude = 30;
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        Dispatch("LUAW", departure, arrival, departure);

        Assert.Contains(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(departure.PendingWarnings, w => w.Contains("3-9-4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Luaw_ArrivalWentAround_NoWarning()
    {
        // A go-around voids the landing clearance — 7110.65 3-9-4.c.1.(b) restricts LUAW only
        // while a clearance for the runway still stands, and a gone-around aircraft must be
        // re-cleared to land. The stale clearance must not re-trigger the advisory just because
        // the aircraft is still airborne on the climb-out (issue #353); airborne analog of
        // Luaw_ArrivalLandedAndExiting_NoWarning.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        GoAroundHelper.InstallGoAroundPhases(CommandDispatcher.BuildMinimalContext(arrival, ActiveRunways.Empty), new GoAroundPhase(), []);
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("LUAW", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InstallGoAroundPhases_NonVisualApproach_VoidsLandingClearance()
    {
        // Every go-around voids the standing landing clearance and any pre-armed pending one,
        // not just visual approaches — the RPO must re-clear each approach.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        arrival.Pattern.PendingLandingClearance = new PendingLandingClearance(ClearanceType.ClearedToLand, "30", null, null);

        GoAroundHelper.InstallGoAroundPhases(CommandDispatcher.BuildMinimalContext(arrival, ActiveRunways.Empty), new GoAroundPhase(), []);

        Assert.Null(arrival.Phases.LandingClearance);
        Assert.Null(arrival.Phases.ClearedRunwayId);
        Assert.Null(arrival.Pattern.PendingLandingClearance);
    }

    [Fact]
    public void Cto_ArrivalClearedToLandSameRunway_NoWarning()
    {
        // Takeoff clearance with an arrival cleared to land is anticipated separation — legal,
        // and outside the 3-9-4 LUAW restriction.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6));
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("CTO", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cto_ArrivalTouchedDownStillRollingOnRunway_Warns396b()
    {
        // 7110.65 3-9-6.b: a departure may not begin its takeoff roll until the preceding landing
        // aircraft is clear of the runway. Touchdown ends the arrival's 3-9-4.c hold on LUAW, so this
        // advisory at takeoff-clearance time is what protects the departure behind a rollout.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeTouchedDownArrival("N200AR", ClearanceType.ClearedToLand, new LandingPhase());
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("CTO", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("3-9-6", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Cto_ArrivalAirborneOverTheRunway_Warns396b()
    {
        // Still airborne over the pavement — the arrival is landing on it; 3-9-6.b by geometry, not phase alone.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeArrival("N200AR", runway);
        arrival.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        arrival.Phases.ClearedRunwayId = "30";
        arrival.Position = new LatLon((runway.Lat1 + runway.Lat2) / 2.0, (runway.Lon1 + runway.Lon2) / 2.0);
        arrival.TrueHeading = new TrueHeading(310);
        arrival.Altitude = 30;
        arrival.IndicatedAirspeed = 65;
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("CTO", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("3-9-6.b", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Cto_ArrivalStoppedOnRunwayForStopAndGo_WarnsOnce()
    {
        // A stopped stop-and-go is a preceding landing aircraft not yet clear of the runway (3-9-6.b);
        // it carries no hold directive, so only the landed-traffic advisory names it — once.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState arrival = MakeTouchedDownArrival("N200AR", ClearanceType.ClearedStopAndGo, new StopAndGoPhase());
        arrival.IndicatedAirspeed = 0;
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("CTO", departure, arrival, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("3-9-6", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CTO warning for a departure (B738, Category III) behind N200AR (C172) airborne 1,500 ft ahead: 3-9-6.a.4 asks for
    /// 6,000 ft with a Category III on either side.
    /// </summary>
    private const string DepartingAheadWarning =
        "N200AR departing ahead on runway 30, 1,500 ft ahead — this departure cannot begin takeoff roll until that traffic is airborne 6,000 ft ahead or past the runway end (7110.65 3-9-6.a)";

    /// <summary>
    /// A synthetic runway 30 (true 310) at OAK's field elevation, 10,000 ft long, so the 3-9-6.a runway end lies beyond the
    /// 6,000 ft distance.
    /// </summary>
    private static RunwayInfo TenThousandFootRunway30()
    {
        var threshold = new LatLon(37.0, -122.0);
        LatLon end = GeoMath.ProjectPoint(threshold, new TrueHeading(310), 10_000 / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "30",
            airportId: "OAK",
            thresholdLat: threshold.Lat,
            thresholdLon: threshold.Lon,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 310,
            elevationFt: 6
        );
    }

    /// <summary>
    /// An aircraft flying <paramref name="phase"/> under <paramref name="clearance"/> (a touch-and-go that has lifted off, a
    /// low approach) airborne <paramref name="aheadFt"/> down runway 30 from <paramref name="departure"/>'s position (moved
    /// to the threshold), climbing out.
    /// </summary>
    private static AircraftState MakeAirborneOptionAhead(
        AircraftState departure,
        RunwayInfo runway,
        double aheadFt,
        ClearanceType clearance,
        Phase phase
    )
    {
        departure.Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        AircraftState touchAndGo = MakeTouchedDownArrival("N200AR", clearance, phase);
        touchAndGo.IsOnGround = false;
        touchAndGo.Position = GeoMath.ProjectPoint(departure.Position, runway.TrueHeading, aheadFt / GeoMath.FeetPerNm);
        touchAndGo.TrueHeading = runway.TrueHeading;
        touchAndGo.TrueTrack = runway.TrueHeading;
        touchAndGo.Altitude = 200;
        touchAndGo.IndicatedAirspeed = 70;
        touchAndGo.VerticalSpeed = 700;
        return touchAndGo;
    }

    [Fact]
    public void Cto_BehindAirborneTouchAndGoInside396a_WarnsDepartingAhead()
    {
        // 7110.65 3-8-2: once airborne a touch-and-go is a departure, so the takeoff behind it is 3-9-6.a: a Category III
        // departure may not begin its roll until the preceding departure is airborne and 6,000 ft down or past the runway end.
        RunwayInfo runway = TenThousandFootRunway30();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState departure = MakeDepartureHoldingShort("N300DP");
        AircraftState touchAndGo = MakeAirborneOptionAhead(
            departure,
            runway,
            1500,
            ClearanceType.ClearedTouchAndGo,
            LiftedOffOptionPhases.TouchAndGo(runway)
        );

        CommandResult result = Dispatch("CTO", departure, touchAndGo, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DepartingAheadWarning, warning);
    }

    [Fact]
    public void Cto_TouchAndGoAirborneInboundOverTheRunway_WarnsOppositeDirection()
    {
        // A lifted-off touch-and-go flying the opposite way over the runway is inbound toward the departure: no 3-9-6.a distance
        // credits it, since every one is for traffic running away down the runway.
        RunwayInfo runway = TenThousandFootRunway30();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState departure = MakeDepartureHoldingShort("N300DP");
        AircraftState touchAndGo = MakeAirborneOptionAhead(
            departure,
            runway,
            1500,
            ClearanceType.ClearedTouchAndGo,
            LiftedOffOptionPhases.TouchAndGo(runway)
        );
        touchAndGo.TrueHeading = runway.TrueHeading.ToReciprocal();
        touchAndGo.TrueTrack = touchAndGo.TrueHeading;

        CommandResult result = Dispatch("CTO", departure, touchAndGo, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "N200AR opposite direction on runway 30, 1,500 ft ahead — this departure cannot begin takeoff roll while that traffic is inbound over the runway (7110.65 3-9-6.a)",
            warning
        );
    }

    [Fact]
    public void Cto_BehindLowApproachPastTheThresholdInside396a_WarnsDepartingAhead()
    {
        // A low approach is a departure once it crosses the landing threshold, so the takeoff behind it is 3-9-6.a.
        RunwayInfo runway = TenThousandFootRunway30();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState departure = MakeDepartureHoldingShort("N300DP");
        AircraftState lowApproach = MakeAirborneOptionAhead(departure, runway, 1500, ClearanceType.ClearedLowApproach, new LowApproachPhase());

        CommandResult result = Dispatch("CTO", departure, lowApproach, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DepartingAheadWarning, warning);
    }

    [Fact]
    public void Cto_BehindAirborneTouchAndGoBeyond396a_NoWarning()
    {
        // Airborne and well past the 6,000 ft Category III distance: 3-9-6.a is met.
        RunwayInfo runway = TenThousandFootRunway30();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState departure = MakeDepartureHoldingShort("N300DP");
        AircraftState touchAndGo = MakeAirborneOptionAhead(
            departure,
            runway,
            7000,
            ClearanceType.ClearedTouchAndGo,
            LiftedOffOptionPhases.TouchAndGo(runway)
        );

        CommandResult result = Dispatch("CTO", departure, touchAndGo, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N200AR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cto_FromHoldShort_LuawOnSameRunway_SucceedsAndWarnsInstructor()
    {
        // Issue #409: clearing a departure for takeoff while another aircraft is holding in
        // position on the same runway violates 3-9-6 same-runway separation. Advisory only —
        // the clearance stands.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("CTO", departure, occupant, departure);

        Assert.True(result.Success, result.Message);
        Assert.Contains(departure.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(departure.PendingWarnings, w => w.Contains("3-9-6", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cto_ToLinedUpAircraft_AnotherHoldingInPositionOnSameRunway_Warns()
    {
        // Dual LUAW, then CTO to one of them while the other still has no takeoff clearance.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        AircraftState occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        AircraftState departure = MakeOccupant("N300DP", runway, new LinedUpAndWaitingPhase());

        CommandResult result = Dispatch("CTO", departure, occupant, departure);

        Assert.True(result.Success, result.Message);
        Assert.Contains(departure.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cto_FromTaxiwayHoldShort_OccupantOnDestinationRunway_NoWarning()
    {
        // CTO issued while holding short of an intermediate TAXIWAY (e.g. "HS E") stores the
        // clearance for later — the aircraft is still minutes from the runway, so warning about
        // its occupancy now would be noise. The advisory fires only when the aircraft is
        // entering the runway.
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        AircraftState departure = MakeDepartureHoldingShort("N300DP", runway: "E");
        ((HoldingShortPhase)departure.Phases!.CurrentPhase!).HoldShort.Reason = HoldShortReason.ExplicitHoldShort;
        departure.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 20,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "30/12",
                },
            ],
        };

        CommandResult result = Dispatch("CTO", departure, occupant, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cto_FromHoldShort_OccupantAlreadyClearedForTakeoff_NoWarning()
    {
        // The occupant is a preceding departure about to roll — anticipated separation (3-9-5).
        RunwayInfo runway = TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        AircraftState occupant = MakeOccupant("N100LU", runway, new LinedUpAndWaitingPhase());
        occupant.Phases!.CurrentPhase!.Requirements[0].IsSatisfied = true;
        AircraftState departure = MakeDepartureHoldingShort("N300DP");

        CommandResult result = Dispatch("CTO", departure, occupant, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Traffic past the hold line (fouling the runway off its pavement)
    // -------------------------------------------------------------------------

    private static CommandResult DispatchOnLayout(string input, AircraftState target, AirportGroundLayout layout, params AircraftState[] world)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext ctx = TestDispatch.Context(Random.Shared, groundLayout: layout) with { ListAircraft = () => world };
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        return CommandDispatcher.DispatchCompound(compound, target, ctx);
    }

    /// <summary>An aircraft stopped holding in position with its nose 30 ft past runway 33's holding position marking.</summary>
    private static AircraftState HolderPastTheBar(RunwayInfo runway, GroundNode bar)
    {
        AircraftState holder = RunwayOccupancyTests.SquareToTheMarking(runway, bar, "B738", facingRunway: true, 30.0, alongFt: 0);
        holder.Callsign = "N100FL";
        holder.FlightPlan = new AircraftFlightPlan { Departure = "OAK" };
        holder.Phases = new PhaseList { AssignedRunway = runway };
        holder.Phases.Add(new HoldingInPositionPhase());
        holder.Phases.Start(CommandDispatcher.BuildMinimalContext(holder, ActiveRunways.Empty));
        return holder;
    }

    /// <summary>A landed arrival off the centreline on its exit with its tail 20 ft inside runway 33's holding position marking.</summary>
    private static AircraftState ExitingTailInside(RunwayInfo runway, GroundNode bar, double groundSpeedKts)
    {
        AircraftState exiting = RunwayOccupancyTests.SquareToTheMarking(runway, bar, "B738", facingRunway: false, 20.0, alongFt: 0);
        exiting.Callsign = "N200EX";
        exiting.IndicatedAirspeed = groundSpeedKts;
        exiting.FlightPlan = new AircraftFlightPlan { Destination = "KOAK" };
        exiting.Phases = new PhaseList { AssignedRunway = runway };
        exiting.Phases.Add(ExitPhaseFixture.ExitOffTheCentreline(runway));
        exiting.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return exiting;
    }

    private static string PastTheHoldLineForTakeoff(string display, string callsigns) =>
        $"traffic past the hold line for runway {display} ({callsigns}) — the runway is not clear; this departure cannot begin takeoff roll until that traffic is clear of the runway (7110.65 3-9-6; AIM 2-3-5.a.1); hold this aircraft short until the runway is clear";

    private static string PastTheHoldLineForLanding(string display, string callsigns) =>
        $"traffic past the hold line for runway {display} ({callsigns}) — the runway is not clear; withhold the landing/option clearance until that traffic is clear of the runway (7110.65 3-10-3.a; AIM 2-3-5.a.1)";

    [Fact]
    public void Cto_HolderPastTheBar_WarnsPastTheHoldLine()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState holder = HolderPastTheBar(oak.Runway, oak.Bar);
        AircraftState departure = MakeDepartureHoldingShort("N300DP", runway: "33");

        CommandResult result = DispatchOnLayout("CTO", departure, oak.Layout, holder, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N100FL", StringComparison.Ordinal));
        Assert.Equal(PastTheHoldLineForTakeoff("33", "N100FL"), warning);
    }

    [Fact]
    public void Cland_HolderPastTheBar_WarnsPastTheHoldLine()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState holder = HolderPastTheBar(oak.Runway, oak.Bar);
        AircraftState arrival = MakeArrival("N400AR", oak.Runway);

        CommandResult result = DispatchOnLayout("CLAND", arrival, oak.Layout, holder, arrival);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(arrival.PendingWarnings, w => w.Contains("N100FL", StringComparison.Ordinal));
        Assert.Equal(PastTheHoldLineForLanding("33", "N100FL"), warning);
    }

    [Fact]
    public void Cto_ExitingTailInsideStopped_WarnsOnceOnly()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState exiting = ExitingTailInside(oak.Runway, oak.Bar, groundSpeedKts: 0);
        AircraftState departure = MakeDepartureHoldingShort("N300DP", runway: "33");

        CommandResult result = DispatchOnLayout("CTO", departure, oak.Layout, exiting, departure);

        Assert.True(result.Success, result.Message);
        string warning = Assert.Single(departure.PendingWarnings, w => w.Contains("N200EX", StringComparison.Ordinal));
        Assert.Equal(PastTheHoldLineForTakeoff("33", "N200EX"), warning);
    }

    [Fact]
    public void Cland_ExitingTailInsideMovingUnrestricted_NoWarning()
    {
        // Leaving the runway and moving with nothing stopping it short of the marking: clear of the runway, and silent.
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState exiting = ExitingTailInside(oak.Runway, oak.Bar, groundSpeedKts: 10);
        AircraftState arrival = MakeArrival("N400AR", oak.Runway);

        CommandResult result = DispatchOnLayout("CLAND", arrival, oak.Layout, exiting, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N200EX", StringComparison.Ordinal));
    }

    [Fact]
    public void Cland_CrosserClearedMovingPastTheLine_NoWarning()
    {
        // Cleared to cross and moving past the near-side line: a crossing under way (3-1-3), silent like a moving crossing on the pavement.
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState crosser = RunwayOccupancyTests.SquareToTheMarking(oak.Runway, oak.Bar, "B738", facingRunway: true, 20.0, alongFt: 0);
        crosser.Callsign = "N500CX";
        crosser.IndicatedAirspeed = 12;
        crosser.Phases = new PhaseList();
        crosser.Phases.Add(new CrossingRunwayPhase(oak.Bar.Id, RunwayOccupancyTests.FarSideBar(oak.Layout, oak.Runway, oak.Bar).Id, "33"));
        crosser.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        AircraftState arrival = MakeArrival("N400AR", oak.Runway);

        CommandResult result = DispatchOnLayout("CLAND", arrival, oak.Layout, crosser, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N500CX", StringComparison.Ordinal));
    }

    [Fact]
    public void Cto_DepartureClearedRollingPastTheLine_NoWarning()
    {
        // A departure already cleared onto runway 33 (its destination hold-short cleared) rolling past the line toward a takeoff:
        // a second CTO behind it is anticipated separation (3-9-5), not traffic past the hold line.
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState rolling = RunwayOccupancyTests.SquareToTheMarking(oak.Runway, oak.Bar, "B738", facingRunway: true, 20.0, alongFt: 0);
        rolling.Callsign = "N600RL";
        rolling.IndicatedAirspeed = 12;
        rolling.FlightPlan = new AircraftFlightPlan { Departure = "OAK" };
        rolling.Phases = new PhaseList { AssignedRunway = oak.Runway };
        rolling.Phases.Add(new TaxiingPhase());
        rolling.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        rolling.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = oak.Bar.Id,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "33",
                    IsCleared = true,
                },
            ],
        };
        AircraftState departure = MakeDepartureHoldingShort("N300DP", runway: "33");

        CommandResult result = DispatchOnLayout("CTO", departure, oak.Layout, rolling, departure);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(departure.PendingWarnings, w => w.Contains("N600RL", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------
    // Safety-logic gate: airports whose vNAS ASDE-X config carries runway
    // configurations have a full safety logic system — no advisory there.
    // -------------------------------------------------------------------------

    [Fact]
    public void Cland_AtSafetyLogicAirport_NoWarning()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return; // snapshot absent — skip silently, matching the TestVnasData pattern
        }

        AircraftState occupant = MakeOccupant("N100LU", Rwy(airportId: "SFO"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy(airportId: "SFO"));

        CommandResult result = DispatchWithConfig("CLAND", arrival, zoa, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OccupiedRunwayWarning_FiresAtAsdexAirportWhileFacilityOnLimited()
    {
        // A CRC user has put SFO's Safety Logic on LIMITED: it raises closed-runway alerts only, so RWY OCCUPIED is off and
        // the 3-9-4 advisory applies again.
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> seed = LoadSeed(zoa);
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> limited = seed.SetItem(
            "SFO",
            seed["SFO"] with
            {
                RunwayConfigurationId = "limited",
            }
        );
        AircraftState occupant = MakeOccupant("N100LU", Rwy(airportId: "SFO"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy(airportId: "SFO"));

        CommandResult result = DispatchWithLiveConfigs("CLAND", arrival, zoa, limited, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OccupiedRunwayWarning_SkippedAtAsdexAirportOnNamedConfiguration()
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> seed = LoadSeed(zoa);
        Assert.True(AsdexSafetyLogicSeed.NamesRunwayConfiguration(zoa, "SFO", seed["SFO"].RunwayConfigurationId));
        AircraftState occupant = MakeOccupant("N100LU", Rwy(airportId: "SFO"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy(airportId: "SFO"));

        CommandResult result = DispatchWithLiveConfigs("CLAND", arrival, zoa, seed, occupant, arrival);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cland_AtAirportWithoutSafetyLogicRunwayConfigs_StillWarns()
    {
        // OAK's facility carries no ASDE-X runway configurations (in the live ZOA config its
        // ASDE-X is display-only) — safety logic cannot run there, so the advisory still applies.
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        AircraftState occupant = MakeOccupant("N100LU", Rwy(airportId: "OAK"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy(airportId: "OAK"));

        DispatchWithConfig("CLAND", arrival, zoa, occupant, arrival);

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A crossing held mid-pavement on <paramref name="runway"/>: the stopped traffic of 3-10-3.a.</summary>
    private static AircraftState HeldOnThePavement(RunwayInfo runway)
    {
        var held = new AircraftState
        {
            Callsign = "N500ST",
            AircraftType = "C172",
            Position = new LatLon((runway.Lat1 + runway.Lat2) / 2.0, (runway.Lon1 + runway.Lon2) / 2.0),
            TrueHeading = runway.TrueHeading + 90,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            Phases = new PhaseList(),
        };
        held.Phases.Add(new TaxiingPhase());
        held.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        held.Ground.Hold = HoldDirective.HoldPosition;
        return held;
    }

    [Fact]
    public void Cland_AtSafetyLogicAirport_HolderPastTheBar_StillWarnsPastTheHoldLine()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if ((zoa is null) || (RunwayBarFixture.RunwayBar("SFO", "28L") is not { } sfo))
        {
            return;
        }

        // 3-10-3.a.1 has no equipage carve-out: the fouler warning shows at SFO as it does for a takeoff. The older stopped-traffic
        // warning keeps the safety-logic gate, so the crossing held on the pavement draws it only without the ARTCC config.
        AircraftState holder = HolderPastTheBar(sfo.Runway, sfo.Bar);
        AircraftState held = HeldOnThePavement(sfo.Runway);
        AircraftState gated = MakeArrival("N400AR", sfo.Runway);
        gated.FlightPlan = new AircraftFlightPlan { Destination = "KSFO", FlightRules = "VFR" };
        AircraftState ungated = MakeArrival("N401AR", sfo.Runway);
        ungated.FlightPlan = new AircraftFlightPlan { Destination = "KSFO", FlightRules = "VFR" };

        CommandResult result = DispatchOnLayoutWithConfig("CLAND", gated, sfo.Layout, zoa, holder, held, gated);
        DispatchOnLayout("CLAND", ungated, sfo.Layout, holder, held, ungated);

        Assert.True(result.Success, result.Message);
        Assert.True(AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(zoa, LoadSeed(zoa), "SFO"));
        string display = RunwayIdentifier.ToDisplayDesignator(sfo.Runway.Designator);
        Assert.Equal(
            PastTheHoldLineForLanding(display, "N100FL"),
            Assert.Single(gated.PendingWarnings, w => w.Contains("N100FL", StringComparison.Ordinal))
        );
        Assert.DoesNotContain(gated.PendingWarnings, w => w.Contains("traffic stopped on runway", StringComparison.Ordinal));
        Assert.Contains(ungated.PendingWarnings, w => w.Contains("traffic stopped on runway", StringComparison.Ordinal));
    }

    private static CommandResult DispatchOnLayoutWithConfig(
        string input,
        AircraftState target,
        AirportGroundLayout layout,
        ArtccConfigRoot artccConfig,
        params AircraftState[] world
    )
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext ctx = TestDispatch.Context(
            Random.Shared,
            groundLayout: layout,
            artccConfig: artccConfig,
            asdexSafetyLogicConfigs: LoadSeed(artccConfig)
        ) with
        {
            ListAircraft = () => world,
        };
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        return CommandDispatcher.DispatchCompound(compound, target, ctx);
    }

    [Theory]
    [InlineData("SFO", true)]
    [InlineData("KSFO", true)] // ICAO form resolves via the leading-K strip
    [InlineData("OAK", false)] // facility present but no ASDE-X runway configurations in the snapshot
    [InlineData("KOAK", false)]
    [InlineData("XYZ", false)] // unknown facility — no positive evidence of safety logic
    [InlineData("", false)]
    public void OccupiedRunwayAlertingIsLive_OnTheLoadSeed_ResolvesFromAsdexRunwayConfigurations(string airportId, bool expected)
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        Assert.Equal(expected, AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(zoa, LoadSeed(zoa), airportId));
    }

    [Theory]
    [InlineData("named", "SFO", true)]
    [InlineData("named", "KSFO", true)] // ICAO form resolves via the leading-K strip
    [InlineData("limited", "SFO", false)]
    [InlineData("blank", "SFO", false)] // "" is LIMITED too
    [InlineData("no live entry", "SFO", false)] // runway configurations in the ARTCC config, but the detector does not run
    [InlineData("no runways", "SFO", false)] // a live entry the detector skips
    [InlineData("no artcc config", "SFO", false)]
    [InlineData("named", "OAK", false)] // ASDE-X without runway configurations: its live entry can only be LIMITED
    public void OccupiedRunwayAlertingIsLive_TruthTable(string liveState, string airportId, bool expected)
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> seed = LoadSeed(zoa);
        AsdexSafetyLogicConfig sfo = seed["SFO"];
        (ArtccConfigRoot? Config, ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Live) state = liveState switch
        {
            "named" => (zoa, seed),
            "limited" => (zoa, seed.SetItem("SFO", sfo with { RunwayConfigurationId = "limited" })),
            "blank" => (zoa, seed.SetItem("SFO", sfo with { RunwayConfigurationId = "" })),
            "no live entry" => (zoa, AsdexSafetyLogicSeed.Empty),
            "no runways" => (zoa, seed.SetItem("SFO", sfo with { Runways = [] })),
            "no artcc config" => (null, seed),
            _ => throw new ArgumentOutOfRangeException(nameof(liveState), liveState, "unknown live state"),
        };

        Assert.True(AsdexSafetyLogicSeed.NamesRunwayConfiguration(zoa, "SFO", sfo.RunwayConfigurationId));
        Assert.Equal(expected, AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(state.Config, state.Live, airportId));
    }

    /// <summary>A ZOA scenario at SFO with one aircraft airborne well clear of it: the engine fixture for the live-configuration tests.</summary>
    internal const string OneAircraftAtSfo = """
        {
          "id": "rpo-asdex-limited", "name": "RPO advisories at SFO", "artccId": "ZOA", "primaryAirportId": "SFO",
          "aircraft": [
            {
              "id": "a1", "aircraftId": "UAL1", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates", "coordinates": { "lat": 37.9, "lon": -122.6 }, "altitude": 5000, "heading": 280, "speed": 250
              },
              "flightplan": {
                "rules": "IFR", "departure": "KSFO", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450,
                "route": "", "remarks": "", "aircraftType": "B738"
              }
            }
          ]
        }
        """;

    [Theory]
    [InlineData("limited", true)]
    [InlineData(null, false)] // the configuration the scenario load seeded, a named one
    public void Engine_Cland_AtSfo_WarnsOnlyWhileTheFacilityIsOnLimited(string? configurationId, bool warns)
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(OneAircraftAtSfo, 7, MagneticDeclination.EvaluationDateUtc, scenario => scenario.ArtccConfig = ZoaArtccConfig.Full);
        SimScenarioState scenario = engine.Scenario!;
        AsdexSafetyLogicConfig sfo = scenario.AsdexSafetyLogicConfigs["SFO"];
        Assert.True(AsdexSafetyLogicSeed.NamesRunwayConfiguration(scenario.ArtccConfig, "SFO", sfo.RunwayConfigurationId));
        if (configurationId is not null)
        {
            scenario.AsdexSafetyLogicConfigs = scenario.AsdexSafetyLogicConfigs.SetItem("SFO", sfo with { RunwayConfigurationId = configurationId });
        }

        AircraftState occupant = MakeOccupant("N100LU", Rwy(airportId: "SFO"), new LinedUpAndWaitingPhase());
        AircraftState arrival = MakeArrival("N200AR", Rwy(airportId: "SFO"));
        engine.World.AddAircraft(occupant);
        engine.World.AddAircraft(arrival);

        CommandResult result = engine.SendCommand("N200AR", "CLAND");

        Assert.True(result.Success, result.Message);
        Assert.Equal(warns, arrival.PendingWarnings.Any(w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase)));
    }
}
