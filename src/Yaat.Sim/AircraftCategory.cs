namespace Yaat.Sim;

public enum AircraftCategory
{
    Jet,
    Turboprop,
    Piston,
    Helicopter,
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
        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();

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
            AircraftCategory.Helicopter => 5.0,
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
            AircraftCategory.Helicopter => belowTenK ? 1200 : 800,
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
            AircraftCategory.Helicopter => 800,
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
            AircraftCategory.Helicopter => 2.0,
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
            AircraftCategory.Helicopter => 3.0,
            _ => 3.5,
        };
    }

    /// <summary>Ground acceleration during takeoff roll (kts/sec). Helicopter: N/A, vertical liftoff.</summary>
    public static double GroundAccelRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 5.0,
            AircraftCategory.Turboprop => 3.0,
            AircraftCategory.Piston => 2.0,
            AircraftCategory.Helicopter => 2.0,
            _ => 5.0,
        };
    }

    /// <summary>Approximate rotation speed Vr (knots). Helicopter: 0 (vertical liftoff).</summary>
    public static double RotationSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 150,
            AircraftCategory.Turboprop => 110,
            AircraftCategory.Piston => 65,
            AircraftCategory.Helicopter => 0,
            _ => 150,
        };
    }

    /// <summary>Target speed after liftoff (knots). Helicopter: 60 KIAS climb speed.</summary>
    public static double InitialClimbSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 180,
            AircraftCategory.Turboprop => 130,
            AircraftCategory.Piston => 80,
            AircraftCategory.Helicopter => 60,
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
            AircraftCategory.Helicopter => 1200,
            _ => 3000,
        };
    }

    /// <summary>Final approach speed (knots). Helicopter: 70 KIAS.</summary>
    public static double ApproachSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 140,
            AircraftCategory.Turboprop => 110,
            AircraftCategory.Piston => 75,
            AircraftCategory.Helicopter => 70,
            _ => 140,
        };
    }

    /// <summary>Flare initiation altitude (feet AGL). Helicopter: 50ft for hover transition.</summary>
    public static double FlareAltitude(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 30,
            AircraftCategory.Turboprop => 20,
            AircraftCategory.Piston => 15,
            AircraftCategory.Helicopter => 50,
            _ => 30,
        };
    }

    /// <summary>Descent rate during flare (fpm). Helicopter: 150 fpm slow descent to hover.</summary>
    public static double FlareDescentRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 200,
            AircraftCategory.Turboprop => 150,
            AircraftCategory.Piston => 100,
            AircraftCategory.Helicopter => 150,
            _ => 200,
        };
    }

    /// <summary>Speed at touchdown (knots). Helicopter: 0 (hover to touchdown).</summary>
    public static double TouchdownSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 135,
            AircraftCategory.Turboprop => 105,
            AircraftCategory.Piston => 65,
            AircraftCategory.Helicopter => 0,
            _ => 135,
        };
    }

    /// <summary>
    /// Ground braking deceleration during rollout (kts/sec). Models normal commercial operations
    /// (autobrake 2 + moderate reverse thrust) on a long runway — not emergency maximum braking.
    /// Helicopter: 0 (no rollout).
    /// </summary>
    public static double RolloutDecelRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 2.5,
            AircraftCategory.Turboprop => 2.0,
            AircraftCategory.Piston => 1.5,
            AircraftCategory.Helicopter => 0,
            _ => 2.5,
        };
    }

    /// <summary>Traffic pattern altitude above field (feet AGL). Helicopter: 500ft per AIM §4-3-3.</summary>
    public static double PatternAltitudeAgl(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1500,
            AircraftCategory.Turboprop => 1000,
            AircraftCategory.Piston => 1000,
            AircraftCategory.Helicopter => 500,
            _ => 1500,
        };
    }

    /// <summary>Downwind offset from runway centerline (nm). Helicopter: 0.5nm (tighter per AIM).</summary>
    public static double PatternSizeNm(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1.5,
            AircraftCategory.Turboprop => 1.0,
            AircraftCategory.Piston => 0.75,
            AircraftCategory.Helicopter => 0.5,
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
            AircraftCategory.Helicopter => 0.2,
            _ => 0.75,
        };
    }

    /// <summary>Distance past threshold abeam on downwind before base turn (nm).</summary>
    public static double BaseExtensionNm(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 2.5,
            AircraftCategory.Turboprop => 1.5,
            AircraftCategory.Piston => 1.0,
            AircraftCategory.Helicopter => 0.75,
            _ => 2.5,
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
            AircraftCategory.Helicopter => 70,
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
            AircraftCategory.Helicopter => 60,
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
            AircraftCategory.Helicopter => 6.0,
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
            AircraftCategory.Helicopter => 500,
            _ => 1000,
        };
    }

    /// <summary>Touch-and-go rollout duration (seconds). Helicopter: brief hover transition.</summary>
    public static double TouchAndGoRolloutSeconds(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 4.0,
            AircraftCategory.Turboprop => 4.0,
            AircraftCategory.Piston => 3.0,
            AircraftCategory.Helicopter => 3.0,
            _ => 4.0,
        };
    }

    /// <summary>Stop-and-go pause duration at zero speed (seconds). Helicopter: hover pause.</summary>
    public static double StopAndGoPauseSeconds(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 5.0,
            AircraftCategory.Turboprop => 4.0,
            AircraftCategory.Piston => 3.0,
            AircraftCategory.Helicopter => 3.0,
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
            AircraftCategory.Helicopter => 30,
            _ => 100,
        };
    }

    /// <summary>Minimum groundspeed (kts) for a rejected landing / go-around. Helicopter: 0 (can always go around).</summary>
    public static double RejectedLandingMinSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 60,
            AircraftCategory.Turboprop => 50,
            AircraftCategory.Piston => 40,
            AircraftCategory.Helicopter => 0,
            _ => 60,
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

    /// <summary>Taxi ground speed on straight segments (knots). TaxiingPhase reduces for turns. Helicopter: wheeled ground taxi at 15 kts.</summary>
    public static double TaxiSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 30,
            AircraftCategory.Turboprop => 25,
            AircraftCategory.Piston => 20,
            AircraftCategory.Helicopter => 15,
            _ => 30,
        };
    }

    /// <summary>Pushback speed (knots). All categories reverse at ~5 kts.</summary>
    public static double PushbackSpeed(AircraftCategory cat)
    {
        _ = cat;
        return 5;
    }

    /// <summary>Pushback turn rate (deg/sec). Tug-steered, slower than self-powered taxi. Flat across categories.</summary>
    public static double PushbackTurnRate(AircraftCategory cat)
    {
        _ = cat;
        return 5;
    }

    /// <summary>
    /// Ground turn rate while taxiing (deg/sec).
    /// Calibrated at 15 kt corner speed: jet → 22 m radius (B737/A320 outer main gear sweep),
    /// turboprop → 18 m (ATR-72/CRJ-700), piston → 13 m (C172).
    /// </summary>
    public static double GroundTurnRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 20,
            AircraftCategory.Turboprop => 25,
            AircraftCategory.Piston => 35,
            AircraftCategory.Helicopter => 30,
            _ => 20,
        };
    }

    /// <summary>Target speed (kts) when executing a taxiway turn of 90° or more.</summary>
    public static double TaxiCornerSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 15,
            AircraftCategory.Turboprop => 15,
            AircraftCategory.Piston => 20,
            AircraftCategory.Helicopter => 10,
            _ => 15,
        };
    }

    /// <summary>Taxi acceleration rate (kts/sec).</summary>
    public static double TaxiAccelRate(AircraftCategory cat)
    {
        _ = cat;
        return 3;
    }

    /// <summary>Taxi deceleration rate (kts/sec). Raised from 3 to match new taxi speeds: stopping
    /// distance from 30 kts = 30²/(2×5×3600) ≈ 0.025 nm, within look-ahead braking range.</summary>
    public static double TaxiDecelRate(AircraftCategory cat)
    {
        _ = cat;
        return 5;
    }

    /// <summary>Speed when exiting runway onto taxiway (knots). Fallback when exit angle is unknown.</summary>
    public static double RunwayExitSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 25,
            AircraftCategory.Turboprop => 22,
            AircraftCategory.Piston => 18,
            AircraftCategory.Helicopter => 15,
            _ => 25,
        };
    }

    /// <summary>Turn-off speed for high-speed exits (≤45° from runway heading).</summary>
    public static double HighSpeedExitSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 30,
            AircraftCategory.Turboprop => 25,
            AircraftCategory.Piston => 18,
            AircraftCategory.Helicopter => 15,
            _ => 30,
        };
    }

    /// <summary>Turn-off speed for standard exits (>45° from runway heading).</summary>
    public static double StandardExitSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 15,
            AircraftCategory.Turboprop => 15,
            AircraftCategory.Piston => 12,
            AircraftCategory.Helicopter => 10,
            _ => 15,
        };
    }

    /// <summary>Minimum speed maintained during rollout before the kinematic braking point for an assigned exit.</summary>
    public static double RolloutCoastSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 40,
            AircraftCategory.Turboprop => 35,
            AircraftCategory.Piston => 25,
            AircraftCategory.Helicopter => 15,
            _ => 40,
        };
    }

    /// <summary>
    /// Returns the angle-dependent exit turn-off speed. Uses high-speed threshold for
    /// exits ≤45° from runway heading, standard threshold for steeper exits.
    /// Falls back to <see cref="RunwayExitSpeed"/> when exit angle is unknown.
    /// </summary>
    public static double ExitTurnOffSpeed(AircraftCategory cat, double? exitAngleDeg)
    {
        if (exitAngleDeg is null)
        {
            return RunwayExitSpeed(cat);
        }

        return exitAngleDeg.Value <= 45.0 ? HighSpeedExitSpeed(cat) : StandardExitSpeed(cat);
    }

    /// <summary>Speed when crossing a runway (knots).</summary>
    public static double RunwayCrossingSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 15,
            AircraftCategory.Turboprop => 15,
            AircraftCategory.Piston => 12,
            AircraftCategory.Helicopter => 10,
            _ => 15,
        };
    }

    public static double DefaultSpeed(AircraftCategory cat, double altitude)
    {
        return cat switch
        {
            AircraftCategory.Jet => altitude switch
            {
                < 10000 => 250, // 14 CFR 91.117
                < 18000 => 280, // Transition through Class B/C
                < 28000 => 290, // Standard climb
                _ => 280, // High altitude (Mach transition region)
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
            AircraftCategory.Helicopter => altitude switch
            {
                < 10000 => 100,
                _ => 120,
            },
            _ => 250,
        };
    }

    /// <summary>Type-aware default speed: scales category speed by the aircraft's approach speed ratio.</summary>
    public static double DefaultSpeed(AircraftCategory cat, double altitude, string? aircraftType)
    {
        return ScaleByApproachRatio(DefaultSpeed(cat, altitude), cat, aircraftType);
    }

    /// <summary>Type-aware approach speed: uses FAA ACD value if available, else category default.</summary>
    public static double ApproachSpeed(AircraftCategory cat, string? aircraftType)
    {
        var typeSpeed = AircraftApproachSpeed.GetApproachSpeed(aircraftType);
        return typeSpeed ?? ApproachSpeed(cat);
    }

    /// <summary>Type-aware touchdown speed, scaled proportionally to approach speed ratio.</summary>
    public static double TouchdownSpeed(AircraftCategory cat, string? aircraftType)
    {
        return ScaleByApproachRatio(TouchdownSpeed(cat), cat, aircraftType);
    }

    /// <summary>Type-aware downwind speed, scaled proportionally to approach speed ratio.</summary>
    public static double DownwindSpeed(AircraftCategory cat, string? aircraftType)
    {
        return ScaleByApproachRatio(DownwindSpeed(cat), cat, aircraftType);
    }

    /// <summary>Type-aware base speed, scaled proportionally to approach speed ratio.</summary>
    public static double BaseSpeed(AircraftCategory cat, string? aircraftType)
    {
        return ScaleByApproachRatio(BaseSpeed(cat), cat, aircraftType);
    }

    private static double ScaleByApproachRatio(double categoryValue, AircraftCategory cat, string? aircraftType)
    {
        var typeSpeed = AircraftApproachSpeed.GetApproachSpeed(aircraftType);
        if (typeSpeed is null)
        {
            return categoryValue;
        }

        double catSpeed = ApproachSpeed(cat);
        return catSpeed > 0 ? categoryValue * (typeSpeed.Value / catSpeed) : categoryValue;
    }

    /// <summary>Air taxi speed (knots). §3-11-1.c: above 20 KIAS, below 100ft AGL. 40 KIAS nominal.</summary>
    public static double AirTaxiSpeed(AircraftCategory cat)
    {
        _ = cat;
        return 40;
    }

    /// <summary>Air taxi altitude above field (feet AGL). §3-11-1.c: below 100ft AGL.</summary>
    public static double AirTaxiAltitudeAgl(AircraftCategory cat)
    {
        _ = cat;
        return 50;
    }
}
