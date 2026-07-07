using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

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

        if (_lookup.TryGetValue(baseType, out var cat))
        {
            return cat;
        }

        if (AircraftSiblingMap.TryResolve(baseType, out var sibling) && _lookup.TryGetValue(sibling, out var sibCat))
        {
            return sibCat;
        }

        return AircraftCategory.Jet;
    }

    /// <summary>
    /// True when <paramref name="aircraftType"/> is a known ICAO type designator in the specs
    /// lookup (directly or via its sibling), regardless of category. Used to recognize all-letter
    /// type overrides (e.g. "PUMA", "GAZL") in the ADD command without misreading airport ICAOs
    /// or fix names (e.g. "KOAK") as aircraft types.
    /// </summary>
    public static bool IsKnownType(string aircraftType)
    {
        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        if (_lookup.ContainsKey(baseType))
        {
            return true;
        }

        return AircraftSiblingMap.TryResolve(baseType, out var sibling) && _lookup.ContainsKey(sibling);
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

    /// <summary>
    /// Along-runway distance (feet) past the threshold that the final-approach descent target aims
    /// for, so the aircraft touches down at a realistic aiming point instead of on the threshold.
    /// Slow aircraft float little in the flare and would otherwise touch down essentially on the
    /// threshold; aiming the glidepath down the runway makes a light piston cross the threshold
    /// slightly high (~20 ft) and land near the numbers (~500 ft, FAA-H-8083-3 technique).
    /// Turboprops float even less than jets, so they need the largest offset to reach the touchdown
    /// zone (~450 ft → ~850-1,000 ft touchdown). Jets already float ~1,700 ft down the flare to a
    /// realistic touchdown zone, so they get no offset. Helicopter: 0 (vertical descent).
    /// </summary>
    public static double LandingAimPointOffsetFt(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 0,
            AircraftCategory.Turboprop => 450,
            AircraftCategory.Piston => 400,
            AircraftCategory.Helicopter => 0,
            _ => 0,
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
        // Piston: 2.5 kts/s ≈ 0.13 g. Matches routine C172/PA28 braking on a dry runway
        // (AIM 4-3-21, POH short-field max ≈ 0.25 g). ComfortableBrakingMultiplier×this
        // becomes the ceiling used by LandingPhase exit planning.
        return cat switch
        {
            AircraftCategory.Jet => 2.5,
            AircraftCategory.Turboprop => 2.0,
            AircraftCategory.Piston => 2.5,
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

    /// <summary>
    /// Compressed past-abeam extension when "make short approach" is armed for an
    /// upcoming downwind leg. Pilots tighten the back of the pattern by turning base
    /// near abeam-the-threshold rather than at the normal extension. AIM 4-3-3 allows
    /// the pilot to vary pattern size; AIM FIG 4-3-2 requires the turn to final to
    /// complete at least 1/4 mile from the runway, so values stay positive.
    /// </summary>
    public static double ShortApproachBaseExtensionNm(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 0.3,
            AircraftCategory.Turboprop => 0.2,
            AircraftCategory.Piston => 0.15,
            AircraftCategory.Helicopter => 0.1,
            _ => 0.3,
        };
    }

    /// <summary>
    /// Minimum final-approach length after a "make short approach" base turn (nm).
    /// Floors the request so the resulting profile remains stabilized at the realistic
    /// approach speed for the category. AIM FIG 4-3-2 publishes a 1/4-mile minimum
    /// (helicopter floor); jet/turboprop need more room to be at the stabilized-approach
    /// gate (~500 ft AGL) on a 3° glideslope.
    /// </summary>
    public static double MinShortApproachFinalNm(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 1.5,
            AircraftCategory.Turboprop => 1.0,
            AircraftCategory.Piston => 0.5,
            AircraftCategory.Helicopter => 0.25,
            _ => 1.5,
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

    /// <summary>
    /// Touch-and-go rollout duration (seconds). The pilot reduces flaps, retrims and
    /// applies takeoff power during this window; reacceleration to Vr begins after.
    /// Helicopter: brief hover transition.
    /// </summary>
    public static double TouchAndGoRolloutSeconds(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 10.0,
            AircraftCategory.Turboprop => 8.0,
            AircraftCategory.Piston => 6.0,
            AircraftCategory.Helicopter => 3.0,
            _ => 10.0,
        };
    }

    /// <summary>
    /// Stop-and-go pause duration at zero speed (seconds). Models the brief hold
    /// at full stop before throttling up for the next takeoff. Helicopter: hover pause.
    /// </summary>
    public static double StopAndGoPauseSeconds(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 10.0,
            AircraftCategory.Turboprop => 7.0,
            AircraftCategory.Piston => 5.0,
            AircraftCategory.Helicopter => 3.0,
            _ => 10.0,
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

    /// <summary>
    /// Multiplier applied to <see cref="TaxiSpeed"/> when the aircraft has been
    /// instructed to expedite taxi. Bumps the straight-line cap by ~30%
    /// (jet 30→39, turboprop 25→32.5, piston 20→26, helo 15→19.5). Corner
    /// speeds are unchanged — turn geometry still governs deceleration.
    /// </summary>
    public const double TaxiExpediteMultiplier = 1.3;

    /// <summary>
    /// Lowest taxi speed (kts) a controller may command via <c>SPD &lt;n&gt;</c>. Matches the
    /// deliberate-slow-but-still-moving pace of <see cref="PushbackSpeed"/> and sits above the
    /// tightest-arc creep floor (<see cref="SlowTurnSpeedKts"/>), which is reserved for turn
    /// geometry rather than a straight-line cruise cap. Aviation-reviewed.
    /// </summary>
    public const double MinCommandedTaxiSpeedKts = 5.0;

    /// <summary>
    /// Highest taxi speed (kts) a controller may command via <c>SPD &lt;n&gt;</c> — the same
    /// ceiling as expedite taxi (<see cref="TaxiSpeed"/> × <see cref="TaxiExpediteMultiplier"/>,
    /// rounded to whole knots: jet 39, turboprop 33, piston 26, helo 20). Keeps the numeric knob a
    /// clean superset of <c>EXP</c> with no unfounded faster-than-expedite regime. Aviation-reviewed.
    /// </summary>
    public static double MaxCommandedTaxiSpeed(AircraftCategory cat) =>
        Math.Round(TaxiSpeed(cat) * TaxiExpediteMultiplier, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Max-effort braking deceleration (kts/s) used when a runway exit is
    /// expedited (<c>EXP</c>) — above the firm 5 kts/s used for normal explicit
    /// exits. Lets LandingPhase take the earliest reachable exit and lets
    /// RunwayExitPhase brake firmly to the hold-short stop after the turn-off.
    /// Category-specific (aviation-reviewed): jets have anti-skid / autobrake so
    /// ~0.39 g is firm-but-normal on dry; lighter types come down to avoid
    /// modeling a skid (no anti-skid on most pistons), and a helicopter
    /// running-landing rollout barely wheel-brakes at all.
    /// </summary>
    public static double ExpediteExitDecelRate(AircraftCategory cat) =>
        cat switch
        {
            AircraftCategory.Jet => 7.5, // ~0.39 g, autobrake-MAX / firm manual, dry
            AircraftCategory.Turboprop => 6.0, // ~0.31 g, beta/reverse + brakes, mostly no anti-skid
            AircraftCategory.Piston => 5.0, // ~0.26 g, controlled hard brake, no anti-skid (avoid skid)
            AircraftCategory.Helicopter => 4.0, // running-landing rollout; wheel braking barely applies
            _ => 7.5,
        };

    /// <summary>Pushback speed (knots). All categories reverse at ~5 kts.</summary>
    public static double PushbackSpeed(AircraftCategory cat)
    {
        _ = cat;
        return 5;
    }

    /// <summary>
    /// Forward alignment creep speed (knots) for the final pull onto a ramp spot. Final stand/spot
    /// positioning ("docking") is done at walking pace for precision, slower than the reverse leg.
    /// </summary>
    public static double PushbackAlignSpeed(AircraftCategory cat)
    {
        _ = cat;
        return 3;
    }

    /// <summary>Pushback turn rate (deg/sec). Tug-steered, slower than self-powered taxi. Flat across categories.</summary>
    public static double PushbackTurnRate(AircraftCategory cat)
    {
        _ = cat;
        return 5;
    }

    /// <summary>
    /// How far past the target taxiway node the aircraft overshoots during pushback (nm).
    /// Simulates the tug pushing and turning simultaneously — larger aircraft need more room
    /// to complete the turn, so the overshoot is bigger.
    /// Uses CWT code (A-I) with fallback to AircraftCategory.
    /// </summary>
    public static double PushbackOvershootNm(string aircraftType)
    {
        var cwt = WakeTurbulenceData.GetCwt(aircraftType);
        if (cwt is not null)
        {
            return cwt switch
            {
                "A" => 0.013, // Super (A388): ~80ft
                "B" => 0.012, // Upper Heavy (B744): ~70ft
                "C" => 0.010, // Lower Heavy (B763): ~60ft
                "D" => 0.008, // Upper Large (B738): ~50ft
                "E" => 0.007, // Lower Large (E170): ~45ft
                "F" => 0.007, // Upper Small (C560): ~40ft
                _ => 0.005, // G-I Small/Light: ~30ft
            };
        }

        var cat = AircraftCategorization.Categorize(aircraftType);
        return cat switch
        {
            AircraftCategory.Jet => 0.008,
            AircraftCategory.Turboprop => 0.007,
            AircraftCategory.Piston => 0.005,
            _ => 0.008,
        };
    }

    /// <summary>
    /// Distance (nm) for a simple pushback (no taxiway/heading target). Floors
    /// at the prior 0.015 nm (~91 ft) baseline so small aircraft are unaffected;
    /// scales up to ~1× aircraft length for jets so the tail clears the gate
    /// envelope. B738 (~110 ft) pushes ~110 ft; A388 (~240 ft) pushes ~240 ft.
    /// </summary>
    public static double SimplePushbackDistanceNm(string aircraftType)
    {
        const double FtPerNm = 6076.12;
        const double BaselineNm = 0.015;

        double lengthFt;
        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record?.LengthFt is { } len && len > 0)
        {
            lengthFt = len;
        }
        else
        {
            var cwt = WakeTurbulenceData.GetCwt(aircraftType);
            lengthFt = cwt switch
            {
                "A" => 240, // Super (A388)
                "B" => 230, // Upper Heavy (B744)
                "C" => 180, // Lower Heavy (B763)
                "D" => 110, // Upper Large (B738)
                "E" => 95, // Lower Large (E170)
                "F" => 50, // Upper Small (C560)
                _ => 30, // G-I Small/Light (C172)
            };
        }

        return Math.Max(BaselineNm, lengthFt / FtPerNm);
    }

    /// <summary>
    /// Maximum ground yaw rate while taxiing (deg/sec) — the gear/tiller-limited ceiling, reached only
    /// once the aircraft is rolling faster than the low crossover speed (~3 kt) where v/R at the tight
    /// nose-wheel radius meets it. Below that, achievable yaw is v/R-limited (see
    /// <see cref="GroundYawRateAtSpeed"/>): nose-wheel steering sets path curvature 1/R, so heading
    /// change per unit distance is fixed and heading change per unit time is ω = v/R — an aircraft
    /// creeping at walking pace cannot slew its nose at the full rate. Values reflect real ground
    /// handling: jets pivot ponderously (~12 °/s at max tiller),
    /// turboprops tighter, light singles briskly (~20 °/s full rudder + differential brake). A 120°
    /// turn then takes ~6 s (piston) to ~10 s (jet). Helicopters keep the hover pedal-turn rate
    /// (their air-taxi rate is separately speed-blended in <c>AirTaxiPhase</c>).
    ///
    /// Governing invariant: every traced arc radius R ≥ <see cref="SlowTurnSpeedKts"/> / this rate,
    /// so the ceiling is actually reachable at creep speed without the slow-turn floor re-inflating
    /// the yaw past it (see <see cref="NoseWheelTurnRadiusFt"/>).
    /// </summary>
    public static double GroundTurnRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 12,
            AircraftCategory.Turboprop => 16,
            AircraftCategory.Piston => 20,
            AircraftCategory.Helicopter => 30,
            _ => 12,
        };
    }

    /// <summary>
    /// Ground yaw rate (deg/sec) achievable at groundspeed <paramref name="groundSpeedKts"/>: ω = v/R
    /// at the tightest steerable (nose-wheel) radius <see cref="NoseWheelTurnRadiusFt"/>, capped at the
    /// gear-limited <see cref="GroundTurnRate"/> ceiling. The aircraft can steer to its minimum radius,
    /// so it reaches the ceiling at a low speed (~3 kt) and keeps full corner / pure-pursuit re-acquire
    /// authority above that; only below it does the rate fall off (a near-stationary aircraft cannot
    /// slew its nose fast — at 1 kt a piston manages ~6 °/s, not the ceiling). Using the tight radius
    /// rather than a wide routine-taxi radius is deliberate: the navigator's low-speed re-acquire needs
    /// the tight-radius authority, and throttling it to a wide-radius rate makes the aircraft orbit
    /// ramp-connector fillets. Helicopters are exempt — a wheeled ground pivot is a pedal turn, so it
    /// holds the full hover rate at any speed.
    /// </summary>
    public static double GroundYawRateAtSpeed(AircraftCategory cat, double groundSpeedKts)
    {
        double ceiling = GroundTurnRate(cat);
        if (cat == AircraftCategory.Helicopter)
        {
            return ceiling;
        }

        double vFtPerSec = groundSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double yawRateDegPerSec = (vFtPerSec / NoseWheelTurnRadiusFt(cat)) * (180.0 / Math.PI);
        return Math.Min(ceiling, yawRateDegPerSec);
    }

    /// <summary>
    /// Nose-gear minimum turn radius (ft) used when a lineup-onto-runway
    /// maneuver builds its pivot arc. Sized to match real aircraft ground-ops
    /// geometry so <c>v/r</c> (tangent rotation rate) stays well under
    /// <see cref="GroundTurnRate"/> at realistic LUAW speeds — i.e. the closed-
    /// form arc integrator in a lineup-plan playback always has turn-rate
    /// headroom for error correction.
    ///
    /// Reference values: Boeing 737 nose-gear minimum turn radius ≈ 56 ft
    /// per the 737-800 ACAP §5; we round up for wake-turbulence padding and
    /// wingtip clearance on corner-rounded taxiway approaches. Piston singles
    /// can pivot much tighter but the value here is what we actually trace
    /// for realism + stability (not the physical minimum).
    /// </summary>
    public static double LineUpTurnRadiusFt(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 70.0,
            AircraftCategory.Turboprop => 50.0,
            AircraftCategory.Piston => 25.0,
            AircraftCategory.Helicopter => 25.0,
            _ => 70.0,
        };
    }

    /// <summary>
    /// Minimum ground-turn radius (ft) achievable at full nose-wheel deflection
    /// at very low forward speed. Derived from approximate aircraft wheelbase
    /// and max nose-wheel deflection angle (typical jets: 54 ft wheelbase × 65°
    /// deflection ≈ 25 ft; narrower for smaller categories). Used by the
    /// <see cref="PathPrimitiveKind.SlowTurn"/> primitive for tight programmatic
    /// maneuvers (lineup pivots). Roughly ⅓ of <see cref="LineUpTurnRadiusFt"/>.
    ///
    /// Each value must satisfy R ≥ <see cref="SlowTurnSpeedKts"/> / <see cref="GroundTurnRate"/> so
    /// that a slow-turn played at the speed floor does not yaw faster than the category ceiling
    /// (piston 15 ft = 3 kt / 20 °/s; a light single swings a realistic ~15 ft arc out of a spot
    /// rather than full-lock pivoting on the nose wheel).
    /// </summary>
    public static double NoseWheelTurnRadiusFt(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 25.0,
            AircraftCategory.Turboprop => 18.0,
            AircraftCategory.Piston => 15.0,
            AircraftCategory.Helicopter => 10.0,
            _ => 25.0,
        };
    }

    /// <summary>
    /// Tightest defensible nose-gear turn radius (ft) for a deliberate, brake-assisted, walking-pace pivot
    /// at a geometrically tight junction — below the comfortable <see cref="NoseWheelTurnRadiusFt"/> but at or
    /// above the inner-main-gear radius (tighter would pivot on a near-locked inner main gear). A jet floors at
    /// ~15 ft (≈ B737-800 inner-main-gear radius); smaller categories scale down. Used to clamp the adaptive
    /// corner-rounding radius when the available approach straight is shorter than the comfortable tangent
    /// length (aviation-reviewed: Boeing FCTM tight-turn technique / judgmental oversteer, AC 150/5300-13B).
    /// </summary>
    public static double TightTurnFloorRadiusFt(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 15.0,
            AircraftCategory.Turboprop => 12.0,
            AircraftCategory.Piston => 8.0,
            AircraftCategory.Helicopter => 8.0,
            _ => 15.0,
        };
    }

    /// <summary>
    /// Fastest ground speed (knots) at which the gear-limited <see cref="GroundTurnRate"/> can still
    /// track a turn of <paramref name="radiusFt"/> — i.e. <c>v = ω·r</c>. Above this the required yaw
    /// rate exceeds the nose-wheel steering rate and the nose can no longer follow the arc. Floored at
    /// <see cref="SlowTurnSpeedKts"/> for degenerate-small radii. Used to set the playback speed of
    /// nose-wheel-radius corner-rounding / entry-alignment arcs: rounding a sharp corner at the
    /// nose-wheel minimum radius settles a jet near ~5 kt — a realistic sharp-taxiway-turn / spot-exit
    /// speed (Boeing FCTM, AC 120-74), not the 3 kt low-visibility (SMGCS) creep the flat floor implied.
    /// </summary>
    public static double TurnRateLimitedSpeedKts(AircraftCategory cat, double radiusFt)
    {
        double omegaRadPerSec = GroundTurnRate(cat) * (Math.PI / 180.0);
        double vKts = omegaRadPerSec * radiusFt * 3600.0 / GeoMath.FeetPerNm;
        return Math.Max(vKts, SlowTurnSpeedKts);
    }

    /// <summary>
    /// Slowest defensible forward speed (knots) during a <see cref="PathPrimitiveKind.SlowTurn"/>
    /// primitive — a walking-pace creep for the very tightest geometry and low-visibility (SMGCS) taxi.
    /// Normal sharp-corner rounding runs faster, at <see cref="TurnRateLimitedSpeedKts"/>; this is the
    /// floor, not the default.
    /// </summary>
    public const double SlowTurnSpeedKts = 3.0;

    /// <summary>Target speed (kts) when executing a taxiway turn of 90° or more.</summary>
    public static double TaxiCornerSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 15,
            AircraftCategory.Turboprop => 15,
            AircraftCategory.Piston => 10,
            AircraftCategory.Helicopter => 10,
            _ => 15,
        };
    }

    /// <summary>Target speed (kts) for very tight taxiway turns (150°+ reversal). Near crawl speed.</summary>
    public static double TaxiTightCornerSpeed(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 8,
            AircraftCategory.Turboprop => 8,
            AircraftCategory.Piston => 10,
            AircraftCategory.Helicopter => 5,
            _ => 8,
        };
    }

    /// <summary>
    /// Angle-dependent taxi speed for turns at taxiway nodes.
    /// 0-30° → TaxiSpeed (straight), 30-90° → TaxiCornerSpeed (standard turn),
    /// 90-150° → TaxiTightCornerSpeed (sharp turn), 150°+ → TaxiTightCornerSpeed.
    /// </summary>
    public static double CornerSpeedForAngle(AircraftCategory cat, double turnAngleDeg)
    {
        double maxSpeed = TaxiSpeed(cat);
        double cornerSpeed = TaxiCornerSpeed(cat);
        double tightCornerSpeed = TaxiTightCornerSpeed(cat);

        if (turnAngleDeg <= 30.0)
        {
            return maxSpeed;
        }

        if (turnAngleDeg <= 90.0)
        {
            double frac = (turnAngleDeg - 30.0) / 60.0;
            return maxSpeed - ((maxSpeed - cornerSpeed) * frac);
        }

        if (turnAngleDeg <= 150.0)
        {
            double frac = (turnAngleDeg - 90.0) / 60.0;
            return cornerSpeed - ((cornerSpeed - tightCornerSpeed) * frac);
        }

        return tightCornerSpeed;
    }

    /// <summary>Taxi acceleration rate (kts/sec).</summary>
    public static double TaxiAccelRate(AircraftCategory cat)
    {
        _ = cat;
        return 3;
    }

    /// <summary>
    /// Taxi/runway-exit deceleration rate (kts/sec). Used for both post-landing rollout
    /// braking and taxi-speed deceleration. Jets and turboprops have powerful brakes and
    /// sustain 5 kts/s (within autobrake-medium authority). GA pistons with toe brakes
    /// decelerate more gently — 2 kts/s is a realistic "moderate" pedal application on
    /// a C172, and the lower rate widens the brake look-ahead window so the navigator
    /// can stop at hold-short lines without needing the route to snap speed to zero.
    /// </summary>
    public static double TaxiDecelRate(AircraftCategory cat)
    {
        return cat switch
        {
            AircraftCategory.Jet => 5,
            AircraftCategory.Turboprop => 5,
            AircraftCategory.Piston => 2,
            AircraftCategory.Helicopter => 2,
            _ => 5,
        };
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
    /// Returns the angle-dependent exit turn-off speed. ICAO Annex 14 §3.10 and
    /// FAA AC 150/5300-13B Table 4-2 publish 30° as the design intersection angle
    /// for a rapid-exit taxiway, with acute-angle exits up to 45°. Past 46° an
    /// aircraft entering at 30 kts would exceed comfortable lateral acceleration,
    /// so steeper exits use the standard turn-off speed. The 46° boundary
    /// (rather than a strict 45°) absorbs coordinate-precision noise — a
    /// published 45° taxiway commonly measures 45.02° from geojson centerlines.
    /// Falls back to <see cref="RunwayExitSpeed"/> when exit angle is unknown.
    /// </summary>
    public static double ExitTurnOffSpeed(AircraftCategory cat, double? exitAngleDeg)
    {
        if (exitAngleDeg is null)
        {
            return RunwayExitSpeed(cat);
        }

        const double HighSpeedAngleMaxDeg = 46.0;
        return exitAngleDeg.Value <= HighSpeedAngleMaxDeg ? HighSpeedExitSpeed(cat) : StandardExitSpeed(cat);
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
        return 100;
    }

    /// <summary>
    /// Build a category-typical <see cref="AircraftProfile"/> from these constants. Used only as
    /// the base for an <see cref="Data.AircraftProfileOverride"/> targeting a type that has no
    /// profile in AircraftProfiles.json and no sibling (e.g. the SF50): the override supplies the
    /// type-specific corrections and this fills the unspecified fields with sane category defaults.
    /// The caller stamps the real <c>TypeCode</c> via <c>with</c>. These values are reasonable
    /// category averages, not a substitute for a real per-type profile.
    /// </summary>
    public static AircraftProfile BaselineProfile(AircraftCategory cat)
    {
        (double cruiseTas, double cruiseAlt, double ceiling, double initialApproach, double climbFinalMach) = cat switch
        {
            AircraftCategory.Jet => (440, 35000, 41000, 210, 0.74),
            AircraftCategory.Turboprop => (280, 24000, 25000, 180, 0.0),
            AircraftCategory.Piston => (140, 8000, 14000, 110, 0.0),
            AircraftCategory.Helicopter => (120, 0, 12000, 90, 0.0),
            _ => (440, 35000, 41000, 210, 0.74),
        };

        return new AircraftProfile
        {
            TypeCode = "",
            IsProp = cat is AircraftCategory.Piston or AircraftCategory.Turboprop,
            IsHelo = cat is AircraftCategory.Helicopter,
            AirborneAccelRate = AccelRate(cat),
            AirborneDecelRate = DecelRate(cat),
            GroundAccelRate = GroundAccelRate(cat),
            RotateSpeed = RotationSpeed(cat),
            ClimbSpeedInitial = InitialClimbSpeed(cat),
            ClimbSpeedFl150 = DefaultSpeed(cat, 15000),
            ClimbSpeedFl240 = DefaultSpeed(cat, 24000),
            ClimbSpeedFinal = climbFinalMach > 0 ? climbFinalMach : DefaultSpeed(cat, 30000),
            ClimbRateInitial = InitialClimbRate(cat),
            ClimbRateFl150 = ClimbRate(cat, 15000),
            ClimbRateFl240 = ClimbRate(cat, 24000) * 0.6,
            ClimbRateFinal = ClimbRate(cat, 24000) * 0.3,
            CruiseSpeed = cruiseTas,
            CruiseAltitude = cruiseAlt,
            Ceiling = ceiling,
            DescentSpeedInitial = climbFinalMach > 0 ? climbFinalMach : DefaultSpeed(cat, 30000),
            DescentSpeedFl100 = DefaultSpeed(cat, 10000),
            InitialApproachSpeed = initialApproach,
            DescentRateInitial = DescentRate(cat),
            DescentRateFl100 = DescentRate(cat),
            DescentRateApproach = PatternDescentRate(cat),
            FinalApproachSpeed = ApproachSpeed(cat),
            LandingSpeed = TouchdownSpeed(cat),
            PatternSpeed = DownwindSpeed(cat),
            HoldingSpeed = 0,
            StandardTurnRateOverride = 0,
        };
    }
}
