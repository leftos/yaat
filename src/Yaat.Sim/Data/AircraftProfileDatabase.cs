using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data;

/// <summary>
/// Static lookup for per-type aircraft performance profiles.
/// Initialized once at startup from AircraftProfiles.json.
/// </summary>
public static class AircraftProfileDatabase
{
    private static readonly ILogger Log = SimLog.CreateLogger("AircraftProfileDatabase");

    private static Dictionary<string, AircraftProfile> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsInitialized => _lookup.Count > 0;

    public static int Count => _lookup.Count;

    public static void Initialize(Dictionary<string, AircraftProfile> lookup)
    {
        _lookup = new Dictionary<string, AircraftProfile>(lookup, StringComparer.OrdinalIgnoreCase);
        Log.LogInformation("Loaded {Count} aircraft profiles", _lookup.Count);
    }

    /// <summary>
    /// Get the performance profile for an ICAO type designator.
    /// Strips prefixes like "H/" and suffixes like "/L" automatically.
    /// </summary>
    public static AircraftProfile? Get(string? aircraftType)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return null;
        }

        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _lookup.TryGetValue(baseType, out var profile) ? profile : null;
    }

    /// <summary>
    /// Load profiles from a JSON file on disk.
    /// Returns a dictionary keyed by ICAO type code.
    /// </summary>
    public static Dictionary<string, AircraftProfile> LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var profiles =
            JsonSerializer.Deserialize<List<AircraftProfile>>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize aircraft profiles from {path}");

        var result = new Dictionary<string, AircraftProfile>(profiles.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            result[profile.TypeCode] = profile;
        }

        return result;
    }
}
