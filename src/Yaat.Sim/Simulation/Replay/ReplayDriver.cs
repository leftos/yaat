using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// Drives a recording forward over a <see cref="SimulationEngine"/> — a whole range, one second, or one
/// physics sub-tick. It owns the recorded-action cursors: how far into the action log the replay has
/// advanced, and which actions a pre-tick pass already applied so the main pass does not repeat them.
///
/// That cursor state is the whole reason this is a separate object. It is meaningful only while a
/// recording is being replayed, whereas the two replay <em>mode</em> flags stay on the engine because
/// four members outside replay read them to decide whether they may record an action or run an AI
/// brain — those describe the engine's mode, not this driver's bookkeeping.
///
/// The engine exposes every driver method as its own, so no caller names this type.
/// </summary>
internal sealed class ReplayDriver(SimulationEngine engine)
{
    private readonly SimulationEngine _engine = engine;

    private List<RecordedAction>? _actions;
    private int _actionCursor;
    private int _preTickActionCursor;
    private readonly HashSet<int> _preTickAppliedActionIndexes = [];

    /// <summary>Whether a recording has been loaded and the cursors positioned — replay stepping is a no-op until then.</summary>
    private bool IsArmed => _actions is not null;

    /// <summary>
    /// Points both cursors just past <paramref name="seconds"/> so the next step treats only later
    /// actions as pending. Called after any jump in time: a fast-forward, a fresh replay load, or a
    /// snapshot restore.
    /// </summary>
    private void SeekTo(int seconds)
    {
        if (_actions is not { } actions)
        {
            return;
        }

        _actionCursor = 0;
        _preTickActionCursor = 0;
        _preTickAppliedActionIndexes.Clear();
        while (_actionCursor < actions.Count && actions[_actionCursor].ElapsedSeconds <= seconds)
        {
            _actionCursor++;
        }
        while (_preTickActionCursor < actions.Count && actions[_preTickActionCursor].ElapsedSeconds <= seconds)
        {
            _preTickActionCursor++;
        }
    }

    /// <summary>
    /// Re-points the cursors at the engine's current time after a snapshot restore. Without it a
    /// subsequent <see cref="OneSecond"/> would treat actions from t=0 onward as still pending and
    /// re-apply them on top of the restored state. This is what makes the hybrid pattern work: replay
    /// to load the scenario, restore to jump to a saved state, then step forward from there.
    /// </summary>
    public void ReseekAfterRestore(int restoredSeconds)
    {
        if (IsArmed)
        {
            SeekTo(restoredSeconds);
        }
    }

    /// <summary>Arms the driver with an action log and positions the cursors at <paramref name="seconds"/>.</summary>
    private void Arm(List<RecordedAction> actions, int seconds)
    {
        _actions = actions;
        _engine.ReplayHasRecordedAircraftSpawns = actions.Any(static a => a is RecordedAircraftSpawn);
        SeekTo(seconds);
    }

    public void FromStartTo(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier)
    {
        Range(0, targetSeconds, actions, actionApplier);
    }

    public void FastForwardTo(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier)
    {
        var scenario = _engine.Scenario;
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
        Range(currentSeconds, targetSeconds, actions, actionApplier);
        Arm(actions, targetSeconds);
    }

    public void Range(int startSeconds, int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier)
    {
        RangeCore(startSeconds, targetSeconds, actions, actionApplier, archiveForVerification: null, drifts: null);
    }

    public ReplayResult RangeWithVerification(
        int startSeconds,
        int targetSeconds,
        List<RecordedAction> actions,
        RecordingArchive archive,
        Action<RecordedAction>? actionApplier
    )
    {
        var drifts = new List<SnapshotDriftReport>();
        RangeCore(startSeconds, targetSeconds, actions, actionApplier, archive, drifts);
        return new ReplayResult(drifts);
    }

    private void RangeCore(
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
            _engine.ResetReplayTrackApplier();
        }
        actionApplier ??= _engine.ApplyRecordedAction;
        bool previousReplayState = _engine.IsReplayingRecordedActions;
        bool previousReplaySpawnState = _engine.ReplayHasRecordedAircraftSpawns;
        _engine.IsReplayingRecordedActions = true;
        _engine.ReplayHasRecordedAircraftSpawns = actions.Any(static a => a is RecordedAircraftSpawn);

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

            // Range replay walks a caller-supplied action list with its own local cursors, leaving the
            // driver's stepping cursors untouched — the two are independent traversals of the log.
            int actionCursor = 0;
            int preTickActionCursor = 0;
            var preTickAppliedActionIndexes = new HashSet<int>();

