using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Commands;

internal static class ApproachCommandParser
{
    /// <summary>
    /// Parses CAPP [AT|DCT fix] [CFIX alt] approachId.
    /// The approach ID is always the last token.
    /// AT fix approachId — clear approach at a fix.
    /// DCT fix approachId — direct to fix then approach.
    /// DCT fix CFIX altToken approachId — direct to fix, crossing fix altitude, then approach.
    /// </summary>
    internal static ParsedCommand? ParseCapp(string? arg, IFixLookup? fixes, bool force)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return new ClearedApproachCommand(null, null, force, null, null, null, null, null, null, null, null);
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        // Approach ID is always the last token
        var approachId = tokens[^1].ToUpperInvariant();

        if (tokens.Length == 1)
        {
            // Simple: CAPP ILS28R
            return new ClearedApproachCommand(approachId, null, force, null, null, null, null, null, null, null, null);
        }

        var keyword = tokens[0].ToUpperInvariant();

        if (keyword == "AT" && tokens.Length >= 3 && fixes is not null)
        {
            // AT fixName approachId
            var fixName = tokens[1].ToUpperInvariant();
            var pos = fixes.GetFixPosition(fixName);
            if (pos is null)
            {
                return null;
            }

            return new ClearedApproachCommand(approachId, null, force, fixName, pos.Value.Lat, pos.Value.Lon, null, null, null, null, null);
        }

        if (keyword == "DCT" && tokens.Length >= 3 && fixes is not null)
        {
            // DCT fixName [CFIX altToken] approachId
            var dctFixName = tokens[1].ToUpperInvariant();
            var dctPos = fixes.GetFixPosition(dctFixName);
            if (dctPos is null)
            {
                return null;
            }

            // Check for CFIX keyword: DCT fix CFIX altToken approachId (5 tokens)
            if (tokens.Length == 5 && tokens[2].Equals("CFIX", StringComparison.OrdinalIgnoreCase))
            {
                var (crossAlt, crossAltType) = ParseCfixAltitudeToken(tokens[3]);
                if (crossAlt is null)
                {
                    return null;
                }

                return new ClearedApproachCommand(
                    approachId,
                    null,
                    force,
                    null,
                    null,
                    null,
                    dctFixName,
                    dctPos.Value.Lat,
                    dctPos.Value.Lon,
                    crossAlt,
                    crossAltType
                );
            }

            // Simple DCT: DCT fixName approachId
            return new ClearedApproachCommand(approachId, null, force, null, null, null, dctFixName, dctPos.Value.Lat, dctPos.Value.Lon, null, null);
        }

