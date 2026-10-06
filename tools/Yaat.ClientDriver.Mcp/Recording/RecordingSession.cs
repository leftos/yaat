using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>The window a recording captures and the process it belongs to.</summary>
/// <param name="Pid">The process that owns the window, whose audio is recorded when audio is on.</param>
/// <param name="Hwnd">The window's native handle.</param>
/// <param name="HasPipe">True for a YAAT client with an automation pipe, which answers <c>get_sim_time</c>.</param>
public sealed record RecordingTarget(int Pid, long Hwnd, bool HasPipe);

/// <summary>What <c>record_start</c> asks for, once its target is resolved.</summary>
/// <param name="Target">The window to record.</param>
/// <param name="Ffmpeg">The ffmpeg found on PATH.</param>
/// <param name="Out">The MP4 to write, or empty for the default under <c>.tmp/client-driver/recordings/</c>.</param>
/// <param name="Fps">The frames per second the recorder writes.</param>
/// <param name="Audio">True to record the process's own audio beside the video.</param>
public sealed record RecordingRequest(RecordingTarget Target, string Ffmpeg, string Out, int Fps, bool Audio);

/// <summary>The files of one recording, all beside its MP4.</summary>
/// <param name="Mp4">The clip.</param>
/// <param name="Marks">The marks file, <c>&lt;clip&gt;-marks.json</c>.</param>
/// <param name="RecorderLog">The recorder's stderr, <c>&lt;clip&gt;-recorder.log</c>.</param>
/// <param name="FfmpegLog">ffmpeg's stderr, <c>&lt;clip&gt;-ffmpeg.log</c>.</param>
/// <param name="Deadline">The file holding the instant the recorder stops, <c>&lt;mp4&gt;.deadline</c>.</param>
/// <param name="Actions">The input actions logged while it records, <c>&lt;clip&gt;-actions.jsonl</c> (see <see cref="ActionLog"/>).</param>
public sealed record RecordingPaths(string Mp4, string Marks, string RecorderLog, string FfmpegLog, string Deadline, string Actions)
{
    /// <summary>The files of the recording into <paramref name="mp4"/>.</summary>
    /// <param name="mp4">The clip's full path.</param>
    public static RecordingPaths For(string mp4)
    {
        string clip = Path.Combine(Path.GetDirectoryName(mp4) ?? string.Empty, Path.GetFileNameWithoutExtension(mp4));
        return new RecordingPaths(
            mp4,
            clip + "-marks.json",
            clip + "-recorder.log",
            clip + "-ffmpeg.log",
            mp4 + ".deadline",
            clip + "-actions.jsonl"
        );
    }
}

/// <summary>How a recording is encoded.</summary>
/// <param name="Encoder">The video encoder.</param>
/// <param name="Audio">True when the process's audio is recorded too.</param>
public sealed record RecordingEncoding(string Encoder, bool Audio);

/// <summary>A recording that started.</summary>
/// <param name="Target">The window recorded.</param>
/// <param name="Paths">Its files.</param>
/// <param name="Frame">The recorder's frames.</param>
/// <param name="Crop">The client area kept of each frame.</param>
/// <param name="Encoding">Its encoder and whether audio is on.</param>
public sealed record RecordingStarted(RecordingTarget Target, RecordingPaths Paths, FrameFormat Frame, CropBox Crop, RecordingEncoding Encoding);

/// <summary>One mark in the marks file.</summary>
/// <param name="Label">The caller's label.</param>
/// <param name="WallUtc">When the mark was made.</param>
/// <param name="ClipSeconds">Seconds into the clip: the wall time less the first frame's; null before the first frame.</param>
/// <param name="SimSeconds">The client's scenario clock; null for a process without a pipe, or when the client did not answer.</param>
public sealed record RecordingMark(string Label, DateTime WallUtc, double? ClipSeconds, double? SimSeconds);

/// <summary>A mark that was written.</summary>
/// <param name="Count">How many marks the recording now has.</param>
/// <param name="Mark">The mark.</param>
/// <param name="MarksPath">The marks file it was written to.</param>
public sealed record RecordingMarked(int Count, RecordingMark Mark, string MarksPath);