            if (startSeconds == 0)
            {
                SimulationEngine.ApplyRecordedAircraftSpawnsBeforeTick(
                    actions,
                    ref preTickActionCursor,
                    0,
                    actionApplier,
                    preTickAppliedActionIndexes
                );

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

            double subDelta = 1.0 / SimulationEngine.PhysicsSubTickRate;
            var sw = new Stopwatch();
            for (int t = startSeconds + 1; t <= targetSeconds; t++)
            {
                _engine.Scenario!.ElapsedSeconds = t;

                sw.Restart();
                SimulationEngine.ApplyRecordedAircraftSpawnsBeforeTick(
                    actions,
                    ref preTickActionCursor,
                    t,
                    actionApplier,
                    preTickAppliedActionIndexes
                );
                _engine.TickPrePhysics();
                _engine.AccumulateTiming("PrePhysics", sw);

                for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
                {
                    sw.Restart();
                    _engine.TickPhysics(subDelta);
                    _engine.AccumulateTiming("Physics", sw);
                }

                sw.Restart();
                _engine.TickPostPhysics();
                _engine.AccumulateTiming("PostPhysics", sw);
                _engine.ClearTerminalEntries();

                // Advance weather timeline if active
                if (_engine.Scenario!.WeatherTimeline is { } timeline)
                {
                    _engine.World.Weather = timeline.GetWeatherAt(t);
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
                    var report = SnapshotDiff.Compare(t, snap, _engine.World.GetSnapshot());
                    if (report.AircraftDrifts.Count > 0)
                    {
                        drifts.Add(report);
                    }
                }

                _engine.FireTickCompleted(t);
            }
        }
        finally
        {
            _engine.IsReplayingRecordedActions = previousReplayState;
            _engine.ReplayHasRecordedAircraftSpawns = previousReplaySpawnState;
        }
    }

    public void To(SessionRecording recording, double targetSeconds, Action<SimScenarioState> configureAfterLoad)
    {
        _engine.TickTimings.Clear();
        _engine.LoadScenario(recording.ScenarioJson, recording.RngSeed, recording.MagneticModelDateUtc ?? MagneticDeclination.EvaluationDateUtc);

        // The scenario JSON does not carry the resolved runtime student position (the server sets it
        // at load via InitializeTrackPositions). Restore it from the recording so CanInitiateWithStudent,
        // proactive check-ins, and Class B/C boundary holds replay as they did live.
        if (_engine.Scenario is not null && recording.StudentPositionState is { } studentPosition)
        {
            _engine.Scenario.StudentPosition = studentPosition.Position;
            _engine.Scenario.StudentTcp = studentPosition.Tcp;
            _engine.World.StudentTcp = studentPosition.Tcp;
            _engine.Scenario.StudentPositionType = studentPosition.PositionType;
            _engine.Scenario.IsStudentTowerPosition = studentPosition.IsTowerPosition;
        }

        if (_engine.Scenario is not null)
        {
            configureAfterLoad(_engine.Scenario);
        }

        // Apply weather if present
        if (recording.WeatherJson is not null)
        {
            _engine.ApplyWeatherJson(recording.WeatherJson);
            if (_engine.Scenario is not null)
            {
                _engine.Scenario.MetarReissuanceEnabled = recording.MetarReissuanceEnabled;
            }
        }

        // FAS-reduction variety was captured from the recording's initial snapshot: on for
        // sessions recorded with the feature, off for pre-feature recordings so they re-simulate
        // with the original uniform slow-down.
        if (_engine.Scenario is not null)
        {
            _engine.Scenario.FinalApproachSpeedVarietyEnabled = recording.FinalApproachSpeedVarietyEnabled;
        }

        // Deserialize the bundled ARTCC config so TrackResolver's TCP/ERAM fallback works
        // for AS commands targeting positions outside the scenario's StudentTcp/AtcPositions.
        // Older recordings without the bundle leave this as null; callers can set it manually.
        if (_engine.Scenario is not null && recording.ArtccConfigJson is { } artccJson)
        {
            try
            {
                _engine.Scenario.ArtccConfig = JsonSerializer.Deserialize<ArtccConfigRoot>(artccJson, RecordingJsonOptions.Default);
            }
            catch (JsonException ex)
            {
                _engine.Logger.LogWarning(ex, "Failed to deserialize bundled ArtccConfig; replay will fall back to scenario-only resolution");
            }
        }

        FromStartTo((int)targetSeconds, recording.Actions, actionApplier: null);

        // Store the replay cursor so OneSecond() can continue from here.
        Arm(recording.Actions, (int)targetSeconds);
    }

    public void OneSecond()
    {
        var scenario = _engine.Scenario;
        if (scenario is null || _actions is not { } actions)
        {
            return;
        }

        scenario.ElapsedSeconds += 1;
        int t = (int)scenario.ElapsedSeconds;

        var sw = new Stopwatch();
        bool previousReplayState = _engine.IsReplayingRecordedActions;
        _engine.IsReplayingRecordedActions = true;

        try
        {
            sw.Restart();
            SimulationEngine.ApplyRecordedAircraftSpawnsBeforeTick(
                actions,
                ref _preTickActionCursor,
                t,
                _engine.ApplyRecordedAction,
                _preTickAppliedActionIndexes
            );
            _engine.TickPrePhysics();
            _engine.AccumulateTiming("PrePhysics", sw);

            double subDelta = 1.0 / SimulationEngine.PhysicsSubTickRate;
            for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
            {
                sw.Restart();
                _engine.TickPhysics(subDelta);
                _engine.AccumulateTiming("Physics", sw);
            }

            sw.Restart();
            _engine.TickPostPhysics();
            _engine.AccumulateTiming("PostPhysics", sw);
            _engine.ClearTerminalEntries();

            if (scenario.WeatherTimeline is { } timeline)
            {
                _engine.World.Weather = timeline.GetWeatherAt(t);
            }

            ApplyPendingActionsThrough(t, actions);

            _engine.FireTickCompleted(t);
        }
        finally
        {
            _engine.IsReplayingRecordedActions = previousReplayState;
        }
    }

    public void OneSubTick()
    {
        var scenario = _engine.Scenario;
        if (scenario is null || _actions is not { } actions)
        {
            return;
        }

        const double eps = 1e-9;
        double prev = scenario.ElapsedSeconds;
        double subDelta = 1.0 / SimulationEngine.PhysicsSubTickRate;
        scenario.ElapsedSeconds = prev + subDelta;

        // "We just started a new integer second" — the previous ElapsedSeconds
        // sat exactly on an integer, so this sub-tick is the first of four.
        bool atSecondStart = Math.Abs(prev - Math.Round(prev)) < eps;

        // "We just finished an integer second" — the new ElapsedSeconds lands
        // exactly on an integer, so this sub-tick is the last of four.
        bool atSecondEnd = Math.Abs(scenario.ElapsedSeconds - Math.Round(scenario.ElapsedSeconds)) < eps;
        bool previousReplayState = _engine.IsReplayingRecordedActions;
        _engine.IsReplayingRecordedActions = true;

        try
        {
            if (atSecondStart)
            {
                int t = (int)Math.Ceiling(scenario.ElapsedSeconds);
                SimulationEngine.ApplyRecordedAircraftSpawnsBeforeTick(
                    actions,
                    ref _preTickActionCursor,
                    t,
                    _engine.ApplyRecordedAction,
                    _preTickAppliedActionIndexes
                );
                _engine.TickPrePhysics();
            }

            _engine.TickPhysics(subDelta);

            if (atSecondEnd)
            {
                // Snap away any floating-point drift accumulated across sub-ticks.
                scenario.ElapsedSeconds = Math.Round(scenario.ElapsedSeconds);
                int t = (int)scenario.ElapsedSeconds;

                _engine.TickPostPhysics();
                _engine.ClearTerminalEntries();

                if (scenario.WeatherTimeline is { } timeline)
                {
                    _engine.World.Weather = timeline.GetWeatherAt(t);
                }

                ApplyPendingActionsThrough(t, actions);

                _engine.FireTickCompleted(t);
            }
        }
        finally
        {
            _engine.IsReplayingRecordedActions = previousReplayState;
        }
    }

    /// <summary>
    /// Applies every action at or before <paramref name="t"/> that the pre-tick spawn pass did not
    /// already apply, advancing the stepping cursor past them.
    /// </summary>
    private void ApplyPendingActionsThrough(int t, List<RecordedAction> actions)
    {
        while (_actionCursor < actions.Count && actions[_actionCursor].ElapsedSeconds <= t)
        {
            if (!_preTickAppliedActionIndexes.Contains(_actionCursor))
            {
                _engine.ApplyRecordedAction(actions[_actionCursor]);
            }

            _actionCursor++;
        }
    }
}
