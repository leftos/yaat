using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
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
        aircraft.PendingPilotTransmissions.Add(new PilotTransmission(aircraft.Callsign, text, PrepareForTts(aircraft, text), sourceKind, kind));
    }

    /// <summary>
    /// Queues a solo-training SAY-channel readback for delayed radio transcript and typed
    /// pilot-audio broadcast.
    /// </summary>
    public static void QueueSoloPilotReadback(AircraftState aircraft, string text, string sourceKind)
    {
        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(aircraft.Callsign, text, PrepareForTts(aircraft, text), sourceKind, PilotTransmissionKind.SayReadback)
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
    public static string? BuildReadback(CompoundCommand compound, AircraftState aircraft) =>
        BuildReadback(compound, aircraft, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate);

    public static string? BuildReadback(
        CompoundCommand compound,
        AircraftState aircraft,
        PilotPersonality personality,
        FrequencyActivityLevel activityLevel
    )
    {
        // Per-block clause lists are joined internally with ", " (parallel commands within
        // a `,`-separated block); blocks themselves are joined with ", then " to mark the
        // `;` (sequential) boundary the controller dictated. Without "then", TTS reads
        // sequential and parallel clauses identically.
        var blockTexts = new List<string>();
        foreach (var block in compound.Blocks)
        {
            var blockClauses = new List<string>();
            var conditionLead = FormatCondition(block.Condition);
            foreach (var cmd in block.Commands)
            {
                var clause = VerbalizeForReadback(cmd, aircraft, personality, activityLevel);
                if (string.IsNullOrEmpty(clause))
                {
                    continue;
                }

                blockClauses.Add(conditionLead is null ? clause : conditionLead + " " + clause);
                conditionLead = null; // condition is spoken once per block
            }

            if (blockClauses.Count > 0)
            {
                blockTexts.Add(string.Join(", ", blockClauses));
            }
        }

        if (blockTexts.Count == 0)
        {
            return null;
        }

        var body = string.Join(", then ", blockTexts);
        body = ApplyQuietFlavor(aircraft.Callsign, body, personality, activityLevel);
        return Format(aircraft, body);
    }

    private static string? VerbalizeForReadback(
        ParsedCommand cmd,
        AircraftState aircraft,
        PilotPersonality personality,
        FrequencyActivityLevel activityLevel
    ) =>
        cmd switch
        {
            ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrAbove or AltitudeAssignmentModifier.AtOrBelow } cm =>
                BuildAltitudeRestrictionClause(aircraft, cm),
            LineUpAndWaitCommand luaw => BuildRunwayInstructionClause(aircraft, "line up and wait")
                ?? PhraseologyVerbalizer.Verbalize(luaw, personality, activityLevel),
            ClearedForTakeoffCommand cto => BuildTakeoffClearanceClause(
                aircraft,
                "cleared for takeoff",
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
                BuildRunwayInstructionClause(aircraft, "cleared to land"),
                cland.CautionWakeTurbulence
            ) ?? PhraseologyVerbalizer.Verbalize(cland, personality, activityLevel),
            LandAndHoldShortCommand lahso => BuildLandAndHoldShortClause(aircraft, lahso),
            TouchAndGoCommand tg => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared touch and go", explicitRunwayId: tg.RunwayId)
                    ?? PhraseologyVerbalizer.Verbalize(tg, personality, activityLevel),
                tg.TrafficPattern
            ),
            StopAndGoCommand sg => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared stop and go") ?? PhraseologyVerbalizer.Verbalize(sg, personality, activityLevel),
                sg.TrafficPattern
            ),
            LowApproachCommand la => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared low approach") ?? PhraseologyVerbalizer.Verbalize(la, personality, activityLevel),
                la.TrafficPattern
            ),
            ClearedForOptionCommand option => AppendTrafficPatternClause(
                BuildRunwayInstructionClause(aircraft, "cleared for the option")
                    ?? PhraseologyVerbalizer.Verbalize(option, personality, activityLevel),
                option.TrafficPattern
            ),
            _ => PhraseologyVerbalizer.Verbalize(cmd, personality, activityLevel),
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

    private static string BuildAltitudeRestrictionClause(AircraftState aircraft, ClimbMaintainCommand cmd)
    {
        var vfr = aircraft.FlightPlan.IsVfr ? "VFR " : "";
        var restriction = cmd.Modifier == AltitudeAssignmentModifier.AtOrAbove ? "at or above" : "at or below";
        return $"maintain {vfr}{restriction} {PhraseologyVerbalizer.AltitudeWords(cmd.Altitude)}";
    }

    private static string BuildTakeoffClearanceClause(
        AircraftState aircraft,
        string lead,
        DepartureInstruction departure,
        int? assignedAltitude,
        bool includeRunway,
        bool cautionWakeTurbulence
    )
    {
        var sb = new StringBuilder(lead);
        var runwayId = ResolveTakeoffRunwayId(aircraft);
        if (includeRunway && !string.IsNullOrWhiteSpace(runwayId))
        {
            sb.Append(" runway ");
            sb.Append(PhraseologyVerbalizer.SpellRunway(runwayId));
        }

        var departureClause = BuildDepartureInstructionClause(departure);
        if (!string.IsNullOrWhiteSpace(departureClause))
        {
            sb.Append(", ");
            sb.Append(departureClause);
        }

        if (assignedAltitude is { } altitude)
        {
            sb.Append(", climb and maintain ");
            sb.Append(PhraseologyVerbalizer.AltitudeWords(altitude));
        }

        if (cautionWakeTurbulence)
        {
            sb.Append(", caution wake turbulence");
        }

        return sb.ToString();
    }

    private static string ResolveTakeoffRunwayId(AircraftState aircraft)
    {
        if (!string.IsNullOrWhiteSpace(aircraft.Procedure.DepartureRunway))
        {
            return aircraft.Procedure.DepartureRunway;
        }

        return aircraft.Phases?.AssignedRunway?.Designator ?? "";
    }

    private static string? BuildRunwayInstructionClause(AircraftState aircraft, string lead, string? explicitRunwayId = null)
    {
        var runwayId = ResolveRunwayId(aircraft, explicitRunwayId);
        if (string.IsNullOrWhiteSpace(runwayId))
        {
            return null;
        }

        return $"{lead} runway {PhraseologyVerbalizer.SpellRunway(runwayId)}";
    }

    private static string? AppendWakeAdvisoryClause(string? clause, bool cautionWakeTurbulence)
    {
        if (clause is null || !cautionWakeTurbulence)
        {
            return clause;
        }

        return $"{clause}, caution wake turbulence";
    }

    private static string BuildLandAndHoldShortClause(AircraftState aircraft, LandAndHoldShortCommand command)
    {
        var holdShortRunway = PhraseologyVerbalizer.SpellRunway(command.CrossingRunwayId);
        var landingClause = BuildRunwayInstructionClause(aircraft, "cleared to land");
        if (landingClause is null)
        {
            return $"cleared to land, hold short runway {holdShortRunway}";
        }

        return $"{landingClause}, hold short runway {holdShortRunway}";
    }

    private static string? AppendTrafficPatternClause(string? clause, PatternDirection? direction)
    {
        if (string.IsNullOrWhiteSpace(clause))
        {
            return clause;
        }

        if (direction is null)
        {
            return clause;
        }

        return $"{clause}, make {PatternDirectionWord(direction.Value)} traffic";
    }

    private static string ResolveRunwayId(AircraftState aircraft, string? explicitRunwayId = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRunwayId))
        {
            return explicitRunwayId;
        }

        return aircraft.Phases?.ClearedRunwayId ?? aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Phases?.ActiveApproach?.RunwayId ?? "";
    }

    private static string BuildDepartureInstructionClause(DepartureInstruction departure) =>
        departure switch
        {
            DefaultDeparture => "",
            RunwayHeadingDeparture => "fly runway heading",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Right } => "right crosswind departure",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Left } => "left crosswind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Right } => "right downwind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Left } => "left downwind departure",
            RelativeTurnDeparture rel =>
                $"make a {TurnDirectionWord(rel.Direction)} {PhraseologyVerbalizer.DegreesWords(rel.Degrees)} degree departure",
            FlyHeadingDeparture { Direction: TurnDirection.Right } fh =>
                $"turn right heading {PhraseologyVerbalizer.HeadingDigits(fh.MagneticHeading)}",
            FlyHeadingDeparture { Direction: TurnDirection.Left } fh =>
                $"turn left heading {PhraseologyVerbalizer.HeadingDigits(fh.MagneticHeading)}",
            FlyHeadingDeparture fh => $"fly heading {PhraseologyVerbalizer.HeadingDigits(fh.MagneticHeading)}",
            OnCourseDeparture => "on course",
            DirectFixDeparture { Direction: TurnDirection.Left } dfd => $"turn left direct {PhraseologyVerbalizer.SpellFix(dfd.FixName)}",
            DirectFixDeparture { Direction: TurnDirection.Right } dfd => $"turn right direct {PhraseologyVerbalizer.SpellFix(dfd.FixName)}",
            DirectFixDeparture dfd => $"direct {PhraseologyVerbalizer.SpellFix(dfd.FixName)}",
            ClosedTrafficDeparture ct when ct.RunwayId is not null =>
                $"make {PatternDirectionWord(ct.Direction)} traffic runway {PhraseologyVerbalizer.SpellRunway(ct.RunwayId)}",
            ClosedTrafficDeparture ct => $"make {PatternDirectionWord(ct.Direction)} traffic",
            _ => "",
        };

    private static string TurnDirectionWord(TurnDirection direction) => direction == TurnDirection.Right ? "right" : "left";

    private static string PatternDirectionWord(PatternDirection direction) => direction == PatternDirection.Right ? "right" : "left";

    /// <summary>
    /// Pilot-initiated spawn check-in fired by <c>AtParkingPhase</c> 5 seconds after spawn
    /// in solo-training mode. Both IFR and VFR aircraft check in. Output:
    /// <c>"[N123AB] ground, november one two three alpha bravo at the ramp, with information Alpha, ready to taxi."</c>
    /// No controller-side rule equivalent — this is a pure pilot utterance, so it lives here
    /// rather than in <c>PhraseologyRules</c>.
    /// </summary>
    public static string BuildReadyToTaxi(AircraftState aircraft)
    {
        return BuildReadyToTaxi(aircraft, "ground");
    }

    public static string BuildReadyToTaxi(AircraftState aircraft, string facilityCallName)
    {
        var location = aircraft.Ground.ParkingSpot is { Length: > 0 } spot ? $"at {spot.ToLowerInvariant()}" : "at the ramp";
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {CleanFacilityCallName(facilityCallName, "ground")}, {spoken} {location}, with information Alpha, ready to taxi.";
    }

    /// <summary>
    /// Pilot check-in fired by <c>HoldingShortPhase</c> on entry (RunwayCrossing reason only)
    /// in solo-training mode. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo holding short runway two eight right, ready for departure."</c>
    /// </summary>
    public static string BuildHoldingShortReady(AircraftState aircraft, string runwayId)
    {
        return BuildHoldingShortReady(aircraft, runwayId, "tower");
    }

    public static string BuildHoldingShortReady(AircraftState aircraft, string runwayId, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        return $"[{aircraft.Callsign}] {CleanFacilityCallName(facilityCallName, "tower")}, {spoken} holding short runway {rwy}, ready for departure.";
    }

    /// <summary>
    /// Pilot reminder fired by <c>LinedUpAndWaitingPhase</c> 10 seconds after entry when no
    /// takeoff clearance has been issued — the "did you forget me?" call. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo runway two eight right, ready."</c>
    /// </summary>
    public static string BuildLinedUpReady(AircraftState aircraft, string runwayId)
    {
        return BuildLinedUpReady(aircraft, runwayId, "tower");
    }

    public static string BuildLinedUpReady(AircraftState aircraft, string runwayId, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        return $"[{aircraft.Callsign}] {CleanFacilityCallName(facilityCallName, "tower")}, {spoken} runway {rwy}, ready.";
    }

    /// <summary>
    /// Pilot check-in fired by <c>FinalApproachPhase</c> on entry for aircraft that
    /// <em>spawned</em> on final (gated by <c>!HasMadeInitialContact</c>). Two branches:
    /// <list type="bullet">
    ///   <item><description>IFR with active approach: <c>"[N123AB] tower, american one twenty three, ILS two eight right."</c></description></item>
    ///   <item><description>VFR / IFR-no-approach: <c>"[N123AB] tower, american one twenty three three-mile final runway two eight right, with information Alpha."</c></description></item>
    /// </list>
    /// </summary>
    public static string BuildOnFinal(
        AircraftState aircraft,
        string runwayId,
        bool ifrWithActiveApproach,
        string? approachId,
        int distanceMilesForVfr
    )
    {
        return BuildOnFinal(aircraft, runwayId, ifrWithActiveApproach, approachId, distanceMilesForVfr, "tower");
    }

    public static string BuildOnFinal(
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
            var apch = SpokenApproachName(approachId, runwayId);
            return $"[{aircraft.Callsign}] {facility}, {spoken}, {apch}.";
        }

        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        var miles = Math.Max(1, distanceMilesForVfr);
        return $"[{aircraft.Callsign}] {facility}, {spoken} {SpellMiles(miles)}-mile final runway {rwy}, with information Alpha.";
    }

    public static string BuildArrivalApproachRequest(AircraftState aircraft, string? runwayId, int distanceMiles, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var facility = CleanFacilityCallName(facilityCallName, "approach");
        var miles = Math.Max(1, distanceMiles);
        var distance = $"{SpellMiles(miles)} miles";
        if (!string.IsNullOrWhiteSpace(runwayId))
        {
            return $"[{aircraft.Callsign}] {facility}, {spoken} {distance} to land runway {PhraseologyVerbalizer.SpellRunway(runwayId)}.";
        }

        return $"[{aircraft.Callsign}] {facility}, {spoken} {distance} from the airport, request approach.";
    }

    /// <summary>
    /// Initial-contact check-in for VFR aircraft joining a towered field's traffic pattern for
    /// closed traffic. Fired by <c>PatternEntryPhase</c> on entry in solo-training mode, gated
    /// by <c>!HasMadeInitialContact</c>. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha."</c>
    /// </summary>
    public static string BuildClosedTrafficRequest(AircraftState aircraft, LatLon airportPosition, int altitudeFt)
    {
        return BuildClosedTrafficRequest(aircraft, airportPosition, altitudeFt, "tower");
    }

    public static string BuildClosedTrafficRequest(AircraftState aircraft, LatLon airportPosition, int altitudeFt, string facilityCallName)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        double distNm = GeoMath.DistanceNm(airportPosition, aircraft.Position);
        int distMiles = Math.Max(1, (int)Math.Round(distNm));
        double bearingFromAirport = GeoMath.BearingTo(airportPosition, aircraft.Position);
        string direction = BearingToCardinal8(bearingFromAirport);
        string distWords = SpellDistanceDigits(distMiles);
        string altitudeWords = AtcNumberParser.AltitudeToWords(altitudeFt);
        return $"[{aircraft.Callsign}] {CleanFacilityCallName(facilityCallName, "tower")}, {spoken}, {distWords} miles {direction} at {altitudeWords}, request closed traffic, with information Alpha.";
    }

    /// <summary>
    /// Pilot readback for the controller's CT (contact next controller) instruction. The
    /// controller side reads "contact (facility) on (frequency)"; the pilot drops the verb,
    /// repeats the frequency for confirmation, and signs off. Output:
    /// <c>"[N123AB] approach on one two five point three five, november one two three alpha bravo, so long."</c>
    /// </summary>
    public static string BuildContactReadback(AircraftState aircraft, string facilityName, double frequencyMhz)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var freq = PhraseologyVerbalizer.FrequencyToWords(frequencyMhz);
        // Caller decides casing — pass Position.RadioName ("NorCal Approach") or the
        // capitalized FacilityShortname fallback ("Approach"). Don't lowercase here:
        // CompactForTerminal strips the bracketed prefix so this phrase becomes
        // sentence-initial in the terminal display.
        return $"[{aircraft.Callsign}] {facilityName} on {freq}, {spoken}, so long.";
    }

    /// <summary>
    /// Pilot acknowledgement for the controller's FCA (frequency change approved) dismissal.
    /// "Frequency change approved" is the controller phraseology per FAA 7110.65 §7-6-11; pilots
    /// don't recite it verbatim. Real readback (per AIM 4-2-3 ¶3) is a sign-off:
    /// <c>"[N123AB] november one two three alpha bravo, good day."</c>
    /// </summary>
    public static string BuildFrequencyChangeApproved(AircraftState aircraft)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {spoken}, good day.";
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
    /// Legacy bridge for sites whose builders still produce a single TTS string. Adds the solo-TTS branch with a
    /// position-relevance gate so solo TWR/APP students hear sites that weren't yet migrated
    /// to <see cref="PilotSpeechText"/>. New code should prefer the dual-output overload.
    /// </summary>
    public static void RouteSoloOrRpoTransmission(
        AircraftState aircraft,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        string? studentPositionType,
        string pilotSpeechText,
        string warningText,
        IReadOnlyCollection<string> soloRelevantPositions,
        string sourceKind = SourceResponse
    )
    {
        if (soloTrainingMode)
        {
            bool relevant = studentPositionType is { Length: > 0 } st && soloRelevantPositions.Contains(st, StringComparer.OrdinalIgnoreCase);
            if (relevant)
            {
                QueueSoloPilotTransmission(aircraft, pilotSpeechText, PilotTransmissionKind.Proactive, sourceKind);
            }
            else
            {
                aircraft.PendingWarnings.Add(warningText);
            }
            return;
        }

        if (rpoShowPilotSpeech)
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
    public static string BuildMidfieldDownwindReminder(AircraftState aircraft, string runwayId)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        return $"[{aircraft.Callsign}] {spoken}, midfield downwind runway {rwy}.";
    }

    /// <summary>
    /// Brief uncleared-traffic reminder fired by <c>FinalApproachPhase</c> at 1 NM from threshold
    /// when the aircraft has no landing clearance, in solo-training mode for VFR pattern aircraft.
    /// Uses the GA-pilot colloquial "short final" form rather than the FAA controller-canonical
    /// "(distance) mile final" — pilot transmissions don't have to mirror 7110.65 phraseology.
    /// Output:
    /// <c>"[N123AB] november one two three alpha bravo, short final runway two eight right."</c>
    /// </summary>
    public static string BuildShortFinalReminder(AircraftState aircraft, string runwayId)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        return $"[{aircraft.Callsign}] {spoken}, short final runway {rwy}.";
    }

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
    public static string BuildLostSightOfTraffic(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return $"[{aircraft.Callsign}] {spoken}, negative contact with {targetSpoken}.";
    }

    /// <summary>
    /// Pilot transmission when the pilot initiates a go-around. Reason is a short sim-internal
    /// descriptor ("no landing clearance", "too high at missed approach point", etc.) that's
    /// included parenthetically so the controller has the why; the spoken callout itself is the
    /// AIM standard "going around."
    /// </summary>
    public static string BuildGoingAround(AircraftState aircraft, string reason)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return $"[{aircraft.Callsign}] {spoken}, going around.";
        }
        return $"[{aircraft.Callsign}] {spoken}, going around ({reason}).";
    }

    /// <summary>
    /// Pilot transmission when approaching published DA/MDA without a landing clearance.
    /// Gives the solo-training controller a radio-visible chance to issue the clearance
    /// before the aircraft reaches minimums and initiates the missed approach.
    /// </summary>
    public static string BuildApproachingMinimumsNoLandingClearance(AircraftState aircraft)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {spoken}, approaching minimums, no landing clearance.";
    }

    /// <summary>
    /// Pilot response when a live solo-training controller instruction is rejected by
    /// dispatch. Mirrors 7110.65 operational-request phraseology: "unable" plus a reason
    /// when one is available.
    /// </summary>
    public static string BuildUnable(AircraftState aircraft, string? reason)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var cleanedReason = CleanUnableReason(reason);
        if (string.IsNullOrEmpty(cleanedReason))
        {
            return $"[{aircraft.Callsign}] {spoken}, unable.";
        }

        return $"[{aircraft.Callsign}] {spoken}, unable, {cleanedReason}.";
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
    public static string BuildHoldingShortTaxi(AircraftState aircraft, string label, string taxiway)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {spoken}, {label} at {taxiway}.";
    }

    /// <summary>
    /// Pilot transmission when the aircraft is holding short of a runway prior to a runway crossing.
    /// </summary>
    public static string BuildHoldingShortCrossing(AircraftState aircraft, string runwayId)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        return $"[{aircraft.Callsign}] {spoken}, holding short runway {rwy}.";
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
        string terminal = $"{aircraft.Callsign}, clear of runway {runwayId} at {taxiway}.";
        string tts = $"{spoken}, clear of runway {runwaySpoken} at {taxiway}.";
        return new PilotSpeechText(terminal, tts);
    }

    /// <summary>
    /// Pilot transmission when an instructed taxi exit cannot be made (overshoot, wrong-side, etc.).
    /// Pilot phraseology: "negative" for no-can-do, target taxiway for context.
    /// </summary>
    public static string BuildUnableToExit(AircraftState aircraft, string taxiway)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {spoken}, negative on the exit at {taxiway}.";
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
    public static string BuildTargetLanded(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return $"[{aircraft.Callsign}] {spoken}, {targetSpoken} is on the ground, breaking off the follow.";
    }

    /// <summary>
    /// Pilot transmission when the pilot can't catch up to the follow target before the leader
    /// lands or leaves the area, so the follow is being abandoned.
    /// </summary>
    public static string BuildUnableToCatchUp(AircraftState aircraft, string targetCallsign)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var targetSpoken = CallsignParser.IcaoToSpoken(targetCallsign);
        return $"[{aircraft.Callsign}] {spoken}, unable to catch up to {targetSpoken}, breaking off the follow.";
    }

    /// <summary>
    /// Pilot airborne-spawn check-in fired by <see cref="PilotProactive.TickAirborneCheckIn"/>
    /// the first tick an aircraft is observed airborne in solo-training mode and has not
    /// yet spoken to ATC. Branches on <see cref="SimScenarioState.StudentPositionType"/>
    /// (TWR / APP / CTR) × <see cref="AircraftFlightPlan.IsVfr"/> × VFR intent
    /// (inbound / transit / no-destination). Returns <see langword="null"/> when the
    /// student position is GND, null, or unrecognized.
    /// </summary>
    public static string? BuildAirborneCheckIn(AircraftState aircraft, SimScenarioState scenario, LatLon primaryAirportPosition)
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
        int altitudeFt = (int)Math.Round(aircraft.Altitude);

        if (aircraft.FlightPlan.IsVfr)
        {
            return BuildVfrAirborne(aircraft, scenario, primaryAirportPosition, positionType, facilityCallName, spoken, altitudeFt);
        }
        return BuildIfrAirborne(aircraft, positionType, facilityCallName, spoken, altitudeFt);
    }

    /// <summary>
    /// Pilot callout when solo-training AI self-restricts outside controlled airspace.
    /// The pilot is reporting their compliance state, not reading back a controller phrase.
    /// Returns terminal + TTS forms independently — terminal uses "Class C" / "OAK" while
    /// TTS keeps "the charlie" / "oscar alpha kilo" for natural speech.
    /// </summary>
    public static PilotSpeechText BuildAirspaceBoundaryHoldText(
        AircraftState aircraft,
        Yaat.Sim.Data.Airspace.AirspaceClass airspaceClass,
        string airportIdent,
        LatLon referencePosition
    )
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        double distNm = GeoMath.DistanceNm(referencePosition, aircraft.Position);
        int distMiles = Math.Max(1, (int)Math.Round(distNm));
        double bearingFromReference = GeoMath.BearingTo(referencePosition, aircraft.Position);
        string direction = BearingToCardinal8(bearingFromReference);
        string distWords = SpellDistanceDigits(distMiles);
        // Airspace volume idents (OAK, SFO, …) are airport identifiers — Class B/C/D
        // airspace is tied to an airport, not a VOR — so prefer the airport-name lookup
        // ("Oakland Airport") over the navaid lookup that <see cref="PhraseologyVerbalizer.SpellFix"/>
        // would do for direct-to-fix contexts.
        string airportTts = PhraseologyVerbalizer.SpellAirportName(airportIdent);
        string airportTerminal = airportIdent.ToUpperInvariant();
        string airspaceLabel = AirspaceClassToLabel(airspaceClass);
        string airspaceTts = $"the {airspaceLabel}";
        string airspaceTerminal = $"Class {AirspaceClassToLetter(airspaceClass)}";
        string reason = airspaceClass == Yaat.Sim.Data.Airspace.AirspaceClass.Charlie ? "awaiting two-way" : "awaiting clearance";

        string terminal = $"{aircraft.Callsign}, holding outside {airspaceTerminal}, {distMiles} miles {direction} of {airportTerminal}, {reason}.";
        string tts = $"{spoken}, holding outside {airspaceTts}, {distWords} miles {direction} of {airportTts}, {reason}.";
        return new PilotSpeechText(terminal, tts);
    }

    private static string AirspaceClassToLabel(Yaat.Sim.Data.Airspace.AirspaceClass airspaceClass) =>
        airspaceClass switch
        {
            Yaat.Sim.Data.Airspace.AirspaceClass.Bravo => "bravo",
            Yaat.Sim.Data.Airspace.AirspaceClass.Charlie => "charlie",
            _ => airspaceClass.ToString().ToLowerInvariant(),
        };

    private static string AirspaceClassToLetter(Yaat.Sim.Data.Airspace.AirspaceClass airspaceClass) =>
        airspaceClass switch
        {
            Yaat.Sim.Data.Airspace.AirspaceClass.Bravo => "B",
            Yaat.Sim.Data.Airspace.AirspaceClass.Charlie => "C",
            _ => airspaceClass.ToString(),
        };

    private static string BuildIfrAirborne(AircraftState aircraft, string positionType, string facilityCallName, string spoken, int altitudeFt)
    {
        if (positionType == "TWR")
        {
            // Direct-to-tower IFR is rare. Mention destination runway if known; otherwise drop the runway clause.
            var rwy = aircraft.Procedure.DestinationRunway;
            var rwyClause = !string.IsNullOrEmpty(rwy) ? " runway " + PhraseologyVerbalizer.SpellRunway(rwy) : "";
            return $"[{aircraft.Callsign}] {facilityCallName}, {spoken}{rwyClause}, with information Alpha.";
        }

        bool isFlightLevel = altitudeFt >= 18000 && altitudeFt % 100 == 0;
        var altitudeWords = AtcNumberParser.AltitudeToWords(altitudeFt);
        // Sub-100 ft (or non-positive) altitudes render as empty — drop the clause entirely
        // rather than emit "level , " or "flight level , ".
        var altitudePhrase = string.IsNullOrEmpty(altitudeWords) ? "" : (isFlightLevel ? altitudeWords : "level " + altitudeWords);
        var altitudeClause = altitudePhrase.Length == 0 ? "" : " " + altitudePhrase;

        if (positionType == "CTR" && isFlightLevel)
        {
            // Class A enroute — no airport ATIS reference.
            return $"[{aircraft.Callsign}] {facilityCallName}, {spoken}{altitudeClause}.";
        }

        return $"[{aircraft.Callsign}] {facilityCallName}, {spoken}{altitudeClause}, with information Alpha.";
    }

    private static string BuildVfrAirborne(
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
        string distWords = SpellDistanceDigits(distMiles);

        var dest = aircraft.FlightPlan.Destination;
        var primary = scenario.PrimaryAirportId ?? "";
        bool noDest = string.IsNullOrEmpty(dest);
        bool inbound = !noDest && dest.Equals(primary, StringComparison.OrdinalIgnoreCase);

        // Sub-100 ft altitudes render as empty — drop the "at {altitude}" clause rather
        // than emit a dangling "at , ". Pilots who just lifted off a low-elevation field
        // (or who otherwise read airborne while still in ground effect) shouldn't break the
        // sentence shape.
        string atAltitude = string.IsNullOrEmpty(altitudeWords) ? "" : " at " + altitudeWords;

        if (noDest)
        {
            // AIM 4-3-1 form: "VFR [Xbound] at [altitude]" is the canonical activity phrase. Stating
            // position with an explicit "of the field"/"of [airport]" anchor avoids the bare-direction
            // adjacency that would read as contradicting the heading-of-flight.
            string heading = HeadingToBoundCardinal(aircraft.TrueHeading.Degrees);
            string positionAnchor = positionType == "TWR" ? "the field" : PhraseologyVerbalizer.SpellAirportName(scenario.PrimaryAirportId ?? "");
            return $"[{aircraft.Callsign}] {facilityCallName}, {spoken} {distWords} miles {direction} of {positionAnchor}, VFR {heading}{atAltitude}.";
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
            return $"[{aircraft.Callsign}] {facilityCallName}, {spoken}{atAltitude}, {distWords} miles {direction} of {airportSpoken}, {intent}.";
        }

        var atisSuffix = inbound ? ", with information Alpha" : "";
        return $"[{aircraft.Callsign}] {facilityCallName}, {spoken} {distWords} miles {direction}{atAltitude}, {intent}{atisSuffix}.";
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

    private static string Format(AircraftState aircraft, string body)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {body}, {spoken}.";
    }

    private static string CompactForTerminal(AircraftState aircraft, string speechText)
    {
        var text = StripBracketedPrefix(aircraft, speechText);
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        text = Regex.Replace(text, Regex.Escape(spoken), aircraft.Callsign, RegexOptions.IgnoreCase);
        text = CompactRunwayPhrases(text);
        text = CompactDistancePhrases(text);
        text = CompactFrequencyPhrases(text);
        return text;
    }

    private static string StripBracketedPrefix(AircraftState aircraft, string text)
    {
        var prefix = $"[{aircraft.Callsign}] ";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text;
    }

    public static string PrepareForTts(AircraftState aircraft, string text)
    {
        var speech = StripBracketedPrefix(aircraft, text);
        speech = Regex.Replace(speech, @"\bx-ray\b", "xray", RegexOptions.IgnoreCase);
        return Regex.Replace(speech, @"(?<=\w)-(?=\w)", " ");
    }

    private static string CompactRunwayPhrases(string text)
    {
        return Regex.Replace(
            text,
            @"\brunway (?<d1>zero|one|two|three|four|five|six|seven|eight|nine)(?: (?<d2>zero|one|two|three|four|five|six|seven|eight|nine))?(?: (?<suffix>left|right|center))?\b",
            match =>
            {
                var d1 = DigitWordToChar(match.Groups["d1"].Value);
                var d2 = match.Groups["d2"].Success ? DigitWordToChar(match.Groups["d2"].Value).ToString() : "";
                var suffix = match.Groups["suffix"].Success ? RunwaySuffix(match.Groups["suffix"].Value) : "";
                return $"runway {d1}{d2}{suffix}";
            },
            RegexOptions.IgnoreCase
        );
    }

    private static string CompactDistancePhrases(string text)
    {
        text = Regex.Replace(
            text,
            @"\b(?<n>one|two|three|four|five|six|seven|eight|nine|ten)-mile\b",
            match => $"{NumberWordToInt(match.Groups["n"].Value)}-mile",
            RegexOptions.IgnoreCase
        );

        return Regex.Replace(
            text,
            @"\b(?<n>one|two|three|four|five|six|seven|eight|nine|ten) miles\b",
            match => $"{NumberWordToInt(match.Groups["n"].Value)} miles",
            RegexOptions.IgnoreCase
        );
    }

    // Aviation VHF frequencies are spelled out digit-by-digit per FAA 7110.65 §2-4-16
    // ("one two five point three five"). For terminal display, render as digits ("125.35")
    // so controllers see the same form they typed in CT / pulled off the strip.
    private static readonly string DigitWordPattern = "(?:zero|one|two|three|four|five|six|seven|eight|nine)";

    private static string CompactFrequencyPhrases(string text)
    {
        return Regex.Replace(
            text,
            @"\b(?<int>"
                + DigitWordPattern
                + @"(?:\s+"
                + DigitWordPattern
                + @")+)\s+point\s+(?<frac>"
                + DigitWordPattern
                + @"(?:\s+"
                + DigitWordPattern
                + @")*)\b",
            match =>
            {
                var integerDigits = string.Concat(
                    match.Groups["int"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(DigitWordToChar)
                );
                var fractionDigits = string.Concat(
                    match.Groups["frac"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(DigitWordToChar)
                );
                return $"{integerDigits}.{fractionDigits}";
            },
            RegexOptions.IgnoreCase
        );
    }

    private static char DigitWordToChar(string word) =>
        word.ToLowerInvariant() switch
        {
            "zero" => '0',
            "one" => '1',
            "two" => '2',
            "three" => '3',
            "four" => '4',
            "five" => '5',
            "six" => '6',
            "seven" => '7',
            "eight" => '8',
            "nine" => '9',
            _ => '?',
        };

    private static int NumberWordToInt(string word) =>
        word.ToLowerInvariant() switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            _ => 0,
        };

    private static string RunwaySuffix(string word) =>
        word.ToLowerInvariant() switch
        {
            "left" => "L",
            "right" => "R",
            "center" => "C",
            _ => "",
        };

    /// <summary>
    /// CIFP-style approach id ("I28R", "R28R-Y", "VIS28R") to ATC-spoken form
    /// ("ILS two eight right", "RNAV two eight right yankee", "visual approach runway two eight right").
    /// Falls back to NATO-spelled prefix + spoken runway if the prefix isn't recognized.
    /// </summary>
    internal static string SpokenApproachName(string approachId, string runwayId)
    {
        if (string.IsNullOrEmpty(approachId))
        {
            return string.Empty;
        }

        var rwySpelled = PhraseologyVerbalizer.SpellRunway(runwayId);
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
        var suffixSpoken = suffix is null ? string.Empty : " " + string.Join(' ', suffix.Select(c => NatoPhoneticAlphabet.SpellChar(c)));
        return $"{prefixSpoken} {rwySpelled}{suffixSpoken}";
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

    private static string? FormatCondition(BlockCondition? condition) =>
        condition switch
        {
            null => null,
            AtFixCondition fix => $"at {fix.FixName.ToLowerInvariant()},",
            LevelCondition level => $"at {PhraseologyVerbalizer.AltitudeWords(level.Altitude)},",
            _ => null, // GiveWayCondition and other condition kinds have their own dispatch path.
        };
}
