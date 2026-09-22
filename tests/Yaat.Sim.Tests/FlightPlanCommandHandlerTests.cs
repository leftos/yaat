using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="FlightPlanCommandHandler.TryChangeDestination"/>.
///
/// The handler resolves any user-typed airport identifier (FAA "OAK" or ICAO
/// "KOAK") through <c>NavigationDatabase.TryResolveAirport</c>, writes the
/// canonical ICAO form to <c>aircraft.FlightPlan.Destination</c>, and rejects
/// unknown airports with a clear error so APT no longer silently accepts typos.
/// </summary>
[Collection("NavDbMutator")]
public class FlightPlanCommandHandlerTests
{
    public FlightPlanCommandHandlerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string? initialDestination = null)
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan { Destination = initialDestination ?? "" },
        };
    }

    [Fact]
    public void TryChangeDestination_FaaCode_ResolvesToIcao()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft();

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Equal("Destination changed to KOAK", result.Message);
    }

    [Fact]
    public void TryChangeDestination_Icao_StoresIcao()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft();

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "KOAK");

        Assert.True(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_LowercaseFaa_ResolvesToIcao()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft();

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "oak");

        Assert.True(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_Unknown_Rejects()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(initialDestination: "KSFO");

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "ZZZZ");

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("Unknown airport", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZZZZ", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pre-existing destination must not be clobbered by a rejected change.
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_Empty_Rejects()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(initialDestination: "KSFO");

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "");

        Assert.False(result.Success);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_Whitespace_Rejects()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(initialDestination: "KSFO");

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "   ");

        Assert.False(result.Success);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_FixIdent_Rejects()
    {
        // BERKS is a fix, not an airport — mirrors the NavigationDatabase test.
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(initialDestination: "KSFO");

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "BERKS");

        Assert.False(result.Success);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_NewAirport_ClearsArrivalProcedureState()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        NavigationDatabase navDb = TestVnasData.NavigationDb;
        (double Lat, double Lon)? hirmoPos = navDb.GetFixPosition("HIRMO");
        if (hirmoPos is null)
        {
            return;
        }

        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan { Destination = "KSFO" },
            Procedure = new AircraftProcedure { ActiveStarId = "EMZOH4", DestinationRunway = "12" },
        };
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) });
        aircraft.Approach.Expected = "H28R";
        // The pending clearance belongs to the airport being LEFT (KSFO): an approach at the NEW
        // destination is a correction toward the field the aircraft is already arriving at and is kept.
        RunwayInfo rwy28R = TestRunwayFactory.Make(designator: "28R", airportId: "SFO", heading: 280, thresholdLat: 37.61, thresholdLon: -122.38);
        aircraft.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = new ApproachClearance
            {
                ApproachId = "H28R",
                AirportCode = "KSFO",
                RunwayId = "28R",
                FinalApproachCourse = rwy28R.TrueHeading,
            },
            AssignedRunway = rwy28R,
        };

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");

        Assert.True(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Procedure.ActiveStarId);
        Assert.Null(aircraft.Procedure.DestinationRunway);
        Assert.Null(aircraft.Approach.Expected);
        Assert.Null(aircraft.Approach.PendingClearance);
        Assert.Empty(aircraft.Targets.NavigationRoute);
    }

    [Fact]
    public void TryChangeDestination_NewAirport_PreservesDepartureTaxiPhase()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(280),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KSFO" },
            Procedure = new AircraftProcedure { ActiveStarId = "EMZOH4", DestinationRunway = "28R" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new TaxiingPhase());
        aircraft.Phases.Start(
            new PhaseContext
            {
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 0,
                Logger = NullLogger.Instance,
            }
        );

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "KOAK");

        Assert.True(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Procedure.ActiveStarId);
        Assert.Null(aircraft.Procedure.DestinationRunway);
        Assert.IsType<TaxiingPhase>(aircraft.Phases!.CurrentPhase);
    }

    /// <summary>
    /// A VFR aircraft established on the right downwind for SJC 30R with a standing landing
    /// clearance — the N428KK configuration. The phase chain, its <c>AssignedRunway</c> and the
    /// landing clearance all belong to SJC, whichever airport the flight plan later names.
    /// </summary>
    private static AircraftState MakeSjcPatternAircraft()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("KSJC", "30R")!;
        Assert.NotNull(runway);

        TrueHeading reciprocal = runway.TrueHeading.ToReciprocal();
        (double Lat, double Lon) centerline = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 2.5);
        var patternSide = new TrueHeading((runway.TrueHeading.Degrees + 90) % 360);
        (double Lat, double Lon) position = GeoMath.ProjectPoint(centerline.Lat, centerline.Lon, patternSide, 1.5);

        var aircraft = new AircraftState
        {
            Callsign = "N428KK",
            AircraftType = "C172",
            Position = new LatLon(position.Lat, position.Lon),
            Altitude = 1500,
            TrueHeading = reciprocal,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KSJC",
            },
            Phases = new PhaseList(),
        };

        CommandResult entered = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, "30R", null);
        Assert.True(entered.Success, entered.Message);
        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.Equal("SJC", NavigationDatabase.NormalizeAirport(aircraft.Phases!.AssignedRunway!.AirportId));

        CommandResult cleared = PatternCommandHandler.TryClearedToLand(
            new ClearedToLandCommand { RunwayId = "30R" },
            aircraft,
            TestDispatch.Context(Random.Shared)
        );
        Assert.True(cleared.Success, cleared.Message);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);

        return aircraft;
    }

    [Fact]
    public void TryChangeDestination_NewAirport_CancelsPatternAtOldAirport()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeSjcPatternAircraft();

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Phases);
        Assert.Null(aircraft.Procedure.DestinationRunway);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("pattern to RWY 30R cancelled by APT OAK", StringComparison.Ordinal));
    }

    [Fact]
    public void TryChangeDestination_SameAirport_KeepsPattern()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeSjcPatternAircraft();
        // The plan named another field while the aircraft flew the SJC pattern; APT SJC brings the
        // two back together, so the pattern is not cancelled even though the destination changes.
        aircraft.FlightPlan.Destination = "KOAK";

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "KSJC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSJC", aircraft.FlightPlan.Destination);
        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.Equal("30R", aircraft.Phases!.AssignedRunway!.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void TryChangeDestination_ThenEnterPattern_ResolvesRunwayAtNewDestination()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AircraftState aircraft = MakeSjcPatternAircraft();

        CommandResult changed = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");
        Assert.True(changed.Success, changed.Message);

        CommandResult entered = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Base, "28L", 4, null);

        Assert.True(entered.Success, entered.Message);
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(aircraft.Phases!.AssignedRunway!.AirportId));
        Assert.Equal("28L", aircraft.Phases.AssignedRunway.Designator);
    }

    /// <summary>
    /// A jet that has just left KSJC 30R and is climbing out. Its <c>AssignedRunway</c> is the DEPARTURE
    /// runway — <see cref="InitialClimbPhase"/> carries it — so an airport identity test alone reads the
    /// climb-out as an arrival flown to SJC. Re-filing the destination must leave the departure chain alone.
    /// </summary>
    [Fact]
    public void TryChangeDestination_AirborneDeparture_KeepsInitialClimb()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("KSJC", "30R")!;
        Assert.NotNull(runway);

        var aircraft = new AircraftState
        {
            Callsign = "SWA123",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = 900,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KSJC",
                Destination = "KSFO",
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new InitialClimbPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));
        Assert.IsType<InitialClimbPhase>(aircraft.Phases.CurrentPhase);

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "KOAK");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.IsType<InitialClimbPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Empty(aircraft.PendingWarnings);
    }

    /// <summary>An aircraft established on the OAK ILS 30, with the approach chain installed.</summary>
    private static AircraftState MakeOakIlsAircraft(string filedDestination)
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("KOAK", "30")!;
        Assert.NotNull(runway);

        (double Lat, double Lon) position = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading.ToReciprocal(),
            8.0
        );

        var aircraft = new AircraftState
        {
            Callsign = "N182AK",
            AircraftType = "C182",
            Position = new LatLon(position.Lat, position.Lon),
            TrueHeading = runway.TrueHeading,
            Altitude = 3000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = true, Destination = filedDestination },
        };

        var cmd = new ClearedApproachCommand(
            "I30",
            "KOAK",
            Force: true,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: null,
            DctFixLat: null,
            DctFixLon: null,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        CommandResult cleared = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
        Assert.True(cleared.Success, cleared.Message);
        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.NotNull(aircraft.Phases!.ActiveApproach);
        Assert.NotEmpty(aircraft.Targets.NavigationRoute);

        aircraft.Approach.Expected = "I30";
        return aircraft;
    }

    /// <summary>
    /// The flight plan said KSJC while the aircraft was being worked into Oakland; <c>APT OAK</c> corrects
    /// the plan toward the airport it is already arriving at. Nothing about the arrival may be torn down —
    /// cancelling the approach and wiping the route it is flying is the opposite of what the RPO asked for.
    /// </summary>
    [Fact]
    public void TryChangeDestination_ApproachAtNewAirport_IsKeptAsCorrection()
    {
        if (TestVnasData.NavigationDb is null || TestVnasData.NavigationDb.GetApproach("KOAK", "I30") is null)
        {
            return;
        }

        AircraftState aircraft = MakeOakIlsAircraft(filedDestination: "KSJC");
        int routeCount = aircraft.Targets.NavigationRoute.Count;

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.NotNull(aircraft.Phases!.ActiveApproach);
        Assert.Equal(routeCount, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("I30", aircraft.Approach.Expected);
        Assert.Empty(aircraft.PendingWarnings);
    }

    /// <summary>
    /// The same approach, but the plan already named Oakland and the RPO sends the aircraft to San Jose:
    /// the approach belongs to the airport being left, so it is cancelled and the RPO told so.
    /// </summary>
    [Fact]
    public void TryChangeDestination_ApproachAtOldAirport_IsCancelled()
    {
        if (TestVnasData.NavigationDb is null || TestVnasData.NavigationDb.GetApproach("KOAK", "I30") is null)
        {
            return;
        }

        AircraftState aircraft = MakeOakIlsAircraft(filedDestination: "KOAK");

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "SJC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSJC", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Phases);
        Assert.Null(aircraft.Approach.Expected);
        Assert.Empty(aircraft.Targets.NavigationRoute);
        Assert.Contains(
            aircraft.PendingWarnings,
            w => w.Contains("approach to RWY", StringComparison.Ordinal) && w.Contains("cancelled by APT SJC", StringComparison.Ordinal)
        );
    }
}
