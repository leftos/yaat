using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for EXIT K overshoot on SFO runway 28L.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — DAL2581 (A319/L) lands on 28L
/// with EXIT K at t=783. Old code: aircraft overshot K, rolled past it on the
/// centerline, and did a ~135° turn to reach the hold-short. New code should
/// have LandingPhase resolve K ahead, commit a ResolvedExitInfo, and hand off
/// to RunwayExitPhase which follows the exit path smoothly.
/// </summary>
public class ExitKOvershootTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/e55edd55bed7.zip";

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
    /// DAL2581 is given EXIT K at t=783 while on final approach to 28L.
    /// After exit, the aircraft should be on taxiway K with no heading reversal
    /// (heading change from runway heading should be ≤90°).
    /// </summary>
    [Fact]
    public void DAL2581_ExitsAtK_NoHeadingReversal()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=782 — just before the EXIT K command at t=783
        engine.Replay(recording, 782);

        var ac = engine.FindAircraft("DAL2581");
        Assert.NotNull(ac);

        // Send EXIT K manually to ensure the current code's dispatch handles it
        var result = engine.SendCommand("DAL2581", "EXIT K");
        output.WriteLine($"EXIT K result: success={result.Success}, message={result.Message}");
        Assert.True(result.Success, $"EXIT K command failed: {result.Message}");

        double runwayHeading = ac.TrueHeading.Degrees;
        output.WriteLine($"t=782: hdg={runwayHeading:F1} gs={ac.GroundSpeed:F0} phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        // Tick forward until exit completes or 600 seconds elapse
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("DAL2581");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            if (t % 30 == 0)
            {
                output.WriteLine(
                    $"t+{t}: gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0}"
                        + $" taxiway={ac.Ground.CurrentTaxiway ?? "(none)"} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
                );
            }

            // CurrentTaxiway is set when RunwayExitPhase completes
            if (ac.Ground.CurrentTaxiway is not null)
            {
                double finalHeading = ac.TrueHeading.Degrees;
                double headingChange = new TrueHeading(finalHeading).AbsAngleTo(new TrueHeading(runwayHeading));

                output.WriteLine(
                    $"t+{t}: exited at taxiway {ac.Ground.CurrentTaxiway} hdg={finalHeading:F0}"
                        + $" (change={headingChange:F0}° from rwy hdg {runwayHeading:F0})"
                );

                // Aircraft should exit at K
                Assert.Equal("K", ac.Ground.CurrentTaxiway, StringComparer.OrdinalIgnoreCase);

                // No near-180 reversal — heading change should be ≤100°.
                // K at SFO is a ~90° perpendicular taxiway, so 90° is expected.
                // The old broken behavior was ~135° (overshoot + reversal).
                Assert.True(headingChange <= 100, $"Heading change {headingChange:F0}° exceeds 100° — aircraft likely overshot and reversed");
                return;
            }
        }

        Assert.Fail("DAL2581 never exited the runway within 600 seconds");
    }
}
