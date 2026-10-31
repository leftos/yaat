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
    private const string MissingHotwordsToken = "<missing-hotwords>";
    private const string SyntaxHotwordsToken = "<syntax-hotwords>";
    private const string BlankHotwordsToken = "<blank-hotwords>";
    private const string FolderToken = "<folder>";

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
    [InlineData("--device must be cpu or gpu, got 'tpu'", "uwb-atcc", "--device", "tpu")]
    [InlineData("--device applies to local engines; gemini runs in the cloud", "uwb-atcc", "--device", "gpu", "--stt", "gemini:x")]
    [InlineData("--device needs a value", "uwb-atcc", "--device")]
    [InlineData("the first argument must be the corpus")]
    [InlineData("the first argument must be the corpus", "--all")]
    [InlineData("nor an existing directory", MissingDirToken)]
    [InlineData("rename", UwbNamedDirToken)]
    [InlineData("--decoding must be greedy or beam, got 'x'", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "x")]
    [InlineData("--decoding applies to the sherpa engine, got whisper", "uwb-atcc", "--decoding", "beam")]
    [InlineData("--decoding applies to the sherpa engine, got sherpa-streaming", "uwb-atcc", "--stt", "sherpa-streaming:m", "--decoding", "greedy")]
    [InlineData("--decoding needs a value", "uwb-atcc", "--decoding")]
    [InlineData("--num-paths needs an integer from 1 to 4, got 'x'", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--num-paths", "x")]
    [InlineData("--num-paths needs an integer from 1 to 4, got '0'", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--num-paths", "0")]
    [InlineData("--num-paths needs an integer from 1 to 4, got '5'", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--num-paths", "5")]
    [InlineData("--num-paths needs --decoding beam", "uwb-atcc", "--stt", "sherpa:m", "--num-paths", "2")]
    [InlineData("--num-paths needs --decoding beam", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "greedy", "--num-paths", "1")]
    [InlineData("--hotwords-file needs --decoding beam", "uwb-atcc", "--stt", "sherpa:m", "--hotwords-file", "x.txt")]
    [InlineData("does not exist", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--hotwords-file", MissingHotwordsToken)]
    [InlineData("cannot contain '/'", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--hotwords-file", SyntaxHotwordsToken)]
    [InlineData("the hotwords file has no phrases", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--hotwords-file", BlankHotwordsToken)]
    [InlineData("--hotword-score needs a positive number, got 'x'", "uwb-atcc", "--hotword-score", "x")]
    [InlineData("--hotword-score needs a positive number, got '0'", "uwb-atcc", "--hotword-score", "0")]
    [InlineData("--hotword-score needs a positive number, got 'Infinity'", "uwb-atcc", "--hotword-score", "Infinity")]
    [InlineData("--hotword-score needs --hotwords-file", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--hotword-score", "2")]
    [InlineData("--hotword-case must be asis, title, lower or upper, got 'x'", "uwb-atcc", "--hotword-case", "x")]
    [InlineData("--hotword-case needs --hotwords-file", "uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam", "--hotword-case", "lower")]
    [InlineData("--slots needs a corpus folder, not uwb-atcc", "uwb-atcc", "--slots")]
    [InlineData("--resolve needs --slots", FolderToken, "--resolve")]
    [InlineData(
        "--room-hotwords must be unpadded, padded or oracle, got 'x'",
        FolderToken,
        "--slots",
        "--stt",
        "sherpa:m",
        "--decoding",
        "beam",
        "--room-hotwords",
        "x"
    )]
    [InlineData("--room-hotwords needs --slots", FolderToken, "--stt", "sherpa:m", "--decoding", "beam", "--room-hotwords", "padded")]
    [InlineData("--room-hotwords needs --decoding beam", FolderToken, "--slots", "--stt", "sherpa:m", "--room-hotwords", "padded")]
    [InlineData(
        "--room-hotwords and --hotwords-file exclude each other",
        FolderToken,
        "--slots",
        "--stt",
        "sherpa:m",
        "--decoding",
        "beam",
        "--room-hotwords",
        "padded",
        "--hotwords-file",
        "x.txt"
    )]
    [InlineData("--hotword-cap needs a positive integer, got '0'", FolderToken, "--hotword-cap", "0")]
    [InlineData("--hotword-cap needs a positive integer, got 'x'", FolderToken, "--hotword-cap", "x")]
    [InlineData("--hotword-cap needs --room-hotwords", FolderToken, "--slots", "--stt", "sherpa:m", "--decoding", "beam", "--hotword-cap", "50")]
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
        Assert.Equal(new SttChoice(SttKind.Sherpa, modelDir, SttDevice.Default, SherpaDecoding.Greedy), options.Stt);
    }

    [Fact]
    public void A_Streaming_Sherpa_Engine_With_An_Explicit_None_Prompt_Takes_One_Arm()
    {
        string modelDir = Path.Combine(Path.GetTempPath(), "models", "nemotron-560ms-int8");

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", $"sherpa-streaming:{modelDir}", "--prompt", "none"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(["none"], options.Prompts);
        Assert.Equal(new SttChoice(SttKind.SherpaStreaming, modelDir, SttDevice.Default, SherpaDecoding.Greedy), options.Stt);
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
        Assert.Equal(new SttChoice(kind, model, SttDevice.Default, SherpaDecoding.Greedy), options.Stt);
    }

    [Fact]
    public void A_Whisper_Url_Source_Survives_Its_First_Colon_And_Keeps_Both_Prompt_Arms()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", "whisper:https://h/m.bin"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(new SttChoice(SttKind.Whisper, "https://h/m.bin", SttDevice.Default, SherpaDecoding.Greedy), options.Stt);
        Assert.Equal(["default", "none"], options.Prompts);
    }

    [Fact]
    public void A_GPU_Device_On_A_Sherpa_Engine_Reaches_The_Choice()
    {
        string modelDir = Directory.CreateDirectory(Path.Combine(_dir, "models", "parakeet-v3-int8")).FullName;

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", $"sherpa:{modelDir}", "--device", "gpu"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(SttDevice.Gpu, options.Stt.Device);
        Assert.Equal("gpu", CorpusSttEngines.DeviceText(options.Stt));
    }

    [Theory]
    [InlineData("whisper:m", null, "auto")]
    [InlineData("sherpa-streaming:m", null, "cpu")]
    [InlineData("whisper:m", "cpu", "cpu")]
    public void The_Device_Text_A_Run_Records_Comes_From_The_Engine_And_The_Flag(string stt, string? device, string expected)
    {
        var args = new List<string> { "uwb-atcc", "--stt", stt };
        if (device is not null)
        {
            args.AddRange(["--device", device]);
        }

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([.. args]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(expected, CorpusSttEngines.DeviceText(options.Stt));
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

    [Fact]
    public void Beam_Decoding_Alone_Takes_Four_Paths_And_The_Default_Score()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", "sherpa:m", "--decoding", "beam"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(new SherpaDecoding(SherpaDecodingMethod.Beam, 4, 1.5f), options.Stt.Decoding);
        Assert.Empty(options.Hotwords);
        Assert.Null(options.HotwordsNote);
    }

    [Fact]
    public void An_Explicit_Greedy_Decoding_Is_The_Default_Choice()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc", "--stt", "sherpa:m", "--decoding", "greedy"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(SherpaDecoding.Greedy, options.Stt.Decoding);
    }

    [Fact]
    public void The_Hotword_Flags_Resolve_The_Choice_The_Phrases_And_The_Note()
    {
        string file = WriteHotwords("hotwords.txt", "Southwest", "", "KAISER  Air");

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([
            "uwb-atcc",
            "--stt",
            "sherpa:m",
            "--decoding",
            "beam",
            "--num-paths",
            "2",
            "--hotwords-file",
            file,
            "--hotword-score",
            "2.5",
            "--hotword-case",
            "lower",
        ]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(new SherpaDecoding(SherpaDecodingMethod.Beam, 2, 2.5f), options.Stt.Decoding);
        Assert.Equal(["southwest", "kaiser air"], options.Hotwords);
        Assert.Equal("hotwords.txt, 2 phrases, score 2.5, case lower", options.HotwordsNote);
        Assert.DoesNotContain(_dir, options.HotwordsNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Hotwords_File_Alone_Takes_Title_Case_And_The_Default_Score()
    {
        string file = WriteHotwords("hotwords.txt", "SOUTHWEST two");

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([
            "uwb-atcc",
            "--stt",
            "sherpa:m",
            "--decoding",
            "beam",
            "--hotwords-file",
            file,
        ]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(["Southwest Two"], options.Hotwords);
        Assert.Equal("hotwords.txt, 1 phrases, score 1.5, case title", options.HotwordsNote);
    }

    [Fact]
    public void A_Hotwords_File_That_Cannot_Be_Read_Fails_With_A_Message()
    {
        string file = WriteHotwords("held.txt", "Southwest");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([
            "uwb-atcc",
            "--stt",
            "sherpa:m",
            "--decoding",
            "beam",
            "--hotwords-file",
            file,
        ]);

        Assert.Null(parse.Options);
        Assert.NotNull(parse.Error);
        Assert.StartsWith($"could not read the hotwords file {file}: ", parse.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Slots_ParsesResolveAndRoomHotwords()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([
            Substitute(FolderToken),
            "--slots",
            "--resolve",
            "--stt",
            "sherpa:m",
            "--decoding",
            "beam",
            "--room-hotwords",
            "padded",
        ]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(new CorpusSlotOptions(true, SlotRoom.Padded, 300, HotwordCase.Title), options.Slots);
        Assert.Equal("room padded, cap 300, score 1.5, case title", options.HotwordsNote);
        Assert.Empty(options.Hotwords);
    }

    [Fact]
    public void HotwordScoreAndCase_ApplyToRoomHotwords()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([
            Substitute(FolderToken),
            "--slots",
            "--stt",
            "sherpa:m",
            "--decoding",
            "beam",
            "--room-hotwords",
            "oracle",
            "--hotword-score",
            "2.5",
            "--hotword-case",
            "lower",
            "--hotword-cap",
            "50",
        ]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal(new CorpusSlotOptions(false, SlotRoom.Oracle, 50, HotwordCase.Lower), options.Slots);
        Assert.Equal(new SherpaDecoding(SherpaDecodingMethod.Beam, 4, 2.5f), options.Stt.Decoding);
        Assert.Equal("room oracle, cap 50, score 2.5, case lower", options.HotwordsNote);
    }

    [Fact]
    public void NoSlots_SlotsNull()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([Substitute(FolderToken)]);

        Assert.Null(parse.Error);
        Assert.Null(Assert.IsType<CorpusWerRunner.Options>(parse.Options).Slots);
    }

    private string WriteHotwords(string name, params string[] lines)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private string Substitute(string arg) =>
        arg switch
        {
            MissingDirToken => Path.Combine(_dir, "does-not-exist"),
            UwbNamedDirToken => Directory.CreateDirectory(Path.Combine(_dir, "UWB-ATCC")).FullName,
            MissingHotwordsToken => Path.Combine(_dir, "no-hotwords.txt"),
            SyntaxHotwordsToken => WriteHotwords("syntax.txt", "alpha/bravo"),
            BlankHotwordsToken => WriteHotwords("blank.txt", "", "  "),
            FolderToken => Directory.CreateDirectory(Path.Combine(_dir, "liveatc")).FullName,
            _ => arg,
        };
}
