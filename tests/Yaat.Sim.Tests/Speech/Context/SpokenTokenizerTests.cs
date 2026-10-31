using Xunit;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.Sim.Tests.Speech.Context;

public class SpokenTokenizerTests
{
    // --- Worked examples: token text, kind and source word ---

    [Fact]
    public void SouthWestTwentyFourNinetyFive() =>
        AssertTokens(
            "southwest twenty four ninety five",
            ("southwest", TokenKind.Word, 0),
            ("2", TokenKind.Digit, 1),
            ("4", TokenKind.Digit, 2),
            ("9", TokenKind.Digit, 3),
            ("5", TokenKind.Digit, 4)
        );

    [Fact]
    public void RunwayThreeZero() =>
        AssertTokens("runway three zero", ("runway", TokenKind.Word, 0), ("3", TokenKind.Digit, 1), ("0", TokenKind.Digit, 2));

    [Fact]
    public void WhiskeyFour() => AssertTokens("whiskey four", ("whiskey", TokenKind.Nato, 0), ("4", TokenKind.Digit, 1));

    [Fact]
    public void NovemberEightTwoNineEightLima() =>
        AssertTokens(
            "november eight two nine eight lima",
            ("november", TokenKind.Nato, 0),
            ("8", TokenKind.Digit, 1),
            ("2", TokenKind.Digit, 2),
            ("9", TokenKind.Digit, 3),
            ("8", TokenKind.Digit, 4),
            ("lima", TokenKind.Nato, 5)
        );

    [Fact]
    public void PacValleySevenSeventyTwo() =>
        AssertTokens(
            "pac valley seven seventy two",
            ("pac", TokenKind.Word, 0),
            ("valley", TokenKind.Word, 1),
            ("7", TokenKind.Digit, 2),
            ("7", TokenKind.Digit, 3),
            ("2", TokenKind.Digit, 4)
        );

    [Fact]
    public void HorizonTwentyOneOhOne() =>
        AssertTokens(
            "horizon twenty one oh one",
            ("horizon", TokenKind.Word, 0),
            ("2", TokenKind.Digit, 1),
            ("1", TokenKind.Digit, 2),
            ("0", TokenKind.Digit, 3),
            ("1", TokenKind.Digit, 4)
        );

    [Fact]
    public void FedexFiveSixtyEightHeavy() =>
        AssertTokens(
            "fedex five sixty eight heavy",
            ("fedex", TokenKind.Word, 0),
            ("5", TokenKind.Digit, 1),
            ("6", TokenKind.Digit, 2),
            ("8", TokenKind.Digit, 3),
            ("heavy", TokenKind.Word, 4)
        );

    [Fact]
    public void TaxiwayCharlieAbeamKaiser() =>
        AssertTokens(
            "taxiway charlie abeam kaiser",
            ("taxiway", TokenKind.Word, 0),
            ("charlie", TokenKind.Nato, 1),
            ("abeam", TokenKind.Word, 2),
            ("kaiser", TokenKind.Word, 3)
        );

    [Fact]
    public void DirectMitoe() => AssertTokens("direct mitoe", ("direct", TokenKind.Word, 0), ("mitoe", TokenKind.Word, 1));

    // --- Number expansion ---

    [Theory]
    [InlineData("twenty four ninety five")]
    [InlineData("seven seventy two")]
    [InlineData("one hundred")]
    [InlineData("three thousand")]
    [InlineData("four thousand five hundred")]
    [InlineData("five sixty eight")]
    public void DigitRunsMatchNormalizeDigits(string input)
    {
        string digits = string.Concat(SpokenTokenizer.Tokenize(input).Where(t => t.Kind == TokenKind.Digit).Select(t => t.Text));
        Assert.Equal(AtcNumberParser.NormalizeDigits(input).Replace(" ", ""), digits);
    }

    [Fact]
    public void ThirtyEqualsThreeZero() => Assert.Equal(Texts(SpokenTokenizer.Tokenize("thirty")), Texts(SpokenTokenizer.Tokenize("three zero")));

    [Fact]
    public void OneHundredIsOneZeroZero() => Assert.Equal(["1", "0", "0"], DigitTexts("one hundred"));

    [Fact]
    public void OhBetweenDigitsIsZero() => Assert.Equal(["2", "1", "0", "1"], DigitTexts("two one oh one"));

    [Fact]
    public void IcaoTokenSplits()
    {
        AssertTokens(
            "n4926f",
            ("november", TokenKind.Nato, 0),
            ("4", TokenKind.Digit, 0),
            ("9", TokenKind.Digit, 0),
            ("2", TokenKind.Digit, 0),
            ("6", TokenKind.Digit, 0),
            ("foxtrot", TokenKind.Nato, 0)
        );

        AssertTokens("28r", ("2", TokenKind.Digit, 0), ("8", TokenKind.Digit, 0), ("right", TokenKind.Word, 0));
    }

    [Fact]
    public void Fillers() => Assert.Equal(["southwest", "5"], SpokenTokenizer.Tokenize("uh southwest um five").Select(t => t.Text));

    private static void AssertTokens(string transcript, params (string Text, TokenKind Kind, int SourceWord)[] expected)
    {
        IReadOnlyList<SpokenToken> tokens = SpokenTokenizer.Tokenize(transcript);
        Assert.Equal(expected.Length, tokens.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Text, tokens[i].Text);
            Assert.Equal(expected[i].Kind, tokens[i].Kind);
            Assert.Equal(expected[i].SourceWord, tokens[i].SourceWord);
        }
    }

    private static (string Text, TokenKind Kind)[] Texts(IReadOnlyList<SpokenToken> tokens) => [.. tokens.Select(t => (t.Text, t.Kind))];

    private static string[] DigitTexts(string transcript) =>
        [.. SpokenTokenizer.Tokenize(transcript).Where(t => t.Kind == TokenKind.Digit).Select(t => t.Text)];
}
