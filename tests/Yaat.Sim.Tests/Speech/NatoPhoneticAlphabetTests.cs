using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class NatoPhoneticAlphabetTests
{
    [Fact]
    public void LetterToWord_Contains_All_26_Letters()
    {
        Assert.Equal(26, NatoPhoneticAlphabet.LetterToWord.Count);
        for (var c = 'A'; c <= 'Z'; c++)
        {
            Assert.True(NatoPhoneticAlphabet.LetterToWord.ContainsKey(c), $"missing letter {c}");
        }
    }

    [Fact]
    public void WordToLetter_Is_Inverse_Of_LetterToWord()
    {
        foreach (var (letter, word) in NatoPhoneticAlphabet.LetterToWord)
        {
            Assert.True(NatoPhoneticAlphabet.WordToLetter.TryGetValue(word, out var reversed), $"word {word} not in reverse map");
            Assert.Equal(letter, reversed);
        }
    }

    [Fact]
    public void WordToLetter_Is_Case_Insensitive()
    {
        Assert.True(NatoPhoneticAlphabet.WordToLetter.TryGetValue("TANGO", out var t));
        Assert.Equal('T', t);
        Assert.True(NatoPhoneticAlphabet.WordToLetter.TryGetValue("Tango", out var t2));
        Assert.Equal('T', t2);
    }

    [Fact]
    public void Words_List_Is_In_AZ_Order()
    {
        Assert.Equal(26, NatoPhoneticAlphabet.Words.Count);
        Assert.Equal("alpha", NatoPhoneticAlphabet.Words[0]);
        Assert.Equal("bravo", NatoPhoneticAlphabet.Words[1]);
        Assert.Equal("zulu", NatoPhoneticAlphabet.Words[25]);
    }

    [Fact]
    public void WordSet_Contains_All_Words()
    {
        Assert.Equal(26, NatoPhoneticAlphabet.WordSet.Count);
        foreach (var word in NatoPhoneticAlphabet.Words)
        {
            Assert.Contains(word, NatoPhoneticAlphabet.WordSet);
        }
    }

    [Fact]
    public void WordSet_Is_Case_Insensitive()
    {
        Assert.Contains("TANGO", NatoPhoneticAlphabet.WordSet);
        Assert.Contains("Tango", NatoPhoneticAlphabet.WordSet);
        Assert.Contains("tango", NatoPhoneticAlphabet.WordSet);
    }

    [Theory]
    [InlineData('A', "alpha")]
    [InlineData('N', "november")]
    [InlineData('Z', "zulu")]
    [InlineData('a', "alpha")] // lowercase accepted via TryGetWord
    public void TryGetWord_Returns_Canonical_Form(char letter, string expected)
    {
        Assert.True(NatoPhoneticAlphabet.TryGetWord(letter, out var word));
        Assert.Equal(expected, word);
    }

    [Theory]
    [InlineData("tango", 'T')]
    [InlineData("Tango", 'T')]
    [InlineData("ALPHA", 'A')]
    [InlineData("zulu", 'Z')]
    public void TryGetLetter_Returns_Canonical_Letter(string word, char expected)
    {
        Assert.True(NatoPhoneticAlphabet.TryGetLetter(word, out var letter));
        Assert.Equal(expected, letter);
    }

    [Fact]
    public void TryGetLetter_Returns_False_For_Non_Nato()
    {
        Assert.False(NatoPhoneticAlphabet.TryGetLetter("runway", out _));
        Assert.False(NatoPhoneticAlphabet.TryGetLetter("climb", out _));
    }

    [Theory]
    [InlineData('0', "zero")]
    [InlineData('5', "five")]
    [InlineData('9', "nine")]
    [InlineData('A', "alpha")]
    [InlineData('z', "zulu")]
    public void SpellChar_Digits_And_Letters(char input, string expected)
    {
        Assert.Equal(expected, NatoPhoneticAlphabet.SpellChar(input));
    }

    [Fact]
    public void SpellChar_Unknown_Returns_Uppercase()
    {
        // Anything non-ASCII / non-digit / non-letter falls through to its uppercase form.
        Assert.Equal("-", NatoPhoneticAlphabet.SpellChar('-'));
    }
}
