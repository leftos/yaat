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

        Assert.Equal("ggml-atc.bin", CorpusWerRunner.RecordedModel(path));
    }

    [Theory]
    [InlineData("https://huggingface.co/jacktol/whisper/resolve/main/ggml-model.bin")]
    [InlineData("whisper-medium")]
    public void A_Url_Or_Catalog_Id_Is_Recorded_As_Given(string source) => Assert.Equal(source, CorpusWerRunner.RecordedModel(source));
}