/// <summary>How a recording ended.</summary>
/// <param name="Paths">Its files.</param>
/// <param name="Mp4Exists">True when the MP4 is on disk.</param>
/// <param name="End">The recorder's end line, or null when it never wrote one.</param>
/// <param name="DroppedFrames">fps x elapsed - written, from the end line; null without one.</param>
/// <param name="Problem">What went wrong, or null for a clean stop.</param>
/// <param name="Note">
/// How it ended when that is no problem but worth saying: <see cref="RecordingSession.EndedOnItsOwnNote"/> for a pipeline
/// that exited cleanly before the stop; null otherwise.
/// </param>
public sealed record RecordingStopResult(RecordingPaths Paths, bool Mp4Exists, RecorderEnd? End, int? DroppedFrames, string? Problem, string? Note)
{
    /// <summary><c>&lt;mp4&gt;, &lt;seconds&gt; s, &lt;frames&gt; frames</c>, with <c>unknown</c> for what the recorder never said.</summary>
    public string Summary
    {
        get
        {
            string seconds = End?.Seconds.ToString("F1", CultureInfo.InvariantCulture) ?? "unknown";
            string frames = End?.Frames.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            return $"{Paths.Mp4}, {seconds} s, {frames} frames";
        }
    }
}

/// <summary>
/// The one recording this server runs at a time, shared by <c>record_start</c>, <c>record_mark</c>, <c>record_stop</c> and
/// <c>wait_until</c>'s <c>stop_recording</c>; a singleton, because tool classes are built per call. It holds the pipeline's
/// process, notices on every mark, stop and start when it died, and rewrites the marks file on every mark so it survives a
/// crash. A stop moves the deadline to now and waits for the recorder, then for ffmpeg; so does the server's own shutdown.
/// </summary>
/// <param name="backend">Runs the processes and reads the windows.</param>
/// <param name="clock">The wall clock marks are stamped with, and the clock of the check that a new pipeline still runs.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
public sealed class RecordingSession(IRecordingBackend backend, TimeProvider clock, ILogger<RecordingSession> logger) : IAsyncDisposable, IDisposable
{
    /// <summary>What a mark or a stop says when nothing is recording.</summary>
    public const string NothingRecordingMessage = "Nothing is recording; call record_start first.";

    /// <summary>The label of the mark <c>wait_until</c>'s <c>stop_recording</c> adds at the match.</summary>
    public const string MatchMarkLabel = "wait_until met";

    /// <summary>What a stop says of a pipeline that exited cleanly before it: its one-hour deadline passed, or the window changed.</summary>
    public const string EndedOnItsOwnNote = "the recording ended on its own (the one-hour cap or a window change) before the stop";

    private const string DefaultDirectory = ".tmp/client-driver/recordings";
    private const int LogTailLines = 5;

    /// <summary>How long the recorder has to stop once its deadline is now; past it, the recorder alone is killed.</summary>
    private static readonly TimeSpan RecorderStopWait = TimeSpan.FromSeconds(15);

    /// <summary>How long ffmpeg has after the recorder to drain, flush and rewrite the MP4 for +faststart; past it, the pipeline is killed.</summary>
    private static readonly TimeSpan EncodeWait = TimeSpan.FromSeconds(120);

