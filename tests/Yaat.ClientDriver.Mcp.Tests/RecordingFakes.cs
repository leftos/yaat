extern alias mcp;

using System.Globalization;
using mcp::Yaat.ClientDriver.Mcp.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// A clock the test moves by hand, for the wall times a recording session stamps its marks with. Its timers fire at once, on
/// the thread pool, so a wait the session takes from it (the check that a new pipeline is still running) never sleeps.
/// </summary>
/// <param name="start">The instant the clock reads until the test moves it.</param>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new FiredTimer();
    }

    /// <summary>A timer that has already fired once and never fires again.</summary>
    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// The recording pipeline's process as a test drives it: it runs until the test says it exited, or until the stop's wait
/// reaches it, when <see cref="OnStop"/> plays the pipeline's own ending (the recorder's end line, the MP4 on disk).
/// </summary>
internal sealed class FakeRecordingProcess : IRecordingProcess
{
    public bool HasExited { get; set; }

    public int ExitCode { get; set; }

    public bool IsRecorderRunning => !HasExited && !RecorderKilled;

    /// <summary>True once the whole pipeline was killed.</summary>
    public bool Killed { get; private set; }

    /// <summary>True once the recorder alone was killed.</summary>
    public bool RecorderKilled { get; private set; }

    /// <summary>True for a recorder that does not stop within the stop's budget for it.</summary>
    public bool RecorderMissesStop { get; set; }

    /// <summary>True for a pipeline a kill does not end: it is recorded as killed but goes on running.</summary>
    public bool IgnoresKill { get; set; }

    /// <summary>What the pipeline does once the stop's deadline reaches it; null leaves it running past the stop's wait.</summary>
    public Action? OnStop { get; set; }

    /// <summary>When set, a wait for the pipeline's exit waits for this, honouring its token, and then exits as <see cref="OnStop"/> says.</summary>
    public TaskCompletionSource? PendingExit { get; set; }

    /// <summary>The budget of every wait for the recorder's exit, in order.</summary>
    public List<TimeSpan> RecorderWaits { get; } = [];

    /// <summary>The budget of every wait for the whole pipeline's exit, in order.</summary>
    public List<TimeSpan> PipelineWaits { get; } = [];

    public Task<bool> WaitForRecorderExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        RecorderWaits.Add(timeout);
        return Task.FromResult(HasExited || RecorderKilled || !RecorderMissesStop);
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        PipelineWaits.Add(timeout);
        if (PendingExit is { } pending)
        {
            await pending.Task.WaitAsync(ct);
        }
        else if (OnStop is null)
        {
            return HasExited;
        }

        OnStop?.Invoke();
        HasExited = true;
        return true;
    }

    public void KillRecorder() => RecorderKilled = true;

    public void KillTree()
    {
        Killed = true;
        HasExited = !IgnoresKill;
    }

    public void Dispose() { }
}

/// <summary>
/// A recording backend with no ffmpeg, recorder or window behind it: one 1202x732 window whose 1200x700 client area sits at
/// (1, 31), a probe that answers that size, and a pipeline that is only the command line it was started with. Each start
/// after the first gets a fresh process; the deadline is written to its file as well as recorded.
/// </summary>
internal sealed class FakeRecordingBackend : IRecordingBackend
{
    private bool _processStarted;

    public string? Ffmpeg { get; set; } = @"C:\tools\ffmpeg\ffmpeg.exe";

    public List<TopLevelWindow> Windows { get; } = [new TopLevelWindow(0x1234, "YAAT - fake")];

    public WindowGeometry Geometry { get; set; } = new(new ScreenRect(100, 200, 1302, 932), new ScreenRect(101, 231, 1301, 931), false);

    public string? ProbeRefusal { get; set; }

    /// <summary>When set, writing a deadline throws this.</summary>
    public Exception? DeadlineFailure { get; set; }

    /// <summary>Runs as each pipeline starts, before the start returns it.</summary>
    public Action? OnStart { get; set; }

    public List<string> CommandLines { get; } = [];

    public List<PipelineCommandLine> Launches { get; } = [];

    public List<(string Path, DateTime EndUtc)> Deadlines { get; } = [];

    /// <summary>The process the last start returned, or the one the next start will return when none has started yet.</summary>
    public FakeRecordingProcess Process { get; private set; } = new();

    public string? FindFfmpeg() => Ffmpeg;

    public string FindRecorder() => @"C:\tools\recorder\Yaat.WindowRecorder.exe";

    public Task<string> ChooseEncoderAsync(string ffmpeg, CancellationToken ct) => Task.FromResult(FfmpegPipeline.Libx264);

    public IReadOnlyList<TopLevelWindow> TopLevelWindows(int pid) => Windows;

    public WindowGeometry? ReadWindow(long hwnd) => Geometry;

    public Task<RecorderProbe> ProbeAsync(string recorder, IReadOnlyList<string> arguments, CancellationToken ct) =>
        Task.FromResult(new RecorderProbe(1202, 732, ProbeRefusal));

    public IRecordingProcess Start(PipelineCommandLine commandLine)
    {
        if (_processStarted)
        {
            Process = new FakeRecordingProcess();
        }

        _processStarted = true;
        CommandLines.Add(commandLine.Text);
        Launches.Add(commandLine);
        OnStart?.Invoke();
        return Process;
    }

    public void WriteDeadline(string path, DateTime endUtc)
    {
        if (DeadlineFailure is { } failure)
        {
            throw failure;
        }

        Deadlines.Add((path, endUtc));
        File.WriteAllText(path, endUtc.ToString("o", CultureInfo.InvariantCulture) + "\n");
    }
}

/// <summary>Builds recording sessions over the fakes, and writes the recorder's log lines a real run would.</summary>
internal static class RecordingFakes
{
    public static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A session over a fresh fake backend and clock, for a test that never records.</summary>
    public static RecordingSession NewSession() => NewSession(new FakeRecordingBackend(), new ManualClock(Start));

    public static RecordingSession NewSession(FakeRecordingBackend backend, ManualClock clock, ILogger<RecordingSession> logger) =>
        new(backend, clock, logger);

    public static RecordingSession NewSession(FakeRecordingBackend backend, ManualClock clock) =>
        new(backend, clock, NullLogger<RecordingSession>.Instance);

    /// <summary>Appends <paramref name="lines"/> to the recorder log beside <paramref name="mp4"/>, as the recorder's stderr would.</summary>
    public static void WriteRecorderLog(string mp4, params string[] lines) => File.AppendAllLines(RecordingPaths.For(mp4).RecorderLog, lines);
}

/// <summary>A logger that keeps every entry's level and formatted message, for a test that asserts what was logged.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
