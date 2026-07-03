namespace Yaat.Client.Find;

/// <summary>
/// Pure matching logic for the shared in-view Find. Kept separate from
/// <see cref="FindController"/> so it can be unit-tested without any UI or observable state.
/// </summary>
public static class FindMatcher
{
    private static readonly char[] TokenSeparators = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// Returns the items whose <see cref="IFindableItem.GetFindText"/> contains <b>every</b>
    /// whitespace-separated token of <paramref name="query"/> (case-insensitive), preserving the
    /// input order. A blank query matches nothing — Find highlights only once the controller has
    /// text to search for.
    /// </summary>
    public static List<IFindableItem> ComputeMatches(IReadOnlyList<IFindableItem> items, string query)
    {
        var tokens = (query ?? "").Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var matches = new List<IFindableItem>();
        foreach (var item in items)
        {
            var text = item.GetFindText();
            if (MatchesAllTokens(text, tokens))
            {
                matches.Add(item);
            }
        }
        return matches;
    }

    private static bool MatchesAllTokens(string text, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }
        return true;
    }
}
