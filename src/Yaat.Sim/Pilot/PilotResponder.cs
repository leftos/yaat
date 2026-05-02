using System.Text;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Builds deterministic pilot-readback strings from accepted dispatched commands. Used by
/// <see cref="CommandDispatcher"/> in solo-training mode to push readbacks into
/// <see cref="AircraftState.PendingNotifications"/>.
///
/// Most readbacks come from inverting <see cref="PhraseologyRule"/>s via
/// <see cref="PhraseologyVerbalizer"/> — controllers and pilots share the same vocabulary,
/// so the rule that would have parsed the controller's words is also the readback. Pilot-only
/// utterances (spawn check-in, "going around" volunteered) live here directly because they
/// have no controller-side rule equivalent.
///
/// Output format:
/// <code>"[N123AB] descend and maintain five thousand, november one two three alpha bravo."</code>
/// The <c>[ICAO]</c> bracket prefix is for terminal rendering (so the user sees who is talking
/// even if multiple aircraft talk back close together); the spoken-callsign tail is the real
/// ATC closing form.
/// </summary>
public static class PilotResponder
{
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
    public static string? BuildReadback(CompoundCommand compound, AircraftState aircraft)
    {
        var clauses = new List<string>();
        foreach (var block in compound.Blocks)
        {
            var conditionLead = FormatCondition(block.Condition);
            foreach (var cmd in block.Commands)
            {
                var clause = PhraseologyVerbalizer.Verbalize(cmd);
                if (string.IsNullOrEmpty(clause))
                {
                    continue;
                }

                clauses.Add(conditionLead is null ? clause : conditionLead + " " + clause);
                conditionLead = null; // condition is spoken once per block
            }
        }

        if (clauses.Count == 0)
        {
            return null;
        }

        return Format(aircraft, string.Join(", ", clauses));
    }

    /// <summary>
    /// Pilot-initiated spawn check-in fired by <c>AtParkingPhase</c> 5 seconds after spawn
    /// in solo-training mode. Both IFR and VFR aircraft check in. Output:
    /// <c>"[N123AB] ground, november one two three alpha bravo at the ramp, with information Alpha, ready to taxi."</c>
    /// No controller-side rule equivalent — this is a pure pilot utterance, so it lives here
    /// rather than in <c>PhraseologyRules</c>.
    /// </summary>
    public static string BuildReadyToTaxi(AircraftState aircraft)
    {
        var location = aircraft.Ground.ParkingSpot is { Length: > 0 } spot ? $"at {spot.ToLowerInvariant()}" : "at the ramp";
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] ground, {spoken} {location}, with information Alpha, ready to taxi.";
    }

    /// <summary>
    /// Pilot check-in fired by <c>HoldingShortPhase</c> on entry (RunwayCrossing reason only)
    /// in solo-training mode. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo holding short runway two eight right, ready for departure."</c>
    /// </summary>
    public static string BuildHoldingShortReady(AircraftState aircraft, string runwayId)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        return $"[{aircraft.Callsign}] tower, {spoken} holding short runway {rwy}, ready for departure.";
    }

    /// <summary>
    /// Pilot reminder fired by <c>LinedUpAndWaitingPhase</c> 10 seconds after entry when no
    /// takeoff clearance has been issued — the "did you forget me?" call. Output:
    /// <c>"[N123AB] tower, november one two three alpha bravo runway two eight right, ready."</c>
    /// </summary>
    public static string BuildLinedUpReady(AircraftState aircraft, string runwayId)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        return $"[{aircraft.Callsign}] tower, {spoken} runway {rwy}, ready.";
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
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        if (ifrWithActiveApproach && !string.IsNullOrEmpty(approachId))
        {
            var apch = SpokenApproachName(approachId, runwayId);
            return $"[{aircraft.Callsign}] tower, {spoken}, {apch}.";
        }

        var rwy = PhraseologyVerbalizer.SpellRunway(runwayId);
        var miles = Math.Max(1, distanceMilesForVfr);
        return $"[{aircraft.Callsign}] tower, {spoken} {SpellMiles(miles)}-mile final runway {rwy}, with information Alpha.";
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

        string facility = positionType switch
        {
            "TWR" => "tower",
            "APP" => "approach",
            "CTR" => "center",
            _ => "",
        };
        if (facility.Length == 0)
        {
            return null;
        }

        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        int altitudeFt = (int)Math.Round(aircraft.Altitude);

        if (aircraft.FlightPlan.IsVfr)
        {
            return BuildVfrAirborne(aircraft, scenario, primaryAirportPosition, facility, spoken, altitudeFt);
        }
        return BuildIfrAirborne(aircraft, facility, spoken, altitudeFt);
    }

    private static string BuildIfrAirborne(AircraftState aircraft, string facility, string spoken, int altitudeFt)
    {
        if (facility == "tower")
        {
            // Direct-to-tower IFR is rare. Mention destination runway if known; otherwise drop the runway clause.
            var rwy = aircraft.Procedure.DestinationRunway;
            var rwyClause = !string.IsNullOrEmpty(rwy) ? " runway " + PhraseologyVerbalizer.SpellRunway(rwy) : "";
            return $"[{aircraft.Callsign}] tower, {spoken}{rwyClause}, with information Alpha.";
        }

        bool isFlightLevel = altitudeFt >= 18000 && altitudeFt % 100 == 0;
        var altitudePhrase = isFlightLevel ? AtcNumberParser.AltitudeToWords(altitudeFt) : "level " + AtcNumberParser.AltitudeToWords(altitudeFt);

        if (facility == "center" && isFlightLevel)
        {
            // Class A enroute — no airport ATIS reference.
            return $"[{aircraft.Callsign}] center, {spoken} {altitudePhrase}.";
        }

        return $"[{aircraft.Callsign}] {facility}, {spoken} {altitudePhrase}, with information Alpha.";
    }

    private static string BuildVfrAirborne(
        AircraftState aircraft,
        SimScenarioState scenario,
        LatLon primaryAirportPosition,
        string facility,
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

        if (noDest)
        {
            // AIM 4-3-1 form: "VFR [Xbound] at [altitude]" is the canonical activity phrase. Stating
            // position with an explicit "of the field"/"of [airport]" anchor avoids the bare-direction
            // adjacency that would read as contradicting the heading-of-flight.
            string heading = HeadingToBoundCardinal(aircraft.TrueHeading.Degrees);
            string positionAnchor = facility == "tower" ? "the field" : SpellAirportLetters(scenario.PrimaryAirportId ?? "");
            string atisSuffix = facility == "center" ? "" : ", with information Alpha";
            return $"[{aircraft.Callsign}] {facility}, {spoken} {distWords} miles {direction} of {positionAnchor}, VFR {heading} at {altitudeWords}{atisSuffix}.";
        }

        string intent;
        if (inbound)
        {
            intent = facility switch
            {
                "tower" => "inbound for landing",
                "approach" => "request landing",
                // Center doesn't naturally handle landings — fall back to transit phrasing.
                _ => "request transition",
            };
        }
        else
        {
            intent = "request transition";
        }

        if (facility == "center")
        {
            var airportSpoken = SpellAirportLetters(scenario.PrimaryAirportId ?? "");
            return $"[{aircraft.Callsign}] center, {spoken} at {altitudeWords}, {distWords} miles {direction} of {airportSpoken}, {intent}.";
        }

        return $"[{aircraft.Callsign}] {facility}, {spoken} {distWords} miles {direction} at {altitudeWords}, {intent}, with information Alpha.";
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

    private static string SpellAirportLetters(string airport) => string.Join(' ', airport.Select(NatoPhoneticAlphabet.SpellChar));

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
