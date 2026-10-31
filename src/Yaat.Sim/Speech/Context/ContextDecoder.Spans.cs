namespace Yaat.Sim.Speech.Context;

public static partial class ContextDecoder
{
    private readonly record struct TokenRange(int Start, int End)
    {
        public int Length => End - Start;
    }

    private readonly record struct FormShape(IReadOnlyList<SpokenToken> Tokens, int NameCount, int CodeCount)
    {
        /// <summary>Whether the form holds a word, or starts with "november" or a NATO word then a digit: such a form can stand anywhere.</summary>
        public bool IsPrefixed => SpokenFormScore.IsPrefixedForm(Tokens);
    }

    private readonly record struct SpanScore(double Raw, double Weighted);

    /// <summary>Where a match sits: <paramref name="Core"/> is what was scored, <paramref name="Full"/> adds an absorbed "heavy"/"super".</summary>
    private readonly record struct SpanPlace(TokenRange Core, TokenRange Full);

    private sealed record HypothesisView(HeardText Heard, string Text, int Index, bool Typed);

    /// <summary>What one <see cref="Decode"/> call shares across its hypotheses: its inputs and the tokens of every form it has met.</summary>
    private sealed class DecodeCall(TransmissionContext context, DecoderSettings settings)
    {
        private readonly Dictionary<SpokenForm, FormShape> _shapes = new(ReferenceEqualityComparer.Instance);

        public TransmissionContext Context { get; } = context;

        public DecoderSettings Settings { get; } = settings;

        public FormShape ShapeOf(SpokenForm form)
        {
            if (!_shapes.TryGetValue(form, out FormShape shape))
            {
                IReadOnlyList<SpokenToken> tokens = SpokenTokenizer.Tokenize(form.Text);
                int nameCount = tokens.Count(token => token.Kind == TokenKind.Word);
                shape = new FormShape(tokens, nameCount, tokens.Count - nameCount);
                _shapes[form] = shape;
            }
            return shape;
        }
    }

    /// <summary>One hypothesis's words and tokens, with what the span rules ask of them.</summary>
    private sealed class HeardText(string text, MatchMode mode, double[]? confidence, bool typed, DecoderSettings settings)
    {
        private readonly double[]? _confidence = confidence;
        private readonly MatchMode _mode = mode;
        private readonly DecoderSettings _settings = settings;
        private readonly SpokenToken[] _tokens = [.. SpokenTokenizer.Tokenize(text)];

        public string[] Words { get; } = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        public bool Typed { get; } = typed;

        public IReadOnlyList<SpokenToken> Tokens => _tokens;

        public TokenRange Whole => new(0, _tokens.Length);

        public bool ConfidenceGated => _confidence is not null;

        public IReadOnlyList<SpokenToken> Slice(TokenRange range) => new ArraySegment<SpokenToken>(_tokens, range.Start, range.Length);

        public bool IsConfident(int sourceWord) => (_confidence is not null) && (_confidence[sourceWord] >= _settings.ConfidentWordProb);

        /// <summary>Whether the range starts inside a digit run; the edge of <paramref name="run"/> is a run boundary.</summary>
        public bool StartsInsideDigits(TokenRange range, TokenRange run) =>
            (range.Start > run.Start) && IsDigit(range.Start - 1) && IsDigit(range.Start);

        /// <summary>Whether the range ends inside a digit run; the edge of <paramref name="run"/> is a run boundary.</summary>
        public bool EndsInsideDigits(TokenRange range, TokenRange run) => (range.End < run.End) && IsDigit(range.End - 1) && IsDigit(range.End);

        /// <summary>Whether the token at <paramref name="index"/> opens a new source word, so a span ending there splits no word.</summary>
        public bool OpensSourceWord(int index) => _tokens[index - 1].SourceWordEnd <= _tokens[index].SourceWord;

        /// <summary>Whether a span ending at <paramref name="index"/> would part a spoken pair such as "ninety | five".</summary>
        public bool SplitsSpokenPair(int index) =>
            CameFromNumberWord(index - 1, NumberWordKind.Tens) && CameFromNumberWord(index, NumberWordKind.Digit);

        /// <summary>Whether the token came from a digit word of its own, not the units of a spoken pair such as "sixty eight".</summary>
        public bool IsLoneDigitWord(int index) => CameFromNumberWord(index, NumberWordKind.Digit) && !SplitsSpokenPair(index);