    /// <summary>How long a killed pipeline has to be gone.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>How long after its start the pipeline is checked, so one that dies at once is reported by <c>record_start</c>.</summary>
    private static readonly TimeSpan StartCheckDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The deadline a recording starts with, so a recorder this server lost track of still ends.</summary>
    private static readonly TimeSpan LongestRecording = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions MarksJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
    };

    /// <summary>Serializes every change to the recording; never disposed, so a call still queued on it at shutdown gets it.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ActiveRecording? _active;

    /// <summary>The last recording started, kept after it ends so an action stamped while it ran is still logged to it.</summary>
    private ActiveRecording? _lastRecording;

    /// <summary>Set once the server is stopping; a start after it is refused, so no pipeline outlives the server.</summary>
    private bool _disposed;

    /// <summary>True while a recording is held: running, or ended on its own and not yet noticed by a mark, stop or start.</summary>
    public bool IsRecording => Volatile.Read(ref _active) is not null;

    /// <summary>The MP4 of the recording held, or null when nothing is recording.</summary>
    public string? RecordingMp4 => Volatile.Read(ref _active)?.Started.Paths.Mp4;

    /// <summary>An identity of the recording held, unique to it even when a later one writes the same MP4; null when nothing is recording.</summary>
    public Guid? RecordingId => Volatile.Read(ref _active)?.Id;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The session's wall clock, which stamps an input action at its start for <see cref="LogActionAsync"/>.</summary>
    public DateTime UtcNow => Now;

    /// <summary>
    /// Appends <paramref name="action"/> to the action log of the last recording started when that recording is of the action's
    /// process and the action was stamped while it ran, from its launch to its stop; does nothing otherwise. The first action
    /// logged writes the header, with the first frame's instant when the recorder has written it and the action's window scale.
    /// It never waits on the gate a stop holds through the encode: the actions file has its recording's own lock. Returns null,
    /// or what went wrong reading the recorder log or writing the file: the action already happened, so a failed write is
    /// reported beside its result rather than failing it.
    /// </summary>
    public Task<string?> LogActionAsync(RecordedAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ActiveRecording? recording = Volatile.Read(ref _lastRecording);
        bool logged =
            (recording is not null)
            && (recording.Started.Target.Pid == action.Pid)
            && (action.WallUtc >= recording.LaunchedUtc)
            && ((recording.EndedUtc is not { } ended) || (action.WallUtc <= ended));
        return Task.FromResult(logged ? WriteAction(recording!, action) : null);
    }

    private string? WriteAction(ActiveRecording active, RecordedAction action)
    {
        string path = active.Started.Paths.Actions;
        lock (active.Sync)
        {
            try
            {
                active.StartedUtc ??= RecorderLog.FirstFrameUtc(RecorderLog.Read(active.Started.Paths.RecorderLog));
                if (!active.ActionsStarted)
                {
                    ActionLog.WriteHeader(path, active.StartedUtc, active.Started.Frame.Fps, active.Started.Crop, action.Site.RenderScaling);
                    active.ActionsStarted = true;
                    active.ActionsHeaderHasStart = active.StartedUtc is not null;
                }

                double? clipSeconds = (active.StartedUtc is { } first) ? ActionLog.ClipSeconds(action.WallUtc, first) : null;
                ActionLog.Append(path, action, clipSeconds);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not log a {Kind} action to {Actions}", action.Kind, path);
                return $"could not write the action log {path}: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Writes the first frame's instant into an action log whose header went out before it was known, and fills the clip
    /// seconds of the lines written before it; a failure is logged, since the recording has ended either way.
    /// </summary>
    private void FinishActions(ActiveRecording active)
    {
        lock (active.Sync)
        {
            if (!active.ActionsStarted || active.ActionsHeaderHasStart || (active.StartedUtc is not { } first))
            {
                return;
            }

            try
            {
                ActionLog.WriteStart(active.Started.Paths.Actions, first);
                active.ActionsHeaderHasStart = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                logger.LogWarning(ex, "Could not write the first frame's instant into the action log {Actions}", active.Started.Paths.Actions);
            }
        }
    }

    /// <summary>The ffmpeg a new recording will use.</summary>
    /// <exception cref="McpException">A recording is already running, or ffmpeg is not on PATH.</exception>
    public async Task<string> RequireFfmpegAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EndDeadOrThrowIfRecording();
        }
        finally
        {
            _gate.Release();
        }

        return backend.FindFfmpeg() ?? throw new McpException(FfmpegPipeline.MissingFfmpegMessage);
    }

    /// <summary>Probes the window and starts the recorder piped into ffmpeg.</summary>
    /// <exception cref="McpException">
    /// A recording is running, the window is gone or minimized, the output exists, the marks file cannot be written, the
    /// recorder refused the window, or the pipeline died at once.
    /// </exception>
    public async Task<RecordingStarted> StartAsync(RecordingRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                throw new McpException("The client-driver server is stopping; record_start is refused.");
            }

            EndDeadOrThrowIfRecording();
            RecordingTarget target = request.Target;
            WindowGeometry window = ReadRecordableWindow(target);
            var paths = RecordingPaths.For(ResolveOut(request));
            string encoder = await backend.ChooseEncoderAsync(request.Ffmpeg, ct).ConfigureAwait(false);
            string recorder = backend.FindRecorder();
            int? audioPid = request.Audio ? target.Pid : null;
            RecorderProbe probe = await backend.ProbeAsync(recorder, FfmpegPipeline.ProbeArguments(target.Hwnd, audioPid), ct).ConfigureAwait(false);
            if (probe.Refusal is { } refusal)
            {
                throw new McpException($"The window recorder refused pid {target.Pid}'s window 0x{target.Hwnd:X}: {refusal}");
            }

            var frame = new FrameFormat(probe.Width, probe.Height, request.Fps);
            var started = new RecordingStarted(
                target,
                paths,
                frame,
                FittedCrop(window, frame, target),
                new RecordingEncoding(encoder, request.Audio)
            );
            _active = await LaunchAsync(started, recorder, request.Ffmpeg, ct).ConfigureAwait(false);
            Volatile.Write(ref _lastRecording, _active);
            return started;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Appends a mark stamped at the call, with the client's scenario clock from <paramref name="simSeconds"/> for a process
    /// with a pipe. The clock is read before the gate is taken, so a client slow to answer never holds up a stop; a stop that
    /// ran meanwhile leaves the mark to the recording it was made in.
    /// </summary>
    /// <exception cref="McpException">Nothing is recording, or the pipeline died, which ends the recording.</exception>
    public async Task<RecordingMarked> MarkAsync(string label, Func<int, CancellationToken, Task<double?>> simSeconds, CancellationToken ct)
    {
        ActiveRecording called = Volatile.Read(ref _active) ?? throw new McpException(NothingRecordingMessage);
        DateTime wallUtc = Now;
        RecordingTarget target = called.Started.Target;
        double? sim = target.HasPipe ? await simSeconds(target.Pid, ct).ConfigureAwait(false) : null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ActiveRecording active = ReferenceEquals(_active, called) ? RequireAlive() : called;
            return AddMark(active, label, wallUtc, sim);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the recording and waits for the encode to finish; the caller's cancel stops only the wait for the gate.</summary>
    /// <exception cref="McpException">Nothing is recording.</exception>
    public async Task<RecordingStopResult> StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ActiveRecording active = _active ?? throw new McpException(NothingRecordingMessage);
            return await StopCoreAsync(active).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>wait_until</c>'s <c>stop_recording</c>: a final <see cref="MatchMarkLabel"/> mark at the match's own scenario time,
    /// then the stop, of the recording <paramref name="recordingId"/> names only. Null when that recording is no longer the
    /// one held, even when a new one writes to the same MP4.
    /// </summary>
    /// <param name="recordingId">The <see cref="RecordingId"/> of the recording that was running when the wait began.</param>
    /// <param name="simSeconds">The match's scenario time.</param>
    /// <param name="ct">Cancels the wait for the gate.</param>
    public async Task<RecordingStopResult?> StopAtMatchAsync(Guid recordingId, double simSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((_active is not { } active) || (active.Id != recordingId))
            {
                return null;
            }

            if (!active.Process.HasExited)
            {
                AddMark(active, MatchMarkLabel, Now, simSeconds);
            }

            return await StopCoreAsync(active).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends a running recording the way a stop does, with the same budgets, so the server's shutdown leaves a finished MP4
    /// rather than a pipeline running on to its one-hour cap.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_active is { } active)
            {
                await EndAtShutdownAsync(active).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Blocks on <see cref="DisposeAsync"/>, for a container disposed synchronously.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// The stop's end at shutdown. A deadline that cannot be written kills the recorder instead, and ffmpeg still gets its
    /// budget to finish the MP4; nothing here throws, since the server is stopping either way.
    /// </summary>
    private async Task EndAtShutdownAsync(ActiveRecording active)
    {
        string mp4 = active.Started.Paths.Mp4;
        active.EndedUtc ??= Now;
        try
        {
            PipelineEnd ended;
            try
            {
                ended = await EndPipelineAsync(active).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "The server is stopping but could not move the deadline of {Mp4}; its recorder is killed", mp4);
                active.Process.KillRecorder();
                ended = await WaitForEncodeAsync(active, null).ConfigureAwait(false);
            }

            if (ended.Problem is { } problem)
            {
                logger.LogWarning("The server is stopping; the recording into {Mp4}: {Problem}", mp4, problem);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "The server is stopping and could not end the recording into {Mp4} cleanly", mp4);
        }
        finally
        {
            End(active);
        }
    }

    /// <summary>Refuses while a recording runs; one whose pipeline ended on its own (the one-hour cap, a window change) is ended here.</summary>
    private void EndDeadOrThrowIfRecording()
    {
        if (_active is not { } active)
        {
            return;
        }

        RecordingStarted started = active.Started;
        if (!active.Process.HasExited)
        {
            throw new McpException(
                $"A recording is already running: {started.Paths.Mp4} (pid {started.Target.Pid}); " + "call record_stop before starting another."
            );
        }

        active.EndedUtc ??= Now;
        int code = active.Process.ExitCode;
        logger.Log(
            (code == 0) ? LogLevel.Information : LogLevel.Warning,
            "The recording into {Mp4} ended on its own with code {Code}; it is closed before the next start",
            started.Paths.Mp4,
            code
        );
        End(active);
        try
        {
            FinishMarks(active, RecorderLog.Read(started.Paths.RecorderLog));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not finish the marks file {Marks} of the recording that ended on its own", started.Paths.Marks);
        }

        FinishActions(active);
    }

    /// <summary>Writes the first frame's instant into the marks file, as a stop does, when no mark has read it yet.</summary>
    private static void FinishMarks(ActiveRecording active, IReadOnlyList<string> recorderLog)
    {
        if (active.StartedUtc is null)
        {
            active.StartedUtc = RecorderLog.FirstFrameUtc(recorderLog);
            WriteMarks(active);
        }
    }

    private WindowGeometry ReadRecordableWindow(RecordingTarget target)
    {
        string window = $"Pid {target.Pid}'s window 0x{target.Hwnd:X}";
        WindowGeometry geometry =
            backend.ReadWindow(target.Hwnd) ?? throw new McpException($"{window} is gone; call list_windows({target.Pid}) again.");
        if (geometry.IsMinimized)
        {
            throw new McpException($"{window} is minimized; a minimized window has no surface to capture. Restore it and call record_start again.");
        }

        return geometry;
    }

    private string ResolveOut(RecordingRequest request)
    {
        string name = string.Create(CultureInfo.InvariantCulture, $"{request.Target.Pid}-{Now:yyyyMMdd-HHmmss}.mp4");
        string path = Path.GetFullPath((request.Out.Length > 0) ? request.Out : Path.Combine(DefaultDirectory, name));
        if (File.Exists(path))
        {
            throw new McpException($"{path} already exists; record_start never overwrites a file. Pass another out, or move that file away.");
        }

        return path;
    }

    /// <summary>The client-area crop, refused when it does not fit the frame the recorder will send.</summary>
    private static CropBox FittedCrop(WindowGeometry window, FrameFormat frame, RecordingTarget target)
    {
        CropBox crop = ClientAreaCrop.Compute(window);
        bool fits = (crop.Width > 0) && (crop.Height > 0) && (crop.X + crop.Width <= frame.Width) && (crop.Y + crop.Height <= frame.Height);
        if (!fits)
        {
            throw new McpException(
                $"Pid {target.Pid}'s window 0x{target.Hwnd:X}: its client area ({crop.Width}x{crop.Height} at {crop.X},{crop.Y}) does not fit "
                    + $"the recorder's {frame.Width}x{frame.Height} frame. Resize or restore the window and call record_start again."
            );
        }

        return crop;
    }

    private static PipelineCommand Command(RecordingStarted started, string recorder, string ffmpeg)
    {
        RecordingPaths paths = started.Paths;
        bool audio = started.Encoding.Audio;
        string? audioPipe = audio ? $"yaat-rec-{Guid.NewGuid():N}" : null;
        int? audioPid = audio ? started.Target.Pid : null;
        IReadOnlyList<string> recorderArguments = FfmpegPipeline.RecorderArguments(
            started.Target.Hwnd,
            started.Frame.Fps,
            paths.Deadline,
            audioPid,
            audioPipe
        );
        var spec = new FfmpegSpec(started.Encoding.Encoder, started.Frame, started.Crop, paths.Mp4, audioPipe);
        return new PipelineCommand(
            new PipelineStage(recorder, recorderArguments, paths.RecorderLog, paths.Deadline),
            new PipelineStage(ffmpeg, FfmpegPipeline.Arguments(spec), paths.FfmpegLog, paths.Mp4)
        );
    }

    /// <summary>
    /// Writes the empty marks file and the one-hour deadline, starts the pipeline, and checks it a moment later: one that
    /// died at once is reported with its exit code and log tail, and a start cancelled in that moment kills what it started.
    /// </summary>
    private async Task<ActiveRecording> LaunchAsync(RecordingStarted started, string recorder, string ffmpeg, CancellationToken ct)
    {
        RecordingPaths paths = started.Paths;
        PipelineCommandLine commandLine = FfmpegPipeline.CommandLine(Command(started, recorder, ffmpeg));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Mp4)!);
        // A log left by an earlier run into the same name would hand this one its first-frame time.
        File.Delete(paths.RecorderLog);
        WriteFirstMarks(started);
        backend.WriteDeadline(paths.Deadline, Now + LongestRecording);
        string environment = string.Join(", ", commandLine.Environment.Select(variable => $"{variable.Key}={variable.Value}"));
        logger.LogInformation("record_start: {CommandLine} with {Environment}", commandLine.Text, environment);
        var active = new ActiveRecording(started, backend.Start(commandLine), Now);
        try
        {
            await Task.Delay(StartCheckDelay, clock, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillTreeAsync(active).ConfigureAwait(false);
            if (active.Process.HasExited)
            {
                End(active);
            }
            else
            {
                // A kill that has not taken yet leaves the pipeline held, so its handle is kept and a later start or stop reaps it.
                _active = active;
            }

            throw;
        }

        if (active.Process.HasExited)
        {
            string died = DiedMessage(active);
            End(active);
            throw new McpException(died);
        }

        return active;
    }

    /// <summary>The running recording, or throws: nothing recording, or a pipeline that died, which ends the recording.</summary>
    private ActiveRecording RequireAlive()
    {
        ActiveRecording active = _active ?? throw new McpException(NothingRecordingMessage);
        if (!active.Process.HasExited)
        {
            return active;
        }

        string died = DiedMessage(active);
        End(active);
        throw new McpException(died);
    }

    private RecordingMarked AddMark(ActiveRecording active, string label, DateTime wallUtc, double? simSeconds)
    {
        active.StartedUtc ??= RecorderLog.FirstFrameUtc(RecorderLog.Read(active.Started.Paths.RecorderLog));
        double? clipSeconds = (active.StartedUtc is { } first) ? Math.Round((wallUtc - first).TotalSeconds, 3) : null;
        var mark = new RecordingMark(label, wallUtc, clipSeconds, simSeconds);
        active.Marks.Add(mark);
        WriteMarks(active);
        return new RecordingMarked(active.Marks.Count, mark, active.Started.Paths.Marks);
    }

    private async Task<RecordingStopResult> StopCoreAsync(ActiveRecording active)
    {
        RecordingPaths paths = active.Started.Paths;
        active.EndedUtc ??= Now;
        PipelineEnd ended;
        try
        {
            ended = await EndPipelineAsync(active).ConfigureAwait(false);
        }
        finally
        {
            End(active);
        }

        IReadOnlyList<string> log = RecorderLog.Read(paths.RecorderLog);
        FinishMarks(active, log);
        FinishActions(active);
        RecorderEnd? end = RecorderLog.End(log);
        int? dropped = (end is null) ? null : Math.Max(0, (int)Math.Round(active.Started.Frame.Fps * end.Seconds) - end.Frames);
        return new RecordingStopResult(paths, File.Exists(paths.Mp4), end, dropped, ended.Problem, ended.Note);
    }

    /// <summary>
    /// Moves the deadline to now, gives the recorder <see cref="RecorderStopWait"/> to stop (killing it alone past that, which
    /// ends ffmpeg's input) and ffmpeg <see cref="EncodeWait"/> more to finish the MP4 (killing the pipeline past that). The
    /// waits ignore any caller's cancel: they are capped, and a pipeline left mid-stop would run on to its one-hour cap.
    /// </summary>
    private async Task<PipelineEnd> EndPipelineAsync(ActiveRecording active)
    {
        IRecordingProcess process = active.Process;
        if (process.HasExited)
        {
            return (process.ExitCode == 0) ? new PipelineEnd(null, EndedOnItsOwnNote) : new PipelineEnd(DiedMessage(active), null);
        }

        RecordingPaths paths = active.Started.Paths;
        backend.WriteDeadline(paths.Deadline, Now);
        string? recorderProblem = null;
        if (!await process.WaitForRecorderExitAsync(RecorderStopWait, CancellationToken.None).ConfigureAwait(false))
        {
            process.KillRecorder();
            recorderProblem =
                $"the recorder did not stop within {RecorderStopWait.TotalSeconds:0} s of the stop and was killed, "
                + $"so the clip runs up to {RecorderStopWait.TotalSeconds:0} s past the stop";
        }

        return await WaitForEncodeAsync(active, recorderProblem).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives ffmpeg <see cref="EncodeWait"/> to finish the MP4 once the recorder has ended, killing the pipeline past that;
    /// <paramref name="recorderProblem"/> is what already went wrong with the recorder, or null.
    /// </summary>
    private async Task<PipelineEnd> WaitForEncodeAsync(ActiveRecording active, string? recorderProblem)
    {
        IRecordingProcess process = active.Process;
        RecordingPaths paths = active.Started.Paths;
        if (!await process.WaitForExitAsync(EncodeWait, CancellationToken.None).ConfigureAwait(false))
        {
            await KillTreeAsync(active).ConfigureAwait(false);
            return new PipelineEnd(
                $"ffmpeg did not finish within {EncodeWait.TotalSeconds:0} s of the recorder's end, so the pipeline was killed; "
                    + $"{paths.Mp4} may be unplayable",
                null
            );
        }

        int code = process.ExitCode;
        string? ffmpegProblem =
            (code == 0) ? null : $"ffmpeg exited with code {code} (see {paths.FfmpegLog}); last recorder log lines: {LogTail(paths)}";
        string? problem = (recorderProblem, ffmpegProblem) switch
        {
            (null, _) => ffmpegProblem,
            (_, null) => recorderProblem,
            _ => $"{recorderProblem}; {ffmpegProblem}",
        };
        return new PipelineEnd(problem, null);
    }

    private async Task KillTreeAsync(ActiveRecording active)
    {
        active.Process.KillTree();
        if (!await active.Process.WaitForExitAsync(KillWait, CancellationToken.None).ConfigureAwait(false))
        {
            logger.LogWarning(
                "The recording pipeline for {Mp4} was killed but had not exited {Seconds} s later",
                active.Started.Paths.Mp4,
                KillWait.TotalSeconds
            );
        }
    }

    private static string DiedMessage(ActiveRecording active)
    {
        RecordingPaths paths = active.Started.Paths;
        string mp4 = File.Exists(paths.Mp4) ? $" An MP4 exists at {paths.Mp4}." : string.Empty;
        return $"The recording pipeline for {paths.Mp4} exited on its own with code {active.Process.ExitCode}; the recording has ended."
            + $" Last recorder log lines: {LogTail(paths)}.{mp4}";
    }

    private static string LogTail(RecordingPaths paths)
    {
        IReadOnlyList<string> tail = RecorderLog.Tail(RecorderLog.Read(paths.RecorderLog), LogTailLines);
        return (tail.Count == 0) ? "(the recorder log is empty)" : string.Join(" | ", tail);
    }

    /// <summary>
    /// Forgets the recording once its pipeline has exited: releases its process handle and deletes its deadline file. A
    /// pipeline still running keeps both and stays the recording held, so its deadline still ends it and a later call finds it.
    /// </summary>
    private void End(ActiveRecording active)
    {
        RecordingPaths paths = active.Started.Paths;
        if (!active.Process.HasExited)
        {
            logger.LogWarning(
                "The recording pipeline for {Mp4} is still running; it stays held, with its deadline file {Deadline}",
                paths.Mp4,
                paths.Deadline
            );
            return;
        }

        if (ReferenceEquals(_active, active))
        {
            _active = null;
        }

        active.Process.Dispose();
        try
        {
            File.Delete(paths.Deadline);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete the deadline file {Deadline}", paths.Deadline);
        }
    }

    /// <summary>
    /// The marks file a recording starts with, written before its pipeline starts so a folder it cannot be written in starts
    /// nothing.
    /// </summary>
    private static void WriteFirstMarks(RecordingStarted started)
    {
        try
        {
            WriteMarks(started, null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException(
                $"Could not write the marks file {started.Paths.Marks}: {ex.Message} Pass an out in a folder this server can write to.",
                ex
            );
        }
    }

    private static void WriteMarks(ActiveRecording active) => WriteMarks(active.Started, active.StartedUtc, active.Marks);

    /// <summary>Rewrites the marks file whole, through a temporary file, so a reader never sees half of it.</summary>
    private static void WriteMarks(RecordingStarted started, DateTime? startedUtc, IReadOnlyList<RecordingMark> marks)
    {
        var file = new MarksFile(started.Target.Pid, started.Target.Hwnd, started.Frame.Fps, startedUtc, marks);
        string temporary = started.Paths.Marks + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, MarksJson) + "\n");
        File.Move(temporary, started.Paths.Marks, overwrite: true);
    }

    /// <summary>The marks file: <c>{ pid, hwnd, fps, startedUtc, marks: [ { label, wallUtc, clipSeconds, simSeconds } ] }</c>.</summary>
    private sealed record MarksFile(int Pid, long Hwnd, int Fps, DateTime? StartedUtc, IReadOnlyList<RecordingMark> Marks);

    /// <summary>How a pipeline ended: what went wrong, and what is worth saying that is not a problem.</summary>
    private sealed record PipelineEnd(string? Problem, string? Note);

    /// <summary>
    /// A recording: what started, its pipeline, its marks, when it launched and stopped, and the first frame's instant once
    /// known. <see cref="Sync"/> guards the instants, read by actions logged off the gate, and the actions file.
    /// </summary>
    private sealed class ActiveRecording(RecordingStarted started, IRecordingProcess process, DateTime launchedUtc)
    {
        private DateTime? _startedUtc;
        private DateTime? _endedUtc;

        public RecordingStarted Started { get; } = started;

        public IRecordingProcess Process { get; } = process;

        public Guid Id { get; } = Guid.NewGuid();

        public List<RecordingMark> Marks { get; } = [];

        /// <summary>Guards the first frame's and the stop's instants, and every write to the actions file.</summary>
        public object Sync { get; } = new();

        /// <summary>When the pipeline was launched; an action stamped before it is not this recording's.</summary>
        public DateTime LaunchedUtc { get; } = launchedUtc;

        public DateTime? StartedUtc
        {
            get
            {
                lock (Sync)
                {
                    return _startedUtc;
                }
            }
            set
            {
                lock (Sync)
                {
                    _startedUtc = value;
                }
            }
        }

        /// <summary>When the recording was stopped, or noticed to have ended; an action stamped after it is not this recording's.</summary>
        public DateTime? EndedUtc
        {
            get
            {
                lock (Sync)
                {
                    return _endedUtc;
                }
            }
            set
            {
                lock (Sync)
                {
                    _endedUtc = value;
                }
            }
        }

        /// <summary>True once the action log's header is written, by the first action logged.</summary>
        public bool ActionsStarted { get; set; }

        /// <summary>True once the action log's header carries the first frame's instant.</summary>
        public bool ActionsHeaderHasStart { get; set; }
    }
}