        return null;
    }

    /// <summary>
    /// Parses CAPPSI approachId [airportCode].
    /// Returns a straight-in approach clearance.
    /// </summary>
    internal static ParsedCommand? ParseCappSi(string? arg, IFixLookup? fixes)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airport = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return new ClearedApproachStraightInCommand(approachId, airport);
    }

    /// <summary>
    /// Parses JAPP approachId [airportCode].
    /// </summary>
    internal static ParsedCommand? ParseJapp(string? arg, IFixLookup? fixes, bool force)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airport = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return new JoinApproachCommand(approachId, airport, force);
    }

    /// <summary>
    /// Parses JAPPSI approachId [airportCode].
    /// </summary>
    internal static ParsedCommand? ParseJappSi(string? arg, IFixLookup? fixes)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airport = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return new JoinApproachStraightInCommand(approachId, airport);
    }

    /// <summary>
    /// Parses JFAC approachId.
    /// </summary>
    internal static ParsedCommand? ParseJfac(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return new JoinFinalApproachCourseCommand(null);
        }

        return new JoinFinalApproachCourseCommand(arg.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Parses JARR starId [transition].
    /// </summary>
    internal static ParsedCommand? ParseJarr(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var starId = tokens[0].ToUpperInvariant();
        var transition = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return new JoinStarCommand(starId, transition);
    }

    internal static ParsedCommand? ParseJawy(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return new JoinAirwayCommand(arg.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Parses JRADO fixRadial — a single token where the last 3 digits are the radial
    /// and the rest is the fix name (e.g., "OAK090" → fix=OAK, radial=090).
    /// </summary>
    internal static ParsedCommand? ParseJrado(string? arg, IFixLookup? fixes)
    {
        return ParseRadialCommand(arg, fixes, outbound: true);
    }

    /// <summary>
    /// Parses JRADI fixRadial — same format as JRADO but inbound.
    /// </summary>
    internal static ParsedCommand? ParseJradi(string? arg, IFixLookup? fixes)
    {
        return ParseRadialCommand(arg, fixes, outbound: false);
    }

    /// <summary>
    /// Parses HOLDP fixName inboundCourse [legLength[M]] [direction] [entry].
    /// Flexible token counts:
    ///   3 tokens: fix course direction OR fix course leg (defaults direction=Right)
    ///   4 tokens: fix course leg direction (standard) OR fix course leg numericLeg (defaults direction=Right)
    ///   5 tokens: fix course leg direction entry
    /// Default direction is Right per 7110.65. Default leg is 1M.
    /// </summary>
    internal static ParsedCommand? ParseHold(string? arg, IFixLookup? fixes)
    {
        if (string.IsNullOrWhiteSpace(arg) || fixes is null)
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            return null;
        }

        var fixName = tokens[0].ToUpperInvariant();
        var pos = fixes.GetFixPosition(fixName);
        if (pos is null)
        {
            return null;
        }

        if (!int.TryParse(tokens[1], out var inboundCourse) || inboundCourse < 0 || inboundCourse > 360)
        {
            return null;
        }

        // Defaults
        TurnDirection direction = TurnDirection.Right;
        double legLength = 1;
        bool isMinuteBased = true;
        HoldingEntry? entry = null;

        if (tokens.Length == 3)
        {
            // 3 tokens: fix course (direction | leg)
            if (TryParseDirection(tokens[2], out var dir3))
            {
                direction = dir3;
            }
            else if (TryParseLeg(tokens[2], out var leg3, out var min3))
            {
                legLength = leg3;
                isMinuteBased = min3;
            }
            else
            {
                return null;
            }
        }
        else if (tokens.Length >= 4)
        {
            // Token[2] is always leg
            if (!TryParseLeg(tokens[2], out legLength, out isMinuteBased))
            {
                return null;
            }

            // Token[3] is direction (or numeric → default direction Right)
            if (TryParseDirection(tokens[3], out var dir4))
            {
                direction = dir4;
            }

            // Token[4] is entry if present
            if (tokens.Length >= 5)
            {
                entry = ParseEntry(tokens[4]);
            }
        }

        return new HoldingPatternCommand(fixName, pos.Value.Lat, pos.Value.Lon, inboundCourse, legLength, isMinuteBased, direction, entry);
    }

    private static bool TryParseDirection(string token, out TurnDirection direction)
    {
        var upper = token.ToUpperInvariant();
        if (upper is "R" or "RIGHT")
        {
            direction = TurnDirection.Right;
            return true;
        }

        if (upper is "L" or "LEFT")
        {
            direction = TurnDirection.Left;
            return true;
        }

        direction = default;
        return false;
    }

    private static bool TryParseLeg(string token, out double legLength, out bool isMinuteBased)
    {
        var upper = token.ToUpperInvariant();
        isMinuteBased = upper.EndsWith('M');
        var valueStr = isMinuteBased ? upper[..^1] : upper;
        if (double.TryParse(valueStr, out legLength) && legLength > 0)
        {
            return true;
        }

        legLength = 0;
        isMinuteBased = false;
        return false;
    }

    private static HoldingEntry? ParseEntry(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "D" => HoldingEntry.Direct,
            "T" => HoldingEntry.Teardrop,
            "P" => HoldingEntry.Parallel,
            _ => null,
        };
    }

    /// <summary>
    /// Parses PTAC heading altitudeHundreds approachId.
    /// Example: "PTAC 280 025 ILS30"
    /// </summary>
    internal static ParsedCommand? ParsePtac(string? arg, IFixLookup? fixes)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 3)
        {
            return null;
        }

        if (!int.TryParse(tokens[0], out var heading) || heading < 1 || heading > 360)
        {
            return null;
        }

        int? altitude = AltitudeResolver.Resolve(tokens[1], fixes);
        if (altitude is null)
        {
            return null;
        }

        var approachId = tokens[2].ToUpperInvariant();
        return new PositionTurnAltitudeClearanceCommand(heading, altitude.Value, approachId);
    }

    /// <summary>
    /// Parses CVIA [altitudeHundreds].
    /// Example: "CVIA" or "CVIA 190"
    /// </summary>
    internal static ParsedCommand? ParseCvia(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return new ClimbViaCommand(null);
        }

        if (!int.TryParse(arg.Trim(), out var value) || value <= 0)
        {
            return null;
        }

        int altitude = value < 1000 ? value * 100 : value;
        return new ClimbViaCommand(altitude);
    }

    /// <summary>
    /// Parses DVIA [altitudeHundreds] or DVIA SPD <speed> <fix>.
    /// Example: "DVIA", "DVIA 040", "DVIA SPD 230 GOSHI"
    /// </summary>
    internal static ParsedCommand? ParseDvia(string? arg, IFixLookup? fixes)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return new DescendViaCommand(null);
        }

        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // DVIA SPD <speed> <fix>
        if (parts.Length >= 3 && parts[0].Equals("SPD", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(parts[1], out var speed) || speed <= 0)
            {
                return null;
            }

            var fixName = parts[2].ToUpperInvariant();
            if (fixes is null)
            {
                return null;
            }

            var fixPos = fixes.GetFixPosition(fixName);
            if (fixPos is null)
            {
                return null;
            }

            return new DescendViaCommand(null, speed, fixName, fixPos.Value.Lat, fixPos.Value.Lon);
        }

        if (!int.TryParse(parts[0], out var value) || value <= 0)
        {
            return null;
        }

        // Altitude in hundreds
        int altitude = value < 1000 ? value * 100 : value;
        return new DescendViaCommand(altitude);
    }

    /// <summary>
    /// Parses CFIX fixName [A|B]altitudeHundreds [speed].
    /// Example: "CFIX SUNOL A040" or "CFIX SUNOL 040 250"
    /// </summary>
    internal static ParsedCommand? ParseCfix(string? arg, IFixLookup? fixes)
    {
        if (string.IsNullOrWhiteSpace(arg) || fixes is null)
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        var fixName = tokens[0].ToUpperInvariant();
        var pos = fixes.GetFixPosition(fixName);
        if (pos is null)
        {
            return null;
        }

        var (altitude, altType) = ParseCfixAltitudeToken(tokens[1]);
        if (altitude is null)
        {
            return null;
        }

        int? speed = null;
        if (tokens.Length >= 3 && int.TryParse(tokens[2], out var parsedSpeed) && parsedSpeed > 0)
        {
            speed = parsedSpeed;
        }

        return new CrossFixCommand(fixName, pos.Value.Lat, pos.Value.Lon, altitude.Value, altType, speed);
    }

    /// <summary>
    /// Parses DEPART fixName heading.
    /// Example: "DEPART SUNOL 270"
    /// </summary>
    internal static ParsedCommand? ParseDepart(string? arg, IFixLookup? fixes)
    {
        if (string.IsNullOrWhiteSpace(arg) || fixes is null)
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2)
        {
            return null;
        }

        var fixName = tokens[0].ToUpperInvariant();
        var pos = fixes.GetFixPosition(fixName);
        if (pos is null)
        {
            return null;
        }

        if (!int.TryParse(tokens[1], out var heading) || heading < 1 || heading > 360)
        {
            return null;
        }

        return new DepartFixCommand(fixName, pos.Value.Lat, pos.Value.Lon, heading);
    }

    /// <summary>
    /// Parses APPS [airportCode].
    /// </summary>
    internal static ParsedCommand? ParseApps(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return new ListApproachesCommand(null);
        }

        return new ListApproachesCommand(arg.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Parses CVA runwayId [LEFT|RIGHT] [FOLLOW callsign].
    /// </summary>
    internal static ParsedCommand? ParseCva(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        string runwayId = tokens[0].ToUpperInvariant();
        PatternDirection? direction = null;
        string? followCallsign = null;

        for (int i = 1; i < tokens.Length; i++)
        {
            var upper = tokens[i].ToUpperInvariant();
            if (upper == "LEFT")
            {
                direction = PatternDirection.Left;
            }
            else if (upper == "RIGHT")
            {
                direction = PatternDirection.Right;
            }
            else if (upper == "FOLLOW" && i + 1 < tokens.Length)
            {
                followCallsign = tokens[i + 1].ToUpperInvariant();
                break;
            }
        }

        return new ClearedVisualApproachCommand(runwayId, null, direction, followCallsign);
    }

    /// <summary>
    /// Shared parser for radial commands (JRADO and JRADI).
    /// Splits a single token like "OAK090" into fix name + radial.
    /// </summary>
    private static ParsedCommand? ParseRadialCommand(string? arg, IFixLookup? fixes, bool outbound)
    {
        if (string.IsNullOrWhiteSpace(arg) || fixes is null)
        {
            return null;
        }

        var token = arg.Trim().ToUpperInvariant();

        // Minimum length: 2-char fix + 3-digit radial = 5 chars
        if (token.Length < 5)
        {
            return null;
        }

        // Last 3 chars must be digits (radial)
        var radialStr = token[^3..];
        if (!radialStr.All(char.IsDigit))
        {
            return null;
        }

        var fixName = token[..^3];
        var pos = fixes.GetFixPosition(fixName);
        if (pos is null)
        {
            return null;
        }

        var radial = int.Parse(radialStr);
        if (radial < 0 || radial > 360)
        {
            return null;
        }

        if (outbound)
        {
            return new JoinRadialOutboundCommand(fixName, pos.Value.Lat, pos.Value.Lon, radial);
        }

        return new JoinRadialInboundCommand(fixName, pos.Value.Lat, pos.Value.Lon, radial);
    }

    /// <summary>
    /// Parses a CFIX altitude token with optional A/B prefix.
    /// A = AtOrAbove, B = AtOrBelow, no prefix = At.
    /// The numeric part is in hundreds (e.g., "040" = 4000ft).
    /// Returns (altitude in feet, CrossFixAltitudeType).
    /// </summary>
    private static (int? Altitude, CrossFixAltitudeType AltType) ParseCfixAltitudeToken(string token)
    {
        CrossFixAltitudeType altType = CrossFixAltitudeType.At;
        var numericPart = token;

        if (token.StartsWith('A') && token.Length > 1)
        {
            altType = CrossFixAltitudeType.AtOrAbove;
            numericPart = token[1..];
        }
        else if (token.StartsWith('B') && token.Length > 1)
        {
            altType = CrossFixAltitudeType.AtOrBelow;
            numericPart = token[1..];
        }

        if (!int.TryParse(numericPart, out var value) || value <= 0)
        {
            return (null, altType);
        }

        int altitude = value < 1000 ? value * 100 : value;
        return (altitude, altType);
    }
}
