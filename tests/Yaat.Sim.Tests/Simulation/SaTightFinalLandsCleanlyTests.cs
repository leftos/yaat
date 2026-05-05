using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression test for the "SA goes around due to bank angle at flare" bug.
///
/// Recording: S2-OAK-3 | VFR Sequencing. N9225L (piston) is on right downwind for
/// OAK 28R. The controller issues SA at t=234 and CLAND at t=237. With the bundled
/// (pre-fix) Sim, BasePhase descends the aircraft to the 3-degree GS intercept
/// altitude (~168 ft AGL) before the base→final turn fires. By the time the
/// 90-degree base→final turn rolls out, the aircraft is at ~65 ft AGL, banked 16°,
/// still 6° off the runway final heading — LandingPhase's stabilization gate trips
/// (bank > 15°) and GoAround fires at t=300.
///
/// After the fix, N9225L must complete the short approach and land cleanly on
/// 28R without going around.
/// </summary>
public class SaTightFinalLandsCleanlyTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sa-tight-final-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N9225L";

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
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("BasePhase", LogLevel.Debug)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("FinalApproachPhase", LogLevel.Debug)
            .EnableCategory("LandingPhase", LogLevel.Debug)
            .EnableCategory("GoAroundHelper", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay the recording from t=0 with current code. The SA at t=234 + CLAND at
    /// t=237 + the existing pattern phase logic should land N9225L cleanly without
    /// inserting a GoAroundPhase. Today this fails — GoAround fires around t=300
    /// with reason "unstable: bank …".
    /// </summary>
    [Fact]
    public void N9225L_ShortApproach_LandsWithoutGoAround()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 0);

        bool sawDownwind = false;
        bool sawBase = false;
        bool sawFinal = false;
        bool sawLanding = false;
        bool exitedRunway = false;
        bool wentAround = false;
        string? lastPhaseLogged = null;

        // Replay through to t=420 — well past the t=300 GA event in the original
        // recording but with enough margin for a delayed landing rollout.
        for (int t = 1; t <= 420; t++)
        {
            engine.ReplayOneSecond();

            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                output.WriteLine($"t={t}: aircraft despawned");
                break;
            }

            var phases = ac.Phases?.Phases;
            if (phases is null)
            {
                continue;
            }

            // Detect any GoAroundPhase in the chain — installed at the moment GA fires.
            if (phases.OfType<GoAroundPhase>().Any())
            {
                wentAround = true;
                output.WriteLine(
                    $"t={t}: GoAroundPhase appeared — alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F1} bank={ac.BankAngle:F1} vs={ac.VerticalSpeed:F0}"
                );
                break;
            }

            var current = ac.Phases?.CurrentPhase;
            string currentName = current?.GetType().Name ?? "(none)";
            if (currentName != lastPhaseLogged)
            {
                output.WriteLine(
                    $"t={t}: phase={currentName} alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F1} bank={ac.BankAngle:F1} ias={ac.IndicatedAirspeed:F1}"
                );
                lastPhaseLogged = currentName;
            }
            else if (current is LandingPhase && (t % 5 == 0))
            {
                output.WriteLine(
                    $"t={t}: Landing alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F1} bank={ac.BankAngle:F1} vs={ac.VerticalSpeed:F0} ias={ac.IndicatedAirspeed:F1} onGround={ac.IsOnGround}"
                );
            }

            if (current is DownwindPhase)
            {
                sawDownwind = true;
            }
            if (current is BasePhase)
            {
                sawBase = true;
            }
            if (current is FinalApproachPhase)
            {
                sawFinal = true;
            }
            if (current is LandingPhase && ac.IsOnGround)
            {
                sawLanding = true;
            }

            if (current is RunwayExitPhase || current is TaxiingPhase)
            {
                exitedRunway = true;
                output.WriteLine(
                    $"t={t}: EXITED RUNWAY into {currentName} alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F1} pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5})"
                );
                break;
            }
        }

        Assert.True(sawDownwind, "Aircraft should have flown Downwind");
        Assert.True(sawBase, "Aircraft should have flown Base");
        Assert.True(sawFinal, "Aircraft should have flown FinalApproach");
        Assert.False(wentAround, $"{Callsign} should land cleanly on SA, not go around");
        Assert.True(sawLanding, $"{Callsign} should touch down in LandingPhase");
        Assert.True(exitedRunway, $"{Callsign} should cleanly exit the runway onto a taxiway after rollout");
    }
}
