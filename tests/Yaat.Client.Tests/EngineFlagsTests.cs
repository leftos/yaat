using Xunit;
using Yaat.Client.Services.Speech;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The engine flags <c>--eval</c> and <c>--atc-ouroboros</c> share: each flag's value parses or fails with its message, and
/// the flags resolve into one engine choice or the first cross-flag error.
/// </summary>
public sealed class EngineFlagsTests
{
    [Fact]
    public void Beam_On_Sherpa_Takes_The_Default_Path_Count()
    {
        // A rooted model directory on whichever OS runs the test; a Windows literal is not a path on macOS or Linux.
        string modelDir = Path.Combine(Path.GetTempPath(), "m", "rasr");
        SttChoice? choice = Resolve(out string? error, "--stt", $"sherpa:{modelDir}", "--decoding", "beam");

        Assert.Null(error);
        Assert.NotNull(choice);
        Assert.Equal(SttKind.Sherpa, choice.Kind);
        Assert.Equal(SherpaDecodingMethod.Beam, choice.Decoding.Method);
        Assert.Equal(4, choice.Decoding.NumPaths);
        Assert.Equal("sherpa:rasr+beam4", CorpusSttEngines.RecordedModel(choice));
    }

    [Fact]
    public void Num_Paths_Sets_The_Beam_Path_Count()
    {
        SttChoice? choice = Resolve(out string? error, "--stt", @"sherpa:C:\m\rasr", "--decoding", "beam", "--num-paths", "2");

        Assert.Null(error);
        Assert.Equal(2, choice!.Decoding.NumPaths);
    }

    [Fact]
    public void Device_Goes_Onto_A_Local_Engine()
    {
        SttChoice? choice = Resolve(out string? error, "--stt", @"sherpa:C:\m\rasr", "--device", "gpu");

        Assert.Null(error);
        Assert.Equal(SttDevice.Gpu, choice!.Device);
        Assert.Equal(SherpaDecodingMethod.Greedy, choice.Decoding.Method);
    }

    [Theory]
    [InlineData("--num-paths needs --decoding beam", "--stt", "sherpa:m", "--num-paths", "2")]
    [InlineData("--decoding applies to the sherpa engine, got whisper", "--stt", "whisper:x", "--decoding", "beam")]
    [InlineData("--device applies to local engines; gemini runs in the cloud", "--stt", "gemini:x", "--device", "gpu")]
    [InlineData("--device, --decoding and --num-paths need --stt", "--device", "cpu")]
    public void Contradicting_Flags_Fail_With_Their_Message(string expected, params string[] args)
    {
        SttChoice? choice = Resolve(out string? error, args);

        Assert.Null(choice);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Num_Paths_Beyond_The_Beam_Limit_Fails_To_Apply()
    {
        var flags = new EngineFlags();

        Assert.Equal("--num-paths needs an integer from 1 to 4, got '9'", flags.TryApply("--num-paths", "9"));
    }

    [Fact]
    public void No_Engine_Flags_Resolve_To_No_Choice_And_No_Error()
    {
        SttChoice? choice = new EngineFlags().Resolve(out string? error);

        Assert.Null(choice);
        Assert.Null(error);
    }

    [Fact]
    public void Handles_Exactly_The_Engine_Flags()
    {
        string[] engineFlags = ["--stt", "--device", "--decoding", "--num-paths"];
        Assert.All(engineFlags, flag => Assert.True(EngineFlags.Handles(flag)));
        Assert.False(EngineFlags.Handles("--trials"));
    }

    /// <summary>Applies <paramref name="args"/> as flag/value pairs, failing the test on a value that does not apply, then resolves.</summary>
    private static SttChoice? Resolve(out string? error, params string[] args)
    {
        var flags = new EngineFlags();
        for (int i = 0; i < args.Length; i += 2)
        {
            Assert.Null(flags.TryApply(args[i], args[i + 1]));
        }
        return flags.Resolve(out error);
    }
}
