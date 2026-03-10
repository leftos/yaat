using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yaat.Sim;

public static class SimLog
{
    private static ILoggerFactory? _factory;

    public static void Initialize(ILoggerFactory factory) => _factory = factory;

    public static ILogger CreateLogger<T>() => _factory?.CreateLogger<T>() ?? NullLoggerFactory.Instance.CreateLogger<T>();

    public static ILogger CreateLogger(string category) => _factory?.CreateLogger(category) ?? NullLoggerFactory.Instance.CreateLogger(category);
}
