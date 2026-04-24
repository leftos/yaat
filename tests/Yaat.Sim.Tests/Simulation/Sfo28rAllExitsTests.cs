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
/// Comprehensive E2E tests for every exit from SFO 28R. Spawns a B738 on 1nm
/// final, lands, and verifies each exit produces a smooth monotonic turn to the
/// hold-short. Tests cover:
///   - No exit instruction (default exit selection)
///   - Each named exit via EXIT command
///   - Exits too close to threshold (pilot says unable, picks next)
///   - High-speed shallow exits (T, Q at ~20°) and standard 90° exits
/// </summary>
public class Sfo28rAllExitsTests(ITestOutputHelper output)
{
    // 28R exits ordered from threshold (east) to departure end (west).
    // Aircraft touches down near the eastern end and rolls west.
    private static readonly string[] ExitsThresholdOrder = ["C", "C2", "N", "P", "L", "T", "D", "Q", "K", "R", "S2", "S1", "C3"];

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("SimulationEngine", Microsoft.Extensions.Logging.LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    // Branch nodes for each exit, ordered east→west (threshold order for 28R)
    private static readonly (string Taxiway, int BranchNodeId, double Angle)[] ExitBranchNodes =
    [
        ("C", 545, 90),
        ("C2", 740, 90),
        ("N", 726, 90),
        ("P", 738, 90),
        ("L", 327, 90),
        ("T", 230, 19),
        ("E", 230, 111),
        ("D", 227, 90),
        ("Q", 207, 20),
        ("K", 207, 90),
        ("R", 711, 90),
        ("S2", 679, 91),
        ("S1", 678, 90),
        ("C3", 678, 90),
    ];

    /// <summary>
    /// Diagnostic: map the B738 deceleration curve against exit branch node
    /// positions. Shows aircraft speed and distance to each exit at every second
    /// during rollout, revealing which exits are reachable and at what speed.
    /// </summary>
    [Fact]
    public void Diagnostic_DecelerationCurveVsExits()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var runway28R = navDb.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        // Collect branch node positions
        var branchPositions = new List<(string Taxiway, double Lat, double Lon, double Angle)>();
        foreach (var (twy, nodeId, angle) in ExitBranchNodes)
        {
            if (layout.Nodes.TryGetValue(nodeId, out var node))
            {
                branchPositions.Add((twy, node.Position.Lat, node.Position.Lon, angle));
            }
        }

        // Spawn B738 on 1nm final
        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TST738",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.ElevationFt + 318,
            IndicatedAirspeed = 145,
            IsOnGround = false,
            Departure = "SFO",
            Destination = "SFO",
            FlightRules = "IFR",
            CruiseAltitude = 3000,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = runway28R };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.GroundLayout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-decel",
            ScenarioName = "SFO Decel Curve",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        var clearResult = engine.SendCommand("TST738", "CLAND");
        Assert.True(clearResult.Success);

        // Header
        output.WriteLine("t(s) | phase        | gs(kts) | " + string.Join(" | ", branchPositions.Select(b => $"{b.Taxiway, 4}")));
        output.WriteLine("---- | ------------ | ------- | " + string.Join(" | ", branchPositions.Select(_ => "----")));

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();

            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "?";

            // Log every tick for debugging
            if (t >= 25 && t <= 100 && t % 1 == 0)
            {
                output.WriteLine(
                    $"{t, 4} | {phase, -12} | {aircraft.GroundSpeed, 7:F1} | onGround={aircraft.IsOnGround} | pos=({aircraft.Position.Lat:F5},{aircraft.Position.Lon:F5})"
                );
            }

            // Skip airborne approach ticks
            if (!aircraft.IsOnGround && (phase.Contains("Approach") || phase.Contains("Final")))
            {
                continue;
            }

            if (phase.Contains("Hold") || phase.Contains("Taxi"))
            {
                output.WriteLine($"  → Exited on {aircraft.CurrentTaxiway}, hdg={aircraft.TrueHeading.Degrees:F0}°, t={t}s");
                break;
            }

            // Compute along-track distance to each branch node (positive = ahead, negative = behind)
            var distances = new List<string>();
            foreach (var (twy, lat, lon, angle) in branchPositions)
            {
                double alongTrack = GeoMath.AlongTrackDistanceNm(lat, lon, aircraft.Position.Lat, aircraft.Position.Lon, runway28R.TrueHeading);
                string marker = alongTrack > 0 ? $"{alongTrack:F2}" : "PAST";
                distances.Add($"{marker, 4}");
            }

