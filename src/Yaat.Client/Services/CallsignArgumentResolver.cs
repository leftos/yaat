using Yaat.Client.Models;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// Rewrites partial callsign arguments inside command input text before it is sent
/// to the server. Mirrors the first-word partial callsign matching that MainViewModel
/// already performs on the leading callsign prefix — but applies to callsign tokens
/// at argument positions inside FOLLOW, FOLLOWG, RTIS, RTISF, and the CVA
/// <c>FOLLOW &lt;cs&gt;</c> modifier.
///
/// Why this lives in the client: keeps the server strict (<c>SimulationEngine.FindAircraft</c>
/// stays exact-match-only) and reuses the same ambiguity error messages as
/// <see cref="CallsignMatcher"/>.
/// </summary>
internal static class CallsignArgumentResolver
{
    /// <summary>
    /// Explicit allowlist of commands where callsign arguments get partial-match resolution.
    /// Other commands whose parameters happen to have a "callsign" type hint (e.g., ACCEPT
    /// handoff) are intentionally excluded — adding them is a separate scope decision.
    /// CVA is handled as a special case because its FOLLOW modifier isn't declared via Overloads.
    /// </summary>
    private static readonly HashSet<CanonicalCommandType> GenericCallsignArgCommands =
    [
        CanonicalCommandType.Follow,
        CanonicalCommandType.FollowGround,
        CanonicalCommandType.ReportTrafficInSight,
        CanonicalCommandType.ReportTrafficInSightForced,
    ];

    /// <summary>
    /// Rewrite result. On success <see cref="Error"/> is null and <see cref="Text"/> is
    /// the possibly-rewritten input. On ambiguity <see cref="Text"/> is null and
    /// <see cref="Error"/> carries the user-facing message. Tokens that match zero
    /// aircraft are left untouched — the server will reject at execution time,
    /// matching current typo behavior and allowing intentional references to
    /// aircraft not yet on frequency.
    /// </summary>
    internal readonly record struct Result(string? Text, string? Error);

    internal static Result TryRewrite(string input, CommandScheme scheme, IReadOnlyCollection<AircraftModel> aircraft)
    {
        if (string.IsNullOrEmpty(input) || aircraft.Count == 0)
        {
            return new Result(input, null);
        }

        // Walk the input block by block, preserving separators (, and ;) verbatim so
        // the rewritten string has identical structure. We track the positions of
        // separators in the ORIGINAL string and rebuild with rewritten block content.
        var blocks = SplitBlocks(input);
        var rebuilt = new System.Text.StringBuilder(input.Length);

        foreach (var block in blocks)
        {
            if (block.IsSeparator)
            {
                rebuilt.Append(block.Text);
                continue;
            }

            var rewrite = TryRewriteBlock(block.Text, scheme, aircraft);
            if (rewrite.Error is not null)
            {
                return new Result(null, rewrite.Error);
            }

            rebuilt.Append(rewrite.Text ?? block.Text);
        }

        return new Result(rebuilt.ToString(), null);
    }

    private readonly record struct Block(string Text, bool IsSeparator);

