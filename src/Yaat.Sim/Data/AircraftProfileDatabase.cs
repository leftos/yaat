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
    private static Dictionary<string, IReadOnlySet<string>> _overriddenFields = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _siblingFallbackWarned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SiblingFallbackWarnedLock = new();

    public static bool IsInitialized => _lookup.Count > 0;

    public static int Count => _lookup.Count;

    /// <summary>
    /// Initialize the lookup from base profiles (AircraftProfiles.json) plus authoritative
    /// partial overrides (AircraftProfileOverrides.json). Each override is merged onto its base
    /// profile — a direct profile, a sibling's profile, or a synthesized category baseline for a
    /// type that has neither (e.g. the SF50) — and the resulting effective profile is stored so
    /// <see cref="Get"/> works unchanged. Overridden field names are recorded for
    /// <see cref="IsOverridden"/> so the correction adapter can treat them as authoritative.
    ///
    /// Requires <see cref="AircraftSiblingMap"/> and <see cref="AircraftCategorization"/> to be
    /// initialized first (sibling resolution and category-baseline synthesis depend on them).
    /// </summary>
    public static void Initialize(Dictionary<string, AircraftProfile> baseProfiles, IReadOnlyList<AircraftProfileOverride> overrides)
    {
        var lookup = new Dictionary<string, AircraftProfile>(baseProfiles, StringComparer.OrdinalIgnoreCase);
        var overriddenFields = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ov in overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.TypeCode))
            {
                Log.LogWarning("Skipping aircraft profile override with empty typeCode");
                continue;
            }

            var type = ov.TypeCode.Trim().ToUpperInvariant();

            AircraftProfile baseProfile;
            if (lookup.TryGetValue(type, out var direct))
            {
                baseProfile = direct;
            }
            else if (AircraftSiblingMap.TryResolve(type, out var sibling) && baseProfiles.TryGetValue(sibling, out var sibProfile))
            {
                baseProfile = sibProfile with { TypeCode = type };
            }
            else
            {
                baseProfile = CategoryPerformance.BaselineProfile(AircraftCategorization.Categorize(type)) with { TypeCode = type };
            }

            var (merged, fields) = ov.ApplyTo(baseProfile);
            lookup[type] = merged;
            overriddenFields[type] = fields;
        }

        _lookup = lookup;
        _overriddenFields = overriddenFields;
        ClearSiblingFallbackWarnings();
        Log.LogInformation("Loaded {Count} aircraft profiles ({OverrideCount} with overrides)", _lookup.Count, overriddenFields.Count);
    }

    /// <summary>
    /// True if <paramref name="fieldName"/> (an <see cref="AircraftProfile"/> property name) was
    /// explicitly set by an override for this type. The correction adapter consults this to leave
    /// overridden fields un-rescaled — overrides are authoritative.
    /// </summary>
    public static bool IsOverridden(string? aircraftType, string fieldName)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return false;
        }

        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _overriddenFields.TryGetValue(baseType, out var set) && set.Contains(fieldName);
    }

    /// <summary>
    /// Reset the per-type "used sibling fallback" warning set. Called when the
    /// sibling map is reloaded so a now-resolvable type warns again if it falls
    /// back to a different sibling in the new map.
    /// </summary>
    internal static void ClearSiblingFallbackWarnings()
    {
        lock (SiblingFallbackWarnedLock)
        {
            _siblingFallbackWarned.Clear();
        }
    }

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
            bool shouldWarn;
            lock (SiblingFallbackWarnedLock)
            {
                shouldWarn = _siblingFallbackWarned.Add(baseType);
            }

            if (shouldWarn)
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

    /// <summary>
    /// Load authoritative partial overrides from a JSON file on disk. Returns an empty list when
    /// the file is absent so the caller can wire it unconditionally.
    /// </summary>
    public static IReadOnlyList<AircraftProfileOverride> LoadOverridesFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<AircraftProfileOverride>>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize aircraft profile overrides from {path}");
    }
}
