using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The content a shortened readback clause may never drop, add or reorder against its canonical clause: its numbers and
/// runway designators, the type keyword and altitude unit each number is spoken with, the turn and altitude directions,
/// and the hold-short and crossing instructions a controller must hear read back.
/// <para>
/// It does not enforce: altitude qualifiers ("at or above", "until established"; F3), non-numeric targets such as fix
/// names and taxiway hold-short targets, taxiway letters of a route (F11), safety-critical verbs (F12) other than hold
/// short and cross (cleared to land, cleared for takeoff, go around, line up and wait, follow, behind, the Class B
/// clearance), nor the readback-level rules (callsign F1, wake suffix F2, jargon F14), which sit outside one clause.
/// </para>
/// </summary>
public static class AviationFloor
{
    private static readonly HashSet<string> Keywords = ["heading", "degrees", "knots", "mach", "squawk", "runway"];

    private static readonly Dictionary<string, string> WordClasses = new()
    {
        ["left"] = "LEFT",
        ["right"] = "RIGHT",
        ["climb"] = "CLIMB",
        ["climbing"] = "CLIMB",
        ["up"] = "CLIMB",
        ["descend"] = "DESCEND",
        ["descending"] = "DESCEND",
        ["down"] = "DESCEND",
        ["cross"] = "CROSS",
    };

    private static readonly char[] Punctuation = [',', '.', ';', ':', '!', '?', '"', '\'', '(', ')'];

    /// <summary>
    /// Whether <paramref name="variant"/> keeps the floor of <paramref name="canonical"/>: with both texts' spoken numbers
    /// normalised to digits, the variant's anchors equal the canonical's, the same anchors in the same order, none dropped
    /// and none added; and the altitude units the two texts speak (the phrase "flight level", the words thousand and
    /// hundred), read before normalising, are the same units in the same order. The anchors are every token starting with
    /// a digit (a number, or a runway designator with its side), the keywords heading, degrees, knots, mach, squawk and
    /// runway, the direction classes left, right, climb (climb, climbing, up) and descend (descend, descending, down), the
    /// phrase "hold short" and the word "cross".
    /// </summary>
    /// <param name="canonical">The canonical clause, as the verbatim rule renders it.</param>
    /// <param name="variant">The shortened clause that would replace it.</param>
    /// <returns>True when the variant may replace the canonical clause.</returns>
    public static bool Holds(string canonical, string variant) =>
        Anchors(canonical).SequenceEqual(Anchors(variant), StringComparer.Ordinal)
        && UnitAnchors(canonical).SequenceEqual(UnitAnchors(variant), StringComparer.Ordinal);

    private static List<string> Anchors(string text)
    {
        string[] tokens = AtcNumberParser.NormalizeDigits(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var anchors = new List<string>();
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if ((token == "hold") && (i + 1 < tokens.Length) && (tokens[i + 1] == "short"))
            {
                anchors.Add("HOLDSHORT");
                i++;
            }
            else if (char.IsAsciiDigit(token[0]) || Keywords.Contains(token))
            {
                anchors.Add(token);
            }
            else if (WordClasses.TryGetValue(token, out string? wordClass))
            {
                anchors.Add(wordClass);
            }
        }
        return anchors;
    }

    /// <summary>
    /// The altitude units of <paramref name="text"/> in order, read from its raw words because normalising folds them into
    /// the number: "flight level" → FL, thousand → THOUSAND, hundred → HUNDRED.
    /// </summary>
    private static List<string> UnitAnchors(string text)
    {
        string[] words =
        [
            .. text.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.Trim(Punctuation))
                .Where(word => word.Length > 0),
        ];
        var units = new List<string>();
        for (int i = 0; i < words.Length; i++)
        {
            if ((words[i] == "flight") && (i + 1 < words.Length) && (words[i + 1] == "level"))
            {
                units.Add("FL");
                i++;
            }
            else if (words[i] == "thousand")
            {
                units.Add("THOUSAND");
            }
            else if (words[i] == "hundred")
            {
                units.Add("HUNDRED");
            }
        }
        return units;
    }
}
