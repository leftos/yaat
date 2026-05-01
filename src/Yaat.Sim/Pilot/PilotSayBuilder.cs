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

    /// <summary>
    /// Position report relative to the nearest known fix. Uses the active <see cref="NavigationDatabase"/>
    /// to find the closest fix and its bearing/distance. Falls back to a polite "unable to determine"
    /// message if no fix is reachable, since raw lat/lon would be unintelligible over the radio.
    /// </summary>
    public static string BuildPosition(AircraftState aircraft)
    {
        try
        {
            var fixTuples = NavigationDatabase.Instance.GetFixTuples();
            var frd = FrdResolver.ToFrd(aircraft.Position.Lat, aircraft.Position.Lon, fixTuples);
            if (frd is null)
            {
                return "Unable to determine position";
            }

            var parsed = FrdResolver.ParseFrd(frd);
            if (parsed is null)
            {
                return frd;
            }

            var (fixName, radial, distance) = parsed.Value;
            if (radial is null || distance is null || distance == 0)
            {
                return $"Over {fixName}";
            }

            string cardinal = BearingToCardinal(radial.Value);
            return $"{SpokenDigits(distance.Value)} miles {cardinal} of {fixName}";
        }
        catch (InvalidOperationException)
        {
            return "Unable to determine position";
        }
    }

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
