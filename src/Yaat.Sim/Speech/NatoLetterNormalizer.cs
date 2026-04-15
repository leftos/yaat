using System.Text;

namespace Yaat.Sim.Speech;

/// <summary>
/// Collapses runs of NATO phonetic letter words ("tango uniform whiskey") into single
/// taxiway-name tokens ("T", "U", "W") so phraseology rules can capture them with plain
/// <c>{taxiway}</c> or <c>{path...}</c> groups. Used as a post-callsign-extraction step in the
/// <see cref="PhraseologyMapper"/> pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The collapse is topology-aware: multi-letter taxiway names ("TE", "W11" is deferred) are
/// recognized only when they appear in the airport's current taxiway set. Without that check,
/// a transcript like "taxi via tango echo" would ambiguously map to either <c>["T", "E"]</c>
/// (two single-letter taxiways) or <c>["TE"]</c> (one multi-letter taxiway). An airport will
/// not name a taxiway "TE" if T actually connects to E, exactly because of that ambiguity —
/// so consulting the set resolves the ambiguity correctly in every real airport.
/// </para>
/// <para>
/// Ordering: this step runs AFTER <see cref="CallsignParser"/> has removed the callsign, so
/// "November 346 Golf, taxi via tango" still parses to <c>N346G</c> + taxi to taxiway T —
/// the NATO normalizer never sees "november" in that transcript. If a NATO word survives
/// callsign extraction in isolation, it's treated as a taxiway letter; that's the right call
/// in the ground-taxi contexts this normalizer exists for.
/// </para>
/// <para>
/// Alphanumeric taxiway names like <c>B6</c> / <c>A13</c> are deferred to a later iteration:
/// the <c>"bravo six"</c> → <c>"B6"</c> split is entangled with digit normalization in
/// <see cref="AtcNumberParser.NormalizeDigits"/>, which runs before this normalizer and would
/// already have converted "six" to "6". Supporting those cleanly needs a pre-digit hook.
/// </para>
/// </remarks>
public static class NatoLetterNormalizer
{
    /// <summary>
    /// Maximum multi-letter taxiway name length considered during topology disambiguation.
    /// Real-world airports rarely exceed 2-3 letter alphabetic taxiway names; 4 gives headroom
    /// without paying unbounded combinatorial cost for degenerate cases.
    /// </summary>
    private const int MaxMultiLetterName = 4;

    // Reverse of the NATO phonetic alphabet: word -> single letter. Built case-insensitive so
    // the normalizer works against AtcNumberParser-normalized tokens (all lowercase) and any
    // other upstream normalization that might preserve case.
    private static readonly Dictionary<string, char> WordToLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = 'A',
        ["bravo"] = 'B',
        ["charlie"] = 'C',
        ["delta"] = 'D',
        ["echo"] = 'E',
        ["foxtrot"] = 'F',
        ["golf"] = 'G',
        ["hotel"] = 'H',
        ["india"] = 'I',
        ["juliet"] = 'J',
        ["kilo"] = 'K',
        ["lima"] = 'L',
        ["mike"] = 'M',
        ["november"] = 'N',
        ["oscar"] = 'O',
        ["papa"] = 'P',
        ["quebec"] = 'Q',
        ["romeo"] = 'R',
        ["sierra"] = 'S',
        ["tango"] = 'T',
        ["uniform"] = 'U',
        ["victor"] = 'V',
        ["whiskey"] = 'W',
        ["xray"] = 'X',
        ["yankee"] = 'Y',
        ["zulu"] = 'Z',
    };

    /// <summary>
    /// Walks <paramref name="tokens"/> left-to-right, replacing runs of NATO phonetic words
    /// with taxiway-name tokens. Non-NATO tokens pass through unchanged (case preserved).
    /// When a run of NATO words could form a multi-letter taxiway name that exists in
    /// <paramref name="taxiwayNames"/>, the longest such name wins; otherwise each NATO word
    /// collapses to its single letter.
    /// </summary>
    /// <param name="tokens">Input token list (already filler-stripped and number-normalized).</param>
    /// <param name="taxiwayNames">
    /// Uppercase taxiway-name set for the current airport. Pass an empty set when no scenario
    /// context is available — the normalizer falls back to single-letter splits, which is the
    /// correct behavior for the common "taxi via tango uniform whiskey" case.
    /// </param>
    /// <returns>A new list with NATO runs collapsed. Never mutates the input.</returns>
    public static List<string> Collapse(IReadOnlyList<string> tokens, IReadOnlySet<string> taxiwayNames)
    {
        var result = new List<string>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            if (!WordToLetter.ContainsKey(tokens[i]))
            {
                result.Add(tokens[i]);
                i++;
                continue;
            }

            // Found the start of a NATO run. Extend until the next non-NATO token (or end).
            var runStart = i;
            while (i < tokens.Count && WordToLetter.ContainsKey(tokens[i]))
            {
                i++;
            }
            var runEnd = i; // exclusive

            // Greedy longest-match split of the run against the airport's taxiway names.
            var j = runStart;
            while (j < runEnd)
            {
                var matchLen = FindLongestMatch(tokens, j, runEnd, taxiwayNames);
                if (matchLen > 1)
                {
                    var sb = new StringBuilder(matchLen);
                    for (var k = 0; k < matchLen; k++)
                    {
                        sb.Append(WordToLetter[tokens[j + k]]);
                    }
                    result.Add(sb.ToString());
                    j += matchLen;
                }
                else
                {
                    // Single NATO letter — always valid, even when the taxiway set is empty.
                    result.Add(WordToLetter[tokens[j]].ToString());
                    j++;
                }
            }
        }
        return result;
    }

    private static int FindLongestMatch(IReadOnlyList<string> tokens, int start, int runEnd, IReadOnlySet<string> taxiwayNames)
    {
        if (taxiwayNames.Count == 0)
        {
            return 1;
        }

        var maxLen = Math.Min(MaxMultiLetterName, runEnd - start);
        for (var len = maxLen; len >= 2; len--)
        {
            var sb = new StringBuilder(len);
            for (var k = 0; k < len; k++)
            {
                sb.Append(WordToLetter[tokens[start + k]]);
            }
            if (taxiwayNames.Contains(sb.ToString()))
            {
                return len;
            }
        }
        return 1;
    }
}
