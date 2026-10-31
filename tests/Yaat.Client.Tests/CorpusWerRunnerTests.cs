using System.Text.Json;
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

        Assert.Equal("ggml-atc.bin", CorpusSttEngines.RecordedModel(new SttChoice(SttKind.Whisper, path, SttDevice.Default, SherpaDecoding.Greedy)));
    }

    [Theory]
    [InlineData("https://huggingface.co/jacktol/whisper/resolve/main/ggml-model.bin")]
    [InlineData("whisper-medium")]
    public void A_Url_Or_Catalog_Id_Is_Recorded_As_Given(string source) =>
        Assert.Equal(source, CorpusSttEngines.RecordedModel(new SttChoice(SttKind.Whisper, source, SttDevice.Default, SherpaDecoding.Greedy)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Sherpa_Model_Dir_Is_Recorded_As_Sherpa_And_Its_Dir_Name(bool trailingSeparator)
    {
        string dir = ModelDir("parakeet-v3-int8", trailingSeparator);

        Assert.Equal(
            "sherpa:parakeet-v3-int8",
            CorpusSttEngines.RecordedModel(new SttChoice(SttKind.Sherpa, dir, SttDevice.Default, SherpaDecoding.Greedy))
        );
    }

    [Fact]
    public void A_Beam_Sherpa_Engine_Is_Recorded_With_Its_Path_Count()
    {
        var decoding = new SherpaDecoding(SherpaDecodingMethod.Beam, 4, 1.5f);

        Assert.Equal(
            "sherpa:parakeet-v3-int8+beam4",
            CorpusSttEngines.RecordedModel(new SttChoice(SttKind.Sherpa, ModelDir("parakeet-v3-int8", false), SttDevice.Default, decoding))
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Streaming_Sherpa_Model_Dir_Is_Recorded_With_Its_Own_Prefix(bool trailingSeparator)
    {
        string dir = ModelDir("nemotron-560ms-int8", trailingSeparator);

        Assert.Equal(
            "sherpa-streaming:nemotron-560ms-int8",
            CorpusSttEngines.RecordedModel(new SttChoice(SttKind.SherpaStreaming, dir, SttDevice.Default, SherpaDecoding.Greedy))
        );
    }

    [Theory]
    [InlineData(SttKind.Gemini, "gemini-4-argon", "gemini:gemini-4-argon")]
    [InlineData(SttKind.OpenAi, "gpt-transcribe", "openai:gpt-transcribe")]
    public void A_Cloud_Model_Is_Recorded_With_Its_Kind_Prefix(SttKind kind, string model, string expected) =>
        Assert.Equal(expected, CorpusSttEngines.RecordedModel(new SttChoice(kind, model, SttDevice.Default, SherpaDecoding.Greedy)));

    [Fact]
    public void The_Report_Renders_An_Arm_That_Carries_A_Timing()
    {
        CorpusWerTiming timing = CorpusWerLatency.Summarize("cpu", 1.5, [(412, false), (100, true)], new CorpusWerMemory(10, 20, -1, -1));

        string report = CorpusWerRunner.BuildReport(Run(timing), []);

        Assert.Contains("Device cpu, model load and warm-up 1.5 s", report, StringComparison.Ordinal);
        Assert.Contains("p50 412 ms", report, StringComparison.Ordinal);
        Assert.Contains("1 cached, excluded", report, StringComparison.Ordinal);
        Assert.Contains("GPU memory n/a", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Report_Renders_The_GPU_Memory_Figures_It_Measured()
    {
        CorpusWerTiming timing = CorpusWerLatency.Summarize("gpu", 1.5, [(412, false)], new CorpusWerMemory(10, 20, 0, 2L * 1024 * 1024 * 1024));

        string report = CorpusWerRunner.BuildReport(Run(timing), []);

        Assert.Contains("GPU memory 0 MB before load, peak 2048 MB", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Report_Renders_A_Device_Warning_Under_The_Timing_Block()
    {
        const string warning = "CPU mode, but the process's GPU memory rose 100 MB through model load";
        CorpusWerTiming timing = CorpusWerLatency.Summarize("cpu", 1.5, [(412, false)], new CorpusWerMemory(10, 20, 0, 100L * 1024 * 1024)) with
        {
            DeviceWarning = warning,
        };

        string report = CorpusWerRunner.BuildReport(Run(timing), []);

        Assert.Contains(warning, report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Report_Renders_The_Provenance_And_Feed_Tables_Before_The_Audio_Line()
    {
        CorpusWerBaseline run = RunWith(
            null,
            [new CorpusWerRoleTotals("owner", 1, 10, 0, 0, 0, 0), new CorpusWerRoleTotals("owner+agreed", 2, 30, 1, 2, 0, 0.1)],
            [new CorpusWerRoleTotals("koak-app", 2, 30, 1, 2, 0, 0.1)]
        );

        string report = CorpusWerRunner.BuildReport(run, []);

        Assert.Contains("| Provenance | Segments | Reference words | S | D | I | WER |", report, StringComparison.Ordinal);
        Assert.Contains("| owner+agreed | 2 | 30 | 1 | 2 | 0 | 10.0% |", report, StringComparison.Ordinal);
        Assert.Contains("| Feed | Segments | Reference words | S | D | I | WER |", report, StringComparison.Ordinal);
        Assert.Contains("| koak-app | 2 | 30 | 1 | 2 | 0 | 10.0% |", report, StringComparison.Ordinal);
        Assert.True(report.IndexOf("| Provenance |", StringComparison.Ordinal) < report.IndexOf("| Feed |", StringComparison.Ordinal));
        Assert.True(report.IndexOf("| Feed |", StringComparison.Ordinal) < report.IndexOf("Audio ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Report_Omits_The_Group_Tables_For_An_Arm_Without_Them()
    {
        string report = CorpusWerRunner.BuildReport(Run(null), []);

        Assert.DoesNotContain("| Provenance |", report, StringComparison.Ordinal);
        Assert.DoesNotContain("| Feed |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Report_Omits_The_Timing_Lines_For_An_Arm_Without_One()
    {
        string report = CorpusWerRunner.BuildReport(Run(null), []);

        Assert.DoesNotContain("model load", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Latency over", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory: working set", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Report_Header_Names_The_Hotwords_Note()
    {
        string report = CorpusWerRunner.BuildReport(Run(null) with { Hotwords = "hot.txt, 3 phrases, score 1.5, case title" }, []);

        Assert.Contains("model `model`, hotwords hot.txt, 3 phrases, score 1.5, case title", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Report_Header_Omits_Hotwords_For_A_Run_Without_Them() =>
        Assert.DoesNotContain("hotwords", CorpusWerRunner.BuildReport(Run(null), []), StringComparison.Ordinal);

    [Fact]
    public void A_Segment_Line_Without_Nbest_Serializes_A_Null()
    {
        string json = JsonSerializer.Serialize(Line(null), CorpusWerJsonlContext.Default.CorpusWerSegmentLine);

        Assert.Contains("\"nbest\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Segment_Line_With_Nbest_Serializes_Every_Entry()
    {
        CorpusWerSegmentLine line = Line([new SttHypothesis("alpha", ["a"], [-0.5], -1.25), new SttHypothesis("alto", ["b"], [-0.75], null)]);

        string json = JsonSerializer.Serialize(line, CorpusWerJsonlContext.Default.CorpusWerSegmentLine);

        Assert.Contains(
            "\"nbest\":[{\"text\":\"alpha\",\"tokens\":[\"a\"],\"tokenLogProbs\":[-0.5],\"score\":-1.25},"
                + "{\"text\":\"alto\",\"tokens\":[\"b\"],\"tokenLogProbs\":[-0.75],\"score\":null}]",
            json,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData(null)]
    public void A_Transcript_Without_Tokens_Records_No_Nbest(string? text) =>
        Assert.Null(CorpusWerRunner.RecordedNbest(SttHypotheses.Single(text, null, false)));

    [Fact]
    public void Entries_With_Tokens_Are_The_Recorded_Nbest()
    {
        SttHypothesis[] entries = [new SttHypothesis("alpha", ["a"], [-0.5], -1.25), new SttHypothesis("alto", [], [], -2)];

        Assert.Same(entries, CorpusWerRunner.RecordedNbest(new SttHypotheses(entries, null, false)));
    }

    private static CorpusWerSegmentLine Line(IReadOnlyList<SttHypothesis>? nbest) =>
        new("none", "id", "pilot", "alpha", "alpha", "alpha", 0, 0, 0, 1, 12, false, nbest);

    private static CorpusWerBaseline Run(CorpusWerTiming? timing) => RunWith(timing, null, null);

    private static CorpusWerBaseline RunWith(
        CorpusWerTiming? timing,
        IReadOnlyList<CorpusWerRoleTotals>? provenance,
        IReadOnlyList<CorpusWerRoleTotals>? feeds
    ) =>
        new(
            "uwb-atcc",
            "rev",
            "sha",
            1117,
            400,
            "fp",
            "model",
            [new CorpusWerArm("default", [new CorpusWerRoleTotals("all", 10, 100, 0, 0, 0, 0.2)], 30, 6, 0, timing, provenance, feeds)],
            null
        );

    private static string ModelDir(string name, bool trailingSeparator)
    {
        string dir = Path.Combine(Path.GetTempPath(), "models", name);
        return trailingSeparator ? dir + Path.DirectorySeparatorChar : dir;
    }
}
