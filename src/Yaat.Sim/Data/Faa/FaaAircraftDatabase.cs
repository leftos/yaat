namespace Yaat.Sim.Data.Faa;

/// <summary>
/// Static lookup for FAA Aircraft Characteristics Database records by ICAO type designator.
/// Initialized once at startup from the downloaded/cached FAA ACD data.
/// </summary>
public static class FaaAircraftDatabase
{
    private static Dictionary<string, FaaAircraftRecord> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsInitialized => _lookup.Count > 0;

    public static int Count => _lookup.Count;

    public static void Initialize(Dictionary<string, FaaAircraftRecord> lookup)
    {
        _lookup = new Dictionary<string, FaaAircraftRecord>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the full FAA ACD record for an ICAO type designator.
    /// Strips prefixes like "H/" and suffixes like "/L" automatically.
    /// </summary>
    public static FaaAircraftRecord? Get(string? aircraftType)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return null;
        }

        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _lookup.TryGetValue(baseType, out var record) ? record : null;
    }
}
