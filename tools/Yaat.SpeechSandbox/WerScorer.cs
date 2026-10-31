namespace Yaat.SpeechSandbox;

/// <summary>
/// Word-level edits one hypothesis needs against one reference: how many substitutions, deletions
/// and insertions the alignment costs, and how many words the reference holds.
/// </summary>
public readonly record struct WerCounts(int Substitutions, int Deletions, int Insertions, int ReferenceWords)
{
    /// <summary>Total edits, which always equals the word-level Levenshtein distance between the two texts.</summary>
    public int Errors => Substitutions + Deletions + Insertions;

    /// <summary>
    /// Errors per reference word. An empty reference scores 0 when the hypothesis is empty too and 1
    /// otherwise, so a hypothesis made only of insertions can never look perfect.
    /// </summary>
    public double Rate => (ReferenceWords == 0) ? ((Errors == 0) ? 0 : 1) : (double)Errors / ReferenceWords;

    /// <summary>Field-wise sum of two count sets, for pooling many utterances into one corpus total.</summary>
    public static WerCounts operator +(WerCounts a, WerCounts b) =>
        new(a.Substitutions + b.Substitutions, a.Deletions + b.Deletions, a.Insertions + b.Insertions, a.ReferenceWords + b.ReferenceWords);
}

/// <summary>
/// Word error rate over whitespace-separated words: a full Levenshtein alignment with unit costs for
/// substitution, deletion and insertion, counted back out as the S/D/I breakdown.
/// </summary>
public static class WerScorer
{
    /// <summary>
    /// Aligns <paramref name="hypothesis"/> against <paramref name="reference"/> and counts the edits.
    /// A null hypothesis counts as every reference word deleted.
    /// </summary>
    public static WerCounts Score(string reference, string? hypothesis)
    {
        string[] refWords = reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string[] hypWords = (hypothesis is null) ? [] : hypothesis.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        int[,] cost = new int[refWords.Length + 1, hypWords.Length + 1];
        for (int i = 0; i <= refWords.Length; i++)
        {
            cost[i, 0] = i;
        }
        for (int j = 0; j <= hypWords.Length; j++)
        {
            cost[0, j] = j;
        }
        for (int i = 1; i <= refWords.Length; i++)
        {
            for (int j = 1; j <= hypWords.Length; j++)
            {
                int substitution = cost[i - 1, j - 1] + (WordsEqual(refWords[i - 1], hypWords[j - 1]) ? 0 : 1);
                cost[i, j] = Math.Min(Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1), substitution);
            }
        }

        return CountEdits(refWords, hypWords, cost);
    }

    /// <summary>
    /// Walks the finished cost matrix from the bottom-right cell back to the origin. At each cell the
    /// diagonal wins when it explains the cell's cost, then the vertical step (a reference word
    /// missing from the hypothesis), then the horizontal one (a word the hypothesis added).
    /// </summary>
    private static WerCounts CountEdits(string[] refWords, string[] hypWords, int[,] cost)
    {
        int substitutions = 0;
        int deletions = 0;
        int insertions = 0;
        int i = refWords.Length;
        int j = hypWords.Length;
        while ((i > 0) || (j > 0))
        {
            if ((i > 0) && (j > 0))
            {
                int substitution = WordsEqual(refWords[i - 1], hypWords[j - 1]) ? 0 : 1;
                if (cost[i, j] == cost[i - 1, j - 1] + substitution)
                {
                    substitutions += substitution;
                    i--;
                    j--;
                    continue;
                }
            }
            if ((i > 0) && (cost[i, j] == cost[i - 1, j] + 1))
            {
                deletions++;
                i--;
                continue;
            }
            insertions++;
            j--;
        }
        return new WerCounts(substitutions, deletions, insertions, refWords.Length);
    }

    private static bool WordsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Accumulates per-utterance counts into a corpus total. The rate is pooled — total errors over total
/// reference words — not the mean of the per-utterance rates, so a long utterance weighs in
/// proportion to its length.
/// </summary>
public sealed class WerTally
{
    /// <summary>How many scored utterances have been added.</summary>
    public int Segments { get; private set; }

    /// <summary>Field-wise sum of everything added so far.</summary>
    public WerCounts Total { get; private set; }

    /// <summary>Pooled word error rate over everything added so far.</summary>
    public double Rate => Total.Rate;

    /// <summary>Adds one scored utterance to the tally.</summary>
    public void Add(WerCounts counts)
    {
        Segments++;
        Total += counts;
    }
}
