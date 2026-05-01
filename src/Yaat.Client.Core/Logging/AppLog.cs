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

    public static void Initialize(string logFileName)
    {
        LogPath = YaatPaths.Combine(logFileName);

        var provider = new FileLoggerProvider(LogPath);

        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(provider);
        });
        SimLog.Initialize(_factory);
    }

    /// <summary>
    /// Browser/WASM initializer. The desktop <see cref="Initialize"/> path
    /// builds a <c>FileLoggerProvider</c> against <c>YaatPaths</c> — that's
    /// useless in WASM where there's no filesystem and useful logs need to
    /// land in the browser DevTools console. This wires a minimal
    /// <see cref="ConsoleLineLoggerProvider"/> that calls
    /// <c>Console.WriteLine</c>, which Mono-WASM forwards to <c>console.log</c>.
    /// </summary>
    public static void InitializeForBrowser(LogLevel minimumLevel = LogLevel.Information)
    {
        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddProvider(new ConsoleLineLoggerProvider());
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
