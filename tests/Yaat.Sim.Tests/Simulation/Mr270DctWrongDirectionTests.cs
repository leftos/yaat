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
    internal const string RecordingPath = "TestData/mr270-dct-wrong-direction-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N172SP";

    // From snapshot inspection of the recording (V1 timing):
    //   t=495: InitialClimb exits, hdg=202.3°T, PreferredTurnDirection=Right
    //   t=510: hdg=247.3°T (still turning right — bug); fix should have hdg < 202.3°T
    //   t=525: hdg=292.3°T (continuing right past rollout — bug)
    //   t=529: FHN 170 forced recovery
    //
    // The exact rollout second is ground-timing-dependent (the V2 nav stack taxis out
    // slower, pushing takeoff and the whole departure ~5 s later), so the test detects
    // the actual InitialClimbPhase exit rather than hard-coding it. ClimbWindowStart is
    // chosen early enough that the aircraft is airborne and still in InitialClimbPhase
    // under both V1 and V2 timing; PostRolloutWindow is how long after the exit to verify
    // the recovery turn and that the turn bias stayed cleared.
    private const int ClimbWindowStart = 460;
    private const int PostRolloutWindow = 25;
    private const int SettleSeconds = 15; // ticks after exit for the left turn to OAK30NUM to settle

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
    /// After the 270-degree right departure turn completes and DCT OAK30NUM activates, the
    /// aircraft must turn the SHORT way to OAK30NUM (left from ~202 deg toward ~178 deg) — not
    /// continue the right turn. The test detects the actual InitialClimbPhase exit (rollout) so it
    /// is robust to ground-timing differences, then verifies the heading settles turning LEFT
    /// toward OAK30NUM (rather than continuing right past rollout) and PreferredTurnDirection is
    /// cleared once the departure phase ends. With the bug, the leftover Right bias forced the
    /// long-way right turn (heading climbing past 260 deg) and PreferredTurnDirection stayed set.
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

        AssertTurnsLeftAfterRollout(engine, recording, output);
    }

    /// <summary>
    /// Replay through the departure, detect the InitialClimbPhase exit, and assert the recovery
    /// turn behaviour. Shared by the V1-default test above and the all-V2 variant in
    /// <see cref="Mr270DctUnderV2Tests"/> so both ground-timing models are covered.
    /// </summary>
    internal static void AssertTurnsLeftAfterRollout(SimulationEngine engine, SessionRecording recording, ITestOutputHelper output)
    {
        // Replay to a point where the aircraft is airborne and still in its departure climb
        // under either ground-timing model, then tick forward until InitialClimbPhase exits.
        engine.Replay(recording, ClimbWindowStart);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.True(ac.Phases?.CurrentPhase is InitialClimbPhase, $"{Callsign} should still be in InitialClimbPhase at t={ClimbWindowStart}");

        double rolloutHeading = double.NaN;
        int rolloutSecond = -1;
        double lastClimbHeading = ac.TrueHeading.Degrees;

        // Phase 1: advance until the departure climb exits, capturing the rollout heading.
        for (int t = ClimbWindowStart + 1; t <= ClimbWindowStart + 120; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            if (ac.Phases?.CurrentPhase is InitialClimbPhase)
            {
                lastClimbHeading = ac.TrueHeading.Degrees;
                continue;
            }

            rolloutHeading = lastClimbHeading;
            rolloutSecond = t;
            output.WriteLine(
                $"InitialClimb exited at t={t}: rolloutHdg={rolloutHeading:F2} "
                    + $"tgtHdg={ac.Targets.TargetTrueHeading?.Degrees.ToString("F2") ?? "(none)"} "
                    + $"preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"} "
                    + $"route=[{string.Join(",", ac.Targets.NavigationRoute.Select(n => n.Name))}]"
            );
            break;
        }

        Assert.True(rolloutSecond > 0, "InitialClimbPhase never exited within the scan window");

        double headingAtSettle = double.NaN;
        bool preferredSetAfterRollout = false;

        // Phase 2: from the exit onward, verify the recovery turn and that the turn bias stayed cleared.
        for (int t = rolloutSecond; t <= rolloutSecond + PostRolloutWindow; t++)
        {
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            if (ac.Targets.PreferredTurnDirection is not null)
            {
                preferredSetAfterRollout = true;
            }

            if (t == rolloutSecond + SettleSeconds)
            {
                headingAtSettle = ac.TrueHeading.Degrees;
            }

            if (((t - rolloutSecond) % 5 == 0) || (t == rolloutSecond + SettleSeconds))
            {
                output.WriteLine(
                    $"t={t} (+{t - rolloutSecond}): hdg={ac.TrueHeading.Degrees, 7:F2} "
                        + $"tgtHdg={ac.Targets.TargetTrueHeading?.Degrees.ToString("F2") ?? "(none)"} "
                        + $"preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"} "
                        + $"bank={ac.BankAngle, 6:F2}"
                );
            }

            engine.ReplayOneSecond();
        }

        // Primary assertion: once settled, the aircraft is turning LEFT (heading decreasing from
        // rollout toward bearing-to-OAK30NUM ~178 deg), NOT continuing right past rollout. The +1 deg
        // margin covers the residual heading change during the snap tick; the >=350 branch covers a
        // left turn that wraps below 0/360.
        Assert.True(
            headingAtSettle <= rolloutHeading + 1.0 || headingAtSettle >= 350.0,
            $"At rollout+{SettleSeconds}s, heading should be turning LEFT from rollout (<= {rolloutHeading + 1.0:F1}) "
                + $"toward OAK30NUM, but was {headingAtSettle:F2} - long-way right turn detected"
        );

        // Secondary assertion: PreferredTurnDirection must be cleared once the departure phase ends
        // (HeadingToleranceDeg vs HeadingSnapDeg mismatch leaves it set otherwise).
        Assert.False(
            preferredSetAfterRollout,
            "PreferredTurnDirection should be cleared after InitialClimb exits but was still set during the post-rollout window"
        );
    }
}

/// <summary>
/// All-V2 variant of <see cref="Mr270DctWrongDirectionTests"/>. Under the V2 nav stack N172SP taxis
/// out slower, so takeoff and the whole departure shift ~5 s later — the original V1-pinned sample
/// window caught the tail of InitialClimbPhase (where PreferredTurnDirection=Right is still valid).
/// The dynamic-exit detection in <see cref="Mr270DctWrongDirectionTests.AssertTurnsLeftAfterRollout"/>
/// is timing-agnostic, so the recovery turn and turn-bias clearing are verified at whatever second the
/// climb actually exits. Runs in the parallelization-disabled "V2 Acceptance" collection.
/// </summary>
[Collection("V2 Acceptance")]
public class Mr270DctUnderV2Tests(ITestOutputHelper output)
{
    [Fact]
    public void N172SP_TurnsLeftToOak30numAfterMr270Rollout_OnV2()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = RecordingLoader.Load(Mr270DctWrongDirectionTests.RecordingPath);
        if (recording is null)
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(Yaat.Sim.Data.Airport.FilletMode.Standard));
        Mr270DctWrongDirectionTests.AssertTurnsLeftAfterRollout(engine, recording, output);
    }
}
