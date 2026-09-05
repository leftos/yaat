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
    /// What kind of run this engine is being driven as, and therefore what may differ from other runs — see
    /// <see cref="Simulation.RunProfile"/>. A bare engine is a <see cref="RunKind.Test"/> run; a host that is
    /// something else says so at creation, and the replay driver switches to <see cref="RunKind.Replay"/> for the
    /// duration of each replay step through <see cref="EnterReplay"/>.
    /// </summary>
    public RunProfile RunProfile { get; internal set; } = RunProfile.Test;

    /// <summary>
    /// Runs the engine as a <see cref="RunKind.Replay"/> until the returned scope is disposed, then restores the
    /// profile the engine had before. Scoped rather than latched so a test can replay a recording to a cutoff and
    /// then tick the engine live from there — the engine returns to its own kind when the replay step ends.
    /// </summary>
    internal ReplayScope EnterReplay() => new(this);

    /// <summary>The scope <see cref="EnterReplay"/> hands out. A struct because the sub-tick driver opens one four times a second.</summary>
    internal readonly struct ReplayScope : IDisposable
    {
        private readonly SimulationEngine _engine;
        private readonly RunProfile _previous;

        internal ReplayScope(SimulationEngine engine)
        {
            _engine = engine;
            _previous = engine.RunProfile;
            engine.RunProfile = RunProfile.Replay;
        }

        public void Dispose()
        {
            _engine.RunProfile = _previous;
        }
    }

    /// <summary>
    /// Opt-in per-step timing. Null in production so the spine pays one null check per step; attach a dictionary
    /// and every spine step records into it under its <see cref="Spine.StepId"/> name, plus the three segment
    /// rollups <c>PrePhysics</c> / <c>Physics</c> / <c>PostPhysics</c> and the physics internals
    /// (<c>Physics.WorldTick</c>, …). Keyed bucket → (count, total ms). Cleared at the start of each
    /// <see cref="Replay"/>. The soak runner's <c>--timings</c> and the reconstruction benchmark attach one; call
    /// <see cref="DumpTickTimings"/> to format. Not thread-safe — one engine, one dictionary.
    /// </summary>
    public Dictionary<string, (int Count, double Ms)>? TickTimings { get; set; }

    /// <summary>
    /// Replay from t=0 to <paramref name="targetSeconds"/>, applying recorded actions at the correct times.
    /// Resets engine state every call (rewinds to scratch); not a step function — looping this is O(N²)
    /// and trips assertions like the magnetic declination cache. To advance from the current state, use
    /// <see cref="FastForwardTo"/>; to step second-by-second, use <see cref="ReplayOneSecond"/>.
    /// The default action applier is the <see cref="Actions"/> router under the replay host, which refuses the
    /// host-owned verbs (strips, TDLS, coordination); pass a custom <paramref name="actionApplier"/> to handle those
    /// (server rewind).
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
        if (TickTimings is not { Count: > 0 } timings)
        {
            return "(no tick timings recorded)";
        }
        var sb = new StringBuilder();
        sb.AppendLine("Tick timings (bucket: count, totalMs, avgMs):");
        foreach (var kvp in timings.OrderByDescending(k => k.Value.Ms))
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
    /// Arms the replay driver against a scenario that was loaded by something other than a recording, with the
    /// cursors positioned at the engine's current second. <see cref="ReplayOneSecond"/> is a no-op until a driver
    /// is armed, and the usual way to arm one is <see cref="Replay"/>, which loads the scenario from the recording
    /// itself.
    ///
    /// The tick oracle needs the other order. It compares run kinds, so the scenario load must not be a variable:
    /// every room is loaded identically through the server's own scenario load, and only the per-second stepping
    /// differs. Arming afterwards is what lets the replay room be built that way.
    /// </summary>
    public void ArmReplay(List<RecordedAction> actions)
    {
        var scenario = Scenario ?? throw new InvalidOperationException("ArmReplay requires a loaded scenario");
        _replay.Arm(actions, (int)scenario.ElapsedSeconds);
    }

    /// <summary>
    /// Advances the replay by one second: ticks physics, applies any recorded
    /// actions at the new time, and advances weather. Call after <see cref="Replay"/>
    /// or <see cref="ArmReplay"/> to continue the recording second-by-second while inspecting state between ticks.
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
