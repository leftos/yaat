using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Actions;

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
/// <para>
/// Every <see cref="ParsedCommand"/> subtype has exactly one kind, and every kind has one
/// <see cref="ActionScope"/> (<see cref="RecordedCommandClassifier.ScopeOf"/>). There is no default arm:
/// <see cref="RecordedCommandClassifier.ClassifyParsed"/> throws for a subtype nothing has claimed, so adding a
/// command type means deciding what it is addressed to before it can be recorded and replayed. The default it
/// replaced routed every unclaimed type to <see cref="Compound"/>, which was safe for an aircraft-scoped command and
/// silently dropped every global one at the appliers' aircraft-exists guard (<c>SQALL</c>, <c>TAXIALL</c>, <c>ADD</c>,
/// <c>ASDXALERTS</c>, the TDLS verbs — the 2026-09-05 audit).
/// </para>
/// </summary>
public enum RecordedCommandKind
{
    /// <summary>
    /// An aviation instruction — anything <see cref="CommandDispatcher"/> owns, including a multi-verb chain the
    /// single-command parser cannot read. Routes through <see cref="CommandParser.ParseCompound"/> +
    /// <see cref="CommandDispatcher.DispatchCompound"/> against the addressed aircraft.
    /// </summary>
    Compound,

    SayOrShow,

    /// <summary>
    /// DA / FP / RMK. Live, the server's flight-plan arm applied these and recorded the resulting
    /// <see cref="RecordedAmendFlightPlan"/> alongside the command, so on replay the command itself is a
    /// no-op: the flight-plan state arrives through the amendment, and dispatching the command would put
    /// a flight-plan edit through the phase gate (a hold cancels on any non-additive command).
    /// </summary>
    FlightPlan,
    Delete,
    DeleteQueued,
    TrackOwnership,
    GhostTrack,
    Reposition,
    Strip,
    Coordination,

    /// <summary>RDAUTO — coordination auto-acknowledge for a position, no aircraft.</summary>
    GlobalCoordination,
    Consolidate,
    Deconsolidate,
    SpawnNow,
    SpawnDelay,
    SquawkAll,
    AcceptAllHandoffs,
    InitiateHandoffAll,
    Note,
    Timer,
    HoldForRelease,
    DisarmHoldForRelease,
    ReleaseDeparture,

    /// <summary>TAXIALL — every aircraft at parking taxis to the named runway.</summary>
    TaxiAll,

    /// <summary>TDLSOPS — a facility's active operational configuration.</summary>
    TdlsOps,

    /// <summary>TDLSQ / TDLSS / TDLSW / TDLSD — one aircraft's PDC lifecycle.</summary>
    Tdls,

    /// <summary>ASDXALERTS — clear every ASDE-X alert inhibit in the room.</summary>
    AsdexEnableAllAlerts,

    /// <summary>ADD — spawn an aircraft from a spawn spec (draws the shared RNG live).</summary>
    AddAircraft,

    /// <summary>CFR — a departure's release window (wall-clock UTC live).</summary>
    Cfr,

    /// <summary>A bare AS — the issuing connection's active position, no aircraft.</summary>
    SetActivePosition,

    /// <summary>BM — timeline bookmarks; deliberately never recorded.</summary>
    Bookmark,

    /// <summary>PAUSE / UNPAUSE / SIMRATE — the room's clock; deliberately never recorded.</summary>
    Transport,
}

/// <summary>A <see cref="ParsedCommand"/> subtype no arm of the classifier has claimed. Decide its kind and scope; there is no default.</summary>
public sealed class UnroutedCommandException(Type commandType)
    : InvalidOperationException(
        $"{commandType.Name} has no RecordedCommandKind — add it to RecordedCommandClassifier.ClassifyParsed (and ScopeOf if it is a new kind)"
    )
{
    public Type CommandType { get; } = commandType;
}

public static class RecordedCommandClassifier
{
    public readonly record struct Classification(RecordedCommandKind Kind, ActionScope Scope, ParsedCommand? Parsed);

