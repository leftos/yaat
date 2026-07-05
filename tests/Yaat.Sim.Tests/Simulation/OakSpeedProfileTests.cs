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
/// Diagnostic: tick-by-tick speed profiles for OAK exits.
/// Logs phase, speed, heading, and distance to exit branch node each second.
/// </summary>
public class OakSpeedProfileTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void OAK30_W5_SpeedProfile()
    {
        RunSpeedProfile("OAK", "30", "B738", 130, 1.0, "EXIT W5", "W5");
    }

    [Fact]
    public void OAK30_W6_SpeedProfile()
    {
        RunSpeedProfile("OAK", "30", "B738", 130, 1.0, "EXIT W6", "W6");
    }

    [Fact]
    public void OAK28R_H_SpeedProfile()
    {
        RunSpeedProfile("OAK", "28R", "C172", 70, 0.5, "ER H", "H");
    }

    private void RunSpeedProfile(
        string airportId,
        string runwayId,
        string aircraftType,
        double touchdownSpeed,
        double finalDistNm,
        string exitCommand,
        string exitTaxiway
    )
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var runway = navDb.GetRunway(airportId, runwayId);
        Assert.NotNull(runway);

        var layout = new TestAirportGroundData().GetLayout(airportId);
        Assert.NotNull(layout);

        // Find the branch node dynamically: a node that has edges on both the runway and the exit taxiway
        var branchNode = layout.Nodes.Values.FirstOrDefault(n =>
            n.Edges.Any(e => e.IsRunwayCenterline) && n.Edges.Any(e => e.MatchesTaxiway(exitTaxiway))
        );
        Assert.True(branchNode is not null, $"No branch node found for exit taxiway {exitTaxiway} on runway {runwayId}");

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
            IndicatedAirspeed = touchdownSpeed,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airportId,
                Destination = airportId,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

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
            ScenarioId = "test-speed-profile",
            ScenarioName = "Speed Profile",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = airportId,
        };

        var clearResult = engine.SendCommand("TSTAC", "CLAND");
        Assert.True(clearResult.Success);

        var exitResult = engine.SendCommand("TSTAC", exitCommand);
        Assert.True(exitResult.Success, $"{exitCommand} failed: {exitResult.Message}");

        output.WriteLine($"t(s) | phase            | gs(kts) | hdg   | dist to branch(nm) | twy");
        output.WriteLine("---- | ---------------- | ------- | ----- | ------------------- | ----");

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();

            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "?";

            double distToBranch = GeoMath.AlongTrackDistanceNm(
                branchNode.Position.Lat,
                branchNode.Position.Lon,
                aircraft.Position.Lat,
                aircraft.Position.Lon,
                runway.TrueHeading
            );

            string distStr = distToBranch > 0 ? $"{distToBranch:F3}" : $"PAST {-distToBranch:F3}";
            string twy = aircraft.Ground.CurrentTaxiway ?? "-";

            if (aircraft.IsOnGround || phase.Contains("Landing") || phase.Contains("Exit") || phase.Contains("Hold"))
            {
                output.WriteLine(
                    $"{t, 4} | {phase, -16} | {aircraft.GroundSpeed, 7:F1} | {aircraft.TrueHeading.Degrees, 5:F1} | {distStr, 19} | {twy}"
                );
            }

            if (phase.Contains("Hold") || phase.Contains("Taxi"))
            {
                output.WriteLine($"\n  → Exited on {aircraft.Ground.CurrentTaxiway}, hdg={aircraft.TrueHeading.Degrees:F0}°");
                break;
            }
        }
    }
}
