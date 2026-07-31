using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yaat.Client.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// How many previous sessions to keep alongside the live log, as <c>.1</c> … <c>.N</c>.
    /// </summary>
    private const int KeepPreviousLogs = 3;

    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Roll the previous session aside before truncating. Without this, a user who hits a hang or
        // crash and then relaunches to collect their log destroys the only record of the failure —
        // the new session's FileMode.Create wipes it before anyone reads it.
        var rotationError = RotatePreviousLogs(path);

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream) { AutoFlush = true };

        if (rotationError is not null)
        {
            // Rotation runs before any logger exists, so a failure can't be reported through the
            // normal channel. Surface it as the first line instead of dropping it.
            _writer.WriteLine($"[warn] Could not rotate previous log files: {rotationError.Message}");
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _writer));
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    /// <summary>
    /// Shifts <c>log</c> → <c>log.1</c> → <c>log.2</c> … dropping the oldest. Returns the exception
    /// when rotation could not complete (most often another client instance holding a handle) so the
    /// caller can record it; the log itself is still opened either way.
    /// </summary>
    private static Exception? RotatePreviousLogs(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var oldest = $"{path}.{KeepPreviousLogs}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = KeepPreviousLogs - 1; i >= 1; i--)
            {
                var from = $"{path}.{i}";
                if (File.Exists(from))
                {
                    File.Move(from, $"{path}.{i + 1}", overwrite: true);
                }
            }

            File.Move(path, $"{path}.1", overwrite: true);
            return null;
        }
        catch (IOException ex)
        {
            return ex;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ex;
        }
    }
}

public sealed class FileLogger(string category, StreamWriter writer) : ILogger
{
    private static readonly object WriteLock = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var level = logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "????",
        };

        var line = $"{timestamp} [{level}] {category}: {message}";

        lock (WriteLock)
        {
            writer.WriteLine(line);
            if (exception is not null)
            {
                writer.WriteLine(exception);
            }
        }
    }
}
