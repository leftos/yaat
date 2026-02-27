using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yaat.Client.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<string, FileLogger>
        _loggers = new();

    public FileLoggerProvider(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream)
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(
            categoryName,
            name => new FileLogger(name, _writer));
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}

public sealed class FileLogger(
    string category, StreamWriter writer) : ILogger
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

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
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

        var line =
            $"{timestamp} [{level}] {category}: {message}";

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
