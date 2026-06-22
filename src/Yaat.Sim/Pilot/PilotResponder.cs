using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
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
public static class PilotResponder
{
    public const string SourceResponse = "Response";
    public const string SourceSayReadback = "SayReadback";

    /// <summary>
    /// Solo-relevant student positions for transmissions that are tower-aviated
    /// (clear-of-runway, holding-short ready, lined-up ready, on-final, etc.).
    /// </summary>
    public static readonly IReadOnlyCollection<string> SoloPositionsTower = ["TWR"];

    /// <summary>
    /// Solo-relevant student positions for transmissions controllers handle in either
    /// the tower or terminal area (pattern reports, follow operations, going around).
    /// </summary>
    public static readonly IReadOnlyCollection<string> SoloPositionsTowerApproach = ["TWR", "APP"];

    /// <summary>
    /// Queues a solo-training pilot line for delayed radio transcript and typed pilot-audio
    /// broadcast. Immediate controller responses stay on the Response channel; this queue
    /// represents what the pilot says when the frequency is available.
    /// </summary>
    public static void QueueSoloPilotTransmission(AircraftState aircraft, string text, PilotTransmissionKind kind, string sourceKind)
    {
        // String-form lines (stored follow-ups, legacy callers): the terminal SAY message strips the
        // bracketed callsign prefix (the SAY column carries the callsign); TTS additionally normalizes.
        var terminal = StripBracketedPrefix(aircraft, text);
        aircraft.PendingPilotTransmissions.Add(new PilotTransmission(aircraft.Callsign, terminal, NormalizeForTts(terminal), sourceKind, kind));
    }

    /// <summary>
    /// Queues a solo-training pilot line whose terminal (compact, callsign in the SAY column) and
    /// spoken (phonetic) forms were built independently from the canonical command.
    /// </summary>
    public static void QueueSoloPilotTransmission(AircraftState aircraft, PilotSpeechText text, PilotTransmissionKind kind, string sourceKind)
    {
        aircraft.PendingPilotTransmissions.Add(new PilotTransmission(aircraft.Callsign, text.Terminal, text.Tts, sourceKind, kind));
    }

