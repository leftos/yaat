namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Uncontrolled final-approach speed schedule — what a pilot flies on a long straight-in when ATC has
/// issued no speed. Stages are additive on Vref the way airline flap schedules are (Boeing FCTM:
/// flaps UP ≈ Vref+70, flaps 5 ≈ Vref+30…+50, landing flaps = Vref), not multiplicative, so a heavy
/// and a regional jet stay a realistic distance apart instead of splaying to 250 kt for the heavy.
/// <list type="bullet">
/// <item><b>Clean</b> (<see cref="CleanSpeedKts"/>): Vref+70 for jets, capped by wake class (240 heavy / 220 large /
/// 210 regional), Vref+55 capped 200 for turboprops, Vref+35 capped 120 for pistons, ±5 kt per-aircraft jitter.
/// Held from a long-final spawn until the approach-flap stage. Supported by 7110.65 §5-7-1.a.3(d) (keep
/// aircraft clean as long as circumstances permit) and the 210-kt jet floor in §5-7-3.c / AIM 4-4-12.</item>
/// <item><b>Approach flap</b> (<see cref="ApproachFlapSpeedKts"/>): max(1.3·Vref, min(clean−25, Vref+45)), reached
/// by <see cref="ApproachFlapReachGateNm"/> — 9 ± 1.5 nm for jets, 8 ± 1 nm for turboprops; pistons have no
/// distinct stage. Brackets the customary "210 until 10, 180 until 5" without putting every jet at 180 at 10 nm.</item>
/// <item><b>Configuration</b> 1.3·Vref by 5 nm and <b>Vref</b> by 2–5 nm are owned by <see cref="FinalApproachPhase"/>
/// and unchanged.</item>
/// </list>
/// Per-aircraft jitter and the flap reach gate are pure functions of the callsign (no RNG state consumed), the
/// same approach <see cref="FinalApproachSpeedVariety"/> takes for the Vref reach gate.
/// </summary>
public static class FinalApproachSpeedSchedule
{
    public const double JetCleanAdditiveKts = 70;
    public const double TurbopropCleanAdditiveKts = 55;
    public const double PistonCleanAdditiveKts = 35;

    public const double HeavyCleanCapKts = 240;
    public const double LargeJetCleanCapKts = 220;
    public const double RegionalJetCleanCapKts = 210;
    public const double TurbopropCleanCapKts = 200;
    public const double PistonCleanCapKts = 120;

    /// <summary>Per-aircraft clean-speed jitter half-width (kt).</summary>
    public const double CleanJitterKts = 5;

    public const double ApproachFlapAdditiveKts = 45;
    public const double ApproachFlapBelowCleanKts = 25;

    public const double JetFlapGateCenterNm = 9.0;
    public const double JetFlapGateHalfWidthNm = 1.5;
    public const double TurbopropFlapGateCenterNm = 8.0;
    public const double TurbopropFlapGateHalfWidthNm = 1.0;

    /// <summary>Spawn distance at/beyond which an OnFinal aircraft starts at clean speed.</summary>
    public const double CleanSpawnDistanceNm = 10.5;

    /// <summary>Spawn distance beyond which an OnFinal aircraft starts at the approach-flap speed.</summary>
    public const double ApproachFlapSpawnDistanceNm = 6.0;

    /// <summary>Spawn distance beyond which an OnFinal aircraft starts at configuration speed (1.3·Vref).</summary>
    public const double ConfigSpawnDistanceNm = 4.0;

    /// <summary>Short-final spawn additive over Vref — the residual bleed a pilot still carries at 3–4 nm.</summary>
    public const double ShortFinalSpawnAdditiveKts = 8;

    /// <summary>Mirrors <c>FinalApproachPhase.ConfigSpeedMultiplier</c> (1.3·Vref unstabilized-approach gate).</summary>
    public const double ConfigSpeedMultiplier = 1.3;

