namespace Yaat.Sim;

/// <summary>
/// Maps ICAO aircraft type designators to CWT (Cooperative Wake Turbulence) codes (A-I).
/// Must be initialized via Initialize() with data from AircraftCwt.json before use.
/// Replaces the previous RECAT WTG (A-G) system with the FAA's CWT classification.
/// </summary>
public static class WakeTurbulenceData
{
    private static Dictionary<string, string> _cwtLookup = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(Dictionary<string, string> lookup)
    {
        _cwtLookup = new Dictionary<string, string>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get CWT code (A-I) for an aircraft type designator. Returns null if unknown.</summary>
    public static string? GetCwt(string aircraftType)
    {
        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _cwtLookup.TryGetValue(baseType, out var cwt) && !string.IsNullOrEmpty(cwt) ? cwt : null;
    }

    /// <summary>
    /// Max visual detection range (nm) for a target aircraft based on its CWT size category.
    /// CWT A (Super, e.g. A388) has the largest visual signature; CWT I (Small, e.g. C172) the smallest.
    /// Falls back to AircraftCategory when CWT is unknown.
    /// </summary>
    public static double TrafficDetectionRangeNm(string aircraftType, AircraftCategory fallbackCategory)
    {
        var cwt = GetCwt(aircraftType);
        if (cwt is not null)
        {
            return cwt switch
            {
                "A" => 15.0, // Super (A388): 262ft wingspan
                "B" => 12.0, // Upper Heavy (B744, B77W): 195-225ft
                "C" => 10.0, // Lower Heavy (B763, A332, B788): 156-198ft
                "D" => 8.0, // B757: 124ft wingspan, 155ft fuselage
                "E" => 8.0, // Large Low (DC85, IL76): 148-165ft wingspan
                "F" => 7.0, // Upper Medium (B738, A320): 112-118ft
                "G" => 5.0, // Lower Medium (CRJ7, E170): 72-94ft
                "H" => 3.5, // Upper Small (C208, PC12): 52-58ft
                "I" => 2.5, // Small (C172, PA28): 36-39ft
                _ => 7.0,
            };
        }

        return fallbackCategory switch
        {
            AircraftCategory.Jet => 7.0,
            AircraftCategory.Turboprop => 5.0,
            AircraftCategory.Piston => 2.5,
            AircraftCategory.Helicopter => 2.5,
            _ => 7.0,
        };
    }
}
