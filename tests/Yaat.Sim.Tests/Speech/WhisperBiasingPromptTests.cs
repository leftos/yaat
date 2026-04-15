using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class WhisperBiasingPromptTests
{
    [Fact]
    public void Default_DoesNotContainNatoAlphabet()
    {
        // NATO phonetic words in the biasing prompt prime whisper-large-turbo3 to extrapolate
        // alphabetical letter sequences. Real-world failure: pilot said "tango uniform whiskey"
        // (taxi via T U W), Whisper extended to "tango uniform whiskey xray yankee" — X and Y
        // weren't in the audio at all. The biasing prompt was the cue: NATO words sorted
        // alphabetically in the prompt + NATO letters trained into Whisper from ATC corpora
        // → strong prior toward completing the sequence.
        //
        // whisper-large-turbo3 recognizes NATO words as ordinary English ("tango", "whiskey",
        // "november", "bravo" are all common words), so removing them from the biasing prompt
        // costs nothing for single-letter recognition and eliminates the alphabet-completion
        // bias. Callsign probes (see docs/speech-pipeline.md) confirmed tail-number recognition
        // works without per-callsign biasing — this is the static equivalent.
        var prompt = WhisperBiasingPrompt.Default;

        // Hallmark uncommon NATO words (unlikely to appear as English vocabulary by accident).
        string[] natoMarkers = ["foxtrot", "juliet", "kilo", "quebec", "sierra", "tango", "uniform", "whiskey", "xray", "yankee", "zulu"];
        foreach (var word in natoMarkers)
        {
            Assert.DoesNotContain(word, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Default_StillContainsAtcCommandVocabulary()
    {
        // Regression guard: make sure the fix to drop NATO didn't accidentally wipe the
        // command-verb literals that the rule engine relies on for recognition biasing.
        var prompt = WhisperBiasingPrompt.Default;

        string[] mustContain = ["climb", "descend", "runway", "heading", "cleared", "takeoff", "approach", "maintain", "turn"];
        foreach (var word in mustContain)
        {
            Assert.Contains(word, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }
}
