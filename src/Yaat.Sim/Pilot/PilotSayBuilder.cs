using Yaat.Sim.Data;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Generates pilot transmission text for SAY-class verbs (SALT, SHDG, SPOS, SSPD,
/// SMACH, SEAPP, freeform SAY). Pure functions over <see cref="AircraftState"/> with no
/// broadcast or routing concerns — callers (CommandDispatcher for triggered/sequenced
/// SAY blocks; the server's command pipeline for direct controller queries) decide where
/// the resulting text goes.
///
/// Output is plain text with numeric values: "Heading 270, direct MENLO", "Leaving 5,000
/// for FL240", "250 knots", "Mach 0.78", "Expecting the ILS 19L approach". Radio
/// phraseology (digit-by-digit speech, "thousand"/"hundred" forms, "Mach point X") is
/// owned by downstream consumers (RPO readback or a TTS engine), not the message text.
/// </summary>
public static class PilotSayBuilder
{
    public static string BuildAltitude(AircraftState aircraft)
    {
        int alt = RoundToNearest(aircraft.Altitude, 100);
        var target = aircraft.Targets.AssignedAltitude;
        if (target is null)
        {
            return PlainAltitude(alt);
        }

        int targetAlt = RoundToNearest(target.Value, 100);
        if (Math.Abs(alt - targetAlt) < 100)
        {
            return $"Level {PlainAltitude(alt)}";
        }

        return $"Leaving {PlainAltitude(alt)} for {PlainAltitude(targetAlt)}";
    }

    public static string BuildHeading(AircraftState aircraft)
    {
        int hdg = NormalizeHeading(RoundToNearest(aircraft.TrueHeading.Degrees, 5));
        bool isTurning = Math.Abs(aircraft.BankAngle) > 1.0;
        string? turnDir = isTurning ? (aircraft.BankAngle < 0 ? "left" : "right") : null;

        if (aircraft.Targets.NavigationRoute.Count > 0)
        {
            string fixName = aircraft.Targets.NavigationRoute[0].Name;
            return isTurning ? $"Heading {PlainHeading(hdg)}, turning {turnDir} direct {fixName}" : $"Heading {PlainHeading(hdg)}, direct {fixName}";
        }

        if (isTurning && aircraft.Targets.TargetTrueHeading is { } targetHdg)
        {
            int targetRounded = NormalizeHeading(RoundToNearest(targetHdg.Degrees, 5));
            return $"Heading {PlainHeading(hdg)}, turning {turnDir} {PlainHeading(targetRounded)}";
        }

        return $"Heading {PlainHeading(hdg)}";
    }

    public static string BuildSpeed(AircraftState aircraft)
    {
        var ias = (int)Math.Round(aircraft.IndicatedAirspeed);
        if (aircraft.Altitude >= 24000)
        {
            return $"{ias.ToString(System.Globalization.CultureInfo.InvariantCulture)} knots, {BuildMach(aircraft)}";
        }

        return $"{ias.ToString(System.Globalization.CultureInfo.InvariantCulture)} knots";
    }

    public static string BuildMach(AircraftState aircraft)
    {
        var mach = WindInterpolator.IasToMach(aircraft.IndicatedAirspeed, aircraft.Altitude);
        return $"Mach {PlainMach(mach)}";
    }

    public static string BuildExpectedApproach(AircraftState aircraft)
    {
        return aircraft.Approach.Expected is not null
            ? $"Expecting the {PlainApproach(aircraft.Approach.Expected)} approach"
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
    /// Plain altitude form: thousands grouped with comma below FL180 ("5,000",
    /// "17,900"); "FL{xxx}" at or above FL180 ("FL180", "FL250").
    /// </summary>
    internal static string PlainAltitude(int altitudeFt)
    {
        if (altitudeFt >= 18000)
        {
            return $"FL{altitudeFt / 100}";
        }
        return altitudeFt.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Three-digit zero-padded heading: "270", "005", "360".</summary>
    internal static string PlainHeading(int hdg) => new MagneticHeading(hdg).ToDisplayString();

    /// <summary>Plain Mach: "0.78", "0.65", "0.80".</summary>
    internal static string PlainMach(double mach) => Math.Round(mach, 2).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Plain CIFP approach identifier: type letter expanded to a name, optional parallel
    /// suffix split off as a separate token, runway digits and L/R/C kept as-is.
    /// Examples:
    ///   I19L  → "ILS 19L"
    ///   IZ28R → "ILS Z 28R"
    ///   R09   → "RNAV 09"
    ///   V19   → "VOR 19"
    /// Falls back to the raw id if the format doesn't match.
    /// </summary>
    internal static string PlainApproach(string approachId)
    {
        if (string.IsNullOrWhiteSpace(approachId))
        {
            return approachId;
        }

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
            return approachId;
        }

        var typePart = approachId[..runwayStart];
        var runwayPart = approachId[runwayStart..];
        var typeName = ApproachTypeName(typePart[0]);
        return typePart.Length >= 2 ? $"{typeName} {typePart[1]} {runwayPart}" : $"{typeName} {runwayPart}";
    }

    /// <summary>ARINC 424 / CIFP single-letter approach type code → full type name.</summary>
    private static string ApproachTypeName(char typeCode) =>
        typeCode switch
        {
            'I' => "ILS",
            'R' => "RNAV",
            'V' => "VOR",
            'L' => "LOC",
            'B' => "LOC-BC",
            'S' => "SDF",
            'N' => "NDB",
            'D' => "VOR/DME",
            'Q' => "NDB/DME",
            'P' => "GPS",
            'T' => "TACAN",
            'X' => "LDA",
            'J' => "GLS",
            'M' => "MLS",
            'H' => "RNP",
            _ => typeCode.ToString(),
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
