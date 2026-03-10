using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yaat.Sim;

namespace Yaat.Client.Logging;

/// <summary>
/// Static logger factory for the client app.
/// Call <see cref="Initialize"/> once at startup.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory? _factory;

    public static string LogPath { get; private set; } = "";

    public static void Initialize()
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat");
        LogPath = Path.Combine(logDir, "yaat-client.log");

        var provider = new FileLoggerProvider(LogPath);

        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(provider);
        });
        SimLog.Initialize(_factory);
    }

    public static ILogger CreateLogger<T>()
    {
        if (_factory is null)
        {
            return NullLoggerFactory.Instance.CreateLogger<T>();
        }

        return _factory.CreateLogger<T>();
    }

    public static ILogger CreateLogger(string category)
    {
        if (_factory is null)
        {
            return NullLoggerFactory.Instance.CreateLogger(category);
        }

        return _factory.CreateLogger(category);
    }

    /// <summary>
    /// Disposes the logger factory, flushing all buffered output.
    /// Call on fatal crash before exiting.
    /// </summary>
    public static void Flush()
    {
        _factory?.Dispose();
    }
}
