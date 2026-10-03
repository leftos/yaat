using System.Diagnostics;
using System.Globalization;
using Windows.Graphics.Capture;

namespace Yaat.WindowRecorder;

/// <summary>
/// Records one top-level window through Windows.Graphics.Capture and writes its frames as raw BGRA to stdout at a
/// constant rate, for ffmpeg to read with -f rawvideo; with --audio-pid, also the audio a process tree renders, as raw
/// s16le into a named pipe. With --audio-only, the audio alone. A run lasts a fixed number of seconds, until the
/// instant a file holds, which may be rewritten while it runs, or both: the file, moved to the run's start plus the seconds. Everything the tool says goes to stderr.
/// </summary>
internal static class Program
{
    private const int ExitUsage = 2;
    private const int ExitResized = 3;
    private const int ExitAudioActivation = 4;
    private const int ExitAudioUnread = 5;
    private const int ProcessLoopbackMinimumBuild = 20348;

    /// <summary>Below this share of the requested rate, the run says the reader fell behind.</summary>
    private const double KeptUpShare = 0.95;
    private static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds(5);

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            await Console.Error.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return 0;
        }
        var options = Options.Parse(args, out string? usageError);
        // One deadline for the whole run, so the video and the audio end at the same instant. A probe records nothing.
        Deadline? deadline = options is null || options.Probe ? null : options.OpenDeadline(out usageError);
        if (options is null || (deadline is null && !options.Probe))
        {
            await Console.Error.WriteLineAsync($"Yaat.WindowRecorder: {usageError}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return ExitUsage;
        }
        return options.AudioOnly
            ? await RecordAudioOnlyAsync(options, deadline!).ConfigureAwait(false)
            : await CaptureWindowAsync(options, deadline).ConfigureAwait(false);
    }

    /// <summary><paramref name="deadline"/> is null only for a probe.</summary>
    private static async Task<int> CaptureWindowAsync(Options options, Deadline? deadline)
    {
        nint hwnd = options.Hwnd ?? WindowFinder.FindByTitle(options.Title!);
        string? refusal = RefuseWindow(hwnd, options.Title);
        if (refusal is not null)
        {
            await Console.Error.WriteLineAsync($"Yaat.WindowRecorder: {refusal}").ConfigureAwait(false);
            return ExitUsage;
        }

        using WindowCapture capture = new(hwnd);
        // The audio pipe is created here, before the first video byte: ffmpeg opens its second input only after the first
        // has delivered data, so the pipe exists by the time it is opened. A probe with --audio-pid activates the audio
        // too, so an activation failure is refused before ffmpeg starts.
        using AudioTrack? audio = options.AudioPid is null
            ? null
            : await OpenAudioAsync(options.AudioPid.Value, options.AudioPipe).ConfigureAwait(false);
        if (options.AudioPid is not null && audio is null)
        {
            return ExitAudioActivation;
        }
        if (options.Probe)
        {
            Console.Out.WriteLine(FormattableString.Invariant($"{capture.OutputSize.Width}x{capture.OutputSize.Height}"));
            return 0;
        }
        try
        {
            return await RecordAsync(capture, options, deadline!, audio).ConfigureAwait(false);
        }
        catch (WindowResizedException resized)
        {
            await Console
                .Error.WriteLineAsync($"Yaat.WindowRecorder: {resized.Message}; the output size is fixed for a run, so it stopped.")
                .ConfigureAwait(false);
            return ExitResized;
        }
    }

    private static string? RefuseWindow(nint hwnd, string? title)
    {
        if (hwnd == nint.Zero)
        {
            return $"no top-level window is titled '{title}'.";
        }
        if (WindowFinder.IsMinimized(hwnd))
        {
            return "the window is minimized; a minimized window has no surface to capture. Restore it and rerun.";
        }
        return GraphicsCaptureSession.IsSupported() ? null : "Windows.Graphics.Capture is not supported on this machine.";
    }

    private static async Task<int> RecordAudioOnlyAsync(Options options, Deadline deadline)
    {
        using AudioTrack? audio = await OpenAudioAsync(options.AudioPid!.Value, options.AudioPipe).ConfigureAwait(false);
        if (audio is null)
        {
            return ExitAudioActivation;
        }
        // Alone, the clock starts when ffmpeg opens the pipe, so the audio begins with ffmpeg's other inputs.
        return await audio.RecordAsync(deadline, startOnConnect: true).ConfigureAwait(false) ? 0 : ExitAudioUnread;
    }

    /// <summary>Returns the activated track, or null after one line on stderr when process loopback cannot start.</summary>
    private static async Task<AudioTrack?> OpenAudioAsync(int processId, string? pipeName)
    {
        string? refusal = RefuseAudio(processId);
        if (refusal is not null)
        {
            await Console.Error.WriteLineAsync($"Yaat.WindowRecorder: {refusal}").ConfigureAwait(false);
            return null;
        }
        try
        {
            return AudioTrack.Open(processId, pipeName);
        }
        catch (AudioActivationException failure)
        {
            await Console
                .Error.WriteLineAsync($"Yaat.WindowRecorder: process-loopback audio for process {processId} could not start: {failure.Message}.")
                .ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Process loopback activates for any process id, a missing one included, and then records silence; a missing process
    /// is refused here instead, since a silent track for a mistyped id is not what was asked for.
    /// </summary>
    private static string? RefuseAudio(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, ProcessLoopbackMinimumBuild))
        {
            return $"process-loopback audio needs Windows build {ProcessLoopbackMinimumBuild} or later.";
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            return null;
        }
        catch (ArgumentException)
        {
            return $"no process has id {processId}, so there is no audio to record.";
        }
    }

    private static async Task<int> RecordAsync(WindowCapture capture, Options options, Deadline deadline, AudioTrack? audio)
    {
        int fps = options.Fps;
        await Console
            .Error.WriteLineAsync($"Yaat.WindowRecorder: {capture.OutputSize.Width}x{capture.OutputSize.Height} at {fps} fps {deadline.Describe()}")
            .ConfigureAwait(false);
        byte[] frame = new byte[capture.FrameBytes];
        capture.Start();
        if (!await WaitForFirstFrameAsync(capture, frame).ConfigureAwait(false))
        {
            await Console
                .Error.WriteLineAsync($"Yaat.WindowRecorder: no frame arrived in {FirstFrameWait.TotalSeconds} s; the window is not being composed.")
                .ConfigureAwait(false);
            return ExitUsage;
        }

        // The first frame is the run's start for both tracks; ffmpeg lines them up by sample and frame count from there.
        deadline.Start();
        Task<bool> audioRecording = audio?.RecordAsync(deadline, startOnConnect: false) ?? Task.FromResult(true);
        (int written, TimeSpan elapsed) = await WriteFramesAsync(capture, frame, deadline, fps).ConfigureAwait(false);
        await ReportFrameRateAsync(written, elapsed, fps).ConfigureAwait(false);
        return await audioRecording.ConfigureAwait(false) ? 0 : ExitAudioUnread;
    }

    /// <summary>
    /// Writes a frame on every timer tick until the deadline passes, and closes stdout before the caller waits for the
    /// audio: the EOF is what makes ffmpeg flush the frames its encoder still holds (h264_nvenc keeps a few) and end the
    /// video stream, so ffmpeg is never left waiting on a video input that has stopped sending while the audio drains.
    /// Returns the frames written and the time from the first of them to the end.
    /// </summary>
    private static async Task<(int Written, TimeSpan Elapsed)> WriteFramesAsync(WindowCapture capture, byte[] frame, Deadline deadline, int fps)
    {
        int written = 0;
        Stopwatch clock = new();
        await using (Stream stdout = Console.OpenStandardOutput())
        await using (BufferedStream output = new(stdout, 1 << 20))
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1.0 / fps));
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false) && !deadline.HasPassed())
            {
                clock.Start();
                capture.TryCopyLatestFrame(frame);
                await output.WriteAsync(frame).ConfigureAwait(false);
                written++;
            }
            clock.Stop();
            await output.FlushAsync().ConfigureAwait(false);
        }
        StandardOutput.Close();
        return (written, clock.Elapsed);
    }

    /// <summary>
    /// A write blocks while the reader is busy, and the timer skips the ticks it missed, so a slow reader means fewer frames
    /// rather than a late end; the reader stamps each frame with its arrival time, so the file still plays in real time.
    /// </summary>
    private static async Task ReportFrameRateAsync(int written, TimeSpan elapsed, int fps)
    {
        double seconds = elapsed.TotalSeconds;
        double rate = seconds > 0 ? written / seconds : 0;
        await Console
            .Error.WriteLineAsync(FormattableString.Invariant($"Yaat.WindowRecorder: {written} frames in {seconds:F1} s ({rate:F1} fps of {fps})"))
            .ConfigureAwait(false);
        if (seconds > 0 && rate < KeptUpShare * fps)
        {
            await Console
                .Error.WriteLineAsync(
                    "Yaat.WindowRecorder: the reader fell behind the frame rate; the recording still plays in real time because each frame carries its arrival time."
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>The pool delivers on its own cadence; the run starts on the first delivered frame, not on a blank one.</summary>
    private static async Task<bool> WaitForFirstFrameAsync(WindowCapture capture, byte[] frame)
    {
        DateTime deadline = DateTime.UtcNow + FirstFrameWait;
        while (DateTime.UtcNow < deadline)
        {
            if (capture.TryCopyLatestFrame(frame))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
        }
        return false;
    }

    private sealed record Options(
        string? Title,
        nint? Hwnd,
        int Fps,
        int Seconds,
        string? UntilFile,
        bool Probe,
        int? AudioPid,
        string? AudioPipe,
        bool AudioOnly
    )
    {
        public const string Usage =
            "usage: Yaat.WindowRecorder (--title <exact window title> | --hwnd <decimal>) [--fps 30] ([--seconds <n>] [--until-file <path>] | --probe)"
            + " [--audio-pid <pid> --audio-pipe <name>]\n"
            + "       Yaat.WindowRecorder --audio-only --audio-pid <pid> [--seconds <n>] [--until-file <path>] [--audio-pipe <name>]\n"
            + "       Yaat.WindowRecorder --help\n"
            + "A run needs --seconds, --until-file or both. --until-file names a file holding one UTC instant in round-trip (\"o\")\n"
            + "format; the run ends when it passes, and the file may be rewritten while the run goes on. With both, the file is\n"
            + "rewritten at the run's start (the first frame, or the reader connecting) to --seconds later, unless it changed\n"
            + "since the tool read it.";

        /// <summary>Returns null, with the reason in <paramref name="error"/>, when the deadline file cannot be read.</summary>
        public Deadline? OpenDeadline(out string? error)
        {
            if (UntilFile is null)
            {
                error = null;
                return Deadline.AfterSeconds(Seconds);
            }
            return Deadline.FromFile(UntilFile, Seconds, out error);
        }

        public static Options? Parse(string[] args, out string? error)
        {
            string? title = null;
            nint? hwnd = null;
            int fps = 30;
            int seconds = 0;
            string? untilFile = null;
            bool probe = false;
            int? audioPid = null;
            string? audioPipe = null;
            bool audioOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--title" when i + 1 < args.Length:
                        title = args[++i];
                        break;
                    case "--hwnd"
                        when i + 1 < args.Length && long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long handle):
                        hwnd = (nint)handle;
                        i++;
                        break;
                    case "--fps" when TryReadPositive(args, ref i, out int rate):
                        fps = rate;
                        break;
                    case "--seconds" when TryReadPositive(args, ref i, out int length):
                        seconds = length;
                        break;
                    case "--until-file" when i + 1 < args.Length:
                        untilFile = args[++i];
                        break;
                    case "--probe":
                        probe = true;
                        break;
                    case "--audio-pid" when TryReadPositive(args, ref i, out int pid):
                        audioPid = pid;
                        break;
                    case "--audio-pipe" when i + 1 < args.Length:
                        audioPipe = args[++i];
                        break;
                    case "--audio-only":
                        audioOnly = true;
                        break;
                    default:
                        error = $"unexpected or incomplete argument '{args[i]}'.";
                        return null;
                }
            }
            Options options = new(title, hwnd, fps, seconds, untilFile, probe, audioPid, audioPipe, audioOnly);
            error = audioOnly ? options.RefuseAudioOnly() : options.RefuseWindowRun();
            return error is null ? options : null;
        }

        /// <summary>Reads the next argument as a positive decimal, advancing past it; false leaves the index alone.</summary>
        private static bool TryReadPositive(string[] args, ref int index, out int value)
        {
            if (index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0)
            {
                index++;
                return true;
            }
            value = 0;
            return false;
        }

        private string? RefuseWindowRun()
        {
            if (Title is null && Hwnd is null)
            {
                return "one of --title or --hwnd is required.";
            }
            if (!Probe && !HasEnd)
            {
                return "--seconds, --until-file or both are required unless --probe is given.";
            }
            return RefuseAudioPairing();
        }

        private bool HasEnd => Seconds > 0 || UntilFile is not null;

        /// <summary>Beside a window, stdout carries the video, so the audio needs a pipe; a probe only activates it.</summary>
        private string? RefuseAudioPairing()
        {
            if (AudioPipe is not null && AudioPid is null)
            {
                return "--audio-pipe needs --audio-pid.";
            }
            return (AudioPid is not null && AudioPipe is null && !Probe) ? "--audio-pid beside a window needs --audio-pipe." : null;
        }

        private string? RefuseAudioOnly()
        {
            if (Title is not null || Hwnd is not null || Probe)
            {
                return "--audio-only records no window; drop --title, --hwnd and --probe.";
            }
            if (AudioPid is null)
            {
                return "--audio-only needs --audio-pid.";
            }
            return HasEnd ? null : "--audio-only needs --seconds, --until-file or both.";
        }
    }
}