        /// <summary>Whether the token is an article; "a" tokenizes as a NATO letter, so this reads its source word.</summary>
        public bool IsArticle(int index)
        {
            SpokenToken token = _tokens[index];
            bool oneWord = token.SourceWordEnd == token.SourceWord + 1;
            return oneWord && ContextPhraseWords.Articles.Contains(Words[token.SourceWord].ToLowerInvariant());
        }

        /// <summary>The end of the digit run that starts at <paramref name="start"/>.</summary>
        public int DigitRunEnd(int start)
        {
            int end = start;
            while ((end < _tokens.Length) && IsDigit(end))
            {
                end++;
            }
            return end;
        }

        /// <summary>
        /// Whether the range is shaped like the form: each of the name and the code part within one token of the form's, and
        /// present exactly when the form has it. A missing part scores 1, so a part only one side has would match on nothing.
        /// A form that starts with a word needs a range that does: a code token before it belongs to whatever precedes the form.
        /// </summary>
        public bool Fits(TokenRange range, FormShape shape)
        {
            (IReadOnlyList<SpokenToken> Name, IReadOnlyList<SpokenToken> Code) parts = SpokenFormScore.SplitHeard(Slice(range), shape.Tokens);
            bool strayCode = (shape.Tokens.FirstOrDefault()?.Kind == TokenKind.Word) && (_tokens[range.Start].Kind != TokenKind.Word);
            return !strayCode && PartFits(parts.Name.Count, shape.NameCount) && PartFits(parts.Code.Count, shape.CodeCount);
        }

        public SpanScore Score(TokenRange range, SpokenForm form, FormShape shape)
        {
            double raw = SpokenFormScore.ScoreSpan(Slice(range), shape.Tokens, 1.0, ModeFor(range), _settings);
            return new SpanScore(raw, raw * form.Prior);
        }

        public EntityMatch Match(ContextEntity entity, TokenRange range, double score, bool own)
        {
            int firstWord = _tokens[range.Start].SourceWord;
            int endWord = _tokens[range.End - 1].SourceWordEnd;
            return new EntityMatch(entity, new WordSpan(firstWord, endWord), score, own, string.Join(' ', Words[firstWord..endWord]));
        }

        /// <summary>The token range a whitespace word produced, or null for a word that produced none.</summary>
        public TokenRange? RangeOfWord(int word)
        {
            int start = Array.FindIndex(_tokens, token => CameFrom(token, word));
            if (start < 0)
            {
                return null;
            }

            int end = start;
            while ((end < _tokens.Length) && CameFrom(_tokens[end], word))
            {
                end++;
            }
            return new TokenRange(start, end);
        }

        /// <summary>Whether every token of the source word lies inside the range.</summary>
        public bool WordLiesInside(int word, TokenRange range) =>
            (RangeOfWord(word) is { } own) && (own.Start >= range.Start) && (own.End <= range.End);

        public bool ProducedOnlyWords(int word) => _tokens.All(token => !CameFrom(token, word) || (token.Kind == TokenKind.Word));

        public bool ProducedOneToken(int word) => _tokens.Count(token => CameFrom(token, word)) == 1;

        private static bool CameFrom(SpokenToken token, int word) => (token.SourceWord <= word) && (word < token.SourceWordEnd);

        private static bool PartFits(int heard, int form) => ((heard == 0) == (form == 0)) && (Math.Abs(heard - form) <= 1);

        private bool CameFromNumberWord(int index, NumberWordKind kind) =>
            AtcNumberParser.TryGetNumberWord(Words[_tokens[index].SourceWord].ToLowerInvariant(), out NumberWordKind heard, out _) && (heard == kind);

        private bool IsDigit(int index) => _tokens[index].Kind == TokenKind.Digit;

        private MatchMode ModeFor(TokenRange range)
        {
            for (int i = range.Start; i < range.End; i++)
            {
                SpokenToken token = _tokens[i];
                bool confident = Enumerable.Range(token.SourceWord, token.SourceWordEnd - token.SourceWord).Any(IsConfident);
                if ((token.Kind == TokenKind.Word) && confident)
                {
                    return MatchMode.ExactOnly;
                }
            }
            return _mode;
        }
    }
}
