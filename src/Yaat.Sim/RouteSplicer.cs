namespace Yaat.Sim;

/// <summary>
/// Applies the ERAM <c>AM RTE</c> route-splice grammar (docs/crc/eram.md Table 8) to a filed route. The
/// route is the ordered token list <c>[Departure] + enroute + [Destination]</c>. A splice is a
/// <c>.</c>-separated list of tokens whose first and last elements are <b>anchors</b> matched against the
/// existing route (the join and resume points); the whole list replaces the span between them (7110.65
/// §4-2-5 route amendment). A trailing up-arrow (<c>↑</c> / <c>[</c>) swaps the departure airport; a
/// trailing down-arrow (<c>↓</c> / <c>]</c>) swaps the destination.
///
/// This reproduces the splice <i>semantics</i>, not CRC's proprietary SID/STAR route normalization, so a
/// result may differ from CRC's expanded output by the fixes a procedure would inject.
///
/// The route is treated positionally (first token = departure, last = destination). It assumes a plan with
/// both a departure and a destination — the AM RTE use case — so a degenerate destination-only plan would
/// mislabel its first fix as the departure.
/// </summary>
public static class RouteSplicer
{
    /// <summary>
    /// Splices <paramref name="spliceArg"/> into the route described by
    /// <paramref name="departure"/>/<paramref name="enroute"/>/<paramref name="destination"/> and returns
    /// the amended departure/enroute/destination. Returns null when the splice is malformed (empty, a
    /// multi-token departure swap, or a reversed splice whose resume anchor lies before its join anchor).
    /// </summary>
    public static (string Departure, string Enroute, string Destination)? Splice(
        string departure,
        string enroute,
        string destination,
        string spliceArg
    )
    {
        spliceArg = spliceArg.Trim();
        if (spliceArg.Length == 0)
        {
            return null;
        }

        // The [ / ] keys (rendered as up/down arrows) swap the departure / destination airport.
        bool depSwap = spliceArg[^1] is '↑' or '[';
        bool destSwap = spliceArg[^1] is '↓' or ']';
        if (depSwap || destSwap)
        {
            spliceArg = spliceArg[..^1].Trim();
        }

        var parts = spliceArg.Split(['.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        bool hasDep = !string.IsNullOrWhiteSpace(departure);
        bool hasDest = !string.IsNullOrWhiteSpace(destination);
        var tokens = new List<string>();
        if (hasDep)
        {
            tokens.Add(departure.Trim());
        }
        tokens.AddRange(enroute.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (hasDest)
        {
            tokens.Add(destination.Trim());
        }

        List<string> rebuilt;

        if (depSwap)
        {
            // "The departure airport must be the only element entered."
            if (parts.Length != 1)
            {
                return null;
            }
            rebuilt = [parts[0]];
            rebuilt.AddRange(tokens.Skip(hasDep ? 1 : 0));
            return ReSplit(rebuilt);
        }

        int joinIdx = IndexOf(tokens, parts[0], 0);
        if (joinIdx < 0)
        {
            // A new leading anchor (e.g. a SID) → keep the departure and splice right after it.
            joinIdx = hasDep ? 1 : 0;
        }

        if (destSwap)
        {
            // Replace from the join anchor through the old destination; the last part becomes the new dest.
            rebuilt = [.. tokens.Take(joinIdx), .. parts];
            return ReSplit(rebuilt);
        }

        int resumeIdx = IndexOf(tokens, parts[^1], joinIdx);
        if (resumeIdx < 0 && IndexOf(tokens, parts[^1], 0) is var earlier && earlier >= 0 && earlier < joinIdx)
        {
            // The resume anchor exists only before the join anchor — a reversed splice. Reject rather than
            // emit a route that doubles back.
            return null;
        }

        rebuilt = [.. tokens.Take(joinIdx), .. parts];
        if (resumeIdx >= 0)
        {
            rebuilt.AddRange(tokens.Skip(resumeIdx + 1));
        }
        else if (hasDest)
        {
            // The last part is a genuinely new end fix → replace the end but keep the filed destination.
            rebuilt.Add(destination.Trim());
        }

        return ReSplit(rebuilt);
    }

    private static (string Departure, string Enroute, string Destination) ReSplit(List<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return ("", "", "");
        }
        if (tokens.Count == 1)
        {
            return (tokens[0], "", "");
        }
        var enroute = string.Join(' ', tokens.Skip(1).Take(tokens.Count - 2));
        return (tokens[0], enroute, tokens[^1]);
    }

    private static int IndexOf(List<string> tokens, string target, int startFrom)
    {
        for (int i = Math.Max(0, startFrom); i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], target, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}
