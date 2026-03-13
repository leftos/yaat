namespace Yaat.Sim.Commands;

internal static class GroundCommandParser
{
    /// <summary>
    /// Parses PUSH [taxiway] [heading|facing_taxiway].
    /// Examples: PUSH, PUSH 180, PUSH TE, PUSH TE 180, PUSH TE T (onto TE facing toward T).
    /// </summary>
    internal static ParsedCommand? ParsePushback(string? arg)
    {
        if (arg is null)
        {
            return new PushbackCommand();
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // @parking or $spot syntax: PUSH @A10, PUSH $7A, PUSH @A10 180
        if (tokens.Length >= 1 && (tokens[0].StartsWith('@') || tokens[0].StartsWith('$')) && tokens[0].Length > 1)
        {
            bool isSpot = tokens[0].StartsWith('$');
            string name = tokens[0][1..].ToUpperInvariant();
            if (tokens.Length == 1)
            {
                return isSpot ? new PushbackCommand(DestinationSpot: name) : new PushbackCommand(DestinationParking: name);
            }

            if (tokens.Length == 2)
            {
                int? hdg = null;
                string? facingTwy = null;
                if (int.TryParse(tokens[1], out var h) && h >= 1 && h <= 360)
                {
                    hdg = h;
                }
                else
                {
                    facingTwy = tokens[1].ToUpperInvariant();
                }

                return isSpot
                    ? new PushbackCommand(Heading: hdg, FacingTaxiway: facingTwy, DestinationSpot: name)
                    : new PushbackCommand(Heading: hdg, FacingTaxiway: facingTwy, DestinationParking: name);
            }

            return isSpot ? new PushbackCommand(DestinationSpot: name) : new PushbackCommand(DestinationParking: name);
        }

        if (tokens.Length == 1)
        {
            if (int.TryParse(tokens[0], out var heading) && heading >= 1 && heading <= 360)
            {
                return new PushbackCommand(heading);
            }

            return new PushbackCommand(Taxiway: tokens[0].ToUpperInvariant());
        }

        if (tokens.Length == 2 && !int.TryParse(tokens[0], out _))
        {
            string taxiway = tokens[0].ToUpperInvariant();
            if (int.TryParse(tokens[1], out var hdg) && hdg >= 1 && hdg <= 360)
            {
                return new PushbackCommand(hdg, taxiway);
            }

            // Two non-numeric tokens: PUSH TE T → push onto TE facing toward T
            return new PushbackCommand(Taxiway: taxiway, FacingTaxiway: tokens[1].ToUpperInvariant());
        }

        // Fallback: treat whole arg as taxiway name
        return new PushbackCommand(Taxiway: arg.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Parses TAXI path [RWY runway] [HS runway...].
    /// Also handles trailing runway: TAXI T U W 30 → path=[T,U,W], dest=30.
    /// Keywords: HS starts hold-short list, RWY sets destination runway.
    /// </summary>
    internal static ParsedCommand? ParseTaxi(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        return ParseTaxiTokens(arg.Split(' ', StringSplitOptions.RemoveEmptyEntries), detectTrailingRunway: true);
    }

    /// <summary>
    /// Parses RWY {runway} [TAXI] path [HS runway...].
    /// Standalone RWY {runway} (no path) returns AssignRunwayCommand.
    /// </summary>
    internal static ParsedCommand? ParseRwyTaxi(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
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
            return new AssignRunwayCommand(destRunway);
        }

        var result = ParseTaxiTokens(remaining, detectTrailingRunway: false);
        if (result is not TaxiCommand taxi)
        {
            return null;
        }

        return new TaxiCommand(
            taxi.Path,
            taxi.HoldShorts,
            destRunway,
            taxi.NoDelete,
            taxi.DestinationParking,
            taxi.CrossRunways,
            taxi.DestinationSpot
        );
    }

    /// <summary>
    /// Shared taxi token parser. Handles path, HS, RWY keywords, @parking, and $spot tokens.
    /// If detectTrailingRunway is true and no explicit RWY keyword was found,
    /// treats the last path token as a destination runway if it looks like one.
    /// Tokens starting with @ set DestinationParking, $ set DestinationSpot.
    /// </summary>
    internal static ParsedCommand? ParseTaxiTokens(string[] tokens, bool detectTrailingRunway)
    {
        if (tokens.Length == 0)
        {
            return null;
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
            return null;
        }

        return new TaxiCommand(path, holdShorts, destRunway, noDelete, destParking, crossRunways, destSpot);
    }

    /// <summary>
    /// Parses TAXIALL {runway|@spot}.
    /// Each parked aircraft gets A* pathfinding to the destination.
    /// </summary>
    internal static ParsedCommand? ParseTaxiAll(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        var token = arg.Trim();
        if (token.Length == 0)
        {
            return null;
        }

        if (token.StartsWith('@') && token.Length > 1)
        {
            return new TaxiAllCommand(DestinationParking: token[1..].ToUpperInvariant());
        }

        if (token.StartsWith('$') && token.Length > 1)
        {
            return new TaxiAllCommand(DestinationSpot: token[1..].ToUpperInvariant());
        }

        return new TaxiAllCommand(DestinationRunway: token.ToUpperInvariant());
    }

    /// <summary>
    /// Parses CROSS runway.
    /// </summary>
    internal static ParsedCommand? ParseCross(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        var runway = arg.Trim().ToUpperInvariant();
        if (runway.Length == 0)
        {
            return null;
        }

        return new CrossRunwayCommand(runway);
    }

    /// <summary>
    /// Parses HS target (taxiway or runway).
    /// </summary>
    internal static ParsedCommand? ParseHoldShort(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        var target = arg.Trim().ToUpperInvariant();
        if (target.Length == 0)
        {
            return null;
        }

        return new HoldShortCommand(target);
    }

    /// <summary>
    /// Parses FOLLOW callsign.
    /// </summary>
    internal static ParsedCommand? ParseFollow(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        var callsign = arg.Trim();
        if (callsign.Length == 0)
        {
            return null;
        }

        return new FollowCommand(callsign);
    }

    /// <summary>
    /// Parses GIVEWAY / BEHIND callsign.
    /// </summary>
    internal static ParsedCommand? ParseGiveWay(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        // GW {callsign} [{runway/taxiway}]
        // The optional location is a single token (runway or taxiway name).
        // If there are more tokens, this is a compound form handled by ParseBlock.
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var callsign = parts[0];
        var location = parts.Length == 2 ? parts[1].Trim().ToUpperInvariant() : null;
        return new GiveWayCommand(callsign, location);
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