    /// <summary>
    /// Clean (flaps-up) speed the aircraft holds on a long final before configuring. Never below the
    /// configuration speed so the stages stay monotone for very slow types.
    /// </summary>
    public static double CleanSpeedKts(string aircraftType, AircraftCategory category, double vrefKts, string callsign)
    {
        double additive;
        double cap;
        switch (category)
        {
            case AircraftCategory.Jet:
                additive = JetCleanAdditiveKts;
                cap = JetCleanCapKts(aircraftType, category);
                break;
            case AircraftCategory.Turboprop:
                additive = TurbopropCleanAdditiveKts;
                cap = TurbopropCleanCapKts;
                break;
            default:
                additive = PistonCleanAdditiveKts;
                cap = PistonCleanCapKts;
                break;
        }

        double jitter = ((FinalApproachSpeedVariety.UnitInterval(callsign, "clean") * 2.0) - 1.0) * CleanJitterKts;
        double clean = Math.Min(vrefKts + additive, cap) + jitter;
        return Math.Max(clean, vrefKts * ConfigSpeedMultiplier);
    }

    private static double JetCleanCapKts(string aircraftType, AircraftCategory category)
    {
        var wake = WakeTurbulenceData.WakeClassForType(aircraftType, category);
        if (wake is WakeTurbulenceData.WakeClass.Super or WakeTurbulenceData.WakeClass.Heavy)
        {
            return HeavyCleanCapKts;
        }

        // CWT G is the regional-jet band (CRJ/E-jets); everything else large is a mainline narrowbody.
        return WakeTurbulenceData.GetCwt(aircraftType) == "G" ? RegionalJetCleanCapKts : LargeJetCleanCapKts;
    }

    /// <summary>Approach-flap (≈flaps 5) speed: below clean, above the 1.3·Vref configuration speed.</summary>
    public static double ApproachFlapSpeedKts(double vrefKts, double cleanKts) =>
        Math.Max(vrefKts * ConfigSpeedMultiplier, Math.Min(cleanKts - ApproachFlapBelowCleanKts, vrefKts + ApproachFlapAdditiveKts));

    /// <summary>
    /// Distance from threshold (nm) by which the aircraft is settled at approach-flap speed, with per-aircraft
    /// variety. Null for pistons and helicopters, which have no distinct intermediate stage.
    /// </summary>
    public static double? ApproachFlapReachGateNm(AircraftCategory category, string callsign)
    {
        (double center, double halfWidth) = category switch
        {
            AircraftCategory.Jet => (JetFlapGateCenterNm, JetFlapGateHalfWidthNm),
            AircraftCategory.Turboprop => (TurbopropFlapGateCenterNm, TurbopropFlapGateHalfWidthNm),
            _ => (0.0, 0.0),
        };
        if (center <= 0)
        {
            return null;
        }

        double u = (FinalApproachSpeedVariety.UnitInterval(callsign, "flap") * 2.0) - 1.0;
        return center + (u * halfWidth);
    }

    /// <summary>
    /// The schedule evaluated at a distance from threshold: the speed an aircraft placed on final at
    /// <paramref name="distanceToThresholdNm"/> is already flying. Used for OnFinal spawns and as the
    /// generator in-trail spacing ceiling (a follower is never sped up above its own normal profile).
    /// </summary>
    public static double SpeedAtDistanceKts(
        string aircraftType,
        AircraftCategory category,
        double vrefKts,
        string callsign,
        double distanceToThresholdNm
    )
    {
        if (distanceToThresholdNm >= CleanSpawnDistanceNm)
        {
            return CleanSpeedKts(aircraftType, category, vrefKts, callsign);
        }

        if (distanceToThresholdNm > ApproachFlapSpawnDistanceNm)
        {
            double clean = CleanSpeedKts(aircraftType, category, vrefKts, callsign);
            return ApproachFlapReachGateNm(category, callsign) is null ? clean : ApproachFlapSpeedKts(vrefKts, clean);
        }

        if (distanceToThresholdNm > ConfigSpawnDistanceNm)
        {
            return vrefKts * ConfigSpeedMultiplier;
        }

        return vrefKts + ShortFinalSpawnAdditiveKts;
    }
}
