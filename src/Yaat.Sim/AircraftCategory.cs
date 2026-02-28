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

    /// <summary>Ground acceleration during takeoff roll (kts/sec).</summary>
    public static double GroundAccelRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 5.0,
            AircraftCategory.Turboprop => 3.0,
            AircraftCategory.Piston => 2.0,
            _ => 5.0,
        };
    }

    /// <summary>Approximate rotation speed Vr (knots).</summary>
    public static double RotationSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 150,
            AircraftCategory.Turboprop => 110,
            AircraftCategory.Piston => 65,
            _ => 150,
        };
    }

    /// <summary>Target speed after liftoff, approximately V2+10 (knots).</summary>
    public static double InitialClimbSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 180,
            AircraftCategory.Turboprop => 130,
            AircraftCategory.Piston => 80,
            _ => 180,
        };
    }

    /// <summary>Climb rate immediately after liftoff (fpm).</summary>
    public static double InitialClimbRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 3000,
            AircraftCategory.Turboprop => 1800,
            AircraftCategory.Piston => 800,
            _ => 3000,
        };
    }

    /// <summary>Final approach speed (Vref + additive, knots).</summary>
    public static double ApproachSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 140,
            AircraftCategory.Turboprop => 110,
            AircraftCategory.Piston => 75,
            _ => 140,
        };
    }

    /// <summary>Flare initiation altitude (feet AGL).</summary>
    public static double FlareAltitude(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 30,
            AircraftCategory.Turboprop => 20,
            AircraftCategory.Piston => 15,
            _ => 30,
        };
    }

    /// <summary>Descent rate during flare (fpm).</summary>
    public static double FlareDescentRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 200,
            AircraftCategory.Turboprop => 150,
            AircraftCategory.Piston => 100,
            _ => 200,
        };
    }

    /// <summary>Speed at touchdown (knots).</summary>
    public static double TouchdownSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 135,
            AircraftCategory.Turboprop => 105,
            AircraftCategory.Piston => 65,
            _ => 135,
        };
    }

    /// <summary>Ground braking deceleration during rollout (kts/sec).</summary>
    public static double RolloutDecelRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 5.0,
            AircraftCategory.Turboprop => 3.5,
            AircraftCategory.Piston => 2.5,
            _ => 5.0,
        };
    }

    /// <summary>Traffic pattern altitude above field (feet AGL).</summary>
    public static double PatternAltitudeAgl(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1500,
            AircraftCategory.Turboprop => 1000,
            AircraftCategory.Piston => 1000,
            _ => 1500,
        };
    }

    /// <summary>Downwind offset from runway centerline (nm).</summary>
    public static double PatternSizeNm(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1.5,
            AircraftCategory.Turboprop => 1.0,
            AircraftCategory.Piston => 0.75,
            _ => 1.5,
        };
    }

    /// <summary>Distance past departure end before crosswind turn (nm).</summary>
    public static double CrosswindExtensionNm(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 0.75,
            AircraftCategory.Turboprop => 0.5,
            AircraftCategory.Piston => 0.3,
            _ => 0.75,
        };
    }

    /// <summary>Distance past threshold abeam on downwind before base turn (nm).</summary>
    public static double BaseExtensionNm(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1.0,
            AircraftCategory.Turboprop => 0.75,
            AircraftCategory.Piston => 0.5,
            _ => 1.0,
        };
    }

    /// <summary>Speed on downwind leg (knots).</summary>
    public static double DownwindSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 200,
            AircraftCategory.Turboprop => 150,
            AircraftCategory.Piston => 90,
            _ => 200,
        };
    }

    /// <summary>Speed on base leg (knots).</summary>
    public static double BaseSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 170,
            AircraftCategory.Turboprop => 130,
            AircraftCategory.Piston => 80,
            _ => 170,
        };
    }

    /// <summary>Turn rate in pattern (deg/sec). Tighter than enroute.</summary>
    public static double PatternTurnRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 3.0,
            AircraftCategory.Turboprop => 4.0,
            AircraftCategory.Piston => 5.0,
            _ => 3.0,
        };
    }

    /// <summary>Descent rate on downwind/base in pattern (fpm).</summary>
    public static double PatternDescentRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1000,
            AircraftCategory.Turboprop => 800,
            AircraftCategory.Piston => 700,
            _ => 1000,
        };
    }

    /// <summary>Touch-and-go rollout duration before reaccelerating (seconds).</summary>
    public static double TouchAndGoRolloutSeconds(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 4.0,
            AircraftCategory.Turboprop => 4.0,
            AircraftCategory.Piston => 3.0,
            _ => 4.0,
        };
    }

    /// <summary>Stop-and-go pause duration at zero speed before takeoff roll (seconds).</summary>
    public static double StopAndGoPauseSeconds(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 5.0,
            AircraftCategory.Turboprop => 4.0,
            AircraftCategory.Piston => 3.0,
            _ => 5.0,
        };
    }

    /// <summary>Low approach go-around initiation altitude (feet AGL).</summary>
    public static double LowApproachAltitudeAgl(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 100,
            AircraftCategory.Turboprop => 75,
            AircraftCategory.Piston => 50,
            _ => 100,
        };
    }

    /// <summary>Maximum holding speed (KIAS) per AIM altitude band.</summary>
    public static double MaxHoldingSpeed(double altitude)
    {
        return altitude switch
        {
            <= 6000 => 200,
            <= 14000 => 230,
            _ => 265,
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
