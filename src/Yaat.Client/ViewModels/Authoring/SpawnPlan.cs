using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Authoring.Spawns;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>How the spawn planner makes its slots.</summary>
public enum SpawnPlannerMode
{
    /// <summary>Time bands, each with its own interval and jitter.</summary>
    Bands,

    /// <summary>A workload curve over the planning length.</summary>
    Workload,
}

/// <summary>The spawn planner's filter as typed; a blank member restricts nothing.</summary>
/// <param name="FlightRules">The flight rules to take.</param>
/// <param name="Kind">The kind of start to take, or null for any.</param>
/// <param name="Departure">The departure airport to take.</param>
/// <param name="Destination">The destination airport to take.</param>
/// <param name="Approach">The expected approach to take.</param>
/// <param name="ExcludeDeparture">The departure airport to leave out.</param>
/// <param name="ExcludeDestination">The destination airport to leave out.</param>
/// <param name="ExcludeCallsigns">The callsigns to leave out, separated by commas or spaces.</param>
public sealed record SpawnPlanFilter(
    string FlightRules,
    SpawnKind? Kind,
    string Departure,
    string Destination,
    string Approach,
    string ExcludeDeparture,
    string ExcludeDestination,
    string ExcludeCallsigns
)
{
    /// <summary>Gets the filter that restricts nothing.</summary>
    public static SpawnPlanFilter Empty { get; } = new("", null, "", "", "", "", "", "");
}

/// <summary>One band row as typed: minutes from and to, the interval as m:ss, and the jitter seconds either way.</summary>
/// <param name="From">The band's start in minutes.</param>
/// <param name="To">The band's end in minutes.</param>
/// <param name="Interval">The interval as m:ss.</param>
/// <param name="Plus">The most seconds a slot moves later.</param>
/// <param name="Minus">The most seconds a slot moves earlier.</param>
public sealed record SpawnPlanBand(string From, string To, string Interval, string Plus, string Minus)
{
    /// <summary>Gets the band a new planner starts with, and Add band falls back to: the first hour every 2:00, 15 s either way.</summary>
    public static SpawnPlanBand Default { get; } = new("0", "60", "2:00", "15", "15");
}

/// <summary>One workload control point: a percentage at a time in minutes from the start.</summary>
/// <param name="TimeMinutes">The point's time in minutes.</param>
/// <param name="Percent">The workload at that time.</param>
public sealed record SpawnPlanPoint(double TimeMinutes, double Percent);

/// <summary>The workload mode's settings.</summary>
/// <param name="BucketSizeSec">The graph's bucket size in seconds.</param>
/// <param name="MinIntervalSec">The interval at full workload.</param>
/// <param name="MaxIntervalSec">The interval at no workload.</param>
/// <param name="JitterSec">The most seconds a slot moves either way.</param>
/// <param name="StartPercent">The workload at the start.</param>
/// <param name="EndPercent">The workload at the end.</param>
/// <param name="DistributeAllMatched">True to place every matched aircraft, false to walk the curve.</param>
/// <param name="AutoPeaks">How many peaks the auto-fill places.</param>
/// <param name="AutoPeakPercent">The workload at each auto-filled peak.</param>
/// <param name="AutoTroughPercent">The workload at each auto-filled trough.</param>
public sealed record SpawnPlanWorkload(
    int BucketSizeSec,
    int MinIntervalSec,
    int MaxIntervalSec,
    int JitterSec,
    double StartPercent,
    double EndPercent,
    bool DistributeAllMatched,
    int AutoPeaks,
    double AutoPeakPercent,
    double AutoTroughPercent
)
{
    /// <summary>Gets the workload settings a new planner starts with, and a plan without them loads.</summary>
    public static SpawnPlanWorkload Default { get; } = new(300, 60, 300, 15, 20, 20, true, 2, 100, 30);
}

/// <summary>Every spawn planner input, saved as one versioned plan file.</summary>
/// <param name="Version">The plan format's version; <see cref="CurrentVersion"/> when written.</param>
/// <param name="Filter">Which aircraft the plan takes.</param>
/// <param name="Mode">How the plan makes its slots.</param>
/// <param name="Bands">The band rows.</param>
/// <param name="Workload">The workload settings.</param>
/// <param name="ControlPoints">The workload curve's control points.</param>
/// <param name="PlanningLengthSec">The length the plan is laid over, in seconds.</param>
/// <param name="WorkingTimeMinutes">The working time an extend adds after the last spawn.</param>
/// <param name="MinGapSec">The least time between two spawns at one origin; zero or less is off.</param>
/// <param name="Seed">The seed of the random order and jitter.</param>
/// <param name="RandomOrder">True to order the matched aircraft randomly, false by spawn delay.</param>
/// <param name="Remedy">What the plan does about a spawn after the planning length.</param>
public sealed record SpawnPlan(
    int Version,
    SpawnPlanFilter Filter,
    SpawnPlannerMode Mode,
    IReadOnlyList<SpawnPlanBand> Bands,
    SpawnPlanWorkload Workload,
    IReadOnlyList<SpawnPlanPoint> ControlPoints,
    int PlanningLengthSec,
    int WorkingTimeMinutes,
    int MinGapSec,
    int Seed,
    bool RandomOrder,
    SpawnOverrunRemedy Remedy
)
{
    /// <summary>The plan format's version this YAAT writes and reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The planning length a new planner starts with, and a plan without one loads.</summary>
    public const int DefaultPlanningLengthSec = 3600;

    /// <summary>The working time a new planner starts with, and a plan without one loads.</summary>
    public const int DefaultWorkingTimeMinutes = 10;

    /// <summary>The minimum same-origin gap a new planner starts with, and a plan without one loads.</summary>
    public const int DefaultMinGapSec = 60;
}

