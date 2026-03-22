using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for FinalApproach speed bug: aircraft entering FinalApproachPhase
/// should not immediately decelerate to FAS. Speed should be maintained until
/// within 5nm of the threshold, then decelerate to FAS.
///
/// Recording: S2-OAK-4 VFR Transitions / Radar Concepts — FDX3807 (B763/L)
/// intercepts OAK 30 at 12nm. Before the fix, it would immediately slow to
/// FAS (140kts) from 224kts at 12nm.
/// </summary>
public class FinalApproachSpeedTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/fas-too-early-recording.yaat-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// FDX3807 enters FinalApproach at 12nm. At 7nm (well above 5nm threshold),
    /// the aircraft should still be above FAS — not decelerating prematurely.
    /// </summary>
    [Fact]
    public void FDX3807_MaintainsSpeedAboveFasUntil5nm()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 300);

        // Advance until FDX3807 is on FinalApproach and at ~7nm
        for (int t = 1; t <= 1000; t++)
        {
            engine.ReplayOneSecond();

            var ac = engine.FindAircraft("FDX3807");
            if (ac is null)
            {
                continue;
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "";
            if (phase != "FinalApproach")
            {
                continue;
            }

            var runway = ac.Phases?.AssignedRunway;
            if (runway is null)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, runway.ThresholdLatitude, runway.ThresholdLongitude);

            // Check at 7nm: aircraft should still be well above FAS
            if ((distNm <= 7.5) && (distNm >= 6.5))
            {
                var cat = AircraftCategorization.Categorize(ac.AircraftType);
                double fas = AircraftPerformance.ApproachSpeed(ac.AircraftType, cat);

                output.WriteLine(
                    $"At {distNm:F1}nm: ias={ac.IndicatedAirspeed:F0}kts, fas={fas:F0}kts, "
                        + $"tgtSpd={ac.Targets.TargetSpeed?.ToString("F0") ?? "null"}"
                );

                Assert.True(
                    ac.IndicatedAirspeed > fas + 30,
                    $"At {distNm:F1}nm from threshold, aircraft should not have started decelerating to FAS yet. "
                        + $"IAS={ac.IndicatedAirspeed:F0}kts, FAS={fas:F0}kts"
                );
                return;
            }
        }

        Assert.Fail("FDX3807 never reached 7nm on FinalApproach");
    }

    /// <summary>
    /// FDX3807 should decelerate to FAS within the last 5nm and reach FAS
    /// by the time it's at ~2nm from the threshold.
    /// </summary>
    [Fact]
    public void FDX3807_DeceleratesToFasWithin5nm()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 300);

        for (int t = 1; t <= 1200; t++)
        {
            engine.ReplayOneSecond();

            var ac = engine.FindAircraft("FDX3807");
            if (ac is null)
            {
                continue;
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "";
            if (phase != "FinalApproach")
            {
                continue;
            }

            var runway = ac.Phases?.AssignedRunway;
            if (runway is null)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, runway.ThresholdLatitude, runway.ThresholdLongitude);

            // At 2nm the aircraft should have reached FAS
            if ((distNm <= 2.2) && (distNm >= 1.8))
            {
                var cat = AircraftCategorization.Categorize(ac.AircraftType);
                double fas = AircraftPerformance.ApproachSpeed(ac.AircraftType, cat);

                output.WriteLine($"At {distNm:F1}nm: ias={ac.IndicatedAirspeed:F0}kts, fas={fas:F0}kts");

                Assert.True(
                    ac.IndicatedAirspeed <= fas + 10,
                    $"At {distNm:F1}nm from threshold, aircraft should be at or near FAS. " + $"IAS={ac.IndicatedAirspeed:F0}kts, FAS={fas:F0}kts"
                );
                return;
            }
        }

        Assert.Fail("FDX3807 never reached 2nm on FinalApproach");
    }
}
