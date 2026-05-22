using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

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

        var aircraft = MakeAircraft();

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");

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

        var aircraft = MakeAircraft();

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "KOAK");

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

        var aircraft = MakeAircraft();

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "oak");

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

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "ZZZZ");

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

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "");

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

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "   ");

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

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "BERKS");

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

        var navDb = TestVnasData.NavigationDb;
        var hirmoPos = navDb.GetFixPosition("HIRMO");
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
        aircraft.Approach.Expected = "H12-Z";
        var rwy12 = TestRunwayFactory.Make(designator: "12", airportId: "OAK", heading: 120, thresholdLat: 37.73, thresholdLon: -122.22);
        aircraft.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = new ApproachClearance
            {
                ApproachId = "H12-Z",
                AirportCode = "KOAK",
                RunwayId = "12",
                FinalApproachCourse = rwy12.TrueHeading,
            },
            AssignedRunway = rwy12,
        };

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");

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
        };
        aircraft.Phases = new PhaseList();
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

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "KOAK");

        Assert.True(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Procedure.ActiveStarId);
        Assert.Null(aircraft.Procedure.DestinationRunway);
        Assert.IsType<TaxiingPhase>(aircraft.Phases!.CurrentPhase);
    }
}
