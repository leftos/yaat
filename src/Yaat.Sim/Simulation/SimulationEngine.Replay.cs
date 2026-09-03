using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Replay drivers -- advancing a recording forward over a range, a second, or a sub-tick.
public sealed partial class SimulationEngine
{
    // Replay cursor state — set by Replay(), consumed by ReplayOneSecond()
    private List<RecordedAction>? _replayActions;
    private int _replayActionCursor;
    private int _replayPreTickActionCursor;
    private readonly HashSet<int> _replayPreTickAppliedActionIndexes = [];
    private bool _isReplayingRecordedActions;
    private bool _replayHasRecordedAircraftSpawns;

    /// <summary>
    /// Diagnostic per-tick timing buckets. Keyed by bucket name (e.g. "PrePhysics",
    /// "Physics.Ground", "Physics.World", "PostPhysics"). Populated by
    /// <see cref="ReplayRange"/>. Reset at the start of each <see cref="Replay"/> /
    /// <see cref="ReplayRange"/> call. Intended for test instrumentation only —
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
        ReplayRange(0, targetSeconds, actions, actionApplier);
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
        var scenario = Scenario;
        if (scenario is null)
        {
            throw new InvalidOperationException("FastForwardTo requires a loaded scenario");
        }
        int currentSeconds = (int)scenario.ElapsedSeconds;
        if (targetSeconds <= currentSeconds)
        {
            throw new ArgumentException(
                $"FastForwardTo cannot rewind: current={currentSeconds}s target={targetSeconds}s. "
                    + "Use ReplayFromStartTo or restore from a snapshot to go backward.",
                nameof(targetSeconds)
            );
        }
        ReplayRange(currentSeconds, targetSeconds, actions, actionApplier);

