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
    /// <para>
    /// A free-text coordination message ends the chain the way it ends a split: everything from the message's start
    /// to the end of the line is message content, so only the head typed before it is parsed. <c>FH 090; RDTXT /1
    /// HOLD, PAUSE</c> is a heading plus a message whose text happens to end in "PAUSE", not a chained PAUSE, and the
    /// head alone decides the verdict. The head needs only one command — the message is the second.
    /// </para>
    /// </summary>
    private static bool TryParseGenuineCompound(string command, [NotNullWhen(true)] out CompoundCommand? compound)
    {
        compound = null;

        var single = CommandParser.Parse(command);
        if ((single.IsSuccess) && (single.Value is not null))
        {
            return false;
        }

        if (TryWalkUnits(command, out _, out var freeTextStart) && (freeTextStart >= 0))
        {
            var head = command[..freeTextStart].TrimEnd(' ', '\t', ';', ',');
            if (head.Length == 0)
            {
                return false;
            }

            return TryParseCompoundOfAtLeast(head, 1, out compound);
        }

        return TryParseCompoundOfAtLeast(command, 2, out compound);
    }

    /// <summary>
    /// The compound <paramref name="text"/> parses as, when it carries at least <paramref name="minCommands"/> commands.
    /// </summary>
    private static bool TryParseCompoundOfAtLeast(string text, int minCommands, [NotNullWhen(true)] out CompoundCommand? compound)
    {
        compound = null;

        var parsed = CommandParser.ParseCompound(text);
        if ((!parsed.IsSuccess) || (parsed.Value is null) || (parsed.Value.Blocks.Sum(b => b.Commands.Count) < minCommands))
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
    /// True for a scoped special whose argument is a free-text message that runs to the end of the line: the
    /// coordination message verbs (<c>RDTXT</c>, and <c>RDH &lt;list&gt; &lt;text&gt;</c>). A <c>,</c> or <c>;</c>
    /// the instructor typed inside such a message is message content, so the walk stops there and the message keeps
    /// the rest of the line — mirroring the <c>NOTE …; …</c> rule in <see cref="TryParseGenuineCompound"/>.
    /// Deliberately NOT free text: the scratchpad verbs (a STARS scratchpad is 3–4 alphanumerics, so a separator
    /// after one is never message text — <c>SP1 ABC, HO 3G</c> stays a chain), <see cref="StripAnnotateCommand"/>
    /// (the pinned chain contracts <c>AN 1 X, AN 2 Y</c> and <c>AN 1 X; SQVFR</c>), and the half-strip verbs (the
    /// pinned recording rewrite <c>HSA HSTRIP_x a; AN 3 RV</c>). A hold-release with no message (<c>RDH</c>,
    /// <c>RDH 1</c>) carries no text and is not free text.
    /// </summary>
    private static bool IsFreeTextSpecial(ParsedCommand cmd) => cmd is CoordinationModifyCommand or CoordinationHoldCommand { Text: not null };

    /// <summary>
    /// True when <paramref name="text"/> parses whole as one free-text special — i.e. the single-command parser owns
    /// every separator after its verb as message text. Asked of one walked piece, never of the whole line: a message
    /// can start after a <c>,</c> as easily as after a <c>;</c>.
    /// </summary>
    private static bool IsFreeTextLine(string text)
    {
        var parsed = CommandParser.Parse(text);
        return (parsed.IsSuccess) && (parsed.Value is not null) && IsFreeTextSpecial(parsed.Value);
    }

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
    /// recursion — the aviation arm must take it instead, queueing the strip verb behind the condition. That same
    /// guard declines a line that is nothing but a free-text message (<c>RDTXT /1 HOLD, GO</c>).
    /// </para>
    /// <para>
    /// A free-text message (<see cref="IsFreeTextSpecial"/>) ends the walk wherever it starts — after a <c>;</c> or
    /// after a <c>,</c> — and becomes one unit holding the rest of the line, because everything past its verb is
    /// message content. Its words are never parsed: scanning them for scoped specials or bail verbs is what made
    /// <c>HO 3G; RDTXT /1 HOLD, DEL</c> look like a chained delete.
    /// </para>
    /// </summary>
    public static bool TrySplitSpecialCompound(string command, out List<CompoundUnit> units)
    {
        units = [];

        if (!TryWalkUnits(command, out var walked, out var freeTextStart))
        {
            return false;
        }

        if ((walked.Count == 1) && string.Equals(walked[0].Text.Trim(), command.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var freeTextUnits = freeTextStart >= 0 ? 1 : 0;
        if ((!TryParseUnitCommands(walked, walked.Count - freeTextUnits, out var commands)) || (!IsSplittableChain(commands, freeTextUnits)))
        {
            return false;
        }

        units = walked;
        return true;
    }

    /// <summary>
    /// Walks the line's dispatch units in order: <c>;</c> splits it into blocks, a block holding a scoped special
    /// splits further on <c>,</c> into pieces, and a block without one stays whole. The first piece that is a free-text
    /// message ends the walk — it contributes one last unit holding the rest of the original line, taken by offset from
    /// <paramref name="command"/> so the typed spacing survives, and <paramref name="freeTextStart"/> reports where in
    /// <paramref name="command"/> that message starts (-1 when the line has none). False when a block or a piece no
    /// longer parses, which puts the caller back on the single-command path.
    /// </summary>
    private static bool TryWalkUnits(string command, out List<CompoundUnit> units, out int freeTextStart)
    {
        units = [];
        freeTextStart = -1;

        var blockStrings = command.Split(';');
        var blockStart = 0;
        for (int bi = 0; bi < blockStrings.Length; bi++)
        {
            if (!TryBlockPieces(blockStrings[bi], blockStart, out var pieces))
            {
                units = [];
                return false;
            }

            foreach (var piece in pieces)
            {
                if (IsFreeTextLine(piece.Text))
                {
                    freeTextStart = piece.Start;
                    units.Add(new CompoundUnit(bi, command[piece.Start..].Trim()));
                    return true;
                }

                units.Add(new CompoundUnit(bi, piece.Text));
            }

            blockStart += blockStrings[bi].Length + 1;
        }

        return true;
    }

    /// <summary>
    /// The pieces one <c>;</c> block contributes, each with its offset in the original line: a block holding a scoped
    /// special is split on <c>,</c> so every command dispatches alone, an aviation-only block stays whole. False when
    /// the block itself no longer parses.
    /// </summary>
    private static bool TryBlockPieces(string block, int blockStart, out List<LinePiece> pieces)
    {
        pieces = [];

        var trimmed = block.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var parsed = CommandParser.ParseCompound(trimmed);
        if ((!parsed.IsSuccess) || (parsed.Value is null))
        {
            return false;
        }

        if (!parsed.Value.Blocks.SelectMany(b => b.Commands).Any(IsScopedSpecial))
        {
            pieces.Add(new LinePiece(blockStart, trimmed));
            return true;
        }

        return TryCommaPieces(block, blockStart, pieces);
    }

    /// <summary>
    /// Splits a block on <c>,</c> into single-command pieces. False when a fragment no longer parses: a comma inside a
    /// scoped argument that is not free text over-splits into pieces the parser rejects, and the caller bails to the
    /// single-command path rather than dispatch half an argument.
    /// </summary>
    private static bool TryCommaPieces(string block, int blockStart, List<LinePiece> pieces)
    {
        var pieceStart = blockStart;
        foreach (var piece in block.Split(','))
        {
            var text = piece.Trim();
            if ((text.Length == 0) || (!CommandParser.ParseCompound(text).IsSuccess))
            {
                return false;
            }

            pieces.Add(new LinePiece(pieceStart, text));
            pieceStart += piece.Length + 1;
        }

        return true;
    }

    /// <summary>
    /// The commands the first <paramref name="count"/> units parse to — the units before a free-text tail. The tail is
    /// deliberately not parsed: everything in it is message content, so scanning it for verbs is the bug the free-text
    /// rule exists to prevent.
    /// </summary>
    private static bool TryParseUnitCommands(List<CompoundUnit> units, int count, out List<ParsedCommand> commands)
    {
        commands = [];

        for (int i = 0; i < count; i++)
        {
            var parsed = CommandParser.ParseCompound(units[i].Text);
            if ((!parsed.IsSuccess) || (parsed.Value is null))
            {
                return false;
            }

            commands.AddRange(parsed.Value.Blocks.SelectMany(b => b.Commands));
        }

        return true;
    }

    /// <summary>
    /// The split verdict over the walked units: at least two commands in all, at least one scoped special, and no
    /// bail-set command among them. A free-text tail counts as the one scoped-special command it is, and can never be
    /// a bail command — its words were never commands.
    /// </summary>
    private static bool IsSplittableChain(List<ParsedCommand> commands, int freeTextUnits)
    {
        if (commands.Count + freeTextUnits < 2)
        {
            return false;
        }

        if ((freeTextUnits == 0) && (!commands.Any(IsScopedSpecial)))
        {
            return false;
        }

        return !commands.Any(IsSplitterBail);
    }

    /// <summary>One walked piece of the typed line: its offset in the original string and its trimmed text.</summary>
    private readonly record struct LinePiece(int Start, string Text);
}

/// <summary>One dispatch unit of a split scoped-special compound: the <c>;</c> block it came from and its text.</summary>
public readonly record struct CompoundUnit(int BlockIndex, string Text);
