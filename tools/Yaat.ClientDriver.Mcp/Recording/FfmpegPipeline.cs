using System.Globalization;
using System.Text;

namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>The raw frames the recorder writes: their size and the rate it writes them at.</summary>
/// <param name="Width">The frame width the recorder's probe reported.</param>
/// <param name="Height">The frame height the recorder's probe reported.</param>
/// <param name="Fps">The frames per second the recorder writes.</param>
public sealed record FrameFormat(int Width, int Height, int Fps);

/// <summary>One recording's ffmpeg: its encoder, the frames it reads, the crop it keeps, its output and its audio pipe.</summary>
/// <param name="Encoder">The video encoder, <see cref="FfmpegPipeline.Nvenc"/> or <see cref="FfmpegPipeline.Libx264"/>.</param>
/// <param name="Frame">The raw frames on stdin.</param>
/// <param name="Crop">The client area kept of each frame.</param>
/// <param name="OutputPath">The MP4 to write.</param>
/// <param name="AudioPipeName">The recorder's PCM pipe, or null for a recording without audio.</param>
public sealed record FfmpegSpec(string Encoder, FrameFormat Frame, CropBox Crop, string OutputPath, string? AudioPipeName);

/// <summary>One side of the recording pipeline: an executable, its arguments, and the file its stderr goes to.</summary>
/// <param name="Executable">The full path of the executable.</param>
/// <param name="Arguments">Its arguments, unquoted.</param>
/// <param name="StandardErrorLog">The file its stderr is redirected to.</param>
/// <param name="PathArgument">
/// The one argument that is a path (the recorder's deadline file, ffmpeg's output), which reaches cmd through a variable.
/// </param>
public sealed record PipelineStage(string Executable, IReadOnlyList<string> Arguments, string StandardErrorLog, string PathArgument);

/// <summary>The recorder piped into ffmpeg.</summary>
/// <param name="Recorder">The window recorder, whose stdout carries the frames.</param>
/// <param name="Ffmpeg">The ffmpeg that reads them.</param>
public sealed record PipelineCommand(PipelineStage Recorder, PipelineStage Ffmpeg);

/// <summary>The line cmd runs, the variables its paths are passed in, and the recorder it starts.</summary>
/// <param name="Text">The command text, every path in it a quoted <c>%YAAT_REC_&lt;NAME&gt;%</c> reference.</param>
/// <param name="Environment">The path each referenced variable holds, set on the cmd process.</param>
/// <param name="RecorderExecutable">The recorder's exe, by which the stop tells the recorder from ffmpeg among cmd's children.</param>
public sealed record PipelineCommandLine(string Text, IReadOnlyDictionary<string, string> Environment, string RecorderExecutable);

/// <summary>
/// The recording pipeline as the video-capture skill's wgc route runs it: the window recorder writes raw BGRA frames to its
/// stdout and, with audio, s16le PCM to a named pipe, and one ffmpeg reads both. cmd joins the two with an OS pipe, so the
/// frames never pass through this process. The recorder closes stdout after its last frame, which ends ffmpeg's video input.
/// </summary>
public static class FfmpegPipeline
{
    /// <summary>The GPU encoder, used when the build lists it and a probe encode succeeds.</summary>
    public const string Nvenc = "h264_nvenc";

    /// <summary>The CPU encoder every other machine gets.</summary>
    public const string Libx264 = "libx264";

    /// <summary>What <c>record_start</c> says when ffmpeg is not on PATH.</summary>
    public const string MissingFfmpegMessage =
        "ffmpeg is not on PATH. Fix: winget install Gyan.FFmpeg, then restart Claude Code so the client-driver MCP server starts with the new PATH.";

    /// <summary>
    /// A tenth of a second of a generated picture through nvenc: listed is not enough, as a build lists it without a GPU. The
    /// picture is 256x256 because NVENC refuses a frame below its minimum size (64x64 fails "Frame Dimension less than the
    /// minimum supported value" on an RTX 4090), which would make every machine fall back to libx264.
    /// </summary>
    public static IReadOnlyList<string> NvencProbeArguments { get; } =
    ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=256x256", "-t", "0.1", "-c:v", Nvenc, "-f", "null", "-"];

