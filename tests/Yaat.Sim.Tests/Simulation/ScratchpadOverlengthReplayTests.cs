using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E replay test for the over-length STARS scratchpad bug reproduced in
/// `n346g-scratchpad-overlength-recording` (scenario S2-OAK-5 (1) | Practical Exam
/// Preparation/Advanced Concepts, ZOA/OAK).
///
/// A student, via CRC, typed "N346G" and slewed on N436MS's track. STARS' implied-command
/// fallthrough routed the leftover text to <c>SP1 N346G</c> (recorded action at t=74:
/// <c>AS 3O SP1 N346G</c>). The SP1 handler stored the 5-character value with no length
/// validation, so the snapshot at t=90 shows <c>Scratchpad1 = "N346G"</c> on N436MS.
///
/// A STARS primary scratchpad holds at most 3 characters (4 if the facility enables
/// <c>Allow4CharacterScratchpad</c>; ZOA does not). After the fix, the over-length entry is
/// rejected with FORMAT and the scratchpad stays clear.
/// </summary>
public class ScratchpadOverlengthReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/e5c26ff62464.zip";
    private const string Callsign = "N436MS";

    // SP1 N346G is applied at t=74; a bare SP1 (clear/toggle) follows at t=160. Asserting at
    // t=90 lands cleanly between them, after the over-length entry would have been stored.
    private const int AssertElapsedS = 90;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

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

    [Fact]
    public void OverlengthScratchpad_IsRejected_NotStored()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, AssertElapsedS);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // The 5-character "N346G" must never have been stored as the primary scratchpad.
        Assert.NotEqual("N346G", aircraft.Stars.Scratchpad1);
        Assert.Null(aircraft.Stars.Scratchpad1);
    }
}
