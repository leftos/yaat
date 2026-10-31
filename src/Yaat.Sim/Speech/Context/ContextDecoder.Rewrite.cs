namespace Yaat.Sim.Speech.Context;

public static partial class ContextDecoder
{
    private static string Rewrite(HeardText heard, CallsignHit? callsign, IReadOnlyList<AcceptedSpan> entities, DecodeCall call)
    {
        string[] words = [.. heard.Words];
        if (callsign?.Form is { } callsignForm)
        {
            RewriteMatch(heard, words, callsign.Core, call.ShapeOf(callsignForm).Tokens);
        }
        foreach (AcceptedSpan span in entities)
        {
            RewriteMatch(heard, words, span.Place.Core, call.ShapeOf(span.Form).Tokens);
        }
        return string.Join(' ', words.Where(word => word.Length > 0));
    }

    private static void RewriteMatch(HeardText heard, string[] words, TokenRange range, IReadOnlyList<SpokenToken> form)
    {
        IReadOnlyList<SpokenToken> spoken = heard.Slice(range);
        if (spoken.Select(token => token.Text).SequenceEqual(form.Select(token => token.Text)))
        {
            return;
        }

        (IReadOnlyList<SpokenToken> Name, IReadOnlyList<SpokenToken> Code) parts = SpokenFormScore.SplitHeard(spoken, form);
        RewriteName(heard, words, range, parts.Name, form);
        RewriteCode(heard, words, parts.Code, form);
    }

    private static void RewriteName(
        HeardText heard,
        string[] words,
        TokenRange range,
        IReadOnlyList<SpokenToken> heardName,
        IReadOnlyList<SpokenToken> form
    )
    {
        List<string> formName = [.. form.Where(token => token.Kind == TokenKind.Word).Select(token => token.Text)];
        if ((heardName.Count == 0) || (formName.Count == 0) || (string.Concat(heardName.Select(token => token.Text)) == string.Concat(formName)))
        {
            return;
        }

        int first = heardName[0].SourceWord;
        int end = heardName[^1].SourceWordEnd;
        if (CanReplaceRun(heard, range, heardName, [.. Enumerable.Range(first, end - first)]))
        {
            ReplaceWords(words, first, end, string.Join(' ', formName));
        }
    }

    /// <summary>Whether the name's tokens cover every word of the run, and each word is all letters, not confident and inside the range.</summary>
    private static bool CanReplaceRun(HeardText heard, TokenRange range, IReadOnlyList<SpokenToken> heardName, List<int> run)
    {
        bool contiguous = run.All(word => heardName.Any(token => (token.SourceWord <= word) && (word < token.SourceWordEnd)));
        return contiguous && run.All(word => heard.ProducedOnlyWords(word) && !heard.IsConfident(word) && heard.WordLiesInside(word, range));
    }

    private static void RewriteCode(HeardText heard, string[] words, IReadOnlyList<SpokenToken> heardCode, IReadOnlyList<SpokenToken> form)
    {
        List<SpokenToken> formCode = [.. form.Where(token => token.Kind != TokenKind.Word)];
        if (heardCode.Count != formCode.Count)
        {
            return;
        }

        for (int i = 0; i < heardCode.Count; i++)
        {
            SpokenToken said = heardCode[i];
            bool nearMiss = (said.Kind == TokenKind.Word) && (formCode[i].Kind == TokenKind.Nato) && (said.Text != formCode[i].Text);
            IEnumerable<int> saidWords = Enumerable.Range(said.SourceWord, said.SourceWordEnd - said.SourceWord);
            if (nearMiss && saidWords.All(word => heard.ProducedOneToken(word) && !heard.IsConfident(word)))
            {
                ReplaceWords(words, said.SourceWord, said.SourceWordEnd, formCode[i].Text);
            }
        }
    }

    private static void ReplaceWords(string[] words, int first, int end, string replacement)
    {
        Array.Fill(words, string.Empty, first, end - first);
        words[first] = replacement;
    }
}
