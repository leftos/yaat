using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Fluent builder for configuring SimLog in tests with per-category log filtering.
/// Default: only Warning+ from all categories. Use <see cref="EnableCategory"/> to
/// selectively enable Debug/Trace output for specific submodules.
/// </summary>
public sealed class SimLogBuilder
{
    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, LogLevel> _categories = new();
    private LogLevel _defaultLevel = LogLevel.Warning;

    private SimLogBuilder(ITestOutputHelper output) => _output = output;

    public static SimLogBuilder CreateForTest(ITestOutputHelper output) => new(output);

    /// <summary>
    /// Set the default minimum log level for categories not explicitly enabled.
    /// Default is <see cref="LogLevel.Warning"/>.
    /// </summary>
    public SimLogBuilder WithDefaultLevel(LogLevel level)
    {
        _defaultLevel = level;
        return this;
    }

    /// <summary>
    /// Enable a specific category (substring match, case-insensitive) at the given level.
    /// Category names match SimLog.CreateLogger calls: "SimulationEngine", "CommandDispatcher",
    /// "GroundConflictDetector", "FlightPhysics", "GeoJsonParser", etc.
    /// </summary>
    public SimLogBuilder EnableCategory(string categorySubstring, LogLevel level)
    {
        _categories[categorySubstring] = level;
        return this;
    }

    /// <summary>
    /// Build the <see cref="ILoggerFactory"/> with the configured filters.
    /// </summary>
    public ILoggerFactory Build()
    {
        var defaultLevel = _defaultLevel;
        var categories = new Dictionary<string, LogLevel>(_categories);

        return LoggerFactory.Create(builder =>
        {
            builder.AddXUnit(_output);
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter(
                (category, level) =>
                {
                    if (category is not null)
                    {
                        foreach (var (key, minLevel) in categories)
                        {
                            if (category.Contains(key, StringComparison.OrdinalIgnoreCase))
                            {
                                return level >= minLevel;
                            }
                        }
                    }

                    return level >= defaultLevel;
                }
            );
        });
    }

    /// <summary>
    /// Build the factory and initialize <see cref="SimLog"/> in one call.
    /// </summary>
    public void InitializeSimLog() => SimLog.Initialize(Build());
}
