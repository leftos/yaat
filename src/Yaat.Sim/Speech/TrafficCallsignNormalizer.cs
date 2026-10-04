using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Speech;

/// <summary>
/// Collapses a spoken traffic callsign that follows a traffic cue ("follow", "behind", "give way to")
/// into a single ICAO token, so the single-token <c>{callsign}</c> slot of the matching
/// <see cref="PhraseologyRules"/> sees the whole telephony: "follow american 2231 on ground" becomes
/// "follow AAL2231 on ground". Runs in <see cref="PhraseologyMapper.MapWithTrace"/> after the addressed
/// aircraft's callsign is removed and before <see cref="NatoLetterNormalizer"/>, which would otherwise
/// collapse the "november" of an N-number into a letter.
/// <para>
/// The cue list is also what keeps <see cref="CallsignParser.TryParseTrailing"/> from taking the
/// traffic callsign at the end of "… give way to american 2231" as the addressed aircraft.
/// </para>
/// </summary>
public static class TrafficCallsignNormalizer
{
    private static readonly ILogger Log = SimLog.CreateLogger("TrafficCallsignNormalizer");

    /// <summary>
    /// The literal token sequences that directly precede a <c>{callsign}</c> slot in
    /// <see cref="PhraseologyRules"/>, longest first so "follow traffic" wins over "follow".
    /// </summary>
    private static readonly string[][] Cues =
    [
        ["give", "way", "to"],
        ["follow", "traffic"],
        ["follow", "the"],
        ["follow"],
        ["behind"],
    ];

    /// <summary>
    /// Replace each callsign spoken directly after a traffic cue with its ICAO form. A cue followed by
    /// anything <see cref="CallsignParser.TryParseLeading"/> cannot read, or by a callsign missing from a
    /// non-empty <paramref name="activeCallsigns"/>, leaves the tokens unchanged.
    /// </summary>
    /// <param name="tokens">Normalized transcript tokens with the addressed callsign already removed.</param>
    /// <param name="activeCallsigns">Callsigns on frequency, the parser's tiebreaker for shared telephonies.</param>
    /// <returns>A new token list; the input is not modified.</returns>
    public static List<string> Normalize(List<string> tokens, IReadOnlyCollection<string> activeCallsigns)
    {
        var result = new List<string>(tokens.Count);
        int i = 0;
        while (i < tokens.Count)
        {
            int cueLength = CueLengthAt(tokens, i);
            if (cueLength == 0)
            {
                result.Add(tokens[i]);
                i++;
                continue;
            }

            int callsignStart = i + cueLength;
            result.AddRange(tokens.GetRange(i, cueLength));
            i = callsignStart;
            if (callsignStart >= tokens.Count)
            {
                continue;
            }

            CallsignParser.ParsedCallsign? parsed = CallsignParser.TryParseLeading(string.Join(' ', tokens.Skip(callsignStart)), activeCallsigns);
            if (parsed is null)
            {
                continue;
            }

            // With traffic on frequency, a telephony-shaped phrase that names none of it ("follow the
            // boeing 737" → BOE737) is a description, not a callsign. With no list there is nothing to
            // check against, so the parse stands.
            if ((activeCallsigns.Count > 0) && !activeCallsigns.Contains(parsed.IcaoCallsign, StringComparer.OrdinalIgnoreCase))
            {
                Log.LogDebug("[Speech] TrafficCallsign: {Icao} is not on frequency, tokens left as spoken", parsed.IcaoCallsign);
                continue;
            }

            Log.LogDebug(
                "[Speech] TrafficCallsign: \"{Spoken}\" → {Icao}",
                string.Join(' ', tokens.GetRange(callsignStart, parsed.TokensConsumed)),
                parsed.IcaoCallsign
            );
            result.Add(parsed.IcaoCallsign);
            i = callsignStart + parsed.TokensConsumed;
        }
        return result;
    }

    /// <summary>
    /// True when the tokens directly before <paramref name="index"/> are a traffic cue, so a callsign
    /// starting at <paramref name="index"/> names the traffic rather than the addressed aircraft.
    /// </summary>
    public static bool IsPrecededByCue(IReadOnlyList<string> tokens, int index)
    {
        foreach (string[] cue in Cues)
        {
            int cueStart = index - cue.Length;
            if ((cueStart >= 0) && MatchesAt(tokens, cueStart, cue))
            {
                return true;
            }
        }
        return false;
    }

    private static int CueLengthAt(IReadOnlyList<string> tokens, int index)
    {
        foreach (string[] cue in Cues)
        {
            if ((index + cue.Length <= tokens.Count) && MatchesAt(tokens, index, cue))
            {
                return cue.Length;
            }
        }
        return 0;
    }

    private static bool MatchesAt(IReadOnlyList<string> tokens, int start, string[] cue)
    {
        for (int k = 0; k < cue.Length; k++)
        {
            if (!string.Equals(tokens[start + k], cue[k], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }
}
