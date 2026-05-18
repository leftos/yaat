using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data;

/// <summary>
/// Maps ICAO aircraft type designators (e.g. "C172", "B738") to short human-readable
/// display names ("Cessna Skyhawk 172/Cutlass", "Boeing 737-800") for the Aircraft List
/// "Name" column.
/// </summary>
/// <remarks>
/// Data is seeded from the FAA Aircraft Characteristics Database via
/// <c>tools/refresh-aircraft-display-names.py</c>. The committed JSON is the source of
/// truth — hand-edit entries directly when an FAA modelFaa string reads poorly.
/// </remarks>
public static class AircraftDisplayNames
{
    private static readonly ILogger Log = SimLog.CreateLogger("AircraftDisplayNames");

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _lookup = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Number of ICAO type designators loaded. Forces initialization.</summary>
    public static int Count => _lookup.Value.Count;

    /// <summary>
    /// Look up a display name for an ICAO type designator (e.g. "C172" → "Cessna Skyhawk 172/Cutlass").
    /// Returns false if the type is unknown. Caller-supplied designator may be any case.
    /// </summary>
    public static bool TryGet(string? icaoType, out string displayName)
    {
        displayName = "";
        if (string.IsNullOrWhiteSpace(icaoType))
        {
            return false;
        }
        return _lookup.Value.TryGetValue(icaoType.Trim().ToUpperInvariant(), out displayName!);
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "aircraft-display-names.json");
        if (!File.Exists(path))
        {
            Log.LogWarning("aircraft-display-names.json not found at {Path}; display-name map will be empty", path);
            return new Dictionary<string, string>();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (raw is null || raw.Count == 0)
            {
                Log.LogWarning("aircraft-display-names.json at {Path} parsed empty", path);
                return new Dictionary<string, string>();
            }

            var dict = new Dictionary<string, string>(raw.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in raw)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                {
                    dict[k.Trim().ToUpperInvariant()] = v.Trim();
                }
            }
            Log.LogInformation("Loaded {Count} aircraft display names", dict.Count);
            return dict;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to load aircraft-display-names.json at {Path}", path);
            return new Dictionary<string, string>();
        }
    }
}
