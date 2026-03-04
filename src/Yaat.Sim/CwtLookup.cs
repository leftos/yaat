namespace Yaat.Sim;

/// <summary>
/// Maps ICAO aircraft type designators to CWT (Cooperative Wake Turbulence) codes.
/// Must be initialized via Initialize() with data from AircraftCwt.json before use.
/// </summary>
public static class CwtLookup
{
    private static Dictionary<string, string> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(Dictionary<string, string> lookup)
    {
        _lookup = new Dictionary<string, string>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get CWT code for an aircraft type designator. Returns empty string if unknown.</summary>
    public static string GetCwt(string aircraftType)
    {
        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _lookup.TryGetValue(baseType, out var cwt) && !string.IsNullOrEmpty(cwt) ? cwt : "";
    }
}
