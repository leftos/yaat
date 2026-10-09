using System.Collections.Frozen;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Which verbs a controller sends without selecting an aircraft. The answer is read off
/// <see cref="RecordedCommandClassifier.ScopeOf"/>, the scope the router resolves before any arm runs, so the client's
/// routing and the server's cannot disagree about a verb. <see cref="Commands.CommandDefinition.IsGlobal"/> is a different
/// question (the pilot never answers Unable) and is not used here.
/// </summary>
public static class CommandScopes
{
    /// <summary>
    /// The <see cref="RecordedCommandKind"/> each verb sent without a selection parses to. Only the verbs whose kind is
    /// room-addressed (<see cref="ActionScope.Global"/> or <see cref="ActionScope.Position"/>), plus the two
    /// callsign-scoped verbs in <see cref="IsCallsignVerbSentWithoutSelection"/>; <c>CommandScopesTests</c> checks every
    /// entry against the parser and every room-addressed kind against the table.
    /// </summary>
    public static readonly FrozenDictionary<CanonicalCommandType, RecordedCommandKind> VerbKinds = new Dictionary<
        CanonicalCommandType,
        RecordedCommandKind
    >
    {
        [CanonicalCommandType.Pause] = RecordedCommandKind.Transport,
        [CanonicalCommandType.Unpause] = RecordedCommandKind.Transport,
        [CanonicalCommandType.SimRate] = RecordedCommandKind.Transport,
        [CanonicalCommandType.Add] = RecordedCommandKind.AddAircraft,
        [CanonicalCommandType.SquawkAll] = RecordedCommandKind.SquawkAll,
        [CanonicalCommandType.SquawkNormalAll] = RecordedCommandKind.SquawkAll,
        [CanonicalCommandType.SquawkStandbyAll] = RecordedCommandKind.SquawkAll,
        [CanonicalCommandType.Consolidate] = RecordedCommandKind.Consolidate,
        [CanonicalCommandType.ConsolidateFull] = RecordedCommandKind.Consolidate,
        [CanonicalCommandType.Deconsolidate] = RecordedCommandKind.Deconsolidate,
        [CanonicalCommandType.SetActivePosition] = RecordedCommandKind.SetActivePosition,
        [CanonicalCommandType.AcceptAllHandoffs] = RecordedCommandKind.AcceptAllHandoffs,
        [CanonicalCommandType.InitiateHandoffAll] = RecordedCommandKind.InitiateHandoffAll,
        [CanonicalCommandType.CoordinationAutoAck] = RecordedCommandKind.GlobalCoordination,
        [CanonicalCommandType.TaxiAll] = RecordedCommandKind.TaxiAll,
        [CanonicalCommandType.TdlsOpsConfig] = RecordedCommandKind.TdlsOps,
        [CanonicalCommandType.Bookmark] = RecordedCommandKind.Bookmark,
        [CanonicalCommandType.HoldForRelease] = RecordedCommandKind.HoldForRelease,
        [CanonicalCommandType.DisarmHoldForRelease] = RecordedCommandKind.DisarmHoldForRelease,
        [CanonicalCommandType.ReleaseDeparture] = RecordedCommandKind.ReleaseDeparture,
        [CanonicalCommandType.ActiveRunways] = RecordedCommandKind.ActiveRunways,
        [CanonicalCommandType.AsdexEnableAllAlerts] = RecordedCommandKind.AsdexEnableAllAlerts,
        [CanonicalCommandType.GhostTrack] = RecordedCommandKind.GhostTrack,
        [CanonicalCommandType.Timer] = RecordedCommandKind.Timer,
    }.ToFrozenDictionary();

    /// <summary>
    /// True for a verb the controller sends without selecting an aircraft (the client sends it with an empty callsign):
    /// every verb whose kind is addressed to the room or to the issuing position, plus the callsign-scoped pair in
    /// <see cref="IsCallsignVerbSentWithoutSelection"/>.
    /// </summary>
    public static bool SendsWithoutSelection(CanonicalCommandType type)
    {
        if (!VerbKinds.TryGetValue(type, out RecordedCommandKind kind))
        {
            return false;
        }

        return (RecordedCommandClassifier.ScopeOf(kind) is ActionScope.Global or ActionScope.Position) || IsCallsignVerbSentWithoutSelection(type);
    }

    /// <summary>
    /// The two callsign-scoped verbs that still need no selection: <c>GHOST</c> names the track's callsign in its own
    /// argument, and <c>TIMER</c> sent with an empty callsign is the room's timer.
    /// </summary>
    private static bool IsCallsignVerbSentWithoutSelection(CanonicalCommandType type) =>
        type is CanonicalCommandType.GhostTrack or CanonicalCommandType.Timer;
}
