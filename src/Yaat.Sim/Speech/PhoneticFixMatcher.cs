using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim.Speech;

/// <summary>
/// Fuzzy-match a transcribed token against known fix names. Whisper transcribes fix names
/// phonetically as English words (e.g. "CEPIN" → "sepin", "SUNOL" → "sue nol"), so the
/// rule-matching pipeline needs a post-pass that corrects captures to real fixes.
/// </summary>
/// <remarks>
/// Two-stage scope:
/// <list type="number">
///   <item>
///     <description>
///       <b>Programmed fixes</b> — the small set of fixes the relevant aircraft is actually
///       programmed to navigate (route, approach, STAR, departure). Highly likely to be the
///       correct target, so the threshold is generous.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Full nav database</b> (<see cref="NavigationDatabase.AllFixNames"/>) — fallback
///       when no programmed fix matches. Much larger search space, so the threshold is stricter.
///     </description>
///   </item>
/// </list>
/// Scoring combines two edit distances:
/// <list type="bullet">
///   <item><description>Raw Levenshtein on the lowercased strings.</description></item>
///   <item><description>Phonetic Levenshtein on a simplified phonetic encoding that folds together common confusable English phonemes (soft/hard C, PH/F, TH/T, etc.).</description></item>
/// </list>
/// </remarks>
public static class PhoneticFixMatcher
{
    private static readonly ILogger Log = SimLog.CreateLogger("PhoneticFixMatcher");

    /// <summary>
    /// Threshold for the programmed-fix scope. Both raw and phonetic Levenshtein must be
    /// ≤ this value for the match to count. 2 edits on a 5-character fix name is the
    /// sweet spot — accepts "sepin" and "seepin" for CEPIN, rejects "quantum" for ALTAM.
    /// </summary>
    private const int ProgrammedFixMaxDistance = 2;

    /// <summary>
    /// Strict threshold for the full-nav-database fallback. Much larger candidate set means
    /// we require near-exact match to avoid spurious hits.
    /// </summary>
    private const int FullDatabaseMaxDistance = 1;

    /// <summary>
    /// Try to match <paramref name="token"/> against a known fix. Returns the canonical fix
    /// name (uppercase) on success, or null if no candidate is close enough.
    /// </summary>
    /// <param name="token">The transcribed token, e.g. "sepin" or "sue nol".</param>
    /// <param name="programmedFixes">
    /// Per-aircraft programmed fix set from <c>AircraftState.GetProgrammedFixes()</c>. Matches
    /// against this set are preferred — they're the fixes the aircraft is actually routed over.
    /// </param>
    /// <param name="allowFullDatabaseFallback">
    /// When true and no programmed fix matches, search <see cref="NavigationDatabase.AllFixNames"/>
    /// as a secondary pass. Slower (thousands of candidates) and lower confidence but handles
    /// the case where the controller references a fix that isn't in the aircraft's route yet.
    /// </param>
    public static string? TryMatch(string token, IReadOnlyCollection<string> programmedFixes, bool allowFullDatabaseFallback = true)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var normalizedToken = token.Trim().ToUpperInvariant();
        var tokenPhonetic = Phonetize(normalizedToken);

        // Pass 1: programmed fixes (generous threshold).
        var best = FindBest(normalizedToken, tokenPhonetic, programmedFixes, ProgrammedFixMaxDistance);
        if (best is not null)
        {
            return best;
        }

        // Pass 2: full nav database (strict threshold).
        if (allowFullDatabaseFallback)
        {
            try
            {
                var allFixes = NavigationDatabase.Instance?.AllFixNames;
                if (allFixes is not null && allFixes.Length > 0)
                {
                    best = FindBest(normalizedToken, tokenPhonetic, allFixes, FullDatabaseMaxDistance);
                    if (best is not null)
                    {
                        return best;
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                // NavigationDatabase may not be initialized in some test contexts.
                Log.LogDebug(ex, "NavigationDatabase not initialized; skipping full-DB fallback");
            }
        }

        return null;
    }

    /// <summary>
    /// Find the candidate with the smallest combined edit distance to the token. The combined
    /// score is <c>max(rawDistance, phoneticDistance)</c> — a match must be close BOTH lexically
    /// AND phonetically, otherwise we risk false positives (short phonetic codes can collide
    /// easily). Returns null if the best candidate exceeds <paramref name="maxDistance"/>.
    /// </summary>
    private static string? FindBest(string token, string tokenPhonetic, IEnumerable<string> candidates, int maxDistance)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }
            var upperCandidate = candidate.ToUpperInvariant();

