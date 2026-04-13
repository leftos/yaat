using System.Text.RegularExpressions;

namespace Yaat.Sim.Speech;

/// <summary>
/// Extracts custom telephony designators embedded in scenario flight-plan remarks fields.
/// Some scenarios use ad-hoc callsigns (charter ops, cargo feeders, corporate shuttle) that
/// don't live in <see cref="AirlineTelephony"/>. The speech pipeline feeds these extracted
/// telephonies into Whisper's <c>initial_prompt</c> so the custom callsign is recognized
/// without polluting the global airline database.
/// </summary>
/// <remarks>
/// Observed patterns in ZOA scenario examples (Phase 1 supports the two reliable ones):
/// <list type="bullet">
///   <item><description><c>CALLSIGN "JETLINX" /V/</c> — labeled quoted (canonical)</description></item>
///   <item><description><c>CS "PACK COAST"</c> — short labeled quoted</description></item>
///   <item><description><c>"CIRCADIAN"</c>, <c>/V/ "FLEX MALTA"</c> — bare quoted</description></item>
///   <item><description><c>/V/ CALLSING AIRSHARE</c> — typo'd label (not supported)</description></item>
///   <item><description><c>/V/ GOLDEN GATE</c>, <c>/V/ MEDIVAC</c> — bare word(s), too ambiguous
///     (collides with <c>/V/ PARKING XXX, AUTO GENERATED</c>) — not supported</description></item>
/// </list>
/// </remarks>
public static class ScenarioCallsignExtractor
{
    // Labeled form: CALLSIGN "..." or CS "..." (case-insensitive, either straight or smart quotes).
    private static readonly Regex LabeledQuoted = new(@"\b(?:CALLSIGN|CS)\s*[""“]([^""”]+)[""”]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare quoted: any double-quoted substring. Caller must filter ambiguous results.
    private static readonly Regex BareQuoted = new(@"[""“]([^""”]+)[""”]", RegexOptions.Compiled);

    /// <summary>
    /// Extract custom telephony designators from a single remarks string.
    /// Returns an empty list if nothing was found.
    /// </summary>
    /// <param name="remarks">Raw remarks field from a flight plan (may be null or empty).</param>
    /// <returns>Unique uppercase telephonies, in discovery order.</returns>
    public static IReadOnlyList<string> Extract(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks))
        {
            return [];
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: labeled quoted (strongest signal).
        foreach (Match m in LabeledQuoted.Matches(remarks))
        {
            var cs = NormalizeCandidate(m.Groups[1].Value);
            if (cs.Length > 0 && seen.Add(cs))
            {
                results.Add(cs);
            }
        }

        // Pass 2: bare quoted (weaker but still reliable — any quoted token in remarks).
        foreach (Match m in BareQuoted.Matches(remarks))
        {
            var cs = NormalizeCandidate(m.Groups[1].Value);
            if (cs.Length > 0 && seen.Add(cs))
            {
                results.Add(cs);
            }
        }

        return results;
    }

    /// <summary>
    /// Normalize a candidate telephony: uppercase, trimmed, stripped of trailing digits so
    /// <c>"FLEX MALTA" 1385</c> → "FLEX MALTA". Returns empty string if the candidate doesn't
    /// look like a plausible telephony (too short, contains digits embedded mid-string, etc.).
    /// </summary>
    private static string NormalizeCandidate(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 30)
        {
            return "";
        }
        // Only A-Z, space, hyphen. Reject anything with digits or punctuation — those aren't
        // telephony phrases, they're flight plan remarks (routes, frequencies, etc.).
        foreach (var c in trimmed)
        {
            var isUpperLetter = c >= 'A' && c <= 'Z';
            var isLowerLetter = c >= 'a' && c <= 'z';
            var isSpaceOrHyphen = c == ' ' || c == '-';
            if (!(isUpperLetter || isLowerLetter || isSpaceOrHyphen))
            {
                return "";
            }
        }
        return trimmed.ToUpperInvariant();
    }
}
