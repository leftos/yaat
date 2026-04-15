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
        // ScrambledNato_NoLetterAdjacentPairs test for the sequence-bias break.
        var prompt = WhisperBiasingPrompt.Default;

        string[] allNato =
        [
            "alpha",
            "bravo",
            "charlie",
            "delta",
            "echo",
            "foxtrot",
            "golf",
            "hotel",
            "india",
            "juliet",
            "kilo",
            "lima",
            "mike",
            "november",
            "oscar",
            "papa",
            "quebec",
            "romeo",
            "sierra",
            "tango",
            "uniform",
            "victor",
            "whiskey",
            "xray",
            "yankee",
            "zulu",
        ];
        foreach (var word in allNato)
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
        // invariant.
        var prompt = WhisperBiasingPrompt.Default;
        var tokens = prompt.Split(' ');

        var natoLetter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = 0,
            ["bravo"] = 1,
            ["charlie"] = 2,
            ["delta"] = 3,
            ["echo"] = 4,
            ["foxtrot"] = 5,
            ["golf"] = 6,
            ["hotel"] = 7,
            ["india"] = 8,
            ["juliet"] = 9,
            ["kilo"] = 10,
            ["lima"] = 11,
            ["mike"] = 12,
            ["november"] = 13,
            ["oscar"] = 14,
            ["papa"] = 15,
            ["quebec"] = 16,
            ["romeo"] = 17,
            ["sierra"] = 18,
            ["tango"] = 19,
            ["uniform"] = 20,
            ["victor"] = 21,
            ["whiskey"] = 22,
            ["xray"] = 23,
            ["yankee"] = 24,
            ["zulu"] = 25,
        };

        var natoSequence = new List<(string Word, int Letter)>();
        foreach (var token in tokens)
        {
            if (natoLetter.TryGetValue(token, out var letter))
            {
                natoSequence.Add((token, letter));
            }
        }

        // Sanity: all 26 present.
        Assert.Equal(26, natoSequence.Count);

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
