namespace Yaat.Sim;

/// <summary>
/// Maps ICAO aircraft type designators to FAA ACD approach speeds (knots).
/// Must be initialized via Initialize() with data from the FAA Aircraft
/// Characteristics Database before use. Falls back to category defaults
/// when an unknown type is encountered.
/// </summary>
public static class AircraftApproachSpeed
{
    private static Dictionary<string, int> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(Dictionary<string, int> lookup)
    {
        _lookup = new Dictionary<string, int>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get approach speed (knots) for an aircraft type designator. Returns null if unknown.</summary>
    public static int? GetApproachSpeed(string? aircraftType)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return null;
        }

        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _lookup.TryGetValue(baseType, out var speed) ? speed : null;
    }
}
