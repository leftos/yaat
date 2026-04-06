using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for AT fix triggers firing during active phases (e.g., approach).
///
/// Bug: "AT CEPIN SPD 180 AXMUL" preset never fires because UpdateCommandQueue
/// is entirely skipped when phases are active. The aircraft flies through CEPIN
/// via turn anticipation and the ReachFix trigger never evaluates.
///
/// Fix: NotifyFixSequenced fires AT fix triggers when the navigation route or
/// approach phase sequences past a fix, regardless of phase state.
///
/// Recording: S1-SFO-2 | Ground Control 28/01 — SKW3398 on approach to 28R
/// with presets "CFIX CEPIN 3000 210; CAPP 28R; AT CEPIN SPD 180 AXMUL".
/// </summary>
public class AtFixTriggerDuringPhasesTests(ITestOutputHelper output)
{
    private const string RecordingPath =
        "TestData/sfo-ground-spd-until-bundle.yaat-bug-report-bundle.zip";

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
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After CEPIN is sequenced from the approach nav route, the AT CEPIN block
    /// in the command queue must fire and set TargetSpeed=180. The speed reduction
    /// must happen within a few seconds of sequencing — not later when the approach
    /// speed kicks in at ~126kts.
    /// </summary>
    [Fact]
    public void SKW3398_SlowsTo180AtCepin()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // SKW3398 spawns at t=0, approaches CEPIN on the CAPP 28R approach.
        engine.Replay(recording, 100);

        bool cepinWasInRoute = false;
        int? cepinSequencedAt = null;

        for (int t = 1; t <= 400; t++)
        {
            engine.ReplayOneSecond();
            var aircraft = engine.FindAircraft("SKW3398");
            if (aircraft is null)
            {
                continue;
            }

            bool cepinInRoute = aircraft.Targets.NavigationRoute.Any(f => f.Name == "CEPIN");

            // Detect when CEPIN is sequenced
            if (cepinWasInRoute && !cepinInRoute && cepinSequencedAt is null)
            {
                cepinSequencedAt = 100 + t;
                output.WriteLine(
                    $"CEPIN sequenced at t={cepinSequencedAt}: ias={aircraft.IndicatedAirspeed:F0} "
                    + $"tgtSpd={aircraft.Targets.TargetSpeed?.ToString() ?? "null"}");
            }

            cepinWasInRoute = cepinInRoute;

            // Within 10 seconds of CEPIN being sequenced, the speed target
            // must be 180 from the AT CEPIN SPD 180 command.
            if (cepinSequencedAt is not null && (100 + t) - cepinSequencedAt.Value <= 10)
            {
                if (aircraft.Targets.TargetSpeed == 180)
                {
                    output.WriteLine(
                        $"PASS: TargetSpeed=180 at t={100 + t}, "
                        + $"{(100 + t) - cepinSequencedAt.Value}s after CEPIN sequenced");
                    return;
                }
            }

            // If 10 seconds passed since CEPIN sequenced and we didn't see 180, fail
            if (cepinSequencedAt is not null && (100 + t) - cepinSequencedAt.Value > 10)
            {
                Assert.Fail(
                    $"AT CEPIN SPD 180 trigger did not fire within 10s of CEPIN being sequenced at t={cepinSequencedAt}. "
                    + $"Current tgtSpd={aircraft.Targets.TargetSpeed?.ToString() ?? "null"}");
            }
        }

        Assert.NotNull(cepinSequencedAt);
    }
}
