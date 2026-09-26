using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Spawns an arrival on a 1 nm final to a real runway (real navdata and ground layout) with the production landing
/// chain — <see cref="FinalApproachPhase"/> → <see cref="LandingPhase"/> → <see cref="RunwayExitPhase"/> →
/// <see cref="HoldingAfterExitPhase"/> — in a <see cref="SimulationEngine"/>, and clears it to land with no exit
/// instruction. The caller initializes <c>SimLog</c> first and drives the engine with <c>TickOneSecond</c>.
/// </summary>
public static class ShortFinalArrival
{
    /// <summary>The engine the arrival was added to, the arrival, and the runway it is cleared to.</summary>
    public sealed record Spawned(SimulationEngine Engine, AircraftState Aircraft, RunwayInfo Runway);

    /// <summary>Returns null when navdata or the airport's ground layout is unavailable (silent skip).</summary>
    public static Spawned? SpawnClearedToLand(string airport, string runwayDesignator, string aircraftType, string callsign)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout(airport);
        if (layout is null)
        {
            return null;
        }

        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway(airport, runwayDesignator);
        Assert.NotNull(runway);

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        (double lat, double lon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Position = new LatLon(lat, lon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318, // ~3° glidepath at 1 nm
            IndicatedAirspeed = 140,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = airport,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));

        var engine = new SimulationEngine(groundData);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-short-final-arrival",
            ScenarioName = "Short-final arrival",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = airport,
        };

        CommandResult cland = engine.SendCommand(callsign, "CLAND");
        Assert.True(cland.Success, $"CLAND failed: {cland.Message}");

        return new Spawned(engine, aircraft, runway);
    }
}
