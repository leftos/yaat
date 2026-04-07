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

    private void LogResult(string label, string? requested, ExitResult result)
    {
        string relaxed = (requested is not null && result.FinalTaxiway != requested) ? $" (relaxed from {requested})" : "";
        output.WriteLine(
            $"EXIT {label}: actual={result.FinalTaxiway}{relaxed}, hdg={result.FinalHeading:F0}°, "
                + $"turn={result.TotalHeadingChange:F0}°, exitTime={result.ExitDurationSeconds}s, total={result.TotalSeconds}s"
        );
    }

    private void AssertSmoothExit(ExitResult result, string label)
    {
        Assert.NotNull(result.FinalTaxiway);

        Assert.True(
            result.MaxReversal < 5.0,
            $"[{label}] Heading reversal of {result.MaxReversal:F1}° at t={result.MaxReversalTime}s "
                + $"(hdg {result.HeadingBeforeReversal:F0}° → {result.HeadingAtReversal:F0}°). "
                + "Exit turn should be monotonic."
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
            Latitude = acLat,
            Longitude = acLon,
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + altAboveField,
            IndicatedAirspeed = touchdownSpeed,
            IsOnGround = false,
            Departure = airportId,
            Destination = airportId,
            FlightRules = "IFR",
            CruiseAltitude = 3000,
        };

        var layout = new TestAirportGroundData().GetLayout(airportId);
        Assert.NotNull(layout);

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
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
        int exitStartTime = -1;
        int exitEndTime = -1;
        bool inExitPhase = false;
        double initialExitHeading = 0;

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "none";

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
            double startHdg = headingSamples[0].Heading;
            double endHdg = headingSamples[^1].Heading;
            double overallChange = NormalizeAngle(endHdg - startHdg);
            bool turningRight = overallChange > 0;

            for (int i = 1; i < headingSamples.Count; i++)
            {
                double delta = NormalizeAngle(headingSamples[i].Heading - headingSamples[i - 1].Heading);
                bool thisStepRight = delta > 0;

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

        return new ExitResult
        {
            FinalTaxiway = aircraft.CurrentTaxiway,
            FinalHeading = aircraft.TrueHeading.Degrees,
            TotalHeadingChange = totalHeadingChange,
            TotalSeconds = exitEndTime,
            ExitDurationSeconds = exitEndTime - exitStartTime,
            MaxReversal = maxReversal,
            MaxReversalTime = maxReversalTime,
            HeadingBeforeReversal = headingBeforeReversal,
            HeadingAtReversal = headingAtReversal,
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
        public required double MaxReversal { get; init; }
        public required int MaxReversalTime { get; init; }
        public required double HeadingBeforeReversal { get; init; }
        public required double HeadingAtReversal { get; init; }
    }
}
