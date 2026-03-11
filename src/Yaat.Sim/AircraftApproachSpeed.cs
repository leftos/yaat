using Yaat.Sim.Data.Faa;

namespace Yaat.Sim;

/// <summary>
/// Maps ICAO aircraft type designators to FAA ACD approach speeds (knots).
/// Delegates to <see cref="FaaAircraftDatabase"/> when available, otherwise
/// uses a legacy flat lookup. Falls back to category defaults when an unknown
/// type is encountered.
/// </summary>
public static class AircraftApproachSpeed
{
    private static Dictionary<string, int> _legacyLookup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy initializer for flat approach-speed-only JSON (used by tests with old FaaAcd.json).</summary>
    public static void Initialize(Dictionary<string, int> lookup)
    {
        _legacyLookup = new Dictionary<string, int>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get approach speed (knots) for an aircraft type designator. Returns null if unknown.</summary>
    public static int? GetApproachSpeed(string? aircraftType)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return null;
        }

        // Prefer full database when available
        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record?.ApproachSpeedKnot is { } speed)
        {
            return speed;
        }

        // Fall back to legacy flat lookup
        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _legacyLookup.TryGetValue(baseType, out var legacySpeed) ? legacySpeed : null;
    }
}