/// <summary>What one <see cref="SpawnPlanFile.Read"/> or <see cref="SpawnPlanFile.Parse"/> found.</summary>
/// <param name="Plan">The plan, or null when it could not be read.</param>
/// <param name="Error">Why it could not be read; null when it was.</param>
public sealed record SpawnPlanReadResult(SpawnPlan? Plan, string? Error);

/// <summary>Writes and reads spawn plan files: indented JSON, LF line ends, with a <c>version</c> field.</summary>
public static class SpawnPlanFile
{
    private static readonly ILogger Log = AppLog.CreateLogger("SpawnPlanFile");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    /// <summary>The plan as the file's text.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(SpawnPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.Serialize(plan, Options);
    }

    /// <summary>Writes the plan to the file, replacing it whole.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="plan">The plan.</param>
    public static void Write(string path, SpawnPlan plan)
    {
        ArgumentNullException.ThrowIfNull(path);
        AtomicFile.WriteAllText(path, Serialize(plan));
    }

    /// <summary>Reads a plan file; a file that cannot be read, or is not a plan of this version, gives the reason.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The plan, or why there is none.</returns>
    public static SpawnPlanReadResult Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Cannot read spawn plan {Path}", path);
            return new SpawnPlanReadResult(null, $"Cannot read the plan file: {ex.Message}");
        }

        return Parse(text);
    }

    /// <summary>
    /// Reads a plan from its text. Text that is not a plan of this version, names a mode, remedy or kind this YAAT does not
    /// know, holds an empty band or control point, or has a planning length of zero or less gives the reason. A missing
    /// planning length, working time or minimum gap loads its default.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The plan, or why there is none.</returns>
    public static SpawnPlanReadResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (VersionError(root) is { } error)
            {
                return Fail(error);
            }

            SpawnPlan? plan = root.Deserialize<SpawnPlan>(Options);
            return (plan is null) ? Fail("The file is not a spawn plan.") : Complete(plan, root);
        }
        catch (JsonException ex)
        {
            return Fail($"The file is not a readable spawn plan: {ex.Message}");
        }
    }

    private static SpawnPlanReadResult Fail(string error) => new(null, error);

    private static string? VersionError(JsonElement root)
    {
        if ((root.ValueKind != JsonValueKind.Object) || (!TryVersion(root, out int version)))
        {
            return "The file is not a spawn plan: it has no version.";
        }

        if (version != SpawnPlan.CurrentVersion)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The plan is version {version}; this YAAT reads spawn plan version {SpawnPlan.CurrentVersion}."
            );
        }

        return null;
    }

    private static bool TryVersion(JsonElement root, out int version)
    {
        version = 0;
        return (Find(root, "version") is { } value) && (value.ValueKind == JsonValueKind.Number) && (value.TryGetInt32(out version));
    }

    private static JsonElement? Find(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static SpawnPlanReadResult Complete(SpawnPlan plan, JsonElement root)
    {
        SpawnPlan filled = plan with
        {
            Filter = plan.Filter ?? SpawnPlanFilter.Empty,
            Bands = plan.Bands ?? [],
            Workload = plan.Workload ?? SpawnPlanWorkload.Default,
            ControlPoints = plan.ControlPoints ?? [],
            PlanningLengthSec = (Find(root, "planningLengthSec") is null) ? SpawnPlan.DefaultPlanningLengthSec : plan.PlanningLengthSec,
            WorkingTimeMinutes = (Find(root, "workingTimeMinutes") is null) ? SpawnPlan.DefaultWorkingTimeMinutes : plan.WorkingTimeMinutes,
            MinGapSec = (Find(root, "minGapSec") is null) ? SpawnPlan.DefaultMinGapSec : plan.MinGapSec,
        };
        string? error = EnumError(filled) ?? ContentError(filled);
        return (error is null) ? new SpawnPlanReadResult(filled, null) : Fail(error);
    }

    private static string? EnumError(SpawnPlan plan)
    {
        bool known = (Enum.IsDefined(plan.Mode)) && (Enum.IsDefined(plan.Remedy)) && ((plan.Filter.Kind is not { } kind) || (Enum.IsDefined(kind)));
        return known ? null : "The plan names a mode, remedy or start kind this YAAT does not know.";
    }

    private static string? ContentError(SpawnPlan plan)
    {
        if ((plan.Bands.Any(band => band is null)) || (plan.ControlPoints.Any(point => point is null)))
        {
            return "The plan holds an empty band or control point.";
        }

        return (plan.PlanningLengthSec <= 0) ? "The plan's planning length must be more than zero." : null;
    }
}
