using PR = Yaat.Sim.Commands.ParseResult<Yaat.Sim.Commands.ParsedCommand>;

namespace Yaat.Sim.Commands;

internal static class GroundCommandParser
{
    /// <summary>
    /// Parses PUSH [taxiway] [heading|facing_taxiway].
    /// Examples: PUSH, PUSH 180, PUSH TE, PUSH TE 180, PUSH TE T (onto TE facing toward T).
    /// </summary>
    internal static PR ParsePushback(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new PushbackCommand(null, null, null, null, null));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // @parking or $spot syntax: PUSH @A10, PUSH $7A, PUSH @A10 180
        if (tokens.Length >= 1 && (tokens[0].StartsWith('@') || tokens[0].StartsWith('$')) && tokens[0].Length > 1)
        {
            bool isSpot = tokens[0].StartsWith('$');
            string name = tokens[0][1..].ToUpperInvariant();
            if (tokens.Length == 1)
            {
                return PR.Ok(isSpot ? new PushbackCommand(null, null, null, null, name) : new PushbackCommand(null, null, null, name, null));
            }

            if (tokens.Length == 2)
            {
                MagneticHeading? hdg = null;
                string? facingTwy = null;
                if (int.TryParse(tokens[1], out var h) && h >= 1 && h <= 360)
                {
                    hdg = new MagneticHeading(h);
                }
                else
                {
                    facingTwy = tokens[1].ToUpperInvariant();
                }

                return PR.Ok(isSpot ? new PushbackCommand(hdg, null, facingTwy, null, name) : new PushbackCommand(hdg, null, facingTwy, name, null));
            }

            return PR.Ok(isSpot ? new PushbackCommand(null, null, null, null, name) : new PushbackCommand(null, null, null, name, null));
        }

        if (tokens.Length == 1)
        {
            if (int.TryParse(tokens[0], out var heading) && heading >= 1 && heading <= 360)
            {
                return PR.Ok(new PushbackCommand(new MagneticHeading(heading), null, null, null, null));
            }

            return PR.Ok(new PushbackCommand(null, tokens[0].ToUpperInvariant(), null, null, null));
        }

        if (tokens.Length == 2 && !int.TryParse(tokens[0], out _))
        {
            string taxiway = tokens[0].ToUpperInvariant();
            if (int.TryParse(tokens[1], out var hdg) && hdg >= 1 && hdg <= 360)
            {
                return PR.Ok(new PushbackCommand(new MagneticHeading(hdg), taxiway, null, null, null));
            }

            // Two non-numeric tokens: PUSH TE T → push onto TE facing toward T
            return PR.Ok(new PushbackCommand(null, taxiway, tokens[1].ToUpperInvariant(), null, null));
        }

