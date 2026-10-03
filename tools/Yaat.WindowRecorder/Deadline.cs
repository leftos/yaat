using System.Diagnostics;
using System.Globalization;

namespace Yaat.WindowRecorder;

/// <summary>
/// When a run ends: a fixed number of seconds after <see cref="Start"/>, the instant a file holds, or both, where the file
/// is moved to that many seconds after <see cref="Start"/>. The file is rewritten while the run goes on (Record-Window.ps1
/// -Stop and -Extend), so its end can move after launch; it holds one line, a UTC instant in round-trip ("o") format. The
/// video and the audio of one run share one instance, and both ask it from their own threads.
/// </summary>
internal sealed class Deadline
{
    /// <summary>How stale the file's value may get; the video and audio loops ask far more often than this.</summary>
    private static readonly TimeSpan RereadInterval = TimeSpan.FromMilliseconds(200);

    private readonly string? _path;
    private readonly int _seconds;
    private readonly Lock _gate = new();
    private readonly Stopwatch _sinceRead = Stopwatch.StartNew();

    /// <summary>The file's end as first read; a re-base happens only while the file still holds it.</summary>
    private readonly DateTime _firstReadUtc;
    private DateTime _endUtc;
    private bool _started;
    private bool _warned;

    private Deadline(string? path, int seconds, DateTime endUtc)
    {
        _path = path;
        _seconds = seconds;
        _endUtc = endUtc;
        _firstReadUtc = endUtc;
    }

    /// <summary>A fixed length that has not started: it never passes until <see cref="Start"/> is called.</summary>
    public static Deadline AfterSeconds(int seconds) => new(null, seconds, DateTime.MaxValue);

    /// <summary>
    /// The end a file holds; with <paramref name="seconds"/> above 0, <see cref="Start"/> moves it to that many seconds
    /// later. Returns null, with the reason in <paramref name="error"/>, when the file cannot be read or holds no instant.
    /// </summary>
    public static Deadline? FromFile(string path, int seconds, out string? error) =>
        TryRead(path, out DateTime endUtc, out error) ? new Deadline(path, seconds, endUtc) : null;

    /// <summary>
    /// Starts the run's clock; later calls change nothing, so the video and the audio of one run share the first. A length
    /// counts from the run's own start (the first frame, or the reader connecting), not from launch, so setup does not come
    /// out of it. With a file, the file is rewritten to the new end, so whoever reads it (-Extend) sees it; a file that no
    /// longer holds what was first read was rewritten by a -Stop or -Extend during setup, and that end wins.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _seconds == 0)
            {
                return;
            }
            _started = true;
            DateTime endUtc = DateTime.UtcNow.AddSeconds(_seconds);
            if (_path is null)
            {
                _endUtc = endUtc;
                return;
            }
            Rebase(_path, endUtc);
        }
    }

    /// <summary>
    /// True once the end has passed. A file that has gone missing or holds something unreadable keeps the last end it
    /// held: a rewrite caught half-way must not stop a run, and a deleted file must not make one endless.
    /// </summary>
    public bool HasPassed()
    {
        lock (_gate)
        {
            if (_path is not null && _sinceRead.Elapsed >= RereadInterval)
            {
                Reread(_path);
            }
            return DateTime.UtcNow >= _endUtc;
        }
    }

    public string Describe()
    {
        if (_path is null)
        {
            return FormattableString.Invariant($"for {_seconds} s");
        }
        return _seconds == 0
            ? $"until {_path} says stop"
            : FormattableString.Invariant($"for {_seconds} s from the first frame, until {_path} says stop");
    }

    private void Rebase(string path, DateTime endUtc)
    {
        _sinceRead.Restart();
        if (!TryRead(path, out DateTime currentUtc, out string? error))
        {
            Console.Error.WriteLine($"Yaat.WindowRecorder: {error}; the end was not moved to the run's start.");
            return;
        }
        if (currentUtc != _firstReadUtc)
        {
            _endUtc = currentUtc;
            Console.Error.WriteLine(
                FormattableString.Invariant($"Yaat.WindowRecorder: {path} changed during setup, so the run keeps its end, {currentUtc:o}.")
            );
            return;
        }
        // Written beside the file under a name unique to this process and moved over it, so a reader never sees half a
        // line and a -Stop writing at the same moment never shares the temporary file.
        string temporary = FormattableString.Invariant($"{path}.{Environment.ProcessId}.tmp");
        File.WriteAllText(temporary, endUtc.ToString("o", CultureInfo.InvariantCulture) + "\n");
        File.Move(temporary, path, overwrite: true);
        _endUtc = endUtc;
        Console.Error.WriteLine(FormattableString.Invariant($"Yaat.WindowRecorder: {path} moved to the run's start plus {_seconds} s, {endUtc:o}."));
    }

    private void Reread(string path)
    {
        _sinceRead.Restart();
        if (TryRead(path, out DateTime endUtc, out string? error))
        {
            _endUtc = endUtc;
        }
        else if (!_warned)
        {
            _warned = true;
            Console.Error.WriteLine(FormattableString.Invariant($"Yaat.WindowRecorder: {error}; the run keeps the last end it read, {_endUtc:o}."));
        }
    }

    private static bool TryRead(string path, out DateTime endUtc, out string? error)
    {
        string text;
        try
        {
            // Delete sharing lets Move-Item replace the file while it is open here.
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            text = reader.ReadToEnd().Trim();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            endUtc = default;
            error = $"the deadline file {path} could not be read ({failure.Message})";
            return false;
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out endUtc))
        {
            error = null;
            return true;
        }
        error = $"the deadline file {path} holds '{text}', not a UTC instant in round-trip (\"o\") format";
        return false;
    }
}
