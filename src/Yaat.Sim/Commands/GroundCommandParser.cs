using Yaat.Sim.Data.Airport;
using PR = Yaat.Sim.Commands.ParseResult<Yaat.Sim.Commands.ParsedCommand>;

namespace Yaat.Sim.Commands;

internal static class GroundCommandParser
{
    /// <summary>
    /// Parses PUSH [@parking|$spot|taxiway] [orientation].
    /// Orientation forms: <c>&lt;C</c> (tail toward cardinal C), <c>&gt;C</c> (face cardinal C),
    /// <c>FACE C</c>, <c>TAIL C</c>, or a second taxiway name (face along push-taxiway toward it).
    /// Cardinals: N, NE, E, SE, S, SW, W, NW.
    /// Examples: PUSH, PUSH &lt;E, PUSH FACE NE, PUSH TE, PUSH TE TAIL W, PUSH TE T, PUSH @A10, PUSH $7A FACE E.
    /// A stand destination takes no facing, neither an orientation nor a facing taxiway: the aircraft parks on the
    /// stand's own heading.
    /// </summary>
    internal static PR ParsePushback(string? arg)
    {
        PR parsed = ParsePushbackForm(arg);
        if (
            parsed.Value is PushbackCommand { DestinationParking: { } stand } push
            && ((push.MagneticHeading is not null) || (push.FacingTaxiway is not null))
        )
        {
            return PR.Fail(StandFacingRefusal(stand));
        }

        return parsed;
    }

    /// <summary>Why a <c>PUSH @stand</c> with a facing is refused; the handler refuses a hand-built one the same way.</summary>
    /// <param name="stand">The stand's name as typed.</param>
    /// <returns>The refusal text.</returns>
    internal static string StandFacingRefusal(string stand) =>
        $"PUSH @{stand} does not take a facing — the aircraft parks on the stand's own heading";