        // Fallback: treat whole arg as taxiway name
        return PR.Ok(new PushbackCommand(null, arg.Trim().ToUpperInvariant(), null, null, null));
    }

    /// <summary>
    /// Parses TAXI path [RWY runway] [HS runway...].
    /// Also handles trailing runway: TAXI T U W 30 → path=[T,U,W], dest=30.
    /// Keywords: HS starts hold-short list, RWY sets destination runway.
    /// </summary>
    internal static PR ParseTaxi(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("TAXI requires a path");
        }

        return ParseTaxiTokens(arg.Split(' ', StringSplitOptions.RemoveEmptyEntries), detectTrailingRunway: true);
    }

    /// <summary>
    /// Parses RWY {runway} [TAXI] path [HS runway...].
    /// Standalone RWY {runway} (no path) returns AssignRunwayCommand.
    /// </summary>
    internal static PR ParseRwyTaxi(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("RWY requires a runway ID");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("RWY requires a runway ID");
        }

        string destRunway = tokens[0].ToUpperInvariant();
        int startIdx = 1;

        // Skip optional TAXI keyword
        if (startIdx < tokens.Length && tokens[startIdx].Equals("TAXI", StringComparison.OrdinalIgnoreCase))
        {
            startIdx++;
        }

        var remaining = tokens[startIdx..];
        if (remaining.Length == 0)
        {
            // Standalone RWY {runway} — assign runway without taxi
            return PR.Ok(new AssignRunwayCommand(destRunway));
        }

        var result = ParseTaxiTokens(remaining, detectTrailingRunway: false);
        if (!result.IsSuccess || result.Value is not TaxiCommand taxi)
        {
            return result.IsSuccess ? PR.Fail("invalid RWY taxi path") : result;
        }

        return PR.Ok(
            new TaxiCommand(taxi.Path, taxi.HoldShorts, destRunway, taxi.NoDelete, taxi.DestinationParking, taxi.CrossRunways, taxi.DestinationSpot)
        );
    }

    /// <summary>
    /// Shared taxi token parser. Handles path, HS, RWY keywords, @parking, and $spot tokens.
    /// If detectTrailingRunway is true and no explicit RWY keyword was found,
    /// treats the last path token as a destination runway if it looks like one.
    /// Tokens starting with @ set DestinationParking, $ set DestinationSpot.
    /// </summary>
    internal static PR ParseTaxiTokens(string[] tokens, bool detectTrailingRunway)
    {
        if (tokens.Length == 0)
        {
            return PR.Fail("empty taxi route");
        }

        var path = new List<string>();
        var holdShorts = new List<string>();
        List<string>? crossRunways = null;
        string? destRunway = null;
        string? destParking = null;
        string? destSpot = null;
        bool inHoldShort = false;
        bool inRwy = false;
        bool inCross = false;
        bool noDelete = false;

        foreach (var token in tokens)
        {
            if (token.Equals("NODEL", StringComparison.OrdinalIgnoreCase))
            {
                noDelete = true;
                continue;
            }

            if (token.Equals("HS", StringComparison.OrdinalIgnoreCase))
            {
                inHoldShort = true;
                inRwy = false;
                inCross = false;
                continue;
            }

            if (token.Equals("RWY", StringComparison.OrdinalIgnoreCase))
            {
                inRwy = true;
                inHoldShort = false;
                inCross = false;
                continue;
            }

            if (token.Equals("CROSS", StringComparison.OrdinalIgnoreCase))
            {
                inCross = true;
                inHoldShort = false;
                inRwy = false;
                continue;
            }

            if (inRwy)
            {
                destRunway = token.ToUpperInvariant();
                inRwy = false;
                continue;
            }

            if (inCross)
            {
                crossRunways ??= [];
                crossRunways.Add(token.ToUpperInvariant());
                continue;
            }

            // @token = parking destination, $token = spot destination (strip prefix)
            if (token.StartsWith('@') && token.Length > 1)
            {
                destParking = token[1..].ToUpperInvariant();
                continue;
            }

            if (token.StartsWith('$') && token.Length > 1)
            {
                destSpot = token[1..].ToUpperInvariant();
                continue;
            }

            // #nodeId = node reference (pass through as-is, no uppercasing)
            if (token.StartsWith('#') && token.Length > 1)
            {
                path.Add(token);
                continue;
            }

            if (inHoldShort)
            {
                holdShorts.Add(token.ToUpperInvariant());
            }
            else
            {
                path.Add(token.ToUpperInvariant());
            }
        }

        // If no explicit RWY keyword, check if last path token is a runway
        if (detectTrailingRunway && destRunway is null && path.Count >= 2)
        {
            var last = path[^1];
            if (CommandParser.IsRunwayArg(last))
            {
                destRunway = last;
                path.RemoveAt(path.Count - 1);
            }
        }

        // Allow empty path when parking/spot destination is set (A* will find the route)
        if (path.Count == 0 && destParking is null && destSpot is null)
        {
            return PR.Fail("empty taxi route");
        }

        return PR.Ok(new TaxiCommand(path, holdShorts, destRunway, noDelete, destParking, crossRunways, destSpot));
    }

    /// <summary>
    /// Parses TAXIALL {runway|@spot}.
    /// Each parked aircraft gets A* pathfinding to the destination.
    /// </summary>
    internal static PR ParseTaxiAll(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("TAXIALL requires a destination");
        }

        var token = arg.Trim();
        if (token.Length == 0)
        {
            return PR.Fail("TAXIALL requires a destination");
        }

        if (token.StartsWith('@') && token.Length > 1)
        {
            return PR.Ok(new TaxiAllCommand(DestinationParking: token[1..].ToUpperInvariant()));
        }

        if (token.StartsWith('$') && token.Length > 1)
        {
            return PR.Ok(new TaxiAllCommand(DestinationSpot: token[1..].ToUpperInvariant()));
        }

        return PR.Ok(new TaxiAllCommand(DestinationRunway: token.ToUpperInvariant()));
    }

    /// <summary>
    /// Parses CROSS runway.
    /// </summary>
    internal static PR ParseCross(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("CROSS requires a runway");
        }

        var runway = arg.Trim().ToUpperInvariant();
        if (runway.Length == 0)
        {
            return PR.Fail("CROSS requires a runway");
        }

        return PR.Ok(new CrossRunwayCommand(runway));
    }

    /// <summary>
    /// Parses HS target (taxiway or runway).
    /// </summary>
    internal static PR ParseHoldShort(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("HS requires a target");
        }

        var target = arg.Trim().ToUpperInvariant();
        if (target.Length == 0)
        {
            return PR.Fail("HS requires a target");
        }

        return PR.Ok(new HoldShortCommand(target));
    }

    /// <summary>
    /// Parses FOLLOWG callsign (ground follow).
    /// </summary>
    internal static PR ParseFollowGround(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("FOLLOWG requires a callsign");
        }

        var callsign = arg.Trim();
        if (callsign.Length == 0)
        {
            return PR.Fail("FOLLOWG requires a callsign");
        }

        return PR.Ok(new FollowGroundCommand(callsign));
    }

    /// <summary>
    /// Parses GIVEWAY / BEHIND callsign.
    /// </summary>
    internal static PR ParseGiveWay(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("GIVEWAY requires a callsign");
        }

        // GW {callsign} [{runway/taxiway}]
        // The optional location is a single token (runway or taxiway name).
        // If there are more tokens, this is a compound form handled by ParseBlock.
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return PR.Fail("GIVEWAY requires a callsign");
        }

        var callsign = parts[0];
        var location = parts.Length == 2 ? parts[1].Trim().ToUpperInvariant() : null;
        return PR.Ok(new GiveWayCommand(callsign, location));
    }

    /// <summary>
    /// Parses EXIT taxiway [NODEL].
    /// </summary>
    internal static ParsedCommand ParseExitTaxiway(string arg)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var taxiway = tokens[0].ToUpperInvariant();
        bool noDelete = tokens.Length > 1 && tokens[1].Equals("NODEL", StringComparison.OrdinalIgnoreCase);
        return new ExitTaxiwayCommand(taxiway, noDelete);
    }
}
