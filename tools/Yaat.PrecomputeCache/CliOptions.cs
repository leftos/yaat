using System.Globalization;
using Yaat.Sim.Data.Airport.Precompute;

namespace Yaat.PrecomputeCache;

/// <summary>What one run of the tool does.</summary>
public enum CliMode
{
    /// <summary>Compute and write the entries of the listed (or named) airports.</summary>
    Compute,

    /// <summary>Report each listed (or named) airport whose entry is missing or stale.</summary>
    Check,

    /// <summary>Rebuild the design-group envelopes and the pinned FAA test copy from current FAA data.</summary>
    RefreshEnvelopes,

    /// <summary>Rebuild the airport list from every ARTCC's vNAS training scenarios.</summary>
    RefreshAirports,
}

/// <summary>A command line the tool was given, parsed and checked.</summary>
public sealed record CliOptions
{
    /// <summary>The help text printed with every usage error.</summary>
    public const string Usage = """
        Usage: dotnet run --project tools/Yaat.PrecomputeCache -- [options]

          (no mode flag)        compute the entry of every airport in <root>/airports.txt, or of each --airport
          --airport <id>        an airport to work on, FAA or ICAO id (repeatable); used instead of the list
          --force               recompute an airport whose entry is already current
          --parallel <n>        how many stands to plan at once, across every airport in flight (default: the processor count)
          --airports-in-flight <n>
                                how many airports to work on at once, each holding its parsed layout (default: 4)
          --check               report each airport whose entry is missing or stale, and which half, as a GitHub
                                ::warning:: line; an unreadable entry is an ::error:: line and exits 1
          --online              with --check: also compare the GeoJSON MD5 and NavData serial with vNAS now
          --refresh-envelopes   rebuild design-group-envelopes.json and the pinned test copy
                                tests/Yaat.Sim.Tests/TestData/FaaAcd.json from the current FAA data
          --refresh-airports    rebuild airports.txt from every ARTCC's vNAS training scenarios
          --root <dir>          the cache folder (default: src/Yaat.Sim/Data/PrecomputeCache in this repo)

        Exit codes: 0 done (a missing or stale entry is a warning), 1 a failure or an unreadable entry, 2 a usage error.
        """;

    /// <summary>How many airports a compute run works on at once when <c>--airports-in-flight</c> is not given.</summary>
    public const int DefaultAirportsInFlight = 4;

    /// <summary>What the run does.</summary>
    public required CliMode Mode { get; init; }

    /// <summary>The airports named with <c>--airport</c>, FAA ids, sorted; empty to use the list.</summary>
    public required IReadOnlyList<string> Airports { get; init; }

    /// <summary>Whether a current entry is recomputed.</summary>
    public required bool Force { get; init; }

    /// <summary>How many stands are planned at once, across every airport in flight.</summary>
    public required int Parallel { get; init; }

    /// <summary>How many airports a compute run works on at once.</summary>
    public required int AirportsInFlight { get; init; }

    /// <summary>Whether the check also compares the GeoJSON MD5 and NavData serial with vNAS.</summary>
    public required bool Online { get; init; }

    /// <summary>The <c>--root</c> given, or null for the repo's cache folder.</summary>
    public required string? Root { get; init; }

    /// <summary>Parses <paramref name="args"/>.</summary>
    /// <param name="args">The command line after <c>--</c>.</param>
    /// <returns>The options.</returns>
    /// <exception cref="CliUsageException">An unknown flag, a missing or bad value, or flags that do not go together.</exception>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var parser = new Parser();
        var queue = new Queue<string>(args);
        while (queue.TryDequeue(out string? flag))
        {
            parser.Apply(flag, queue);
        }

        return parser.Build();
    }

    private sealed class Parser
    {
        private readonly List<string> _airports = [];
        private readonly HashSet<CliMode> _modes = [];
        private bool _force;
        private int? _parallel;
        private int? _airportsInFlight;
        private bool _online;
        private string? _root;

        public void Apply(string flag, Queue<string> rest)
        {
            switch (flag)
            {
                case "--airport":
                    _airports.Add(ValueOf(flag, rest));
                    break;
                case "--force":
                    _force = true;
                    break;
                case "--parallel":
                    _parallel = AtLeastOne(flag, ValueOf(flag, rest));
                    break;
                case "--airports-in-flight":
                    _airportsInFlight = AtLeastOne(flag, ValueOf(flag, rest));
                    break;
                case "--check":
                    _modes.Add(CliMode.Check);
                    break;
                case "--online":
                    _online = true;
                    break;
                case "--refresh-envelopes":
                    _modes.Add(CliMode.RefreshEnvelopes);
                    break;
                case "--refresh-airports":
                    _modes.Add(CliMode.RefreshAirports);
                    break;
                case "--root":
                    _root = ValueOf(flag, rest);
                    break;
                default:
                    throw new CliUsageException($"Unknown argument '{flag}'");
            }
        }

        public CliOptions Build()
        {
            if (_modes.Count > 1)
            {
                throw new CliUsageException("--check, --refresh-envelopes and --refresh-airports each name a run of their own; give one");
            }

            CliMode mode = _modes.Count == 0 ? CliMode.Compute : _modes.Single();
            Require(!_online || (mode == CliMode.Check), "--online goes with --check only");
            Require(
                (!_force && (_parallel is null) && (_airportsInFlight is null)) || (mode == CliMode.Compute),
                "--force, --parallel and --airports-in-flight go with a compute run only"
            );
            Require((_airports.Count == 0) || (mode is CliMode.Compute or CliMode.Check), "--airport goes with a compute run or --check only");
            return new CliOptions
            {
                Mode = mode,
                Airports = ScenarioAirportCollector.Normalize(_airports),
                Force = _force,
                Parallel = _parallel ?? Environment.ProcessorCount,
                AirportsInFlight = _airportsInFlight ?? DefaultAirportsInFlight,
                Online = _online,
                Root = _root,
            };
        }

        private static string ValueOf(string flag, Queue<string> rest) =>
            rest.TryDequeue(out string? value) && !value.StartsWith("--", StringComparison.Ordinal)
                ? value
                : throw new CliUsageException($"{flag} needs a value");

        private static int AtLeastOne(string flag, string value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && (count >= 1)
                ? count
                : throw new CliUsageException($"{flag} needs a whole number of at least 1, not '{value}'");

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new CliUsageException(message);
            }
        }
    }
}

/// <summary>A command line the tool cannot run: the message says what is wrong, and the usage text follows it.</summary>
/// <param name="message">What is wrong.</param>
public sealed class CliUsageException(string message) : Exception(message);

/// <summary>A run that cannot go on: the message says why and what to do.</summary>
public sealed class ToolFailureException : Exception
{
    /// <summary>A failure the tool found itself.</summary>
    /// <param name="message">Why, and what to do.</param>
    public ToolFailureException(string message)
        : base(message) { }

    /// <summary>A failure another exception caused, kept as the inner exception.</summary>
    /// <param name="message">Why, and what to do.</param>
    /// <param name="innerException">The exception that caused it.</param>
    public ToolFailureException(string message, Exception innerException)
        : base(message, innerException) { }
}
