using Xunit;
using Yaat.Client.Services;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--corpus-wer</c> argument parser: every malformed command line fails with a message naming
/// the problem, and the corpus defaults (subset, seed, prompt arms) fill in for a well-formed one.
/// </summary>
public sealed class CorpusWerArgsTests : IDisposable
{
    private const string MissingDirToken = "<missing-dir>";
    private const string UwbNamedDirToken = "<uwb-named-dir>";

    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-wer-args-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("exclude each other", "uwb-atcc", "--subset", "5", "--all")]
    [InlineData("--subset needs a positive integer", "uwb-atcc", "--subset", "0")]
    [InlineData("--subset needs a positive integer", "uwb-atcc", "--subset", "x")]
    [InlineData("--prompt must be default, none or both", "uwb-atcc", "--prompt", "loud")]
    [InlineData("--seed needs a value", "uwb-atcc", "--seed")]
    [InlineData("--stt kind must be whisper, sherpa, sherpa-streaming, gemini or openai, got 'vosk'", "uwb-atcc", "--stt", "vosk:x")]
    [InlineData("--stt needs <kind>:<source>", "uwb-atcc", "--stt", "sherpa")]
    [InlineData("--stt needs <kind>:<source>", "uwb-atcc", "--stt", "sherpa:")]
    [InlineData("needs a Whisper engine; sherpa engines take no prompt", "uwb-atcc", "--stt", "sherpa:m", "--prompt", "default")]
    [InlineData("needs a Whisper engine; sherpa engines take no prompt", "uwb-atcc", "--stt", "sherpa:m", "--prompt", "both")]
    [InlineData("needs a Whisper engine; sherpa-streaming engines take no prompt", "uwb-atcc", "--stt", "sherpa-streaming:m", "--prompt", "both")]
    [InlineData("needs a Whisper engine; gemini engines take no prompt", "uwb-atcc", "--stt", "gemini:m", "--prompt", "default")]
    [InlineData("the sherpa engine cannot update it", "uwb-atcc", "--stt", "sherpa:m", "--update-baseline")]
    [InlineData("the openai engine cannot update it", "uwb-atcc", "--stt", "openai:m", "--update-baseline")]
    [InlineData("unrecognised argument '--whisper'", "uwb-atcc", "--whisper", "x")]
    [InlineData("unrecognised argument '--frobnicate'", "uwb-atcc", "--frobnicate")]
    [InlineData("the first argument must be the corpus")]
    [InlineData("the first argument must be the corpus", "--all")]
    [InlineData("nor an existing directory", MissingDirToken)]
    [InlineData("rename", UwbNamedDirToken)]
    public void A_Malformed_Command_Line_Fails_With_A_Message(string fragment, params string[] args)
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([.. args.Select(Substitute)]);

        Assert.Null(parse.Options);
        Assert.NotNull(parse.Error);
        Assert.Contains(fragment, parse.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Uwb_Atcc_Alone_Takes_The_Uwb_Defaults()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal("uwb-atcc", options.Corpus);
        Assert.Null(options.CorpusDir);
        Assert.Equal(400, options.Subset);
        Assert.Equal(1117, options.Seed);
        Assert.Equal(["default", "none"], options.Prompts);
    }

    [Fact]
    public void A_Folder_Takes_Every_Segment_And_Its_Lowercased_Name()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "MyCorpus")).FullName;

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([folder]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Null(options.Subset);
        Assert.Equal("mycorpus", options.Corpus);
        Assert.Equal(folder, options.CorpusDir);
    }

    [Fact]
    public void A_Sherpa_Engine_Takes_No_Prompt_And_Keeps_Its_Model_Dir()
    {
        string modelDir = Path.Combine(Path.GetTempPath(), "models", "parakeet-v3-int8");

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", $"sherpa:{modelDir}"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(["none"], options.Prompts);
        Assert.Equal(new SttChoice(SttKind.Sherpa, modelDir), options.Stt);
    }

    [Fact]
    public void A_Streaming_Sherpa_Engine_With_An_Explicit_None_Prompt_Takes_One_Arm()
    {
        string modelDir = Path.Combine(Path.GetTempPath(), "models", "nemotron-560ms-int8");

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", $"sherpa-streaming:{modelDir}", "--prompt", "none"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(["none"], options.Prompts);
        Assert.Equal(new SttChoice(SttKind.SherpaStreaming, modelDir), options.Stt);
    }

    [Theory]
    [InlineData("gemini:gemini-4-argon", SttKind.Gemini, "gemini-4-argon")]
    [InlineData("openai:gpt-transcribe", SttKind.OpenAi, "gpt-transcribe")]
    public void A_Cloud_Engine_Takes_No_Prompt_And_Keeps_Its_Model_Id(string stt, SttKind kind, string model)
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", stt]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(["none"], options.Prompts);
        Assert.Equal(new SttChoice(kind, model), options.Stt);
    }

    [Fact]
    public void A_Whisper_Url_Source_Survives_Its_First_Colon_And_Keeps_Both_Prompt_Arms()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", "whisper:https://h/m.bin"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(new SttChoice(SttKind.Whisper, "https://h/m.bin"), options.Stt);
        Assert.Equal(["default", "none"], options.Prompts);
    }

    [Fact]
    public void Uwb_Atcc_Alone_Defaults_To_The_Recommended_Whisper_Model()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(SttKind.Whisper, options.Stt.Kind);
        Assert.Equal(LmKitModelCatalog.RecommendedWhisperId, options.Stt.Source);
    }

    private string Substitute(string arg) =>
        arg switch
        {
            MissingDirToken => Path.Combine(_dir, "does-not-exist"),
            UwbNamedDirToken => Directory.CreateDirectory(Path.Combine(_dir, "UWB-ATCC")).FullName,
            _ => arg,
        };
}
