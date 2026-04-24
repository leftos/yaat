using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Scenarios;
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
    private const string RecordingPath = "TestData/af532381c459.zip";

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

        // SKW3398 spawns at t=0 with CFIX CEPIN 3000 210 presets.
        // CEPIN is sequenced at ~t=99, so start early enough to observe it.
        engine.Replay(recording, 50);

        var initialAc = engine.FindAircraft("SKW3398");
        bool cepinWasInRoute = initialAc?.Targets.NavigationRoute.Any(f => f.Name == "CEPIN") ?? false;
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
                cepinSequencedAt = 50 + t;
                output.WriteLine(
                    $"CEPIN sequenced at t={cepinSequencedAt}: ias={aircraft.IndicatedAirspeed:F0} "
                        + $"tgtSpd={aircraft.Targets.TargetSpeed?.ToString() ?? "null"}"
                );
            }

            cepinWasInRoute = cepinInRoute;

            // Within 10 seconds of CEPIN being sequenced, the speed target
            // must be 180 from the AT CEPIN SPD 180 command.
            if (cepinSequencedAt is not null && (50 + t) - cepinSequencedAt.Value <= 10)
            {
                if (aircraft.Targets.TargetSpeed == 180)
                {
                    output.WriteLine($"PASS: TargetSpeed=180 at t={50 + t}, " + $"{(50 + t) - cepinSequencedAt.Value}s after CEPIN sequenced");
                    return;
                }
            }

            // If 10 seconds passed since CEPIN sequenced and we didn't see 180, fail
            if (cepinSequencedAt is not null && (50 + t) - cepinSequencedAt.Value > 10)
            {
                Assert.Fail(
                    $"AT CEPIN SPD 180 trigger did not fire within 10s of CEPIN being sequenced at t={cepinSequencedAt}. "
                        + $"Current tgtSpd={aircraft.Targets.TargetSpeed?.ToString() ?? "null"}"
                );
            }
        }

        Assert.NotNull(cepinSequencedAt);
    }

    /// <summary>
    /// Full speed profile for SKW3398 from spawn through landing on 28R.
    /// Tests original presets and two corrected variants that defer CAPP to CEPIN.
    ///
    /// For override presets: replays the recording to capture SKW3398's spawn position,
    /// then creates a fresh aircraft at that position and dispatches the new presets.
    /// </summary>
    [Theory]
    [InlineData(null, "original recording presets")]
    [InlineData("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R; AT CEPIN SPD 180 AXMUL", "separate AT triggers")]
    [InlineData("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R, SPD 180 AXMUL", "parallel AT trigger")]
    public void Diagnostic_SKW3398_SpeedProfile(string? overridePreset, string label)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        output.WriteLine($"=== {label} ===\n");

        if (overridePreset is not null)
        {
            engine.PresetOverride = loaded =>
            {
                if (loaded.State.Callsign == "SKW3398")
                {
                    loaded.PresetCommands.Clear();
                    loaded.PresetCommands.Add(new PresetCommand { Command = overridePreset });
                }
            };
        }

        engine.Replay(recording, 0);

        var skw = engine.FindAircraft("SKW3398");
        Assert.NotNull(skw);

        output.WriteLine(
            $"Spawn: IAS={skw.IndicatedAirspeed:F0} alt={skw.Altitude:F0} "
                + $"phase={skw.Phases?.CurrentPhase?.Name ?? "-"} route={skw.Targets.NavigationRoute.Count} "
                + $"tgtSpd={skw.Targets.TargetSpeed?.ToString("F0") ?? "null"} asnSpd={skw.Targets.AssignedSpeed?.ToString("F0") ?? "null"}\n"
        );

        // SFO 28R threshold
        double threshLat = 37.6131,
            threshLon = -122.3575;

        output.WriteLine($"{"t", 5} {"IAS", 7} {"tgtSpd", 7} {"asnSpd", 7} {"alt", 7} {"phase", 18} {"distThr", 8} {"route", 5} {"event", 0}");
        output.WriteLine(new string('-', 100));

        bool hadCepin = skw.Targets.NavigationRoute.Any(f => f.Name == "CEPIN");
        bool hadAxmul = skw.Targets.NavigationRoute.Any(f => f.Name == "AXMUL");
        bool logged5nm = false;

        for (int t = 1; t <= 800; t++)
        {
            engine.ReplayOneSecond();

            var aircraft = engine.FindAircraft("SKW3398");
            if (aircraft is null)
            {
                output.WriteLine($"{t, 5} -- aircraft deleted --");
                break;
            }

            bool hasCepin = aircraft.Targets.NavigationRoute.Any(f => f.Name == "CEPIN");
            bool hasAxmul = aircraft.Targets.NavigationRoute.Any(f => f.Name == "AXMUL");
            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "-";
            double distThr = GeoMath.DistanceNm(aircraft.Position, new LatLon(threshLat, threshLon));
            string evt = "";

            if (hadCepin && !hasCepin)
            {
                evt = "<<< CEPIN sequenced";
            }

            if (hadAxmul && !hasAxmul)
            {
                evt = "<<< AXMUL sequenced";
            }

            if (!logged5nm && distThr <= 5.0)
            {
                evt = "<<< 5nm from threshold";
                logged5nm = true;
            }

            if (aircraft.IsOnGround && t > 10)
            {
                output.WriteLine(
                    $"{t, 5} {aircraft.IndicatedAirspeed, 7:F1} "
                        + $"{aircraft.Targets.TargetSpeed?.ToString("F1") ?? "null", 7} "
                        + $"{aircraft.Targets.AssignedSpeed?.ToString("F0") ?? "null", 7} "
                        + $"{aircraft.Altitude, 7:F0} {phase, 18} {distThr, 8:F2} {aircraft.Targets.NavigationRoute.Count, 5} <<< TOUCHDOWN"
                );
                break;
            }

            if (t % 10 == 0 || t <= 3 || evt.Length > 0)
            {
                output.WriteLine(
                    $"{t, 5} {aircraft.IndicatedAirspeed, 7:F1} "
                        + $"{aircraft.Targets.TargetSpeed?.ToString("F1") ?? "null", 7} "
                        + $"{aircraft.Targets.AssignedSpeed?.ToString("F0") ?? "null", 7} "
                        + $"{aircraft.Altitude, 7:F0} {phase, 18} {distThr, 8:F2} {aircraft.Targets.NavigationRoute.Count, 5} {evt}"
                );
            }

            hadCepin = hasCepin;
            hadAxmul = hasAxmul;
        }
    }
}