    private static PR ParsePushbackForm(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new PushbackCommand(null, null, null, null, null));
        }

        string[] tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

        // Remaining tokens describe taxiway and/or orientation. A sigil with no name behind it names
        // nothing, and only the first token carries a destination sigil, so a later one would be misread
        // as a facing taxiway (PUSH TE @B27) — refuse both rather than guess.
        string[] rest = tokens[idx..];
        foreach (string token in rest)
        {
            if (token == "@")
            {
                return PR.Fail("@ needs a gate or helipad name — PUSH @A10");
            }

            if (token == "$")
            {
                return PR.Fail("$ needs a spot name — PUSH $7A");
            }

            if ((token.Length > 1) && (token.StartsWith('@') || token.StartsWith('$')))
            {
                return PR.Fail(
                    $"'{token.ToUpperInvariant()}' must be the first PUSH argument — a @gate or $spot destination comes before any taxiway or facing"
                );
            }
        }

        bool hasParkingOrSpot = parking is not null || spot is not null;

        // Bare PUSH or just @parking/$spot — no taxiway, no orientation.
        if (rest.Length == 0)
        {
            return PR.Ok(new PushbackCommand(null, null, null, parking, spot));
        }

        // Helper to assemble the result with a taxiway and an optional magnetic facing heading.
        static PushbackCommand Build(MagneticHeading? hdg, string? taxiway, string? facingTwy, string? parking, string? spot) =>
            new(hdg, taxiway, facingTwy, parking, spot);

        // First, try to read an orientation directly (no taxiway): PUSH <E, PUSH FACE E, PUSH $7A TAIL W.
        (MagneticHeading? Hdg, int Consumed, string? Error) orient = TryOrientation(rest, 0);
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
        (MagneticHeading? Hdg, int Consumed, string? Error) orient2 = TryOrientation(rest, 1);
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

            // PUSH TE <E / PUSH TE FACE E / PUSH $7A FACE E
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
    /// Parses PUSHM &lt;target&gt; &lt;target&gt; [&lt;target&gt; …] [orientation] — a tug move through two or more
    /// ramp points. A target is a spot (<c>$6A</c>), a parking stand or helipad (<c>@D15</c>) or a graph node
    /// (<c>#1926</c>), and the sigil is mandatory on every one of them: without it a name is ambiguous between a
    /// spot, a gate and a taxiway. The optional trailing orientation is the final rest facing, in the same
    /// 8-point cardinal grammar <c>PUSH</c> uses (<c>FACE C</c>, <c>TAIL C</c>, <c>&gt;C</c>, <c>&lt;C</c>).
    /// Examples: PUSHM $6A $6B, PUSHM #1926 $5A, PUSHM @D15 $6A FACE E.
    /// </summary>
    internal static PR ParsePushbackMulti(string? arg)
    {
        string[] tokens = arg?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var targets = new List<string>();
        MagneticHeading? finalFacing = null;

        for (int i = 0; i < tokens.Length; i++)
        {
            (MagneticHeading? Hdg, int Consumed, string? Error) orient = TryOrientation(tokens, i);
            if (orient.Error is not null)
            {
                return PR.Fail(orient.Error);
            }

            if (orient.Hdg is not null)
            {
                if (i + orient.Consumed != tokens.Length)
                {
                    return PR.Fail("takes the facing last — put FACE/TAIL or </> with a cardinal after the final target");
                }

                finalFacing = orient.Hdg;
                break;
            }

            if (!IsTargetToken(tokens[i]))
            {
                return PR.Fail(
                    $"target '{tokens[i]}' needs a sigil — $ for a spot ($6A), @ for a gate or helipad (@D15), # for a graph node (#1926)"
                );
            }

            targets.Add(NormalizeTargetToken(tokens[i]));
        }

        if (targets.Count < 2)
        {
            return PR.Fail("needs at least two targets — use PUSH to move to a single one");
        }

        return PR.Ok(new PushbackMultiCommand(targets, finalFacing));
    }

    /// <summary>Whether the token names a tug-move target: <c>$spot</c>, <c>@parking</c> or <c>#nodeId</c>.</summary>
    private static bool IsTargetToken(string token) =>
        token[0] == '#' ? NodeRefToken.IsNodeReference(token) : (((token[0] == '$') || (token[0] == '@')) && (token.Length > 1));

    /// <summary>
    /// A target token in canonical shape: the sigil kept (it is the only thing that separates spot <c>$7</c>
    /// from gate <c>@7</c>) and the name upper-cased. A <c>#id</c> is already canonical.
    /// </summary>
    private static string NormalizeTargetToken(string token) => token[0] == '#' ? token : string.Concat(token[..1], token[1..].ToUpperInvariant());

    /// <summary>
    /// Reads an orientation (<c>&lt;C</c>, <c>&gt;C</c>, <c>FACE C</c>, <c>TAIL C</c>) starting at
    /// <paramref name="start"/>. Returns the resolved magnetic facing, how many tokens it used, and a message
    /// when the tokens looked like an orientation but did not parse as one.
    /// </summary>
    private static (MagneticHeading? Hdg, int Consumed, string? Error) TryOrientation(string[] tokens, int start)
    {
        if (start >= tokens.Length)
        {
            return (null, 0, null);
        }

        string t = tokens[start];

        // <C / >C — single token, arrow + cardinal (no whitespace).
        if (t.Length >= 2 && ((t[0] == '<') || (t[0] == '>')))
        {
            bool tail = t[0] == '<';
            int? card = ParseCardinal(t[1..]);
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

            int? card = ParseCardinal(tokens[start + 1]);
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

    /// <summary>The eight cardinal tokens the pushback grammar accepts, in 45° order from north.</summary>
    private static readonly string[] Cardinals = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>
    /// The cardinal token that renders a facing back into the command grammar (90 → <c>E</c>) — the inverse of
    /// <see cref="ParseCardinal"/> over the eight headings it produces, so a canonical form round-trips. A
    /// facing that is not one of the eight snaps to the nearest, the rule the ground view's own facing menu
    /// already applies when it composes a <c>PUSH FACE &lt;cardinal&gt;</c>.
    /// </summary>
    internal static string CardinalToken(MagneticHeading heading)
    {
        int bucket = (int)Math.Round((((heading.Degrees % 360.0) + 360.0) % 360.0) / 45.0) % 8;
        return Cardinals[bucket];
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
    /// Also handles trailing runway: TAXI T U W 30 → path=[T,U,W], dest=30, and a lone runway:
    /// TAXI 1L → path=[], dest=1L (only honoured when the aircraft is already at that runway).
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

        string[] tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

        string[] remaining = tokens[startIdx..];
        if (remaining.Length == 0)
        {
            // Standalone RWY {runway} — assign runway without taxi
            return PR.Ok(new AssignRunwayCommand(destRunway));
        }

        PR result = ParseTaxiTokens(remaining, detectTrailingRunway: false);
        if (!result.IsSuccess || result.Value is not TaxiCommand taxi)
        {
            return result.IsSuccess ? PR.Fail("invalid RWY taxi path") : result;
        }

        if (taxi.DestinationParking is not null || taxi.DestinationSpot is not null)
        {
            return PR.Fail("a taxi clearance cannot name both a runway (RWY) and a parking/spot destination");
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
    /// Shared taxi token parser. Handles path, HS, RWY, CROSS keywords, @parking, and $spot tokens.
    /// If detectTrailingRunway is true and no explicit RWY keyword was found,
    /// treats the last path token as a destination runway if it looks like one.
    /// Outside an HS clause, tokens starting with @ set DestinationParking and $ set DestinationSpot.
    /// An HS clause owns every following token until the next keyword: <c>$17</c> there is a spot
    /// hold-short target and <c>@A12</c> is rejected (ATCTrainer grammar — the path optionally ends
    /// with a runway, parking, or spot destination, then the hold-short list follows).
    /// </summary>
    internal static PR ParseTaxiTokens(string[] tokens, bool detectTrailingRunway)
    {
        if (tokens.Length == 0)
        {
            return PR.Fail("empty taxi route");
        }

        var path = new List<string>();
        var pathTurnHints = new List<TurnDirection?>();
        var holdShorts = new List<HoldShortTarget>();
        List<string>? crossRunways = null;
        string? destRunway = null;
        string? destParking = null;
        string? destSpot = null;
        bool inHoldShort = false;
        bool inRwy = false;
        bool inCross = false;
        bool noDelete = false;

        foreach (string token in tokens)
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

            if (inHoldShort)
            {
                if (!HoldShortTarget.TryParse(token, out HoldShortTarget holdShortTarget, out string? holdShortError))
                {
                    return PR.Fail(holdShortError!);
                }

                holdShorts.Add(holdShortTarget);
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

            // A leading > / < prefixes a per-taxiway turn-direction hint ("> A" = right onto A).
            (TurnDirection? hint, string? name) = StripTurnHint(token);
            path.Add(name.ToUpperInvariant());
            pathTurnHints.Add(hint);
        }

        // If no explicit RWY keyword, check if last path token is a runway. A lone runway token
        // (TAXI 1L) is a destination too — the aircraft is expected to already be at that runway;
        // TryTaxi enforces that. A runway followed by taxiways (TAXI 28R G D) stays a path
        // segment the aircraft taxis along, as does a runway before a parking / spot destination
        // (TAXI G 28R @B12 taxis along 28R to the ramp — the destination is the ramp).
        if (detectTrailingRunway && destRunway is null && destParking is null && destSpot is null && path.Count >= 1)
        {
            string last = path[^1];
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

        // A takeoff-runway assignment and a ramp destination contradict each other (7110.65 §3-7-2.b's
        // leading-runway form is for an assigned takeoff runway).
        if (destRunway is not null && (destParking is not null || destSpot is not null))
        {
            return PR.Fail("a taxi clearance cannot name both a runway (RWY) and a parking/spot destination");
        }

        // Allow an empty path when a destination is set (the handler resolves the route)
        if (path.Count == 0 && destRunway is null && destParking is null && destSpot is null)
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

        string token = arg.Trim();
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
    /// Parses TAXIAUTO {runway|@parking|$spot} — the same destination sigils as TAXI. The handler uses A*
    /// pathfinding to discover a taxiway sequence from the aircraft's current position and delegates to the
    /// regular Taxi pipeline.
    /// </summary>
    internal static PR ParseTaxiAuto(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("TAXIAUTO requires a destination (runway, @parking, or $spot)");
        }

        string token = arg.Trim();
        if (token.Length == 0)
        {
            return PR.Fail("TAXIAUTO requires a destination (runway, @parking, or $spot)");
        }

        if (token.StartsWith('@') && token.Length > 1)
        {
            return PR.Ok(new TaxiAutoCommand(DestinationRunway: null, DestinationParking: token[1..].ToUpperInvariant(), DestinationSpot: null));
        }

        if (token.StartsWith('$') && token.Length > 1)
        {
            return PR.Ok(new TaxiAutoCommand(DestinationRunway: null, DestinationParking: null, DestinationSpot: token[1..].ToUpperInvariant()));
        }

        return PR.Ok(new TaxiAutoCommand(DestinationRunway: token.ToUpperInvariant(), DestinationParking: null, DestinationSpot: null));
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

        string[] tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Ok(new ResumeCommand([], []));
        }

        var crossRunways = new List<string>();
        var holdShorts = new List<HoldShortTarget>();
        ParseMode mode = ParseMode.None;

        foreach (string raw in tokens)
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
                    if (!HoldShortTarget.TryParse(raw, out HoldShortTarget holdShortTarget, out string? holdShortError))
                    {
                        return PR.Fail(holdShortError!);
                    }

                    holdShorts.Add(holdShortTarget);
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
    /// Parses CROSS [&lt;rwy&gt; [&lt;rwy&gt;...]] [HS &lt;target&gt; [&lt;target&gt;...]].
    /// The verb starts in "cross" mode, so leading tokens are crossing runways; an
    /// <c>HS</c> keyword switches to hold-short targets. Bare CROSS (no argument)
    /// produces <c>CrossRunwayCommand([], [])</c>, which clears the next uncleared
    /// hold-short on the route. Mirrors <see cref="ParseResume"/>'s mode loop.
    /// </summary>
    internal static PR ParseCross(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new CrossRunwayCommand([], []));
        }

        string[] tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Ok(new CrossRunwayCommand([], []));
        }

        var crossRunways = new List<string>();
        var holdShorts = new List<HoldShortTarget>();
        ParseMode mode = ParseMode.Cross;

        foreach (string raw in tokens)
        {
            if (raw.Equals("HS", StringComparison.OrdinalIgnoreCase))
            {
                mode = ParseMode.HoldShort;
                continue;
            }
            if (raw.Equals("CROSS", StringComparison.OrdinalIgnoreCase))
            {
                mode = ParseMode.Cross;
                continue;
            }

            switch (mode)
            {
                case ParseMode.Cross:
                    crossRunways.Add(raw.ToUpperInvariant());
                    break;
                case ParseMode.HoldShort:
                    if (!HoldShortTarget.TryParse(raw, out HoldShortTarget holdShortTarget, out string? holdShortError))
                    {
                        return PR.Fail(holdShortError!);
                    }

                    holdShorts.Add(holdShortTarget);
                    break;
                default:
                    return PR.Fail($"CROSS: unexpected argument '{raw}'");
            }
        }

        // A keyword with no targets following is an actionable error.
        if (mode == ParseMode.HoldShort && holdShorts.Count == 0)
        {
            return PR.Fail("CROSS HS requires at least one target");
        }

        return PR.Ok(new CrossRunwayCommand(crossRunways, holdShorts));
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

        if (arg.Trim().Length == 0)
        {
            return PR.Fail("HS requires a target");
        }

        if (!HoldShortTarget.TryParse(arg, out HoldShortTarget target, out string? error))
        {
            return PR.Fail(error!);
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

        string callsign = arg.Trim();
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
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return PR.Fail("GIVEWAY requires a callsign");
        }

        string callsign = parts[0];
        string? location = parts.Length == 2 ? parts[1].Trim().ToUpperInvariant() : null;
        return PR.Ok(new GiveWayCommand(callsign, location));
    }

    /// <summary>
    /// Parses the argument of an exit command (EL/ER/EXIT) into an optional
    /// taxiway plus the NODEL/EXP keyword modifiers, accepted in any order. The
    /// first token that is not a recognized modifier is taken as the taxiway
    /// name; any further non-modifier token is an error.
    /// </summary>
    private static (string? Taxiway, bool NoDelete, bool Expedite, string? Error) ParseExitModifiers(string? arg)
    {
        string? taxiway = null;
        bool noDelete = false;
        bool expedite = false;

        if (arg is null)
        {
            return (null, false, false, null);
        }

        foreach (string token in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("NODEL", StringComparison.OrdinalIgnoreCase))
            {
                noDelete = true;
            }
            else if (token.Equals("EXP", StringComparison.OrdinalIgnoreCase))
            {
                expedite = true;
            }
            else if (taxiway is null)
            {
                taxiway = token.ToUpperInvariant();
            }
            else
            {
                return (null, false, false, $"unexpected token '{token}'");
            }
        }

        return (taxiway, noDelete, expedite, null);
    }

    /// <summary>
    /// Parses EL [taxiway] [NODEL] [EXP] in any modifier order.
    /// </summary>
    internal static PR ParseExitLeft(string? arg)
    {
        (string? taxiway, bool noDelete, bool expedite, string? error) = ParseExitModifiers(arg);
        return error is not null ? PR.Fail($"EL: {error}") : PR.Ok(new ExitLeftCommand(noDelete, taxiway, expedite));
    }

    /// <summary>
    /// Parses ER [taxiway] [NODEL] [EXP] in any modifier order.
    /// </summary>
    internal static PR ParseExitRight(string? arg)
    {
        (string? taxiway, bool noDelete, bool expedite, string? error) = ParseExitModifiers(arg);
        return error is not null ? PR.Fail($"ER: {error}") : PR.Ok(new ExitRightCommand(noDelete, taxiway, expedite));
    }

    /// <summary>
    /// Parses EXIT taxiway [NODEL] [EXP]. The taxiway is required.
    /// </summary>
    internal static PR ParseExitTaxiway(string arg)
    {
        (string? taxiway, bool noDelete, bool expedite, string? error) = ParseExitModifiers(arg);
        if (error is not null)
        {
            return PR.Fail($"EXIT: {error}");
        }

        return taxiway is null ? PR.Fail("EXIT requires a taxiway") : PR.Ok(new ExitTaxiwayCommand(taxiway, noDelete, expedite));
    }
}
