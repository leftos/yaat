using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// Drives a recording forward over a <see cref="SimulationEngine"/> — a whole range, one second, or one physics
/// sub-tick — by running the spine under a <see cref="ReplayHost"/>. It owns the <see cref="ReplayCursors"/> its
/// stepping entry points advance through the action log; a range replay walks a caller-supplied list with cursors of
/// its own, so the two traversals never interfere.
///
/// That cursor state is the whole reason this is a separate object. It is meaningful only while a recording is
/// being replayed, whereas the <see cref="RunProfile"/> stays on the engine because members outside replay read it to
/// decide whether they may record an action, spawn generated traffic or run an AI brain — that describes the run the
/// engine is in, not this driver's bookkeeping. Each stepping call here runs under
/// <see cref="SimulationEngine.EnterReplay"/> and hands the previous profile back after.
///
/// The engine exposes every driver method as its own, so no caller names this type.
/// </summary>
internal sealed class ReplayDriver(SimulationEngine engine)
{
    private readonly SimulationEngine _engine = engine;

    private ReplayHost? _host;

    /// <summary>Whether a recording has been loaded and the cursors positioned — replay stepping is a no-op until then.</summary>
    private bool IsArmed => _host is not null;

    /// <summary>
    /// Re-points the cursors at the engine's current time after a snapshot restore. Without it a subsequent
    /// <see cref="OneSecond"/> would treat actions from t=0 onward as still pending and re-apply them on top of the
    /// restored state. This is what makes the hybrid pattern work: replay to load the scenario, restore to jump to a
    /// saved state, then step forward from there.
    /// </summary>
    public void ReseekAfterRestore(int restoredSeconds)
    {
        _host?.Cursors.SeekTo(restoredSeconds);
    }

    /// <summary>Arms the driver with an action log and positions the cursors at <paramref name="seconds"/>.</summary>
    public void Arm(List<RecordedAction> actions, int seconds)
    {
        var cursors = new ReplayCursors(actions);
        cursors.SeekTo(seconds);
        _host = new ReplayHost(_engine, cursors, applier: null);
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
            _engine.PositionSelections.Clear();
        }

        using (_engine.EnterReplay())
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

            // A range replay walks the caller's list with its own cursors, leaving the driver's stepping cursors
            // untouched — the two are independent traversals of the log.
            var host = new ReplayHost(_engine, new ReplayCursors(actions), actionApplier);
            if (startSeconds == 0)
            {
                // Actions at t=0 land before the first second: spawns first, then settings and immediate commands.
                host.ApplyPreTickRecordedActions(0);
                host.ApplyRecordedActionsThrough(0);
            }
            else
            {
                host.Cursors.SeekTo(startSeconds);
            }

            // The range starts where the caller says it does, whatever the engine's clock read before.
            var scenario = _engine.Scenario!;
            scenario.ElapsedSeconds = startSeconds;

            for (int t = startSeconds + 1; t <= targetSeconds; t++)
            {
                _engine.RunSecond(host);

                if (archiveForVerification is not null && drifts is not null && verifyByTimestamp.TryGetValue(t, out var snapIdx))
                {
                    var snap = archiveForVerification.ReadSnapshot(snapIdx);
                    var report = SnapshotDiff.Compare(t, snap, _engine.World.GetSnapshot());
                    if (report.AircraftDrifts.Count > 0)
                    {
                        drifts.Add(report);
                    }
                }
            }
        }
    }

    public void To(SessionRecording recording, double targetSeconds, Action<SimScenarioState> configureAfterLoad)
    {
        _engine.TickTimings?.Clear();
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
        if (_engine.Scenario is null || _host is not { } host)
        {
            return;
        }

        using (_engine.EnterReplay())
        {
            _engine.RunSecond(host);
        }
    }

    /// <summary>
    /// One physics sub-tick. The clock advances by a quarter second; the second opens on the first sub-tick and
    /// closes on the fourth, so four calls from an integer second run the same segments as <see cref="OneSecond"/>.
    /// Pre-physics runs at a quarter past the previous integer here, where <see cref="OneSecond"/> runs it at the
    /// integer — a pre-existing difference the trace and the spine parity test make visible rather than hide.
    /// </summary>
    public void OneSubTick()
    {
        var scenario = _engine.Scenario;
        if (scenario is null || _host is not { } host)
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

        int subTick = (int)Math.Round((scenario.ElapsedSeconds - Math.Floor(prev + eps)) / subDelta) - 1;

        using (_engine.EnterReplay())
        {
            if (atSecondStart)
            {
                _engine.OpenSecond(host);
                _engine.RunPrePhysics(host);
            }

            _engine.RunPhysicsSubTick(subDelta, subTick);

            if (atSecondEnd)
            {
                // Snap away any floating-point drift accumulated across sub-ticks.
                scenario.ElapsedSeconds = Math.Round(scenario.ElapsedSeconds);

                _engine.RunPostPhysics(host);
                _engine.RunEndOfSecond(host);
            }
        }
    }
}
