using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
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
/// <c>AircraftState.PendingPilotSpeech</c> as the <c>PilotSpeechText</c> built by
/// <c>PilotResponder</c> (written and spoken forms), instead of the terse controller-debug
/// text in <c>PendingWarnings</c>, and the <c>PilotSpeech</c> terminal entry shows the
/// written form.
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
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
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
        SessionRecording? recording = RecordingLoader.Load(BundlePath);
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine($"Skipped: {BundlePath} or test data not present");
            return;
        }

        // Collect the pilot-speech entries the engine emits as it drains them each tick.
        var allSpeech = new List<(string Callsign, PilotSpeechText Speech)>();
        engine.PilotSpeechEmitted += (callsign, speech) => allSpeech.Add((callsign, speech));
        var terminalSpeech = new List<string>();
        engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Kind == "PilotSpeech")
            {
                terminalSpeech.Add(entry.Message);
            }
        };

        // Set the toggle BEFORE replay so it's active for every transmission site.
        engine.ReplayWithScenarioOverride(recording, (int)recording.TotalElapsedSeconds, scenario => scenario.RpoShowPilotSpeech = true);

        // The bundle log shows sim-initiated transmissions during the session (traffic-in-sight,
        // field-in-sight, going-around, short-final-no-clearance, holding-short, clear-of-runway).
        // With the setting on they route to pilot speech rather than PendingWarnings.
        output.WriteLine($"Pilot-speech entries emitted during replay: {allSpeech.Count}");
        foreach ((string? cs, PilotSpeechText? s) in allSpeech)
        {
            output.WriteLine($"  {cs}: {s.TerminalForRpo} | {s.Tts}");
        }

        Assert.True(
            allSpeech.Count > 0,
            "Expected at least one pilot-speech entry after a 2270s replay with RpoShowPilotSpeech=true; "
                + "if zero entries appeared the routing may be broken."
        );

        // Spot-check format: every entry's spoken form contains the NATO-spelled callsign for TTS
        // ("november one two three..., ...").
        foreach ((string? cs, PilotSpeechText? s) in allSpeech)
        {
            Assert.True(
                s.Tts.Contains(Yaat.Sim.Speech.CallsignParser.IcaoToSpoken(cs), StringComparison.OrdinalIgnoreCase),
                $"Expected the spoken form to contain the NATO-spoken form of {cs}, got: {s.Tts}"
            );
        }

        // The PilotSpeech terminal entry (what the RPO terminal, speech bubble and history show)
        // carries the written form, one entry per emitted transmission, never the spelled-out TTS form.
        Assert.Equal(allSpeech.Select(e => e.Speech.TerminalForRpo), terminalSpeech);
        Assert.All(terminalSpeech, message => Assert.DoesNotContain("november", message, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Same replay but with the setting OFF (default). PendingPilotSpeech must remain empty;
    /// the events all flow through PendingWarnings as before.
    /// </summary>
    [Fact]
    public void RpoMode_PilotSpeechOff_PreservesWarningRouting()
    {
        SessionRecording? recording = RecordingLoader.Load(BundlePath);
        SimulationEngine? engine = BuildEngine();
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