    private static readonly char[] CharactersNeedingQuotes = [' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')'];

    private static readonly char[] CharactersCmdExpands = ['%', '!'];

    /// <summary>
    /// ffmpeg's arguments: rawvideo BGRA from stdin, stamped with arrival times and kept that way (<c>-fps_mode vfr</c>), so a
    /// recorder that sent fewer frames still plays in real time; the audio pipe as the second input; the client-area crop;
    /// and <c>-n</c>, so an existing output is never overwritten.
    /// </summary>
    /// <param name="spec">The recording's encoder, frames, crop, output and audio pipe.</param>
    public static IReadOnlyList<string> Arguments(FfmpegSpec spec)
    {
        List<string> arguments =
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
            Invariant($"{spec.Frame.Width}x{spec.Frame.Height}"),
            "-framerate",
            Invariant($"{spec.Frame.Fps}"),
            "-i",
            "-",
        ];
        if (spec.AudioPipeName is { } pipe)
        {
            arguments.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-i", $@"\\.\pipe\{pipe}"]);
        }

        arguments.AddRange(["-vf", spec.Crop.Filter, "-c:v", spec.Encoder]);
        if (!spec.Encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            // nvenc converts BGRA on the GPU; every other encoder needs the conversion named.
            arguments.AddRange(["-pix_fmt", "yuv420p"]);
        }

