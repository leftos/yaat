using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for FinalApproach speed scheduling. The phase has two kinematic gates:
///   1. Configuration gate (1.3 * Vref) reached by ~5 NM — heavies bleed off intercept
///      speed before they're inside the stabilized-approach window.
///   2. Vref gate (FAS) reached by ~2 NM.
///
/// Recordings: S2-OAK-4 VFR Transitions / Radar Concepts — FDX3807 (B763) on a 12 NM
/// final to OAK 30. Two recordings exist because they capture different bugs and the
/// older one ends before FDX3807 reaches 5 NM.
/// </summary>
public class FinalApproachSpeedTests(ITestOutputHelper output)
{
    /// <summary>
    /// Older session: only carries FDX3807 to ~7 NM before the recording ends.
    /// Used by the "don't decel too early at 7 NM" and "at FAS by 2 NM" checks.
    /// </summary>
    private const string RecordingPath = "TestData/f8e389804194.zip";

    /// <summary>
    /// Full session through landing — used for the configuration-gate assertion at 5 NM.
    /// In this session FDX3807 spawns at 224 KIAS and (pre-fix) flat-lines that speed
    /// until ~5 NM before decelerating in one bleed.
    /// </summary>
    private const string FullRecordingPath = "TestData/10797ffbbfea.zip";

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
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

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

            double distNm = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude);

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

            double distNm = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude);

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

    /// <summary>
    /// FDX3807 spawns OnFinal at 12 NM doing 224 KIAS (1.6 * Vref). By the time it
    /// reaches the stabilized-approach window (~5 NM), it should already be at the
    /// configuration band (~1.3 * Vref ≈ 182 KIAS for B763) — not still cruising at
    /// intercept speed. Pre-fix the aircraft holds 224 until ~5 NM then bleeds in
    /// one shot to Vref; post-fix the configuration gate kicks in around 6-7 NM and
    /// the aircraft is settled at config speed by 5 NM.
    /// </summary>
    [Fact]
    public void FDX3807_ReachesConfigSpeedBy5nm()
    {
        var recording = RecordingLoader.Load(FullRecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1100);

        for (int t = 1; t <= 250; t++)
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

            double distNm = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude);

            if ((distNm <= 5.0) && (distNm >= 4.5))
            {
                var cat = AircraftCategorization.Categorize(ac.AircraftType);
                double fas = AircraftPerformance.ApproachSpeed(ac.AircraftType, cat);
                double configBand = (fas * 1.3) + 5.0;

                output.WriteLine(
                    $"At {distNm:F2}nm: ias={ac.IndicatedAirspeed:F0}kts, fas={fas:F0}kts, configBand≤{configBand:F0}kts, "
                        + $"tgtSpd={ac.Targets.TargetSpeed?.ToString("F0") ?? "null"}"
                );

                Assert.True(
                    ac.IndicatedAirspeed <= configBand,
                    $"At {distNm:F2}nm from threshold, aircraft should be at or below configuration speed (~{fas * 1.3:F0}kts). "
                        + $"IAS={ac.IndicatedAirspeed:F0}kts, FAS={fas:F0}kts"
                );
                return;
            }
        }

        Assert.Fail("FDX3807 never reached 5nm on FinalApproach");
    }

    /// <summary>
    /// The configuration gate must not undershoot Vref. If current IAS is already at or
    /// below configSpeed (1.3 * Vref), the gate must short-circuit without commanding a
    /// deeper deceleration — only the FAS gate is allowed to target Vref. This guards
    /// against a regression where small jets that spawn slow get over-decelerated.
    /// </summary>
    [Fact]
    public void FDX3807_TargetSpeedNeverBelowVref()
    {
        var recording = RecordingLoader.Load(FullRecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1100);

        double? lowestTarget = null;
        double vref = 0;

        for (int t = 1; t <= 250; t++)
        {
            engine.ReplayOneSecond();

            var ac = engine.FindAircraft("FDX3807");
            if (ac is null)
            {
                continue;
            }

            if (vref <= 0)
            {
                var cat = AircraftCategorization.Categorize(ac.AircraftType);
                vref = AircraftPerformance.ApproachSpeed(ac.AircraftType, cat);
            }

            if (ac.Targets.TargetSpeed is { } tgt)
            {
                if (lowestTarget is null || tgt < lowestTarget.Value)
                {
                    lowestTarget = tgt;
                }
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "";
            if (phase == "Landing")
            {
                break;
            }
        }

        Assert.True(vref > 0, "Vref was never resolved (FDX3807 never appeared)");
        Assert.True(lowestTarget is null || lowestTarget.Value >= vref - 0.5, $"TargetSpeed dipped below Vref ({vref:F0}): lowest={lowestTarget:F0}");
    }
}
