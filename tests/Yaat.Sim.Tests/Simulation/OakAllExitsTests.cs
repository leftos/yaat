using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E exit tests for OAK runways 30 (B738 at 130kts) and 28R (C172 at 70kts).
/// Same structure as <see cref="Sfo28rAllExitsTests"/>: spawn on short final,
/// test every exit for smooth monotonic turns.
/// </summary>
public class OakAllExitsTests(ITestOutputHelper output)
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

    // --- OAK 30 exits, ordered from threshold ---
    private static readonly string[] Rwy30ExitsThresholdOrder = ["W1", "W2", "W6", "W7", "W3", "W4", "W5"];

    [Theory]
    [InlineData(null)]
    [InlineData("W1")]
    [InlineData("W2")]
    [InlineData("W6")]
    [InlineData("W7")]
    [InlineData("W3")]
    [InlineData("W4")]
    [InlineData("W5")]
    public void OAK30_B738_ExitsSmoothly(string? exitTaxiway)
    {
        ExitResult? result = RunExitTest("OAK", "30", "B738", 130, 1.0, exitTaxiway, Rwy30ExitsThresholdOrder, onExitTick: null);
        if (result is null)
        {
            return;
        }

        string label = exitTaxiway ?? "default";
        LogResult(label, exitTaxiway, result);
        AssertSmoothExit(result, label);
    }

    // --- OAK 28R exits, ordered from threshold (measured down-runway distance) ---
    private static readonly string[] Rwy28RExitsThresholdOrder = ["B", "E", "G", "H", "P", "J", "C1"];

    [Theory]
    [InlineData(null)]
    [InlineData("B")]
    [InlineData("G")]
    [InlineData("H")]
    [InlineData("E")]
    [InlineData("P")]
    [InlineData("J")]
    [InlineData("C1")]
    public void OAK28R_C172_ExitsSmoothly(string? exitTaxiway)
    {
        ExitResult? result = RunExitTest("OAK", "28R", "C172", 70, 0.5, exitTaxiway, Rwy28RExitsThresholdOrder, onExitTick: null);
        if (result is null)
        {
            return;
        }

        string label = exitTaxiway ?? "default";
        LogResult(label, exitTaxiway, result);
        AssertSmoothExit(result, label);
    }

    /// <summary>
    /// The C172's <c>EXIT J</c> off OAK 28R turns 143° right off the runway onto J at node 377 (J crosses 28R at
    /// 38.6°), past <c>GroundNavigator.ReversalEntryThresholdDeg</c>. That is a corner to round at the junction, not a
    /// turn about on J: the aircraft is still on the runway centreline there, a few feet off J's centreline, and the 25 ft
    /// occupied-edge band would otherwise read it as standing inside J. This pins that no turn about — brake, jog, hold
    /// or reversal — is ever planned on the way off.
    /// </summary>
    [Fact]
    public void OAK28R_C172_ExitJ_TurnsOffAtTheJunctionNotATurnAboutOnJ()
    {
        ExitResult? result = RunExitTest("OAK", "28R", "C172", 70, 0.5, "J", Rwy28RExitsThresholdOrder, AssertNoTaxiwayTurnAbout);
        if (result is null)
        {
            return;
        }

        Assert.Equal("J", result.FinalTaxiway);
    }

    /// <summary>Fails when the exit phase's navigator has planned or is playing a turn about on the exit taxiway.</summary>
    private static void AssertNoTaxiwayTurnAbout(AircraftState aircraft, int t)
    {
        if (aircraft.Phases?.CurrentPhase is not RunwayExitPhase exitPhase)
        {
            return;
        }

        GroundNavigatorPlaybackDto? playback = ((RunwayExitPhaseDto)exitPhase.ToSnapshot()).Navigator?.Playback;
        if (playback is null)
        {
            return;
        }

        string? flag =
            (playback.TurnAboutHold is not null) ? "hold"
            : (playback.TurnAboutBrake is not null) ? "brake"
            : (playback.PendingTurnAboutArc is not null) ? "pending arc"
            : (playback.TurnAboutReversalPlaying == true) ? "reversal playing"
            : null;
        if (flag is not null)
        {
            Assert.Fail(
                $"t={t}s: turning about on J at the runway junction ({flag}=true) at {aircraft.GroundSpeed:F1} kt, "
                    + $"heading {aircraft.TrueHeading.Degrees:F1}°"
            );
        }
    }

    private void LogResult(string label, string? requested, ExitResult result)
    {
        string relaxed = (requested is not null && result.FinalTaxiway != requested) ? $" (relaxed from {requested})" : "";
        output.WriteLine(
            $"EXIT {label}: actual={result.FinalTaxiway}{relaxed}, hdg={result.FinalHeading:F0}°, "
                + $"turn={result.TotalHeadingChange:F0}°, exitTime={result.ExitDurationSeconds}s, total={result.TotalSeconds}s, "
                + $"maxDev={result.MaxDeviationFt:F1}ft@t={result.MaxDeviationTime}s, avgDev={result.AvgDeviationFt:F1}ft"
        );
    }

    private void AssertSmoothExit(ExitResult result, string label)
    {
        Assert.NotNull(result.FinalTaxiway);

        double maxAllowedFt = result.TotalHeadingChange > 120.0 ? 50.0 : 35.0;

        Assert.True(
            result.MaxDeviationFt < maxAllowedFt,
            $"[{label}] Max path deviation {result.MaxDeviationFt:F1}ft at t={result.MaxDeviationTime}s. "
                + $"Avg deviation {result.AvgDeviationFt:F1}ft. Should stay within {maxAllowedFt:F0}ft of route."
        );

        Assert.True(result.ExitDurationSeconds <= 90, $"[{label}] Exit took {result.ExitDurationSeconds}s — should complete in under 90s");
    }

    private ExitResult? RunExitTest(
        string airportId,
        string runwayId,
        string aircraftType,
        double touchdownSpeed,
        double finalDistNm,
        string? exitTaxiway,
        string[] thresholdOrder,
        Action<AircraftState, int>? onExitTick
    )
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        RunwayInfo? runway = navDb.GetRunway(airportId, runwayId);
        Assert.NotNull(runway);

        // Spawn on short final
        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, finalDistNm);

        // Altitude: ~3° glide slope
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

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        Assert.NotNull(layout);

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = $"test-{airportId.ToLowerInvariant()}-exits",
            ScenarioName = $"{airportId} Exit Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = airportId,
        };

        CommandResult clearResult = engine.SendCommand("TSTAC", "CLAND");
        Assert.True(clearResult.Success, $"CLAND failed: {clearResult.Message}");

        if (exitTaxiway is not null)
        {
            CommandResult exitResult = engine.SendCommand("TSTAC", $"EXIT {exitTaxiway}");
            Assert.True(exitResult.Success, $"EXIT {exitTaxiway} failed: {exitResult.Message}");
        }

        // Tick through landing + exit
        var headingSamples = new List<(int Time, double Heading)>();
        var deviationSamples = new List<(int Time, double DeviationFt)>();
        int exitStartTime = -1;
        int exitEndTime = -1;
        bool inExitPhase = false;

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "none";

            if (phase == "Runway Exit" && !inExitPhase)
            {
                inExitPhase = true;
                exitStartTime = t;
            }

            if (inExitPhase)
            {
                headingSamples.Add((t, aircraft.TrueHeading.Degrees));
                if (aircraft.Ground.LastNavDiag is { } diag)
                {
                    deviationSamples.Add((t, diag.PathDeviationFt));
                }

                if (phase == "Runway Exit")
                {
                    onExitTick?.Invoke(aircraft, t);
                }
            }

            if (inExitPhase && phase != "Runway Exit")
            {
                exitEndTime = t;
                break;
            }
        }

        if (exitStartTime < 0)
        {
            Assert.Fail(
                $"Aircraft never entered Runway Exit phase within 300s. "
                    + $"Last phase={aircraft.Phases?.CurrentPhase?.Name}, gs={aircraft.GroundSpeed:F1}"
            );
        }

        if (exitEndTime < 0)
        {
            Assert.Fail(
                $"Aircraft never left Runway Exit phase within 300s. "
                    + $"Last phase={aircraft.Phases?.CurrentPhase?.Name}, gs={aircraft.GroundSpeed:F1}"
            );
        }

        // Path deviation analysis
        double maxDeviationFt = 0;
        int maxDeviationTime = 0;
        double sumDeviationFt = 0;
        foreach ((int time, double dev) in deviationSamples)
        {
            sumDeviationFt += dev;
            if (dev > maxDeviationFt)
            {
                maxDeviationFt = dev;
                maxDeviationTime = time;
            }
        }
        double avgDeviationFt = deviationSamples.Count > 0 ? sumDeviationFt / deviationSamples.Count : 0;

        double totalHeadingChange = headingSamples.Count >= 2 ? Math.Abs(NormalizeAngle(headingSamples[^1].Heading - headingSamples[0].Heading)) : 0;

        string? finalTaxiway = aircraft.Ground.CurrentTaxiway;
        Assert.NotNull(finalTaxiway);
        Assert.Contains(finalTaxiway, thresholdOrder);

        return new ExitResult
        {
            FinalTaxiway = finalTaxiway,
            FinalHeading = aircraft.TrueHeading.Degrees,
            TotalHeadingChange = totalHeadingChange,
            TotalSeconds = exitEndTime,
            ExitDurationSeconds = exitEndTime - exitStartTime,
            MaxDeviationFt = maxDeviationFt,
            MaxDeviationTime = maxDeviationTime,
            AvgDeviationFt = avgDeviationFt,
        };
    }

    private static double NormalizeAngle(double degrees)
    {
        double d = degrees % 360;
        if (d > 180)
        {
            d -= 360;
        }

        if (d <= -180)
        {
            d += 360;
        }

        return d;
    }

    private sealed class ExitResult
    {
        public required string? FinalTaxiway { get; init; }
        public required double FinalHeading { get; init; }
        public required double TotalHeadingChange { get; init; }
        public required int TotalSeconds { get; init; }
        public required int ExitDurationSeconds { get; init; }
        public required double MaxDeviationFt { get; init; }
        public required int MaxDeviationTime { get; init; }
        public required double AvgDeviationFt { get; init; }
    }
}
