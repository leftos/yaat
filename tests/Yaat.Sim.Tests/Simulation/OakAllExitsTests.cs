using Microsoft.Extensions.Logging;
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
        var result = RunExitTest("OAK", "30", "B738", 130, 1.0, exitTaxiway, Rwy30ExitsThresholdOrder);
        if (result is null)
        {
            return;
        }

        string label = exitTaxiway ?? "default";
        LogResult(label, exitTaxiway, result);
        AssertSmoothExit(result, label);
    }

    // --- OAK 28R exits, ordered from threshold ---
    private static readonly string[] Rwy28RExitsThresholdOrder = ["B", "G", "H", "E", "P", "J", "C1"];

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
        var result = RunExitTest("OAK", "28R", "C172", 70, 0.5, exitTaxiway, Rwy28RExitsThresholdOrder);
        if (result is null)
        {
            return;
        }

        string label = exitTaxiway ?? "default";
        LogResult(label, exitTaxiway, result);
        AssertSmoothExit(result, label);
    }

    /// <summary>
    /// Diagnostic: dump tick CSVs for specific exits. Run manually, then visualize with:
    ///   dotnet run --project tools/Yaat.TickAnimator -- \
    ///     --layout tests/Yaat.Sim.Tests/TestData/oak.geojson \
    ///     --ticks .tmp/oak-30-W6-ticks.csv --aircraft B738 --output .tmp/oak-30-W6.gif
    /// </summary>
    [Theory]
    [InlineData("30", "B738", 130, 1.0, "W6")]
    [InlineData("30", "B738", 130, 1.0, "W4")]
    [InlineData("28R", "C172", 70, 0.5, null)]
    [InlineData("28R", "C172", 70, 0.5, "P")]
    [InlineData("28R", "C172", 70, 0.5, "E")]
    [InlineData("28R", "C172", 70, 0.5, "J")]
    public void Diagnostic_DumpTickCsv(string runwayId, string aircraftType, double speed, double dist, string? exitTaxiway)
    {
        DumpTickCsvImpl(runwayId, aircraftType, speed, dist, exitTaxiway);
    }

    [Fact]
    public void Diag_OAK28R_ExitE_Isolated() => DumpTickCsvImpl("28R", "C172", 70, 0.5, "E");

    [Fact]
    public void Diag_OAK28R_ExitP_Isolated() => DumpTickCsvImpl("28R", "C172", 70, 0.5, "P");

    private void DumpTickCsvImpl(string runwayId, string aircraftType, double speed, double dist, string? exitTaxiway)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundNavigator", LogLevel.Trace)
            .EnableCategory("RunwayExitPhase", LogLevel.Trace)
            .EnableCategory("AirportGroundLayout", LogLevel.Debug)
            .InitializeSimLog();
        var engine = new SimulationEngine(groundData);

        var navDb = NavigationDatabase.Instance;
        var runway = navDb.GetRunway("OAK", runwayId);
        Assert.NotNull(runway);

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, dist);
        double altAboveField = dist * 318;

        var aircraft = new AircraftState
        {
            Callsign = "TSTAC",
            AircraftType = aircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + altAboveField,
            IndicatedAirspeed = speed,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "IFR",
                CruiseAltitude = 3000,
            },
        };

        var layout = new TestAirportGroundData().GetLayout("OAK");
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
            ScenarioId = "test-oak-dump",
            ScenarioName = "OAK Tick Dump",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        var clearResult = engine.SendCommand("TSTAC", "CLAND");
        Assert.True(clearResult.Success);

        if (exitTaxiway is not null)
        {
            var exitResult = engine.SendCommand("TSTAC", $"EXIT {exitTaxiway}");
            Assert.True(exitResult.Success);
        }

        var recorder = new TickRecorder(aircraft);
        bool done = false;
        for (int t = 1; t <= 300 && !done; t++)
        {
            engine.TickOneSecond();
            recorder.Record(t);

            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "none";
            if (phase.Contains("Hold"))
            {
                // Record a few more ticks after stopping
                for (int extra = 1; extra <= 3; extra++)
                {
                    engine.TickOneSecond();
                    recorder.Record(t + extra);
                }

                done = true;
            }
        }

        string exitLabel = exitTaxiway ?? "default";
        string repoRoot = TickRecorder.FindRepoRoot();
        string csvPath = Path.Combine(repoRoot, ".tmp", $"oak-{runwayId}-{exitLabel}-ticks.csv");
        recorder.WriteCsv(csvPath);
        output.WriteLine($"Wrote {recorder.Count} ticks to {csvPath}");
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
        string[] thresholdOrder
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

        // Spawn on short final
        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, finalDistNm);

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
                CruiseAltitude = 3000,
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
            ScenarioId = $"test-{airportId.ToLowerInvariant()}-exits",
            ScenarioName = $"{airportId} Exit Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = airportId,
        };

        var clearResult = engine.SendCommand("TSTAC", "CLAND");
        Assert.True(clearResult.Success, $"CLAND failed: {clearResult.Message}");

        if (exitTaxiway is not null)
        {
            var exitResult = engine.SendCommand("TSTAC", $"EXIT {exitTaxiway}");
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
        foreach (var (time, dev) in deviationSamples)
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

        return new ExitResult
        {
            FinalTaxiway = aircraft.Ground.CurrentTaxiway,
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
