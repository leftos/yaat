namespace Yaat.Sim;

public enum AircraftCategory
{
    Jet,
    Turboprop,
    Piston,
}

/// <summary>
/// Maps ICAO aircraft type designators to category.
/// Must be initialized via Initialize() with data from
/// AircraftSpecs.json before use. Falls back to Jet
/// when an unknown type is encountered.
/// </summary>
public static class AircraftCategorization
{
    private static Dictionary<string, AircraftCategory> _lookup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Populate the lookup from external data (e.g.
    /// AircraftSpecs.json EngineType field). Call once
    /// at startup before any physics simulation runs.
    /// </summary>
    public static void Initialize(Dictionary<string, AircraftCategory> lookup)
    {
        _lookup = new Dictionary<string, AircraftCategory>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    public static AircraftCategory Categorize(string aircraftType)
    {
        var baseType = aircraftType.Contains('/') ? aircraftType.Split('/')[0] : aircraftType;

        baseType = baseType.Trim().ToUpperInvariant();

        return _lookup.TryGetValue(baseType, out var cat) ? cat : AircraftCategory.Jet;
    }
}

/// <summary>
/// Performance constants per aircraft category.
/// Values validated by aviation-sim-expert against
/// AIM / FAA 7110.65 references.
/// </summary>
public static class CategoryPerformance
{
    public static double TurnRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 2.5,
            AircraftCategory.Turboprop => 3.0,
            AircraftCategory.Piston => 3.0,
            _ => 3.0,
        };
    }

    public static double ClimbRate(AircraftCategory cat, double altitude)
    {
        bool belowTenK = altitude < 10000;
        return cat switch
        {
            AircraftCategory.Jet => belowTenK ? 2500 : 1800,
            AircraftCategory.Turboprop => belowTenK ? 1500 : 1200,
            AircraftCategory.Piston => belowTenK ? 700 : 500,
            _ => 1800,
        };
    }

    public static double DescentRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1800,
            AircraftCategory.Turboprop => 1200,
            AircraftCategory.Piston => 500,
            _ => 1800,
        };
    }

    public static double AccelRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 2.5,
            AircraftCategory.Turboprop => 1.5,
            AircraftCategory.Piston => 1.0,
            _ => 2.5,
        };
    }

    public static double DecelRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 3.5,
            AircraftCategory.Turboprop => 2.5,
            AircraftCategory.Piston => 2.0,
            _ => 3.5,
        };
    }

    public static double DefaultSpeed(AircraftCategory cat, double altitude)
    {
        return cat switch
        {
            AircraftCategory.Jet => altitude switch
            {
                < 10000 => 250,
                < 24000 => 290,
                _ => 280,
            },
            AircraftCategory.Turboprop => altitude switch
            {
                < 10000 => 200,
                < 24000 => 250,
                _ => 270,
            },
            AircraftCategory.Piston => altitude switch
            {
                < 10000 => 110,
                _ => 120,
            },
            _ => 250,
        };
    }
}
