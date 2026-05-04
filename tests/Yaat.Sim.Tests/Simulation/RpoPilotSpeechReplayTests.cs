using Xunit;
using Xunit.Abstractions;
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
    private const string BundlePath = "TestData/rpo-pilot-speech-recording.yaat-bug-report-bundle.zip";

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
    /// and asserts that at least one aircraft's transmissions landed in PendingPilotSpeech
    /// across the run. We accumulate across the whole replay (rather than asserting on a
    /// single tick) because PendingPilotSpeech is drained by the server's TickProcessor in
    /// production — in the embedded engine there's no draining, but multiple events can
    /// still pile on a single aircraft.
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

        // Set the toggle BEFORE replay so it's active for every transmission site.
        engine.ReplayWithScenarioOverride(recording, (int)recording.TotalElapsedSeconds, scenario => scenario.RpoShowPilotSpeech = true);

        // Walk every aircraft and collect any pilot-speech entries observed.
        var allSpeech = new List<(string Callsign, string Speech)>();
        foreach (var ac in engine.World.GetSnapshot())
        {
            foreach (var s in ac.PendingPilotSpeech)
            {
                allSpeech.Add((ac.Callsign, s));
            }
        }

        // The bundle log shows ~10 sim-initiated transmissions during the session
        // (traffic-in-sight, field-in-sight, going-around, short-final-no-clearance,
        // holding-short, clear-of-runway). After full replay with the setting on, at
        // least some should land in PendingPilotSpeech rather than PendingWarnings.
        // Most will have been "consumed" by mid-replay drains — but the very last
        // transmission per aircraft remains visible at end-of-replay.
        output.WriteLine($"PendingPilotSpeech entries at end of replay: {allSpeech.Count}");
        foreach (var (cs, s) in allSpeech)
        {
            output.WriteLine($"  {cs}: {s}");
        }

        Assert.True(
            allSpeech.Count > 0,
            "Expected at least one pilot-speech entry after a 2270s replay with RpoShowPilotSpeech=true; "
                + "if zero entries appeared the routing may be broken."
        );

        // Spot-check format: every entry should follow the [CALLSIGN] spoken-form pattern
        // built by PilotResponder, never the terse "callsign holding short" form.
        foreach (var (cs, s) in allSpeech)
        {
            Assert.StartsWith($"[{cs}]", s);
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

        // Default behavior: RpoShowPilotSpeech stays false.
        engine.Replay(recording, (int)recording.TotalElapsedSeconds);

        int pilotSpeechCount = 0;
        foreach (var ac in engine.World.GetSnapshot())
        {
            pilotSpeechCount += ac.PendingPilotSpeech.Count;
        }

        Assert.Equal(0, pilotSpeechCount);
    }
}