    /// <summary>
    /// Classifies a recorded command body (caller has already extracted any
    /// <c>AS {tcp}</c> prefix with <c>TrackResolver.ExtractAsPrefix</c>; the prefix resolves to the acting identity
    /// through <c>TrackResolver.ResolveIdentity</c> on every run kind).
    /// A body the single-command parser rejects is a multi-verb chain: <see cref="RecordedCommandKind.Compound"/>
    /// against the addressed aircraft, with <c>Parsed</c> null.
    /// </summary>
    public static Classification Classify(string commandText)
    {
        var result = CommandParser.Parse(commandText);
        if (!result.IsSuccess || result.Value is null)
        {
            return new Classification(RecordedCommandKind.Compound, ActionScope.Aircraft, null);
        }

        return ClassifyParsed(result.Value);
    }

    /// <summary>
    /// The type-driven half of <see cref="Classify"/>. Exhaustive: a subtype no arm claims throws
    /// <see cref="UnroutedCommandException"/> rather than falling into an aircraft-scoped default.
    /// </summary>
    public static Classification ClassifyParsed(ParsedCommand parsed)
    {
        var kind = KindOf(parsed);
        return new Classification(kind, ScopeOf(kind), parsed);
    }

    /// <summary>What each kind is addressed to — the property the router resolves before any arm runs.</summary>
    public static ActionScope ScopeOf(RecordedCommandKind kind) =>
        kind switch
        {
            RecordedCommandKind.Compound => ActionScope.Aircraft,
            RecordedCommandKind.SayOrShow => ActionScope.Aircraft,
            RecordedCommandKind.FlightPlan => ActionScope.Callsign,
            RecordedCommandKind.Delete => ActionScope.Callsign,
            RecordedCommandKind.DeleteQueued => ActionScope.Aircraft,
            RecordedCommandKind.TrackOwnership => ActionScope.Aircraft,
            RecordedCommandKind.GhostTrack => ActionScope.Callsign,
            RecordedCommandKind.Reposition => ActionScope.Aircraft,
            RecordedCommandKind.Strip => ActionScope.Callsign,
            RecordedCommandKind.Coordination => ActionScope.Aircraft,
            RecordedCommandKind.GlobalCoordination => ActionScope.Position,
            RecordedCommandKind.Consolidate => ActionScope.Global,
            RecordedCommandKind.Deconsolidate => ActionScope.Global,
            RecordedCommandKind.SpawnNow => ActionScope.Callsign,
            RecordedCommandKind.SpawnDelay => ActionScope.Callsign,
            RecordedCommandKind.SquawkAll => ActionScope.Global,
            RecordedCommandKind.AcceptAllHandoffs => ActionScope.Position,
            RecordedCommandKind.InitiateHandoffAll => ActionScope.Position,
            RecordedCommandKind.Note => ActionScope.Aircraft,
            RecordedCommandKind.Timer => ActionScope.Callsign,
            RecordedCommandKind.HoldForRelease => ActionScope.Global,
            RecordedCommandKind.DisarmHoldForRelease => ActionScope.Global,
            RecordedCommandKind.ReleaseDeparture => ActionScope.Global,
            RecordedCommandKind.TaxiAll => ActionScope.Global,
            RecordedCommandKind.TdlsOps => ActionScope.Global,
            RecordedCommandKind.Tdls => ActionScope.Aircraft,
            RecordedCommandKind.AsdexEnableAllAlerts => ActionScope.Global,
            RecordedCommandKind.AddAircraft => ActionScope.Global,
            RecordedCommandKind.Cfr => ActionScope.Aircraft,
            RecordedCommandKind.SetActivePosition => ActionScope.Position,
            RecordedCommandKind.Bookmark => ActionScope.Global,
            RecordedCommandKind.Transport => ActionScope.Global,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every RecordedCommandKind needs an ActionScope"),
        };

