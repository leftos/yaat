using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N172SP (C172 VFR, KOAK → KSQL) was given the compound
/// "CTO MR270 014; DCT OAK30NUM VPMID" off runway 28R in recording
/// "S2-OAK-4 | VFR Transitions/Radar Concepts" at t=308. The aircraft
/// correctly performed the 270° right departure turn (rolling out at
/// ~202°T near t=495) but then continued turning right past rollout
/// instead of turning left toward OAK30NUM (bearing ~178°T from rollout
/// position). The controller forced recovery with FHN 170 at t=529.
///
/// Root cause: <see cref="InitialClimbPhase.ApplyDeferredVfrTurn"/>
/// sets <c>Targets.PreferredTurnDirection = Right</c> when the deferred
/// VFR turn fires. The phase exits at <c>HeadingToleranceDeg = 1.0°</c>,
/// but <see cref="FlightPhysics.UpdateHeading"/>'s snap (which clears
/// PreferredTurnDirection) requires &lt;0.5°. The phase commonly exits
/// inside the 1° band but outside the 0.5° snap band, so the Right
/// bias persists. When the queued DCT activates and
/// <see cref="FlightPhysics.UpdateNavigation"/> writes a new
/// TargetTrueHeading (bearing-to-fix), the leftover Right bias forces
/// the long way around.
/// </summary>
public class Mr270DctWrongDirectionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/mr270-dct-wrong-direction-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N172SP";

    // From snapshot inspection of the recording:
    //   t=495: InitialClimb exits, hdg=202.3°T, PreferredTurnDirection=Right
    //   t=510: hdg=247.3°T (still turning right — bug); fix should have hdg < 202.3°T
    //   t=525: hdg=292.3°T (continuing right past rollout — bug)
    //   t=529: FHN 170 forced recovery
    private const int RolloutSecond = 495;
    private const int AssertSecond = 515;
    private const int EndSecond = 528; // just before the user's FHN 170 recovery at 529

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("InitialClimbPhase", LogLevel.Debug)
            .EnableCategory("FlightPhysics", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After the 270° right departure turn completes at t=495 and DCT OAK30NUM
    /// activates, the aircraft must turn the SHORT way to OAK30NUM (left from
    /// 202°T toward ~178°T) — not continue the right turn. Assertion: at
    /// t=515 the true heading must be at-or-less-than the rollout heading
    /// (202.3°T) — i.e., heading has either snapped to OAK30NUM bearing or
    /// is moving left. With the bug, heading at t=515 is ~262°T.
    /// </summary>
    [Fact]
    public void N172SP_TurnsLeftToOak30numAfterMr270Rollout()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Replay to just after InitialClimb exits and DCT activates.
        engine.Replay(recording, RolloutSecond);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        double rolloutHeading = ac.TrueHeading.Degrees;
        output.WriteLine(
            $"t={RolloutSecond}: {Callsign} hdg={rolloutHeading:F2}°T "
                + $"tgtHdg={ac.Targets.TargetTrueHeading?.Degrees.ToString("F2") ?? "(none)"}°T "
                + $"preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"} "
                + $"route=[{string.Join(",", ac.Targets.NavigationRoute.Select(n => n.Name))}]"
        );

        double headingAtAssert = double.NaN;
        bool preferredEverSetAfterRollout = false;

        for (int t = RolloutSecond + 1; t <= EndSecond; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            if (ac.Targets.PreferredTurnDirection is not null)
            {
                preferredEverSetAfterRollout = true;
            }

            if (t == AssertSecond)
            {
                headingAtAssert = ac.TrueHeading.Degrees;
            }

            if (t % 5 == 0 || t == AssertSecond)
            {
                output.WriteLine(
                    $"t={t}: hdg={ac.TrueHeading.Degrees, 7:F2}°T "
                        + $"tgtHdg={ac.Targets.TargetTrueHeading?.Degrees.ToString("F2") ?? "(none)"}°T "
                        + $"preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"} "
                        + $"bank={ac.BankAngle, 6:F2}"
                );
            }
        }

        // Primary assertion: at t=515 the aircraft must be at-or-past-rollout going LEFT (heading
        // decreasing from 202°T toward bearing-to-OAK30NUM ~178°T), NOT continuing right.
        // Allow a small margin (+1°) for the residual heading change during the snap tick.
        Assert.True(
            headingAtAssert <= rolloutHeading + 1.0 || headingAtAssert >= 350.0,
            $"At t={AssertSecond}, heading should be turning LEFT from rollout (≤{rolloutHeading + 1.0:F1}°T) "
                + $"toward OAK30NUM, but was {headingAtAssert:F2}°T — long-way right turn detected"
        );

        // Secondary assertion: PreferredTurnDirection should be cleared once the departure
        // phase ends (HeadingToleranceDeg vs HeadingSnapDeg mismatch leaves it set otherwise).
        Assert.False(
            preferredEverSetAfterRollout,
            "PreferredTurnDirection should be cleared after InitialClimb exits but was still set during the post-rollout window"
        );
    }
}
