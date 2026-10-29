using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Builds deterministic pilot-readback strings from accepted dispatched commands. Used by
/// <see cref="CommandDispatcher"/> in solo-training mode to queue readbacks for the
/// delayed SAY/audio channel.
///
/// Most readbacks come from inverting <see cref="PhraseologyRule"/>s via
/// <see cref="PhraseologyVerbalizer"/> — controllers and pilots share the same vocabulary,
/// so the rule that would have parsed the controller's words is also the readback. Pilot-only
/// utterances (spawn check-in, "going around" volunteered) live here directly because they
/// have no controller-side rule equivalent.
///
/// Spoken output format:
/// <code>"[N123AB] descend and maintain five thousand, november one two three alpha bravo."</code>
/// The <c>[ICAO]</c> bracket prefix is a legacy terminal aid. Solo-mode terminal display removes
/// it and compacts obvious spoken runway/distance forms, while TTS keeps the spoken phrase
/// without the bracket prefix.
/// </summary>
public static partial class PilotResponder
{
    private static readonly ILogger Log = SimLog.CreateLogger("PilotResponder");

    public const string SourceResponse = "Response";
    public const string SourceSayReadback = "SayReadback";

    /// <summary>
    /// Solo-relevant student positions for transmissions that are tower-aviated
    /// (clear-of-runway, holding-short ready, lined-up ready, on-final, etc.).
    /// </summary>
    public static readonly IReadOnlyCollection<string> SoloPositionsTower = ["TWR"];

    /// <summary>
    /// Solo-relevant student positions for transmissions about taxiing on the airport surface that ground or tower
    /// handles (a taxi follow with no route to its traffic).
    /// </summary>
    public static readonly IReadOnlyCollection<string> SoloPositionsGround = ["GND", "TWR"];

    /// <summary>
    /// Solo-relevant student positions for transmissions controllers handle in either
    /// the tower or terminal area (pattern reports, follow operations, going around).
    /// </summary>
    public static readonly IReadOnlyCollection<string> SoloPositionsTowerApproach = ["TWR", "APP"];

    /// <summary>
    /// Queues a solo-training pilot line whose terminal (compact, callsign in the SAY column) and
    /// spoken (phonetic) forms were built independently from the canonical command. Immediate
    /// controller responses stay on the Response channel; this queue represents what the pilot says
    /// when the frequency is available. <paramref name="text"/> is built canonically (never with a
    /// <see cref="PilotPersonality.Varied"/> context), so its own spoken form gives the meter's airtime.
    /// </summary>
    public static void QueueSoloPilotTransmission(AircraftState aircraft, PilotSpeechText text, PilotTransmissionKind kind, bool initialContact) =>
        QueueSoloPilotTransmission(aircraft, text, text, kind, initialContact);

