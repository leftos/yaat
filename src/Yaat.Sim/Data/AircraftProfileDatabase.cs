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
    private static readonly HashSet<string> _siblingFallbackWarned = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsInitialized => _lookup.Count > 0;

    public static int Count => _lookup.Count;

    public static void Initialize(Dictionary<string, AircraftProfile> lookup)
    {
        _lookup = new Dictionary<string, AircraftProfile>(lookup, StringComparer.OrdinalIgnoreCase);
        _siblingFallbackWarned.Clear();
        Log.LogInformation("Loaded {Count} aircraft profiles", _lookup.Count);
    }

    /// <summary>
    /// Reset the per-type "used sibling fallback" warning set. Called when the
    /// sibling map is reloaded so a now-resolvable type warns again if it falls
    /// back to a different sibling in the new map.
    /// </summary>
    internal static void ClearSiblingFallbackWarnings() => _siblingFallbackWarned.Clear();

    /// <summary>
    /// Get the performance profile for an ICAO type designator.
    /// Strips prefixes like "H/" and suffixes like "/L" automatically. Falls back
    /// to the sibling map (e.g. <c>B789 -&gt; B788</c>) if there is no direct hit.
    /// </summary>
    public static AircraftProfile? Get(string? aircraftType)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return null;
        }

        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        if (_lookup.TryGetValue(baseType, out var profile))
        {
            return profile;
        }

        if (AircraftSiblingMap.TryResolve(baseType, out var sibling) && _lookup.TryGetValue(sibling, out var sibProfile))
        {
            if (_siblingFallbackWarned.Add(baseType))
            {
                Log.LogWarning(
                    "No profile for type {Type}; using sibling {Sibling}'s profile. Consider adding a real entry to AircraftProfiles.json.",
                    baseType,
                    sibling
                );
            }
            return sibProfile;
        }

        return null;
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
