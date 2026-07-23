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
/// Runway-exit speed-profile regression tests for OAK. Two invariants:
/// <list type="bullet">
/// <item>No surge — the exit maneuver never accelerates above normal taxi speed toward the runway
/// coast speed and then brakes hard for the hold-short (the old slow-turn-then-surge profile).</item>
/// <item>High-speed exits carry speed through the turn — a shallow rapid exit (W5) is taken at ~its
/// design turn-off speed, not the ~15 kt crawl a tight junction fillet forced before.</item>
/// </list>
/// Each test also logs a tick-by-tick trace for diagnosis.
/// </summary>
public class OakSpeedProfileTests(ITestOutputHelper output)
{
    private readonly record struct ExitSample(int T, double Gs, double DistToBranchNm);

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
    public void OAK30_W5_HighSpeedExit_CarriesSpeedThroughTurn_NoSurge()
    {
        var samples = RunSpeedProfile("OAK", "30", "B738", 130, 1.0, "EXIT W5", "W5");
        if (samples is null)
        {
            return;
        }

        // Jet taxi speed is 30 kt: the exit must never surge above it toward the 40 kt rollout coast.
        AssertNoSurgeAboveTaxiSpeed(samples, taxiSpeedKts: 30, label: "W5");
        // A ~29° rapid exit must be taken at speed. Before the fillet widening the junction arc capped
        // the turn at ~14.8 kt (0.13 g over a 150 ft fillet); it is now ~30 kt over a ~600 ft fillet.
        AssertCarriesSpeedThroughTurn(samples, minKts: 24, label: "W5");
    }

    [Fact]
    public void OAK30_W6_StandardExit_NoSurge()
    {
        var samples = RunSpeedProfile("OAK", "30", "B738", 130, 1.0, "EXIT W6", "W6");
        if (samples is null)
        {
            return;
        }

        // A 90° standard exit slows to its corner speed for the turn (no carry-through assertion), but
        // must still not surge above taxi speed on the straight to the hold-short.
        AssertNoSurgeAboveTaxiSpeed(samples, taxiSpeedKts: 30, label: "W6");
    }

    [Fact]
    public void OAK28R_H_StandardExit_NoSurge()
    {
        var samples = RunSpeedProfile("OAK", "28R", "C172", 70, 0.5, "ER H", "H");
        if (samples is null)
        {
            return;
        }

        // Piston taxi speed is 20 kt.
        AssertNoSurgeAboveTaxiSpeed(samples, taxiSpeedKts: 20, label: "H");
    }

    private static void AssertNoSurgeAboveTaxiSpeed(List<ExitSample> samples, double taxiSpeedKts, string label)
    {
        // Only check the exit taxiway itself — samples at/past the branch. The pre-branch approach is the
        // rollout handoff still decelerating from landing toward taxi speed (a legitimate transient above
        // taxi speed), not the accelerate-to-the-hold-short hump this guards against. The hump (old W5:
        // 14.8→38.9 kt) happens entirely past the branch on the exit straight.
        var onExit = samples.Where(s => s.DistToBranchNm <= 0).ToList();
        Assert.True(onExit.Count > 0, $"[{label}] no exit samples past the branch node");

        var peak = onExit.MaxBy(s => s.Gs);
        Assert.True(
            peak.Gs <= taxiSpeedKts + 2.0,
            $"[{label}] exit surged to {peak.Gs:F1} kt at t={peak.T}s past the branch — should stay at/below "
                + $"taxi speed {taxiSpeedKts:F0} kt (no accelerate-up-to-the-hold-short hump)."
        );
    }

    private static void AssertCarriesSpeedThroughTurn(List<ExitSample> samples, double minKts, string label)
    {
        // The turn happens in the branch-crossing window: within ~0.025 nm (150 ft) either side of the branch.
        var turnWindow = samples.Where(s => Math.Abs(s.DistToBranchNm) <= 0.025).ToList();
        Assert.True(turnWindow.Count > 0, $"[{label}] no exit samples near the branch node — trace did not reach the turn");

        var slowest = turnWindow.MinBy(s => s.Gs);
        Assert.True(
            slowest.Gs >= minKts,
            $"[{label}] slowed to {slowest.Gs:F1} kt through the turn (t={slowest.T}s) — a high-speed exit should "
                + $"carry ≥ {minKts:F0} kt; a tight junction fillet forced the earlier ~15 kt crawl."
        );
    }

    private List<ExitSample>? RunSpeedProfile(
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
            return null;
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

        var exitSamples = new List<ExitSample>();

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

            if (phase == "Runway Exit")
            {
                exitSamples.Add(new ExitSample(t, aircraft.GroundSpeed, distToBranch));
            }

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

        return exitSamples;
    }
}
