namespace Yaat.Sim.Speech;

/// <summary>
/// Rewrites Whisper mishears of NATO phonetic words back to their canonical form so
/// downstream consumers (<see cref="CallsignParser"/>, <see cref="NatoLetterNormalizer"/>)
/// see clean input. Runs as an early pass in <see cref="PhraseologyMapper.Map"/>, AFTER
/// custom-fix collapse but BEFORE callsign extraction — the ordering matters:
/// <list type="bullet">
///   <item><description>After custom-fix collapse so programmed natural-language fix names
///     have already been canonicalized to aliases and can't be confused with NATO mishears.</description></item>
///   <item><description>Before callsign extraction so a mispronounced suffix letter
///     ("november 346 gulf") gets rewritten to "november 346 golf" in time for
///     <see cref="CallsignParser.TryParseLeading"/> to recover the N-number.</description></item>
///   <item><description>Before NATO normalization so the normalizer sees uniform NATO words
///     and collapses them correctly.</description></item>
/// </list>
///
/// <para>
/// <b>Algorithm.</b> For each input token, compute Levenshtein distance to every NATO word,
/// accept a rewrite only when:
/// <list type="number">
///   <item><description>Distance is exactly 1.</description></item>
///   <item><description>First character matches the NATO word (case-insensitive) — Whisper's
///     phonetic mishears almost always preserve the initial consonant; edits land on vowels
///     or trailing syllables.</description></item>
///   <item><description>Length differs by at most 1 (rules out multi-char insertions).</description></item>
///   <item><description>Token length is at least 4 — tiny words collide too easily with
///     unrelated English vocabulary.</description></item>
///   <item><description>The NATO match is unambiguous — if two different NATO words are
///     equidistant from the token, we bail out rather than guess.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Protection set.</b> Tokens that appear in the protection set pass through untouched
/// even when they would otherwise match. The protection set is the union of:
/// <list type="bullet">
///   <item><description><see cref="MapContext.ProgrammedFixes"/> — scenario-scoped fix names
///     the aircraft is programmed to use. A fix literally named "GULF" or "MAKE" must not
///     get clobbered by near-miss matching.</description></item>
///   <item><description>Every literal pattern token from <see cref="PhraseologyRules.All"/>
///     (see <see cref="RuleLiterals"/>). This protects common ATC command words from being
///     mistaken for NATO near-misses: "make" (in "make left traffic") does not become "mike",
///     "land" does not become "lima", etc.</description></item>
/// </list>
/// </para>
/// </summary>
public static class NatoNearMissResolver
{
    // The 26 canonical NATO words live in NatoPhoneticAlphabet (single source).
    private static IReadOnlyList<string> NatoWords => NatoPhoneticAlphabet.Words;

    private static IReadOnlySet<string> NatoExact => NatoPhoneticAlphabet.WordSet;

    /// <summary>
    /// Lazy-built set of every literal pattern token used by <see cref="PhraseologyRules.All"/>.
    /// Used as a protection set so command words like "make", "land", "line", "wait" don't get
    /// rewritten to NATO phonetic equivalents. Lazy because <see cref="PhraseologyRules"/>
    /// static initialization order is not guaranteed relative to this class.
    /// </summary>
    private static readonly Lazy<HashSet<string>> RuleLiterals = new(BuildRuleLiterals);

    /// <summary>Minimum token length eligible for near-miss rewriting. Below this, collision
    /// risk with unrelated English words is too high.</summary>
    private const int MinTokenLength = 4;

    /// <summary>
    /// Walks <paramref name="tokens"/> left-to-right and rewrites likely Whisper mishears of
    /// NATO phonetic words. Pass-through for tokens in <paramref name="protectedWords"/>
    /// (typically <see cref="MapContext.ProgrammedFixes"/>), rule-literal tokens, exact NATO
    /// words, tokens shorter than <see cref="MinTokenLength"/>, and tokens with no
    /// unambiguous distance-1 NATO match. Returns a new list; never mutates the input.
    /// </summary>
    public static List<string> Resolve(IReadOnlyList<string> tokens, IReadOnlySet<string> protectedWords)
    {
        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            var rewritten = TryRewrite(token, protectedWords);
            result.Add(rewritten ?? token);
        }
        return result;
    }

    private static readonly IReadOnlySet<string> EmptyProtectedSet = new HashSet<string>();

    /// <summary>
    /// Single-token variant for callers that don't have a token stream — e.g.
    /// <see cref="CallsignParser"/>'s GA tail-number path needs to rewrite a misheard suffix
    /// letter ("gulf" → "golf") inline while scanning. Returns the canonical NATO word when
    /// the token is an unambiguous distance-1 match, otherwise null.
    /// </summary>
    public static string? TryResolveSingle(string token) => TryRewrite(token, EmptyProtectedSet);

    private static string? TryRewrite(string token, IReadOnlySet<string> protectedWords)
    {
        if (token.Length < MinTokenLength)
        {
            return null;
        }
        if (NatoExact.Contains(token))
        {
            return null;
        }
        if (protectedWords.Contains(token))
        {
            return null;
        }
        if (RuleLiterals.Value.Contains(token))
        {
            return null;
        }

        var firstChar = char.ToLowerInvariant(token[0]);
        string? bestMatch = null;
        var bestDistance = int.MaxValue;
        var ambiguous = false;

        foreach (var nato in NatoWords)
        {
            if (nato[0] != firstChar)
            {
                continue;
            }
            if (Math.Abs(nato.Length - token.Length) > 1)
            {
                continue;
            }

            var dist = LevenshteinBounded(token, nato, maxDistance: 1);
            if (dist > 1)
            {
                continue;
            }

            if (dist < bestDistance)
            {
                bestDistance = dist;
                bestMatch = nato;
                ambiguous = false;
            }
            else if (dist == bestDistance)
            {
                ambiguous = true;
            }
        }

        if (bestMatch is null || ambiguous || bestDistance > 1)
        {
            return null;
        }
        return bestMatch;
    }

    /// <summary>
    /// Levenshtein distance with early-exit when the running minimum exceeds
    /// <paramref name="maxDistance"/>. Returns <paramref name="maxDistance"/> + 1 when the
    /// distance exceeds the bound — callers only need to distinguish "within bound" from
    /// "not". Keeps the algorithm simple and allocation-light for 4-8 char strings.
    /// </summary>
    private static int LevenshteinBounded(string a, string b, int maxDistance)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }
        if (b.Length == 0)
        {
            return a.Length;
        }

        // Single-row dynamic programming — O(min(|a|, |b|)) space.
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                var del = prev[j] + 1;
                var ins = curr[j - 1] + 1;
                var sub = prev[j - 1] + cost;
                curr[j] = Math.Min(Math.Min(del, ins), sub);
                if (curr[j] < rowMin)
                {
                    rowMin = curr[j];
                }
            }

            if (rowMin > maxDistance)
            {
                return maxDistance + 1;
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static HashSet<string> BuildRuleLiterals()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in PhraseologyRules.All)
        {
            foreach (var token in rule.Pattern)
            {
                // Skip capture groups — they're placeholders, not vocabulary words.
                if (token.StartsWith('{') && token.EndsWith('}'))
                {
                    continue;
                }
                // Strip the optional-marker suffix so "and?" protects "and".
                var literal = token.EndsWith('?') ? token[..^1] : token;
                if (literal.Length > 0)
                {
                    set.Add(literal);
                }
            }
        }
        return set;
    }
}