        _replayActions = actions;
        _replayActionCursor = 0;
        _replayPreTickActionCursor = 0;
        _replayPreTickAppliedActionIndexes.Clear();
        _replayHasRecordedAircraftSpawns = _replayActions.Any(static a => a is RecordedAircraftSpawn);
        while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= targetSeconds)
        {
            _replayActionCursor++;
        }
        while (_replayPreTickActionCursor < _replayActions.Count && _replayActions[_replayPreTickActionCursor].ElapsedSeconds <= targetSeconds)
        {
            _replayPreTickActionCursor++;
        }
    }

    /// <summary>
    /// Replays from <paramref name="startSeconds"/> to <paramref name="targetSeconds"/>,
    /// applying actions and ticking physics for each second in the range.
    /// When startSeconds is 0, actions at t=0 are applied first.
    /// </summary>
    public void ReplayRange(int startSeconds, int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        ReplayRangeCore(startSeconds, targetSeconds, actions, actionApplier, archiveForVerification: null, drifts: null);
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
        var drifts = new List<SnapshotDriftReport>();
        ReplayRangeCore(startSeconds, targetSeconds, actions, actionApplier, archive, drifts);
        return new ReplayResult(drifts);
    }

    private void ReplayRangeCore(
        int startSeconds,
        int targetSeconds,
        List<RecordedAction> actions,
        Action<RecordedAction>? actionApplier,
        RecordingArchive? archiveForVerification,
        List<SnapshotDriftReport>? drifts
    )
    {
        if (startSeconds == 0)
        {
            _replayTrackApplier.Reset();
        }
        actionApplier ??= ApplyRecordedAction;
        bool previousReplayState = _isReplayingRecordedActions;
        bool previousReplaySpawnState = _replayHasRecordedAircraftSpawns;
        _isReplayingRecordedActions = true;
        _replayHasRecordedAircraftSpawns = actions.Any(static a => a is RecordedAircraftSpawn);

        try
        {
            var verifyByTimestamp = new Dictionary<int, int>();
            if (archiveForVerification is not null && drifts is not null)
            {
                for (int i = 0; i < archiveForVerification.SnapshotTimestamps.Count; i++)
                {
                    int ts = (int)archiveForVerification.SnapshotTimestamps[i].ElapsedSeconds;
                    if (ts > startSeconds && ts <= targetSeconds && !verifyByTimestamp.ContainsKey(ts))
                    {
                        verifyByTimestamp[ts] = i;
                    }
                }
            }

            int actionCursor = 0;
            int preTickActionCursor = 0;
            var preTickAppliedActionIndexes = new HashSet<int>();

            if (startSeconds == 0)
            {
                ApplyRecordedAircraftSpawnsBeforeTick(actions, ref preTickActionCursor, 0, actionApplier, preTickAppliedActionIndexes);

                // Apply actions at t=0 first (settings, immediate commands)
                while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= 0)
                {
                    if (!preTickAppliedActionIndexes.Contains(actionCursor))
                    {
                        actionApplier(actions[actionCursor]);
                    }

                    actionCursor++;
                }
            }
            else
            {
                // Skip actions before the start time
                while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= startSeconds)
                {
                    actionCursor++;
                }

                while (preTickActionCursor < actions.Count && actions[preTickActionCursor].ElapsedSeconds <= startSeconds)
                {
                    preTickActionCursor++;
                }
            }

            double subDelta = 1.0 / PhysicsSubTickRate;
            var sw = new Stopwatch();
            for (int t = startSeconds + 1; t <= targetSeconds; t++)
            {
                Scenario!.ElapsedSeconds = t;

                sw.Restart();
                ApplyRecordedAircraftSpawnsBeforeTick(actions, ref preTickActionCursor, t, actionApplier, preTickAppliedActionIndexes);
                TickPrePhysics();
                AccumulateTiming("PrePhysics", sw);

                for (int sub = 0; sub < PhysicsSubTickRate; sub++)
                {
                    sw.Restart();
                    TickPhysics(subDelta);
                    AccumulateTiming("Physics", sw);
                }

                sw.Restart();
                TickPostPhysics();
                AccumulateTiming("PostPhysics", sw);
                _terminalEntries.Clear();

                // Advance weather timeline if active
                if (Scenario!.WeatherTimeline is { } timeline)
                {
                    World.Weather = timeline.GetWeatherAt(t);
                }

                // Apply actions at this time
                while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= t)
                {
                    if (!preTickAppliedActionIndexes.Contains(actionCursor))
                    {
                        actionApplier(actions[actionCursor]);
                    }

                    actionCursor++;
                }

                if (archiveForVerification is not null && drifts is not null && verifyByTimestamp.TryGetValue(t, out var snapIdx))
                {
                    var snap = archiveForVerification.ReadSnapshot(snapIdx);
                    var report = SnapshotDiff.Compare(t, snap, World.GetSnapshot());
                    if (report.AircraftDrifts.Count > 0)
                    {
                        drifts.Add(report);
                    }
                }

                FireTickCompleted(t);
            }
        }
        finally
        {
            _isReplayingRecordedActions = previousReplayState;
            _replayHasRecordedAircraftSpawns = previousReplaySpawnState;
        }
    }

    private void AccumulateTiming(string bucket, Stopwatch sw)
    {
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds;
        if (TickTimings.TryGetValue(bucket, out var entry))
        {
            TickTimings[bucket] = (entry.Count + 1, entry.Ms + ms);
        }
        else
        {
            TickTimings[bucket] = (1, ms);
        }
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
        TickTimings.Clear();
        LoadScenario(recording.ScenarioJson, recording.RngSeed, recording.MagneticModelDateUtc ?? MagneticDeclination.EvaluationDateUtc);

        // The scenario JSON does not carry the resolved runtime student position (the server sets it
        // at load via InitializeTrackPositions). Restore it from the recording so CanInitiateWithStudent,
        // proactive check-ins, and Class B/C boundary holds replay as they did live.
        if (Scenario is not null && recording.StudentPositionState is { } studentPosition)
        {
            Scenario.StudentPosition = studentPosition.Position;
            Scenario.StudentTcp = studentPosition.Tcp;
            World.StudentTcp = studentPosition.Tcp;
            Scenario.StudentPositionType = studentPosition.PositionType;
            Scenario.IsStudentTowerPosition = studentPosition.IsTowerPosition;
        }

        if (Scenario is not null)
        {
            configureAfterLoad(Scenario);
        }

        // Apply weather if present
        if (recording.WeatherJson is not null)
        {
            ApplyWeatherJson(recording.WeatherJson);
            if (Scenario is not null)
            {
                Scenario.MetarReissuanceEnabled = recording.MetarReissuanceEnabled;
            }
        }

        // FAS-reduction variety was captured from the recording's initial snapshot: on for
        // sessions recorded with the feature, off for pre-feature recordings so they re-simulate
        // with the original uniform slow-down.
        if (Scenario is not null)
        {
            Scenario.FinalApproachSpeedVarietyEnabled = recording.FinalApproachSpeedVarietyEnabled;
        }

        // Deserialize the bundled ARTCC config so TrackResolver's TCP/ERAM fallback works
        // for AS commands targeting positions outside the scenario's StudentTcp/AtcPositions.
        // Older recordings without the bundle leave this as null; callers can set it manually.
        if (Scenario is not null && recording.ArtccConfigJson is { } artccJson)
        {
            try
            {
                Scenario.ArtccConfig = JsonSerializer.Deserialize<Yaat.Sim.Data.Vnas.ArtccConfigRoot>(artccJson, RecordingJsonOptions.Default);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize bundled ArtccConfig; replay will fall back to scenario-only resolution");
            }
        }

        ReplayFromStartTo((int)targetSeconds, recording.Actions);

        // Store replay cursor so ReplayOneSecond() can continue from here
        _replayActions = recording.Actions;
        _replayActionCursor = 0;
        _replayPreTickActionCursor = 0;
        _replayPreTickAppliedActionIndexes.Clear();
        _replayHasRecordedAircraftSpawns = _replayActions.Any(static a => a is RecordedAircraftSpawn);
        int target = (int)targetSeconds;
        while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= target)
        {
            _replayActionCursor++;
        }
        while (_replayPreTickActionCursor < _replayActions.Count && _replayActions[_replayPreTickActionCursor].ElapsedSeconds <= target)
        {
            _replayPreTickActionCursor++;
        }
    }

    /// <summary>
    /// Advances the replay by one second: ticks physics, applies any recorded
    /// actions at the new time, and advances weather. Call after <see cref="Replay"/>
    /// to continue the recording second-by-second while inspecting state between ticks.
    /// </summary>
    public void ReplayOneSecond()
    {
        var scenario = Scenario;
        if (scenario is null || _replayActions is null)
        {
            return;
        }

        scenario.ElapsedSeconds += 1;
        int t = (int)scenario.ElapsedSeconds;

        var sw = new Stopwatch();
        bool previousReplayState = _isReplayingRecordedActions;
        _isReplayingRecordedActions = true;

        try
        {
            sw.Restart();
            ApplyRecordedAircraftSpawnsBeforeTick(
                _replayActions,
                ref _replayPreTickActionCursor,
                t,
                ApplyRecordedAction,
                _replayPreTickAppliedActionIndexes
            );
            TickPrePhysics();
            AccumulateTiming("PrePhysics", sw);

            double subDelta = 1.0 / PhysicsSubTickRate;
            for (int sub = 0; sub < PhysicsSubTickRate; sub++)
            {
                sw.Restart();
                TickPhysics(subDelta);
                AccumulateTiming("Physics", sw);
            }

            sw.Restart();
            TickPostPhysics();
            AccumulateTiming("PostPhysics", sw);
            _terminalEntries.Clear();

            if (scenario.WeatherTimeline is { } timeline)
            {
                World.Weather = timeline.GetWeatherAt(t);
            }

            while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= t)
            {
                if (!_replayPreTickAppliedActionIndexes.Contains(_replayActionCursor))
                {
                    ApplyRecordedAction(_replayActions[_replayActionCursor]);
                }

                _replayActionCursor++;
            }

            FireTickCompleted(t);
        }
        finally
        {
            _isReplayingRecordedActions = previousReplayState;
        }
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
        var scenario = Scenario;
        if (scenario is null || _replayActions is null)
        {
            return;
        }

        const double eps = 1e-9;
        double prev = scenario.ElapsedSeconds;
        double subDelta = 1.0 / PhysicsSubTickRate;
        scenario.ElapsedSeconds = prev + subDelta;

        // "We just started a new integer second" — the previous ElapsedSeconds
        // sat exactly on an integer, so this sub-tick is the first of four.
        bool atSecondStart = Math.Abs(prev - Math.Round(prev)) < eps;

        // "We just finished an integer second" — the new ElapsedSeconds lands
        // exactly on an integer, so this sub-tick is the last of four.
        bool atSecondEnd = Math.Abs(scenario.ElapsedSeconds - Math.Round(scenario.ElapsedSeconds)) < eps;
        bool previousReplayState = _isReplayingRecordedActions;
        _isReplayingRecordedActions = true;

        try
        {
            if (atSecondStart)
            {
                int t = (int)Math.Ceiling(scenario.ElapsedSeconds);
                ApplyRecordedAircraftSpawnsBeforeTick(
                    _replayActions,
                    ref _replayPreTickActionCursor,
                    t,
                    ApplyRecordedAction,
                    _replayPreTickAppliedActionIndexes
                );
                TickPrePhysics();
            }

            TickPhysics(subDelta);

            if (atSecondEnd)
            {
                // Snap away any floating-point drift accumulated across sub-ticks.
                scenario.ElapsedSeconds = Math.Round(scenario.ElapsedSeconds);
                int t = (int)scenario.ElapsedSeconds;

                TickPostPhysics();
                _terminalEntries.Clear();

                if (scenario.WeatherTimeline is { } timeline)
                {
                    World.Weather = timeline.GetWeatherAt(t);
                }

                while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= t)
                {
                    if (!_replayPreTickAppliedActionIndexes.Contains(_replayActionCursor))
                    {
                        ApplyRecordedAction(_replayActions[_replayActionCursor]);
                    }

                    _replayActionCursor++;
                }

                FireTickCompleted(t);
            }
        }
        finally
        {
            _isReplayingRecordedActions = previousReplayState;
        }
    }

    // --- Commands ---
}