    private static RecordedCommandKind KindOf(ParsedCommand parsed) =>
        parsed switch
        {
            SayCommand
            or SaySpeedCommand
            or SayMachCommand
            or SayExpectedApproachCommand
            or SayAltitudeCommand
            or SayHeadingCommand
            or SayPositionCommand
            or ShowQueuedCommand => RecordedCommandKind.SayOrShow,
            // Before the track family: a bare AS is a member of TrackEngine.IsTrackCommand, but it addresses the
            // issuing connection's position, not an aircraft.
            SetActivePositionCommand => RecordedCommandKind.SetActivePosition,
            _ when CompoundPolicy.IsFlightPlanCommand(parsed) => RecordedCommandKind.FlightPlan,
            DeleteCommand => RecordedCommandKind.Delete,
            DeleteQueuedCommand => RecordedCommandKind.DeleteQueued,
            GhostTrackCommand => RecordedCommandKind.GhostTrack,
            RepositionToLocationCommand or RepositionMoveCommand => RecordedCommandKind.Reposition,
            _ when TrackEngine.IsStripCommand(parsed) => RecordedCommandKind.Strip,
            TdlsOpsConfigCommand => RecordedCommandKind.TdlsOps,
            _ when TrackEngine.IsTdlsCommand(parsed) => RecordedCommandKind.Tdls,
            _ when TrackEngine.IsTrackCommand(parsed) => RecordedCommandKind.TrackOwnership,
            // Before the coordination family: RDAUTO is a member of TrackEngine.IsCoordinationCommand but is
            // addressed to a position, not an aircraft.
            CoordinationAutoAckCommand => RecordedCommandKind.GlobalCoordination,
            _ when TrackEngine.IsCoordinationCommand(parsed) => RecordedCommandKind.Coordination,
            ConsolidateCommand => RecordedCommandKind.Consolidate,
            DeconsolidateCommand => RecordedCommandKind.Deconsolidate,
            SpawnNowCommand => RecordedCommandKind.SpawnNow,
            SpawnDelayCommand => RecordedCommandKind.SpawnDelay,
            SquawkAllCommand or SquawkNormalAllCommand or SquawkStandbyAllCommand => RecordedCommandKind.SquawkAll,
            AcceptAllHandoffsCommand => RecordedCommandKind.AcceptAllHandoffs,
            InitiateHandoffAllCommand => RecordedCommandKind.InitiateHandoffAll,
            NoteCommand => RecordedCommandKind.Note,
            TimerCommand => RecordedCommandKind.Timer,
            HoldForReleaseCommand => RecordedCommandKind.HoldForRelease,
            DisarmHoldForReleaseCommand => RecordedCommandKind.DisarmHoldForRelease,
            ReleaseDepartureCommand => RecordedCommandKind.ReleaseDeparture,
            TaxiAllCommand => RecordedCommandKind.TaxiAll,
            AsdexEnableAllAlertsCommand => RecordedCommandKind.AsdexEnableAllAlerts,
            AddAircraftCommand => RecordedCommandKind.AddAircraft,
            CfrDepartureCommand => RecordedCommandKind.Cfr,
            BookmarkCommand => RecordedCommandKind.Bookmark,
            PauseCommand or UnpauseCommand or SimRateCommand => RecordedCommandKind.Transport,
            _ when IsAviationCommand(parsed) => RecordedCommandKind.Compound,
            _ => throw new UnroutedCommandException(parsed.GetType()),
        };

