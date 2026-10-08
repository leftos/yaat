using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The active-runways editor's text round trip, shared by the load prompt and any window that edits the room's list: the
/// tokens a row shows, the <c>ARWY</c> command a row's text sends (checked with the parser the server's <c>ARWY</c> uses),
/// and the notes on a guess.
/// </summary>
public static class ActiveRunwaysEditor
{
    /// <summary>A row's text for <paramref name="tokens"/>, spelled as the server sent them.</summary>
    public static string ToText(IEnumerable<string> tokens) => string.Join(' ', tokens);

    /// <summary>
    /// The notes under the rows: for each of <paramref name="airports"/>, whether <paramref name="prefill"/> implies no
    /// departure end there (only <c>A</c> ends, or none) and whether it implies no arrival end (only <c>D</c> ends, or none).
    /// A bare end counts as both.
    /// </summary>
    public static List<string> Notes(IEnumerable<string> airports, IReadOnlyDictionary<string, List<string>> prefill)
    {
        var notes = new List<string>();
        foreach (string airport in airports)
        {
            List<string> tokens = prefill.GetValueOrDefault(airport) ?? [];
            if (!tokens.Any(token => !HasUsePrefix(token, 'A')))
            {
                notes.Add($"No departure end implied at {airport}");
            }

            if (!tokens.Any(token => !HasUsePrefix(token, 'D')))
            {
                notes.Add($"No arrival end implied at {airport}");
            }
        }

        return notes;
    }

    /// <summary>
    /// Every end of every runway the navigation data knows at <paramref name="airport"/>, as bare tokens in its order: what
    /// an empty row sets. Empty without navigation data or runways there.
    /// </summary>
    public static List<string> EveryEnd(string airport, NavigationDatabase? navDb)
    {
        var ends = new List<string>();
        if (navDb is null)
        {
            return ends;
        }

        foreach (RunwayInfo runway in navDb.GetRunways(NavigationDatabase.NormalizeAirport(airport)))
        {
            foreach (string end in (string[])[runway.Id.End1, runway.Id.End2])
            {
                if ((!ends.Contains(end, StringComparer.OrdinalIgnoreCase)) && ActiveRunwayListParser.Parse(airport, end, navDb).IsSuccess)
                {
                    ends.Add(end);
                }
            }
        }

        return ends;
    }

    /// <summary>
    /// The <c>ARWY</c> command <paramref name="text"/> answers for <paramref name="airport"/>: an empty row sets every end
    /// there (nothing when the airport has no runway data), and anything else must read with
    /// <see cref="ActiveRunwayListParser.ParseWithNone"/>, the reader the server's <c>ARWY</c> uses (<c>NONE</c> alone
    /// clears the airport), whose message is the refusal. Without navigation data the text is sent unchecked and the
    /// server's <c>ARWY</c> checks it.
    /// </summary>
    public static ActiveRunwaysAnswer ToCommand(string airport, string text, NavigationDatabase? navDb)
    {
        string[] tokens = text.Split(ActiveRunwayListParser.Separators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            List<string> every = EveryEnd(airport, navDb);
            return new ActiveRunwaysAnswer(every.Count > 0 ? Arwy(airport, every) : null, null);
        }

        if (navDb is null)
        {
            return new ActiveRunwaysAnswer(Arwy(airport, tokens), null);
        }

        ActiveRunwayParseResult parsed = ActiveRunwayListParser.ParseWithNone(airport, text, navDb);
        if (!parsed.IsSuccess)
        {
            return new ActiveRunwaysAnswer(null, parsed.Error);
        }

        return parsed.Runways.Count == 0
            ? new ActiveRunwaysAnswer(Arwy(airport, ["NONE"]), null)
            : new ActiveRunwaysAnswer(Arwy(airport, parsed.Runways.Select(runway => runway.ToToken())), null);
    }

    /// <summary>
    /// Reads every row with <see cref="ToCommand"/> and records its refusal on the row: each row's answer, or <c>null</c>
    /// when a row does not read and nothing is to be sent.
    /// </summary>
    public static List<(ActiveRunwaysRow Row, string? Command)>? ReadRows(IReadOnlyList<ActiveRunwaysRow> rows, NavigationDatabase? navDb)
    {
        var answers = new List<(ActiveRunwaysRow Row, string? Command)>(rows.Count);
        foreach (ActiveRunwaysRow row in rows)
        {
            ActiveRunwaysAnswer answer = ToCommand(row.Airport, row.Text, navDb);
            row.Error = answer.Error;
            answers.Add((row, answer.Command));
        }

        return answers.Any(answer => answer.Row.HasError) ? null : answers;
    }

    /// <summary>OK is refused only when every row is empty and an airport has no runway data to fill its row with.</summary>
    public static bool CanConfirm(IReadOnlyCollection<ActiveRunwaysRow> rows, NavigationDatabase? navDb) =>
        (rows.Count > 0) && (rows.Any(row => !string.IsNullOrWhiteSpace(row.Text)) || rows.All(row => EveryEnd(row.Airport, navDb).Count > 0));

    private static bool HasUsePrefix(string token, char use) => (token.Length > 0) && (char.ToUpperInvariant(token[0]) == use);

    private static string Arwy(string airport, IEnumerable<string> tokens) => $"ARWY {airport} {string.Join(' ', tokens)}";
}
