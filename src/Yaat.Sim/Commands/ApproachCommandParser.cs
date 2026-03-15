using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using PR = Yaat.Sim.Commands.ParseResult<Yaat.Sim.Commands.ParsedCommand>;

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
    internal static PR ParseCapp(string? arg, NavigationDatabase? navDb, bool force)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Ok(new ClearedApproachCommand(null, null, force, null, null, null, null, null, null, null, null));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("CAPP requires an approach ID");
        }

        // Approach ID is always the last token
        var approachId = tokens[^1].ToUpperInvariant();

        if (tokens.Length == 1)
        {
            // Simple: CAPP ILS28R
            return PR.Ok(new ClearedApproachCommand(approachId, null, force, null, null, null, null, null, null, null, null));
        }

        var keyword = tokens[0].ToUpperInvariant();

        if (keyword == "AT" && tokens.Length >= 3 && navDb is not null)
        {
            // AT fixName approachId
            var fixName = tokens[1].ToUpperInvariant();
            var pos = navDb.GetFixPosition(fixName);
            if (pos is null)
            {
                return PR.Fail($"fix '{fixName}' not found");
            }

            return PR.Ok(new ClearedApproachCommand(approachId, null, force, fixName, pos.Value.Lat, pos.Value.Lon, null, null, null, null, null));
        }

        if (keyword == "DCT" && tokens.Length >= 3 && navDb is not null)
        {
            // DCT fixName [CFIX altToken] approachId
            var dctFixName = tokens[1].ToUpperInvariant();
            var dctPos = navDb.GetFixPosition(dctFixName);
            if (dctPos is null)
            {
                return PR.Fail($"fix '{dctFixName}' not found");
            }

            // Check for CFIX keyword: DCT fix CFIX altToken approachId (5 tokens)
            if (tokens.Length == 5 && tokens[2].Equals("CFIX", StringComparison.OrdinalIgnoreCase))
            {
                var (crossAlt, crossAltType) = ParseCfixAltitudeToken(tokens[3]);
                if (crossAlt is null)
                {
                    return PR.Fail($"invalid crossing altitude '{tokens[3]}'");
                }

                return PR.Ok(
                    new ClearedApproachCommand(
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
                    )
                );
            }

            // Simple DCT: DCT fixName approachId
            return PR.Ok(
                new ClearedApproachCommand(approachId, null, force, null, null, null, dctFixName, dctPos.Value.Lat, dctPos.Value.Lon, null, null)
            );
        }

        // 2 tokens: approachId airportCode (e.g., "CAPP I9 MIA")
        // Reject if second token is a known command verb (prevents greedy over-matching)
        if (tokens.Length == 2 && !CommandRegistry.AliasToCanonicType.ContainsKey(approachId))
        {
            return PR.Ok(new ClearedApproachCommand(keyword, approachId, force, null, null, null, null, null, null, null, null));
        }

        return PR.Fail($"invalid CAPP args '{arg}'");
    }

    /// <summary>
    /// Parses CAPPSI approachId [airportCode].
    /// Returns a straight-in approach clearance.
    /// </summary>
    internal static PR ParseCappSi(string? arg, NavigationDatabase? navDb)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("CAPPSI requires an approach ID");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("CAPPSI requires an approach ID");
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airport = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return PR.Ok(new ClearedApproachStraightInCommand(approachId, airport));
    }

    /// <summary>
    /// Parses JAPP approachId [airportCode].
    /// </summary>
    internal static PR ParseJapp(string? arg, NavigationDatabase? navDb, bool force)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("JAPP requires an approach ID");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("JAPP requires an approach ID");
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airport = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return PR.Ok(new JoinApproachCommand(approachId, airport, force));
    }

    /// <summary>
    /// Parses JAPPSI approachId [airportCode].
    /// </summary>
    internal static PR ParseJappSi(string? arg, NavigationDatabase? navDb)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("JAPPSI requires an approach ID");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("JAPPSI requires an approach ID");
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airport = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return PR.Ok(new JoinApproachStraightInCommand(approachId, airport));
    }

    /// <summary>
    /// Parses JFAC approachId.
    /// </summary>
    internal static PR ParseJfac(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Ok(new JoinFinalApproachCourseCommand(null));
        }

        return PR.Ok(new JoinFinalApproachCourseCommand(arg.Trim().ToUpperInvariant()));
    }

    /// <summary>
    /// Parses JARR starId [transition].
    /// </summary>
    internal static PR ParseJarr(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("JARR requires a STAR ID");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("JARR requires a STAR ID");
        }

        var starId = tokens[0].ToUpperInvariant();
        var transition = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return PR.Ok(new JoinStarCommand(starId, transition));
    }

    internal static PR ParseJawy(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("JAWY requires an airway ID");
        }

        return PR.Ok(new JoinAirwayCommand(arg.Trim().ToUpperInvariant()));
    }

    /// <summary>
    /// Parses JRADO fixRadial — a single token where the last 3 digits are the radial
    /// and the rest is the fix name (e.g., "OAK090" → fix=OAK, radial=090).
    /// </summary>
    internal static PR ParseJrado(string? arg, NavigationDatabase? navDb)
    {
        return ParseRadialCommand(arg, navDb, outbound: true);
    }

    /// <summary>
    /// Parses JRADI fixRadial — same format as JRADO but inbound.
    /// </summary>
    internal static PR ParseJradi(string? arg, NavigationDatabase? navDb)
    {
        return ParseRadialCommand(arg, navDb, outbound: false);
    }

    /// <summary>
    /// Parses HOLDP fixName inboundCourse [legLength[M]] [direction] [entry].
    /// Flexible token counts:
    ///   3 tokens: fix course direction OR fix course leg (defaults direction=Right)
    ///   4 tokens: fix course leg direction (standard) OR fix course leg numericLeg (defaults direction=Right)
    ///   5 tokens: fix course leg direction entry
    /// Default direction is Right per 7110.65. Default leg is 1M.
    /// </summary>
    internal static PR ParseHold(string? arg, NavigationDatabase? navDb)
    {
        if (string.IsNullOrWhiteSpace(arg) || navDb is null)
        {
            return PR.Fail("HOLDP requires fix name and inbound course");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            return PR.Fail("HOLDP requires fix name, inbound course, and direction/leg");
        }

        var fixName = tokens[0].ToUpperInvariant();
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            return PR.Fail($"fix '{fixName}' not found");
        }

        if (!int.TryParse(tokens[1], out var inboundCourse) || inboundCourse < 0 || inboundCourse > 360)
        {
            return PR.Fail($"invalid inbound course '{tokens[1]}'");
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
                return PR.Fail($"invalid hold direction or leg '{tokens[2]}'");
            }
        }
        else if (tokens.Length >= 4)
        {
            // Token[2] is always leg
            if (!TryParseLeg(tokens[2], out legLength, out isMinuteBased))
            {
                return PR.Fail($"invalid hold leg length '{tokens[2]}'");
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

        return PR.Ok(new HoldingPatternCommand(fixName, pos.Value.Lat, pos.Value.Lon, inboundCourse, legLength, isMinuteBased, direction, entry));
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
    /// Parses PTAC [heading|PH] [altitude|PA] [approachId].
    /// Supports flexible token counts with PH (present heading) and PA (present altitude).
    /// Examples: "PTAC 280 025 ILS30", "PTAC PH PA ILS30", "PTAC PH PA", "PTAC 280 025", "PTAC"
    /// </summary>
    internal static PR ParsePtac(string? arg, NavigationDatabase? navDb)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Ok(new PositionTurnAltitudeClearanceCommand(null, null, null));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 2 || tokens.Length > 3)
        {
            return PR.Fail("PTAC requires [heading|PH] [altitude|PA] [approachId]");
        }

        // Token 0: heading — integer 1-360 or PH
        int? heading;
        if (tokens[0].Equals("PH", StringComparison.OrdinalIgnoreCase))
        {
            heading = null;
        }
        else if (int.TryParse(tokens[0], out var h) && h >= 1 && h <= 360)
        {
            heading = h;
        }
        else
        {
            return PR.Fail($"invalid PTAC heading '{tokens[0]}' (expected 1-360 or PH)");
        }

        // Token 1: altitude — hundreds or PA
        int? altitude;
        if (tokens[1].Equals("PA", StringComparison.OrdinalIgnoreCase))
        {
            altitude = null;
        }
        else
        {
            altitude = AltitudeResolver.Resolve(tokens[1], navDb);
            if (altitude is null)
            {
                return PR.Fail($"invalid PTAC altitude '{tokens[1]}' (expected altitude in hundreds or PA)");
            }
        }

        // Token 2: approachId (optional)
        string? approachId = tokens.Length == 3 ? tokens[2].ToUpperInvariant() : null;

        return PR.Ok(new PositionTurnAltitudeClearanceCommand(heading, altitude, approachId));
    }

    /// <summary>
    /// Parses CVIA [altitudeHundreds].
    /// Example: "CVIA" or "CVIA 190"
    /// </summary>
    internal static PR ParseCvia(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Ok(new ClimbViaCommand(null));
        }

        if (!int.TryParse(arg.Trim(), out var value) || value <= 0)
        {
            return PR.Fail($"invalid CVIA altitude '{arg}'");
        }

        int altitude = value < 1000 ? value * 100 : value;
        return PR.Ok(new ClimbViaCommand(altitude));
    }

    /// <summary>
    /// Parses DVIA [altitudeHundreds] or DVIA SPD &lt;speed&gt; &lt;fix&gt;.
    /// Example: "DVIA", "DVIA 040", "DVIA SPD 230 GOSHI"
    /// </summary>
    internal static PR ParseDvia(string? arg, NavigationDatabase? navDb)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Ok(new DescendViaCommand(null));
        }

        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // DVIA SPD <speed> [fix]
        if (parts.Length >= 2 && parts[0].Equals("SPD", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(parts[1], out var speed) || speed <= 0)
            {
                return PR.Fail($"invalid DVIA speed '{parts[1]}'");
            }

            if (parts.Length < 3)
            {
                return PR.Ok(new DescendViaCommand(null, speed));
            }

            var fixName = parts[2].ToUpperInvariant();
            if (navDb is null)
            {
                return PR.Fail("DVIA SPD fix requires navdata");
            }

            var fixPos = navDb.GetFixPosition(fixName);
            if (fixPos is null)
            {
                return PR.Fail($"fix '{fixName}' not found");
            }

            return PR.Ok(new DescendViaCommand(null, speed, fixName, fixPos.Value.Lat, fixPos.Value.Lon));
        }

        if (!int.TryParse(parts[0], out var value) || value <= 0)
        {
            // Non-numeric arg: treat as STAR name (e.g., "DVIA HHOOD5" = join STAR + descend via)
            return PR.Ok(new JoinStarCommand(parts[0].ToUpperInvariant(), parts.Length > 1 ? parts[1].ToUpperInvariant() : null));
        }

        // Altitude in hundreds
        int altitude = value < 1000 ? value * 100 : value;
        return PR.Ok(new DescendViaCommand(altitude));
    }

    /// <summary>
    /// Parses CFIX fixName [A|B|AT]altitudeHundreds [speed].
    /// Accepts: "CFIX SUNOL A040", "CFIX SUNOL 040 250", "CFIX SUNOL AT 360"
    /// The "AT" keyword form splits altitude into a separate token.
    /// </summary>
    internal static PR ParseCfix(string? arg, NavigationDatabase? navDb)
    {
        if (string.IsNullOrWhiteSpace(arg) || navDb is null)
        {
            return PR.Fail("CFIX requires fix name and altitude");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return PR.Fail("CFIX requires fix name and altitude");
        }

        var fixName = tokens[0].ToUpperInvariant();
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            return PR.Fail($"fix '{fixName}' not found");
        }

        int altTokenIndex = 1;
        int? altitude;
        CrossFixAltitudeType altType;

        // "CFIX SCTRR AT 360" — explicit AT keyword separates fix from altitude
        if (tokens[1].Equals("AT", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 3)
        {
            altTokenIndex = 2;
            (altitude, _) = ParseCfixAltitudeToken(tokens[2]);
            if (altitude is null)
            {
                return PR.Fail($"invalid CFIX altitude '{tokens[2]}'");
            }

            altType = CrossFixAltitudeType.At;
        }
        else
        {
            (altitude, altType) = ParseCfixAltitudeToken(tokens[1]);
            if (altitude is null)
            {
                return PR.Fail($"invalid CFIX altitude '{tokens[1]}'");
            }
        }

        int? speed = null;
        if (tokens.Length > altTokenIndex + 1 && int.TryParse(tokens[altTokenIndex + 1], out var parsedSpeed) && parsedSpeed > 0)
        {
            speed = parsedSpeed;
        }

        return PR.Ok(new CrossFixCommand(fixName, pos.Value.Lat, pos.Value.Lon, altitude.Value, altType, speed));
    }

    /// <summary>
    /// Parses DEPART fixName heading.
    /// Example: "DEPART SUNOL 270"
    /// </summary>
    internal static PR ParseDepart(string? arg, NavigationDatabase? navDb)
    {
        if (string.IsNullOrWhiteSpace(arg) || navDb is null)
        {
            return PR.Fail("DEPART requires fix name and heading");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2)
        {
            return PR.Fail("DEPART requires exactly fix name and heading");
        }

        var fixName = tokens[0].ToUpperInvariant();
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            return PR.Fail($"fix '{fixName}' not found");
        }

        if (!int.TryParse(tokens[1], out var heading) || heading < 1 || heading > 360)
        {
            return PR.Fail($"invalid DEPART heading '{tokens[1]}'");
        }

        return PR.Ok(new DepartFixCommand(fixName, pos.Value.Lat, pos.Value.Lon, heading));
    }

    /// <summary>
    /// Parses APPS [airportCode].
    /// </summary>
    internal static ParsedCommand ParseApps(string? arg)
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
    internal static PR ParseCva(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("CVA requires a runway ID");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("CVA requires a runway ID");
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

        return PR.Ok(new ClearedVisualApproachCommand(runwayId, null, direction, followCallsign));
    }

    /// <summary>
    /// Shared parser for radial commands (JRADO and JRADI).
    /// Splits a single token like "OAK090" into fix name + radial.
    /// </summary>
    private static PR ParseRadialCommand(string? arg, NavigationDatabase? navDb, bool outbound)
    {
        if (string.IsNullOrWhiteSpace(arg) || navDb is null)
        {
            return PR.Fail("radial command requires fixRadial (e.g., OAK090)");
        }

        var token = arg.Trim().ToUpperInvariant();

        // Minimum length: 2-char fix + 3-digit radial = 5 chars
        if (token.Length < 5)
        {
            return PR.Fail($"invalid radial format '{arg}' (too short)");
        }

        // Last 3 chars must be digits (radial)
        var radialStr = token[^3..];
        if (!radialStr.All(char.IsDigit))
        {
            return PR.Fail($"invalid radial format '{arg}' (last 3 chars must be digits)");
        }

        var fixName = token[..^3];
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            return PR.Fail($"fix '{fixName}' not found");
        }

        var radial = int.Parse(radialStr);
        if (radial < 0 || radial > 360)
        {
            return PR.Fail($"invalid radial {radial} (expected 0-360)");
        }

        if (outbound)
        {
            return PR.Ok(new JoinRadialOutboundCommand(fixName, pos.Value.Lat, pos.Value.Lon, radial));
        }

        return PR.Ok(new JoinRadialInboundCommand(fixName, pos.Value.Lat, pos.Value.Lon, radial));
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