    /// <summary>
    /// Queues a solo-training pilot line spoken as <paramref name="spoken"/>, whose frequency-meter airtime is that of
    /// <paramref name="canonical"/>, the same line built with <see cref="PilotSpeechContext.Canonical"/>: a load-varied
    /// line must not make the frequency read quieter than the canonical one would. <paramref name="initialContact"/> marks
    /// the pilot's first call to the position it is calling, the only kind of call that may butt in behind another
    /// check-in the controller has not answered (<see cref="FrequencyState"/>). A ground or departure first call — ready
    /// to taxi, pushback, clearance request, taxi-in, release request, ready for departure — is false on purpose: the
    /// flag marks airborne and pattern check-ins only.
    /// </summary>
    public static void QueueSoloPilotTransmission(
        AircraftState aircraft,
        PilotSpeechText spoken,
        PilotSpeechText canonical,
        PilotTransmissionKind kind,
        bool initialContact
    ) =>
        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(
                aircraft.Callsign,
                spoken.Terminal,
                spoken.Tts,
                SourceResponse,
                kind,
                FrequencyState.EstimateAirtimeSeconds(canonical.Tts)
            )
            {
                IsInitialContact = initialContact,
            }
        );

    /// <summary>
    /// Queues a solo-training SAY-channel readback for delayed radio transcript and typed
    /// pilot-audio broadcast.
    /// </summary>
    public static void QueueSoloPilotReadback(AircraftState aircraft, string text, string sourceKind)
    {
        string terminal = StripBracketedPrefix(aircraft, text);
        string speech = NormalizeForTts(terminal);
        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(
                aircraft.Callsign,
                terminal,
                speech,
                sourceKind,
                PilotTransmissionKind.SayReadback,
                FrequencyState.EstimateAirtimeSeconds(speech)
            )
        );
    }

    /// <summary>
    /// Queues a solo-training SAY-channel readback with independently-built terminal + spoken forms.
    /// </summary>
    public static void QueueSoloPilotReadback(AircraftState aircraft, PilotSpeechText text, string sourceKind)
    {
        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(
                aircraft.Callsign,
                text.Terminal,
                text.Tts,
                sourceKind,
                PilotTransmissionKind.SayReadback,
                FrequencyState.EstimateAirtimeSeconds(text.Tts)
            )
        );
    }

    /// <summary>
    /// Returns the readback line for an accepted compound command, or <see langword="null"/> if
    /// none of its commands had a verbalization. The caller is responsible for the
    /// <c>SoloTrainingMode</c> gate — this method renders unconditionally.
    ///
    /// Compound conditions (e.g., <c>at SUNOL turn left 270</c>) become a leading "at sunol,"
    /// clause attached to the first command in the block. Subsequent commands in the same block
    /// don't repeat the lead-in. The readback fires once at dispatch acceptance for the entire
    /// compound, not again when a queued block's trigger fires.
    /// </summary>
    public static PilotSpeechText? BuildReadback(CompoundCommand compound, AircraftState aircraft) =>
        BuildReadback(compound, aircraft, PilotSpeechContext.Canonical);

    /// <summary>
    /// Readback of a clearance as the aircraft will actually fly it. When the dispatcher applied a rewritten
    /// TAXI (<see cref="CommandResult.EffectiveCommand"/> — the gate lead-out lane the ground map cannot reach
    /// was dropped), the pilot reads back the effective route and names what it could not take first:
    /// "unable mike four, taxi via mike one, alpha …". A crew never reads back pavement it will not use, and
    /// the hearback check only works if the controller hears the substitution. Any other command, or a
    /// result with no <see cref="CommandResult.EffectiveCommand"/>, reads back exactly as issued.
    /// <para>A handler-built readback (<see cref="CommandResult.PilotReadback"/>, every dispatched <c>PUSH</c> /
    /// <c>PUSHM</c>) replaces the clause of the compound's first command of the type that produced it
    /// (<see cref="HandlerReadback.Command"/>), so the pilot says the goal the handler resolved; any later push reads back
    /// from its parsed form (<see cref="PushReadbackPhrases.FromCommand(PushbackCommand)"/>), and no clause of another type
    /// ever takes it. A bare <c>RES</c> that pre-cleared a taxiway hold short carries one too ("continue taxi"), which
    /// replaces the clause of the compound's first <c>RES</c>. A TAXI's route readback that also reads a crossing a
    /// <c>CROSS</c> or <c>RES</c> beside it pre-cleared drops that command's own clause (<see cref="HandlerReadback.Covers"/>).</para>
    /// </summary>
    /// <param name="compound">The compound the controller issued.</param>
    /// <param name="result">The dispatch's result: its effective command and its handler-built push readback.</param>
    /// <param name="aircraft">The aircraft reading back.</param>
    /// <param name="speechContext">What the spoken wording depends on: personality, load band, scenario seed and whole second.</param>
    public static PilotSpeechText? BuildReadbackAsApplied(
        CompoundCommand compound,
        CommandResult result,
        AircraftState aircraft,
        PilotSpeechContext speechContext
    )
    {
        HandlerReadback? pilotReadback = result.PilotReadback;
        TaxiCommand? issuedTaxi = compound.Blocks.SelectMany(b => b.Commands).OfType<TaxiCommand>().FirstOrDefault();
        if (result.EffectiveCommand is not TaxiCommand effectiveTaxi || issuedTaxi is null)
        {
            return BuildReadback(compound, aircraft, speechContext, pilotReadback);
        }

        var asApplied = new CompoundCommand([
            .. compound.Blocks.Select(b => new ParsedBlock(
                b.Condition,
                [.. b.Commands.Select(c => ReferenceEquals(c, issuedTaxi) ? effectiveTaxi : c)]
            )),
        ])
        {
            SourceText = compound.SourceText,
        };
        PilotSpeechText? readback = BuildReadback(asApplied, aircraft, speechContext, pilotReadback);
        if (readback is null)
        {
            return null;
        }

        var unable = issuedTaxi.Path.Where(t => !t.StartsWith('#') && !effectiveTaxi.Path.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unable.Count == 0)
        {
            return readback;
        }

        string terminalPrefix = "unable " + string.Join(" ", unable) + ", ";
        string ttsPrefix = "unable " + string.Join(", ", unable.Select(PhraseologyVerbalizer.SpellTaxiway)) + ", ";
        return readback with
        {
            Terminal = terminalPrefix + readback.Terminal,
            Tts = ttsPrefix + readback.Tts,
            RpoTerminal = readback.RpoTerminal is null ? null : terminalPrefix + readback.RpoTerminal,
        };
    }

    public static PilotSpeechText? BuildReadback(CompoundCommand compound, AircraftState aircraft, PilotSpeechContext speechContext) =>
        BuildReadback(compound, aircraft, speechContext, null);

    /// <summary>Whether <paramref name="cmd"/> is a command of canonical type <paramref name="type"/>; an unsupported command is none.</summary>
    private static bool IsCommandOfType(ParsedCommand cmd, CanonicalCommandType type) =>
        (cmd is not UnsupportedCommand) && (CommandDescriber.ToCanonicalType(cmd) == type);

    /// <summary>
    /// <see cref="BuildReadback(CompoundCommand, AircraftState, PilotSpeechContext)"/>, with the
    /// clause of the compound's first command of the type that produced <paramref name="handlerReadback"/> taken from it when
    /// the handler built one.
    /// </summary>
    private static PilotSpeechText? BuildReadback(
        CompoundCommand compound,
        AircraftState aircraft,
        PilotSpeechContext speechContext,
        HandlerReadback? handlerReadback
    )
    {
        // Per-block clause lists are joined internally with ", " (parallel commands within
        // a `,`-separated block); blocks themselves are joined with ", then " to mark the
        // `;` (sequential) boundary the controller dictated. Without "then", TTS reads
        // sequential and parallel clauses identically. The terminal (compact), spoken (TTS) and
        // RPO (terminal + diagnostic) bodies are assembled in lock-step from each clause's
        // independently-built forms.
        IReadOnlyList<ParsedCommand> covered = handlerReadback?.Covers ?? [];
        var assembly = new ReadbackAssembly(aircraft, handlerReadback, IsSingleUnconditionedCommand(compound, covered))
        {
            Chained = IsChained(compound, covered),
        };
        PilotSpeechContext clauseContext = assembly.Chained ? ChainedClauseContext(speechContext) : speechContext;
        var blocks = new List<BlockClauses>();
        for (int blockIndex = 0; blockIndex < compound.Blocks.Count; blockIndex++)
        {
            PilotSpeechContext blockContext = SpeedChangeIsNow(compound, blockIndex)
                ? clauseContext
                : clauseContext with
                {
                    CurrentIndicatedAirspeed = null,
                };
            if (BuildBlockClauses(compound.Blocks[blockIndex], blockContext, assembly) is { } built)
            {
                blocks.Add(built);
            }
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        string termBody = string.Join(", then ", blocks.Select(block => block.Terminal));
        string spokenBody = string.Join(", then ", blocks.Select(block => block.Tts));
        // A Quiet pick that offered rows beside the canonical takes its quiet courtesy from them, and a chained readback is
        // never chatty, so either skips the flavour.
        string ttsBody =
            (assembly.OfferedQuietRows || assembly.Chained)
                ? spokenBody
                : ApplyQuietFlavor(aircraft.Callsign, spokenBody, termBody, compound, speechContext);
        string rpoBody = string.Join(", then ", blocks.Select(block => block.Rpo));
        return FrameReadback(aircraft, speechContext, termBody, ttsBody, rpoBody);
    }

    /// <summary>
    /// Whether <paramref name="compound"/> chains things the pilot reads back together: two or more commands that produce a
    /// readback clause (<see cref="HasNoReadbackClause"/>), in one block or several. A single command, conditioned or not, is
    /// one clause.
    /// </summary>
    private static bool IsChained(CompoundCommand compound, IReadOnlyList<ParsedCommand> covered) =>
        compound.Blocks.Sum(block => block.Commands.Count(cmd => !HasNoReadbackClause(cmd) && !IsCovered(cmd, covered))) > 1;

    /// <summary>
    /// Whether <paramref name="cmd"/> is one of the commands the handler's readback already says
    /// (<see cref="HandlerReadback.Covers"/>).
    /// </summary>
    private static bool IsCovered(ParsedCommand cmd, IReadOnlyList<ParsedCommand> covered) => covered.Any(c => ReferenceEquals(c, cmd));

    /// <summary>
    /// Whether <paramref name="cmd"/> is read back with no clause at all (acknowledging a pilot's contact, a say-exit-fix
    /// estimate, which the pilot answers with a report of its own), so it chains nothing.
    /// </summary>
    private static bool HasNoReadbackClause(ParsedCommand cmd) => cmd is AcknowledgePilotContactCommand or SayExitFixEstimateCommand;

    /// <summary>
    /// The context a clause of a chained readback picks its variant in: a chained readback is never chatty, so a quiet
    /// frequency speaks the normal rows; a busy one keeps its terse rows on every clause.
    /// </summary>
    private static PilotSpeechContext ChainedClauseContext(PilotSpeechContext speechContext) =>
        (speechContext.Band == FrequencyLoadBand.Quiet) ? speechContext with { Band = FrequencyLoadBand.Normal } : speechContext;

    /// <summary>
    /// The readback being assembled: the aircraft, the handler's readback until a clause takes it, whether the readback is one
    /// unconditioned command, whether any clause's Quiet pick offered rows beside the canonical, and whether the readback
    /// chains clauses (<see cref="IsChained"/>, or a clause that varies only its last part after a route), and the approach
    /// the last expect-approach before the clause being built names.
    /// </summary>
    private sealed class ReadbackAssembly(AircraftState aircraft, HandlerReadback? handlerReadback, bool wholeReadback)
    {
        public string? ExpectedApproachId { get; set; }

        public AircraftState Aircraft { get; } = aircraft;

        public HandlerReadback? HandlerReadback { get; set; } = handlerReadback;

        /// <summary>
        /// The issued commands (matched by reference) whose clause the handler's readback already says, one entry per command
        /// still to drop.
        /// </summary>
        public List<ParsedCommand> Covered { get; } = [.. handlerReadback?.Covers ?? []];

        public bool WholeReadback { get; } = wholeReadback;

        public bool OfferedQuietRows { get; set; }

        public bool Chained { get; set; }
    }

    /// <summary>One block's clauses, joined with ", ", in the terminal, spoken and RPO forms.</summary>
    private readonly record struct BlockClauses(string Terminal, string Tts, string Rpo);

    /// <summary>
    /// The clauses of <paramref name="block"/>, its condition stated once before the first; null when no command of the block
    /// has a readback.
    /// </summary>
    private static BlockClauses? BuildBlockClauses(ParsedBlock block, PilotSpeechContext blockContext, ReadbackAssembly assembly)
    {
        var termClauses = new List<string>();
        var ttsClauses = new List<string>();
        var rpoClauses = new List<string>();
        string? termLead = FormatConditionTerminal(block.Condition);
        string? ttsLead = FormatCondition(block.Condition);
        string? rpoLead = FormatConditionForRpo(block.Condition);
        foreach (ParsedCommand cmd in block.Commands)
        {
            PilotSpeechText? clause = BuildClause(cmd, blockContext, assembly);
            if (clause is null || string.IsNullOrEmpty(clause.Tts))
            {
                continue;
            }

            clause = WithoutStrayCallsignPlaceholder(cmd, clause, assembly.WholeReadback);
            termClauses.Add(termLead is null ? clause.Terminal : termLead + " " + clause.Terminal);
            ttsClauses.Add(ttsLead is null ? clause.Tts : ttsLead + " " + clause.Tts);
            // The RPO body mirrors the terminal body, clause for clause, but takes each clause's
            // diagnostic form (TerminalForRpo) where it has one — the lead/target callsign in
            // traffic & follow calls, which the solo student must not see.
            rpoClauses.Add(rpoLead is null ? clause.TerminalForRpo : rpoLead + " " + clause.TerminalForRpo);
            termLead = null; // condition is stated once per block
            ttsLead = null;
            rpoLead = null;
        }

        return (ttsClauses.Count == 0)
            ? null
            : new BlockClauses(string.Join(", ", termClauses), string.Join(", ", ttsClauses), string.Join(", ", rpoClauses));
    }

    /// <summary>
    /// One command's clause: the handler's readback for the dispatched command (a later one of its type in the compound is its
    /// own), else the clause the readback builders or the verbalizer speak.
    /// </summary>
    private static PilotSpeechText? BuildClause(ParsedCommand issued, PilotSpeechContext blockContext, ReadbackAssembly assembly)
    {
        ParsedCommand cmd = AsItFires(issued, assembly);
        if (cmd is ExpectApproachCommand expect)
        {
            assembly.ExpectedApproachId = expect.ApproachId;
        }

        if (TakeCovered(issued, assembly))
        {
            return null;
        }

        var trace = new ClauseTrace(assembly.WholeReadback);
        PilotSpeechText? clause;
        if ((assembly.HandlerReadback is { } handler) && IsCommandOfType(cmd, handler.Command))
        {
            assembly.HandlerReadback = null;
            clause =
                ((handler.Variants is { } set) && (handler.Captures is { } captures))
                    ? VaryBuilderClause(
                        cmd,
                        new BuilderClause(handler.Speech, set) { Captures = captures },
                        assembly.Aircraft.Callsign,
                        blockContext,
                        trace
                    )
                    : handler.Speech;
        }
        else
        {
            clause = VerbalizeForReadback(cmd, assembly.Aircraft, blockContext, trace);
        }

        assembly.OfferedQuietRows |= trace.OfferedQuietRows;
        assembly.Chained |= trace.Chained;
        return clause;
    }

    /// <summary>
    /// Whether the handler's readback already says <paramref name="cmd"/>, an issued command listed in
    /// <see cref="HandlerReadback.Covers"/>: it is taken off the list and adds no clause.
    /// </summary>
    private static bool TakeCovered(ParsedCommand cmd, ReadbackAssembly assembly)
    {
        int index = assembly.Covered.FindIndex(c => ReferenceEquals(c, cmd));
        if (index < 0)
        {
            return false;
        }

        assembly.Covered.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// <paramref name="cmd"/> as it will fly: a bare CAPP clears the approach expected when it fires, so an expect-approach
    /// before it in the compound names it ahead of the one expected now; any other command is itself.
    /// </summary>
    private static ParsedCommand AsItFires(ParsedCommand cmd, ReadbackAssembly assembly) =>
        ((cmd is ClearedApproachCommand { ApproachId: null } capp) && (assembly.ExpectedApproachId is { } expected))
            ? capp with
            {
                ApproachId = expected,
            }
            : cmd;

    /// <summary>
    /// What a readback tells the clause it builds, whether the clause is the whole readback, and what the clause reports back,
    /// whether its Quiet pick offered rows beside the canonical and whether it chained a varied part after canonical ones.
    /// </summary>
    private sealed class ClauseTrace(bool wholeReadback)
    {
        public bool WholeReadback { get; } = wholeReadback;

        public bool OfferedQuietRows { get; set; }

        public bool Chained { get; set; }
    }

    /// <summary>
    /// Whether <paramref name="compound"/> is one command with no condition: the only readback in which a variant row may place
    /// the callsign itself (<see cref="PilotVariant.CallsignPlaceholder"/>) or be a bare "wilco".
    /// </summary>
    private static bool IsSingleUnconditionedCommand(CompoundCommand compound, IReadOnlyList<ParsedCommand> covered) =>
        (compound.Blocks is [{ Condition: null } only]) && (only.Commands.Count(cmd => !IsCovered(cmd, covered)) == 1);

    /// <summary>
    /// <paramref name="clause"/> with no callsign placeholder left where it cannot be filled. The verbalizer offers rows that
    /// place the callsign only for a whole readback, so this guards a builder that would carry one into a longer readback:
    /// the placeholder is logged and dropped, never spoken.
    /// </summary>
    private static PilotSpeechText WithoutStrayCallsignPlaceholder(ParsedCommand cmd, PilotSpeechText clause, bool wholeReadback)
    {
        if (wholeReadback || !clause.Tts.Contains(PilotVariant.CallsignPlaceholder, StringComparison.Ordinal))
        {
            return clause;
        }

        Log.LogWarning("Readback clause for {Command} placed the callsign inside a longer readback: \"{Tts}\"", cmd, clause.Tts);
        string tts = clause.Tts.Replace(", " + PilotVariant.CallsignPlaceholder, string.Empty, StringComparison.Ordinal);
        return clause with { Tts = tts.Replace(PilotVariant.CallsignPlaceholder, string.Empty, StringComparison.Ordinal).Trim() };
    }

    /// <summary>
    /// Whether a speed in block <paramref name="blockIndex"/> takes effect now and holds with no "until", so its readback may
    /// say it is slowing, speeding up or maintaining against the current speed: it is the first block (a later block's speed
    /// is assigned after the instructions before it), the block has no condition (a conditioned speed is a later change), and
    /// the next block is not a conditioned speed change or resume, the form <see cref="CommandSchemeParser.ExpandSpeedUntil"/>
    /// writes "SPD x UNTIL y" in.
    /// </summary>
    private static bool SpeedChangeIsNow(CompoundCommand compound, int blockIndex)
    {
        if ((blockIndex != 0) || (compound.Blocks[blockIndex].Condition is not null))
        {
            return false;
        }

        return (compound.Blocks.Count == 1)
            || (compound.Blocks[1] is not { Condition: not null } next)
            || !next.Commands.Any(cmd => cmd is SpeedCommand or ResumeNormalSpeedCommand or ResumePublishedSpeedCommand);
    }

    /// <summary>
    /// Frames a readback into its delivered forms: the terminal SAY message (compact body, no
    /// callsign — the SAY column carries it), the spoken TTS line (body + spelled callsign, abbreviated when
    /// <paramref name="speechContext"/> says so), and the RPO terminal body when a clause carries a diagnostic the solo
    /// student must not see.
    /// </summary>
    private static PilotSpeechText FrameReadback(
        AircraftState aircraft,
        PilotSpeechContext speechContext,
        string terminalBody,
        string ttsBody,
        string rpoBody
    )
    {
        string spoken = SpokenReadbackCallsign(aircraft, speechContext);
        string tts = ttsBody.Contains(PilotVariant.CallsignPlaceholder, StringComparison.Ordinal)
            ? $"{ttsBody.Replace(PilotVariant.CallsignPlaceholder, spoken, StringComparison.Ordinal)}."
            : $"{ttsBody}, {spoken}.";
        return new PilotSpeechText(terminalBody, NormalizeForTts(tts))
        {
            RpoTerminal = string.Equals(rpoBody, terminalBody, StringComparison.Ordinal) ? null : rpoBody,
        };
    }

    /// <summary>
    /// Spoken form of the aircraft's own callsign with the "heavy"/"super" wake-class suffix
    /// appended when applicable. Air carriers and any heavy/super aircraft identify themselves with
    /// the word "super" or "heavy" after the call sign (AIM 4-2-4.a.5, e.g. "United Twenty-Five
    /// Heavy"). Large and Small aircraft get no suffix.
    /// </summary>
    private static string SpokenOwnCallsign(AircraftState aircraft)
    {
        string spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        string? suffix = WakeClassSuffix(aircraft);
        return suffix is null ? spoken : $"{spoken} {suffix}";
    }

    /// <summary>
    /// The callsign a readback closes with: the abbreviated N-number (<see cref="CallsignParser.ShortenedNNumberSpoken"/>)
    /// when <paramref name="speechContext"/> says the pilot abbreviates it and the callsign has a short form, else the full
    /// <see cref="SpokenOwnCallsign"/>; the wake-class suffix follows either (AIM 4-2-4.a.5).
    /// </summary>
    public static string SpokenReadbackCallsign(AircraftState aircraft, PilotSpeechContext speechContext)
    {
        if (!speechContext.ShortenCallsign || (CallsignParser.ShortenedNNumberSpoken(aircraft.Callsign) is not { } shortened))
        {
            return SpokenOwnCallsign(aircraft);
        }

        string? suffix = WakeClassSuffix(aircraft);
        return suffix is null ? shortened : $"{shortened} {suffix}";
    }

    /// <summary>
    /// Terminal (compact, ICAO) form of the aircraft's own callsign with the "heavy"/"super"
    /// wake-class suffix appended when applicable — the terminal counterpart to
    /// <see cref="SpokenOwnCallsign"/>, used where a builder echoes the callsign inline in the
    /// terminal SAY text (the airborne check-in).
    /// </summary>
    private static string TerminalOwnCallsign(AircraftState aircraft)
    {
        string? suffix = WakeClassSuffix(aircraft);
        return suffix is null ? aircraft.Callsign : $"{aircraft.Callsign} {suffix}";
    }

    /// <summary>
    /// The wake-class identification word a pilot appends to its own call sign per AIM 4-2-4.a.5:
    /// "heavy" for the Heavy weight class, "super" for the Super class, or <see langword="null"/>
    /// for Large/Small aircraft that carry no suffix.
    /// </summary>
    private static string? WakeClassSuffix(AircraftState aircraft) =>
        WakeTurbulenceData.WakeClassForType(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType)) switch
        {
            WakeTurbulenceData.WakeClass.Heavy => "heavy",
            WakeTurbulenceData.WakeClass.Super => "super",
            _ => null,
        };

    /// <summary>
    /// Resolves the ATIS information letter a pilot states on initial contact with the primary
    /// field, or <see langword="null"/> to suppress the "with information X" clause entirely when
    /// that field has no ATIS position (AIM 4-2-3.a.3 / 4-1-13). The letter comes from
    /// <see cref="SimScenarioState.AtisLetter"/>; suppression is driven by the ARTCC config.
    /// </summary>
    public static string? ResolvePrimaryFieldAtisLetter(SimScenarioState? scenario)
    {
        if (scenario is null)
        {
            return "A";
        }
        string letter = string.IsNullOrEmpty(scenario.AtisLetter) ? "A" : scenario.AtisLetter;
        if (scenario.ArtccConfig is null || string.IsNullOrEmpty(scenario.PrimaryAirportId))
        {
            return letter;
        }
        return scenario.ArtccConfig.AirportHasAtis(scenario.PrimaryAirportId) ? letter : null;
    }

    /// <summary>
    /// The ", with information X" ATIS clause a pilot appends on initial contact, or empty when
    /// <paramref name="atisLetter"/> is null/empty (the field has no ATIS — clause suppressed). The
    /// leading comma is included so the clause inserts mid-sentence or appends before the period;
    /// the terminal and spoken forms are identical.
    /// </summary>
    private static string AtisInfoClause(string? atisLetter)
    {
        if (string.IsNullOrEmpty(atisLetter))
        {
            return "";
        }
        char letter = char.ToUpperInvariant(atisLetter[0]);
        string word = NatoPhoneticAlphabet.TryGetWord(letter, out string? w) ? w : letter.ToString();
        string display = char.ToUpperInvariant(word[0]) + word[1..];
        return $", with information {display}";
    }

    /// <summary>
    /// Pairs a clause's compact terminal form with its spoken form. Returns <see langword="null"/>
    /// when there is no spoken form (the command has no readback).
    /// </summary>
    private static PilotSpeechText? Dual(string? terminal, string? tts) =>
        string.IsNullOrEmpty(tts) ? null : new PilotSpeechText(string.IsNullOrEmpty(terminal) ? tts : terminal, tts);

    private static PilotSpeechText? VerbalizeForReadback(
        ParsedCommand cmd,
        AircraftState aircraft,
        PilotSpeechContext speechContext,
        ClauseTrace trace
    ) =>
        cmd switch
        {
            _ when VariedBuilderClause(cmd, aircraft) is { } built => VaryBuilderClause(cmd, built, aircraft.Callsign, speechContext, trace),
            LineUpAndWaitCommand luaw => AppendWithoutDelayClause(BuildRunwayInstructionClause(aircraft, "line up and wait"), luaw.WithoutDelay)
                ?? VerbalizeDual(luaw, aircraft.Callsign, speechContext, trace),
            ClearedForTakeoffCommand cto => BuildTakeoffClearanceClause(
                aircraft,
                cto.Immediate ? "cleared for immediate takeoff" : "cleared for takeoff",
                cto.Departure,
                cto.AssignedAltitude,
                new TakeoffRunwayWording(IncludeRunway: true, Intersection: null)
            ),
            ClearedTakeoffPresentCommand ctopp => BuildTakeoffClearanceClause(
                aircraft,
                "cleared for takeoff, present position",
                ctopp.Departure,
                ctopp.AssignedAltitude,
                new TakeoffRunwayWording(IncludeRunway: false, Intersection: null)
            ),
            _ when HasNoReadbackClause(cmd) => null,
            HoldShortCommand hsc when IsUnmakeableHoldShort(aircraft, hsc) => BuildUnableHoldShortClause(hsc),
            ClearedToLandCommand cland => BuildClearedToLandClause(aircraft, cland) ?? VerbalizeDual(cland, aircraft.Callsign, speechContext, trace),
            LandAndHoldShortCommand lahso => BuildLandAndHoldShortClause(aircraft, lahso),
            TouchAndGoCommand tg => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared touch and go", explicitRunwayId: tg.RunwayId)
                    ?? VerbalizeDual(tg, aircraft.Callsign, speechContext, trace),
                tg.TrafficPattern,
                tg.PatternRunwayId
            ),
            StopAndGoCommand sg => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared stop and go") ?? VerbalizeDual(sg, aircraft.Callsign, speechContext, trace),
                sg.TrafficPattern,
                sg.PatternRunwayId
            ),
            LowApproachCommand la => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared low approach") ?? VerbalizeDual(la, aircraft.Callsign, speechContext, trace),
                la.TrafficPattern,
                la.PatternRunwayId
            ),
            ClearedForOptionCommand option => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared for the option") ?? VerbalizeDual(option, aircraft.Callsign, speechContext, trace),
                option.TrafficPattern,
                option.PatternRunwayId
            ),
            ExtendPatternCommand ext => BuildExtendPatternClause(aircraft, ext, trace),
            ReportCommand report => BuildReportWilcoClause(report),
            SpeedMaxCommand => BuildMaximumForwardSpeed(),
            ClearedIntoMilitaryRouteCommand cmtr => BuildClearedIntoMilitaryRouteClause(cmtr),
            MaintainMilitaryRouteAltitudesCommand => BuildMaintainRouteAltitudesClause(aircraft),
            ClearedOutOfMilitaryRouteCommand xmtr => BuildClearedOutOfMilitaryRouteClause(aircraft, xmtr),
            ClearedToConductRefuelingCommand car => BuildClearedToConductRefuelingClause(car),
            PushbackCommand push => PushReadbackPhrases.FromCommand(push).ToSpeech(),
            PushbackMultiCommand move => PushReadbackPhrases.FromCommand(move).ToSpeech(),
            FollowGroundCommand follow when GroundCommandHandler.RunwayHoldWithArmedFollow(aircraft) is { } armedHold => BuildArmedFollowClause(
                follow,
                armedHold.RunwayEndFacing(aircraft)
            ),
            FollowGroundCommand follow => BuildFollowGroundClause(follow),
            GiveWayCommand giveWay => BuildGiveWayClause(giveWay),
            HoldPositionCommand when HeldOnRunwayEnd(aircraft) is { } runway => BuildHoldingPositionOnRunwayClause(runway),
            ClearedApproachCommand
            or JoinApproachCommand
            or ClearedApproachStraightInCommand
            or JoinApproachStraightInCommand
            or PositionTurnAltitudeClearanceCommand => BuildApproachClause(cmd, aircraft)
                ?? VerbalizeDual(cmd, aircraft.Callsign, speechContext, trace),
            _ => VerbalizeDual(cmd, aircraft.Callsign, speechContext, trace),
        };

    /// <summary>
    /// An approach clearance with no handler readback (a later block, a condition), the approach named from the command's id:
    /// a CAPP or PTAC whose id names nothing reads "cleared approach"; a PTAC's altitude, when it gives one, is climb or descend
    /// against the present altitude. Null for a join or straight-in whose id names no approach type, or any other command.
    /// </summary>
    private static PilotSpeechText? BuildApproachClause(ParsedCommand cmd, AircraftState aircraft) =>
        cmd switch
        {
            ClearedApproachCommand capp => ApproachCommandHandler.ClearedApproachReadback(
                capp,
                ApproachReadbackName.ForShorthand(ClearedApproachId(capp, aircraft))
            ),
            JoinApproachCommand japp => BuildJoinApproachClause(japp, japp.ApproachId),
            ClearedApproachStraightInCommand cappsi => BuildJoinApproachClause(cappsi, cappsi.ApproachId),
            JoinApproachStraightInCommand jappsi => BuildJoinApproachClause(jappsi, jappsi.ApproachId),
            PositionTurnAltitudeClearanceCommand ptac => BuildPtacClause(ptac, aircraft),
            _ => null,
        };

    private static PilotSpeechText? BuildJoinApproachClause(ParsedCommand cmd, string approachId) =>
        ApproachReadbackName.ForShorthand(approachId) is { } approach
            ? ApproachCommandHandler.JoinApproachReadback(CommandDescriber.ToCanonicalType(cmd), approach)
            : null;

    /// <summary>
    /// The runway end a HOLD given now stops the aircraft on, from the crossing it is in or the taxi in the stretch before a
    /// crossing starts; null when the HOLD stops it clear of the runway.
    /// </summary>
    private static string? HeldOnRunwayEnd(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            CrossingRunwayPhase crossing => crossing.HeldOnRunwayEnd(aircraft),
            TaxiingPhase taxi => taxi.HeldOnRunwayEnd(aircraft),
            _ => null,
        };

    /// <summary>
    /// A HOLD taken mid-crossing with part of the aircraft past the near holding-position marking stops it on the runway, so
    /// the readback says where it is holding — "holding position on runway three three" — rather than the bare "hold
    /// position" (AIM 4-3-21.a: the HOLD is the approval to stop on the runway).
    /// </summary>
    private static PilotSpeechText BuildHoldingPositionOnRunwayClause(string runway) =>
        new(
            $"holding position on runway {PhraseologyVerbalizer.CompactRunway(runway)}",
            $"holding position on runway {PhraseologyVerbalizer.SpellRunway(runway)}"
        );

    /// <summary>
    /// A FOLLOWG away from a runway bar — from parking, mid-taxi or any hold that is not a runway bar —
    /// drops or resumes the route to trail the named aircraft, so the readback is the bare taxi element
    /// "follow the traffic" (§3-7-2.a "FOLLOW (traffic) (restrictions as necessary)"). Like every follow call the pilot
    /// identifies the leader by position, never by callsign (docs/pilot-phraseology.md), so the target
    /// survives only in the RPO form.
    /// </summary>
    private static PilotSpeechText BuildFollowGroundClause(FollowGroundCommand follow) =>
        new("follow the traffic", "follow the traffic") { RpoTerminal = $"follow {follow.TargetCallsign}" };

    /// <summary>
    /// An airborne FOLLOW drops or resumes the route to trail the named aircraft, so the readback is the bare
    /// element "follow the traffic" (7110.65 §7-6-7.a "FOLLOW (description)"; also §3-8-1, tower sequencing; the
    /// same wording the ground form uses). FOLLOWF (force) parses to this same command, so it reads back alike.
    /// Like every follow call the pilot identifies the leader by position, never by callsign
    /// (docs/pilot-phraseology.md), so the target survives only in the RPO form; a bare FOLLOW names no traffic
    /// and keeps "the traffic" in every form.
    /// </summary>
    private static PilotSpeechText BuildFollowAirborneClause(FollowCommand follow) =>
        new("follow the traffic", "follow the traffic")
        {
            RpoTerminal = follow.TargetCallsign is { } target ? $"follow {target}" : "follow the traffic",
        };

    /// <summary>
    /// GIVEWAY is §3-7-2.a's "BEHIND (traffic)": yield to the traffic and trail it on the pilot's own
    /// route. As with the follow calls, the spoken and solo-terminal forms say "the traffic"; the
    /// target callsign is the RPO diagnostic only.
    /// </summary>
    private static PilotSpeechText BuildGiveWayClause(GiveWayCommand giveWay) =>
        new("behind the traffic", "behind the traffic") { RpoTerminal = $"behind {giveWay.TargetCallsign}" };

    /// <summary>
    /// A FOLLOWG taken at a runway bar arms the follow and leaves the aircraft holding short, so the readback states
    /// the hold-short with it — "follow the traffic, hold short of runway one right": "ATC is required to obtain a
    /// readback from the pilot of all runway hold short instructions" (AIM 4-3-18), and the crew says the follow
    /// does not take it across. The leader is "the traffic" in the spoken and solo forms, like every follow call;
    /// its callsign is the RPO diagnostic.
    /// </summary>
    private static PilotSpeechText BuildArmedFollowClause(FollowGroundCommand follow, string runway)
    {
        string terminalHoldShort = $"hold short of runway {PhraseologyVerbalizer.CompactRunway(runway)}";
        return new PilotSpeechText(
            $"follow the traffic, {terminalHoldShort}",
            $"follow the traffic, hold short of runway {PhraseologyVerbalizer.SpellRunway(runway)}"
        )
        {
            RpoTerminal = $"follow {follow.TargetCallsign}, {terminalHoldShort}",
        };
    }

    /// <summary>
    /// True when the bar this <c>HS</c> armed is one the aircraft cannot make. The command handler flags it at
    /// dispatch (<see cref="HoldShortPoint.Unable"/>) from the aircraft's speed and the distance left to the
    /// painted line, so the readback built in the same breath reports what the aircraft will actually do
    /// instead of reading back a hold it will overrun.
    ///
    /// <para>The bar looked at is the first one ahead on the route matching the target, not any bar that
    /// matches: a route crossing the same taxiway twice carries two, and the one further on — which the
    /// aircraft has all the room in the world for — must not answer for the one just armed.</para>
    /// </summary>
    private static bool IsUnmakeableHoldShort(AircraftState aircraft, HoldShortCommand cmd) => ArmedHoldShort(aircraft, cmd.Target)?.Unable == true;

    /// <summary>
    /// The bar a hold-short of <paramref name="target"/> armed on the aircraft's assigned route: the first uncleared one
    /// ahead matching the target (see <see cref="IsUnmakeableHoldShort"/>), or null.
    /// </summary>
    private static HoldShortPoint? ArmedHoldShort(AircraftState aircraft, HoldShortTarget target)
    {
        if (aircraft.Ground.AssignedTaxiRoute is not { } route)
        {
            return null;
        }

        HoldShortPoint? armed = null;
        int armedIndex = int.MaxValue;
        foreach (HoldShortPoint bar in route.HoldShortPoints)
        {
            if (bar.IsCleared || !HoldShortAnnotator.TargetMatches(bar.TargetName, target.MatchKey))
            {
                continue;
            }

            int index = SegmentIndexAhead(route, bar.NodeId);
            if ((armed is null) || (index < armedIndex))
            {
                armed = bar;
                armedIndex = index;
            }
        }

        return armed;
    }

    /// <summary>
    /// Index of the route segment that ends at <paramref name="nodeId"/>, counting from the segment in
    /// progress, or <see cref="int.MaxValue"/> when no segment ahead ends there — which orders a bar the
    /// route has already passed, or one on no segment at all, behind every bar still to come.
    /// </summary>
    /// <param name="route">The route being taxied.</param>
    /// <param name="nodeId">The node the bar protects.</param>
    /// <returns>The segment index, or <see cref="int.MaxValue"/>.</returns>
    private static int SegmentIndexAhead(TaxiRoute route, int nodeId)
    {
        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            if (route.Segments[i].ToNodeId == nodeId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// "unable to hold short of tango, stopping" — the two delivered forms of the refusal P/CG "UNABLE"
    /// names ("inability to comply with a specific instruction, request, or clearance"), which AIM 4-4-7.c
    /// leaves to the pilot: "it is the responsibility of the pilot to accept or refuse the clearance issued".
    /// </summary>
    /// <param name="cmd">The hold-short being refused.</param>
    /// <returns>The terminal and spoken forms of the answer.</returns>
    private static PilotSpeechText BuildUnableHoldShortClause(HoldShortCommand cmd) =>
        new(
            PhraseologyVerbalizer.RenderUnableHoldShort(cmd.Target, spoken: false),
            PhraseologyVerbalizer.RenderUnableHoldShort(cmd.Target, spoken: true)
        );

    /// <summary>
    /// FAA JO 7110.65 §9-2-6.a "CLEARED INTO IR (designator)", with the published-altitudes,
    /// assigned-altitude, or at-or-below clause.
    ///
    /// A builder rather than a <see cref="PhraseologyRule"/> for two reasons. The three forms differ
    /// only by which argument is present, and rule selection happens per canonical type without
    /// seeing the arguments — the longest pattern would win and an assigned altitude would be read
    /// back as "maintain route altitudes". And the comma before the altitude clause cannot be
    /// expressed in a pattern, whose tokens are joined with spaces.
    /// </summary>
    private static PilotSpeechText BuildClearedIntoMilitaryRouteClause(ClearedIntoMilitaryRouteCommand cmd)
    {
        string terminalRoute = cmd.Designator.ToUpperInvariant();
        string spokenRoute = PhraseologyVerbalizer.SpellMilitaryRoute(cmd.Designator);

        if (cmd.AltitudeFt is not { } altitude)
        {
            return new PilotSpeechText(
                $"cleared into {terminalRoute}, maintain {terminalRoute} altitudes",
                $"cleared into {spokenRoute}, maintain {spokenRoute} altitudes"
            );
        }

        // Worded exactly as the existing at-or-below altitude rendering so the two never diverge.
        string prefix = cmd.AtOrBelow ? "maintain at or below" : "maintain";
        return new PilotSpeechText(
            $"cleared into {terminalRoute}, {prefix} {PhraseologyVerbalizer.CompactAltitude(altitude)}",
            $"cleared into {spokenRoute}, {prefix} {PhraseologyVerbalizer.AltitudeWords(altitude)}"
        );
    }

    /// <summary>
    /// §9-2-13 "CLEARED TO CONDUCT REFUELING ALONG (number) TRACK" and its "MAINTAIN BLOCK
    /// (altitude) THROUGH (altitude)" clause. A builder for the same punctuation reason as
    /// <see cref="BuildClearedIntoMilitaryRouteClause"/>.
    /// </summary>
    private static PilotSpeechText BuildClearedToConductRefuelingClause(ClearedToConductRefuelingCommand cmd)
    {
        string terminal = $"cleared to conduct refueling along {cmd.Designator.ToUpperInvariant()} track";
        string spoken = $"cleared to conduct refueling along {PhraseologyVerbalizer.SpellRefuelingTrack(cmd.Designator)} track";

        if (cmd.BlockFloorFt is not { } floor || cmd.BlockCeilingFt is not { } ceiling)
        {
            return new PilotSpeechText(terminal, spoken);
        }

        return new PilotSpeechText(
            $"{terminal}, maintain block {PhraseologyVerbalizer.CompactAltitude(floor)} through {PhraseologyVerbalizer.CompactAltitude(ceiling)}",
            $"{spoken}, maintain block {PhraseologyVerbalizer.AltitudeWords(floor)} through {PhraseologyVerbalizer.AltitudeWords(ceiling)}"
        );
    }

    /// <summary>
    /// FAA JO 7110.65 §9-2-6.a "MAINTAIN IR (designator) ALTITUDES". The command carries no
    /// designator — it comes from the aircraft's clearance — and a <see cref="PhraseologyRule"/>
    /// cannot reach aircraft state, so this is a builder rather than a rule.
    /// </summary>
    private static PilotSpeechText? BuildMaintainRouteAltitudesClause(AircraftState aircraft)
    {
        if (aircraft.MilitaryRoute.Designator is not { Length: > 0 } designator)
        {
            return null;
        }

        return new PilotSpeechText($"maintain {designator} altitudes", $"maintain {PhraseologyVerbalizer.SpellMilitaryRoute(designator)} altitudes");
    }

    /// <summary>
    /// §9-2-6.b "CLEARED TO (destination) FROM IR (designator/exit fix) VIA (route). MAINTAIN
    /// (altitude)." The designator again comes from state rather than from the command.
    /// </summary>
    private static PilotSpeechText? BuildClearedOutOfMilitaryRouteClause(AircraftState aircraft, ClearedOutOfMilitaryRouteCommand cmd)
    {
        if (aircraft.MilitaryRoute.Designator is not { Length: > 0 } designator)
        {
            return null;
        }

        string terminalVia = cmd.Route is { Length: > 0 } route ? $" via {route}" : "";
        string spokenVia = cmd.Route is { Length: > 0 } spoken ? $" via {PhraseologyVerbalizer.SpellRouteString(spoken)}" : "";
        string terminalAlt = cmd.AltitudeFt is { } feet ? $", maintain {PhraseologyVerbalizer.CompactAltitude(feet)}" : "";
        string spokenAlt = cmd.AltitudeFt is { } spokenFeet ? $", maintain {PhraseologyVerbalizer.AltitudeWords(spokenFeet)}" : "";

        return new PilotSpeechText(
            $"cleared to {PhraseologyVerbalizer.FixDisplayTextUpper(cmd.Destination)} from {designator}{terminalVia}{terminalAlt}",
            $"cleared to {PhraseologyVerbalizer.SpellFix(cmd.Destination)} "
                + $"from {PhraseologyVerbalizer.SpellMilitaryRoute(designator)}{spokenVia}{spokenAlt}"
        );
    }

    /// <summary>
    /// Readback of SMAX, 7110.65 5-7-2 "MAINTAIN MAXIMUM FORWARD SPEED": the pilot reads back the instruction's speed,
    /// which has no number.
    /// </summary>
    private static PilotSpeechText? BuildMaximumForwardSpeed() => Dual("maximum forward speed", "maximum forward speed");

    /// <summary>
    /// Pilot wilco for a controller <c>REPORT</c> request: acknowledges now ("will report turning
    /// base"); the actual position report is voiced later when the event occurs. A cancel
    /// (<see cref="ReportTrigger.Cancel"/>) needs no readback.
    /// </summary>
    private static PilotSpeechText? BuildReportWilcoClause(ReportCommand report) =>
        report.Trigger switch
        {
            ReportTrigger.Crosswind => Dual("will report turning crosswind", "will report turning crosswind"),
            ReportTrigger.Downwind => Dual("will report turning downwind", "will report turning downwind"),
            ReportTrigger.Base => Dual("will report turning base", "will report turning base"),
            ReportTrigger.Final => Dual("will report turning final", "will report turning final"),
            ReportTrigger.Midfield => Dual("will report midfield downwind", "will report midfield downwind"),
            ReportTrigger.MileFinal => Dual(
                $"will report {report.DistanceNm}-mile final",
                $"will report {AtcNumberParser.CardinalWord(report.DistanceNm ?? 0)} mile final"
            ),
            ReportTrigger.AtFix when report.FixName is { } fixName => Dual(
                $"will report passing {PhraseologyVerbalizer.FixDisplayText(fixName)}",
                $"will report passing {PhraseologyVerbalizer.SpellFix(fixName)}"
            ),
            _ => null,
        };

    /// <summary>
    /// Default rule-driven clause for commands without a dedicated readback builder: the compact
    /// terminal form and the spoken form, both produced from the canonical command by the verbalizer.
    /// Only the spoken form varies with the speech context and the callsign that seeds its variant pick; the terminal form is
    /// always the canonical text. The spoken pick knows from <paramref name="trace"/> whether the clause is the whole readback,
    /// and reports into it whether its Quiet pick offered rows beside the canonical.
    /// </summary>
    private static PilotSpeechText? VerbalizeDual(ParsedCommand cmd, string callsign, PilotSpeechContext speechContext, ClauseTrace trace)
    {
        ClauseVerbalization spoken = PhraseologyVerbalizer.VerbalizeClause(cmd, callsign, speechContext, trace.WholeReadback);
        trace.OfferedQuietRows |= spoken.OfferedQuietRows;
        return Dual(PhraseologyVerbalizer.VerbalizeTerminal(cmd), spoken.Text);
    }

    /// <summary>
    /// Builds the EXT readback dynamically from the aircraft's current pattern phase
    /// (because bare `EXT` is parsed as <c>ExtendPatternCommand(Leg: null)</c> — the
    /// resolved leg only becomes known when <see cref="PatternCommandHandler"/> applies
    /// it). Includes the runway when one is assigned so the readback matches the
    /// pattern-entry conventions used by ERD/ELD/MLT/MRT.
    /// </summary>
    private static PilotSpeechText? BuildExtendPatternClause(AircraftState aircraft, ExtendPatternCommand ext, ClauseTrace trace)
    {
        PatternEntryLeg? leg = ext.Leg ?? CurrentPatternLeg(aircraft.Phases?.CurrentPhase);
        if (leg is not { } resolved)
        {
            return VerbalizeDual(ext, aircraft.Callsign, PilotSpeechContext.Canonical, trace);
        }

        string? legWord = resolved switch
        {
            PatternEntryLeg.Upwind => "upwind",
            PatternEntryLeg.Crosswind => "crosswind",
            PatternEntryLeg.Downwind => "downwind",
            _ => null,
        };
        if (legWord is null)
        {
            return VerbalizeDual(ext, aircraft.Callsign, PilotSpeechContext.Canonical, trace);
        }

        string? runway = aircraft.Procedure.DestinationRunway;
        if (string.IsNullOrEmpty(runway))
        {
            return new PilotSpeechText($"extend {legWord}", $"extend {legWord}");
        }
        return new PilotSpeechText(
            $"extend {legWord} runway {PhraseologyVerbalizer.CompactRunway(runway)}",
            $"extend {legWord} runway {PhraseologyVerbalizer.SpellRunway(runway)}"
        );
    }

    private static PatternEntryLeg? CurrentPatternLeg(Phase? phase) =>
        phase switch
        {
            UpwindPhase => PatternEntryLeg.Upwind,
            CrosswindPhase => PatternEntryLeg.Crosswind,
            DownwindPhase => PatternEntryLeg.Downwind,
            BasePhase => PatternEntryLeg.Base,
            _ => null,
        };

    /// <summary>
    /// Flavours the spoken body on a quiet frequency, rarely: the pick hashes the canonical terminal body, so a pilot's
    /// flavour does not change with the band's wording. "alright, " never leads a runway clearance, a safety-critical verb,
    /// a turn or a runway spoken with its side, and ", thanks" never follows a go-around, a cancellation, a hold position or
    /// a LAHSO clearance (F8, F12);
    /// a pick that lands on an excluded flavour leaves the readback unflavoured. A readback with a clause whose Quiet pick
    /// offered rows beside the canonical (<see cref="ClauseVerbalization.OfferedQuietRows"/>) is not flavoured:
    /// its rows carry the courtesy.
    /// </summary>
    public static string ApplyQuietFlavor(
        string callsign,
        string body,
        string canonicalBody,
        CompoundCommand compound,
        PilotSpeechContext speechContext
    )
    {
        if ((speechContext.Personality != PilotPersonality.Varied) || (speechContext.Band != FrequencyLoadBand.Quiet))
        {
            return body;
        }

        int bucket = VariantPick.Index(callsign, canonicalBody, speechContext.ElapsedSecond, speechContext.RngSeed, 100);
        string[] words = [.. Regex.Split(canonicalBody.ToLowerInvariant(), "[^a-z0-9]+").Where(word => word.Length > 0)];
        return bucket switch
        {
            < 5 when !ContainsAny(words, NoAlrightTerms) && !words.Any(RunwayWithSide().IsMatch) => $"alright, {body}",
            >= 5 and < 10 when !ContainsAny(words, NoThanksTerms) && !HoldsLandAndHoldShort(compound) => $"{body}, thanks",
            _ => body,
        };
    }

    /// <summary>The word sequences of a canonical body that "alright, " never leads (F8, F12); public for the row guard test.</summary>
    public static readonly string[][] NoAlrightTerms =
    [
        ["left"],
        ["right"],
        ["cleared"],
        ["cancel"],
        ["cross"],
        ["follow"],
        ["behind"],
        ["bravo"],
        ["option"],
        ["line", "up", "and", "wait"],
        ["touch", "and", "go"],
        ["stop", "and", "go"],
        ["low", "approach"],
        ["go", "around"],
        ["hold", "position"],
        ["hold", "short"],
        ["holding", "position"],
        ["holding", "short"],
        ["crossing"],
    ];

    /// <summary>The word sequences of a canonical body that ", thanks" never follows (F8, F12); public for the row guard test.</summary>
    public static readonly string[][] NoThanksTerms =
    [
        ["go", "around"],
        ["cancel"],
        ["hold", "position"],
        ["holding", "position"],
    ];

    /// <summary>A Terminal runway token carrying its side, such as <c>28r</c> or <c>1l</c>.</summary>
    [GeneratedRegex(@"^\d{1,2}[lrc]$")]
    private static partial Regex RunwayWithSide();

    private static bool HoldsLandAndHoldShort(CompoundCommand compound) =>
        compound.Blocks.Any(block => block.Commands.Any(cmd => cmd is LandAndHoldShortCommand));

    /// <summary>Whether <paramref name="words"/> holds any of <paramref name="terms"/> as consecutive whole words.</summary>
    private static bool ContainsAny(string[] words, string[][] terms) =>
        terms.Any(term =>
            Enumerable.Range(0, Math.Max(0, words.Length - term.Length + 1)).Any(start => term.SequenceEqual(words.Skip(start).Take(term.Length)))
        );

    /// <summary>
    /// EXP has four readbacks and a <see cref="PhraseologyRule"/> can express none of them: rule
    /// selection happens per canonical type without seeing the argument or the aircraft, so the
    /// first-declared "expedite climb …" template won every time — a descending aircraft read back
    /// a climb, and so did a taxiing one.
    ///
    /// FAA JO 7110.65 §2-1-5.b makes <c>EXP (altitude)</c> the same clearance as CLIMB/DESCEND AND
    /// MAINTAIN with an expedite qualifier appended (it is also why a plain altitude amendment
    /// cancels the expedite), so the readback mirrors the CM/DM wording and carries the altitude
    /// per AIM §4-4-7.b. The two ground forms come from §3-7-2.j. Branching here matches
    /// <see cref="FlightCommandHandler.ApplyExpedite"/>'s own branch order.
    /// </summary>
    private static BuilderClause BuildExpediteClause(AircraftState aircraft, ExpediteCommand cmd)
    {
        if (cmd.Altitude is { } assigned)
        {
            bool climb = assigned > aircraft.Altitude;
            string verb = climb ? "climb" : "descend";
            string noun = climb ? "climb" : "descent";
            return new BuilderClause(
                new PilotSpeechText(
                    $"{verb} and maintain {PhraseologyVerbalizer.CompactAltitude(assigned)}, expedite {noun}",
                    $"{verb} and maintain {PhraseologyVerbalizer.AltitudeWords(assigned)}, expedite {noun}"
                ),
                climb ? PhraseologyRules.ExpediteClimbToAltitude : PhraseologyRules.ExpediteDescentToAltitude
            );
        }

        if (GroundCommandHandler.HasLandingOrExitPhase(aircraft, PhaseStatus.Active))
        {
            return new BuilderClause(new PilotSpeechText("exit without delay", "exit without delay"), PhraseologyRules.ExpediteExit);
        }

        if (aircraft.IsOnGround)
        {
            return new BuilderClause(new PilotSpeechText("taxi without delay", "taxi without delay"), PhraseologyRules.ExpediteTaxi);
        }

        // Bare EXP is rejected outright when there is no vertical goal, so by the time a readback
        // is built the goal is present and its side tells us which word the pilot uses.
        bool climbing = FlightPhysics.ResolveAltitudeGoal(aircraft) is { } goal && goal > aircraft.Altitude;
        string direction = climbing ? "expedite climb" : "expedite descent";
        return new BuilderClause(
            new PilotSpeechText(direction, direction),
            climbing ? PhraseologyRules.ExpediteClimb : PhraseologyRules.ExpediteDescent
        );
    }

    private static BuilderClause BuildAltitudeRestrictionClause(AircraftState aircraft, ClimbMaintainCommand cmd)
    {
        bool vfr = aircraft.FlightPlan.IsVfr;
        bool atOrAbove = cmd.Modifier == AltitudeAssignmentModifier.AtOrAbove;
        string vfrWord = vfr ? "VFR " : "";
        string restriction = atOrAbove ? "at or above" : "at or below";
        BuilderVariantSet variants = (vfr, atOrAbove) switch
        {
            (true, true) => PhraseologyRules.RestrictionVfrAtOrAbove,
            (true, false) => PhraseologyRules.RestrictionVfrAtOrBelow,
            (false, true) => PhraseologyRules.RestrictionAtOrAbove,
            (false, false) => PhraseologyRules.RestrictionAtOrBelow,
        };
        return new BuilderClause(
            new PilotSpeechText(
                $"maintain {vfrWord}{restriction} {PhraseologyVerbalizer.CompactAltitude(cmd.Altitude)}",
                $"maintain {vfrWord}{restriction} {PhraseologyVerbalizer.AltitudeWords(cmd.Altitude)}"
            ),
            variants
        );
    }

    /// <summary>A clause a readback builder renders directly: its canonical text and the variant set of its shape.</summary>
    internal readonly record struct BuilderClause(PilotSpeechText Canonical, BuilderVariantSet Variants)
    {
        /// <summary>
        /// The spoken values of the set's captures when the builder supplies them (an approach clearance names the approach it
        /// resolved); the set then varies only the canonical's final clause, the one its pattern renders. Null when the rows
        /// are filled from the command's own captures and vary the whole clause.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Captures { get; init; }
    }

    /// <summary>
    /// The clause the readback builds for <paramref name="cmd"/> from a variant set (an altitude restriction, an expedite, an
    /// approach clearance or join, a PTAC, a visual clearance naming its runway, an airborne follow), chosen by
    /// <paramref name="aircraft"/>; null for any other command (an airport-level visual among them), and for an approach
    /// clearance read back canonical in every band (a circling approach, a bare CAPP with no expected approach, a PTAC on the
    /// present heading, with no altitude, or whose id names no approach type and runway). The readback
    /// and <see cref="PhraseologyVerbalizer.SpokenCandidates"/> both dispatch through it.
    /// </summary>
    internal static BuilderClause? VariedBuilderClause(ParsedCommand cmd, AircraftState aircraft) =>
        cmd switch
        {
            ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrAbove or AltitudeAssignmentModifier.AtOrBelow } cm =>
                BuildAltitudeRestrictionClause(aircraft, cm),
            ExpediteCommand exp => BuildExpediteClause(aircraft, exp),
            ClearedApproachCommand capp => BuildVariedApproachClearanceClause(capp, aircraft),
            JoinApproachCommand japp => BuildVariedJoinApproachClause(japp, japp.ApproachId),
            ClearedApproachStraightInCommand cappsi => BuildVariedJoinApproachClause(cappsi, cappsi.ApproachId),
            JoinApproachStraightInCommand jappsi => BuildVariedJoinApproachClause(jappsi, jappsi.ApproachId),
            ClearedVisualApproachCommand { RunwayId: { } runwayId } cva => BuildVariedRunwayVisualClause(cva, runwayId),
            FollowCommand follow => new BuilderClause(BuildFollowAirborneClause(follow), PhraseologyRules.FollowTrafficAirborne),
            PositionTurnAltitudeClearanceCommand ptac => BuildVariedPtacClause(ptac, aircraft),
            _ => null,
        };

    /// <summary>
    /// A PTAC's readback with the variant set of its heading kind and vertical verb, the altitude climb or descend against the
    /// present altitude; null when it is read back canonical in every band (the present heading, no altitude, an approach id
    /// naming no type and runway).
    /// </summary>
    private static BuilderClause? BuildVariedPtacClause(PositionTurnAltitudeClearanceCommand ptac, AircraftState aircraft)
    {
        if (
            ApproachCommandHandler.PtacVariants(
                ptac.Direction,
                ptac.MagneticHeading,
                LaterPtacAltitude(ptac, aircraft),
                ApproachReadbackName.SpokenParts(ptac.ApproachId)
            )
            is not { } variants
        )
        {
            return null;
        }

        return new BuilderClause(BuildPtacClause(ptac, aircraft), variants.Set) { Captures = variants.Captures };
    }

    /// <summary>
    /// A PTAC's canonical readback with no handler readback, the approach named from the command's id ("cleared approach" when
    /// it names nothing) and the altitude climb or descend against the present altitude.
    /// </summary>
    private static PilotSpeechText BuildPtacClause(PositionTurnAltitudeClearanceCommand ptac, AircraftState aircraft) =>
        ApproachCommandHandler.PtacReadback(
            ptac.MagneticHeading,
            ptac.Direction,
            LaterPtacAltitude(ptac, aircraft),
            ApproachReadbackName.ForShorthand(ptac.ApproachId)
        );

    /// <summary>The altitude a PTAC with no handler readback says, against the present altitude; null when it gives none.</summary>
    private static ApproachCommandHandler.PtacAltitude? LaterPtacAltitude(PositionTurnAltitudeClearanceCommand ptac, AircraftState aircraft) =>
        ptac.Altitude is { } feet ? new ApproachCommandHandler.PtacAltitude(feet, aircraft.Altitude) : null;

    /// <summary>A visual clearance to a runway the command names, with its variant set and the captures of its tail.</summary>
    private static BuilderClause BuildVariedRunwayVisualClause(ClearedVisualApproachCommand cva, string runwayId)
    {
        (PilotSpeechText readback, IReadOnlyDictionary<string, string> captures) = ApproachCommandHandler.RunwayVisualReadback(cva, runwayId);
        return new BuilderClause(readback, PhraseologyRules.VisualApproachClearance) { Captures = captures };
    }

    /// <summary>
    /// The approach a CAPP with no handler readback names: its own id, else, for a bare CAPP, the expected approach it clears
    /// when it fires.
    /// </summary>
    private static string? ClearedApproachId(ClearedApproachCommand capp, AircraftState aircraft) => capp.ApproachId ?? aircraft.Approach.Expected;

    /// <summary>
    /// A CAPP's readback with the variant set of its final clause: the typed approach's set, or the "cleared approach" set when
    /// the id names a runway but no type, or, for a bare CAPP clearing an expected visual, the visual clearance to its runway;
    /// null when it is read back canonical in every band.
    /// </summary>
    private static BuilderClause? BuildVariedApproachClearanceClause(ClearedApproachCommand capp, AircraftState aircraft)
    {
        string? approachId = ClearedApproachId(capp, aircraft);
        if (
            (capp is { ApproachId: null, AtFix: null, DctFix: null, CrossFixAltitude: null })
            && ApproachIds.TryParseVisual(aircraft.Approach.Expected, out string? visualRunway)
        )
        {
            return BuildVariedRunwayVisualClause(new ClearedVisualApproachCommand(visualRunway, null, null, null, false), visualRunway);
        }

        (BuilderVariantSet Set, IReadOnlyDictionary<string, string> Captures)? variants = ApproachReadbackName.SpokenParts(approachId) is { } parts
            ? ApproachCommandHandler.ClauseVariants(CommandDescriber.ToCanonicalType(capp), parts.Approach, parts.Runway)
            : null;
        if ((variants is null) && ApproachReadbackName.NamesRunwayButNoType(approachId))
        {
            variants = ApproachCommandHandler.NamingNoTypeVariants;
        }

        if (variants is not { } chosen)
        {
            return null;
        }

        PilotSpeechText canonical = ApproachCommandHandler.ClearedApproachReadback(capp, ApproachReadbackName.ForShorthand(approachId));
        return new BuilderClause(canonical, chosen.Set) { Captures = chosen.Captures };
    }

    /// <summary>A straight-in clearance's or a join's readback with its variant set; null when the id names no type and runway.</summary>
    private static BuilderClause? BuildVariedJoinApproachClause(ParsedCommand cmd, string approachId)
    {
        CanonicalCommandType type = CommandDescriber.ToCanonicalType(cmd);
        if (
            (ApproachReadbackName.ForShorthand(approachId) is not { } name)
            || (ApproachCommandHandler.JoinApproachReadback(type, name) is not { } canonical)
            || (ApproachReadbackName.SpokenParts(approachId) is not { } parts)
            || (ApproachCommandHandler.ClauseVariants(type, parts.Approach, parts.Runway) is not { } variants)
        )
        {
            return null;
        }

        return new BuilderClause(canonical, variants.Set) { Captures = variants.Captures };
    }

    /// <summary>
    /// The spoken clause of a builder-rendered command that has no variant set and is read back canonical in every band (a
    /// military route clearance, with or without an altitude); null for any other command.
    /// </summary>
    internal static string? HeldCanonicalClause(ParsedCommand cmd) =>
        cmd is ClearedIntoMilitaryRouteCommand cmtr ? BuildClearedIntoMilitaryRouteClause(cmtr).Tts : null;

    /// <summary>
    /// The builder's clause as this pilot says it now: the Terminal text is always the canonical; the spoken text is the
    /// canonical or, for a varied pilot, one seeded pick among it and the shape's rows of the band
    /// (<see cref="PhraseologyVerbalizer.VerbalizeBuilderClause"/>). Reports into <paramref name="trace"/> whether the Quiet
    /// pick offered rows beside the canonical, so the readback skips the quiet flavour as for a rule's Quiet rows.
    /// </summary>
    private static PilotSpeechText VaryBuilderClause(
        ParsedCommand cmd,
        BuilderClause built,
        string callsign,
        PilotSpeechContext speechContext,
        ClauseTrace trace
    )
    {
        if (built.Captures is not null)
        {
            return VaryFinalClause(cmd, built, callsign, speechContext, trace);
        }

        var request = new PhraseologyVerbalizer.VariantRequest(callsign, speechContext, trace.WholeReadback);
        ClauseVerbalization spoken = PhraseologyVerbalizer.VerbalizeBuilderClause(cmd, built.Variants, built.Canonical.Tts, request);
        trace.OfferedQuietRows |= spoken.OfferedQuietRows;
        return built.Canonical with { Tts = spoken.Text ?? built.Canonical.Tts };
    }

    /// <summary>
    /// <paramref name="built"/>'s canonical with only its final clause, the one the set's pattern renders from its
    /// <see cref="BuilderClause.Captures"/>, varied as <see cref="VaryBuilderClause"/> varies a whole clause; the route and
    /// crossing clauses before it stay canonical, and a clause after them makes the readback chained, so it is never chatty
    /// (<see cref="ChainedClauseContext"/>). A canonical that does not end with that clause is logged and read back canonical.
    /// </summary>
    private static PilotSpeechText VaryFinalClause(
        ParsedCommand cmd,
        BuilderClause built,
        string callsign,
        PilotSpeechContext speechContext,
        ClauseTrace trace
    )
    {
        PilotSpeechText canonical = built.Canonical;
        IReadOnlyDictionary<string, string> captures = built.Captures ?? new Dictionary<string, string>();
        string final = PhraseologyVerbalizer.RenderSetPattern(built.Variants, captures);
        if (!canonical.Tts.EndsWith(final, StringComparison.Ordinal))
        {
            Log.LogError(
                "Readback of {Command} does not end with the canonical \"{Final}\" of its variant set \"{Set}\": \"{Tts}\"; read back canonical",
                cmd,
                final,
                built.Variants.Name,
                canonical.Tts
            );
            return canonical;
        }

        string prefix = canonical.Tts[..^final.Length];
        bool chained = prefix.Length > 0;
        trace.Chained |= chained;
        PilotSpeechContext clauseContext = chained ? ChainedClauseContext(speechContext) : speechContext;
        var request = new PhraseologyVerbalizer.VariantRequest(callsign, clauseContext, trace.WholeReadback);
        ClauseVerbalization spoken = PhraseologyVerbalizer.VerbalizeBuilderClause(cmd, built.Variants, final, captures, request);
        trace.OfferedQuietRows |= spoken.OfferedQuietRows;
        return canonical with { Tts = prefix + (spoken.Text ?? final) };
    }

    /// <summary>
    /// Pilot readback of a takeoff clearance given while holding short of a taxiway (the handler's
    /// <see cref="CommandResult.PilotReadback"/>). An intersection departure from that taxiway names its departure point
    /// right after the runway ("cleared for takeoff runway 28R at E"); a clearance stored while the aircraft stays held
    /// says where it holds ("cleared for takeoff runway 28R, holding short of E").
    /// </summary>
    /// <param name="aircraft">The aircraft reading back.</param>
    /// <param name="lead">"cleared for takeoff" or "cleared for immediate takeoff".</param>
    /// <param name="departure">The departure instruction the clearance carries.</param>
    /// <param name="assignedAltitude">The altitude the clearance assigns, or null.</param>
    /// <param name="site">The taxiway held short of and where the clearance leaves the aircraft.</param>
    internal static PilotSpeechText BuildTakeoffClearanceAtTaxiwayHold(
        AircraftState aircraft,
        string lead,
        DepartureInstruction departure,
        int? assignedAltitude,
        TaxiwayHoldTakeoff site
    )
    {
        string? intersection = site.Outcome == TaxiwayHoldTakeoffOutcome.IntersectionDeparture ? site.Taxiway : null;
        PilotSpeechText clause = BuildTakeoffClearanceClause(
            aircraft,
            lead,
            departure,
            assignedAltitude,
            new TakeoffRunwayWording(true, intersection)
        );
        if (site.Outcome != TaxiwayHoldTakeoffOutcome.StaysHeld)
        {
            return clause;
        }

        return new PilotSpeechText(
            $"{clause.Terminal}, holding short of {site.Taxiway}",
            $"{clause.Tts}, holding short of {PhraseologyVerbalizer.SpellTaxiway(site.Taxiway)}"
        );
    }

    /// <summary>How a takeoff clearance's readback names the runway.</summary>
    /// <param name="IncludeRunway">Whether the runway is named ("cleared for takeoff runway 28R").</param>
    /// <param name="Intersection">The taxiway of an intersection departure, named right after the runway ("… 28R at E"); null for none.</param>
    private readonly record struct TakeoffRunwayWording(bool IncludeRunway, string? Intersection);

    private static PilotSpeechText BuildTakeoffClearanceClause(
        AircraftState aircraft,
        string lead,
        DepartureInstruction departure,
        int? assignedAltitude,
        TakeoffRunwayWording runwayWording
    )
    {
        var term = new StringBuilder(lead);
        var tts = new StringBuilder(lead);
        string runwayId = ResolveTakeoffRunwayId(aircraft);
        if (runwayWording.IncludeRunway && !string.IsNullOrWhiteSpace(runwayId))
        {
            term.Append(" runway ").Append(PhraseologyVerbalizer.CompactRunway(runwayId));
            tts.Append(" runway ").Append(PhraseologyVerbalizer.SpellRunway(runwayId));
        }

        if (runwayWording.Intersection is { } intersection)
        {
            term.Append(" at ").Append(intersection);
            tts.Append(" at ").Append(PhraseologyVerbalizer.SpellTaxiway(intersection));
        }

        PilotSpeechText departureClause = BuildDepartureInstructionClause(departure);
        if (!string.IsNullOrWhiteSpace(departureClause.Tts))
        {
            term.Append(", ").Append(departureClause.Terminal);
            tts.Append(", ").Append(departureClause.Tts);
        }

        if (assignedAltitude is { } altitude)
        {
            term.Append(", climb and maintain ").Append(PhraseologyVerbalizer.CompactAltitude(altitude));
            tts.Append(", climb and maintain ").Append(PhraseologyVerbalizer.AltitudeWords(altitude));
        }

        // "Caution wake turbulence" is a controller advisory (AIM 4-4-7), not a pilot readback item —
        // the pilot acknowledges the clearance without echoing it. The advisory still appears
        // controller-side via CommandDescriber.
        return new PilotSpeechText(term.ToString(), tts.ToString());
    }

    private static string ResolveTakeoffRunwayId(AircraftState aircraft)
    {
        if (!string.IsNullOrWhiteSpace(aircraft.Procedure.DepartureRunway))
        {
            return aircraft.Procedure.DepartureRunway;
        }

        return aircraft.Phases?.AssignedRunway?.Designator ?? "";
    }

    /// <summary>
    /// Pilot readback for CLAND. When the clearance just changed the landing runway to a diverging
    /// runway during a low approach (#292), the pilot prefaces the readback with "change to runway N"
    /// (7110.65 §3-10-5); otherwise it is the normal "cleared to land runway N".
    /// </summary>
    private static PilotSpeechText? BuildClearedToLandClause(AircraftState aircraft, ClearedToLandCommand cland)
    {
        bool runwayChanged = aircraft.Phases?.LandingRunwayChangedFromLowApproach == true;
        aircraft.Phases?.LandingRunwayChangedFromLowApproach = false;

        PilotSpeechText? landingClause = BuildRunwayInstructionClause(aircraft, "cleared to land", explicitRunwayId: cland.RunwayId);
        if ((landingClause is null) || !runwayChanged)
        {
            return landingClause;
        }

        string runwayId = cland.RunwayId ?? aircraft.Phases?.ClearedRunwayId ?? "";
        return new PilotSpeechText(
            $"change to runway {PhraseologyVerbalizer.CompactRunway(runwayId)}, cleared to land",
            $"change to runway {PhraseologyVerbalizer.SpellRunway(runwayId)}, cleared to land"
        );
    }

    private static PilotSpeechText? BuildRunwayInstructionClause(AircraftState aircraft, string lead, string? explicitRunwayId = null)
    {
        string runwayId = ResolveRunwayId(aircraft, explicitRunwayId);
        if (string.IsNullOrWhiteSpace(runwayId))
        {
            return null;
        }

        return new PilotSpeechText(
            $"{lead} runway {PhraseologyVerbalizer.CompactRunway(runwayId)}",
            $"{lead} runway {PhraseologyVerbalizer.SpellRunway(runwayId)}"
        );
    }

    private static PilotSpeechText? AppendWithoutDelayClause(PilotSpeechText? clause, bool withoutDelay)
    {
        if (clause is null || !withoutDelay)
        {
            return clause;
        }

        return new PilotSpeechText($"{clause.Terminal}, without delay", $"{clause.Tts}, without delay")
        {
            RpoTerminal = clause.RpoTerminal is { } rpoTerminal ? rpoTerminal + ", without delay" : null,
        };
    }

    private static PilotSpeechText BuildLandAndHoldShortClause(AircraftState aircraft, LandAndHoldShortCommand command)
    {
        string holdShortTerm = PhraseologyVerbalizer.CompactRunway(command.CrossingRunwayId);
        string holdShortTts = PhraseologyVerbalizer.SpellRunway(command.CrossingRunwayId);
        PilotSpeechText? landingClause = BuildRunwayInstructionClause(aircraft, "cleared to land");
        if (landingClause is null)
        {
            return new PilotSpeechText($"cleared to land, hold short runway {holdShortTerm}", $"cleared to land, hold short runway {holdShortTts}");
        }

        return new PilotSpeechText(
            $"{landingClause.Terminal}, hold short runway {holdShortTerm}",
            $"{landingClause.Tts}, hold short runway {holdShortTts}"
        )
        {
            RpoTerminal = landingClause.RpoTerminal is { } rpoTerminal ? $"{rpoTerminal}, hold short runway {holdShortTerm}" : null,
        };
    }

    /// <summary>
    /// Appends an option clearance's pattern modifier to its readback — ", make left traffic runway two
    /// eight left". The runway is spoken whenever the clearance named one: it is a runway assignment for
    /// the circuit after this one, and a runway assignment is read back (AIM 4-4-7.b.4). A bare MLT/MRT
    /// keeps the runway-less clause, which means the runway the clearance itself was issued for.
    /// </summary>
    private static PilotSpeechText? AppendTrafficPatternClause(PilotSpeechText? clause, PatternDirection? direction, string? patternRunwayId)
    {
        if (clause is null || direction is null)
        {
            return clause;
        }

        string lead = $", make {PatternDirectionWord(direction.Value)} traffic";
        bool namesRunway = !string.IsNullOrWhiteSpace(patternRunwayId);
        string terminalSuffix = lead + (namesRunway ? $" runway {PhraseologyVerbalizer.CompactRunway(patternRunwayId!)}" : "");
        string spokenSuffix = lead + (namesRunway ? $" runway {PhraseologyVerbalizer.SpellRunway(patternRunwayId!)}" : "");

        return new PilotSpeechText(clause.Terminal + terminalSuffix, clause.Tts + spokenSuffix)
        {
            RpoTerminal = clause.RpoTerminal is { } rpoTerminal ? rpoTerminal + terminalSuffix : null,
        };
    }

    private static string ResolveRunwayId(AircraftState aircraft, string? explicitRunwayId = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRunwayId))
        {
            return explicitRunwayId;
        }

        // The pre-issued clearance is last: an aircraft whose pattern entry is still queued has no
        // PhaseList at all, so without it the readback drops the runway ("cleared to land" bare).
        return aircraft.Phases?.ClearedRunwayId
            ?? aircraft.Phases?.AssignedRunway?.Designator
            ?? aircraft.Phases?.ActiveApproach?.RunwayId
            ?? aircraft.Pattern.PendingLandingClearance?.RunwayId
            ?? "";
    }

    private static PilotSpeechText BuildDepartureInstructionClause(DepartureInstruction departure) =>
        departure switch
        {
            DefaultDeparture => Lit(""),
            PresentPositionHoverDeparture => Lit("holding present position"),
            RunwayHeadingDeparture => Lit("fly runway heading"),
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Crosswind, Direction: PatternDirection.Right } => Lit("right crosswind departure"),
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Crosswind, Direction: PatternDirection.Left } => Lit("left crosswind departure"),
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Downwind, Direction: PatternDirection.Right } => Lit("right downwind departure"),
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Downwind, Direction: PatternDirection.Left } => Lit("left downwind departure"),
            RelativeTurnDeparture rel => new PilotSpeechText(
                $"make a {TurnDirectionWord(rel.Direction)} {rel.Degrees} degree departure",
                $"make a {TurnDirectionWord(rel.Direction)} {PhraseologyVerbalizer.DegreesWords(rel.Degrees)} degree departure"
            ),
            FlyHeadingDeparture { Direction: TurnDirection.Right } fh => new PilotSpeechText(
                $"turn right heading {PhraseologyVerbalizer.HeadingNumber(fh.MagneticHeading)}",
                $"turn right heading {PhraseologyVerbalizer.HeadingDigits(fh.MagneticHeading)}"
            ),
            FlyHeadingDeparture { Direction: TurnDirection.Left } fh => new PilotSpeechText(
                $"turn left heading {PhraseologyVerbalizer.HeadingNumber(fh.MagneticHeading)}",
                $"turn left heading {PhraseologyVerbalizer.HeadingDigits(fh.MagneticHeading)}"
            ),
            FlyHeadingDeparture fh => new PilotSpeechText(
                $"fly heading {PhraseologyVerbalizer.HeadingNumber(fh.MagneticHeading)}",
                $"fly heading {PhraseologyVerbalizer.HeadingDigits(fh.MagneticHeading)}"
            ),
            OnCourseDeparture => Lit("on course"),
            DirectFixDeparture { Direction: TurnDirection.Left } dfd => new PilotSpeechText(
                $"turn left direct {dfd.FixName.ToUpperInvariant()}",
                $"turn left direct {PhraseologyVerbalizer.SpellFix(dfd.FixName)}"
            ),
            DirectFixDeparture { Direction: TurnDirection.Right } dfd => new PilotSpeechText(
                $"turn right direct {dfd.FixName.ToUpperInvariant()}",
                $"turn right direct {PhraseologyVerbalizer.SpellFix(dfd.FixName)}"
            ),
            DirectFixDeparture dfd => new PilotSpeechText(
                $"direct {dfd.FixName.ToUpperInvariant()}",
                $"direct {PhraseologyVerbalizer.SpellFix(dfd.FixName)}"
            ),
            ClosedTrafficDeparture ct when ct.RunwayId is not null => new PilotSpeechText(
                $"make {PatternDirectionWord(ct.Direction)} traffic runway {PhraseologyVerbalizer.CompactRunway(ct.RunwayId)}",
                $"make {PatternDirectionWord(ct.Direction)} traffic runway {PhraseologyVerbalizer.SpellRunway(ct.RunwayId)}"
            ),
            ClosedTrafficDeparture ct => Lit($"make {PatternDirectionWord(ct.Direction)} traffic"),
            _ => Lit(""),
        };

    private static PilotSpeechText Lit(string s) => new(s, s);

    private static string TurnDirectionWord(TurnDirection direction) => direction == TurnDirection.Right ? "right" : "left";

    private static string PatternDirectionWord(PatternDirection direction) => direction == PatternDirection.Right ? "right" : "left";

    /// <summary>
    /// Pilot-initiated spawn check-in fired by <c>AtParkingPhase</c> 5 seconds after spawn
    /// in solo-training mode. Both IFR and VFR aircraft check in. A known spot is named as the gate or
    /// parking it is — <c>ground, at gate F8, with information Alpha, ready to taxi.</c> (terminal) /
    /// <c>ground, november one two three alpha bravo at gate foxtrot eight, with information Alpha, ready to taxi.</c>
    /// (spoken) — a name that reads as a word takes no noun (<c>at KILO RAMP</c> / <c>at kilo ramp</c>), and an unknown
    /// one falls back to <c>at the ramp</c>.
    /// No controller-side rule equivalent — this is a pure pilot utterance, so it lives here
    /// rather than in <c>PhraseologyRules</c>.
    /// </summary>
    public static PilotSpeechText BuildReadyToTaxi(AircraftState aircraft) => BuildReadyToTaxi(aircraft, "ground", "A");

    public static PilotSpeechText BuildReadyToTaxi(AircraftState aircraft, string facilityCallName, string? atisLetter) =>
        BuildReadyToTaxi(aircraft, facilityCallName, atisLetter, ReadyToTaxiLocation.ForStandCall(aircraft));

    /// <summary>
    /// A "ready to taxi" call from <paramref name="location"/>: <c>{facility}, {location}{info}{intent}, ready to taxi.</c>,
    /// with the location "at gate F8", "at spot 9", "on taxiway K", "pushed back from gate F8" or "holding short of C at
    /// T41W". An aircraft on a taxiway whose filed destination is the field it is at asks for parking instead:
    /// <c>{facility}, on taxiway K{info}, request taxi to parking.</c> (AIM 4-2-3.a.1.c, 4-3-18.a.1, 4-3-18.d.1).
    /// </summary>
    public static PilotSpeechText BuildReadyToTaxi(AircraftState aircraft, string facilityCallName, string? atisLetter, ReadyToTaxiLocation location)
    {
        (string place, string placeSpoken) = DescribeReadyToTaxiLocation(location);
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "ground");
        string info = AtisInfoClause(atisLetter);
        string tail = RequestsTaxiToParking(aircraft, location)
            ? $"{info}, request taxi to parking."
            : $"{info}{ReadyToTaxiIntentClause(aircraft)}, ready to taxi.";
        return new PilotSpeechText($"{facility}, {place}{tail}", $"{facility}, {spoken} {placeSpoken}{tail}");
    }

    /// <summary>
    /// A jet or turboprop's stand call from a push-back stand, asking ground for a push:
    /// <c>{facility}, at gate S7{info}, request pushback.</c> (terminal) /
    /// <c>{facility}, november one two three alpha bravo at gate sierra seven{info}, request pushback.</c> (spoken). The
    /// location and ATIS clauses are <see cref="BuildReadyToTaxi(AircraftState, string, string?, ReadyToTaxiLocation)"/>'s;
    /// the call names no destination or flight rules, which the after-push ready-to-taxi call carries.
    /// </summary>
    public static PilotSpeechText BuildPushbackRequest(
        AircraftState aircraft,
        string facilityCallName,
        string? atisLetter,
        ReadyToTaxiLocation location
    )
    {
        (string place, string placeSpoken) = DescribeReadyToTaxiLocation(location);
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "ground");
        string tail = $"{AtisInfoClause(atisLetter)}, request pushback.";
        return new PilotSpeechText($"{facility}, {place}{tail}", $"{facility}, {spoken} {placeSpoken}{tail}");
    }

    /// <summary>
    /// True when a call from <paramref name="location"/> asks for taxi to parking rather than reporting ready to taxi: the
    /// aircraft sits on a taxiway and its filed destination is the field it is at.
    /// </summary>
    public static bool RequestsTaxiToParking(AircraftState aircraft, ReadyToTaxiLocation location) =>
        (location.Kind == ReadyToTaxiLocationKind.Taxiway)
        && NavigationDatabase.AirportIdsMatch(aircraft.FlightPlan.Destination, PilotContactRoster.SurfaceAirportOf(aircraft));

    /// <summary>
    /// A departure's request to a delivery student from <paramref name="location"/> (AIM 5-2-6.h.2, 5-2-3.a, 4-2-3.a.1). IFR:
    /// <c>{facility}, at gate F8, with information A, IFR to Los Angeles Airport.</c> — no aircraft type, no
    /// "request clearance". VFR: <c>{facility}, at gate F8, with information A, VFR departure to the north, at 4500.</c>
    /// naming <paramref name="vfrDirection"/> and the filed cruise altitude (left out when none is filed), never the destination.
    /// </summary>
    public static PilotSpeechText BuildClearanceRequest(
        AircraftState aircraft,
        string facilityCallName,
        string? atisLetter,
        ReadyToTaxiLocation location,
        string? vfrDirection
    )
    {
        (string place, string placeSpoken) = DescribeReadyToTaxiLocation(location);
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "clearance");
        string info = AtisInfoClause(atisLetter);
        (string intent, string intentSpoken) = aircraft.FlightPlan.IsVfr ? VfrDepartureClause(aircraft, vfrDirection) : IfrClearanceClause(aircraft);
        return new PilotSpeechText($"{facility}, {place}{info}{intent}.", $"{facility}, {spoken}, {placeSpoken}{info}{intentSpoken}.");
    }

    private static (string Terminal, string Spoken) IfrClearanceClause(AircraftState aircraft)
    {
        string dest = aircraft.FlightPlan.Destination;
        string clause = string.IsNullOrWhiteSpace(dest) ? ", IFR" : $", IFR to {PhraseologyVerbalizer.SpellAirportName(dest)}";
        return (clause, clause);
    }

    private static (string Terminal, string Spoken) VfrDepartureClause(AircraftState aircraft, string? vfrDirection)
    {
        string direction = vfrDirection is { Length: > 0 } dir ? $" to the {dir}" : "";
        if (aircraft.FlightPlan.Altitude.CruiseFeet is not { } cruise || (cruise <= 0))
        {
            return ($", VFR departure{direction}", $", VFR departure{direction}");
        }

        return (
            $", VFR departure{direction}, at {PhraseologyVerbalizer.CompactAltitude(cruise)}",
            $", VFR departure{direction}, at {PhraseologyVerbalizer.AltitudeWords(cruise)}"
        );
    }

    /// <summary>
    /// A runway spawn at an untowered field asking the radar controller for its departure release (7110.65 §4-3-4):
    /// <c>{facility}, runway 25 at Auburn, ready for departure, request release.</c>
    /// </summary>
    public static PilotSpeechText BuildReleaseRequest(AircraftState aircraft, string runwayId, string airportId, string facilityCallName)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "approach");
        string airport = PhraseologyVerbalizer.SpellAirportName(airportId);
        return new PilotSpeechText(
            $"{facility}, runway {PhraseologyVerbalizer.CompactRunway(runwayId)} at {airport}, ready for departure, request release.",
            $"{facility}, {spoken}, runway {PhraseologyVerbalizer.SpellRunway(runwayId)} at {airport}, ready for departure, request release."
        );
    }

    /// <summary>The location clause of a ready-to-taxi call, terminal and spoken.</summary>
    private static (string Terminal, string Spoken) DescribeReadyToTaxiLocation(ReadyToTaxiLocation location)
    {
        switch (location.Kind)
        {
            case ReadyToTaxiLocationKind.Stand:
            case ReadyToTaxiLocationKind.Spot:
            case ReadyToTaxiLocationKind.PushedBackFrom:
                string noun = SpotNoun(location);
                string preposition = location.Kind == ReadyToTaxiLocationKind.PushedBackFrom ? "pushed back from" : "at";
                string name = noun == "spot" ? DropLeadingSpotWord(location.Name) : location.Name;
                return (
                    $"{preposition} {WithNoun(name, noun)}",
                    $"{preposition} {WithNoun(PhraseologyVerbalizer.SpellDestinationName(location.Name, noun), noun)}"
                );
            case ReadyToTaxiLocationKind.Taxiway:
                return ($"on taxiway {location.Name}", $"on taxiway {PhraseologyVerbalizer.SpellTaxiway(location.Name)}");
            case ReadyToTaxiLocationKind.HoldingShort:
                string spokenTarget = CommandParser.IsRunwayArg(location.Name)
                    ? PhraseologyVerbalizer.SpellRunway(location.Name)
                    : PhraseologyVerbalizer.SpellTaxiway(location.Name);
                return (
                    $"holding short of {HoldShortTarget.Describe(location.Name)} at {location.Taxiway}",
                    $"holding short of {spokenTarget} at {PhraseologyVerbalizer.SpellTaxiway(location.Taxiway)}"
                );
            default:
                return ("at the ramp", "at the ramp");
        }
    }

    /// <summary>
    /// The noun a pilot puts before a place it names: <c>spot</c> for a ramp spot; for a stand none when the name reads
    /// as a word ("at kilo ramp", "taxi to signature"), <c>gate</c> for a gate name, <c>parking</c> otherwise. The empty
    /// noun is safe for <see cref="PhraseologyVerbalizer.SpellDestinationName(string, string)"/>, whose noun-skip only
    /// fires on a non-empty match.
    /// </summary>
    private static string SpotNoun(ReadyToTaxiLocation location) => location.Kind == ReadyToTaxiLocationKind.Spot ? "spot" : StandNoun(location.Name);

    /// <summary>The noun before a stand's name: none for a name that reads as a word, <c>gate</c> for a gate, else <c>parking</c>.</summary>
    private static string StandNoun(string name) =>
        PhraseologyVerbalizer.ContainsPronounceableWord(name) ? "" : (ArrivalParkingPicker.IsGateName(name) ? "gate" : "parking");

    /// <summary>
    /// A spot name without a leading "SPOT" that would repeat the noun ("SPOT7" → "7"), as
    /// <see cref="PhraseologyVerbalizer.SpellDestinationName(string, string)"/> drops it when spoken; a name that is
    /// only the word keeps it.
    /// </summary>
    private static string DropLeadingSpotWord(string name)
    {
        string trimmed = name.Trim();
        if ((trimmed.Length > 4) && trimmed.StartsWith("SPOT", StringComparison.OrdinalIgnoreCase) && !char.IsLetter(trimmed[4]))
        {
            return trimmed[4..].TrimStart(' ', '-');
        }

        return trimmed;
    }

    /// <summary>A name with its noun in front when it has one (<c>gate F8</c>, <c>parking GA13</c>, <c>KILO RAMP</c>).</summary>
    private static string WithNoun(string name, string noun) => noun.Length > 0 ? $"{noun} {name}" : name;

    /// <summary>
    /// Op-type + destination clause a departing pilot states on the ready-to-taxi call
    /// ("IFR to Chicago" / "VFR to Napa", AIM 4-2-3.a.3 / 4-3-18), or empty when no destination is
    /// filed (e.g. a VFR local flight). Leading ", " so it inserts before ", ready to taxi"; the
    /// destination airport is spoken by name so the terminal and spoken forms match. An IFR departure that already has its
    /// clearance (<see cref="AircraftGroundOps.ClearanceObtainedAtSeconds"/>) states neither: the clearance request named them
    /// (AIM 5-2-1.a.5).
    /// </summary>
    private static string ReadyToTaxiIntentClause(AircraftState aircraft)
    {
        string dest = aircraft.FlightPlan.Destination;
        bool ifrWithItsClearance = !aircraft.FlightPlan.IsVfr && (aircraft.Ground.ClearanceObtainedAtSeconds is not null);
        if (string.IsNullOrWhiteSpace(dest) || ifrWithItsClearance)
        {
            return "";
        }
        string opType = aircraft.FlightPlan.IsVfr ? "VFR" : "IFR";
        return $", {opType} to {PhraseologyVerbalizer.SpellAirportName(dest)}";
    }

    /// <summary>
    /// Pilot check-in fired by <c>HoldingShortPhase</c> on entry (DestinationRunway reason only —
    /// the aircraft's assigned departure runway, never an intermediate runway crossing) in
    /// solo-training mode. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo holding short runway two eight right, ready for departure."</c>
    /// </summary>
    public static PilotSpeechText BuildHoldingShortReady(AircraftState aircraft, string runwayId) =>
        BuildHoldingShortReady(aircraft, runwayId, "tower");

    public static PilotSpeechText BuildHoldingShortReady(AircraftState aircraft, string runwayId, string facilityCallName)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "tower");
        return new PilotSpeechText(
            $"{facility}, holding short runway {PhraseologyVerbalizer.CompactRunway(runwayId)}, ready for departure.",
            $"{facility}, {spoken} holding short runway {PhraseologyVerbalizer.SpellRunway(runwayId)}, ready for departure."
        );
    }

    /// <summary>
    /// Pilot reminder fired by <c>LinedUpAndWaitingPhase</c> 10 seconds after entry when no
    /// takeoff clearance has been issued — the "did you forget me?" call. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo runway two eight right, ready."</c>
    /// </summary>
    public static PilotSpeechText BuildLinedUpReady(AircraftState aircraft, string runwayId) => BuildLinedUpReady(aircraft, runwayId, "tower");

    public static PilotSpeechText BuildLinedUpReady(AircraftState aircraft, string runwayId, string facilityCallName)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "tower");
        return new PilotSpeechText(
            $"{facility}, runway {PhraseologyVerbalizer.CompactRunway(runwayId)}, ready.",
            $"{facility}, {spoken} runway {PhraseologyVerbalizer.SpellRunway(runwayId)}, ready."
        );
    }

    /// <summary>
    /// Pilot check-in fired by <c>FinalApproachPhase</c> on entry for aircraft that
    /// <em>spawned</em> on final (gated by <c>!HasMadeInitialContact</c>). Two branches:
    /// <list type="bullet">
    ///   <item><description>IFR with active approach: <c>"[N123AB] tower, american one twenty three, ILS two eight right."</c></description></item>
    ///   <item><description>VFR / IFR-no-approach: <c>"[N123AB] tower, american one twenty three three-mile final runway
    ///   two eight right, with information Alpha."</c></description></item>
    /// </list>
    /// </summary>
    public static PilotSpeechText BuildOnFinal(
        AircraftState aircraft,
        string runwayId,
        bool ifrWithActiveApproach,
        string? approachId,
        int distanceMilesForVfr
    ) => BuildOnFinal(aircraft, runwayId, ifrWithActiveApproach, approachId, distanceMilesForVfr, "tower", "A");

    public static PilotSpeechText BuildOnFinal(
        AircraftState aircraft,
        string runwayId,
        bool ifrWithActiveApproach,
        string? approachId,
        int distanceMilesForVfr,
        string facilityCallName,
        string? atisLetter
    )
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "tower");
        if (ifrWithActiveApproach && !string.IsNullOrEmpty(approachId))
        {
            return new PilotSpeechText(
                $"{facility}, {CompactApproachName(approachId, runwayId)}.",
                $"{facility}, {spoken}, {SpokenApproachName(approachId, runwayId)}."
            );
        }

        int miles = Math.Max(1, distanceMilesForVfr);
        string info = AtisInfoClause(atisLetter);
        return new PilotSpeechText(
            $"{facility}, {miles}-mile final runway {PhraseologyVerbalizer.CompactRunway(runwayId)}{info}.",
            $"{facility}, {spoken} {AtcNumberParser.CardinalWord(miles)}-mile final runway {PhraseologyVerbalizer.SpellRunway(runwayId)}{info}."
        );
    }

    public static PilotSpeechText BuildArrivalApproachRequest(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        // A reminder fired when an arrival nearing the field still has no approach: the pilot prompts
        // the controller for an approach assignment. The pilot names neither a runway nor an approach
        // type — ATC assigns both for the airport's current configuration — so the call needs only
        // the callsign (the old "{N} miles to land runway X" was invented phraseology).
        return new PilotSpeechText("request approach assignment.", $"request approach assignment, {spoken}.");
    }

    /// <summary>
    /// Initial-contact check-in for VFR aircraft joining a towered field's traffic pattern for
    /// closed traffic. Fired by <c>PatternEntryPhase</c> on entry in solo-training mode, gated
    /// by <c>!HasMadeInitialContact</c>. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo, three miles south at one thousand five hundred, request
    /// closed traffic, with information Alpha."</c>
    /// </summary>
    public static PilotSpeechText BuildClosedTrafficRequest(AircraftState aircraft, LatLon airportPosition, int altitudeFt) =>
        BuildClosedTrafficRequest(aircraft, airportPosition, altitudeFt, "tower", "A");

    public static PilotSpeechText BuildClosedTrafficRequest(
        AircraftState aircraft,
        LatLon airportPosition,
        int altitudeFt,
        string facilityCallName,
        string? atisLetter
    )
    {
        string spoken = SpokenOwnCallsign(aircraft);
        double distNm = GeoMath.DistanceNm(airportPosition, aircraft.Position);
        int distMiles = Math.Max(1, (int)Math.Round(distNm));
        double bearingFromAirport = GeoMath.BearingTo(airportPosition, aircraft.Position);
        string direction = BearingToCardinal8(bearingFromAirport);
        string facility = CleanFacilityCallName(facilityCallName, "tower");
        string info = AtisInfoClause(atisLetter);
        // Pilots state altitude to the nearest 100 ft — there is no phraseology for tens/units.
        int reportedAltitude = (int)(Math.Round(altitudeFt / 100.0) * 100);
        return new PilotSpeechText(
            $"{facility}, {distMiles} miles {direction} at {PhraseologyVerbalizer.CompactAltitude(reportedAltitude)}, request closed traffic{info}.",
            $"{facility}, {spoken}, {SpellDistanceDigits(distMiles)} miles {direction} "
                + $"at {AtcNumberParser.AltitudeToWords(reportedAltitude)}, request closed traffic{info}."
        );
    }

    /// <summary>
    /// Pilot readback for the controller's CT (contact next controller) instruction. The
    /// controller side reads "contact (facility) on (frequency)"; the pilot drops the verb,
    /// repeats the frequency for confirmation, and signs off. Output:
    /// <c>"[N123AB] approach on one two five point three five, november one two three alpha bravo, so long."</c>
    /// <paramref name="speechContext"/> may abbreviate the callsign and group the spoken frequency
    /// ("twenty five thirty five"); the terminal text always carries the frequency in figures.
    /// </summary>
    public static PilotSpeechText BuildContactReadback(
        AircraftState aircraft,
        string facilityName,
        double frequencyMhz,
        PilotSpeechContext speechContext
    )
    {
        string spoken = SpokenReadbackCallsign(aircraft, speechContext);
        // Caller decides casing — pass Position.RadioName ("NorCal Approach") or the
        // capitalized FacilityShortname fallback ("Approach"), which reads sentence-initial.
        return new PilotSpeechText(
            $"{facilityName} on {frequencyMhz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, so long.",
            $"{facilityName} on {PhraseologyVerbalizer.FrequencyToWords(frequencyMhz, speechContext.GroupContactFrequency)}, {spoken}, so long."
        );
    }

    /// <summary>
    /// Pilot acknowledgement for the controller's FCA (frequency change approved) dismissal.
    /// "Frequency change approved" is the controller phraseology per FAA 7110.65 §7-6-11; pilots
    /// don't recite it verbatim. Real readback (per AIM 4-2-3 ¶3) is a sign-off:
    /// <c>"[N123AB] november one two three alpha bravo, good day."</c>
    /// </summary>
    public static PilotSpeechText BuildFrequencyChangeApproved(AircraftState aircraft, PilotSpeechContext speechContext)
    {
        string spoken = SpokenReadbackCallsign(aircraft, speechContext);
        return new PilotSpeechText("good day.", $"{spoken}, good day.");
    }

    /// <summary>
    /// Routes a sim-initiated pilot transmission. Two-way:
    /// <list type="bullet">
    ///   <item><description><see cref="AircraftState.PendingPilotSpeech"/> when in RPO mode (i.e.
    ///   <paramref name="soloTrainingMode"/> is false) and the
    ///   <c>RpoShowPilotSpeech</c> setting is on — the whole <paramref name="text"/> is queued and
    ///   broadcast as <c>PilotSpeech</c> kind (green) with its written
    ///   <see cref="PilotSpeechText.TerminalForRpo"/> form.</description></item>
    ///   <item><description><see cref="AircraftState.PendingWarnings"/> otherwise — broadcast as
    ///   <c>Warning</c> kind (orange) with the terse controller-debug <paramref name="warningText"/>.
    ///   This is the default when neither toggle is set.</description></item>
    /// </list>
    /// Solo-mode pilot-speech routing (into <see cref="AircraftState.PendingPilotTransmissions"/>) is
    /// the caller's responsibility — branch on <paramref name="soloTrainingMode"/> first if the
    /// site has a dedicated solo flow, then fall through to this helper for the non-solo case.
    /// </summary>
    public static void RouteRpoTransmission(
        AircraftState aircraft,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        PilotSpeechText text,
        string warningText
    )
    {
        if (!soloTrainingMode && rpoShowPilotSpeech)
        {
            aircraft.PendingPilotSpeech.Add(text);
        }
        else
        {
            aircraft.PendingWarnings.Add(warningText);
        }
    }

    /// <summary>
    /// Overload of <see cref="RouteRpoTransmission(AircraftState, bool, bool, PilotSpeechText, string)"/>
    /// for sim-initiated transmissions whose warning text is the pilot line itself, and whose terminal form
    /// differs between the solo student (who must not see a lead/target callsign diagnostic) and the
    /// RPO/instructor. RPO + <c>RpoShowPilotSpeech</c> queues the whole <paramref name="text"/> (shown green as
    /// <see cref="PilotSpeechText.TerminalForRpo"/>); the solo warning uses the callsign-free
    /// <see cref="PilotSpeechText.Terminal"/>; the RPO warning uses <see cref="PilotSpeechText.TerminalForRpo"/>.
    /// </summary>
    public static void RouteRpoTransmission(AircraftState aircraft, bool soloTrainingMode, bool rpoShowPilotSpeech, PilotSpeechText text)
    {
        if (!soloTrainingMode && rpoShowPilotSpeech)
        {
            aircraft.PendingPilotSpeech.Add(text);
        }
        else if (soloTrainingMode)
        {
            aircraft.PendingWarnings.Add(text.Terminal);
        }
        else
        {
            aircraft.PendingWarnings.Add(text.TerminalForRpo);
        }
    }

    /// <summary>
    /// Unified router for sim-initiated pilot transmissions. Takes a <see cref="PilotSpeechText"/>
    /// (terminal + TTS forms produced independently by the builder, not by regex-compacting the
    /// spoken string) plus the list of student positions for which the transmission is
    /// audiable: e.g. clear-of-runway is tower-only, pattern reports and follow events are
    /// tower-or-approach.
    /// <list type="bullet">
    ///   <item><description><b>Solo + student in <paramref name="soloRelevantPositions"/></b>:
    ///   queue into <see cref="AircraftState.PendingPilotTransmissions"/> so delayed SAY
    ///   and TTS use the spoken pilot line.</description></item>
    ///   <item><description><b>Solo + irrelevant position</b>: terminal warning only (no TTS) —
    ///   the student isn't listening on this frequency.</description></item>
    ///   <item><description><b>RPO with show-pilot-speech</b>: queue <paramref name="text"/> into
    ///   <see cref="AircraftState.PendingPilotSpeech"/>, broadcast as <c>PilotSpeech</c> kind (green)
    ///   with its written <see cref="PilotSpeechText.TerminalForRpo"/> form.</description></item>
    ///   <item><description><b>RPO default</b>: terminal warning with the readable form.</description></item>
    /// </list>
    /// </summary>
    public static void RouteSoloOrRpoTransmission(
        AircraftState aircraft,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        string? studentPositionType,
        PilotSpeechText text,
        IReadOnlyCollection<string> soloRelevantPositions
    )
    {
        if (soloTrainingMode)
        {
            bool studentIsRelevant =
                studentPositionType is { Length: > 0 } st && soloRelevantPositions.Contains(st, StringComparer.OrdinalIgnoreCase);
            if (studentIsRelevant)
            {
                aircraft.PendingPilotTransmissions.Add(
                    new PilotTransmission(
                        aircraft.Callsign,
                        text.Terminal,
                        text.Tts,
                        SourceResponse,
                        PilotTransmissionKind.Proactive,
                        FrequencyState.EstimateAirtimeSeconds(text.Tts)
                    )
                );
            }
            else
            {
                // Solo mode but the student isn't on this frequency — show as a terminal
                // warning so the controller can still see what the pilot would have said,
                // but don't speak it.
                aircraft.PendingWarnings.Add(text.Terminal);
            }
            return;
        }

        if (rpoShowPilotSpeech)
        {
            aircraft.PendingPilotSpeech.Add(text);
        }
        else
        {
            aircraft.PendingWarnings.Add(text.TerminalForRpo);
        }
    }

    /// <summary>
    /// Routes a sim-initiated pilot readback for visual-acquisition events (RTIS / RFIS)
    /// onto the SAY channel rather than the orange Warning channel. Takes a
    /// <see cref="PilotSpeechText"/> whose terminal and TTS forms were built independently
    /// (not by regex-compacting the spoken string). RPO mode + <c>RpoShowPilotSpeech</c> queues
    /// the whole text into <see cref="AircraftState.PendingPilotSpeech"/> (shown green as its written
    /// <see cref="PilotSpeechText.TerminalForRpo"/> form); solo mode
    /// queues both forms via <see cref="QueueSoloPilotReadback(AircraftState, PilotSpeechText, string)"/>;
    /// otherwise the terse <see cref="AircraftState.PendingPilotReadbacks"/> (terminal form) which
    /// the server broadcasts as kind <c>SayReadback</c> (the kind starts with "Say" so the client
    /// maps it to the SAY channel like SPOS / SALT). The callsign rides the SAY column in every
    /// branch, so neither form repeats it inline.
    /// </summary>
    public static void RouteRpoSayReadback(AircraftState aircraft, bool soloTrainingMode, bool rpoShowPilotSpeech, PilotSpeechText text)
    {
        if (!soloTrainingMode && rpoShowPilotSpeech)
        {
            aircraft.PendingPilotSpeech.Add(text);
        }
        else if (soloTrainingMode)
        {
            QueueSoloPilotReadback(aircraft, text, SourceSayReadback);
        }
        else
        {
            aircraft.PendingPilotReadbacks.Add(text.TerminalForRpo);
        }
    }

    /// <summary>
    /// Brief uncleared-traffic reminder fired by <c>DownwindPhase</c> at midfield downwind when
    /// the aircraft has no landing clearance, in solo-training mode for VFR pattern aircraft.
    /// Output:
    /// <c>"[N123AB] november one two three alpha bravo, midfield downwind runway two eight right."</c>
    /// </summary>
    public static PilotSpeechText BuildMidfieldDownwindReminder(AircraftState aircraft, string runwayId)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"midfield downwind runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, midfield downwind runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
        );
    }

    /// <summary>
    /// Brief uncleared-traffic reminder fired by <c>FinalApproachPhase</c> at 1 NM from threshold
    /// when the aircraft has no landing clearance, in solo-training mode for VFR pattern aircraft.
    /// Uses the GA-pilot colloquial "short final" form rather than the FAA controller-canonical
    /// "(distance) mile final" — pilot transmissions don't have to mirror 7110.65 phraseology.
    /// Output:
    /// <c>"[N123AB] november one two three alpha bravo, short final runway two eight right."</c>
    /// </summary>
    public static PilotSpeechText BuildShortFinalReminder(AircraftState aircraft, string runwayId)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"short final runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, short final runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
        );
    }

    /// <summary>
    /// Deferred pilot position report voiced when the controller armed it via <c>REPORT</c> and the
    /// aircraft reaches the leg. Output:
    /// <c>"[N123AB] november one two three alpha bravo, turning base runway two eight right."</c>
    /// </summary>
    public static PilotSpeechText BuildTurningLegReport(AircraftState aircraft, ReportTrigger leg, string runwayId)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string legWord = leg switch
        {
            ReportTrigger.Crosswind => "crosswind",
            ReportTrigger.Downwind => "downwind",
            ReportTrigger.Base => "base",
            ReportTrigger.Final => "final",
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, $"{leg} is not a turning-leg report"),
        };
        return new PilotSpeechText(
            $"turning {legWord} runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, turning {legWord} runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
        );
    }

    /// <summary>
    /// Deferred midfield-downwind report armed by <c>REPORT MIDFIELD</c>, worded by where the aircraft is when it fires.
    /// Output, left traffic:
    /// <c>"[N123AB] november one two three alpha bravo, midfield left downwind runway two eight right."</c>,
    /// <c>"…, left downwind runway two eight right, past midfield."</c> or
    /// <c>"…, left downwind runway two eight right, abeam the numbers."</c>
    /// </summary>
    public static PilotSpeechText BuildMidfieldDownwindReport(
        AircraftState aircraft,
        PatternDirection side,
        MidfieldReportPosition position,
        string runwayId
    )
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string sideWord = side == PatternDirection.Right ? "right" : "left";
        string compact = PhraseologyVerbalizer.CompactRunway(runwayId);
        string spelled = PhraseologyVerbalizer.SpellRunway(runwayId);
        return position switch
        {
            MidfieldReportPosition.Midfield => new PilotSpeechText(
                $"midfield {sideWord} downwind runway {compact}.",
                $"{spoken}, midfield {sideWord} downwind runway {spelled}."
            ),
            MidfieldReportPosition.PastMidfield => new PilotSpeechText(
                $"{sideWord} downwind runway {compact}, past midfield.",
                $"{spoken}, {sideWord} downwind runway {spelled}, past midfield."
            ),
            MidfieldReportPosition.AbeamNumbers => new PilotSpeechText(
                $"{sideWord} downwind runway {compact}, abeam the numbers.",
                $"{spoken}, {sideWord} downwind runway {spelled}, abeam the numbers."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(position), position, "unknown midfield report position"),
        };
    }

    /// <summary>
    /// Deferred pilot "n-mile final" report voiced when the aircraft reaches the armed distance from
    /// the threshold. Output:
    /// <c>"[N123AB] november one two three alpha bravo, five mile final runway two eight right."</c>
    /// </summary>
    public static PilotSpeechText BuildMileFinalReport(AircraftState aircraft, int miles, string runwayId)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"{miles}-mile final runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, {AtcNumberParser.CardinalWord(miles)} mile final runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
        );
    }

    /// <summary>
    /// Deferred pilot fix-passage report voiced when the aircraft reaches the armed fix. Uses the
    /// codified report verb "passing" (7110.65 PCG REPORT- / §5-5-5), not "at" (which connotes a
    /// clearance limit). Output:
    /// <c>"[N123AB] november one two three alpha bravo, passing SUNOL."</c>
    /// </summary>
    public static PilotSpeechText BuildAtFixReport(AircraftState aircraft, string fixName)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"passing {PhraseologyVerbalizer.FixDisplayText(fixName)}.",
            $"{spoken}, passing {PhraseologyVerbalizer.SpellFix(fixName)}."
        );
    }

    /// <summary>Cardinal spelling of a final-report distance (1–20 NM) for TTS, falling back to digits.</summary>
    /// <summary>
    /// Pilot transmission when the aircraft has the named traffic in sight. Used by sim-resolved
    /// RTIS (visual acquisition) and by direct RTIS dispatches in RPO mode with pilot-speech
    /// rendering enabled. AIM 5-5-10 / 5-5-11. Neither the spoken transmission nor the solo terminal
    /// names the traffic's callsign — a pilot (and the solo student playing controller) identifies
    /// traffic by the position/type/altitude call, not by callsign. The RPO terminal keeps the
    /// callsign as an instructor diagnostic of which traffic was acquired.
    /// </summary>
    public static PilotSpeechText BuildTrafficInSight(AircraftState aircraft, string? targetCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string tts = $"{spoken}, traffic in sight.";
        if (string.IsNullOrWhiteSpace(targetCallsign))
        {
            return new PilotSpeechText("traffic in sight.", tts);
        }
        return new PilotSpeechText("traffic in sight.", tts) { RpoTerminal = $"traffic ({targetCallsign}) in sight." };
    }

    /// <summary>
    /// Pilot transmission when the aircraft has the destination field in sight. Used by sim-resolved
    /// RFIS and by direct RFIS dispatches in RPO mode with pilot-speech rendering enabled. Terminal
    /// is the compact form (callsign in the SAY column); TTS prepends the spoken own callsign.
    /// </summary>
    public static PilotSpeechText BuildFieldInSight(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("field in sight.", $"{spoken}, field in sight.");
    }

    /// <summary>
    /// Pilot transmission when the aircraft has lost prior visual contact with the destination
    /// field. Triggers a re-acquisition request from the controller's perspective. Terminal is the
    /// compact form (callsign in the SAY column); TTS prepends the spoken own callsign.
    /// </summary>
    public static PilotSpeechText BuildLostSightOfField(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("lost sight of the field.", $"{spoken}, lost sight of the field.");
    }

    /// <summary>
    /// Pilot transmission when the aircraft has lost prior visual contact with previously-acquired
    /// traffic. Spoken and solo terminal forms say "the traffic" (the pilot never had the lead's
    /// callsign); the RPO terminal names it as an instructor diagnostic.
    /// </summary>
    public static PilotSpeechText BuildLostSightOfTraffic(AircraftState aircraft, string targetCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("lost sight of the traffic.", $"{spoken}, lost sight of the traffic.")
        {
            RpoTerminal = $"lost sight of {targetCallsign}.",
        };
    }

    /// <summary>
    /// Visual-approach follower lost the lead but still holds the FIELD (AIM §5-5-11.a.3 case a):
    /// the clearance stands and the aircraft continues its own visual, but pilot-applied visual
    /// separation just ended, so the report couples the loss with the still-held reference
    /// (7110.65 §7-4-3.c.3 — the controller must reassume radar separation). Spoken and solo
    /// terminal forms say "the traffic"; the RPO terminal names the lead as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildLostSightOfTrafficFieldInSight(AircraftState aircraft, string targetCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            "lost sight of the traffic, field in sight.",
            $"{spoken}, lost sight of the traffic, we still have the field in sight."
        )
        {
            RpoTerminal = $"lost sight of {targetCallsign}, field in sight.",
        };
    }

    /// <summary>
    /// The phrase naming which visual reference(s) were lost, shared by the end-of-visual
    /// builders. Both-false is the backstop wording for an aircraft that never held the
    /// reference it needed (e.g. the lead landed before the follower ever saw the field).
    /// </summary>
    private static string LostVisualReferencePhrase(bool lostField, bool lostTraffic) =>
        (lostField, lostTraffic) switch
        {
            (true, true) => "lost sight of the field and the traffic",
            (true, false) => "lost sight of the field",
            (false, true) => "lost sight of the traffic",
            (false, false) => "don't have the field in sight",
        };

    /// <summary>
    /// Visual approach no longer legal — nothing in sight (AIM §5-5-11.a.3 case c), away from
    /// the runway: the pilot advises, levels off, and asks for a new clearance following the
    /// AIM §5-5-16.a.1 reason → unable → request structure. The climb decision belongs to ATC
    /// (7110.65 §7-4-1.a.2), so the aircraft holds altitude and heading until vectored.
    /// Spoken/solo forms never name the lead; the RPO terminal does when one was involved.
    /// </summary>
    public static PilotSpeechText BuildUnableVisualRequestVectors(AircraftState aircraft, bool lostField, bool lostTraffic, string? leadCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string phrase = LostVisualReferencePhrase(lostField, lostTraffic);
        string spokenPhrase = (lostField || lostTraffic) ? $"we've {phrase}" : $"we {phrase}";
        string terminal = $"unable the visual, {phrase} — request vectors.";
        string tts = $"{spoken}, unable the visual, {spokenPhrase}, request vectors.";
        if (lostTraffic && leadCallsign is not null)
        {
            return new PilotSpeechText(terminal, tts)
            {
                RpoTerminal = $"unable the visual, {phrase.Replace("the traffic", leadCallsign)} — request vectors.",
            };
        }
        return new PilotSpeechText(terminal, tts);
    }

    /// <summary>
    /// The assigned vector never delivered the aircraft to the final approach course — it flew
    /// parallel or diverging until the intercept gave up. The pilot advises and asks for a new
    /// vector, following the AIM §5-5-16.a.1 reason → unable → request structure. Distinct from the
    /// passing-through-the-localizer report, which states a position this aircraft never reached.
    /// </summary>
    public static PilotSpeechText BuildUnableToInterceptRequestVectors(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        const string terminal = "unable to intercept the localizer, request vectors.";
        return new PilotSpeechText(terminal, $"{spoken}, {terminal}");
    }

    /// <summary>
    /// A bare go-around issued outside the final approach fix: the published missed approach does not start from where the
    /// aircraft is, so the pilot refuses and asks for a vector, following the AIM §5-5-16.a.1 unable → reason → request
    /// structure. <paramref name="where"/> is the reason clause ("we're in the procedure turn", "we're in the hold",
    /// "we're outside the final approach fix").
    /// </summary>
    public static PilotSpeechText BuildUnableGoAroundRequestVectors(AircraftState aircraft, string where)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string terminal = $"unable go-around, {where}, request vectors.";
        return new PilotSpeechText(terminal, $"{spoken}, {terminal}");
    }

    /// <summary>
    /// Visual approach no longer legal on short final (committed): the pilot goes around and —
    /// unlike the sim-internal go-around reasons, which stay in the terminal parenthetical —
    /// SPEAKS the reason, per AIM §5-5-5.a.2 ("include the reason" when the pilot initiates
    /// the missed approach).
    /// </summary>
    public static PilotSpeechText BuildGoingAroundLostVisualReference(AircraftState aircraft, bool lostField, bool lostTraffic)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string phrase = LostVisualReferencePhrase(lostField, lostTraffic);
        string spokenPhrase = (lostField || lostTraffic) ? $"we've {phrase}" : $"we {phrase}";
        return new PilotSpeechText($"going around, {phrase}.", $"{spoken}, going around, {spokenPhrase}.");
    }

    /// <summary>
    /// The runway is occupied on short final: the pilot goes around and SPEAKS the reason (AIM
    /// §5-2-5.9 — never land on an occupied runway even with a clearance; §5-5-5.a.2 — include the
    /// reason when the pilot initiates the missed approach).
    /// </summary>
    public static PilotSpeechText BuildGoingAroundTrafficOnRunway(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("going around, traffic on the runway.", $"{spoken}, going around, traffic on the runway.");
    }

    /// <summary>
    /// Rejected takeoff for a runway occupant: the pilot aborts the roll and SPEAKS the reason —
    /// the controller needs to know it was traffic, not a mechanical. "Abort" is the P/CG term
    /// for terminating a preplanned maneuver ("e.g., an aborted takeoff"); "reject" stays the
    /// internal/code name.
    /// </summary>
    public static PilotSpeechText BuildRejectingTakeoffTrafficOnRunway(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("aborting takeoff, traffic on the runway.", $"{spoken}, aborting takeoff, traffic on the runway.");
    }

    /// <summary>
    /// Stopped on the runway after a rejected takeoff, awaiting instructions — the controller's
    /// cue that the runway is now blocked and the aircraft needs an exit or taxi instruction
    /// (AIM 4-3-18.a: movement on the movement area needs ATC approval; the sim's crew always
    /// waits for the instruction rather than self-vacating).
    /// </summary>
    public static PilotSpeechText BuildStoppedOnRunway(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("stopped on the runway, standing by.", $"{spoken} is stopped on the runway, standing by.");
    }

    /// <summary>
    /// Not aligned with the assigned runway on short final: the pilot goes around and SPEAKS
    /// the reason (AIM §5-5-5.a.1(b) — missed approach when a safe landing is not possible;
    /// §5-5-5.a.2 — include the reason). The terminal names the runway and the measured
    /// offset — the controller needs to see WHICH centerline the aircraft was tracking; the
    /// spoken form stays colloquial.
    /// </summary>
    public static PilotSpeechText BuildGoingAroundNotAligned(AircraftState aircraft, string runwayId, double offCenterlineFt)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string displayRwy = Data.Airport.RunwayIdentifier.ToDisplayDesignator(runwayId);
        return new PilotSpeechText(
            $"going around, not lined up with runway {displayRwy} ({offCenterlineFt:F0} ft off centerline).",
            $"{spoken}, going around, we're not lined up."
        );
    }

    /// <summary>
    /// Pilot transmission when the pilot initiates a go-around. Reason is a short sim-internal
    /// descriptor ("no landing clearance", "too high at missed approach point", etc.) that's
    /// included parenthetically so the controller has the why; the spoken callout itself is the
    /// AIM standard "going around."
    /// </summary>
    public static PilotSpeechText BuildGoingAround(AircraftState aircraft, string reason)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new PilotSpeechText("going around.", $"{spoken}, going around.");
        }
        // The reason is a sim-internal diagnostic for the controller-facing terminal line only — the
        // pilot speaks the AIM-standard "going around" without the parenthetical.
        return new PilotSpeechText($"going around ({reason}).", $"{spoken}, going around.");
    }

    /// <summary>
    /// Pilot transmission when approaching published DA/MDA without a landing clearance.
    /// Gives the solo-training controller a radio-visible chance to issue the clearance
    /// before the aircraft reaches minimums and initiates the missed approach.
    /// </summary>
    public static PilotSpeechText BuildApproachingMinimumsNoLandingClearance(AircraftState aircraft)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("approaching minimums, no landing clearance.", $"{spoken}, approaching minimums, no landing clearance.");
    }

    /// <summary>
    /// Pilot response when a live solo-training controller instruction is rejected by
    /// dispatch. Mirrors 7110.65 operational-request phraseology: "unable" plus a reason
    /// when one is available.
    /// </summary>
    public static PilotSpeechText BuildUnable(AircraftState aircraft, string? reason)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string cleanedReason = CleanUnableReason(reason);
        if (string.IsNullOrEmpty(cleanedReason))
        {
            return new PilotSpeechText("unable.", $"{spoken}, unable.");
        }

        return new PilotSpeechText($"unable, {cleanedReason}.", $"{spoken}, unable, {cleanedReason}.");
    }

    /// <summary>Which end of the type's speed envelope a refused speed fell past, and the frame its figure is named in.</summary>
    public enum SpeedLimitKind
    {
        /// <summary>Past the maximum where Vmo governs: the fastest speed, in knots.</summary>
        FastestKnots,

        /// <summary>Past the maximum where Mmo governs (above the IAS/Mach crossover): the fastest Mach number.</summary>
        FastestMach,

        /// <summary>Below the type's final approach speed: the slowest speed, in knots.</summary>
        SlowestKnots,
    }

    /// <summary>
    /// Pilot refusal of a speed the type cannot fly, naming the speed it can: "unable, 290 knots is the fastest we can do",
    /// "unable, Mach .82 is the fastest we can do", "unable, 140 knots is the slowest we can do". The pilot keeps its
    /// current speed and offers the limit instead (AIM 4-4-12.h and 7110.65 §5-7-3.a NOTE 2: a pilot unable to comply with a speed adjustment advises
    /// ATC). The terminal line carries the figure as numerals; the spoken line reads knots as digits
    /// (<see cref="PhraseologyVerbalizer.SpeedWords"/>) and a Mach number as "point eight two"
    /// (<see cref="PhraseologyVerbalizer.MachWords"/>).
    /// </summary>
    /// <param name="aircraft">The refusing aircraft, for its spoken callsign.</param>
    /// <param name="kind">Which limit the refused speed fell past, and so the frame the figure is named in.</param>
    /// <param name="figure">The named speed: knots for the knots kinds, a Mach number for <see cref="SpeedLimitKind.FastestMach"/>.</param>
    public static PilotSpeechText BuildUnableSpeedLimit(AircraftState aircraft, SpeedLimitKind kind, double figure)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string bound = kind == SpeedLimitKind.SlowestKnots ? "slowest" : "fastest";
        if (kind == SpeedLimitKind.FastestMach)
        {
            string machText = figure.ToString(".00", System.Globalization.CultureInfo.InvariantCulture);
            string machWords = PhraseologyVerbalizer.MachWords(figure);
            return new PilotSpeechText(
                $"unable, Mach {machText} is the {bound} we can do.",
                $"{spoken}, unable, Mach {machWords} is the {bound} we can do."
            );
        }

        int knots = (int)Math.Round(figure);
        return new PilotSpeechText(
            $"unable, {knots} knots is the {bound} we can do.",
            $"{spoken}, unable, {PhraseologyVerbalizer.SpeedWords(knots)} knots is the {bound} we can do."
        );
    }

    private static string CleanUnableReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "";
        }

        // The separator sets cover en dash and em dash alongside the ASCII hyphen. Rejection reasons are
        // authored as controller-facing text where "Unable — reason" is the natural typography, and an
        // unhandled dash survives the strip to be spoken: "unable, — already turning off at G".
        string cleaned = reason.Trim();
        cleaned = Regex.Replace(cleaned, @"^\s*unable\b[:,\s\-–—]*", "", RegexOptions.IgnoreCase);
        cleaned = cleaned.Trim(' ', '.', ',', ';', ':', '-', '–', '—');
        if (cleaned.Length == 0)
        {
            return "";
        }

        return cleaned;
    }

    /// <summary>
    /// Pilot transmission for a taxi-side hold-short report ("holding short of [label] at [taxiway]").
    /// Used when the ground phase reaches a hold-short node where the label is a generic identifier
    /// (taxiway intersection, ILS-critical area, etc.) rather than a specific runway designator. With
    /// <paramref name="requestFurtherTaxi"/> — the end of a route that could not reach its destination as cleared — the
    /// report ends asking for the rest of the way ("holding short of C at E, request further taxi").
    /// </summary>
    public static PilotSpeechText BuildHoldingShortTaxi(AircraftState aircraft, string label, string taxiway, bool requestFurtherTaxi)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string request = requestFurtherTaxi ? ", request further taxi" : "";
        return new PilotSpeechText($"{label} at {taxiway}{request}.", $"{spoken}, {label} at {taxiway}{request}.");
    }

    /// <summary>
    /// Pilot transmission when the aircraft is holding short of a runway prior to a runway crossing.
    /// </summary>
    public static PilotSpeechText BuildHoldingShortCrossing(AircraftState aircraft, string runwayId)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"holding short runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, holding short runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
        );
    }

    /// <summary>
    /// Pilot transmission when the aircraft, cleared across a runway, holds short of it because traffic beyond the far
    /// holding-position marking leaves it no room to clear (AIM 4-3-18.b): "Holding short of runway 1L, traffic ahead".
    /// <paramref name="runwayId"/> is a single runway end as the hold-short texts name it — the end nearest the aircraft
    /// (<see cref="Yaat.Sim.Data.Airport.RunwayCrossingEnd.Nearest"/>). Every form says "traffic", never the traffic's callsign.
    /// </summary>
    public static PilotSpeechText BuildHoldingShortTrafficAhead(AircraftState aircraft, string runwayId)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"Holding short of runway {PhraseologyVerbalizer.CompactRunway(runwayId)}, traffic ahead",
            $"{spoken}, holding short of runway {PhraseologyVerbalizer.SpellRunway(runwayId)}, traffic ahead."
        );
    }

    /// <summary>
    /// Pilot transmission when the aircraft, cleared across a runway with a follow armed, holds short of it until the traffic it
    /// follows has passed where their routes meet: "Holding short of runway 1R, waiting for the traffic to pass". The leader is
    /// "the traffic" in the spoken and solo forms, like every follow call; its callsign is the RPO diagnostic.
    /// <paramref name="runwayId"/> is a single runway end as the hold-short texts name it.
    /// </summary>
    public static PilotSpeechText BuildHoldingShortForFollowTraffic(AircraftState aircraft, string runwayId, string leadCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string terminalRunway = PhraseologyVerbalizer.CompactRunway(runwayId);
        return new PilotSpeechText(
            $"Holding short of runway {terminalRunway}, waiting for the traffic to pass",
            $"{spoken}, holding short of runway {PhraseologyVerbalizer.SpellRunway(runwayId)}, waiting for the traffic to pass."
        )
        {
            RpoTerminal = $"Holding short of runway {terminalRunway}, waiting for {leadCallsign} to pass",
        };
    }

    /// <summary>
    /// Pilot transmission when the aircraft has fully exited the active runway after landing.
    /// Reports the runway just vacated and the taxiway used. Returns terminal + TTS forms
    /// independently — terminal uses the digit runway designator, TTS uses the spelled form.
    /// </summary>
    public static PilotSpeechText BuildClearOfRunwayText(AircraftState aircraft, string runwayId, string taxiway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string runwaySpoken = PhraseologyVerbalizer.SpellRunway(runwayId);
        string terminal = $"clear of runway {PhraseologyVerbalizer.CompactRunway(runwayId)} at {taxiway}.";
        string tts = $"{spoken}, clear of runway {runwaySpoken} at {taxiway}.";
        return new PilotSpeechText(terminal, tts);
    }

    /// <summary>
    /// Pilot transmission in place of <see cref="BuildClearOfRunwayText"/> when the aircraft stopped with its nose at another
    /// runway's holding position marking and its tail still inside the runway it was clearing (a crossover shorter than the
    /// aircraft): it reports where it holds and that it has not cleared. Terminal and TTS forms are built independently.
    /// </summary>
    public static PilotSpeechText BuildHoldingShortNotClearOfRunwayText(
        AircraftState aircraft,
        string otherRunway,
        string taxiway,
        string clearingRunway
    )
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string otherCompact = PhraseologyVerbalizer.CompactRunway(otherRunway);
        string clearingCompact = PhraseologyVerbalizer.CompactRunway(clearingRunway);
        string otherSpelled = PhraseologyVerbalizer.SpellRunway(otherRunway);
        string clearingSpelled = PhraseologyVerbalizer.SpellRunway(clearingRunway);
        string terminal = $"holding short of runway {otherCompact} at {taxiway}, not clear of runway {clearingCompact}.";
        string tts = $"{spoken}, holding short of runway {otherSpelled} at {taxiway}, not clear of runway {clearingSpelled}.";
        return new PilotSpeechText(terminal, tts);
    }

    /// <summary>
    /// The arrival's call to ground after clearing the landing runway (AIM 4-3-21.c: change to ground control and obtain a
    /// taxi clearance) — who is calling, where it is, and the parking it wants. Output:
    /// <c>"Oakland Ground, clear of runway 28R at W, taxi to gate 29."</c> (terminal) /
    /// <c>"Oakland Ground, november one five two sierra papa, clear of runway two eight right at W, taxi to gate two niner."</c>
    /// A gate name is a "gate", a name that reads as a word takes no noun ("taxi to SIGNATURE" / "taxi to signature"),
    /// and anything else is "parking" spelled out ("parking sierra india golf one").
    /// </summary>
    public static PilotSpeechText BuildTaxiInRequest(
        AircraftState aircraft,
        string facilityCallName,
        string? runwayId,
        string? taxiway,
        string parking
    )
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string facility = CleanFacilityCallName(facilityCallName, "ground");
        string runwayTerminal = runwayId is { Length: > 0 } ? $"runway {PhraseologyVerbalizer.CompactRunway(runwayId)}" : "the runway";
        string runwaySpoken = runwayId is { Length: > 0 } ? $"runway {PhraseologyVerbalizer.SpellRunway(runwayId)}" : "the runway";
        string at = taxiway is { Length: > 0 } ? $" at {taxiway}" : "";
        string atSpoken = taxiway is { Length: > 0 } ? $" at {PhraseologyVerbalizer.SpellTaxiway(taxiway)}" : "";
        string noun = StandNoun(parking);
        string destination = WithNoun(parking, noun);
        string destinationSpoken = WithNoun(PhraseologyVerbalizer.SpellDestinationName(parking, noun), noun);
        return new PilotSpeechText(
            $"{facility}, clear of {runwayTerminal}{at}, taxi to {destination}.",
            $"{facility}, {spoken}, clear of {runwaySpoken}{atSpoken}, taxi to {destinationSpoken}."
        );
    }

    /// <summary>
    /// Pilot transmission when an instructed taxi exit cannot be made (overshoot, wrong-side, etc.).
    /// Pilot phraseology: "negative" for no-can-do, target taxiway for context.
    /// </summary>
    /// <summary>
    /// Pilot refusal when an assigned altitude would put a VFR aircraft inside Class B/C it has not been
    /// cleared into. AIM 5-5-6.a.3 requires the pilot to advise ATC rather than comply, and 7110.65 7-9-2
    /// NOTE 1 says controllers should expect that call; the counter-offer names the altitude the pilot will
    /// hold instead. Output:
    /// <c>"[N123AB] unable 3,500, that'd put us in the bravo — we can do 2,000."</c>
    /// </summary>
    public static PilotSpeechText BuildUnableAirspaceAltitude(AircraftState aircraft, int assignedFt, int levelOffFt, string airspaceName)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"unable {PhraseologyVerbalizer.CompactAltitude(assignedFt)}, that'd put us in the {airspaceName} — "
                + $"we can do {PhraseologyVerbalizer.CompactAltitude(levelOffFt)}.",
            $"{spoken}, unable {PhraseologyVerbalizer.AltitudeWords(assignedFt)}, that'd put us in the {airspaceName}; "
                + $"we can do {PhraseologyVerbalizer.AltitudeWords(levelOffFt)}."
        );
    }

    /// <summary>The words of a "through" Class B request, which a later implied clearance reads back as "cleared through".</summary>
    public const string BravoThroughRequest = "request clearance through the bravo";

    private static string BravoPreposition(BravoClearanceWording wording) => wording == BravoClearanceWording.Into ? "into" : "through";

    /// <summary>
    /// A VFR pilot's call for a Class B clearance before reaching the boundary: a VFR aircraft needs one to enter Class B
    /// (7110.65 §7-9-2.a). "Into" when the flight plan's destination is inside that Class B, "through" when the aircraft
    /// only crosses it. Output: <c>"request clearance through the bravo."</c> /
    /// <c>"november one two three alpha bravo, request clearance through the bravo."</c>
    /// </summary>
    public static PilotSpeechText BuildBravoClearanceRequest(AircraftState aircraft, BravoClearanceWording wording)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string request = wording == BravoClearanceWording.Into ? "request clearance into the bravo" : BravoThroughRequest;
        return new PilotSpeechText($"{request}.", NormalizeForTts($"{spoken}, {request}."));
    }

    /// <summary>
    /// A readback of an instruction the pilot took as its Class B clearance, saying so: the terminal form gains
    /// <c>", cleared into the bravo"</c> (or <c>"through"</c>) at the end (<c>"fly heading 090, cleared into the bravo"</c>),
    /// the spoken form gains it ahead of the closing callsign (<c>"fly heading zero nine zero, cleared into the bravo,
    /// november …"</c>), or at the end when the readback does not close with the callsign.
    /// </summary>
    public static PilotSpeechText WithBravoClearance(
        AircraftState aircraft,
        PilotSpeechText readback,
        BravoClearanceWording wording,
        PilotSpeechContext speechContext
    )
    {
        string clause = $", cleared {BravoPreposition(wording)} the bravo";
        string callsignTail = NormalizeForTts($", {SpokenReadbackCallsign(aircraft, speechContext)}.");
        string tts = WithClauseBeforeCallsign(readback.Tts, clause, callsignTail);
        return readback with
        {
            Terminal = readback.Terminal + clause,
            Tts = tts,
            RpoTerminal = readback.RpoTerminal is null ? null : readback.RpoTerminal + clause,
        };
    }

    /// <summary>
    /// <paramref name="tts"/> with <paramref name="clause"/> spoken right before its last callsign. When only a closing
    /// courtesy (", thanks") follows that callsign, the courtesy gives way and the readback closes with the callsign, so the
    /// airspace clearance is read back last (F12); any other words after the callsign are kept after it. With no callsign
    /// found, the clause closes the readback.
    /// </summary>
    private static string WithClauseBeforeCallsign(string tts, string clause, string callsignTail)
    {
        string callsign = callsignTail[..^1];
        int callsignAt = tts.LastIndexOf(callsign, StringComparison.Ordinal);
        if (callsignAt < 0)
        {
            return tts.TrimEnd('.') + clause + ".";
        }

        string remainder = tts[(callsignAt + callsign.Length)..];
        bool courtesyOnly = remainder is "." or "" or ", thanks." or ", thanks";
        return tts[..callsignAt] + clause + (courtesyOnly ? callsignTail : callsign + remainder);
    }

    /// <summary>
    /// Pilot report that it cannot make the exit at <paramref name="taxiway"/> the controller named (P/CG UNABLE): the
    /// rollout gave the exit up rather than brake harder than its firm rate for it.
    /// </summary>
    public static PilotSpeechText BuildUnableToExit(AircraftState aircraft, string taxiway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText($"unable {taxiway}.", $"{spoken}, unable {PhraseologyVerbalizer.SpellTaxiway(taxiway)}.");
    }

    /// <summary>
    /// Pilot report that the exit at <paramref name="taxiway"/> the controller named is not ahead of it on this runway — rolled
    /// past, or a taxiway that never touches the runway. The terminal line is the one <see cref="BuildUnable"/> makes of the
    /// controller-facing "Unable, no {taxiway} ahead"; the spoken form spells the taxiway (AIM 4-2-7).
    /// </summary>
    public static PilotSpeechText BuildUnableNoExitAhead(AircraftState aircraft, string taxiway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText($"unable, no {taxiway} ahead.", $"{spoken}, unable, no {PhraseologyVerbalizer.SpellTaxiway(taxiway)} ahead.");
    }

    /// <summary>
    /// A jet's refusal of a taxi clearance that would have it turn about on <paramref name="taxiway"/>. The terminal line is
    /// the one <see cref="BuildUnable"/> makes of the controller-facing
    /// <see cref="Commands.GroundCommandHandler.NoRoomToTurnAroundReason"/>; without a taxiway (the aircraft stands on none)
    /// the "on" clause is left out. The spoken form spells the taxiway (AIM 4-2-7).
    /// </summary>
    public static PilotSpeechText BuildUnableNoRoomToTurnAround(AircraftState aircraft, string? taxiway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string onTaxiway = (taxiway is null) ? "" : $" on {taxiway}";
        string onSpokenTaxiway = (taxiway is null) ? "" : $" on {PhraseologyVerbalizer.SpellTaxiway(taxiway)}";
        return new PilotSpeechText(
            $"unable, no room to turn around{onTaxiway}, request a route ahead.",
            $"{spoken}, unable, no room to turn around{onSpokenTaxiway}, request a route ahead."
        );
    }

    /// <summary>
    /// The refusal of a taxi clearance naming <paramref name="fromTaxiway"/> then <paramref name="toTaxiway"/>, two taxiways that
    /// meet nowhere the aircraft may join them: it stays put and asks for taxi instructions. The terminal line is the
    /// controller-facing <see cref="SegmentExpander.TaxiwaysDoNotConnectReason"/>; the spoken form spells
    /// both taxiways (AIM 4-2-7).
    /// </summary>
    public static PilotSpeechText BuildUnableTaxiwaysDoNotConnect(AircraftState aircraft, string fromTaxiway, string toTaxiway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string from = PhraseologyVerbalizer.SpellTaxiway(fromTaxiway);
        string to = PhraseologyVerbalizer.SpellTaxiway(toTaxiway);
        return new PilotSpeechText(
            SegmentExpander.TaxiwaysDoNotConnectReason(fromTaxiway, toTaxiway),
            $"{spoken}, unable, {from} and {to} don't connect, request taxi"
        );
    }

    /// <summary>
    /// The refusal of a taxi clearance naming <paramref name="fromTaxiway"/> then <paramref name="toTaxiway"/>, two taxiways that
    /// meet only where the route cannot reach ahead of the aircraft: it stays put and asks for taxi instructions. The terminal
    /// line is the controller-facing <see cref="SegmentExpander.NoRouteAheadReason"/>; the spoken form spells both taxiways
    /// (AIM 4-2-7) and, with <paramref name="intersectionOnlyBehind"/>, says the intersection is behind and would take a turn-about.
    /// </summary>
    public static PilotSpeechText BuildUnableNoRouteAhead(AircraftState aircraft, string fromTaxiway, string toTaxiway, bool intersectionOnlyBehind)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string from = PhraseologyVerbalizer.SpellTaxiway(fromTaxiway);
        string to = PhraseologyVerbalizer.SpellTaxiway(toTaxiway);
        string behind = intersectionOnlyBehind ? ", the intersection is behind us, we'd have to turn around" : "";
        return new PilotSpeechText(
            SegmentExpander.NoRouteAheadReason(fromTaxiway, toTaxiway, intersectionOnlyBehind),
            $"{spoken}, unable, no route from {from} onto {to} ahead{behind}, request taxi"
        );
    }

    /// <summary>
    /// The refusal of a taxi clearance naming <paramref name="fromTaxiway"/> then <paramref name="toTaxiway"/>, a crossing the
    /// airport declares that this aircraft cannot turn onto: the turn from the runway onto the second taxiway exceeds its
    /// type's turn limit (KCOS A2 onto C2, a 150° reversal onto a high-speed exit, for a B738). It stays put and asks for taxi
    /// instructions. The terminal line is the controller-facing <see cref="SegmentExpander.CannotMakeTurnReason"/>; the spoken
    /// form spells the taxiway turned onto (AIM 4-2-7).
    /// </summary>
    public static PilotSpeechText BuildUnableCannotMakeTurn(AircraftState aircraft, string fromTaxiway, string toTaxiway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string to = PhraseologyVerbalizer.SpellTaxiway(toTaxiway);
        return new PilotSpeechText(
            SegmentExpander.CannotMakeTurnReason(fromTaxiway, toTaxiway),
            $"{spoken}, unable the turn onto {to}, request taxi instructions"
        );
    }

    /// <summary>
    /// The refusal of a bare taxi clearance across <paramref name="runway"/> onto <paramref name="taxiway"/> when the taxiway
    /// has no room straight ahead for the whole aircraft to clear the runway's holding position: it stays put, says it is
    /// unable (P/CG "UNABLE") and asks for taxi instructions (AIM 4-3-18.d.3). The terminal line is the controller-facing
    /// <see cref="SegmentExpander.NoRoomToClearRunwayReason"/>; the spoken form spells the taxiway (AIM 4-2-7).
    /// </summary>
    public static PilotSpeechText BuildUnableNoRoomToClearRunway(AircraftState aircraft, string taxiway, string runway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string onto = PhraseologyVerbalizer.SpellTaxiway(taxiway);
        return new PilotSpeechText(
            SegmentExpander.NoRoomToClearRunwayReason(taxiway, runway),
            $"{spoken}, unable, no room on {onto} to clear the runway, request taxi instructions"
        );
    }

    /// <summary>
    /// The refusal of an aircraft whose turn about on <paramref name="taxiway"/> would carry some part of it past
    /// <paramref name="runway"/>'s hold line: it holds short of the runway instead (AIM 2-3-5.a.1). Without a taxiway (the
    /// aircraft stands on none) the "on" clause is left out. The spoken form spells the taxiway (AIM 4-2-7) and the runway.
    /// </summary>
    public static PilotSpeechText BuildUnableNoRoomToTurnAroundAtRunway(AircraftState aircraft, string? taxiway, string runway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string onTaxiway = (taxiway is null) ? "" : $" on {taxiway}";
        string onSpokenTaxiway = (taxiway is null) ? "" : $" on {PhraseologyVerbalizer.SpellTaxiway(taxiway)}";
        return new PilotSpeechText(
            $"unable, no room to turn around{onTaxiway}, holding short of runway {runway}.",
            $"{spoken}, unable, no room to turn around{onSpokenTaxiway}, holding short of runway {PhraseologyVerbalizer.SpellRunway(runway)}."
        );
    }

    /// <summary>
    /// Pilot report of an aircraft stopped on the runway with no exit ahead: it cannot turn around on the runway without
    /// ATC approval (AIM 4-3-21.a), so it asks for a back-taxi to <paramref name="exitBehind"/>, the nearest exit behind
    /// it, or says only that it cannot exit when there is none.
    /// </summary>
    public static PilotSpeechText BuildUnableToExitRequestBackTaxi(AircraftState aircraft, string? exitBehind)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string report = exitBehind is { } taxiway ? $"unable to exit, request back-taxi to {taxiway}." : "unable to exit the runway.";
        return new PilotSpeechText(report, $"{spoken}, {report}");
    }

    /// <summary>
    /// Pilot transmission when the aircraft is breaking off a follow because separation can't
    /// be maintained relative to the lead. Pilot side; controller will likely re-sequence. Spoken
    /// and solo terminal forms omit the lead callsign; the RPO terminal names it as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildUnableToMaintainSeparation(AircraftState aircraft, string leadCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            "unable to maintain separation, breaking off the follow.",
            $"{spoken}, unable to maintain separation, breaking off the follow."
        )
        {
            RpoTerminal = $"unable to maintain separation from {leadCallsign}, breaking off the follow.",
        };
    }

    /// <summary>
    /// Pilot transmission when a taxi follow (<c>FOLLOWG</c>) finds no taxi route onto its traffic's path: the follower holds
    /// in position and asks for taxi instructions. Spoken and solo terminal forms say "traffic"; the RPO terminal names the
    /// lead as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildUnableNoRouteToFollow(AircraftState aircraft, string leadCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            "Unable to follow traffic, no taxi route to its path, request taxi instructions",
            $"{spoken}, unable, no route to follow traffic, request taxi."
        )
        {
            RpoTerminal = $"Unable to follow {leadCallsign}, no taxi route to its path, request taxi instructions",
        };
    }

    /// <summary>
    /// Pilot transmission when its clearing route ran out before its tail was past the hold line: the aircraft is holding short
    /// of <paramref name="nextRunway"/> while still not clear of <paramref name="clearedRunway"/>. Both are a single runway end
    /// as the hold-short texts name them — the end nearest the aircraft
    /// (<see cref="Yaat.Sim.Data.Airport.RunwayCrossingEnd.Nearest"/>) — spoken spelled and terminal compacted as those texts do.
    /// </summary>
    public static PilotSpeechText BuildHoldingShortNotClear(AircraftState aircraft, string nextRunway, string clearedRunway)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string next = PhraseologyVerbalizer.CompactRunway(nextRunway);
        string cleared = PhraseologyVerbalizer.CompactRunway(clearedRunway);
        string nextSpoken = PhraseologyVerbalizer.SpellRunway(nextRunway);
        string clearedSpoken = PhraseologyVerbalizer.SpellRunway(clearedRunway);
        return new PilotSpeechText(
            $"Holding short of runway {next}, not clear of runway {cleared}",
            $"{spoken}, holding short of runway {nextSpoken}, not clear of {clearedSpoken}."
        );
    }

    /// <summary>
    /// Pilot transmission when its clearing route ran out before its tail was past the hold line and no runway lies ahead of
    /// it: the aircraft is holding in position while still not clear of <paramref name="clearedRunway"/>, a single runway end as
    /// the hold-short texts name them — the end nearest the aircraft
    /// (<see cref="Yaat.Sim.Data.Airport.RunwayCrossingEnd.Nearest"/>) — spoken spelled and terminal compacted as those texts do.
    /// </summary>
    public static PilotSpeechText BuildHoldingPositionNotClear(AircraftState aircraft, string clearedRunway) =>
        BuildHoldingPositionNotClear(aircraft, clearedRunway, reason: null);

    /// <summary>
    /// The not-clear call (<see cref="BuildHoldingPositionNotClear(AircraftState, string)"/>) of an aircraft that has come to rest
    /// mid-crossing behind traffic with part of it still inside the far holding-position marking (AIM 2-3-5.a): "Holding position, not
    /// clear of runway 28L, traffic ahead". Every form says "traffic", never the traffic's callsign.
    /// </summary>
    public static PilotSpeechText BuildHoldingPositionNotClearTrafficAhead(AircraftState aircraft, string clearedRunway) =>
        BuildHoldingPositionNotClear(aircraft, clearedRunway, "traffic ahead");

    private static PilotSpeechText BuildHoldingPositionNotClear(AircraftState aircraft, string clearedRunway, string? reason)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string cleared = PhraseologyVerbalizer.CompactRunway(clearedRunway);
        string clearedSpoken = PhraseologyVerbalizer.SpellRunway(clearedRunway);
        string because = reason is null ? "" : $", {reason}";
        return new PilotSpeechText(
            $"Holding position, not clear of runway {cleared}{because}",
            $"{spoken}, holding position, not clear of {clearedSpoken}{because}."
        );
    }

    /// <summary>
    /// Pilot transmission when the lead is lost with no legal way back onto the follower's assigned route: it holds in position
    /// and asks for taxi instructions. Spoken and solo terminal forms say "traffic"; the RPO terminal names the lead as a
    /// diagnostic.
    /// </summary>
    public static PilotSpeechText BuildLostTrafficHolding(AircraftState aircraft, string leadCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            "Lost sight of traffic, holding position, request taxi instructions",
            $"{spoken}, lost the traffic, holding, request taxi."
        )
        {
            RpoTerminal = $"Lost sight of {leadCallsign}, holding position, request taxi instructions",
        };
    }

    /// <summary>
    /// Pilot advisory when a following aircraft has extended its current pattern leg
    /// (<paramref name="legWord"/> = "upwind", "crosswind", or "downwind") to the maximum
    /// follow-extension distance and still cannot turn without cutting off the traffic it was told
    /// to follow. A follower does not turn on its own to resolve this — it keeps flying the leg and
    /// cues the controller for a turn / re-sequence (AIM 5-5-12.a.1 / 4-3-5: the follower maneuvers
    /// as necessary and advises ATC). Spoken and solo terminal forms say "the traffic"; the RPO
    /// terminal names the lead as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildFollowExtendingUnableToTurn(AircraftState aircraft, string targetCallsign, string legWord)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            $"extending {legWord} behind the traffic, unable to turn — request instructions.",
            $"{spoken}, extending {legWord} behind the traffic, unable to turn — request instructions."
        )
        {
            RpoTerminal = $"extending {legWord} behind {targetCallsign}, unable to turn — request instructions.",
        };
    }

    /// <summary>
    /// Pilot advisory when a follower that extended its leg past the traffic's base turn to build spacing has run out of room
    /// (the extension limit, or the leg nearing the final) without the spacing: it gives up the follow, keeps extending and
    /// asks for a base turn (AIM 5-5-12.a.2 — advise ATC when unable). Reason first, then the request. Spoken and solo
    /// terminal forms say "the traffic"; the RPO terminal names the lead as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildUnableToFollowExtendingDownwind(AircraftState aircraft, string targetCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            "unable to follow the traffic, extending downwind, request base turn.",
            $"{spoken}, unable to follow the traffic, extending downwind, request base turn."
        )
        {
            RpoTerminal = $"unable to follow {targetCallsign}, extending downwind, request base turn.",
        };
    }

    /// <summary>
    /// Pilot response to a commanded pattern size below the aircraft's turn-radius minimum
    /// (<see cref="Phases.PatternGeometry.MinFlyablePatternSizeNm"/>): the pattern is flown at
    /// that minimum instead, and the pilot says so rather than silently ignoring the instruction
    /// (AIM 4-4-1.b — the pilot informs ATC when unable; AIM 4-3-5 — no unexpected maneuvers in
    /// the pattern). The RPO terminal carries the numeric substitution as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildUnablePatternSize(AircraftState aircraft, double requestedNm, double floorNm)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        string miles = SpokenPatternMiles(floorNm);
        return new PilotSpeechText(
            $"unable, can't turn it that tight — we'll fly about a {miles} pattern.",
            $"{spoken}, unable, can't turn it that tight, we'll fly about a {miles} pattern."
        )
        {
            RpoTerminal = $"unable {requestedNm:G} NM pattern, flying {floorNm:F1} NM for turn radius.",
        };
    }

    /// <summary>
    /// Colloquial spoken form of a pattern width for the unable readback — pilots say halves of
    /// miles, never decimals ("one point one" is TTS poison; see docs/pilot-phraseology.md).
    /// </summary>
    private static string SpokenPatternMiles(double nm)
    {
        double half = Math.Round(nm * 2.0) / 2.0;
        return half switch
        {
            <= 0.5 => "half-mile",
            1.0 => "one mile",
            1.5 => "mile and a half",
            _ => half == Math.Floor(half) ? $"{half:F0} mile" : $"{Math.Floor(half):F0} and a half mile",
        };
    }

    /// <summary>
    /// Pilot advisory when a follower self-initiates a shallow S-turn on final to open in-trail
    /// spacing behind the traffic it is following (AIM 4-3-5 — pilots maneuvering for spacing
    /// advise the controller). Spoken and solo terminal forms say "the traffic"; the RPO terminal
    /// names the lead as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildSTurnsForSpacing(AircraftState aircraft, string targetCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText("S-turning for spacing behind the traffic.", $"{spoken}, S-turning for spacing behind the traffic.")
        {
            RpoTerminal = $"S-turning for spacing behind {targetCallsign}.",
        };
    }

    /// <summary>
    /// Pilot advisory when a follower finds itself level with or ahead of the traffic it is following on base or final (or
    /// stuck alongside it, unable to fall behind): it turns out to the downwind heading to let the traffic pass and asks for a
    /// base turn behind it (AIM 4-3-5 — pilots maneuvering for spacing advise the controller; AIM 4-3-4 — no cutting in on
    /// final). The maneuver first, then the request; the traffic may still be ahead of the follower when it turns out, so the
    /// call says only that the follower will be behind it. Spoken and solo terminal forms say "the traffic"; the RPO terminal
    /// names the lead as a diagnostic.
    /// </summary>
    public static PilotSpeechText BuildTurningDownwindForSpacing(AircraftState aircraft, string targetCallsign)
    {
        string spoken = SpokenOwnCallsign(aircraft);
        return new PilotSpeechText(
            "turning downwind for spacing behind the traffic, request base turn.",
            $"{spoken}, turning downwind for spacing behind the traffic, request base turn."
        )
        {
            RpoTerminal = $"turning downwind for spacing behind {targetCallsign}, request base turn.",
        };
    }

    /// <summary>
    /// Pilot airborne-spawn check-in fired by <see cref="PilotProactive.TickAirborneCheckIn"/>
    /// the first tick an aircraft is observed airborne in solo-training mode and has not
    /// yet spoken to ATC. Branches on <see cref="SimScenarioState.StudentPositionType"/>
    /// (TWR / APP / CTR) × <see cref="AircraftFlightPlan.IsVfr"/> × VFR intent
    /// (inbound / transit / no-destination). Returns <see langword="null"/> when the
    /// student position is GND, null, or unrecognized.
    /// </summary>
    public static PilotSpeechText? BuildAirborneCheckIn(AircraftState aircraft, SimScenarioState scenario, LatLon primaryAirportPosition)
    {
        string? positionType = scenario.StudentPositionType;
        if (string.IsNullOrEmpty(positionType))
        {
            return null;
        }

        string fallbackFacility = positionType switch
        {
            "TWR" => "tower",
            "APP" => "approach",
            "CTR" => "center",
            _ => "",
        };
        if (fallbackFacility.Length == 0)
        {
            return null;
        }

        string facilityCallName = ResolveStudentFacilityCallName(scenario, positionType, fallbackFacility);
        string spoken = SpokenOwnCallsign(aircraft);
        string? atisLetter = ResolvePrimaryFieldAtisLetter(scenario);
        // Pilots state altitudes to the nearest 100 ft — there is no phraseology for tens/units.
        // Rounding here also makes the FL gate (% 100 == 0) pass for every airborne check-in.
        int altitudeFt = (int)(Math.Round(aircraft.Altitude / 100.0) * 100);

        if (aircraft.FlightPlan.IsVfr)
        {
            return BuildVfrAirborne(aircraft, scenario, primaryAirportPosition, positionType, facilityCallName, spoken, altitudeFt, atisLetter);
        }
        return BuildIfrAirborne(aircraft, positionType, facilityCallName, spoken, altitudeFt, atisLetter);
    }

    // Climb/descent vs level deadband for the check-in vertical-state verb — below this the
    // aircraft is reported "level" rather than "leaving X climbing/descending".
    private const double VerticalTrendThresholdFt = 200;

    private static PilotSpeechText BuildIfrAirborne(
        AircraftState aircraft,
        string positionType,
        string facilityCallName,
        string spoken,
        int altitudeFt,
        string? atisLetter
    )
    {
        string callsign = TerminalOwnCallsign(aircraft);
        string info = AtisInfoClause(atisLetter);
        if (positionType == "TWR")
        {
            // Direct-to-tower IFR is rare. Mention destination runway if known; otherwise drop the runway clause.
            string? rwy = aircraft.Procedure.DestinationRunway;
            string rwyClauseTts = !string.IsNullOrEmpty(rwy) ? ", runway " + PhraseologyVerbalizer.SpellRunway(rwy) : "";
            string rwyClauseTerm = !string.IsNullOrEmpty(rwy) ? ", runway " + PhraseologyVerbalizer.CompactRunway(rwy) : "";
            return new PilotSpeechText($"{facilityCallName}, {callsign}{rwyClauseTerm}{info}.", $"{facilityCallName}, {spoken}{rwyClauseTts}{info}.");
        }

        (string? clauseTerm, string? clauseTts, bool isDeparture) = BuildVerticalStateClause(aircraft, altitudeFt);
        bool isFlightLevel = altitudeFt >= 18000;
        // Departures (climbing / climb-via) reference the departure ATIS via the tower, not arrival
        // ATIS — drop the suffix. Class A enroute (CTR ≥ FL180) has no airport ATIS reference.
        bool dropAtis = isDeparture || (positionType == "CTR" && isFlightLevel);
        string atisSuffix = dropAtis ? "" : info;

        string clauseTermPart = clauseTerm.Length == 0 ? "" : ", " + clauseTerm;
        string clauseTtsPart = clauseTts.Length == 0 ? "" : ", " + clauseTts;

        return new PilotSpeechText(
            $"{facilityCallName}, {callsign}{clauseTermPart}{atisSuffix}.",
            $"{facilityCallName}, {spoken}{clauseTtsPart}{atisSuffix}."
        );
    }

    /// <summary>
    /// Builds the vertical-state clause for an IFR airborne check-in. AIM 5-3-1.b.2.a gives the
    /// "level" / "leaving (present) climbing|descending (assigned)" forms; descend-via / climb-via
    /// (AIM 5-4-1.b.2 / 5-2-9.b.9) name the STAR/SID. Returns the terminal + TTS clause and whether
    /// the aircraft is departing (climbing / climb-via) so the caller can drop the arrival ATIS.
    /// Empty clause when the altitude is sub-reportable (rounds below 100 ft).
    /// </summary>
    private static (string Term, string Tts, bool IsDeparture) BuildVerticalStateClause(AircraftState aircraft, int altitudeFt)
    {
        string altTerm = PhraseologyVerbalizer.CompactAltitude(altitudeFt);
        string altTts = AtcNumberParser.AltitudeToWords(altitudeFt);
        if (altTerm.Length == 0 || altTts.Length == 0)
        {
            return ("", "", false);
        }

        if (aircraft.Procedure.StarViaMode && !string.IsNullOrEmpty(aircraft.Procedure.ActiveStarId))
        {
            return BuildViaClause(aircraft, altTerm, altTts, aircraft.Procedure.ActiveStarId, "descending via", "arrival", isDeparture: false);
        }
        if (aircraft.Procedure.SidViaMode && !string.IsNullOrEmpty(aircraft.Procedure.ActiveSidId))
        {
            return BuildViaClause(aircraft, altTerm, altTts, aircraft.Procedure.ActiveSidId, "climbing via", "departure", isDeparture: true);
        }

        double? trend = aircraft.Targets.AssignedAltitude ?? aircraft.Targets.TargetAltitude;
        if (trend is { } target && Math.Abs(target - altitudeFt) > VerticalTrendThresholdFt)
        {
            bool climbing = target > altitudeFt;
            string verb = climbing ? "climbing" : "descending";
            (string? toTerm, string? toTts) = AltitudeClause(aircraft.Targets.AssignedAltitude);
            return ($"leaving {altTerm} {verb}{toTerm}", $"leaving {altTts} {verb}{toTts}", climbing);
        }

        // Level. FL form already reads as a maintained level, so the "level" verb is dropped above FL180.
        return altitudeFt >= 18000 ? (altTerm, altTts, false) : ($"level {altTerm}", $"level {altTts}", false);
    }

    private static (string Term, string Tts, bool IsDeparture) BuildViaClause(
        AircraftState aircraft,
        string altTerm,
        string altTts,
        string procedureId,
        string verb,
        string procType,
        bool isDeparture
    )
    {
        (string? procTerm, string? procTts) = PhraseologyVerbalizer.ProcedureName(procedureId);
        // "for (altitude)" names an altitude ATC assigned (AIM 5-3-1.b.2.a). Under a climb via that is its "except maintain"
        // altitude, from CVIA nnn or the TDLS climb-via text, never a published top (AIM 5-2-9.h.9 and initial contact
        // Example 2); otherwise it is the assigned altitude.
        bool climbVia = isDeparture && (aircraft.Procedure.ClimbViaTargetFt is not null);
        double? forAltitude = climbVia ? aircraft.Procedure.SidViaCeiling : aircraft.Targets.AssignedAltitude;
        (string? forTerm, string? forTts) = AltitudeClause(forAltitude);
        string leadTerm = forTerm.Length == 0 ? $"leaving {altTerm}" : $"leaving {altTerm} for{forTerm}";
        string leadTts = forTts.Length == 0 ? $"leaving {altTts}" : $"leaving {altTts} for{forTts}";
        return ($"{leadTerm}, {verb} the {procTerm} {procType}", $"{leadTts}, {verb} the {procTts} {procType}", isDeparture);
    }

    /// <summary>
    /// The " (altitude)" suffix naming the cleared altitude for a climb/descent check-in (AIM 5-3-1.b.2.a
    /// "climbing to|descending to (altitude)"). Empty when <paramref name="altitude"/> is null (no discrete altitude
    /// cleared, e.g. a descend-via aircraft following published crossings).
    /// </summary>
    private static (string Term, string Tts) AltitudeClause(double? altitude)
    {
        if (altitude is not { } assigned)
        {
            return ("", "");
        }
        int rounded = (int)(Math.Round(assigned / 100.0) * 100);
        string term = PhraseologyVerbalizer.CompactAltitude(rounded);
        string tts = AtcNumberParser.AltitudeToWords(rounded);
        if (term.Length == 0 || tts.Length == 0)
        {
            return ("", "");
        }
        return ($" {term}", $" {tts}");
    }

    private static PilotSpeechText BuildVfrAirborne(
        AircraftState aircraft,
        SimScenarioState scenario,
        LatLon primaryAirportPosition,
        string positionType,
        string facilityCallName,
        string spoken,
        int altitudeFt,
        string? atisLetter
    )
    {
        string callsignTerm = TerminalOwnCallsign(aircraft);
        double distNm = GeoMath.DistanceNm(primaryAirportPosition, aircraft.Position);
        int distMiles = Math.Max(1, (int)Math.Round(distNm));
        double bearingFromAirport = GeoMath.BearingTo(primaryAirportPosition, aircraft.Position);
        string direction = BearingToCardinal8(bearingFromAirport);
        string altitudeWords = AtcNumberParser.AltitudeToWords(altitudeFt);
        string altitudeCompact = PhraseologyVerbalizer.CompactAltitude(altitudeFt);
        string distWords = SpellDistanceDigits(distMiles);
        string distTerm = distMiles.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string dest = aircraft.FlightPlan.Destination;
        string primary = scenario.PrimaryAirportId ?? "";
        bool noDest = string.IsNullOrEmpty(dest);
        // AirportIdsMatch canonicalizes both sides so "KOAK" matches "OAK". A plain
        // case-insensitive Equals would route a real KOAK-bound VFR through the
        // transit phrasing because the K-prefix never matches the bare FAA id.
        bool inbound = !noDest && NavigationDatabase.AirportIdsMatch(dest, primary);

        // Sub-100 ft altitudes render as empty — drop the "at {altitude}" clause rather
        // than emit a dangling "at , ". Pilots who just lifted off a low-elevation field
        // (or who otherwise read airborne while still in ground effect) shouldn't break the
        // sentence shape.
        string atAltTts = string.IsNullOrEmpty(altitudeWords) ? "" : " at " + altitudeWords;
        string atAltTerm = string.IsNullOrEmpty(altitudeCompact) ? "" : " at " + altitudeCompact;

        if (noDest)
        {
            // AIM 4-3-1 form: "VFR [Xbound] at [altitude]" is the canonical activity phrase. Stating
            // position with an explicit "of the field"/"of [airport]" anchor avoids the bare-direction
            // adjacency that would read as contradicting the heading-of-flight.
            string heading = HeadingToBoundCardinal(aircraft.TrueHeading.Degrees);
            string positionAnchor = positionType == "TWR" ? "the field" : PhraseologyVerbalizer.SpellAirportName(scenario.PrimaryAirportId ?? "");
            return new PilotSpeechText(
                $"{facilityCallName}, {callsignTerm}, {distTerm} miles {direction} of {positionAnchor}, VFR {heading}{atAltTerm}.",
                $"{facilityCallName}, {spoken} {distWords} miles {direction} of {positionAnchor}, VFR {heading}{atAltTts}."
            );
        }

        string intent;
        if (inbound)
        {
            intent = positionType switch
            {
                "TWR" => "inbound for landing",
                "APP" => "request landing",
                // Center doesn't naturally handle landings — fall back to transit phrasing.
                _ => "request transition",
            };
        }
        else
        {
            intent = "request transition";
        }

        if (positionType == "CTR")
        {
            string airportSpoken = PhraseologyVerbalizer.SpellAirportName(scenario.PrimaryAirportId ?? "");
            return new PilotSpeechText(
                $"{facilityCallName}, {callsignTerm}{atAltTerm}, {distTerm} miles {direction} of {airportSpoken}, {intent}.",
                $"{facilityCallName}, {spoken}{atAltTts}, {distWords} miles {direction} of {airportSpoken}, {intent}."
            );
        }

        string atisSuffix = inbound ? AtisInfoClause(atisLetter) : "";
        return new PilotSpeechText(
            $"{facilityCallName}, {callsignTerm} {distTerm} miles {direction}{atAltTerm}, {intent}{atisSuffix}.",
            $"{facilityCallName}, {spoken} {distWords} miles {direction}{atAltTts}, {intent}{atisSuffix}."
        );
    }

    public static string? ResolveStudentRadioName(SimScenarioState? scenario)
    {
        if (scenario?.StudentPosition?.Callsign is not { Length: > 0 } callsign)
        {
            return null;
        }

        string? radioName = scenario.ArtccConfig?.FindPositionByCallsign(callsign, facilityHint: null)?.RadioName;
        return string.IsNullOrWhiteSpace(radioName) ? null : radioName.Trim();
    }

    public static string ResolveStudentFacilityCallName(SimScenarioState? scenario, string positionType, string fallbackFacility)
    {
        string? radioName = ResolveStudentRadioName(scenario);
        if (string.IsNullOrWhiteSpace(radioName))
        {
            return CleanFacilityCallName(fallbackFacility, fallbackFacility);
        }

        if (!PositionTypeMatchesFacility(positionType, fallbackFacility))
        {
            return CleanFacilityCallName(fallbackFacility, fallbackFacility);
        }

        return CleanFacilityCallName(radioName, fallbackFacility);
    }

    /// <summary>
    /// How the pilot addresses the position that answers it: the position's vNAS radio name when it is the
    /// <paramref name="expectedPositionType"/> ("Oakland Ground"), else the generic facility word — a parked aircraft
    /// still says "ground" when a tower-only student answers everything.
    /// </summary>
    public static string ResolveAnsweringCallName(PilotAnsweringPosition answering, string expectedPositionType, string fallbackFacility) =>
        ResolveContextFacilityCallName(answering.PositionType, answering.RadioName, expectedPositionType, fallbackFacility);

    public static string ResolveContextFacilityCallName(
        string? studentPositionType,
        string? studentRadioName,
        string expectedPositionType,
        string fallbackFacility
    )
    {
        if (
            !string.Equals(studentPositionType, expectedPositionType, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(studentRadioName)
        )
        {
            return CleanFacilityCallName(fallbackFacility, fallbackFacility);
        }

        return CleanFacilityCallName(studentRadioName, fallbackFacility);
    }

    private static bool PositionTypeMatchesFacility(string positionType, string fallbackFacility) =>
        (positionType, fallbackFacility.ToLowerInvariant()) switch
        {
            ("TWR", "tower") => true,
            ("GND", "ground") => true,
            ("APP", "approach") => true,
            ("CTR", "center") => true,
            _ => false,
        };

    private static string CleanFacilityCallName(string? facilityCallName, string fallbackFacility)
    {
        string value = string.IsNullOrWhiteSpace(facilityCallName) ? fallbackFacility : facilityCallName.Trim();
        return value.Length == 0 ? fallbackFacility : value;
    }

    /// <summary>
    /// Quantizes a true bearing to the 8-point compass form pilots speak ("north", "northeast", ...).
    /// Each 45° sector is centered on its cardinal/intercardinal — north covers (337.5°, 22.5°],
    /// northeast covers (22.5°, 67.5°], etc.
    /// </summary>
    internal static string BearingToCardinal8(double bearingDeg)
    {
        double b = ((bearingDeg % 360.0) + 360.0) % 360.0;
        if (b < 22.5 || b >= 337.5)
        {
            return "north";
        }
        if (b < 67.5)
        {
            return "northeast";
        }
        if (b < 112.5)
        {
            return "east";
        }
        if (b < 157.5)
        {
            return "southeast";
        }
        if (b < 202.5)
        {
            return "south";
        }
        if (b < 247.5)
        {
            return "southwest";
        }
        if (b < 292.5)
        {
            return "west";
        }
        return "northwest";
    }

    /// <summary>
    /// Quantizes a true heading to the 4-point compass "Xbound" form ("northbound", "eastbound",
    /// "southbound", "westbound"). Each 90° sector is centered on its cardinal — northbound covers
    /// [315°, 45°), eastbound [45°, 135°), etc.
    /// </summary>
    internal static string HeadingToBoundCardinal(double headingDeg)
    {
        double h = ((headingDeg % 360.0) + 360.0) % 360.0;
        if (h < 45.0 || h >= 315.0)
        {
            return "northbound";
        }
        if (h < 135.0)
        {
            return "eastbound";
        }
        if (h < 225.0)
        {
            return "southbound";
        }
        return "westbound";
    }

    private static string SpellDistanceDigits(int miles)
    {
        if (miles < 0)
        {
            miles = 0;
        }
        if (miles < 10)
        {
            return SingleDigitWord(miles);
        }
        return string.Join(' ', miles.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => SingleDigitWord(c - '0')));
    }

    private static string SingleDigitWord(int digit) =>
        digit switch
        {
            0 => "zero",
            1 => "one",
            2 => "two",
            3 => "three",
            4 => "four",
            5 => "five",
            6 => "six",
            7 => "seven",
            8 => "eight",
            9 => "nine",
            _ => digit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private static string StripBracketedPrefix(AircraftState aircraft, string text)
    {
        string prefix = $"[{aircraft.Callsign}] ";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text;
    }

    public static string PrepareForTts(AircraftState aircraft, string text) => NormalizeForTts(StripBracketedPrefix(aircraft, text));

    /// <summary>
    /// TTS-only spelling fixups inappropriate for terminal display: "x-ray" → "xray" and
    /// de-hyphenating word-joined hyphens ("three-mile" → "three mile").
    /// </summary>
    private static string NormalizeForTts(string text)
    {
        string speech = Regex.Replace(text, @"\bx-ray\b", "xray", RegexOptions.IgnoreCase);
        return Regex.Replace(speech, @"(?<=\w)-(?=\w)", " ");
    }

    /// <summary>
    /// CIFP-style approach id ("I28R", "R28R-Y", "VIS28R") to ATC-spoken form
    /// ("ILS two eight right", "RNAV Yankee two eight right", "visual approach runway two eight right"): the type and its
    /// variant before the runway, as 7110.65 4-8-1 names the approach (<see cref="ApproachReadbackName"/>).
    /// Falls back to NATO-spelled prefix + spoken runway if the prefix isn't recognized.
    /// </summary>
    internal static string SpokenApproachName(string approachId, string runwayId) =>
        ApproachName(approachId, PhraseologyVerbalizer.SpellRunway(runwayId), spoken: true);

    /// <summary>Compact terminal approach name: "ILS 28R", "RNAV Y 28R" (raw variant letter, digit runway).</summary>
    internal static string CompactApproachName(string approachId, string runwayId) =>
        ApproachName(approachId, PhraseologyVerbalizer.CompactRunway(runwayId), spoken: false);

    private static string ApproachName(string approachId, string runwayText, bool spoken)
    {
        if (string.IsNullOrEmpty(approachId))
        {
            return string.Empty;
        }

        if (ApproachReadbackName.TypeAndRunway(approachId) is { } named)
        {
            return $"{(spoken ? named.Spoken : named.Terminal)} {runwayText}";
        }

        int dashIdx = approachId.IndexOf('-');
        string? suffix = dashIdx >= 0 && dashIdx < approachId.Length - 1 ? approachId[(dashIdx + 1)..] : null;
        string head = dashIdx >= 0 ? approachId[..dashIdx] : approachId;

        // Strip the trailing runway portion off the head ("I28R" → prefix "I"; "VIS28R" → prefix "VIS").
        int prefixEnd = 0;
        while (prefixEnd < head.Length && !char.IsDigit(head[prefixEnd]))
        {
            prefixEnd++;
        }

        string prefix = head[..prefixEnd];
        string prefixSpoken = ExpandApproachPrefix(prefix);
        string suffixText = suffix is null
            ? string.Empty
            : " " + (spoken ? string.Join(' ', suffix.Select(c => NatoPhoneticAlphabet.SpellChar(c))) : suffix.ToUpperInvariant());
        return $"{prefixSpoken} {runwayText}{suffixText}";
    }

    /// <summary>The spoken prefix of an approach id <see cref="ApproachReadbackName"/> cannot name: the visual, or the prefix spelled.</summary>
    private static string ExpandApproachPrefix(string prefix) =>
        prefix.ToUpperInvariant() switch
        {
            "VIS" => "visual approach runway",
            "" => string.Empty,
            _ => string.Join(' ', prefix.Select(c => NatoPhoneticAlphabet.SpellChar(c))),
        };

    internal static string? FormatCondition(BlockCondition? condition) =>
        condition switch
        {
            null => null,
            AtFixCondition fix => $"at {PhraseologyVerbalizer.SpellFix(fix.FixName)},",
            LevelCondition level => $"at {PhraseologyVerbalizer.AltitudeWords(level.Altitude)},",
            GiveWayCondition => "behind the traffic,",
            _ => null, // other condition kinds have their own dispatch path.
        };

    /// <summary>Compact terminal form of the block-condition lead-in ("at SUNOL," / "at 5000,").</summary>
    internal static string? FormatConditionTerminal(BlockCondition? condition) =>
        condition switch
        {
            null => null,
            AtFixCondition fix => $"at {PhraseologyVerbalizer.FixDisplayText(fix.FixName)},",
            LevelCondition level => $"at {PhraseologyVerbalizer.CompactAltitude(level.Altitude)},",
            GiveWayCondition => "behind the traffic,",
            _ => null,
        };

    /// <summary>
    /// Lead-in for the RPO terminal body: the terminal lead for every condition except GIVEWAY, whose
    /// diagnostic form names the traffic the pilot must yield to ("behind UAL456,").
    /// </summary>
    internal static string? FormatConditionForRpo(BlockCondition? condition) =>
        condition is GiveWayCondition giveWay ? $"behind {giveWay.TargetCallsign}," : FormatConditionTerminal(condition);
}
