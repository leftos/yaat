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
            "A" => 240.0, // Super (A388, A225)
            "B" => 220.0, // Upper Heavy (B744, B77W, B788)
            "C" => 185.0, // Lower Heavy (B763, A306, MD11)
            "D" => 185.0, // Non-Pairwise Heavy (IL76, DC85, A124)
            "E" => 155.0, // B757 (B752, B753)
            "F" => 125.0, // Upper Large (B738, A320)
            "G" => 100.0, // Lower Large (CRJ7, E170)
            "H" => 60.0, // Upper Small (B350, B190, BE40)
            "I" => 30.0, // Lower Small (C172, C208, PC12)
            _ => 80.0, // Unknown — assume medium
        };
    }
}
