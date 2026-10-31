using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The cloud STT engine's pure parts: reading a transcript out of each provider's response body, and the
/// cache key that decides whether a clip is sent again. No test makes a network call.
/// </summary>
public sealed class CloudSttEngineTests
{
    private static readonly byte[] Wav = [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4];

    [Fact]
    public void Gemini_Joins_The_Text_Parts_And_Skips_Thoughts()
    {
        const string json = """
            {"candidates":[{"content":{"parts":[
              {"text":"thinking about it","thought":true},
              {"text":"united two three four "},
              {"text":"climb and maintain one two thousand"}
            ]}}]}
            """;

        Assert.Equal("united two three four climb and maintain one two thousand", CloudSttEngine.ParseGemini(json));
    }

    [Theory]
    [InlineData("""{"candidates":[{"finishReason":"MAX_TOKENS"}]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"united"}]},"finishReason":"SAFETY"}]}""")]
    public void Gemini_With_An_Abnormal_Finish_Is_Invalid_Data(string json) =>
        Assert.Throws<InvalidDataException>(() => CloudSttEngine.ParseGemini(json));

    [Fact]
    public void Gemini_Stopped_With_No_Parts_Is_An_Empty_Transcript() =>
        Assert.Equal(string.Empty, CloudSttEngine.ParseGemini("""{"candidates":[{"content":{"role":"model"},"finishReason":"STOP"}]}"""));

    [Fact]
    public void Gemini_Without_Candidates_Is_Invalid_Data() =>
        Assert.Throws<InvalidDataException>(() => CloudSttEngine.ParseGemini("""{"candidates":[]}"""));

    [Fact]
    public void OpenAi_Returns_The_Text_Field() =>
        Assert.Equal("cleared to land runway two eight right", CloudSttEngine.ParseOpenAi("""{"text":"cleared to land runway two eight right"}"""));

    [Fact]
    public void OpenAi_Without_A_Text_Field_Is_Invalid_Data() => Assert.Throws<InvalidDataException>(() => CloudSttEngine.ParseOpenAi("{}"));

    [Fact]
    public void The_Cache_Key_Is_Stable_For_Identical_Inputs() =>
        Assert.Equal(Key(Wav, SttKind.Gemini, "gemini-4-argon", "hint"), Key([.. Wav], SttKind.Gemini, "gemini-4-argon", "hint"));

    [Fact]
    public void The_Cache_Key_Changes_With_Every_Input()
    {
        string baseline = Key(Wav, SttKind.Gemini, "gemini-4-argon", "hint");
        byte[] otherWav = [.. Wav];
        otherWav[^1] ^= 0xFF;

        string[] variants =
        [
            Key(Wav, SttKind.Gemini, "gemini-4-flash", "hint"),
            Key(Wav, SttKind.OpenAi, "gemini-4-argon", "hint"),
            Key(Wav, SttKind.Gemini, "gemini-4-argon", "hint!"),
            Key(otherWav, SttKind.Gemini, "gemini-4-argon", "hint"),
        ];

        Assert.All(variants, variant => Assert.NotEqual(baseline, variant));
    }

    [Fact]
    public void The_Cache_Key_Changes_With_The_User_Text_Alone() =>
        Assert.NotEqual(
            Key(Wav, SttKind.Gemini, "gemini-4-argon", "hint\nTranscribe this clip verbatim."),
            Key(Wav, SttKind.Gemini, "gemini-4-argon", "hint\nTranscribe this clip.")
        );

    [Theory]
    [InlineData("gpt-transcribe", "languages[]")]
    [InlineData("gpt-4o-transcribe", "language")]
    public void OpenAi_Sends_The_Language_Field_The_Model_Takes(string model, string field) =>
        Assert.Equal((field, "en"), CloudSttEngine.OpenAiLanguageField(model));

    private static string Key(byte[] wav, SttKind kind, string model, string instruction) => CloudSttEngine.CacheKey(wav, kind, model, instruction);
}
