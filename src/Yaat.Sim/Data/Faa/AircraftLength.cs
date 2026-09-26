namespace Yaat.Sim.Data.Faa;

/// <summary>FAA database length, falling back to the CWT bucket length for types the database lacks.</summary>
public static class AircraftLength
{
    public static double ResolveFt(string aircraftType) => FaaAircraftDatabase.Get(aircraftType)?.LengthFt ?? CwtFallbackLengthFt(aircraftType);

    /// <summary>
    /// Estimates aircraft fuselage length (ft) from CWT code when FAA ACD data is unavailable.
    /// </summary>
    public static double CwtFallbackLengthFt(string? aircraftType)
    {
        string? cwt = WakeTurbulenceData.GetCwt(aircraftType ?? "");
        return cwt switch
        {
            "A" => 250.0, // Super (A388)
            "B" => 220.0, // Upper Heavy (B744, B77W)
            "C" => 200.0, // Lower Heavy (B763, A332, B788)
            "D" => 155.0, // B757
            "E" => 130.0, // Large Low (DC85, IL76)
            "F" => 110.0, // Upper Medium (B738, A320)
            "G" => 80.0, // Lower Medium (CRJ7, E170)
            "H" => 60.0, // Upper Small (C208, PC12)
            "I" => 40.0, // Small (C172, PA28)
            _ => 80.0, // Unknown — assume medium
        };
    }
}
