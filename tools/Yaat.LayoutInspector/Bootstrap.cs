using Microsoft.Extensions.Logging;
using Yaat.Sim;
using Yaat.Sim.Testing;

namespace Yaat.LayoutInspector;

/// <summary>
/// Early-startup side effects for the inspector CLI: NavData loading and debug
/// logger wiring. Each helper is isolated so Program.cs stays a thin dispatcher.
/// </summary>
public static class Bootstrap
{
    /// <summary>
    /// Loads NavData from the given directory (or auto-discovers one by walking up
    /// to <c>yaat.slnx</c>). Missing NavData is a warning, not an error — LI falls
    /// back to the default 150ft runway width.
    /// </summary>
    public static void TryLoadNavData(string? navdataDir)
    {
        navdataDir ??= FindDefaultNavDataDir();
        if (navdataDir is null)
        {
            Console.Error.WriteLine("Warning: NavData not found, using default runway widths (150ft)");
            return;
        }

        try
        {
            TestVnasData.SetTestDataDir(navdataDir);
            TestVnasData.EnsureInitialized();
            Console.Error.WriteLine($"Loaded NavData from {navdataDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load NavData: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks up from the executable location to the solution root and returns the
    /// canonical test-data directory, or null if the solution root cannot be
    /// found. Used by both NavData loading and test fixtures.
    /// </summary>
    public static string? FindDefaultNavDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                var testData = Path.Combine(dir.FullName, "tests", "Yaat.Sim.Tests", "TestData");
                return Directory.Exists(testData) ? testData : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Wires up the Yaat.Sim logger factory for --debug-fillets and --debug-exits.
    /// No-op when neither flag is set, preserving the default silent behavior of
    /// LI invocations that don't opt in to logging.
    /// </summary>
    public static void ConfigureDebugLogging(CliOptions opts)
    {
        if (!opts.DebugFillets && !opts.DebugExits)
        {
            return;
        }

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.IncludeScopes = false;
            });

            if (opts.DebugFillets)
            {
                builder.AddFilter("FilletArcGenerator", LogLevel.Debug);
                builder.AddFilter("RunwayCrossingDetector", LogLevel.Debug);
            }

            if (opts.DebugExits)
            {
                builder.AddFilter("AirportGroundLayout", LogLevel.Debug);
            }

            builder.SetMinimumLevel(LogLevel.Warning);
        });
        SimLog.Initialize(loggerFactory);
    }
}