    /// <summary>
    /// Queues a solo-training SAY-channel readback for delayed radio transcript and typed
    /// pilot-audio broadcast.
    /// </summary>
    public static void QueueSoloPilotReadback(AircraftState aircraft, string text, string sourceKind)
    {
        var terminal = StripBracketedPrefix(aircraft, text);
        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(aircraft.Callsign, terminal, NormalizeForTts(terminal), sourceKind, PilotTransmissionKind.SayReadback)
        );
    }

    /// <summary>
    /// Queues a solo-training SAY-channel readback with independently-built terminal + spoken forms.
    /// </summary>
    public static void QueueSoloPilotReadback(AircraftState aircraft, PilotSpeechText text, string sourceKind)
    {
        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(aircraft.Callsign, text.Terminal, text.Tts, sourceKind, PilotTransmissionKind.SayReadback)
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
        BuildReadback(compound, aircraft, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate);

    public static PilotSpeechText? BuildReadback(
        CompoundCommand compound,
        AircraftState aircraft,
        PilotPersonality personality,
        FrequencyActivityLevel activityLevel
    )
    {
        // Per-block clause lists are joined internally with ", " (parallel commands within
        // a `,`-separated block); blocks themselves are joined with ", then " to mark the
        // `;` (sequential) boundary the controller dictated. Without "then", TTS reads
        // sequential and parallel clauses identically. The terminal (compact) and spoken (TTS)
        // bodies are assembled in lock-step from each clause's two independently-built forms.
        var blockTermTexts = new List<string>();
        var blockTtsTexts = new List<string>();
        foreach (var block in compound.Blocks)
        {
            var termClauses = new List<string>();
            var ttsClauses = new List<string>();
            var termLead = FormatConditionTerminal(block.Condition);
            var ttsLead = FormatCondition(block.Condition);
            foreach (var cmd in block.Commands)
            {
                var clause = VerbalizeForReadback(cmd, aircraft, personality, activityLevel);
                if (clause is null || string.IsNullOrEmpty(clause.Tts))
                {
                    continue;
                }

                termClauses.Add(termLead is null ? clause.Terminal : termLead + " " + clause.Terminal);
                ttsClauses.Add(ttsLead is null ? clause.Tts : ttsLead + " " + clause.Tts);
                termLead = null; // condition is stated once per block
                ttsLead = null;
            }

            if (ttsClauses.Count > 0)
            {
                blockTermTexts.Add(string.Join(", ", termClauses));
                blockTtsTexts.Add(string.Join(", ", ttsClauses));
            }
        }

        if (blockTtsTexts.Count == 0)
        {
            return null;
        }

        var ttsBody = ApplyQuietFlavor(aircraft.Callsign, string.Join(", then ", blockTtsTexts), personality, activityLevel);
        var termBody = string.Join(", then ", blockTermTexts);
        return FrameReadback(aircraft, termBody, ttsBody);
    }

    /// <summary>
    /// Frames a readback into its two delivered forms: the terminal SAY message (compact body, no
    /// callsign — the SAY column carries it) and the spoken TTS line (body + spelled callsign).
    /// </summary>
    private static PilotSpeechText FrameReadback(AircraftState aircraft, string terminalBody, string ttsBody)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText(terminalBody, NormalizeForTts($"{ttsBody}, {spoken}."));
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
        PilotPersonality personality,
        FrequencyActivityLevel activityLevel
    ) =>
        cmd switch
        {
            ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrAbove or AltitudeAssignmentModifier.AtOrBelow } cm =>
                BuildAltitudeRestrictionClause(aircraft, cm),
            LineUpAndWaitCommand luaw => AppendWithoutDelayClause(BuildRunwayInstructionClause(aircraft, "line up and wait"), luaw.WithoutDelay)
                ?? VerbalizeDual(luaw, personality, activityLevel),
            ClearedForTakeoffCommand cto => BuildTakeoffClearanceClause(
                aircraft,
                cto.Immediate ? "cleared for immediate takeoff" : "cleared for takeoff",
                cto.Departure,
                cto.AssignedAltitude,
                includeRunway: true,
                cto.CautionWakeTurbulence
            ),
            ClearedTakeoffPresentCommand ctopp => BuildTakeoffClearanceClause(
                aircraft,
                "cleared for takeoff, present position",
                ctopp.Departure,
                ctopp.AssignedAltitude,
                includeRunway: false,
                cautionWakeTurbulence: false
            ),
            AcknowledgePilotContactCommand => null,
            ClearedToLandCommand cland => AppendWakeAdvisoryClause(
                BuildRunwayInstructionClause(aircraft, "cleared to land", explicitRunwayId: cland.RunwayId),
                cland.CautionWakeTurbulence
            ) ?? VerbalizeDual(cland, personality, activityLevel),
            LandAndHoldShortCommand lahso => BuildLandAndHoldShortClause(aircraft, lahso),
            TouchAndGoCommand tg => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared touch and go", explicitRunwayId: tg.RunwayId)
                    ?? VerbalizeDual(tg, personality, activityLevel),
                tg.TrafficPattern
            ),
            StopAndGoCommand sg => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared stop and go") ?? VerbalizeDual(sg, personality, activityLevel),
                sg.TrafficPattern
            ),
            LowApproachCommand la => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared low approach") ?? VerbalizeDual(la, personality, activityLevel),
                la.TrafficPattern
            ),
            ClearedForOptionCommand option => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared for the option") ?? VerbalizeDual(option, personality, activityLevel),
                option.TrafficPattern
            ),
            ExtendPatternCommand ext => BuildExtendPatternClause(aircraft, ext),
            ReportCommand report => BuildReportWilcoClause(report),
            _ => VerbalizeDual(cmd, personality, activityLevel),
        };

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
            ReportTrigger.MileFinal => Dual(
                $"will report {report.DistanceNm}-mile final",
                $"will report {MilesToWords(report.DistanceNm ?? 0)} mile final"
            ),
            ReportTrigger.AtFix => Dual($"will report passing {report.FixName}", $"will report passing {report.FixName}"),
            _ => null,
        };

    /// <summary>
    /// Default rule-driven clause for commands without a dedicated readback builder: the compact
    /// terminal form and the spoken form, both produced from the canonical command by the verbalizer.
    /// </summary>
    private static PilotSpeechText? VerbalizeDual(ParsedCommand cmd, PilotPersonality personality, FrequencyActivityLevel activityLevel) =>
        Dual(
            PhraseologyVerbalizer.VerbalizeTerminal(cmd, personality, activityLevel),
            PhraseologyVerbalizer.Verbalize(cmd, personality, activityLevel)
        );

    /// <summary>
    /// Builds the EXT readback dynamically from the aircraft's current pattern phase
    /// (because bare `EXT` is parsed as <c>ExtendPatternCommand(Leg: null)</c> — the
    /// resolved leg only becomes known when <see cref="PatternCommandHandler"/> applies
    /// it). Includes the runway when one is assigned so the readback matches the
    /// pattern-entry conventions used by ERD/ELD/MLT/MRT.
    /// </summary>
    private static PilotSpeechText? BuildExtendPatternClause(AircraftState aircraft, ExtendPatternCommand ext)
    {
        var leg = ext.Leg ?? CurrentPatternLeg(aircraft.Phases?.CurrentPhase);
        if (leg is not { } resolved)
        {
            return VerbalizeDual(ext, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate);
        }

        var legWord = resolved switch
        {
            PatternEntryLeg.Upwind => "upwind",
            PatternEntryLeg.Crosswind => "crosswind",
            PatternEntryLeg.Downwind => "downwind",
            _ => null,
        };
        if (legWord is null)
        {
            return VerbalizeDual(ext, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate);
        }

        var runway = aircraft.Procedure.DestinationRunway;
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

    private static string ApplyQuietFlavor(string callsign, string body, PilotPersonality personality, FrequencyActivityLevel activityLevel)
    {
        if (personality != PilotPersonality.Varied || activityLevel != FrequencyActivityLevel.Quiet)
        {
            return body;
        }

        var bucket = StableBucket($"{callsign}|{body}", 100);
        return bucket switch
        {
            < 5 => $"alright, {body}",
            < 10 => $"{body}, thanks",
            _ => body,
        };
    }

    private static int StableBucket(string input, int modulo)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        uint value = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        return (int)(value % modulo);
    }

    private static PilotSpeechText BuildAltitudeRestrictionClause(AircraftState aircraft, ClimbMaintainCommand cmd)
    {
        var vfr = aircraft.FlightPlan.IsVfr ? "VFR " : "";
        var restriction = cmd.Modifier == AltitudeAssignmentModifier.AtOrAbove ? "at or above" : "at or below";
        return new PilotSpeechText(
            $"maintain {vfr}{restriction} {PhraseologyVerbalizer.CompactAltitude(cmd.Altitude)}",
            $"maintain {vfr}{restriction} {PhraseologyVerbalizer.AltitudeWords(cmd.Altitude)}"
        );
    }

    private static PilotSpeechText BuildTakeoffClearanceClause(
        AircraftState aircraft,
        string lead,
        DepartureInstruction departure,
        int? assignedAltitude,
        bool includeRunway,
        bool cautionWakeTurbulence
    )
    {
        var term = new StringBuilder(lead);
        var tts = new StringBuilder(lead);
        var runwayId = ResolveTakeoffRunwayId(aircraft);
        if (includeRunway && !string.IsNullOrWhiteSpace(runwayId))
        {
            term.Append(" runway ").Append(PhraseologyVerbalizer.CompactRunway(runwayId));
            tts.Append(" runway ").Append(PhraseologyVerbalizer.SpellRunway(runwayId));
        }

        var departureClause = BuildDepartureInstructionClause(departure);
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

        if (cautionWakeTurbulence)
        {
            term.Append(", caution wake turbulence");
            tts.Append(", caution wake turbulence");
        }

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

    private static PilotSpeechText? BuildRunwayInstructionClause(AircraftState aircraft, string lead, string? explicitRunwayId = null)
    {
        var runwayId = ResolveRunwayId(aircraft, explicitRunwayId);
        if (string.IsNullOrWhiteSpace(runwayId))
        {
            return null;
        }

        return new PilotSpeechText(
            $"{lead} runway {PhraseologyVerbalizer.CompactRunway(runwayId)}",
            $"{lead} runway {PhraseologyVerbalizer.SpellRunway(runwayId)}"
        );
    }

    private static PilotSpeechText? AppendWakeAdvisoryClause(PilotSpeechText? clause, bool cautionWakeTurbulence)
    {
        if (clause is null || !cautionWakeTurbulence)
        {
            return clause;
        }

        return new PilotSpeechText($"{clause.Terminal}, caution wake turbulence", $"{clause.Tts}, caution wake turbulence");
    }

    private static PilotSpeechText? AppendWithoutDelayClause(PilotSpeechText? clause, bool withoutDelay)
    {
        if (clause is null || !withoutDelay)
        {
            return clause;
        }

        return new PilotSpeechText($"{clause.Terminal}, without delay", $"{clause.Tts}, without delay");
    }

    private static PilotSpeechText BuildLandAndHoldShortClause(AircraftState aircraft, LandAndHoldShortCommand command)
    {
        var holdShortTerm = PhraseologyVerbalizer.CompactRunway(command.CrossingRunwayId);
        var holdShortTts = PhraseologyVerbalizer.SpellRunway(command.CrossingRunwayId);
        var landingClause = BuildRunwayInstructionClause(aircraft, "cleared to land");
        if (landingClause is null)
        {
            return new PilotSpeechText($"cleared to land, hold short runway {holdShortTerm}", $"cleared to land, hold short runway {holdShortTts}");
        }

        return new PilotSpeechText(
            $"{landingClause.Terminal}, hold short runway {holdShortTerm}",
            $"{landingClause.Tts}, hold short runway {holdShortTts}"
        );
    }

    private static PilotSpeechText? AppendTrafficPatternClause(PilotSpeechText? clause, PatternDirection? direction)
    {
        if (clause is null || direction is null)
        {
            return clause;
        }

        var suffix = $", make {PatternDirectionWord(direction.Value)} traffic";
        return new PilotSpeechText(clause.Terminal + suffix, clause.Tts + suffix);
    }

    private static string ResolveRunwayId(AircraftState aircraft, string? explicitRunwayId = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRunwayId))
        {
            return explicitRunwayId;
        }

        return aircraft.Phases?.ClearedRunwayId ?? aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Phases?.ActiveApproach?.RunwayId ?? "";
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
    /// in solo-training mode. Both IFR and VFR aircraft check in. Output:
    /// <c>"[N123AB] ground, november one two three alpha bravo at the ramp, with information Alpha, ready to taxi."</c>
    /// No controller-side rule equivalent — this is a pure pilot utterance, so it lives here
    /// rather than in <c>PhraseologyRules</c>.
    /// </summary>
    public static PilotSpeechText BuildReadyToTaxi(AircraftState aircraft)
    {
        return BuildReadyToTaxi(aircraft, "ground");
    }

    public static PilotSpeechText BuildReadyToTaxi(AircraftState aircraft, string facilityCallName)
    {
        var location = aircraft.Ground.ParkingSpot is { Length: > 0 } spot ? $"at {spot.ToLowerInvariant()}" : "at the ramp";
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var facility = CleanFacilityCallName(facilityCallName, "ground");
        return new PilotSpeechText(
            $"{facility}, {location}, with information Alpha, ready to taxi.",
            $"{facility}, {spoken} {location}, with information Alpha, ready to taxi."
        );
    }

    /// <summary>
    /// Pilot check-in fired by <c>HoldingShortPhase</c> on entry (DestinationRunway reason only —
    /// the aircraft's assigned departure runway, never an intermediate runway crossing) in
    /// solo-training mode. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo holding short runway two eight right, ready for departure."</c>
    /// </summary>
    public static PilotSpeechText BuildHoldingShortReady(AircraftState aircraft, string runwayId)
    {
        return BuildHoldingShortReady(aircraft, runwayId, "tower");
    }

    public static PilotSpeechText BuildHoldingShortReady(AircraftState aircraft, string runwayId, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var facility = CleanFacilityCallName(facilityCallName, "tower");
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
    public static PilotSpeechText BuildLinedUpReady(AircraftState aircraft, string runwayId)
    {
        return BuildLinedUpReady(aircraft, runwayId, "tower");
    }

    public static PilotSpeechText BuildLinedUpReady(AircraftState aircraft, string runwayId, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var facility = CleanFacilityCallName(facilityCallName, "tower");
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
    ///   <item><description>VFR / IFR-no-approach: <c>"[N123AB] tower, american one twenty three three-mile final runway two eight right, with information Alpha."</c></description></item>
    /// </list>
    /// </summary>
    public static PilotSpeechText BuildOnFinal(
        AircraftState aircraft,
        string runwayId,
        bool ifrWithActiveApproach,
        string? approachId,
        int distanceMilesForVfr
    )
    {
        return BuildOnFinal(aircraft, runwayId, ifrWithActiveApproach, approachId, distanceMilesForVfr, "tower");
    }

    public static PilotSpeechText BuildOnFinal(
        AircraftState aircraft,
        string runwayId,
        bool ifrWithActiveApproach,
        string? approachId,
        int distanceMilesForVfr,
        string facilityCallName
    )
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var facility = CleanFacilityCallName(facilityCallName, "tower");
        if (ifrWithActiveApproach && !string.IsNullOrEmpty(approachId))
        {
            return new PilotSpeechText(
                $"{facility}, {CompactApproachName(approachId, runwayId)}.",
                $"{facility}, {spoken}, {SpokenApproachName(approachId, runwayId)}."
            );
        }

        var miles = Math.Max(1, distanceMilesForVfr);
        return new PilotSpeechText(
            $"{facility}, {miles}-mile final runway {PhraseologyVerbalizer.CompactRunway(runwayId)}, with information Alpha.",
            $"{facility}, {spoken} {SpellMiles(miles)}-mile final runway {PhraseologyVerbalizer.SpellRunway(runwayId)}, with information Alpha."
        );
    }

    public static PilotSpeechText BuildArrivalApproachRequest(AircraftState aircraft, string? runwayId, int distanceMiles, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var facility = CleanFacilityCallName(facilityCallName, "approach");
        var miles = Math.Max(1, distanceMiles);
        if (!string.IsNullOrWhiteSpace(runwayId))
        {
            return new PilotSpeechText(
                $"{facility}, {miles} miles to land runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
                $"{facility}, {spoken} {SpellMiles(miles)} miles to land runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
            );
        }

        return new PilotSpeechText(
            $"{facility}, {miles} miles from the airport, request approach.",
            $"{facility}, {spoken} {SpellMiles(miles)} miles from the airport, request approach."
        );
    }

    /// <summary>
    /// Initial-contact check-in for VFR aircraft joining a towered field's traffic pattern for
    /// closed traffic. Fired by <c>PatternEntryPhase</c> on entry in solo-training mode, gated
    /// by <c>!HasMadeInitialContact</c>. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha."</c>
    /// </summary>
    public static PilotSpeechText BuildClosedTrafficRequest(AircraftState aircraft, LatLon airportPosition, int altitudeFt)
    {
        return BuildClosedTrafficRequest(aircraft, airportPosition, altitudeFt, "tower");
    }

    public static PilotSpeechText BuildClosedTrafficRequest(AircraftState aircraft, LatLon airportPosition, int altitudeFt, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        double distNm = GeoMath.DistanceNm(airportPosition, aircraft.Position);
        int distMiles = Math.Max(1, (int)Math.Round(distNm));
        double bearingFromAirport = GeoMath.BearingTo(airportPosition, aircraft.Position);
        string direction = BearingToCardinal8(bearingFromAirport);
        var facility = CleanFacilityCallName(facilityCallName, "tower");
        return new PilotSpeechText(
            $"{facility}, {distMiles} miles {direction} at {PhraseologyVerbalizer.CompactAltitude(altitudeFt)}, request closed traffic, with information Alpha.",
            $"{facility}, {spoken}, {SpellDistanceDigits(distMiles)} miles {direction} at {AtcNumberParser.AltitudeToWords(altitudeFt)}, request closed traffic, with information Alpha."
        );
    }

    /// <summary>
    /// Pilot readback for the controller's CT (contact next controller) instruction. The
    /// controller side reads "contact (facility) on (frequency)"; the pilot drops the verb,
    /// repeats the frequency for confirmation, and signs off. Output:
    /// <c>"[N123AB] approach on one two five point three five, november one two three alpha bravo, so long."</c>
    /// </summary>
    public static PilotSpeechText BuildContactReadback(AircraftState aircraft, string facilityName, double frequencyMhz)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        // Caller decides casing — pass Position.RadioName ("NorCal Approach") or the
        // capitalized FacilityShortname fallback ("Approach"), which reads sentence-initial.
        return new PilotSpeechText(
            $"{facilityName} on {frequencyMhz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, so long.",
            $"{facilityName} on {PhraseologyVerbalizer.FrequencyToWords(frequencyMhz)}, {spoken}, so long."
        );
    }

    /// <summary>
    /// Pilot acknowledgement for the controller's FCA (frequency change approved) dismissal.
    /// "Frequency change approved" is the controller phraseology per FAA 7110.65 §7-6-11; pilots
    /// don't recite it verbatim. Real readback (per AIM 4-2-3 ¶3) is a sign-off:
    /// <c>"[N123AB] november one two three alpha bravo, good day."</c>
    /// </summary>
    public static PilotSpeechText BuildFrequencyChangeApproved(AircraftState aircraft)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText("good day.", $"{spoken}, good day.");
    }

    /// <summary>
    /// Routes a sim-initiated pilot transmission. Three-way:
    /// <list type="bullet">
    ///   <item><description><see cref="AircraftState.PendingPilotSpeech"/> when in RPO mode (i.e.
    ///   <paramref name="soloTrainingMode"/> is false) and the
    ///   <c>RpoShowPilotSpeech</c> setting is on — broadcast as <c>PilotSpeech</c> kind (green)
    ///   with the spelled-out spoken form.</description></item>
    ///   <item><description><see cref="AircraftState.PendingWarnings"/> otherwise — broadcast as
    ///   <c>Warning</c> kind (orange) with the existing terse controller-debug text. This is the
    ///   default when neither toggle is set, preserving prior behavior.</description></item>
    /// </list>
    /// Solo-mode pilot-speech routing (into <see cref="AircraftState.PendingPilotTransmissions"/>) is
    /// the caller's responsibility — branch on <paramref name="soloTrainingMode"/> first if the
    /// site has a dedicated solo flow, then fall through to this helper for the non-solo case.
    /// </summary>
    public static void RouteRpoTransmission(
        AircraftState aircraft,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        string pilotSpeechText,
        string warningText
    )
    {
        if (!soloTrainingMode && rpoShowPilotSpeech)
        {
            aircraft.PendingPilotSpeech.Add(pilotSpeechText);
        }
        else
        {
            aircraft.PendingWarnings.Add(warningText);
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
    ///   <item><description><b>RPO with show-pilot-speech</b>: broadcast as
    ///   <c>PilotSpeech</c> kind (green) with the TTS form.</description></item>
    ///   <item><description><b>RPO default</b>: terminal warning with the readable form.</description></item>
    /// </list>
    /// </summary>
    public static void RouteSoloOrRpoTransmission(
        AircraftState aircraft,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        string? studentPositionType,
        PilotSpeechText text,
        IReadOnlyCollection<string> soloRelevantPositions,
        string sourceKind = SourceResponse
    )
    {
        if (soloTrainingMode)
        {
            bool studentIsRelevant =
                studentPositionType is { Length: > 0 } st && soloRelevantPositions.Contains(st, StringComparer.OrdinalIgnoreCase);
            if (studentIsRelevant)
            {
                aircraft.PendingPilotTransmissions.Add(
                    new PilotTransmission(aircraft.Callsign, text.Terminal, text.Tts, sourceKind, PilotTransmissionKind.Proactive)
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
            aircraft.PendingPilotSpeech.Add(text.Tts);
        }
        else
        {
            aircraft.PendingWarnings.Add(text.Terminal);
        }
    }

    /// <summary>
    /// Routes a sim-initiated pilot readback for visual-acquisition events (RTIS / RFIS)
    /// onto the SAY channel rather than the orange Warning channel. Same RPO/solo logic
    /// as <see cref="RouteRpoTransmission"/>: if RPO mode + <c>RpoShowPilotSpeech</c> is
    /// on, fire the spelled-out green <see cref="AircraftState.PendingPilotSpeech"/>;
    /// otherwise fire the terse <see cref="AircraftState.PendingPilotReadbacks"/> which
    /// the server broadcasts as kind <c>SayReadback</c> (the kind starts with "Say" so
    /// the client maps it to the SAY channel like SPOS / SALT).
    /// </summary>
    public static void RouteRpoSayReadback(
        AircraftState aircraft,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        string pilotSpeechText,
        string sayReadbackText
    )
    {
        if (!soloTrainingMode && rpoShowPilotSpeech)
        {
            aircraft.PendingPilotSpeech.Add(pilotSpeechText);
        }
        else if (soloTrainingMode)
        {
            QueueSoloPilotReadback(aircraft, sayReadbackText, SourceSayReadback);
        }
        else
        {
            aircraft.PendingPilotReadbacks.Add(sayReadbackText);
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
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
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
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
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
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var legWord = leg switch
        {
            ReportTrigger.Crosswind => "crosswind",
            ReportTrigger.Downwind => "downwind",
            ReportTrigger.Base => "base",
            ReportTrigger.Final => "final",
            _ => "final",
        };
        return new PilotSpeechText(
            $"turning {legWord} runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, turning {legWord} runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
        );
    }

    /// <summary>
    /// Deferred pilot "n-mile final" report voiced when the aircraft reaches the armed distance from
    /// the threshold. Output:
    /// <c>"[N123AB] november one two three alpha bravo, five mile final runway two eight right."</c>
    /// </summary>
    public static PilotSpeechText BuildMileFinalReport(AircraftState aircraft, int miles, string runwayId)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText(
            $"{miles}-mile final runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, {MilesToWords(miles)} mile final runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
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
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText($"passing {fixName}.", $"{spoken}, passing {fixName}.");
    }

    /// <summary>Cardinal spelling of a final-report distance (1–20 NM) for TTS, falling back to digits.</summary>
    private static string MilesToWords(int miles) =>
        miles switch
        {
            1 => "one",
            2 => "two",
            3 => "three",
            4 => "four",
            5 => "five",
            6 => "six",
            7 => "seven",
            8 => "eight",
            9 => "nine",
            10 => "ten",
            11 => "eleven",
            12 => "twelve",
            13 => "thirteen",
            14 => "fourteen",
            15 => "fifteen",
            16 => "sixteen",
            17 => "seventeen",
            18 => "eighteen",
            19 => "nineteen",
            20 => "twenty",
            _ => miles.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Pilot transmission when the aircraft has the named traffic in sight. Used by sim-resolved
    /// RTIS (visual acquisition) and by direct RTIS dispatches in RPO mode with pilot-speech
    /// rendering enabled. AIM 5-5-10 / 5-5-11 phraseology with NATO-spelled callsigns.
    /// </summary>
    public static string BuildTrafficInSight(AircraftState aircraft, string? targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        if (string.IsNullOrWhiteSpace(targetCallsign))
        {
            return $"[{aircraft.Callsign}] {spoken}, traffic in sight.";
        }
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return $"[{aircraft.Callsign}] {spoken}, traffic in sight, {targetSpoken}.";
    }

    /// <summary>
    /// Pilot transmission when the aircraft has the destination field in sight. Used by sim-resolved
    /// RFIS and by direct RFIS dispatches in RPO mode with pilot-speech rendering enabled.
    /// </summary>
    public static string BuildFieldInSight(AircraftState aircraft)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {spoken}, field in sight.";
    }

    /// <summary>
    /// Pilot transmission when the aircraft has lost prior visual contact with the destination
    /// field. Triggers a re-acquisition request from the controller's perspective.
    /// </summary>
    public static string BuildLostSightOfField(AircraftState aircraft)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {spoken}, negative contact with the field.";
    }

    /// <summary>
    /// Pilot transmission when the aircraft has lost prior visual contact with previously-acquired
    /// traffic.
    /// </summary>
    public static PilotSpeechText BuildLostSightOfTraffic(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return new PilotSpeechText($"negative contact with {targetCallsign}.", $"{spoken}, negative contact with {targetSpoken}.");
    }

    /// <summary>
    /// Pilot transmission when the pilot initiates a go-around. Reason is a short sim-internal
    /// descriptor ("no landing clearance", "too high at missed approach point", etc.) that's
    /// included parenthetically so the controller has the why; the spoken callout itself is the
    /// AIM standard "going around."
    /// </summary>
    public static PilotSpeechText BuildGoingAround(AircraftState aircraft, string reason)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new PilotSpeechText("going around.", $"{spoken}, going around.");
        }
        return new PilotSpeechText($"going around ({reason}).", $"{spoken}, going around ({reason}).");
    }

    /// <summary>
    /// Pilot transmission when approaching published DA/MDA without a landing clearance.
    /// Gives the solo-training controller a radio-visible chance to issue the clearance
    /// before the aircraft reaches minimums and initiates the missed approach.
    /// </summary>
    public static PilotSpeechText BuildApproachingMinimumsNoLandingClearance(AircraftState aircraft)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText("approaching minimums, no landing clearance.", $"{spoken}, approaching minimums, no landing clearance.");
    }

    /// <summary>
    /// Pilot response when a live solo-training controller instruction is rejected by
    /// dispatch. Mirrors 7110.65 operational-request phraseology: "unable" plus a reason
    /// when one is available.
    /// </summary>
    public static PilotSpeechText BuildUnable(AircraftState aircraft, string? reason)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var cleanedReason = CleanUnableReason(reason);
        if (string.IsNullOrEmpty(cleanedReason))
        {
            return new PilotSpeechText("unable.", $"{spoken}, unable.");
        }

        return new PilotSpeechText($"unable, {cleanedReason}.", $"{spoken}, unable, {cleanedReason}.");
    }

    private static string CleanUnableReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "";
        }

        var cleaned = reason.Trim();
        cleaned = cleaned.Replace("__NO_DISPATCHER_ARM__", "", StringComparison.Ordinal);
        cleaned = Regex.Replace(cleaned, @"^\s*unable\b[:,\s-]*", "", RegexOptions.IgnoreCase);
        cleaned = cleaned.Trim(' ', '.', ',', ';', ':', '-');
        if (cleaned.Length == 0)
        {
            return "";
        }

        return cleaned;
    }

    /// <summary>
    /// Pilot transmission for a taxi-side hold-short report ("holding short of [label] at [taxiway]").
    /// Used when the ground phase reaches a hold-short node where the label is a generic identifier
    /// (taxiway intersection, ILS-critical area, etc.) rather than a specific runway designator.
    /// </summary>
    public static PilotSpeechText BuildHoldingShortTaxi(AircraftState aircraft, string label, string taxiway)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText($"{label} at {taxiway}.", $"{spoken}, {label} at {taxiway}.");
    }

    /// <summary>
    /// Pilot transmission when the aircraft is holding short of a runway prior to a runway crossing.
    /// </summary>
    public static PilotSpeechText BuildHoldingShortCrossing(AircraftState aircraft, string runwayId)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText(
            $"holding short runway {PhraseologyVerbalizer.CompactRunway(runwayId)}.",
            $"{spoken}, holding short runway {PhraseologyVerbalizer.SpellRunway(runwayId)}."
        );
    }

    /// <summary>
    /// Pilot transmission when the aircraft has fully exited the active runway after landing.
    /// Reports the runway just vacated and the taxiway used. Returns terminal + TTS forms
    /// independently — terminal uses the digit runway designator, TTS uses the spelled form.
    /// </summary>
    public static PilotSpeechText BuildClearOfRunwayText(AircraftState aircraft, string runwayId, string taxiway)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var runwaySpoken = PhraseologyVerbalizer.SpellRunway(runwayId);
        string terminal = $"clear of runway {PhraseologyVerbalizer.CompactRunway(runwayId)} at {taxiway}.";
        string tts = $"{spoken}, clear of runway {runwaySpoken} at {taxiway}.";
        return new PilotSpeechText(terminal, tts);
    }

    /// <summary>
    /// Pilot transmission when an instructed taxi exit cannot be made (overshoot, wrong-side, etc.).
    /// Pilot phraseology: "negative" for no-can-do, target taxiway for context.
    /// </summary>
    public static PilotSpeechText BuildUnableToExit(AircraftState aircraft, string taxiway)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new PilotSpeechText($"negative on the exit at {taxiway}.", $"{spoken}, negative on the exit at {taxiway}.");
    }

    /// <summary>
    /// Pilot transmission when the aircraft is breaking off a follow because separation can't
    /// be maintained relative to the lead. Pilot side; controller will likely re-sequence.
    /// </summary>
    public static string BuildUnableToMaintainSeparation(AircraftState aircraft, string leadCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var leadSpoken = CallsignParser.IcaoToSpoken(leadCallsign);
        return $"[{aircraft.Callsign}] {spoken}, unable to maintain separation from {leadSpoken}, breaking off the follow.";
    }

    /// <summary>
    /// Pilot transmission when the follow target has landed and the follow is over.
    /// </summary>
    public static PilotSpeechText BuildTargetLanded(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return new PilotSpeechText(
            $"{targetCallsign} is on the ground, breaking off the follow.",
            $"{spoken}, {targetSpoken} is on the ground, breaking off the follow."
        );
    }

    /// <summary>
    /// Pilot transmission when the pilot can't catch up to the follow target before the leader
    /// lands or leaves the area, so the follow is being abandoned.
    /// </summary>
    public static PilotSpeechText BuildUnableToCatchUp(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return new PilotSpeechText(
            $"unable to catch up to {targetCallsign}, breaking off the follow.",
            $"{spoken}, unable to catch up to {targetSpoken}, breaking off the follow."
        );
    }

    /// <summary>
    /// Pilot transmission when a following aircraft has reached its maximum downwind
    /// extension while sequencing behind a pattern-flow-ahead lead and must turn base
    /// before it has the desired trail. Cues the controller to re-sequence (the
    /// follower may roll out tight behind the lead). Output:
    /// <c>"[N123AB] november one two three alpha bravo, turning base behind cessna five six niner sierra x-ray, spacing is tight."</c>
    /// </summary>
    public static PilotSpeechText BuildSequenceTightTurningBase(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return new PilotSpeechText(
            $"turning base behind {targetCallsign}, spacing is tight.",
            $"{spoken}, turning base behind {targetSpoken}, spacing is tight."
        );
    }

    /// <summary>
    /// Pilot advisory when a follower self-initiates a shallow S-turn on final to open in-trail
    /// spacing behind the traffic it is following (AIM 4-3-5 — pilots maneuvering for spacing
    /// advise the controller). Output:
    /// <c>"[N123AB] november one two three alpha bravo, S-turning for spacing behind cessna five six niner sierra x-ray."</c>
    /// </summary>
    public static PilotSpeechText BuildSTurnsForSpacing(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return new PilotSpeechText($"S-turning for spacing behind {targetCallsign}.", $"{spoken}, S-turning for spacing behind {targetSpoken}.");
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
        var positionType = scenario.StudentPositionType;
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
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        // Pilots state altitudes to the nearest 100 ft — there is no phraseology for tens/units.
        // Rounding here also makes the FL gate (% 100 == 0) pass for every airborne check-in.
        int altitudeFt = (int)(Math.Round(aircraft.Altitude / 100.0) * 100);

        if (aircraft.FlightPlan.IsVfr)
        {
            return BuildVfrAirborne(aircraft, scenario, primaryAirportPosition, positionType, facilityCallName, spoken, altitudeFt);
        }
        return BuildIfrAirborne(aircraft, positionType, facilityCallName, spoken, altitudeFt);
    }

    // Climb/descent vs level deadband for the check-in vertical-state verb — below this the
    // aircraft is reported "level" rather than "leaving X climbing/descending".
    private const double VerticalTrendThresholdFt = 200;

    private static PilotSpeechText BuildIfrAirborne(
        AircraftState aircraft,
        string positionType,
        string facilityCallName,
        string spoken,
        int altitudeFt
    )
    {
        var callsign = aircraft.Callsign;
        if (positionType == "TWR")
        {
            // Direct-to-tower IFR is rare. Mention destination runway if known; otherwise drop the runway clause.
            var rwy = aircraft.Procedure.DestinationRunway;
            var rwyClauseTts = !string.IsNullOrEmpty(rwy) ? ", runway " + PhraseologyVerbalizer.SpellRunway(rwy) : "";
            var rwyClauseTerm = !string.IsNullOrEmpty(rwy) ? ", runway " + PhraseologyVerbalizer.CompactRunway(rwy) : "";
            return new PilotSpeechText(
                $"{facilityCallName}, {callsign}{rwyClauseTerm}, with information Alpha.",
                $"{facilityCallName}, {spoken}{rwyClauseTts}, with information Alpha."
            );
        }

        var (clauseTerm, clauseTts, isDeparture) = BuildVerticalStateClause(aircraft, altitudeFt);
        bool isFlightLevel = altitudeFt >= 18000;
        // Departures (climbing / climb-via) reference the departure ATIS via the tower, not arrival
        // ATIS — drop the suffix. Class A enroute (CTR ≥ FL180) has no airport ATIS reference.
        bool dropAtis = isDeparture || (positionType == "CTR" && isFlightLevel);
        var atisSuffix = dropAtis ? "" : ", with information Alpha";

        var clauseTermPart = clauseTerm.Length == 0 ? "" : ", " + clauseTerm;
        var clauseTtsPart = clauseTts.Length == 0 ? "" : ", " + clauseTts;

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
        var altTerm = PhraseologyVerbalizer.CompactAltitude(altitudeFt);
        var altTts = AtcNumberParser.AltitudeToWords(altitudeFt);
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

        var trend = aircraft.Targets.AssignedAltitude ?? aircraft.Targets.TargetAltitude;
        if (trend is { } target && Math.Abs(target - altitudeFt) > VerticalTrendThresholdFt)
        {
            bool climbing = target > altitudeFt;
            var verb = climbing ? "climbing" : "descending";
            var (toTerm, toTts) = AssignedAltitudeClause(aircraft);
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
        var (procTerm, procTts) = PhraseologyVerbalizer.ProcedureName(procedureId);
        // ATC only assigns a "for (altitude)" bottom/top when it differs from the published procedure.
        var (forTerm, forTts) = AssignedAltitudeClause(aircraft);
        var leadTerm = forTerm.Length == 0 ? $"leaving {altTerm}" : $"leaving {altTerm} for{forTerm}";
        var leadTts = forTts.Length == 0 ? $"leaving {altTts}" : $"leaving {altTts} for{forTts}";
        return ($"{leadTerm}, {verb} the {procTerm} {procType}", $"{leadTts}, {verb} the {procTts} {procType}", isDeparture);
    }

    /// <summary>
    /// The " (altitude)" suffix naming the controller-assigned cleared altitude for a climb/descent
    /// check-in (AIM 5-3-1.b.2.a "climbing to|descending to (altitude)"). Empty when no discrete
    /// altitude is assigned (e.g. a descend-via aircraft following published crossings).
    /// </summary>
    private static (string Term, string Tts) AssignedAltitudeClause(AircraftState aircraft)
    {
        if (aircraft.Targets.AssignedAltitude is not { } assigned)
        {
            return ("", "");
        }
        int rounded = (int)(Math.Round(assigned / 100.0) * 100);
        var term = PhraseologyVerbalizer.CompactAltitude(rounded);
        var tts = AtcNumberParser.AltitudeToWords(rounded);
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
        int altitudeFt
    )
    {
        double distNm = GeoMath.DistanceNm(primaryAirportPosition, aircraft.Position);
        int distMiles = Math.Max(1, (int)Math.Round(distNm));
        double bearingFromAirport = GeoMath.BearingTo(primaryAirportPosition, aircraft.Position);
        string direction = BearingToCardinal8(bearingFromAirport);
        string altitudeWords = AtcNumberParser.AltitudeToWords(altitudeFt);
        string altitudeCompact = PhraseologyVerbalizer.CompactAltitude(altitudeFt);
        string distWords = SpellDistanceDigits(distMiles);
        string distTerm = distMiles.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var dest = aircraft.FlightPlan.Destination;
        var primary = scenario.PrimaryAirportId ?? "";
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
                $"{facilityCallName}, {aircraft.Callsign}, {distTerm} miles {direction} of {positionAnchor}, VFR {heading}{atAltTerm}.",
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
            var airportSpoken = PhraseologyVerbalizer.SpellAirportName(scenario.PrimaryAirportId ?? "");
            return new PilotSpeechText(
                $"{facilityCallName}, {aircraft.Callsign}{atAltTerm}, {distTerm} miles {direction} of {airportSpoken}, {intent}.",
                $"{facilityCallName}, {spoken}{atAltTts}, {distWords} miles {direction} of {airportSpoken}, {intent}."
            );
        }

        var atisSuffix = inbound ? ", with information Alpha" : "";
        return new PilotSpeechText(
            $"{facilityCallName}, {aircraft.Callsign} {distTerm} miles {direction}{atAltTerm}, {intent}{atisSuffix}.",
            $"{facilityCallName}, {spoken} {distWords} miles {direction}{atAltTts}, {intent}{atisSuffix}."
        );
    }

    public static string? ResolveStudentRadioName(SimScenarioState? scenario)
    {
        if (scenario?.StudentPosition?.Callsign is not { Length: > 0 } callsign)
        {
            return null;
        }

        var radioName = scenario.ArtccConfig?.FindPositionByCallsign(callsign)?.RadioName;
        return string.IsNullOrWhiteSpace(radioName) ? null : radioName.Trim();
    }

    public static string ResolveStudentFacilityCallName(SimScenarioState? scenario, string positionType, string fallbackFacility)
    {
        var radioName = ResolveStudentRadioName(scenario);
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
        var value = string.IsNullOrWhiteSpace(facilityCallName) ? fallbackFacility : facilityCallName.Trim();
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
        var prefix = $"[{aircraft.Callsign}] ";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text;
    }

    public static string PrepareForTts(AircraftState aircraft, string text) => NormalizeForTts(StripBracketedPrefix(aircraft, text));

    /// <summary>
    /// TTS-only spelling fixups inappropriate for terminal display: "x-ray" → "xray" and
    /// de-hyphenating word-joined hyphens ("three-mile" → "three mile").
    /// </summary>
    private static string NormalizeForTts(string text)
    {
        var speech = Regex.Replace(text, @"\bx-ray\b", "xray", RegexOptions.IgnoreCase);
        return Regex.Replace(speech, @"(?<=\w)-(?=\w)", " ");
    }

    /// <summary>
    /// CIFP-style approach id ("I28R", "R28R-Y", "VIS28R") to ATC-spoken form
    /// ("ILS two eight right", "RNAV two eight right yankee", "visual approach runway two eight right").
    /// Falls back to NATO-spelled prefix + spoken runway if the prefix isn't recognized.
    /// </summary>
    internal static string SpokenApproachName(string approachId, string runwayId) =>
        ApproachName(approachId, PhraseologyVerbalizer.SpellRunway(runwayId), spelledSuffix: true);

    /// <summary>Compact terminal approach name: "ILS 28R", "RNAV 28R Y" (digit runway, raw suffix letter).</summary>
    internal static string CompactApproachName(string approachId, string runwayId) =>
        ApproachName(approachId, PhraseologyVerbalizer.CompactRunway(runwayId), spelledSuffix: false);

    private static string ApproachName(string approachId, string runwayText, bool spelledSuffix)
    {
        if (string.IsNullOrEmpty(approachId))
        {
            return string.Empty;
        }

        var dashIdx = approachId.IndexOf('-');
        var suffix = dashIdx >= 0 && dashIdx < approachId.Length - 1 ? approachId[(dashIdx + 1)..] : null;
        var head = dashIdx >= 0 ? approachId[..dashIdx] : approachId;

        // Strip the trailing runway portion off the head ("I28R" → prefix "I"; "VIS28R" → prefix "VIS").
        var prefixEnd = 0;
        while (prefixEnd < head.Length && !char.IsDigit(head[prefixEnd]))
        {
            prefixEnd++;
        }

        var prefix = head[..prefixEnd];
        var prefixSpoken = ExpandApproachPrefix(prefix);
        var suffixText = suffix is null
            ? string.Empty
            : " " + (spelledSuffix ? string.Join(' ', suffix.Select(c => NatoPhoneticAlphabet.SpellChar(c))) : suffix.ToUpperInvariant());
        return $"{prefixSpoken} {runwayText}{suffixText}";
    }

    private static string ExpandApproachPrefix(string prefix) =>
        prefix.ToUpperInvariant() switch
        {
            "I" => "ILS",
            "R" => "RNAV",
            "L" => "localizer",
            "V" => "VOR",
            "N" => "NDB",
            "D" => "VOR DME",
            "B" => "localizer back course",
            "VIS" => "visual approach runway",
            "" => string.Empty,
            _ => string.Join(' ', prefix.Select(c => NatoPhoneticAlphabet.SpellChar(c))),
        };

    private static string SpellMiles(int miles) =>
        miles switch
        {
            1 => "one",
            2 => "two",
            3 => "three",
            4 => "four",
            5 => "five",
            6 => "six",
            7 => "seven",
            8 => "eight",
            9 => "nine",
            10 => "ten",
            _ => miles.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    internal static string? FormatCondition(BlockCondition? condition) =>
        condition switch
        {
            null => null,
            AtFixCondition fix => $"at {PhraseologyVerbalizer.SpellFix(fix.FixName)},",
            LevelCondition level => $"at {PhraseologyVerbalizer.AltitudeWords(level.Altitude)},",
            _ => null, // GiveWayCondition and other condition kinds have their own dispatch path.
        };

    /// <summary>Compact terminal form of the block-condition lead-in ("at SUNOL," / "at 5000,").</summary>
    internal static string? FormatConditionTerminal(BlockCondition? condition) =>
        condition switch
        {
            null => null,
            AtFixCondition fix => $"at {PhraseologyVerbalizer.FixDisplayText(fix.FixName)},",
            LevelCondition level => $"at {PhraseologyVerbalizer.CompactAltitude(level.Altitude)},",
            _ => null,
        };
}
