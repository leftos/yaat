extern alias mcp;

using mcp::Yaat.ClientDriver.Mcp.Recording;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The one ffmpeg that reads the recorder's raw frames from stdin and, with audio, its PCM pipe: the arguments the
/// video-capture skill's wgc route uses, the cmd line that joins the recorder to ffmpeg, and the encoder choice.
/// </summary>
public sealed class FfmpegPipelineTests
{
    private static readonly CropBox Crop = new(1, 31, 1200, 700);

    // nvenc takes BGRA itself, so no -pix_fmt on the output; the audio pipe is the second input and AAC the audio codec.
    [Fact]
    public void Args_Nvenc_WithAudio()
    {
        IReadOnlyList<string> args = FfmpegPipeline.Arguments(
            new FfmpegSpec(FfmpegPipeline.Nvenc, new FrameFormat(1202, 732, 30), Crop, @"C:\out\clip.mp4", "yaat-rec-1")
        );

        string[] expected =
        [
            "-hide_banner",
            "-n",
            "-use_wallclock_as_timestamps",
            "1",
            "-f",
            "rawvideo",
            "-pix_fmt",
            "bgra",
            "-video_size",
            "1202x732",
            "-framerate",
            "30",
            "-i",
            "-",
            "-f",
            "s16le",
            "-ar",
            "48000",
            "-ac",
            "2",
            "-i",
            @"\\.\pipe\yaat-rec-1",
            "-vf",
            "crop=1200:700:1:31",
            "-c:v",
            "h264_nvenc",
            "-fps_mode",
            "vfr",
            "-c:a",
            "aac",
            "-b:a",
            "192k",
            "-movflags",
            "+faststart",
            @"C:\out\clip.mp4",
        ];
        Assert.Equal(expected, args);
    }

