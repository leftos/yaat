using System.Diagnostics.CodeAnalysis;

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
                // The global coordination command (RDAUTO): it acts on the position's list membership, not on an
                // aircraft, so it cannot ride an aircraft compound.
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
        if (!TryParseGenuineCompound(command, out var compound))
        {
            return null;
        }

        return compound.Blocks.SelectMany(b => b.Commands).FirstOrDefault(IsNonCompoundable);
    }

    /// <summary>
    /// Parses a line as a genuinely multi-command compound. False — with no compound — when the single-command
    /// parser accepts the line whole (a free-text command such as NOTE legitimately swallows a <c>;</c> into its
    /// text, and the server's single-command router handles it as that one command), when the line does not parse
    /// as a compound at all, or when the compound turns out to hold a single command.
    /// </summary>
    private static bool TryParseGenuineCompound(string command, [NotNullWhen(true)] out CompoundCommand? compound)
    {
        compound = null;

        var single = CommandParser.Parse(command);
        if ((single.IsSuccess) && (single.Value is not null))
        {
            return false;
        }

        var parsed = CommandParser.ParseCompound(command);
        if ((!parsed.IsSuccess) || (parsed.Value is null))
        {
            return false;
        }

        if (parsed.Value.Blocks.Sum(b => b.Commands.Count) < 2)
        {
            return false;
        }

        compound = parsed.Value;
        return true;
    }

    /// <summary>
    /// The immediate turn in a parallel block that also carries a takeoff clearance (CTO, CTOPP or GO), or null.
    /// `CTO, R270` is a mis-spelling of the departure modifier `CTO MR270`: applied as typed, the clearance rolls
    /// the aircraft and the turn is rejected on the ground (or, before the guard, wedged it). The sequential form
    /// `CTO; R270` is deliberate — the turn queues until airborne — and is not matched.
    /// </summary>
    private static ParsedCommand? FindTakeoffPairedWithImmediateTurn(CompoundCommand compound)
    {
        foreach (var block in compound.Blocks)
        {
            if (!block.Commands.Any(IsTakeoffClearance))
            {
                continue;
            }

            var turn = block.Commands.FirstOrDefault(IsImmediateTurn);
            if (turn is not null)
            {
                return turn;
            }
        }

        return null;
    }

    /// <summary>
    /// The text form the enforcement sites (the server's routing, the client's pre-send validation) call: they hold
    /// the typed line, not a parsed compound.
    /// </summary>
    public static ParsedCommand? FindTakeoffPairedWithImmediateTurn(string command) =>
        TryParseGenuineCompound(command, out var compound) ? FindTakeoffPairedWithImmediateTurn(compound) : null;

    /// <summary>The single wording of the paired-turn refusal, so the server and the client cannot drift.</summary>
    public static string TakeoffPairedWithImmediateTurnMessage(ParsedCommand turn) =>
        $"{CommandDescriber.DescribeCommand(turn)} cannot be paired with a takeoff clearance — {DepartureTurnHint(TurnDegrees(turn))}";

    /// <summary>
    /// The departure form to reach for instead of an immediate turn on the ground. There is a CTO modifier for a
    /// 270 (<c>CTO MR270</c> / <c>CTO ML270</c>) but none for a 360: a bare <c>CTO 360</c> is the fly-heading-360
    /// departure and the MR/ML modifiers cap at 359, so a 360 can only be flown once the aircraft is airborne.
    /// </summary>
    internal static string DepartureTurnHint(int degrees) =>
        degrees == 360
            ? "there is no 360 departure form — clear for takeoff, then issue R360/L360 once airborne"
            : "for a 270° departure use CTO MR270 or CTO ML270";

    /// <summary>A takeoff clearance: bare CTO, the present-position form (CTOPP), and the takeoff-roll release (GO).</summary>
    private static bool IsTakeoffClearance(ParsedCommand cmd) => cmd is ClearedForTakeoffCommand or ClearedTakeoffPresentCommand or GoCommand;

    /// <summary>A 270/360 turn verb: dispatched, it installs a MakeTurnPhase at once rather than queueing.</summary>
    private static bool IsImmediateTurn(ParsedCommand cmd) =>
        cmd is MakeLeft270Command or MakeRight270Command or MakeLeft360Command or MakeRight360Command;

    /// <summary>How far an immediate turn verb turns — the discriminator for the departure-form hint.</summary>
    private static int TurnDegrees(ParsedCommand turn) => turn is MakeLeft360Command or MakeRight360Command ? 360 : 270;

    /// <summary>
    /// A per-aircraft immediate STARS op that bypasses <see cref="CommandDispatcher"/> (track, coordination, strip,
    /// TDLS) — the commands a single-command router would swallow a <c>;</c>/<c>,</c> tail into.
    /// </summary>
    public static bool IsScopedSpecial(ParsedCommand cmd) =>
        TrackEngine.IsTrackCommand(cmd)
        || TrackEngine.IsCoordinationCommand(cmd)
        || TrackEngine.IsStripCommand(cmd)
        || TrackEngine.IsTdlsCommand(cmd);

    /// <summary>
    /// The splitter's bail set: the rejection set plus <c>DEL</c> and <c>APT</c>, which DO have aviation-path chain
    /// semantics (<c>CROSS 28R; DEL</c>, <c>AT 5000 APT OAK</c>) and must reach the dispatcher whole — never be
    /// special-split, never be rejected.
    /// </summary>
    public static bool IsSplitterBail(ParsedCommand cmd) => IsNonCompoundable(cmd) || cmd is DeleteCommand or ChangeDestinationCommand;

    /// <summary>
    /// Detects a multi-command compound (via <c>;</c>/<c>,</c>) that includes a track/coordination/strip/TDLS command
    /// and produces its ordered dispatch units. Returns false — leaving the caller on the single-command path — for a
    /// single command, an aviation-only compound (dispatched whole so its triggers survive), or any compound
    /// containing a bail-set command. A block containing a scoped special is split on <c>,</c> into single commands
    /// so each dispatches alone; an aviation-only block is kept whole.
    /// <para>
    /// Also false when the split gives back the input unchanged. A condition-prefixed scoped special
    /// (<c>WAIT 1 AN 1 ✓</c>, <c>AT FIX HO 3G</c>) has no separator at all: the scheme expander turns the one block
    /// into two commands, which passes the multi-command check, but it is one dispatch unit and splitting it on
    /// <c>,</c> yields the whole input back. The caller re-routes each unit, so returning true there is an infinite
    /// recursion — the aviation arm must take it instead, queueing the strip verb behind the condition.
    /// </para>
    /// </summary>
    public static bool TrySplitSpecialCompound(string command, out List<CompoundUnit> units)
    {
        units = [];

        var parsed = CommandParser.ParseCompound(command);
        if (!parsed.IsSuccess || parsed.Value is null)
        {
            return false;
        }

        var allCommands = parsed.Value.Blocks.SelectMany(b => b.Commands).ToList();
        if ((allCommands.Count < 2) || (!allCommands.Any(IsScopedSpecial)) || (allCommands.Any(IsSplitterBail)))
        {
            return false;
        }

        var blockStrings = command.Split(';');
        var built = new List<CompoundUnit>();
        for (int bi = 0; bi < blockStrings.Length; bi++)
        {
            if (!TrySplitBlock(blockStrings[bi].Trim(), bi, built))
            {
                return false;
            }
        }

        if ((built.Count == 1) && string.Equals(built[0].Text.Trim(), command.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        units = built;
        return true;
    }

    /// <summary>
    /// Adds one <c>;</c> block's units: a block containing a scoped special is split on <c>,</c> into single commands, an
    /// aviation-only block is kept whole. False when a fragment no longer parses — a comma inside a free-text scoped
    /// argument (scratchpad, annotate) would over-split, so the caller bails to the single-command path.
    /// </summary>
    private static bool TrySplitBlock(string block, int blockIndex, List<CompoundUnit> built)
    {
        if (block.Length == 0)
        {
            return false;
        }

        var blockParsed = CommandParser.ParseCompound(block);
        if ((!blockParsed.IsSuccess) || (blockParsed.Value is null))
        {
            return false;
        }

        if (!blockParsed.Value.Blocks.SelectMany(b => b.Commands).Any(IsScopedSpecial))
        {
            built.Add(new CompoundUnit(blockIndex, block));
            return true;
        }

        foreach (var piece in block.Split(','))
        {
            var text = piece.Trim();
            if ((text.Length == 0) || (!CommandParser.ParseCompound(text).IsSuccess))
            {
                return false;
            }

            built.Add(new CompoundUnit(blockIndex, text));
        }

        return true;
    }
}

/// <summary>One dispatch unit of a split scoped-special compound: the <c>;</c> block it came from and its text.</summary>
public readonly record struct CompoundUnit(int BlockIndex, string Text);
