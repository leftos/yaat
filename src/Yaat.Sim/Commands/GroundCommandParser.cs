using PR = Yaat.Sim.Commands.ParseResult<Yaat.Sim.Commands.ParsedCommand>;

namespace Yaat.Sim.Commands;

internal static class GroundCommandParser
{
    /// <summary>
    /// Parses PUSH [@parking|$spot|taxiway] [orientation].
    /// Orientation forms: <c>&lt;C</c> (tail toward cardinal C), <c>&gt;C</c> (face cardinal C),
    /// <c>FACE C</c>, <c>TAIL C</c>, or a second taxiway name (face along push-taxiway toward it).
    /// Cardinals: N, NE, E, SE, S, SW, W, NW.
    /// Examples: PUSH, PUSH &lt;E, PUSH FACE NE, PUSH TE, PUSH TE TAIL W, PUSH TE T, PUSH @A10 &gt;W, PUSH $7A FACE E.
    /// </summary>
    internal static PR ParsePushback(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new PushbackCommand(null, null, null, null, null));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Ok(new PushbackCommand(null, null, null, null, null));
        }

        // Strip optional leading @parking or $spot token; remember which.
        string? parking = null;
        string? spot = null;
        int idx = 0;
        if (tokens[0].StartsWith('@') && tokens[0].Length > 1)
        {
            parking = tokens[0][1..].ToUpperInvariant();
            idx = 1;
        }
        else if (tokens[0].StartsWith('$') && tokens[0].Length > 1)
        {
            spot = tokens[0][1..].ToUpperInvariant();
            idx = 1;
        }

        // Remaining tokens describe taxiway and/or orientation.
        var rest = tokens[idx..];
        bool hasParkingOrSpot = parking is not null || spot is not null;

        // Bare PUSH or just @parking/$spot — no taxiway, no orientation.
        if (rest.Length == 0)
        {
            return PR.Ok(new PushbackCommand(null, null, null, parking, spot));
        }

        // Helper to assemble the result with a taxiway and an optional magnetic facing heading.
        static PushbackCommand Build(MagneticHeading? hdg, string? taxiway, string? facingTwy, string? parking, string? spot) =>
            new(hdg, taxiway, facingTwy, parking, spot);

        // Try to consume an orientation prefix (<X, >X, FACE X, TAIL X) starting at `start`.
        // Returns the resolved magnetic facing heading, or null if no orientation match.
        // `consumed` reports how many tokens were used.
        static (MagneticHeading? Hdg, int Consumed, string? Error) TryOrientation(string[] tokens, int start)
        {
            if (start >= tokens.Length)
            {
                return (null, 0, null);
            }

            var t = tokens[start];

            // <C / >C — single token, arrow + cardinal (no whitespace).
            if (t.Length >= 2 && (t[0] == '<' || t[0] == '>'))
            {
                bool tail = t[0] == '<';
                var card = ParseCardinal(t[1..]);
                if (card is null)
                {
                    return (null, 0, $"invalid cardinal '{t[1..]}' after '{t[0]}'");
                }

                int facing = tail ? (card.Value + 180) % 360 : card.Value;
                if (facing == 0)
                {
                    facing = 360;
                }

                return (new MagneticHeading(facing), 1, null);
            }

            // FACE C / TAIL C — two tokens.
            bool isFace = t.Equals("FACE", StringComparison.OrdinalIgnoreCase);
            bool isTail = t.Equals("TAIL", StringComparison.OrdinalIgnoreCase);
            if (isFace || isTail)
            {
                if (start + 1 >= tokens.Length)
                {
                    return (null, 0, $"{t.ToUpperInvariant()} requires a cardinal direction (N/NE/E/SE/S/SW/W/NW)");
                }

                var card = ParseCardinal(tokens[start + 1]);
                if (card is null)
                {
                    return (null, 0, $"invalid cardinal '{tokens[start + 1]}' after {t.ToUpperInvariant()}");
                }

                int facing = isTail ? (card.Value + 180) % 360 : card.Value;
                if (facing == 0)
                {
                    facing = 360;
                }

                return (new MagneticHeading(facing), 2, null);
            }

            return (null, 0, null);
        }

        // First, try to read an orientation directly (no taxiway): PUSH <E, PUSH FACE E, PUSH @A10 <E, PUSH $7A TAIL W.
        var orient = TryOrientation(rest, 0);
        if (orient.Error is not null)
        {
            return PR.Fail(orient.Error);
        }

        if (orient.Hdg is not null)
        {
            if (orient.Consumed != rest.Length)
            {
                return PR.Fail("unexpected tokens after PUSH orientation");
            }

            return PR.Ok(Build(orient.Hdg, null, null, parking, spot));
        }

        // Otherwise the first remaining token is a taxiway (or a destination name).
        // Reject pure-numeric tokens — PUSH no longer accepts numeric headings.
        if (int.TryParse(rest[0], out _))
        {
            return PR.Fail("PUSH no longer accepts numeric headings — use FACE/TAIL or </> with a cardinal (N, NE, E, SE, S, SW, W, NW)");
        }

        string taxiway = rest[0].ToUpperInvariant();

        if (rest.Length == 1)
        {
            // PUSH TE / PUSH @A10 TE / PUSH $7A TE
            // For parking/spot variants, a trailing token is a facing taxiway; for plain PUSH it's the push-onto taxiway.
            return hasParkingOrSpot ? PR.Ok(Build(null, null, taxiway, parking, spot)) : PR.Ok(Build(null, taxiway, null, parking, spot));
        }

        // Look for an orientation starting at rest[1].
        var orient2 = TryOrientation(rest, 1);
        if (orient2.Error is not null)
        {
            return PR.Fail(orient2.Error);
        }

        if (orient2.Hdg is not null)
        {
            if (1 + orient2.Consumed != rest.Length)
            {
                return PR.Fail("unexpected tokens after PUSH orientation");
            }

            // PUSH TE <E / PUSH TE FACE E / PUSH @A10 FACE E
            // For parking/spot, the taxiway slot is unused; orientation is absolute facing.
            return hasParkingOrSpot ? PR.Ok(Build(orient2.Hdg, null, null, parking, spot)) : PR.Ok(Build(orient2.Hdg, taxiway, null, parking, spot));
        }

        if (rest.Length == 2 && !int.TryParse(rest[1], out _))
        {
            // PUSH TE T → onto TE facing toward T (kept form).
            string facingTwy = rest[1].ToUpperInvariant();
            return PR.Ok(Build(null, taxiway, facingTwy, parking, spot));
        }

        return PR.Fail("unrecognized PUSH arguments");
    }

    /// <summary>
    /// 8-point compass cardinal → magnetic heading degrees.
    /// Returns null for invalid input. North maps to 360 to match display semantics.
    /// </summary>
    private static int? ParseCardinal(string s) =>
        s.ToUpperInvariant() switch
        {
            "N" => 360,
            "NE" => 45,
            "E" => 90,
            "SE" => 135,
            "S" => 180,
            "SW" => 225,
            "W" => 270,
            "NW" => 315,
            _ => null,
        };

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
            new TaxiCommand(
                taxi.Path,
                taxi.HoldShorts,
                destRunway,
                taxi.NoDelete,
                taxi.DestinationParking,
                taxi.CrossRunways,
                taxi.DestinationSpot,
                taxi.PathTurnHints
            )
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
        var pathTurnHints = new List<TurnDirection?>();
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
                pathTurnHints.Add(null);
                continue;
            }

            if (inHoldShort)
            {
                holdShorts.Add(token.ToUpperInvariant());
            }
            else
            {
                // A leading > / < prefixes a per-taxiway turn-direction hint ("> A" = right onto A).
                var (hint, name) = StripTurnHint(token);
                path.Add(name.ToUpperInvariant());
                pathTurnHints.Add(hint);
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
                pathTurnHints.RemoveAt(pathTurnHints.Count - 1);
            }
        }

        // Carry hints only when at least one taxiway was actually prefixed; otherwise leave null so
        // the common un-hinted command is unchanged and the parallel list never desyncs from Path.
        List<TurnDirection?>? turnHints = pathTurnHints.Exists(h => h is not null) ? pathTurnHints : null;

        // Allow empty path when parking/spot destination is set (A* will find the route)
        if (path.Count == 0 && destParking is null && destSpot is null)
        {
            return PR.Fail("empty taxi route");
        }

        return PR.Ok(new TaxiCommand(path, holdShorts, destRunway, noDelete, destParking, crossRunways, destSpot, turnHints));
    }

    /// <summary>
    /// Splits an optional leading turn-direction glyph off a taxiway token: <c>&gt;A</c> → (Right, "A"),
    /// <c>&lt;B7</c> → (Left, "B7"), <c>C</c> → (null, "C"). A bare glyph with no following name is left
    /// intact (returned as the name) so it fails the later taxiway lookup rather than adding an empty leg.
    /// </summary>
    private static (TurnDirection? Hint, string Name) StripTurnHint(string token)
    {
        if (token.Length > 1)
        {
            if (token[0] == '>')
            {
                return (TurnDirection.Right, token[1..]);
            }

            if (token[0] == '<')
            {
                return (TurnDirection.Left, token[1..]);
            }
        }

        return (null, token);
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
    /// Parses TAXIAUTO {runway|@parking}. The handler uses A* pathfinding to
    /// discover a taxiway sequence from the aircraft's current position and
    /// delegates to the regular Taxi pipeline.
    /// </summary>
    internal static PR ParseTaxiAuto(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("TAXIAUTO requires a destination (runway or @parking)");
        }

        var token = arg.Trim();
        if (token.Length == 0)
        {
            return PR.Fail("TAXIAUTO requires a destination (runway or @parking)");
        }

        if (token.StartsWith('@') && token.Length > 1)
        {
            return PR.Ok(new TaxiAutoCommand(DestinationParking: token[1..].ToUpperInvariant()));
        }

        return PR.Ok(new TaxiAutoCommand(DestinationRunway: token.ToUpperInvariant()));
    }

    /// <summary>
    /// Parses RES [CROSS &lt;rwy&gt; [&lt;rwy&gt;...]] [HS &lt;target&gt; [&lt;target&gt;...]].
    /// CROSS and HS modifiers are independent and can appear in either order;
    /// each is a mode that consumes subsequent tokens until the next mode-switch
    /// keyword. Runway/target tokens are uppercased.
    /// </summary>
    internal static PR ParseResume(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new ResumeCommand([], []));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Ok(new ResumeCommand([], []));
        }

        var crossRunways = new List<string>();
        var holdShorts = new List<string>();
        var mode = ParseMode.None;

        foreach (var raw in tokens)
        {
            if (raw.Equals("CROSS", StringComparison.OrdinalIgnoreCase))
            {
                mode = ParseMode.Cross;
                continue;
            }
            if (raw.Equals("HS", StringComparison.OrdinalIgnoreCase))
            {
                mode = ParseMode.HoldShort;
                continue;
            }

            switch (mode)
            {
                case ParseMode.Cross:
                    crossRunways.Add(raw.ToUpperInvariant());
                    break;
                case ParseMode.HoldShort:
                    holdShorts.Add(raw.ToUpperInvariant());
                    break;
                default:
                    return PR.Fail($"RES: unexpected argument '{raw}' (expected CROSS, HS, or no argument)");
            }
        }

        // If a keyword appeared but no targets followed, fail with an actionable message.
        if (mode == ParseMode.Cross && crossRunways.Count == 0)
        {
            return PR.Fail("RES CROSS requires at least one runway");
        }
        if (mode == ParseMode.HoldShort && holdShorts.Count == 0)
        {
            return PR.Fail("RES HS requires at least one target");
        }

        return PR.Ok(new ResumeCommand(crossRunways, holdShorts));
    }

    private enum ParseMode
    {
        None,
        Cross,
        HoldShort,
    }

    /// <summary>
    /// Parses CROSS [runway]. Bare CROSS (no argument) produces
    /// <c>CrossRunwayCommand(null)</c>, which clears the next uncleared
    /// hold-short on the route. CROSS &lt;runway&gt; targets a specific runway.
    /// </summary>
    internal static PR ParseCross(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new CrossRunwayCommand(null));
        }

        var runway = arg.Trim().ToUpperInvariant();
        if (runway.Length == 0)
        {
            return PR.Ok(new CrossRunwayCommand(null));
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
    internal static PR ParseExitTaxiway(string arg)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("EXIT requires a taxiway");
        }

        if (!CommandParser.TryParseNoDeleteFlag(tokens, "EXIT", out var noDelete, out var error))
        {
            return PR.Fail(error);
        }

        return PR.Ok(new ExitTaxiwayCommand(tokens[0].ToUpperInvariant(), noDelete));
    }
}