        arguments.AddRange(["-fps_mode", "vfr"]);
        if (spec.AudioPipeName is not null)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }

        arguments.AddRange(["-movflags", "+faststart", spec.OutputPath]);
        return arguments;
    }

    /// <summary>The recorder's <c>--probe</c> run: the frame size it will send, or its refusal of a missing or minimized window.</summary>
    /// <param name="hwnd">The window's handle.</param>
    /// <param name="audioPid">The process whose audio is recorded, so a failing activation is refused too; null for none.</param>
    public static IReadOnlyList<string> ProbeArguments(long hwnd, int? audioPid)
    {
        List<string> arguments = ["--probe", "--hwnd", Invariant($"{hwnd}")];
        if (audioPid is { } pid)
        {
            arguments.AddRange(["--audio-pid", Invariant($"{pid}")]);
        }

        return arguments;
    }

    /// <summary>The recorder's run: the window by handle, at <paramref name="fps"/>, until the deadline file's instant.</summary>
    /// <param name="hwnd">The window's handle.</param>
    /// <param name="fps">The frames per second to write.</param>
    /// <param name="deadlinePath">The file holding the instant the run ends.</param>
    /// <param name="audioPid">The process whose audio is recorded, or null for none.</param>
    /// <param name="audioPipe">The pipe the audio goes to; used only with <paramref name="audioPid"/>.</param>
    public static IReadOnlyList<string> RecorderArguments(long hwnd, int fps, string deadlinePath, int? audioPid, string? audioPipe)
    {
        List<string> arguments = ["--hwnd", Invariant($"{hwnd}"), "--fps", Invariant($"{fps}"), "--until-file", deadlinePath];
        if ((audioPid is { } pid) && (audioPipe is not null))
        {
            arguments.AddRange(["--audio-pid", Invariant($"{pid}"), "--audio-pipe", audioPipe]);
        }

        return arguments;
    }

    /// <summary>
    /// The line cmd runs: <c>recorder … 2&gt;log | ffmpeg … 2&gt;log</c>. cmd expands <c>%NAME%</c> in it even inside quotes,
    /// but never rescans what it expanded, so every path (both executables, both logs, the deadline file and the MP4) goes in
    /// as a quoted <c>"%YAAT_REC_&lt;NAME&gt;%"</c> whose value is set on the cmd process: a <c>%</c>, <c>!</c> or <c>&amp;</c>
    /// in a folder name stays literal. Every other argument is quoted where it needs it.
    /// </summary>
    /// <param name="command">The recorder and ffmpeg stages.</param>
    public static PipelineCommandLine CommandLine(PipelineCommand command)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        string recorder = Stage(command.Recorder, "RECORDER", "UNTIL_FILE", environment);
        string ffmpeg = Stage(command.Ffmpeg, "FFMPEG", "OUT", environment);
        return new PipelineCommandLine($"{recorder} | {ffmpeg}", environment, command.Recorder.Executable);
    }

    /// <summary>
    /// cmd's own arguments for <paramref name="commandLine"/>: no AutoRun (<c>/d</c>), delayed expansion off whatever the
    /// registry says (<c>/v:off</c>, so a <c>!</c> in an expanded path is literal), and <c>/s</c>, which strips only the
    /// outer quotes.
    /// </summary>
    /// <param name="commandLine">The pipeline's command line.</param>
    public static string CmdArguments(PipelineCommandLine commandLine) => $"/d /v:off /s /c \"{commandLine.Text}\"";

    /// <summary>
    /// One argument as the C runtime parses it back, also safe from cmd's metacharacters: bare when it has nothing to protect,
    /// otherwise quoted, with the backslashes before a quote (or the closing quote) doubled.
    /// </summary>
    /// <param name="argument">The argument to quote.</param>
    /// <exception cref="ArgumentException">
    /// The argument holds a <c>%</c> or <c>!</c>, which cmd would expand; a path goes in through a variable instead.
    /// </exception>
    public static string Quote(string argument)
    {
        if (argument.IndexOfAny(CharactersCmdExpands) >= 0)
        {
            throw new ArgumentException(
                $"'{argument}' holds a % or !, which cmd would expand; pass it as the stage's PathArgument, through a variable.",
                nameof(argument)
            );
        }

        if ((argument.Length > 0) && (argument.IndexOfAny(CharactersNeedingQuotes) < 0))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            int escapes = (character == '"') ? (backslashes * 2) + 1 : backslashes;
            quoted.Append('\\', escapes).Append(character);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// <see cref="Nvenc"/> when <paramref name="encodersListing"/> (<c>ffmpeg -encoders</c>) lists it and
    /// <paramref name="nvencProbe"/> encodes with it, else <see cref="Libx264"/>; the probe runs only for a listed nvenc.
    /// </summary>
    /// <param name="encodersListing">The output of <c>ffmpeg -encoders</c>.</param>
    /// <param name="nvencProbe">Runs a short nvenc encode and says whether it succeeded.</param>
    public static async Task<string> ChooseEncoderAsync(string encodersListing, Func<Task<bool>> nvencProbe)
    {
        if (!ListsEncoder(encodersListing, Nvenc))
        {
            return Libx264;
        }

        return await nvencProbe().ConfigureAwait(false) ? Nvenc : Libx264;
    }

    /// <summary>True when a line of the listing names <paramref name="name"/> in its second column, after the capability flags.</summary>
    private static bool ListsEncoder(string listing, string name) =>
        listing
            .Split('\n')
            .Any(line =>
                line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [_, var encoder, ..]
                && string.Equals(encoder, name, StringComparison.Ordinal)
            );

    /// <summary>One stage's text, its executable, log and path argument put in <paramref name="environment"/> under its names.</summary>
    /// <exception cref="ArgumentException">The stage's path argument is none of its arguments.</exception>
    private static string Stage(PipelineStage stage, string name, string pathName, Dictionary<string, string> environment)
    {
        if (!stage.Arguments.Contains(stage.PathArgument, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The {name.ToLowerInvariant()} stage's path argument '{stage.PathArgument}' is none of its arguments.",
                nameof(stage)
            );
        }

        string executable = Reference(name, stage.Executable, environment);
        string log = Reference($"{name}_LOG", stage.StandardErrorLog, environment);
        IEnumerable<string> arguments = stage.Arguments.Select(argument =>
            string.Equals(argument, stage.PathArgument, StringComparison.Ordinal) ? Reference(pathName, argument, environment) : Quote(argument)
        );
        return string.Join(' ', [executable, .. arguments, $"2>{log}"]);
    }

    private static string Reference(string name, string path, Dictionary<string, string> environment)
    {
        string variable = $"YAAT_REC_{name}";
        environment[variable] = path;
        return $"\"%{variable}%\"";
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}
