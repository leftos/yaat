namespace Yaat.Sim.Commands;

/// <summary>
/// Which commands may participate in a `;`/`,` compound. Shared by the server's dispatch routing
/// (`RoomEngine`) and the client's pre-send validation (`MainViewModel`) so the two cannot drift —
/// a chained non-compoundable command must be rejected with the same verdict on both sides.
/// </summary>
public static class CompoundPolicy
{
    /// <summary>
    /// True for a command that owns a dedicated server routing arm (sim-control, flight-plan,
    /// spawn, room-wide/global ops) and has no aviation-path chain semantics — e.g. "HO 3G; PAUSE"
    /// must not pause the sim, and a queued PAUSE block would no-op at fire time. A compound
    /// containing one of these is rejected outright with an error naming the verb. Deliberately
    /// NOT in this set: <see cref="DeleteCommand"/> ("CROSS 28R; DEL", issue #311, via
    /// CommandBlock.HasDeleteCommand) and <see cref="ChangeDestinationCommand"/> ("AT 5000 APT OAK")
    /// — both dispatch correctly through the command queue. Keep in sync with the server's
    /// pre-HandleStandardCmd routing arms.
    /// </summary>
    public static bool IsNonCompoundable(ParsedCommand cmd) =>
        cmd
            is ShowQueuedCommand
                or CreateFlightPlanCommand
                or CreateAbbreviatedFlightPlanCommand
                or SetRemarksCommand
                or NoteCommand
                or DeleteQueuedCommand
                or SpawnNowCommand
                or SpawnDelayCommand
                or SetActivePositionCommand
                or AcceptAllHandoffsCommand
                or InitiateHandoffAllCommand
                or SquawkAllCommand
                or SquawkNormalAllCommand
                or SquawkStandbyAllCommand
                or TaxiAllCommand
                or HoldForReleaseCommand
                or DisarmHoldForReleaseCommand
                or ReleaseDepartureCommand
                or CfrDepartureCommand
                or TimerCommand
                or TdlsOpsConfigCommand
                or ConsolidateCommand
                or DeconsolidateCommand
                or PauseCommand
                or UnpauseCommand
                or SimRateCommand
                or AsdexEnableAllAlertsCommand
                or AddAircraftCommand
                or GhostTrackCommand
                // The global coordination command (RDAUTO) — mirrors
                // CoordinationCommandHandler.IsGlobalCoordinationCommand on the server.
                or CoordinationAutoAckCommand;

    /// <summary>
    /// True for a command that edits only the flight plan (DA / FP / RMK). Live, the server routes these
    /// through its flight-plan arm before the aircraft dispatcher ever sees them, so they never touch a
    /// phase or the command queue; replay and reconstruction must treat them the same way, and the
    /// dispatcher refuses them outright so no path can turn a flight-plan edit into a manoeuvre.
    /// </summary>
    public static bool IsFlightPlanCommand(ParsedCommand cmd) =>
        cmd is CreateFlightPlanCommand or CreateAbbreviatedFlightPlanCommand or SetRemarksCommand;

    /// <summary>
    /// Returns the first rejection-set command in a genuinely multi-command compound, or null.
    /// A line the single-command parser accepts whole is not a chain — a free-text command
    /// (NOTE/RMK/...) legitimately swallows ';' into its text, and the server's single-command
    /// router handles it as that one command even when the tail would also parse (e.g.
    /// "NOTE ...; expect delay"). Shared by the server's dispatch routing and the client's
    /// pre-send validation so the verdicts cannot drift.
    /// </summary>
    public static ParsedCommand? FindNonCompoundableInChain(string command)
    {
        var single = CommandParser.Parse(command);
        if (single.IsSuccess && single.Value is not null)
        {
            return null;
        }

        var parsed = CommandParser.ParseCompound(command);
        if (!parsed.IsSuccess || parsed.Value is null)
        {
            return null;
        }

        var allCommands = parsed.Value.Blocks.SelectMany(b => b.Commands).ToList();
        if (allCommands.Count < 2)
        {
            return null;
        }

        return allCommands.Find(IsNonCompoundable);
    }
}
