namespace Yaat.SpeechSandbox;

/// <summary>How the clauses of one gold canonical fared against one hypothesis.</summary>
public sealed record ClauseScore(int Gold, int Recognised, int WrongArgs, int WrongVerb, int Inserted, int Rejected)
{
    public static readonly ClauseScore Zero = new(0, 0, 0, 0, 0, 0);

    /// <summary>Every executed command that was not the gold one: wrong arguments, wrong verb, or an inserted clause.</summary>
    public int Wrong => WrongArgs + WrongVerb + Inserted;

    public static ClauseScore operator +(ClauseScore a, ClauseScore b) =>
        new(
            a.Gold + b.Gold,
            a.Recognised + b.Recognised,
            a.WrongArgs + b.WrongArgs,
            a.WrongVerb + b.WrongVerb,
            a.Inserted + b.Inserted,
            a.Rejected + b.Rejected
        );
}

/// <summary>
/// Command-level scoring of one mapped canonical against its gold label. A canonical splits on
/// <c>,</c> into parallel sibling clauses, so matching is order-insensitive: identical clauses are
/// recognised, then leftover gold clauses pair with a leftover hypothesis clause of the same verb
/// (wrong arguments), then with any leftover hypothesis clause (wrong verb). Gold clauses left
/// over are rejected; hypothesis clauses left over are inserted.
/// </summary>
public static class CommandScoring
{
    /// <summary>Scores <paramref name="hypothesis"/> against <paramref name="gold"/>.</summary>
    /// <param name="gold">The labeled canonical.</param>
    /// <param name="hypothesis">The canonical the pipeline produced, or null when nothing mapped.</param>
    /// <param name="rawTextFallback">
    /// True when the hypothesis is the mapper's raw command text surfaced after both mappers failed:
    /// the user sees text, not an executed command, so every gold clause is rejected.
    /// </param>
    public static ClauseScore Score(string gold, string? hypothesis, bool rawTextFallback)
    {
        List<string> goldClauses = Clauses(gold);
        if (rawTextFallback || hypothesis is null)
        {
            return new ClauseScore(goldClauses.Count, 0, 0, 0, 0, goldClauses.Count);
        }

        List<string> leftover = Clauses(hypothesis);
        List<string> unrecognised = TakeMatches(goldClauses, leftover, (g, h) => g == h, out int recognised);
        List<string> unpaired = TakeMatches(unrecognised, leftover, (g, h) => Verb(g) == Verb(h), out int wrongArgs);
        int wrongVerb = Math.Min(unpaired.Count, leftover.Count);
        return new ClauseScore(goldClauses.Count, recognised, wrongArgs, wrongVerb, leftover.Count - wrongVerb, unpaired.Count - wrongVerb);
    }

    /// <summary>
    /// Pairs each gold clause, in order, with the first remaining hypothesis clause that
    /// <paramref name="pairs"/> accepts, removing it from <paramref name="hypothesis"/>. The caller's
    /// <paramref name="hypothesis"/> list shrinks: afterwards it holds only the clauses left unpaired.
    /// </summary>
    /// <returns>The gold clauses left unpaired, in order.</returns>
    private static List<string> TakeMatches(List<string> gold, List<string> hypothesis, Func<string, string, bool> pairs, out int paired)
    {
        var unpaired = new List<string>();
        paired = 0;
        foreach (string g in gold)
        {
            int index = hypothesis.FindIndex(h => pairs(g, h));
            if (index < 0)
            {
                unpaired.Add(g);
                continue;
            }
            hypothesis.RemoveAt(index);
            paired++;
        }
        return unpaired;
    }

    /// <summary>The <c>,</c>-separated clauses of a canonical, upper-cased with internal whitespace collapsed to one space.</summary>
    private static List<string> Clauses(string canonical) =>
        [
            .. canonical
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(c => string.Join(' ', c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant()),
        ];

    private static string Verb(string clause)
    {
        int space = clause.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? clause : clause[..space];
    }
}