    private static List<Block> SplitBlocks(string input)
    {
        var blocks = new List<Block>();
        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c is ',' or ';')
            {
                if (i > start)
                {
                    blocks.Add(new Block(input[start..i], false));
                }

                blocks.Add(new Block(input[i..(i + 1)], true));
                start = i + 1;
            }
        }

        if (start < input.Length)
        {
            blocks.Add(new Block(input[start..], false));
        }

        return blocks;
    }

    private static Result TryRewriteBlock(string block, CommandScheme scheme, IReadOnlyCollection<AircraftModel> aircraft)
    {
        // Preserve any leading whitespace so formatting round-trips.
        int leadingWs = 0;
        while (leadingWs < block.Length && char.IsWhiteSpace(block[leadingWs]))
        {
            leadingWs++;
        }

        var leading = block[..leadingWs];
        var content = block[leadingWs..];
        if (content.Length == 0)
        {
            return new Result(block, null);
        }

        // Strip any condition prefix (LV/AT/AS/GIVEWAY/BEHIND) and preserve it verbatim.
        // We do NOT rewrite the condition argument itself — GIVEWAY/BEHIND callsigns are
        // handled by the main command parser and LV/AT arguments aren't callsigns.
        var stripped = CommandInputController.StripConditionPrefix(content, out _);
        var prefixLength = content.Length - stripped.Length;
        var prefixText = content[..prefixLength];
        var body = content[prefixLength..];

        if (string.IsNullOrWhiteSpace(body))
        {
            return new Result(block, null);
        }

        var tokens = Tokenize(body, out var tokenSpans);
        if (tokens.Count == 0)
        {
            return new Result(block, null);
        }

        // The verb is the first token that matches a known alias.
        int verbIndex = -1;
        CanonicalCommandType? verbType = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var type = ResolveVerb(tokens[i], scheme);
            if (type is not null)
            {
                verbIndex = i;
                verbType = type;
                break;
            }
        }

        if (verbType is null)
        {
            return new Result(block, null);
        }

        var def = CommandRegistry.Get(verbType.Value);
        if (def is null)
        {
            return new Result(block, null);
        }

        // Collect the set of argument indices (within tokens, relative to the whole block body)
        // whose values should be resolved as callsigns.
        var callsignTokenIndices = new List<int>();

        if (verbType == CanonicalCommandType.ClearedVisualApproach)
        {
            // CVA 28R [LEFT|RIGHT] [FOLLOW <cs>] — custom parser. Scan for the FOLLOW keyword
            // after the verb and treat the next token as a callsign.
            for (int i = verbIndex + 1; i < tokens.Count - 1; i++)
            {
                if (string.Equals(tokens[i], "FOLLOW", StringComparison.OrdinalIgnoreCase))
                {
                    callsignTokenIndices.Add(i + 1);
                    break;
                }
            }
        }
        else if (!GenericCallsignArgCommands.Contains(verbType.Value))
        {
            return new Result(block, null);
        }
        else
        {
            // Generic path: inspect overload parameters. An argument position is a callsign
            // slot if any overload declares a non-literal parameter at that index whose TypeHint
            // contains "callsign".
            int argsAvailable = tokens.Count - verbIndex - 1;
            for (int paramIdx = 0; paramIdx < argsAvailable; paramIdx++)
            {
                bool isCallsign = false;
                foreach (var overload in def.Overloads)
                {
                    if (paramIdx >= overload.Parameters.Length)
                    {
                        continue;
                    }

                    var p = overload.Parameters[paramIdx];
                    if (!p.IsLiteral && p.TypeHint.Contains("callsign", StringComparison.OrdinalIgnoreCase))
                    {
                        isCallsign = true;
                        break;
                    }
                }

                if (isCallsign)
                {
                    callsignTokenIndices.Add(verbIndex + 1 + paramIdx);
                }
            }
        }

        if (callsignTokenIndices.Count == 0)
        {
            return new Result(block, null);
        }

        // Resolve and build the rewritten body in-place by replacing specific token spans.
        var rewrittenBody = new System.Text.StringBuilder(body.Length);
        int cursor = 0;
        foreach (var tokenIdx in callsignTokenIndices)
        {
            var (tokenStart, tokenEnd) = tokenSpans[tokenIdx];
            var tokenText = tokens[tokenIdx];
            var (match, outcome, candidates) = CallsignMatcher.Match(tokenText, aircraft);

            if (outcome == CallsignMatcher.Outcome.Ambiguous)
            {
                return new Result(null, CallsignMatcher.FormatAmbiguityMessage(tokenText, candidates));
            }

            if (outcome == CallsignMatcher.Outcome.None || match is null)
            {
                // Leave untouched — the server will reject at exec time.
                continue;
            }

            if (outcome == CallsignMatcher.Outcome.Exact)
            {
                // Already canonical — no rewrite needed.
                continue;
            }

            // Copy body up to the token, append resolved callsign, skip the original token.
            rewrittenBody.Append(body, cursor, tokenStart - cursor);
            rewrittenBody.Append(match.Callsign);
            cursor = tokenEnd;
        }

        if (cursor == 0)
        {
            // No substitutions occurred.
            return new Result(block, null);
        }

        rewrittenBody.Append(body, cursor, body.Length - cursor);
        return new Result(leading + prefixText + rewrittenBody, null);
    }

    private static CanonicalCommandType? ResolveVerb(string token, CommandScheme scheme)
    {
        foreach (var (type, pattern) in scheme.Patterns)
        {
            foreach (var alias in pattern.Aliases)
            {
                if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into whitespace-separated tokens while also
    /// returning the original (start, end) char ranges of each token so the resolver
    /// can patch the source string without re-tokenizing.
    /// </summary>
    private static List<string> Tokenize(string text, out List<(int Start, int End)> spans)
    {
        var tokens = new List<string>();
        spans = [];
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            tokens.Add(text[start..i]);
            spans.Add((start, i));
        }

        return tokens;
    }
}
