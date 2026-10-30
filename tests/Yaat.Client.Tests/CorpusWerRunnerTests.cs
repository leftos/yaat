using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--corpus-wer</c> runner's pure decisions: when an arm's opening transcripts say the model
/// never loaded, and how the model source is recorded in a results document.
/// </summary>
public sealed class CorpusWerRunnerTests
{
    [Fact]
    public void Ten_Null_Transcripts_Look_Like_A_Load_Failure() =>
        Assert.True(CorpusWerRunner.LooksLikeLoadFailure([.. Enumerable.Repeat<string?>(null, 10)]));

    [Fact]
    public void One_Transcript_Among_Ten_Is_Not_A_Load_Failure() =>
        Assert.False(CorpusWerRunner.LooksLikeLoadFailure([.. Enumerable.Repeat<string?>(null, 9), "cleared to land"]));

    [Fact]
    public void Every_Segment_Of_A_Short_Arm_Null_Looks_Like_A_Load_Failure() =>
        Assert.True(CorpusWerRunner.LooksLikeLoadFailure([null, null, null]));

    [Fact]
    public void No_Segments_Are_Not_A_Load_Failure() => Assert.False(CorpusWerRunner.LooksLikeLoadFailure([]));

    [Fact]
    public void A_Rooted_Model_Path_Is_Recorded_As_Its_File_Name()
    {
        string path = Path.Combine(Path.GetTempPath(), "models", "ggml-atc.bin");

        Assert.Equal("ggml-atc.bin", CorpusSttEngines.RecordedModel(new SttChoice(SttKind.Whisper, path)));
    }

    [Theory]
    [InlineData("https://huggingface.co/jacktol/whisper/resolve/main/ggml-model.bin")]
    [InlineData("whisper-medium")]
    public void A_Url_Or_Catalog_Id_Is_Recorded_As_Given(string source) =>
        Assert.Equal(source, CorpusSttEngines.RecordedModel(new SttChoice(SttKind.Whisper, source)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Sherpa_Model_Dir_Is_Recorded_As_Sherpa_And_Its_Dir_Name(bool trailingSeparator)
    {
        string dir = ModelDir("parakeet-v3-int8", trailingSeparator);

        Assert.Equal("sherpa:parakeet-v3-int8", CorpusSttEngines.RecordedModel(new SttChoice(SttKind.Sherpa, dir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Streaming_Sherpa_Model_Dir_Is_Recorded_With_Its_Own_Prefix(bool trailingSeparator)
    {
        string dir = ModelDir("nemotron-560ms-int8", trailingSeparator);

        Assert.Equal("sherpa-streaming:nemotron-560ms-int8", CorpusSttEngines.RecordedModel(new SttChoice(SttKind.SherpaStreaming, dir)));
    }

    [Theory]
    [InlineData(SttKind.Gemini, "gemini-4-argon", "gemini:gemini-4-argon")]
    [InlineData(SttKind.OpenAi, "gpt-transcribe", "openai:gpt-transcribe")]
    public void A_Cloud_Model_Is_Recorded_With_Its_Kind_Prefix(SttKind kind, string model, string expected) =>
        Assert.Equal(expected, CorpusSttEngines.RecordedModel(new SttChoice(kind, model)));

    private static string ModelDir(string name, bool trailingSeparator)
    {
        string dir = Path.Combine(Path.GetTempPath(), "models", name);
        return trailingSeparator ? dir + Path.DirectorySeparatorChar : dir;
    }
}
