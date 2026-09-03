using System.Text;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;

namespace Yaat.Sim.Simulation;

// Replay drivers. The stepping logic and the recorded-action cursors live in ReplayDriver; the methods
// here are the engine's public surface over it, so callers never name the driver type.
public sealed partial class SimulationEngine
{
    private readonly ReplayDriver _replay;

    /// <summary>
    /// True while a recording is being replayed onto this engine. Read outside replay by the recorders
    /// (which must not re-record a replayed action), by <see cref="TickControllerAi"/> (brains never run
    /// in replay) and by the generators (which must not re-spawn what the log already carries).
    /// </summary>
    internal bool IsReplayingRecordedActions { get; set; }

    /// <summary>
    /// Whether the recording being replayed carries its own aircraft spawns, so the generators must
    /// stand down rather than produce a second set. Only meaningful while
    /// <see cref="IsReplayingRecordedActions"/> is true.
    /// </summary>
    internal bool ReplayHasRecordedAircraftSpawns { get; set; }

    /// <summary>
    /// Diagnostic per-tick timing buckets. Keyed by bucket name (e.g. "PrePhysics",
    /// "Physics.Ground", "Physics.World", "PostPhysics"). Populated by
    /// <see cref="ReplayRange"/> and by the live tick's world timing. Reset at the start of each
    /// <see cref="Replay"/> / <see cref="ReplayRange"/> call. Intended for test instrumentation only —
    /// call <see cref="DumpTickTimings"/> to format.
    /// </summary>
    public Dictionary<string, (int Count, double Ms)> TickTimings { get; } = new();

    /// <summary>
    /// Replay from t=0 to <paramref name="targetSeconds"/>, applying recorded actions at the correct times.
    /// Resets engine state every call (rewinds to scratch); not a step function — looping this is O(N²)
    /// and trips assertions like the magnetic declination cache. To advance from the current state, use
    /// <see cref="FastForwardTo"/>; to step second-by-second, use <see cref="ReplayOneSecond"/>.
    /// The default action applier skips server-only commands (track, coordination); pass a custom
    /// <paramref name="actionApplier"/> to handle those (server rewind).
    /// </summary>
    public void ReplayFromStartTo(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        _replay.FromStartTo(targetSeconds, actions, actionApplier);
    }

    /// <summary>
    /// Advance the engine from its current <c>ElapsedSeconds</c> to <paramref name="targetSeconds"/>,
    /// applying recorded actions at the correct times. Does not reset state — the engine must already
    /// be at the start point. Throws <see cref="ArgumentException"/> if <paramref name="targetSeconds"/>
    /// is not strictly greater than the current time (use <see cref="ReplayFromStartTo"/> or restore from
    /// a snapshot to rewind). Updates the replay cursor so subsequent <see cref="ReplayOneSecond"/> calls
    /// continue from <paramref name="targetSeconds"/>.
    /// </summary>
    public void FastForwardTo(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        _replay.FastForwardTo(targetSeconds, actions, actionApplier);
    }

    /// <summary>
    /// Replays from <paramref name="startSeconds"/> to <paramref name="targetSeconds"/>,
    /// applying actions and ticking physics for each second in the range.
    /// When startSeconds is 0, actions at t=0 are applied first.
    /// </summary>
    public void ReplayRange(int startSeconds, int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        _replay.Range(startSeconds, targetSeconds, actions, actionApplier);
    }

    /// <summary>
    /// Replay variant that compares engine state against snapshots in the supplied
    /// <paramref name="archive"/> at every snapshot timestamp the range covers.
    /// Returns a <see cref="ReplayResult"/> listing the per-snapshot drifts. Empty
    /// drifts list ⇒ every checked snapshot matched within tolerance. Useful for
    /// pinpointing the first tick where replay diverges from a recorded session.
    /// </summary>
    public ReplayResult ReplayRangeWithVerification(
        int startSeconds,
        int targetSeconds,
        List<RecordedAction> actions,
        RecordingArchive archive,
        Action<RecordedAction>? actionApplier = null
    )
    {
        return _replay.RangeWithVerification(startSeconds, targetSeconds, actions, archive, actionApplier);
    }

    /// <summary>
    /// Formats <see cref="TickTimings"/> for diagnostic output. Sorted by total time desc.
    /// </summary>
    public string DumpTickTimings()
    {
        if (TickTimings.Count == 0)
        {
            return "(no tick timings recorded)";
        }
        var sb = new StringBuilder();
        sb.AppendLine("Tick timings (bucket: count, totalMs, avgMs):");
        foreach (var kvp in TickTimings.OrderByDescending(k => k.Value.Ms))
        {
            double avg = kvp.Value.Ms / Math.Max(1, kvp.Value.Count);
            sb.AppendLine($"  {kvp.Key}: n={kvp.Value.Count}, total={kvp.Value.Ms:F1}ms, avg={avg:F3}ms");
        }
        return sb.ToString();
    }

    public const int SnapshotIntervalSeconds = 5;

    public void Replay(SessionRecording recording, double targetSeconds)
    {
        ReplayWithScenarioOverride(recording, targetSeconds, configureAfterLoad: static _ => { });
    }

    /// <summary>
    /// Replay variant that runs <paramref name="configureAfterLoad"/> on the freshly loaded
    /// scenario before any actions or weather are applied. Useful for tests that need to
    /// override scenario state (e.g. <c>ValidateDctFixes</c>) when replaying older recordings
    /// that predate a setting being persisted in the action log.
    /// </summary>
    public void ReplayWithScenarioOverride(SessionRecording recording, double targetSeconds, Action<SimScenarioState> configureAfterLoad)
    {
        _replay.To(recording, targetSeconds, configureAfterLoad);
    }

    /// <summary>
    /// Advances the replay by one second: ticks physics, applies any recorded
    /// actions at the new time, and advances weather. Call after <see cref="Replay"/>
    /// to continue the recording second-by-second while inspecting state between ticks.
    /// </summary>
    public void ReplayOneSecond()
    {
        _replay.OneSecond();
    }

    /// <summary>
    /// Advances the replay by one physics sub-tick (0.25 s). This is the
    /// fine-grained version of <see cref="ReplayOneSecond"/> for tests that
    /// need to observe simulation state at sub-second granularity (e.g.
    /// capture the exact tick a phase transitions). Pre- and post-physics
    /// run only at integer-second boundaries, and recorded actions are
    /// applied once per crossed second (never mid-second), matching
    /// <see cref="ReplayOneSecond"/>'s semantics exactly when called four
    /// times in succession starting from an integer second.
    /// </summary>
    public void ReplayOneSubTick()
    {
        _replay.OneSubTick();
    }
}
