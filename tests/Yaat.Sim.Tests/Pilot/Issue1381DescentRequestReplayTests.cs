using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// GitHub #1381 (YAAT-2286), "everyone wants lower": on the recorded S3-FAT-3 (A) Approaches 1 session every IFR arrival
/// still above its descent profile asked the student for lower, over and over — v0.18.4-beta ended with FFL404 having
/// transmitted <c>"request lower."</c> nine times. The proactive descent request is capped (<c>AircraftState.DescentRequestCap</c>):
/// a capped request stays silent until the aircraft is 2,000 ft above the profile, where it is re-opened once, and it is
/// never re-opened at all from that band upwards. Replaying the reported bundle from t=0 to 2,960 s must keep every
/// aircraft at six requests or fewer, and must emit at least one, so a replay that produces no pilot speech at all
/// cannot pass vacuously. The line is counted where the solo student hears it: a proactive line goes through
/// <c>QueueSoloPilotTransmission</c> onto the say channel, so it surfaces as a <c>SayPilot</c> terminal entry, not
/// through <c>SimulationEngine.PilotSpeechEmitted</c>.
/// </summary>
public sealed class Issue1381DescentRequestReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue1381-everyone-wants-lower-recording.yaat-bug-report-bundle.zip";
    private const string RequestLower = "request lower.";
    private const int MaxAsks = 6;
    private const double EndSeconds = 2960.0;

    [Fact]
    public void Issue1381Replay_NoAircraftAsksForLowerMoreThanSixTimes()
    {
        if (Restore() is not { } restored)
        {
            output.WriteLine($"Skipped: {RecordingPath} is not present");
            return;
        }

        using RecordingArchive archive = restored.Archive;
        SimulationEngine engine = restored.Engine;
        double startedSeconds = restored.StartedSeconds;

        // A solo pilot's line goes out on the say channel (SayPilot), so the terminal entry is where it surfaces; the
        // per-aircraft request is re-asked by PilotRequestTracker through the same builder, so a re-ask counts too.
        var asks = new Dictionary<string, int>(StringComparer.Ordinal);
        engine.TerminalEntryEmitted += entry =>
        {
            if ((entry.Kind == "SayPilot") && entry.Message.EndsWith(RequestLower, StringComparison.Ordinal))
            {
                asks[entry.Callsign] = asks.GetValueOrDefault(entry.Callsign, 0) + 1;
            }
        };

        for (int second = 0; second < (int)(EndSeconds - startedSeconds); second++)
        {
            engine.ReplayOneSecond();
        }

        foreach (KeyValuePair<string, int> entry in asks.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"{entry.Key}: {entry.Value} x \"{RequestLower}\"");
        }

        int total = asks.Values.Sum();
        Assert.True(
            total > 0,
            $"The replay from t={startedSeconds:F0} s to {EndSeconds:F0} s emitted no \"{RequestLower}\" at all, so this guard cannot fail; "
                + "the replay or the descent-request path is broken."
        );

        string offenders = string.Join(", ", asks.Where(pair => pair.Value > MaxAsks).Select(pair => $"{pair.Key} asked {pair.Value} times"));
        Assert.True(offenders.Length == 0, $"Aircraft asked for lower more than {MaxAsks} times (#1381): {offenders}");
    }

    /// <summary>
    /// The t=0 snapshot with the replay driver armed behind it: <see cref="SimulationEngine.Replay"/> alone loses the
    /// load-time auto-track state (YAAT-2290), so the state is restored and the actions armed over it.
    /// </summary>
    private static (SimulationEngine Engine, double StartedSeconds, RecordingArchive Archive)? Restore()
    {
        TestVnasData.EnsureInitialized();
        if ((RecordingLoader.OpenArchive(RecordingPath) is not { } archive) || (archive.ReadSnapshotAt(0) is not { } snapshot))
        {
            return null;
        }

        var groundData = RecordingGroundData.VersionPinned(
            RecordingPath,
            "the recorded t=0 state is replayed second-by-second on the airport as the archive bundles it"
        );
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        SessionRecording recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);
        engine.RestoreFromSnapshot(snapshot.State);
        engine.ArmReplay(recording.Actions);
        return (engine, snapshot.ElapsedSeconds, archive);
    }
}
