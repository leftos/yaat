using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Speech;

public enum ProcedureKind
{
    Sid,
    Star,
}

/// <summary>
/// A SID or STAR procedure registered for speech-recognition fuzzy matching. The
/// <paramref name="CanonicalName"/> is the published CIFP identifier (e.g. <c>EAGUL5</c>),
/// split into <paramref name="BaseName"/> (the fix-like leading portion, used as the input
/// to <see cref="PhoneticFixMatcher"/>) and <paramref name="DigitSuffix"/> (the trailing
/// revision digits, matched exactly against the transcript).
/// </summary>
/// <param name="CanonicalName">Published procedure identifier (e.g. "EAGUL5", "STRADO").</param>
/// <param name="Kind">Whether the procedure is a SID (departure) or STAR (arrival).</param>
/// <param name="BaseName">Leading portion stripped of trailing digits (e.g. "EAGUL" for "EAGUL5").</param>
/// <param name="DigitSuffix">Trailing digit run, empty if the canonical name has none (e.g. "5" for "EAGUL5", "" for "STRADO").</param>
public sealed record ProcedurePattern(string CanonicalName, ProcedureKind Kind, string BaseName, string DigitSuffix);

/// <summary>
/// Collapses multi-token spoken SID / STAR procedure names into the single canonical token
/// used by phraseology rules. Runs between <see cref="NatoLetterNormalizer"/> and rule
/// matching in <see cref="PhraseologyMapper"/> so rules like <c>"descend via the {star}
/// arrival"</c> can use a single-token <c>{star}</c> capture against a name that pilots /
/// controllers actually speak as "EAGUL Five Arrival".
/// </summary>
/// <remarks>
/// <para>
/// Detection strategy: at each token position scan ahead a short window for <c>arrival</c> /
/// <c>departure</c> / <c>transition</c>. When the keyword is found, the candidate slot is the
/// 1-2 tokens between the current position and the keyword: <c>[base]</c> or <c>[base, digit]</c>.
/// The base token is fuzzy-matched against known procedure base names via
/// <see cref="PhoneticFixMatcher.TryMatch"/> (which combines raw and phonetic Levenshtein, so
/// Whisper variants like "eagle" / "egal" still resolve to "EAGUL"). The digit token must match
/// the procedure's digit suffix exactly.
/// </para>
/// <para>
/// The trailing keyword also disambiguates kind: <c>arrival</c> restricts to STARs,
/// <c>departure</c> to SIDs. <c>transition</c> matches both (a transition phrasing can appear
/// after either kind).
/// </para>
/// </remarks>
public static class SidStarNameNormalizer
{
    private static readonly ILogger Log = SimLog.CreateLogger("SidStarNameNormalizer");

    private const int LookaheadWindow = 4;

    public static List<string> Collapse(List<string> tokens, IReadOnlyList<ProcedurePattern> procedures)
    {
        if (procedures.Count == 0 || tokens.Count == 0)
        {
            return tokens;
        }

        var output = new List<string>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            if (TryCollapseAt(tokens, i, procedures, out var canonical, out var consumed))
            {
                Log.LogDebug("[Speech] SidStarCollapse: \"{Spoken}\" → \"{Canonical}\"", string.Join(' ', tokens.GetRange(i, consumed)), canonical);
                output.Add(canonical);
                i += consumed;
                continue;
            }

            output.Add(tokens[i]);
            i++;
        }

        return output;
    }

    private static bool TryCollapseAt(
        List<string> tokens,
        int start,
        IReadOnlyList<ProcedurePattern> procedures,
        out string canonical,
        out int consumed
    )
    {
        canonical = "";
        consumed = 0;

        // Skip non-fix-like tokens at the start (e.g. "the", "via", trailing keywords themselves).
        // The base token must be alphabetic — digits and short articles can't be a procedure base.
        if (!LooksLikeBaseToken(tokens[start]))
        {
            return false;
        }

        // Find the trailing keyword within the lookahead window.
        var keywordIdx = -1;
        ProcedureKind? expectedKind = null;
        for (var k = start + 1; k <= Math.Min(tokens.Count, start + 1 + LookaheadWindow) - 1; k++)
        {
            if (k >= tokens.Count)
            {
                break;
            }
            if (string.Equals(tokens[k], "arrival", StringComparison.OrdinalIgnoreCase))
            {
                keywordIdx = k;
                expectedKind = ProcedureKind.Star;
                break;
            }
            if (string.Equals(tokens[k], "departure", StringComparison.OrdinalIgnoreCase))
            {
                keywordIdx = k;
                expectedKind = ProcedureKind.Sid;
                break;
            }
        }

        if (keywordIdx < 0)
        {
            return false;
        }

        var spanLen = keywordIdx - start;
        if (spanLen == 0 || spanLen > 2)
        {
            return false;
        }

        var baseToken = tokens[start];
        var digitToken = spanLen == 2 ? tokens[start + 1] : null;

        // Build the candidate base-name set restricted to procedures of the expected kind.
        var candidates = new List<string>(procedures.Count);
        foreach (var p in procedures)
        {
            if (p.Kind != expectedKind)
            {
                continue;
            }
            if (!candidates.Contains(p.BaseName, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(p.BaseName);
            }
        }
        if (candidates.Count == 0)
        {
            return false;
        }

        // Fuzzy-match the base token. Disable the full-database fallback because we already
        // narrowed candidates to scenario procedures — a stricter scope.
        var matchedBase = PhoneticFixMatcher.TryMatch(baseToken, candidates, allowFullDatabaseFallback: false);
        if (matchedBase is null)
        {
            return false;
        }

        // Find the procedure whose base matches AND whose digit suffix matches the transcript.
        // For a procedure with no digit suffix, the transcript must have no digit token either.
        foreach (var p in procedures)
        {
            if (p.Kind != expectedKind)
            {
                continue;
            }
            if (!p.BaseName.Equals(matchedBase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.IsNullOrEmpty(p.DigitSuffix))
            {
                if (digitToken is not null)
                {
                    continue;
                }
            }
            else
            {
                if (digitToken is null || !p.DigitSuffix.Equals(digitToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            canonical = p.CanonicalName;
            consumed = spanLen;
            return true;
        }

        return false;
    }

    private static bool LooksLikeBaseToken(string token)
    {
        if (token.Length < 3)
        {
            return false;
        }
        foreach (var c in token)
        {
            if (!char.IsLetter(c))
            {
                return false;
            }
        }
        return true;
    }
}