    /// <summary>
    /// The command types <see cref="CommandDispatcher"/> owns — every instruction to an aircraft, from a heading to a
    /// taxi clearance, plus the parser's <see cref="UnsupportedCommand"/> placeholder and the live-traffic
    /// <see cref="AssumeCommand"/>, both of which the dispatcher answers itself. Listed explicitly, not defaulted:
    /// <c>ActionRoutingCompletenessTests</c> checks it against the dispatcher in both directions, so a type added
    /// here without a dispatcher arm, or given an arm without being added here, fails the build's tests.
    /// </summary>
    public static bool IsAviationCommand(ParsedCommand cmd) =>
        cmd
            is AcknowledgePilotContactCommand
                or AirTaxiCommand
                or AppendDirectToCommand
                or AppendForceDirectToCommand
                or AssignRunwayCommand
                or AssumeCommand
                or BreakConflictCommand
                or Cancel270Command
                or CancelAutoDeleteCommand
                or CancelIfrCommand
                or CancelLandingClearanceCommand
                or CancelTakeoffClearanceCommand
                or ChangeDestinationCommand
                or CircleAirportCommand
                or ClearRunwayCommand
                or ClearTurnRateCommand
                or ClearedApproachCommand
                or ClearedApproachStraightInCommand
                or ClearedBravoAirspaceCommand
                or ClearedForOptionCommand
                or ClearedForTakeoffCommand
                or ClearedIntoMilitaryRouteCommand
                or ClearedOutOfMilitaryRouteCommand
                or ClearedTakeoffPresentCommand
                or ClearedToConductRefuelingCommand
                or ClearedToLandCommand
                or ClearedVisualApproachCommand
                or ClimbMaintainCommand
                or ClimbViaCommand
                or ConstrainedForceDirectToCommand
                or ContactCommand
                or CrossFixCommand
                or CrossRunwayCommand
                or DeleteSpeedRestrictionsCommand
                or DepartFixCommand
                or DescendMaintainCommand
                or DescendViaCommand
                or DirectToCommand
                or EnterFinalCommand
                or EnterLeftBaseCommand
                or EnterLeftCrosswindCommand
                or EnterLeftDownwindCommand
                or EnterRightBaseCommand
                or EnterRightCrosswindCommand
                or EnterRightDownwindCommand
                or ExitLeftCommand
                or ExitRightCommand
                or ExitTaxiwayCommand
                or ExpectApproachCommand
                or ExpediteCommand
                or ExtendPatternCommand
                or FlyHeadingCommand
                or FlyPresentHeadingCommand
                or FollowCommand
                or FollowGroundCommand
                or ForceAltitudeCommand
                or ForceDirectToCommand
                or ForceHeadingCommand
                or ForceLandingCommand
                or ForceSpeedCommand
                or FrequencyChangeApprovedCommand
                or GiveWayCommand
                or GoAroundCommand
                or GoCommand
                or HoldAtFixHoverCommand
                or HoldAtFixOrbitCommand
                or HoldPositionCommand
                or HoldPresentPosition360Command
                or HoldPresentPositionHoverCommand
                or HoldShortCommand
                or HoldingPatternCommand
                or IdentCommand
                or JoinAirwayCommand
                or JoinApproachCommand
                or JoinApproachStraightInCommand
                or JoinFinalApproachCourseCommand
                or JoinRadialInboundCommand
                or JoinRadialOutboundCommand
                or JoinStarCommand
                or LandAndHoldShortCommand
                or LandCommand
                or LeftTurnCommand
                or LineUpAndWaitCommand
                or ListApproachesCommand
                or LowApproachCommand
                or MachCommand
                or MaintainMilitaryRouteAltitudesCommand
                or MakeLeft270Command
                or MakeLeft360Command
                or MakeLeftSTurnsCommand
                or MakeLeftTrafficCommand
                or MakeNormalApproachCommand
                or MakeRight270Command
                or MakeRight360Command
                or MakeRightSTurnsCommand
                or MakeRightTrafficCommand
                or MakeShortApproachCommand
                or NormalRateCommand
                or OffsetLeftPatternCommand
                or OffsetRightPatternCommand
                or PatternSizeCommand
                or Plan270Command
                or PositionTurnAltitudeClearanceCommand
                or PushbackCommand
                or RandomSquawkCommand
                or ReduceToFinalApproachSpeedCommand
                or ReportCommand
                or ReportFieldAdvisoryCommand
                or ReportFieldInSightCommand
                or ReportFieldInSightForcedCommand
                or ReportTrafficAdvisoryCommand
                or ReportTrafficInSightCommand
                or ReportTrafficInSightForcedCommand
                or ReportTrafficLandmarkCommand
                or ReportTrafficPatternCommand
                or ReportTrafficRelativeCommand
                or ResumeCommand
                or ResumeNormalSpeedCommand
                or RightTurnCommand
                or SafetyAlertCommand
                or SayExitFixEstimateCommand
                or SetTurnRateCommand
                or SpeedCommand
                or SquawkCommand
                or SquawkNormalCommand
                or SquawkResetCommand
                or SquawkStandbyCommand
                or SquawkVfrCommand
                or StopAndGoCommand
                or TaxiAutoCommand
                or TaxiCommand
                or TouchAndGoCommand
                or TurnBaseCommand
                or TurnCrosswindCommand
                or TurnDownwindCommand
                or TurnLeftCommand
                or TurnLeftDirectToCommand
                or TurnRightCommand
                or TurnRightDirectToCommand
                or UnsupportedCommand
                or WaitCommand
                or WaitDistanceCommand
                or WakeAdvisoryCommand
                or WarpCommand
                or WarpGroundCommand;
}
