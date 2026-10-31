using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests.Services;

/// <summary>
/// Tests for <see cref="EspeakPhonemizer"/>. The package's build target copies espeak-ng-data beside
/// the test assembly, so phonemization runs against the same data the client's voice pack ships.
/// </summary>
public class EspeakPhonemizerTests
{
    private static readonly string EspeakDataDir = Path.Combine(AppContext.BaseDirectory, "espeak-ng-data");

    [Fact]
    public void Phonemize_OneSentence_ReturnsIpaCodepoints()
    {
        int[][] sentences = EspeakPhonemizer.Phonemize("cleared for takeoff runway two eight right", "en-us", EspeakDataDir);

        int[] sentence = Assert.Single(sentences);
        Assert.NotEmpty(sentence);
        Assert.Contains(sentence, codepoint => codepoint > 127);
    }

    [Fact]
    public void Phonemize_TwoSentences_ReturnsTwoEntries()
    {
        int[][] sentences = EspeakPhonemizer.Phonemize("Roger. Wilco.", "en-us", EspeakDataDir);

        Assert.Equal(2, sentences.Length);
        Assert.All(sentences, sentence => Assert.NotEmpty(sentence));
    }

    [Fact]
    public void Phonemize_MissingDataDir_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"yaat-espeak-missing-{Guid.NewGuid():N}");

        Assert.Throws<DirectoryNotFoundException>(() => EspeakPhonemizer.Phonemize("roger", "en-us", missing));
    }

    [Fact]
    public void RequireSpeech_Empty_Throws() => Assert.Throws<InvalidDataException>(() => EspeakPhonemizer.RequireSpeech([], "test synthesis"));

    [Fact]
    public void RequireSpeech_AllZero_Throws() =>
        Assert.Throws<InvalidDataException>(() => EspeakPhonemizer.RequireSpeech(new float[100], "test synthesis"));

    [Fact]
    public void RequireSpeech_NonZero_ReturnsSamples()
    {
        float[] samples = [0f, 0f, 0.5f];

        Assert.Same(samples, EspeakPhonemizer.RequireSpeech(samples, "test synthesis"));
    }

    [Fact]
    public void VoiceFor_Empty_FallsBackToEnUs() => Assert.Equal(EspeakPhonemizer.DefaultVoice, EspeakPhonemizer.VoiceFor(""));

    [Fact]
    public void VoiceFor_Set_ReturnsIt() => Assert.Equal("en-gb", EspeakPhonemizer.VoiceFor("en-gb"));
}
