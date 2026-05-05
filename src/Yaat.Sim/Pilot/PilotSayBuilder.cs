using System.Text;
using Yaat.Sim.Data;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Generates pilot-style transmission text for SAY-class verbs (SALT, SHDG, SPOS, SSPD,
/// SMACH, SEAPP, freeform SAY). Pure functions over <see cref="AircraftState"/> with no
/// broadcast or routing concerns — callers (CommandDispatcher for triggered/sequenced
/// SAY blocks; the server's SayCommandHandler for direct controller queries) decide where
/// the resulting text goes.
///
/// Output uses AIM-compliant spoken phraseology: digit-by-digit numbers (AIM 4-2-8),
/// thousand/hundred altitude form below FL180 and "flight level X" at/above (AIM 4-2-9),
/// magnetic headings as three spoken digits (AIM 4-2-10), "Mach point X" with no leading
/// zero (AIM 4-2-11), and "leaving X for Y" for level changes (AIM 4-5-1, 5-3-3).
/// </summary>
public static class PilotSayBuilder
{
    public static string BuildAltitude(AircraftState aircraft)
    {
        int alt = RoundToNearest(aircraft.Altitude, 100);
        var target = aircraft.Targets.AssignedAltitude;
        if (target is null)
        {
            return SpokenAltitude(alt);
        }

        int targetAlt = RoundToNearest(target.Value, 100);
        if (Math.Abs(alt - targetAlt) < 100)
        {
            return $"Level {SpokenAltitude(alt)}";
        }

        return $"Leaving {SpokenAltitude(alt)} for {SpokenAltitude(targetAlt)}";
    }

    public static string BuildHeading(AircraftState aircraft)
    {
        int hdg = NormalizeHeading(RoundToNearest(aircraft.TrueHeading.Degrees, 5));
        bool isTurning = Math.Abs(aircraft.BankAngle) > 1.0;
        string? turnDir = isTurning ? (aircraft.BankAngle < 0 ? "left" : "right") : null;

        if (aircraft.Targets.NavigationRoute.Count > 0)
        {
            string fixName = aircraft.Targets.NavigationRoute[0].Name;
            return isTurning
                ? $"Heading {SpokenHeading(hdg)}, turning {turnDir} direct {fixName}"
                : $"Heading {SpokenHeading(hdg)}, direct {fixName}";
        }

        if (isTurning && aircraft.Targets.TargetTrueHeading is { } targetHdg)
        {
            int targetRounded = NormalizeHeading(RoundToNearest(targetHdg.Degrees, 5));
            return $"Heading {SpokenHeading(hdg)}, turning {turnDir} {SpokenHeading(targetRounded)}";
        }

        return $"Heading {SpokenHeading(hdg)}";
    }

    public static string BuildSpeed(AircraftState aircraft)
    {
        var ias = (int)Math.Round(aircraft.IndicatedAirspeed);
        if (aircraft.Altitude >= 24000)
        {
            return $"{SpokenDigits(ias)} knots, {BuildMach(aircraft)}";
        }

        return $"{SpokenDigits(ias)} knots";
    }

    public static string BuildMach(AircraftState aircraft)
    {
        var mach = WindInterpolator.IasToMach(aircraft.IndicatedAirspeed, aircraft.Altitude);
        return $"Mach {SpokenMach(mach)}";
    }

    public static string BuildExpectedApproach(AircraftState aircraft)
    {
        return aircraft.Approach.Expected is not null
            ? $"Expecting the {SpokenApproach(aircraft.Approach.Expected)} approach"
            : "Negative, no approach assigned";
    }

    // Sizeable-airport criteria for both the route-far fallback and the parenthetical
    // airport reference. 6,500 ft typically captures Class C/D airports with airline
    // service and the larger regional fields a working controller (or RPO) recognizes
    // by name; 100 nm covers en-route aircraft transiting unfamiliar airspace.
    private const int SizeableAirportMinRunwayFt = 6500;
    private const double SizeableAirportMaxRangeNm = 100.0;

    /// <summary>
    /// Position report relative to a fix the working controller is likely to recognize.
    /// Primary candidates: the aircraft's filed route (departure, destination, expanded
    /// route fixes) plus active DCT-queue fixes ATC has named. If no candidate is within
    /// 50 nm, falls back to the nearest sizeable airport. When the chosen anchor is a
    /// fix (not an airport), appends a parenthetical airport reference so an unfamiliar
    /// reader can place the fix.
    /// </summary>
    public static string BuildPosition(AircraftState aircraft)
    {
        try
        {
            var navDb = NavigationDatabase.Instance;
            var candidates = BuildPositionCandidates(aircraft, navDb);

            string? primary = candidates.Count > 0 ? FrdResolver.ToFrd(aircraft.Position.Lat, aircraft.Position.Lon, candidates) : null;

            if (primary is null)
            {
                var fallback = navDb.FindNearestSizeableAirport(aircraft.Position, SizeableAirportMinRunwayFt, SizeableAirportMaxRangeNm);
                if (fallback is null)
                {
                    return "Unable to determine position";
                }
                var fallbackFrd = FrdResolver.ToFrd(
                    aircraft.Position.Lat,
                    aircraft.Position.Lon,
                    [(fallback.Value.Id, fallback.Value.Lat, fallback.Value.Lon)],
                    SizeableAirportMaxRangeNm
                );
                return fallbackFrd is null ? "Unable to determine position" : FormatFrd(fallbackFrd, navDb);
            }

            string primaryText = FormatFrd(primary, navDb);
            string? primaryName = FrdResolver.ParseFrd(primary)?.Fix;
            // Anchors that have a published friendly name (VORs, airports) already place
            // themselves for the reader. Only unnamed intersections need an extra airport
            // context line for someone who doesn't recognize the 5-letter waypoint.
            if (primaryName is null || navDb.GetNavaidName(primaryName) is not null || navDb.GetAirportName(primaryName) is not null)
            {
                return primaryText;
            }

            var nearbyAirport = navDb.FindNearestSizeableAirport(aircraft.Position, SizeableAirportMinRunwayFt, SizeableAirportMaxRangeNm);
            if (nearbyAirport is null || string.Equals(nearbyAirport.Value.Id, primaryName, StringComparison.OrdinalIgnoreCase))
            {
                return primaryText;
            }

            var airportFrd = FrdResolver.ToFrd(
                aircraft.Position.Lat,
                aircraft.Position.Lon,
                [(nearbyAirport.Value.Id, nearbyAirport.Value.Lat, nearbyAirport.Value.Lon)],
                SizeableAirportMaxRangeNm
            );
            return airportFrd is null ? primaryText : $"{primaryText}, {FormatFrd(airportFrd, navDb)}";
        }
        catch (InvalidOperationException)
        {
            return "Unable to determine position";
        }
    }

    private static List<(string Name, double Lat, double Lon)> BuildPositionCandidates(AircraftState aircraft, NavigationDatabase navDb)
    {
        var candidates = new List<(string Name, double Lat, double Lon)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                return;
            }
            var pos = navDb.GetFixPosition(name);
            if (pos is not null)
            {
                candidates.Add((name, pos.Value.Lat, pos.Value.Lon));
            }
        }

        var fp = aircraft.FlightPlan;
        Add(fp.Departure);
        Add(fp.Destination);
        foreach (var fix in navDb.ExpandRoute(fp.Route))
        {
            Add(fix);
        }
        foreach (var nav in aircraft.Targets.NavigationRoute)
        {
            if (seen.Add(nav.Name))
            {
                candidates.Add((nav.Name, nav.Position.Lat, nav.Position.Lon));
            }
        }

        return candidates;
    }

    private static string FormatFrd(string frd, NavigationDatabase navDb)
    {
        var parsed = FrdResolver.ParseFrd(frd);
        if (parsed is null)
        {
            return frd;
        }

        var (fixName, radial, distance) = parsed.Value;
        string label = AnchorLabel(fixName, navDb);
        if (radial is null || distance is null || distance == 0)
        {
            return $"Over {label}";
        }

        string cardinal = BearingToCardinal(radial.Value);
        return $"{distance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} miles {cardinal} of {label}";
    }

    /// <summary>
    /// Renders a position-report anchor with the published friendly name. VHF navaids
    /// take priority over airports because identifiers like "OAK" exist as both an FAA
    /// airport code and a colocated VOR — when an aircraft is flying past the navaid on
    /// route, the controller cares about the navaid label, not the airport's:
    ///   "OAK - Oakland VOR"        (CIFP section D navaid)
    ///   "KOAK - Oakland Airport"   (NavData airport)
    ///   "GROAN intersection"       (named RNAV waypoint, no published friendly name)
    /// </summary>
    private static string AnchorLabel(string code, NavigationDatabase navDb)
    {
        var navaidName = navDb.GetNavaidName(code);
        if (!string.IsNullOrWhiteSpace(navaidName))
        {
            return $"{code} - {TitleCase(navaidName)} VOR";
        }

        var rawAirportName = navDb.GetAirportName(code);
        if (!string.IsNullOrWhiteSpace(rawAirportName))
        {
            return $"{code} - {FriendlyAirportName(rawAirportName)}";
        }

        return $"{code} intersection";
    }

    private static string TitleCase(string raw) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLowerInvariant());

    /// <summary>
    /// Heuristic to convert raw NavData airport names into a short readable label.
    /// Strategy: drop trailing generic suffixes (INTL, MUNI, REGIONAL, METRO, EXEC, etc.),
    /// detect when a compound city name like "SAN FRANCISCO" appears later in the string
    /// (which signals a metro qualifier rather than the airport's own name) and trim
    /// everything from that point on, then title-case and append " Airport".
    /// Examples:
    ///   "OAKLAND SAN FRANCISCO BAY"      → "Oakland Airport"
    ///   "SAN FRANCISCO INTL"             → "San Francisco Airport"
    ///   "JOHN F KENNEDY INTL"            → "John F Kennedy Airport"
    ///   "NORMAN Y MINETA SAN JOSE INTL"  → "Norman Y Mineta Airport"
    /// </summary>
    internal static string FriendlyAirportName(string rawName)
    {
        var tokens = rawName.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && IsGenericAirportSuffix(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (tokens.Count == 0)
        {
            return rawName.Trim();
        }

        // A compound city name (e.g. "SAN FRANCISCO") appearing at position 1+ is a metro
        // qualifier glued onto the airport's actual name (KOAK = "OAKLAND SAN FRANCISCO BAY").
        // Trim it and everything after.
        for (int i = 1; i < tokens.Count - 1; i++)
        {
            var pair = $"{tokens[i]} {tokens[i + 1]}";
            if (CompoundCityNames.Contains(pair, StringComparer.OrdinalIgnoreCase))
            {
                tokens.RemoveRange(i, tokens.Count - i);
                break;
            }
        }

        var titled = string.Join(
            ' ',
            tokens.Select(t => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant()))
        );
        return $"{titled} Airport";
    }

    private static bool IsGenericAirportSuffix(string token) =>
        token.Equals("INTL", StringComparison.OrdinalIgnoreCase)
        || token.Equals("INTERNATIONAL", StringComparison.OrdinalIgnoreCase)
        || token.Equals("MUNI", StringComparison.OrdinalIgnoreCase)
        || token.Equals("MUNICIPAL", StringComparison.OrdinalIgnoreCase)
        || token.Equals("REGIONAL", StringComparison.OrdinalIgnoreCase)
        || token.Equals("RGNL", StringComparison.OrdinalIgnoreCase)
        || token.Equals("METRO", StringComparison.OrdinalIgnoreCase)
        || token.Equals("METROPOLITAN", StringComparison.OrdinalIgnoreCase)
        || token.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
        || token.Equals("EXECUTIVE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("FIELD", StringComparison.OrdinalIgnoreCase)
        || token.Equals("AIRPORT", StringComparison.OrdinalIgnoreCase)
        || token.Equals("AIRPARK", StringComparison.OrdinalIgnoreCase)
        || token.Equals("AIRBASE", StringComparison.OrdinalIgnoreCase);

    // Common US compound city names. When one of these appears at position 1+ inside an
    // airport's published name, it's a metro qualifier (e.g. KOAK's "OAKLAND SAN FRANCISCO
    // BAY" = the OAKLAND airport, with "SAN FRANCISCO" tacked on as the metro descriptor).
    private static readonly string[] CompoundCityNames =
    [
        "SAN FRANCISCO",
        "SAN DIEGO",
        "SAN JOSE",
        "SAN ANTONIO",
        "LOS ANGELES",
        "NEW YORK",
        "NEW ORLEANS",
        "FORT WORTH",
        "FORT LAUDERDALE",
        "FORT MYERS",
        "ST LOUIS",
        "ST PAUL",
        "ST PETERSBURG",
        "OKLAHOMA CITY",
        "KANSAS CITY",
    ];

    private static int RoundToNearest(double value, int increment)
    {
        return (int)(Math.Round(value / increment) * increment);
    }

    private static int NormalizeHeading(int hdg)
    {
        hdg %= 360;
        return hdg <= 0 ? 360 : hdg;
    }

    /// <summary>
    /// Spoken altitude per AIM 4-2-9: at/above FL180, "flight level X" with digits spoken
    /// individually; below FL180, group form "X thousand Y hundred". Trailing zero hundreds
    /// are dropped ("five thousand", not "five thousand zero hundred").
    /// </summary>
    internal static string SpokenAltitude(int altitudeFt)
    {
        if (altitudeFt >= 18000)
        {
            int fl = altitudeFt / 100;
            return $"flight level {SpokenDigits(fl)}";
        }

        int thousands = altitudeFt / 1000;
        int hundreds = (altitudeFt % 1000) / 100;

        var sb = new StringBuilder();
        if (thousands > 0)
        {
            sb.Append(SpokenDigits(thousands));
            sb.Append(" thousand");
        }
        if (hundreds > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(SpokenDigit(hundreds));
            sb.Append(" hundred");
        }
        if (sb.Length == 0)
        {
            sb.Append("zero");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Three-digit spoken heading per AIM 4-2-10: every heading reads as three digits,
    /// "two seven zero" or "three six zero". Unlike SpokenDigits the leading zero is
    /// always preserved.
    /// </summary>
    internal static string SpokenHeading(int hdg)
    {
        hdg = NormalizeHeading(hdg);
        return $"{SpokenDigit(hdg / 100)} {SpokenDigit((hdg / 10) % 10)} {SpokenDigit(hdg % 10)}";
    }

    /// <summary>
    /// Number spoken digit-by-digit per AIM 4-2-8 ("two three zero" for 230). Leading
    /// zeros are dropped (1 → "one", not "zero zero one").
    /// </summary>
    internal static string SpokenDigits(int value)
    {
        if (value < 0)
        {
            return $"minus {SpokenDigits(-value)}";
        }
        if (value == 0)
        {
            return "zero";
        }

        var digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parts = new string[digits.Length];
        for (int i = 0; i < digits.Length; i++)
        {
            parts[i] = SpokenDigit(digits[i] - '0');
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Spoken Mach per AIM 4-2-11: "Mach point seven eight" — leading "0." is dropped,
    /// remaining digits are spoken individually.
    /// </summary>
    internal static string SpokenMach(double mach)
    {
        // Round to two decimals, then strip leading "0." and speak each remaining digit.
        var rounded = Math.Round(mach, 2);
        var formatted = rounded.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var dot = formatted.IndexOf('.');
        var digits = dot >= 0 ? formatted[(dot + 1)..] : formatted;

        var parts = new string[digits.Length];
        for (int i = 0; i < digits.Length; i++)
        {
            parts[i] = SpokenDigit(digits[i] - '0');
        }
        return $"point {string.Join(' ', parts)}";
    }

    /// <summary>
    /// Spell a CIFP approach identifier with the type expanded, an optional phonetic letter
    /// suffix, and digit-by-digit runway. CIFP approach IDs use a single-letter type code:
    /// I=ILS, R=RNAV, V=VOR, L=LOC, B=LOC-BC, S=SDF, N=NDB, D=VOR/DME, etc. (ARINC 424
    /// table 5-9). An optional second letter (Y/Z/W/X/V) disambiguates parallel approaches
    /// and is spoken phonetically. Examples:
    ///   I19L  → "ILS one niner left"
    ///   IZ28R → "ILS Zulu two eight right"
    ///   R09   → "RNAV niner"
    ///   V19   → "VOR one niner"
    /// Falls back to the raw id if the format doesn't match.
    /// </summary>
    internal static string SpokenApproach(string approachId)
    {
        if (string.IsNullOrWhiteSpace(approachId))
        {
            return approachId;
        }

        // Find where the runway portion starts: the first digit.
        int runwayStart = -1;
        for (int i = 0; i < approachId.Length; i++)
        {
            if (char.IsDigit(approachId[i]))
            {
                runwayStart = i;
                break;
            }
        }

        if (runwayStart < 1)
        {
            // No type letters or no runway digits — bail out.
            return approachId;
        }

        var typePart = approachId[..runwayStart];
        var runwayPart = approachId[runwayStart..];

        var sb = new StringBuilder();

        // First letter = type code; expand to full word. If unmapped, leave as the raw letter.
        char typeCode = typePart[0];
        sb.Append(ApproachTypeName(typeCode));

        // Second letter (if present) = phonetic suffix for parallel approaches.
        if (typePart.Length >= 2)
        {
            sb.Append(' ');
            sb.Append(PhoneticLetter(typePart[1]));
        }

        // Runway: digits + optional L/R/C.
        char? runwaySuffix = null;
        var runwayDigits = runwayPart;
        if (runwayPart.Length > 0 && (runwayPart[^1] == 'L' || runwayPart[^1] == 'R' || runwayPart[^1] == 'C'))
        {
            runwaySuffix = runwayPart[^1];
            runwayDigits = runwayPart[..^1];
        }

        if (runwayDigits.Length > 0 && runwayDigits.All(char.IsDigit))
        {
            sb.Append(' ');
            sb.Append(SpokenRunway(runwayDigits));
            if (runwaySuffix is { } s)
            {
                sb.Append(' ');
                sb.Append(
                    s switch
                    {
                        'L' => "left",
                        'R' => "right",
                        'C' => "center",
                        _ => s.ToString(),
                    }
                );
            }
        }
        else
        {
            sb.Append(' ');
            sb.Append(runwayPart);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Speak a runway number digit-by-digit, preserving any leading zero from the chart.
    /// "09" → "zero niner", "19" → "one niner", "9" → "niner". Modern FAA charts use the
    /// 2-digit form for runways &lt; 10 (e.g., RWY 09L), and pilots speak what they see.
    /// </summary>
    private static string SpokenRunway(string runwayDigits)
    {
        var parts = new string[runwayDigits.Length];
        for (int i = 0; i < runwayDigits.Length; i++)
        {
            parts[i] = SpokenDigit(runwayDigits[i] - '0');
        }
        return string.Join(' ', parts);
    }

    /// <summary>ARINC 424 / CIFP single-letter approach type code → full type name.</summary>
    private static string ApproachTypeName(char typeCode) =>
        typeCode switch
        {
            'I' => "ILS",
            'R' => "RNAV",
            'V' => "VOR",
            'L' => "LOC",
            'B' => "LOC backcourse",
            'S' => "SDF",
            'N' => "NDB",
            'D' => "VOR DME",
            'Q' => "NDB DME",
            'P' => "GPS",
            'T' => "TACAN",
            'X' => "LDA",
            'J' => "GLS",
            'M' => "MLS",
            'H' => "RNP",
            _ => typeCode.ToString(),
        };

    private static string SpokenDigit(int digit) =>
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
            9 => "niner",
            _ => digit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private static string PhoneticLetter(char c) =>
        char.ToUpperInvariant(c) switch
        {
            'A' => "Alpha",
            'B' => "Bravo",
            'C' => "Charlie",
            'D' => "Delta",
            'E' => "Echo",
            'F' => "Foxtrot",
            'G' => "Golf",
            'H' => "Hotel",
            'I' => "India",
            'J' => "Juliet",
            'K' => "Kilo",
            'L' => "Lima",
            'M' => "Mike",
            'N' => "November",
            'O' => "Oscar",
            'P' => "Papa",
            'Q' => "Quebec",
            'R' => "Romeo",
            'S' => "Sierra",
            'T' => "Tango",
            'U' => "Uniform",
            'V' => "Victor",
            'W' => "Whiskey",
            'X' => "Xray",
            'Y' => "Yankee",
            'Z' => "Zulu",
            _ => c.ToString(),
        };

    private static string BearingToCardinal(int bearing)
    {
        bearing = ((bearing % 360) + 360) % 360;

        return bearing switch
        {
            >= 338 or < 23 => "north",
            >= 23 and < 68 => "northeast",
            >= 68 and < 113 => "east",
            >= 113 and < 158 => "southeast",
            >= 158 and < 203 => "south",
            >= 203 and < 248 => "southwest",
            >= 248 and < 293 => "west",
            _ => "northwest",
        };
    }
}