            output.WriteLine($"{t, 4} | {phase, -12} | {aircraft.GroundSpeed, 7:F1} | {string.Join(" | ", distances)}");
        }
    }

    /// <summary>
    /// Land with no exit instruction. Aircraft should pick an exit and complete
    /// a smooth turn to the hold-short.
    /// </summary>
    [Fact]
    public void NoExitInstruction_ExitsSmoothly()
    {
        var result = RunExitTest(exitCommand: null);
        if (result is null)
        {
            return;
        }

        Assert.NotNull(result.FinalTaxiway);
        output.WriteLine(
            $"Default exit: {result.FinalTaxiway}, hdg={result.FinalHeading:F0}°, turn={result.TotalHeadingChange:F0}°, total={result.TotalSeconds}s"
        );
        AssertSmoothExit(result, "default");
    }

    /// <summary>
    /// Test each named exit from 28R. Exits near the threshold will be too fast —
    /// the pilot should say unable and pick a later exit. Exits further down
    /// should produce clean turns.
    /// </summary>
    [Theory]
    [InlineData("C")]
    [InlineData("C2")]
    [InlineData("N")]
    [InlineData("P")]
    [InlineData("L")]
    [InlineData("T")]
    [InlineData("D")]
    [InlineData("Q")]
    [InlineData("K")]
    [InlineData("R")]
    [InlineData("S2")]
    [InlineData("S1")]
    [InlineData("C3")]
    public void ExitAt_ProducesSmoothTurn(string taxiway)
    {
        var result = RunExitTest(exitCommand: $"EXIT {taxiway}");
        if (result is null)
        {
            return;
        }

        output.WriteLine(
            $"EXIT {taxiway}: actual={result.FinalTaxiway}, hdg={result.FinalHeading:F0}°, "
                + $"turn={result.TotalHeadingChange:F0}°, exitTime={result.ExitDurationSeconds}s, total={result.TotalSeconds}s"
        );

        if (result.FinalTaxiway != taxiway)
        {
            // Aircraft exited on a different taxiway — this is expected for exits
            // too close to the threshold (C, C2, N, P for a B738). The preference
            // relaxes and the pilot picks the next reachable exit.
            output.WriteLine($"  Relaxed: requested {taxiway}, actual {result.FinalTaxiway}");
            Assert.NotNull(result.FinalTaxiway);

            int requestedIdx = Array.IndexOf(ExitsThresholdOrder, taxiway);
            int actualIdx = Array.IndexOf(ExitsThresholdOrder, result.FinalTaxiway);
            Assert.True(actualIdx > requestedIdx, $"Aircraft exited at {result.FinalTaxiway} which is not further down the runway than {taxiway}");
        }

        AssertSmoothExit(result, taxiway);
    }

    private void AssertSmoothExit(ExitTestResult result, string label)
    {
        // Heading changes should be mostly monotonic. SFO 28R is 200 ft wide
        // (ADG V/VI), so hold-short nodes sit ~280 ft from centerline per
        // FAA AC 150/5300-13B Table 3-2. The longer exit taxi past the fillet
        // can produce minor heading corrections at the arc-to-straight transition.
        Assert.True(
            result.MaxReversal < 12.0,
            $"[{label}] Heading reversal of {result.MaxReversal:F1}° detected at t={result.MaxReversalTime}s "
                + $"(hdg went {result.HeadingBeforeReversal:F0}° → {result.HeadingAtReversal:F0}°). "
                + "Exit turn should be mostly monotonic."
        );

        // The exit should complete within a reasonable time
        Assert.True(result.ExitDurationSeconds <= 90, $"[{label}] Exit took {result.ExitDurationSeconds}s — should complete in under 90s");
    }

    private ExitTestResult? RunExitTest(string? exitCommand)
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        var navDb = NavigationDatabase.Instance;
        var runway28R = navDb.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        // Spawn B738 on 1nm final for 28R
        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TST738",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.ElevationFt + 318, // ~3° glide slope at 1nm
            IndicatedAirspeed = 145,
            IsOnGround = false,
            Departure = "SFO",
            Destination = "SFO",
            FlightRules = "IFR",
            CruiseAltitude = 3000,
        };

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        aircraft.Phases = new PhaseList { AssignedRunway = runway28R };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.GroundLayout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-28r-exits",
            ScenarioName = "SFO 28R Exit Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        // Send cleared to land
        var clearResult = engine.SendCommand("TST738", "CLAND");
        Assert.True(clearResult.Success, $"CLAND failed: {clearResult.Message}");

        // Send exit command if specified
        if (exitCommand is not null)
        {
            var exitResult = engine.SendCommand("TST738", exitCommand);
            Assert.True(exitResult.Success, $"{exitCommand} failed: {exitResult.Message}");
        }

        // Tick through the entire landing + exit lifecycle, tracking heading
        // and unable broadcasts across ALL phases.
        var headingSamples = new List<(int Time, double Heading)>();
        int exitStartTime = -1;
        int exitEndTime = -1;
        string? unableMessage = null;
        bool inExitPhase = false;
        double initialExitHeading = 0;

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "none";

            // Check for "unable" pilot broadcasts across ALL phases
            foreach (string warning in aircraft.PendingWarnings)
            {
                if (warning.Contains("unable", StringComparison.OrdinalIgnoreCase))
                {
                    unableMessage ??= warning;
                    output.WriteLine($"  t+{t}s: {warning}");
                }
            }

            if (phase == "Runway Exit" && !inExitPhase)
            {
                inExitPhase = true;
                exitStartTime = t;
                initialExitHeading = aircraft.TrueHeading.Degrees;
            }

            if (inExitPhase)
            {
                headingSamples.Add((t, aircraft.TrueHeading.Degrees));
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

        // Analyze heading monotonicity
        double maxReversal = 0;
        int maxReversalTime = 0;
        double headingBeforeReversal = 0;
        double headingAtReversal = 0;

        if (headingSamples.Count >= 3)
        {
            // Determine turn direction from the overall heading change
            double startHdg = headingSamples[0].Heading;
            double endHdg = headingSamples[^1].Heading;
            double overallChange = NormalizeAngle(endHdg - startHdg);
            bool turningRight = overallChange > 0;

            for (int i = 1; i < headingSamples.Count; i++)
            {
                double delta = NormalizeAngle(headingSamples[i].Heading - headingSamples[i - 1].Heading);
                bool thisStepRight = delta > 0;

                // A reversal is when the aircraft turns opposite to the overall direction
                if ((thisStepRight != turningRight) && (Math.Abs(delta) > maxReversal))
                {
                    maxReversal = Math.Abs(delta);
                    maxReversalTime = headingSamples[i].Time;
                    headingBeforeReversal = headingSamples[i - 1].Heading;
                    headingAtReversal = headingSamples[i].Heading;
                }
            }
        }

        double totalHeadingChange = headingSamples.Count >= 2 ? Math.Abs(NormalizeAngle(headingSamples[^1].Heading - headingSamples[0].Heading)) : 0;

        return new ExitTestResult
        {
            FinalTaxiway = aircraft.CurrentTaxiway,
            FinalHeading = aircraft.TrueHeading.Degrees,
            InitialExitHeading = initialExitHeading,
            TotalHeadingChange = totalHeadingChange,
            TotalSeconds = exitEndTime,
            ExitDurationSeconds = exitEndTime - exitStartTime,
            MaxReversal = maxReversal,
            MaxReversalTime = maxReversalTime,
            HeadingBeforeReversal = headingBeforeReversal,
            HeadingAtReversal = headingAtReversal,
            UnableMessage = unableMessage,
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

    private void TickUntil(SimulationEngine engine, AircraftState aircraft, int maxSeconds, string description, Func<AircraftState, bool> condition)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            if (condition(aircraft))
            {
                output.WriteLine($"  [{description}] at t+{t}s");
                return;
            }
        }

        Assert.Fail(
            $"[{description}] not achieved within {maxSeconds}s. " + $"phase={aircraft.Phases?.CurrentPhase?.Name}, gs={aircraft.GroundSpeed:F1}"
        );
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("C3")]
    public void Diagnostic_DumpTickCsv(string exitTaxiway)
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
            .InitializeSimLog();
        var engine = new SimulationEngine(groundData);

        var navDb = NavigationDatabase.Instance;
        var runway28R = navDb.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TST738",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.ElevationFt + 318,
            IndicatedAirspeed = 145,
            IsOnGround = false,
            Departure = "SFO",
            Destination = "SFO",
            FlightRules = "IFR",
            CruiseAltitude = 3000,
        };

        var layout = groundData.GetLayout("SFO");
        Assert.NotNull(layout);

        aircraft.Phases = new PhaseList { AssignedRunway = runway28R };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.GroundLayout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-dump",
            ScenarioName = "SFO Tick Dump",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        var clearResult = engine.SendCommand("TST738", "CLAND");
        Assert.True(clearResult.Success);
        var exitResult = engine.SendCommand("TST738", $"EXIT {exitTaxiway}");
        Assert.True(exitResult.Success);

        var recorder = new TickRecorder(aircraft);
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            recorder.Record(t);

            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "none";
            if (phase.Contains("Hold"))
            {
                for (int extra = 1; extra <= 3; extra++)
                {
                    engine.TickOneSecond();
                    recorder.Record(t + extra);
                }

                break;
            }
        }

        string repoRoot = TickRecorder.FindRepoRoot();
        string csvPath = Path.Combine(repoRoot, ".tmp", $"sfo-28R-{exitTaxiway}-ticks.csv");
        recorder.WriteCsv(csvPath);
        output.WriteLine($"Wrote {recorder.Count} ticks to {csvPath}");
    }

    private sealed class ExitTestResult
    {
        public required string? FinalTaxiway { get; init; }
        public required double FinalHeading { get; init; }
        public required double InitialExitHeading { get; init; }
        public required double TotalHeadingChange { get; init; }
        public required int TotalSeconds { get; init; }
        public required int ExitDurationSeconds { get; init; }
        public required double MaxReversal { get; init; }
        public required int MaxReversalTime { get; init; }
        public required double HeadingBeforeReversal { get; init; }
        public required double HeadingAtReversal { get; init; }
        public required string? UnableMessage { get; init; }
    }
}
