using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class WhisperBiasingPromptTests
{
    [Fact]
    public void Default_ContainsNatoAlphabet()
    {
        // NATO words must be in the biasing prompt. Dropping them entirely regressed
        // single-word recognition: whisper-large-turbo3 heard "tango" as "tingo" without
        // the per-word bias. We keep them in but in scrambled order — see the
        // Default_NatoAlphabetIsScrambled_NoLetterAdjacentPairs test for the sequence-bias
        // break. The word list is sourced from NatoPhoneticAlphabet (single source).
        var prompt = WhisperBiasingPrompt.Default;
        foreach (var word in NatoPhoneticAlphabet.Words)
        {
            Assert.Contains(word, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Default_NatoAlphabetIsScrambled_NoLetterAdjacentPairs()
    {
        // NATO words in alphabetical order prime whisper-large-turbo3 to extrapolate the
        // alphabet: real regression had pilot say "taxi via tango uniform whiskey" (T U W),
        // Whisper emitted "tango uniform whiskey xray yankee", appending X and Y that
        // weren't in audio. The fix requires the NATO words in the prompt to be ordered
        // such that no two consecutive words' letters are adjacent in the alphabet.
        //
        // Split the prompt into tokens, find the NATO words in sequence, and verify the
        // invariant. The letter map comes from NatoPhoneticAlphabet (single source).
        var prompt = WhisperBiasingPrompt.Default;
        var tokens = prompt.Split(' ');

        var natoSequence = new List<(string Word, int Letter)>();
        foreach (var token in tokens)
        {
            if (NatoPhoneticAlphabet.TryGetLetter(token, out var letter))
            {
                natoSequence.Add((token, letter - 'A'));
            }
        }

        // Sanity: all 26 present, each exactly once.
        Assert.Equal(26, natoSequence.Count);
        Assert.Equal(26, natoSequence.Select(x => x.Letter).Distinct().Count());

        // Invariant: no two adjacent NATO entries in the prompt are alphabet-adjacent.
        // Alphabet-adjacent = |letter_a - letter_b| == 1 (e.g., T (19) and U (20)).
        for (var i = 0; i < natoSequence.Count - 1; i++)
        {
            var a = natoSequence[i];
            var b = natoSequence[i + 1];
            var gap = Math.Abs(a.Letter - b.Letter);
            Assert.True(gap > 1, $"NATO words '{a.Word}' and '{b.Word}' are alphabet-adjacent (gap={gap}) at position {i}");
        }
    }

    [Fact]
    public void Default_StillContainsAtcCommandVocabulary()
    {
        // Regression guard: make sure adding NATO back didn't wipe the command-verb literals
        // that the rule engine relies on for recognition biasing.
        var prompt = WhisperBiasingPrompt.Default;

        string[] mustContain = ["climb", "descend", "runway", "heading", "cleared", "takeoff", "approach", "maintain", "turn"];
        foreach (var word in mustContain)
        {
            Assert.Contains(word, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }
}