            // Fast path: exact match.
            if (upperCandidate == token)
            {
                return upperCandidate;
            }

            var rawDistance = Levenshtein(token, upperCandidate);
            var phoneticDistance = Levenshtein(tokenPhonetic, Phonetize(upperCandidate));
            // Require both to be close — prevents short phonetic codes from colliding.
            var combined = Math.Max(rawDistance, phoneticDistance);

            if (combined < bestDistance)
            {
                bestDistance = combined;
                best = upperCandidate;
            }
        }

        return bestDistance <= maxDistance ? best : null;
    }

    /// <summary>
    /// Compute Levenshtein edit distance between two strings. Simple DP implementation, O(m*n).
    /// </summary>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }
        if (b.Length == 0)
        {
            return a.Length;
        }

        // Two rows are enough for the recurrence; reuse to avoid allocating a full matrix.
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    /// <summary>
    /// Simplified phonetic encoding for English aviation fix names. Folds together common
    /// confusable phonemes so Whisper's English-biased transcription can be normalized to a
    /// form close to the canonical fix spelling.
    /// </summary>
    /// <remarks>
    /// Not a full Double Metaphone implementation — a targeted subset covering:
    /// soft/hard C, PH→F, GH silent, CK→K, QU→KW, TH→T, X→KS, silent leading K/P/W before N,
    /// vowel collapse (first vowel kept as placeholder, rest dropped), double-letter collapse.
    /// This handles the transcription errors we've seen in practice without the 300-line
    /// Lawrence Philips algorithm.
    /// </remarks>
    public static string Phonetize(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return "";
        }

        var upper = word.ToUpperInvariant();
        var sb = new System.Text.StringBuilder(upper.Length);

        for (var i = 0; i < upper.Length; i++)
        {
            var c = upper[i];
            var next = i + 1 < upper.Length ? upper[i + 1] : '\0';

            // Silent leading K, P, W, G before N: "KNOT" → "NOT", "PNEUMATIC" → "NEUMATIC"
            if (sb.Length == 0 && next == 'N' && (c == 'K' || c == 'P' || c == 'G' || c == 'W'))
            {
                continue;
            }

            // C: soft before E/I/Y, hard elsewhere
            if (c == 'C')
            {
                if (next == 'K')
                {
                    sb.Append('K');
                    i++;
                    continue;
                }
                if (next == 'H')
                {
                    sb.Append('K'); // approximation: CH → K (as in "chemical"). Imperfect for "chair" → SH.
                    i++;
                    continue;
                }
                sb.Append(next == 'E' || next == 'I' || next == 'Y' ? 'S' : 'K');
                continue;
            }

            // PH → F
            if (c == 'P' && next == 'H')
            {
                sb.Append('F');
                i++;
                continue;
            }

            // GH: silent in the middle ("night" → "nit"), drop.
            if (c == 'G' && next == 'H')
            {
                i++;
                continue;
            }

            // QU → KW
            if (c == 'Q')
            {
                sb.Append('K');
                if (next == 'U')
                {
                    sb.Append('W');
                    i++;
                }
                continue;
            }

            // TH → T (simplification — voiced/voiceless distinction dropped)
            if (c == 'T' && next == 'H')
            {
                sb.Append('T');
                i++;
                continue;
            }

            // X → KS
            if (c == 'X')
            {
                sb.Append('K');
                sb.Append('S');
                continue;
            }

            // Vowels: drop interior ones, keep a single leading 'A' placeholder so "EAT"
            // and "AT" hash differently from consonant-only skeletons.
            if (c is 'A' or 'E' or 'I' or 'O' or 'U')
            {
                if (sb.Length == 0)
                {
                    sb.Append('A');
                }
                continue;
            }

            // H: drop silent H except leading ("HOUSE" → "HOUS").
            if (c == 'H')
            {
                if (sb.Length == 0)
                {
                    sb.Append('H');
                }
                continue;
            }

            // Y: vowel unless leading.
            if (c == 'Y')
            {
                if (sb.Length == 0)
                {
                    sb.Append('Y');
                }
                continue;
            }

            sb.Append(c);
        }

        // Collapse adjacent duplicates: "BETTER" → "BTR" (already after vowel drop).
        var result = new System.Text.StringBuilder(sb.Length);
        var last = '\0';
        foreach (var c in sb.ToString())
        {
            if (c != last)
            {
                result.Append(c);
            }
            last = c;
        }
        return result.ToString();
    }
}
