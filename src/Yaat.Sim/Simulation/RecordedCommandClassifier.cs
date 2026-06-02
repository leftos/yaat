using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation;

/// <summary>
/// High-level kind of a recorded command, used to drive replay-time dispatch
/// in both <see cref="SimulationEngine.ReplayCommand"/> (Sim-side, used by
/// <see cref="SimulationEngine.Replay(SessionRecording, double)"/>) and the
/// server's <c>RecordingManager.ReplayCommand</c> (used by bug-bundle export).
///
/// Both replay paths previously maintained parallel parse-and-decide flows
/// that drifted apart — most notably commit 1f8d1f66 patched the Sim-side to
/// fall through to <see cref="CommandParser.ParseCompound"/> on single-parse
/// failure, but the server-side wasn't ported, silently dropping every recorded
/// compound command (<c>;</c>/<c>,</c>) in regenerated bug-bundle snapshots.
///
/// <see cref="Compound"/> is the *default* — both single-parse failure and the
/// catch-all switch arm route to it. Adding a new replay-special command type
/// requires both a new enum value and a switch arm in each replay path;
/// forgetting either leaves the new type routing to <see cref="Compound"/>,
/// which is the safe (and historically correct) default.
/// </summary>
public enum RecordedCommandKind
{
    /// <summary>
    /// Default branch. Routes through <see cref="CommandParser.ParseCompound"/>
    /// + <see cref="CommandDispatcher.DispatchCompound"/>. Reached by single-parse
    /// failure (multi-verb compounds) or by the catch-all switch arm for any
    /// parsed type that isn't replay-special.
    /// </summary>
    Compound,

    SayOrShow,
    Delete,
    DeleteQueued,
    TrackOwnership,
    GhostTrack,
    Strip,
    Coordination,
    Consolidate,
    Deconsolidate,
    SpawnNow,
    SpawnDelay,
    SquawkAll,
    AcceptAllHandoffs,
    InitiateHandoffAll,
    Note,
}

public static class RecordedCommandClassifier
{
    public readonly record struct Classification(RecordedCommandKind Kind, ParsedCommand? Parsed);

    /// <summary>
    /// Classifies a recorded command body (caller has already extracted any
    /// <c>AS {tcp}</c> prefix). AS-prefix dispatch stays in each repo's
    /// ReplayCommand because Sim and server resolve identity differently
    /// (<c>ReplayTrackApplier</c> vs <c>TrackHandler.ResolveEffectiveIdentity</c>).
    /// </summary>
    public static Classification Classify(string commandText)
    {
        var result = CommandParser.Parse(commandText);
        if (!result.IsSuccess || result.Value is null)
        {
            return new Classification(RecordedCommandKind.Compound, null);
        }

        var parsed = result.Value;
        return parsed switch
        {
            SayCommand
            or SaySpeedCommand
            or SayMachCommand
            or SayExpectedApproachCommand
            or SayAltitudeCommand
            or SayHeadingCommand
            or SayPositionCommand
            or ShowQueuedCommand => new Classification(RecordedCommandKind.SayOrShow, parsed),
            DeleteCommand => new Classification(RecordedCommandKind.Delete, parsed),
            DeleteQueuedCommand => new Classification(RecordedCommandKind.DeleteQueued, parsed),
            GhostTrackCommand => new Classification(RecordedCommandKind.GhostTrack, parsed),
            _ when TrackEngine.IsStripCommand(parsed) => new Classification(RecordedCommandKind.Strip, parsed),
            _ when TrackEngine.IsTrackCommand(parsed) => new Classification(RecordedCommandKind.TrackOwnership, parsed),
            _ when TrackEngine.IsCoordinationCommand(parsed) => new Classification(RecordedCommandKind.Coordination, parsed),
            ConsolidateCommand => new Classification(RecordedCommandKind.Consolidate, parsed),
            DeconsolidateCommand => new Classification(RecordedCommandKind.Deconsolidate, parsed),
            SpawnNowCommand => new Classification(RecordedCommandKind.SpawnNow, parsed),
            SpawnDelayCommand => new Classification(RecordedCommandKind.SpawnDelay, parsed),
            SquawkAllCommand or SquawkNormalAllCommand or SquawkStandbyAllCommand => new Classification(RecordedCommandKind.SquawkAll, parsed),
            AcceptAllHandoffsCommand => new Classification(RecordedCommandKind.AcceptAllHandoffs, parsed),
            InitiateHandoffAllCommand => new Classification(RecordedCommandKind.InitiateHandoffAll, parsed),
            NoteCommand => new Classification(RecordedCommandKind.Note, parsed),
            _ => new Classification(RecordedCommandKind.Compound, parsed),
        };
    }
}