    [Fact]
    public void Args_Libx264_NoAudio_AddsYuv420p()
    {
        IReadOnlyList<string> args = FfmpegPipeline.Arguments(
            new FfmpegSpec(FfmpegPipeline.Libx264, new FrameFormat(640, 480, 15), Crop, @"C:\out\clip.mp4", null)
        );

        string joined = string.Join(' ', args);
        Assert.Contains(
            "-framerate 15 -i - -vf crop=1200:700:1:31 -c:v libx264 -pix_fmt yuv420p -fps_mode vfr -movflags",
            joined,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("s16le", args);
        Assert.DoesNotContain("-c:a", args);
    }

    // cmd expands %VAR% in its command line even inside quotes, and !VAR! with delayed expansion on, but never rescans an
    // expanded value: every path reaches cmd through a variable, quoted, so a % or ! in a folder name stays literal.
    [Fact]
    public void CommandLine_ReferencesPathsThroughEnvironment_NeverLiteralPaths()
    {
        PipelineCommandLine line = FfmpegPipeline.CommandLine(OddPathsCommand);

        Assert.Equal(
            "\"%YAAT_REC_RECORDER%\" --hwnd 4660 --until-file \"%YAAT_REC_UNTIL_FILE%\" 2>\"%YAAT_REC_RECORDER_LOG%\" "
                + "| \"%YAAT_REC_FFMPEG%\" -i - \"%YAAT_REC_OUT%\" 2>\"%YAAT_REC_FFMPEG_LOG%\"",
            line.Text
        );
        IReadOnlyDictionary<string, string> expected = new Dictionary<string, string>
        {
            ["YAAT_REC_RECORDER"] = @"C:\%USERNAME%\rec dir\Yaat.WindowRecorder.exe",
            ["YAAT_REC_UNTIL_FILE"] = @"C:\clips !x!\a & b.mp4.deadline",
            ["YAAT_REC_RECORDER_LOG"] = @"C:\clips !x!\a & b-recorder.log",
            ["YAAT_REC_FFMPEG"] = @"C:\ff mpeg\ffmpeg.exe",
            ["YAAT_REC_OUT"] = @"C:\clips !x!\a & b.mp4",
            ["YAAT_REC_FFMPEG_LOG"] = @"C:\clips !x!\a & b-ffmpeg.log",
        };
        Assert.Equal(expected, line.Environment);
        Assert.DoesNotContain("USERNAME", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("!x!", line.Text, StringComparison.Ordinal);
        Assert.Equal(@"C:\%USERNAME%\rec dir\Yaat.WindowRecorder.exe", line.RecorderExecutable);
    }

    // /v:off whatever the registry's DelayedExpansion says, so a ! in an expanded path is never read as a variable.
    [Fact]
    public void CommandLine_StartsCmdWithDelayedExpansionOff()
    {
        PipelineCommandLine line = FfmpegPipeline.CommandLine(OddPathsCommand);

        Assert.Equal($"/d /v:off /s /c \"{line.Text}\"", FfmpegPipeline.CmdArguments(line));
    }

    // A bare argument stays bare; one with a space or a cmd metacharacter is quoted, its trailing backslashes doubled.
    [Fact]
    public void Quote_ProtectsSpacesAndTrailingBackslashes()
    {
        Assert.Equal("4660", FfmpegPipeline.Quote("4660"));
        Assert.Equal("\"C:\\a dir\\\\\"", FfmpegPipeline.Quote(@"C:\a dir\"));
        Assert.Equal("\"\"", FfmpegPipeline.Quote(""));
    }

    // A stage whose path argument is none of its arguments would pass the path literally, or not at all: refused.
    [Fact]
    public void CommandLine_PathArgumentMatchesNoArgument_Throws()
    {
        PipelineCommand command = OddPathsCommand with { Ffmpeg = OddPathsCommand.Ffmpeg with { PathArgument = @"C:\elsewhere\b.mp4" } };

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => FfmpegPipeline.CommandLine(command));

        Assert.Contains(@"C:\elsewhere\b.mp4", thrown.Message, StringComparison.Ordinal);
    }

    // cmd would expand a % or ! in a literal argument; only a variable reference keeps one literal, so Quote refuses them.
    [Theory]
    [InlineData("50%")]
    [InlineData("%PATH%")]
    [InlineData("a!b")]
    public void Quote_PercentOrBang_Throws(string argument) => Assert.Throws<ArgumentException>(() => FfmpegPipeline.Quote(argument));

    private static PipelineCommand OddPathsCommand =>
        new(
            new PipelineStage(
                @"C:\%USERNAME%\rec dir\Yaat.WindowRecorder.exe",
                ["--hwnd", "4660", "--until-file", @"C:\clips !x!\a & b.mp4.deadline"],
                @"C:\clips !x!\a & b-recorder.log",
                @"C:\clips !x!\a & b.mp4.deadline"
            ),
            new PipelineStage(
                @"C:\ff mpeg\ffmpeg.exe",
                ["-i", "-", @"C:\clips !x!\a & b.mp4"],
                @"C:\clips !x!\a & b-ffmpeg.log",
                @"C:\clips !x!\a & b.mp4"
            )
        );

    // A build can list h264_nvenc and still fail it at run time (no NVIDIA GPU); only a probe that encodes keeps it.
    [Fact]
    public async Task Encoder_NvencProbeFails_FallsBackToLibx264()
    {
        const string listing =
            " V....D libx264              libx264 H.264 / AVC\n V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)\n";
        int probes = 0;

        Assert.Equal(FfmpegPipeline.Libx264, await FfmpegPipeline.ChooseEncoderAsync(listing, () => Probe(false)));
        Assert.Equal(FfmpegPipeline.Nvenc, await FfmpegPipeline.ChooseEncoderAsync(listing, () => Probe(true)));
        Assert.Equal(FfmpegPipeline.Libx264, await FfmpegPipeline.ChooseEncoderAsync(" V....D libx264  libx264 H.264\n", () => Probe(true)));
        Assert.Equal(2, probes);

        Task<bool> Probe(bool succeeds)
        {
            probes++;
            return Task.FromResult(succeeds);
        }
    }
}
