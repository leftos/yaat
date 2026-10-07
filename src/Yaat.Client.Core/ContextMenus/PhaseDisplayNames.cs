using System.Text;
using System.Text.RegularExpressions;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The display name of a raw sim phase for menu text. A table maps the phases whose sim name does not read as English
/// (<c>FinalApproach</c> → <c>Final</c>, <c>InitialClimb</c> → <c>Departure</c>); everything else falls back to splitting
/// CamelCase into sentence case (<c>MidfieldCrossing</c> → <c>Midfield crossing</c>) while keeping an acronym and the
/// dynamic tail of a name (<c>Holding Short RWY 28R</c> → <c>Holding short RWY 28R</c>).
/// </summary>
internal static partial class PhaseDisplayNames
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["FinalApproach"] = "Final",
        ["ApproachNav"] = "Approach",
        ["InterceptCourse"] = "Approach",
        ["LinedUpAndWaiting"] = "Line up and wait",
        ["LiningUp"] = "Lining up",
        ["Holding After Pushback"] = "Holding after push",
        ["InitialClimb"] = "Departure",
        ["DepartureProcedure"] = "Departure",
        ["GoAround"] = "Go around",
        ["HoldingPattern"] = "Holding",
        ["TouchAndGo"] = "Touch and go",
        ["StopAndGo"] = "Stop and go",
        ["LowApproach"] = "Low approach",
        ["Approach-H"] = "Approach",
        ["Landing-H"] = "Landing",
        ["Takeoff-H"] = "Takeoff",
        ["AirTaxi"] = "Air taxi",
        ["At Parking"] = "At parking",
    };

    /// <summary>The display name of <paramref name="rawPhase"/>: the table first, else the sentence-cased fallback. Empty in, empty out.</summary>
    public static string For(string rawPhase)
    {
        if (string.IsNullOrEmpty(rawPhase))
        {
            return string.Empty;
        }

        return Table.TryGetValue(rawPhase, out string? display) ? display : SentenceCase(CamelBoundary().Replace(rawPhase, " "));
    }

    private static string SentenceCase(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(WordCase(words[i], isFirst: i == 0));
        }

        return builder.ToString();
    }

    /// <summary>
    /// One word of the fallback: a word with a lowercase letter is lower-cased (its first letter capitalised when it
    /// opens the name), while a word with none — an acronym or a callsign tail like <c>RWY</c>, <c>28R</c>, <c>N123</c> —
    /// is kept as it is.
    /// </summary>
    private static string WordCase(string word, bool isFirst)
    {
        if (!word.Any(char.IsLower))
        {
            return word;
        }

        string lowered = word.ToLowerInvariant();
        return isFirst ? char.ToUpperInvariant(lowered[0]) + lowered[1..] : lowered;
    }

    /// <summary>Inserts a boundary between a lowercase letter and a following uppercase one, so <c>MidfieldCrossing</c> splits.</summary>
    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBoundary();
}
