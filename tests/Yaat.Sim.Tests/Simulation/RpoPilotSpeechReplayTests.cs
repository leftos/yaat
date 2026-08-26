using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the RPO-mode pilot-speech rendering setting.
///
/// Recording: S2-OAK-5 Practical Exam Preparation/Advanced Concepts (ZOA, RPO mode, 2270 s).
/// The session contains multiple sim-initiated pilot transmissions that the user originally
/// saw as orange Warning entries: traffic-in-sight (RTIS), field-in-sight (RFIS), midfield
/// reports, short-final-without-landing-clearance reminders, holding-short, clear-of-runway,
/// and going-around.
///
/// With <c>RpoShowPilotSpeech=true</c> set on the scenario, those events should land in
/// <c>AircraftState.PendingPilotSpeech</c> with the spelled-out spoken form built by
/// <c>PilotResponder</c>, instead of the terse controller-debug text in
/// <c>PendingWarnings</c>.
/// </summary>
public class RpoPilotSpeechReplayTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/a67670e50d58.zip";

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

    /// <summary>
    /// Replays the entire 2270-second session with the RPO pilot-speech setting flipped on
    /// and asserts that transmissions were routed to pilot speech across the run. The engine
    /// drains <c>PendingPilotSpeech</c> every tick, the same contract the server's TickProcessor
    /// implements, so the per-aircraft buffer is empty between ticks and
    /// <see cref="SimulationEngine.PilotSpeechEmitted"/> is what accumulates over the session.
    /// </summary>
    [Fact]
    public void RpoMode_PilotSpeechOn_RoutesSimInitiatedTransmissionsToPilotSpeech()
    {
        var recording = RecordingLoader.Load(BundlePath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine($"Skipped: {BundlePath} or test data not present");
            return;
        }

        // Collect the pilot-speech entries the engine emits as it drains them each tick.
        var allSpeech = new List<(string Callsign, string Speech)>();
        engine.PilotSpeechEmitted += (callsign, speech) => allSpeech.Add((callsign, speech));

        // Set the toggle BEFORE replay so it's active for every transmission site.
        engine.ReplayWithScenarioOverride(recording, (int)recording.TotalElapsedSeconds, scenario => scenario.RpoShowPilotSpeech = true);

        // The bundle log shows sim-initiated transmissions during the session (traffic-in-sight,
        // field-in-sight, going-around, short-final-no-clearance, holding-short, clear-of-runway).
        // With the setting on they route to pilot speech rather than PendingWarnings.
        output.WriteLine($"Pilot-speech entries emitted during replay: {allSpeech.Count}");
        foreach (var (cs, s) in allSpeech)
        {
            output.WriteLine($"  {cs}: {s}");
        }

        Assert.True(
            allSpeech.Count > 0,
            "Expected at least one pilot-speech entry after a 2270s replay with RpoShowPilotSpeech=true; "
                + "if zero entries appeared the routing may be broken."
        );

        // Spot-check format: every entry should be a pilot-speech transmission containing
        // the spoken-form callsign (NATO-spelled or telephony) for TTS. Legacy builders
        // emit "[CALLSIGN] november one two three..."; dual-output builders emit
        // "november one two three..., ..." — both contain the spoken callsign.
        foreach (var (cs, s) in allSpeech)
        {
            Assert.True(
                s.Contains($"[{cs}]") || s.Contains(Yaat.Sim.Speech.CallsignParser.IcaoToSpoken(cs), StringComparison.OrdinalIgnoreCase),
                $"Expected pilot-speech entry to contain [{cs}] or its NATO-spoken form, got: {s}"
            );
        }
    }

    /// <summary>
    /// Same replay but with the setting OFF (default). PendingPilotSpeech must remain empty;
    /// the events all flow through PendingWarnings as before.
    /// </summary>
    [Fact]
    public void RpoMode_PilotSpeechOff_PreservesWarningRouting()
    {
        var recording = RecordingLoader.Load(BundlePath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine($"Skipped: {BundlePath} or test data not present");
            return;
        }

        // Count what the engine emits as it drains, not what is left in the buffer — the buffer is
        // drained every tick, so reading it at the end of the replay can only ever return zero.
        int emitted = 0;
        engine.PilotSpeechEmitted += (_, _) => emitted++;

        // Default behavior: RpoShowPilotSpeech stays false.
        engine.Replay(recording, (int)recording.TotalElapsedSeconds);

        Assert.Equal(0, emitted);
    }
}
