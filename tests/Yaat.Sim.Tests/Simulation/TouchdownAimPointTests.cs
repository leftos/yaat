using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Aircraft must touch down at a realistic aiming point down the runway, not on the threshold.
/// A light single floats little in the flare, so without help it lands essentially on the
/// threshold; the per-category aim-point offset (CategoryPerformance.LandingAimPointOffsetFt)
/// aims its glidepath down the runway so it crosses slightly high and touches down near the
/// numbers (~500 ft). Jets already float to a realistic touchdown zone in the flare, so they
/// get no offset.
/// </summary>
public class TouchdownAimPointTests(ITestOutputHelper output)
{
    // Light piston must land near the numbers, not on the threshold (the fix). The jet already
    // floats to a realistic touchdown zone and is checked only for a sane bound.
    [Theory]
    [InlineData("C172", 400.0, 800.0)]
    [InlineData("C208", 500.0, 1100.0)]
    [InlineData("AT76", 500.0, 1100.0)]
    [InlineData("B738", 800.0, 2500.0)]
    public void LandsInTouchdownZone(string aircraftType, double minFt, double maxFt)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData());
        double? touchdownFt = RunToTouchdown(engine, "OAK", "28R", aircraftType);

        Assert.NotNull(touchdownFt);
        output.WriteLine($"{aircraftType} touched down {touchdownFt:F0} ft past the 28R threshold");
        Assert.True(
            touchdownFt >= minFt && touchdownFt <= maxFt,
            $"Expected {aircraftType} to touch down {minFt:F0}-{maxFt:F0} ft past the threshold, got {touchdownFt:F0} ft."
        );
    }

    /// <summary>
    /// Spawn established on the glidepath at ~2 nm final, clear to land, tick until on the
    /// ground, and return the along-track distance (ft) of the touchdown point past the threshold.
    /// </summary>
    private static double? RunToTouchdown(SimulationEngine engine, string airportId, string runwayId, string aircraftType)
    {
        var navDb = NavigationDatabase.Instance;
        var runway = navDb.GetRunway(airportId, runwayId);
        Assert.NotNull(runway);

        const double finalDistNm = 2.0;
        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, finalDistNm);
        double altAboveField = finalDistNm * 318;

        var aircraft = new AircraftState
        {
            Callsign = "TSTAC",
            AircraftType = aircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + altAboveField,
            IndicatedAirspeed = CategoryPerformance.ApproachSpeed(AircraftCategorization.Categorize(aircraftType)),
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airportId,
                Destination = airportId,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

        var layout = new TestAirportGroundData().GetLayout(airportId);
        Assert.NotNull(layout);

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = $"test-{airportId.ToLowerInvariant()}-touchdown",
            ScenarioName = $"{airportId} Touchdown Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = airportId,
        };

        var clearResult = engine.SendCommand("TSTAC", "CLAND");
        Assert.True(clearResult.Success, $"CLAND failed: {clearResult.Message}");

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            if (aircraft.IsOnGround)
            {
                double pastThresholdNm = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading);
                return pastThresholdNm * GeoMath.FeetPerNm;
            }
        }

        return null;
    }
}
