namespace Yaat.Sim;

/// <summary>
/// Maps ICAO aircraft type designators to FAA wake turbulence group (WTG) codes.
/// Must be initialized via Initialize() with data from AircraftSpecs.json before use.
/// </summary>
public static class WakeTurbulenceData
{
    private static Dictionary<string, string> _wtgLookup = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(Dictionary<string, string> lookup)
    {
        _wtgLookup = new Dictionary<string, string>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get WTG code (A-F) for an aircraft type designator. Returns null if unknown.</summary>
    public static string? GetWtg(string aircraftType)
    {
        var baseType = aircraftType.Contains('/') ? aircraftType.Split('/')[0] : aircraftType;
        baseType = baseType.Trim().ToUpperInvariant();
        return _wtgLookup.TryGetValue(baseType, out var wtg) && !string.IsNullOrEmpty(wtg) ? wtg : null;
    }

    /// <summary>
    /// Max visual detection range (nm) for a target aircraft based on its WTG size.
    /// WTG A (Super, e.g. A388) has the largest visual signature; WTG F (Small, e.g. C172) the smallest.
    /// Falls back to AircraftCategory when WTG is unknown.
    /// </summary>
    public static double TrafficDetectionRangeNm(string aircraftType, AircraftCategory fallbackCategory)
    {
        var wtg = GetWtg(aircraftType);
        if (wtg is not null)
        {
            return wtg switch
            {
                "A" => 15.0, // Super (A388): 262ft wingspan
                "B" => 12.0, // Upper Heavy (B744, B77W): 195-213ft
                "C" => 10.0, // Lower Heavy (B763, A332): 156-198ft
                "D" => 8.0, // Upper Large (B738, A320): 112-124ft
                "E" => 6.0, // Lower Large (E170, CRJ9): 72-86ft
                "F" => 3.0, // Small (C172, PA28): 36-58ft
                _ => 8.0,
            };
        }

        return fallbackCategory switch
        {
            AircraftCategory.Jet => 8.0,
            AircraftCategory.Turboprop => 5.0,
            AircraftCategory.Piston => 3.0,
            AircraftCategory.Helicopter => 3.0,
            _ => 8.0,
        };
    }
}
