using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Data;

/// <summary>
/// Corrects AircraftProfile speed and climb values using FAA Aircraft Characteristics
/// Database (ACD) approach speed as ground truth.
///
/// Named "Eurocontrol" because AircraftProfiles.json originates from Eurocontrol BADA
/// (Base of Aircraft Data) performance models. The BADA-derived speeds are systematically
/// ~20% too high for approach speeds compared to FAA ACD certification data. This adapter
/// uses the ACD as an anchor to bring all related speeds closer to reality.
///
/// WHY THIS EXISTS:
/// AircraftProfiles.json contains hand-crafted performance data whose approach speeds
/// are systematically ~20% too high compared to the FAA ACD (which publishes authoritative
/// Vref values). Other profile speeds (pattern, climb, initial approach) inherit similar
/// inaccuracies. Rather than modifying the source JSON (which would lose the original data
/// and make updates harder), this adapter applies corrections at runtime.
///
/// If better per-type performance data becomes available in the future, stop installing
/// this adapter — the <see cref="PassthroughProfileCorrectionAdapter"/> will be used
/// automatically and profile values will be read directly.
///
/// DATA SOURCES:
/// - FAA ACD approach speed (approachSpeedKnot): authoritative Vref, derived from
///   certification flight test data. Available for ~900 aircraft types.
/// - Profile ceiling: used with engine class to estimate sea-level climb rate via
///   empirical ceiling/climb-rate ratios validated against known aircraft.
/// - ACD engine type + engine count: determines which correction multipliers to use.
///
/// METHODOLOGY:
/// Each correction was derived by comparing profile values against FAA ACD data and
/// known real-world performance across 139 aircraft types. The equations and multipliers
/// are documented inline with their derivation and validation results.
/// </summary>
public sealed class EurocontrolProfileCorrectionAdapter : IProfileCorrectionAdapter
{
    // -----------------------------------------------------------------------
    // Speed correction multipliers
    //
    // These convert FAA ACD Vref into other speed targets. Derived empirically
    // by comparing real-world speeds to ACD Vref across multiple aircraft per
    // category. The ratios are stable within each engine class because approach
    // speed correlates strongly with wing loading, which drives all low-speed
    // performance.
    // -----------------------------------------------------------------------

    // Pattern (downwind) speed as a multiple of Vref.
    // Piston: ~1.29 (C172: 80/62), but 1.25 avoids overshooting lighter types.
    // Turboprop/jet: profile ratios are already reasonable (~0.92 of profileFAS),
    // so we use a lower floor that only catches under-estimates.
    private const double PatternMultiplierPiston = 1.25;
    private const double PatternMultiplierTurboprop = 1.15;
    private const double PatternMultiplierJet = 1.10;

    // Initial climb speed (Vy) cap as a multiple of Vref.
    // Piston Vy is typically 1.2x Vref (C172: 75/62 = 1.21). Cap at 1.25 to
    // catch the most inflated profiles without over-correcting. Turboprop/jet
    // climb speeds are higher relative to Vref and less inflated in profiles.
    private const double ClimbSpeedCapPiston = 1.25;
    private const double ClimbSpeedCapTurboprop = 1.40;
    private const double ClimbSpeedCapJet = 1.40;

    // -----------------------------------------------------------------------
    // Climb rate estimation from ceiling
    //
    // For piston and turboprop aircraft, profile climb rates are often wildly
    // wrong (C172: 400 fpm profile vs 730 fpm real). The FAA ACD has no climb
    // rate data, but the profile's service ceiling is a useful proxy:
    //
    //   Sea-level climb rate ≈ ceiling / divisor
    //
    // This works because ceiling is determined by where excess power drops to
    // ~100 fpm, so higher ceiling = more excess power at sea level = higher
    // climb rate. The divisor varies by engine class and configuration.
    //
    // Validated against known real-world climb rates:
    //   C172: 13000/18 = 722 fpm (real: 730, error: -1%)
    //   C182: 18000/18 = 1000 fpm (real: 924, error: +8%)
    //   C210: 27000/28 = 964 fpm (real: 930, error: +4%)  [turbocharged]
    //   BE36: 18000/18 = 1000 fpm (real: 1030, error: -3%)
    //   BE58: 20000/13 = 1538 fpm (real: 1500, error: +3%) [twin]
    //   PA34: 19000/13 = 1462 fpm (real: 1400, error: +4%) [twin]
    //   PC12: 30000/17 = 1765 fpm (real: 1920, error: -8%) [turboprop]
    //   B190: 25000/10 = 2500 fpm (real: 2600, error: -4%) [twin turboprop]
    //
    // Not applied to jets — jet profile climb rates are generally accurate.
    // -----------------------------------------------------------------------

    // Ceiling-to-climb-rate divisors by engine class.
    // Single piston with ceiling > 20k is likely turbocharged (higher ceiling
    // without proportionally higher climb rate), so uses a larger divisor.
    private const double ClimbRateDivisorSinglePiston = 18.0;
    private const double ClimbRateDivisorSinglePistonTurbo = 28.0;
    private const double ClimbRateDivisorTwinPiston = 13.0;
    private const double ClimbRateDivisorSingleTurboprop = 17.0;
    private const double ClimbRateDivisorTwinTurboprop = 10.0;
    private const double TurboPistonCeilingThreshold = 20000.0;

    /// <summary>
    /// Returns a corrected final approach speed using the FAA ACD as ground truth.
    /// If no ACD data is available, returns the profile value unchanged.
    /// </summary>
    public double FinalApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd)
    {
        // FAA ACD approach speed is authoritative Vref from certification testing.
        // Profile FAS is typically ~20% too high across all engine classes.
        if (acd?.ApproachSpeedKnot is { } vref)
        {
            return vref;
        }

        return profile.FinalApproachSpeed;
    }

    /// <summary>
    /// Returns a corrected pattern/downwind speed.
    /// Uses max(profile, ACD × multiplier) so we only raise values that are
    /// too low — profiles that already have reasonable pattern speeds are untouched.
    /// </summary>
    public double PatternSpeed(AircraftProfile profile, FaaAircraftRecord? acd)
    {
        if (acd?.ApproachSpeedKnot is not { } vref)
        {
            return profile.PatternSpeed;
        }

        double multiplier = EngineClassMultiplier(acd, PatternMultiplierPiston, PatternMultiplierTurboprop, PatternMultiplierJet);
        double floor = vref * multiplier;
        return Math.Max(profile.PatternSpeed, floor);
    }

    /// <summary>
    /// Returns a corrected base speed derived from corrected pattern and approach speeds.
    /// Base leg is the deceleration transition between downwind and final, so the
    /// midpoint of those two speeds is a natural target.
    /// </summary>
    public double BaseSpeed(AircraftProfile profile, FaaAircraftRecord? acd)
    {
        double pattern = PatternSpeed(profile, acd);
        double fas = FinalApproachSpeed(profile, acd);
        return (pattern + fas) / 2.0;
    }

    /// <summary>
    /// Returns a corrected initial approach speed (IAS used during descent below 10k).
    /// Scaled proportionally by the ACD/profile FAS ratio since IAS and FAS are in
    /// the same speed regime (both driven by wing loading and aircraft weight).
    /// </summary>
    public double InitialApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd)
    {
        if (acd?.ApproachSpeedKnot is not { } vref || profile.FinalApproachSpeed <= 0)
        {
            return profile.InitialApproachSpeed;
        }

        double ratio = (double)vref / profile.FinalApproachSpeed;
        return profile.InitialApproachSpeed * ratio;
    }

    /// <summary>
    /// Returns a corrected initial climb speed (Vy).
    /// Uses min(profile, ACD × cap) to reduce inflated values without lowering
    /// profiles that are already at or below the expected Vy-to-Vref relationship.
    /// </summary>
    public double ClimbSpeedInitial(AircraftProfile profile, FaaAircraftRecord? acd)
    {
        if (acd?.ApproachSpeedKnot is not { } vref)
        {
            return profile.ClimbSpeedInitial;
        }

        double cap = EngineClassMultiplier(acd, ClimbSpeedCapPiston, ClimbSpeedCapTurboprop, ClimbSpeedCapJet);
        double maxClimbSpeed = vref * cap;
        return Math.Min(profile.ClimbSpeedInitial, maxClimbSpeed);
    }

    /// <summary>
    /// Returns an estimated sea-level climb rate derived from the profile's service
    /// ceiling and the aircraft's engine class. Uses max(profile, estimate) so we
    /// only correct upward — profiles that already have reasonable climb rates (like
    /// BE58 at 1500 fpm) are not modified.
    ///
    /// Not applied to jets, whose profile climb rates are generally accurate.
    /// </summary>
    public double ClimbRateInitial(AircraftProfile profile, FaaAircraftRecord? acd)
    {
        if (acd is null || profile.Ceiling <= 0)
        {
            return profile.ClimbRateInitial;
        }

        double? estimated = EstimateClimbRateFromCeiling(profile.Ceiling, acd);
        if (estimated is null)
        {
            return profile.ClimbRateInitial;
        }

        // Only correct upward: if the profile already has a higher value, keep it.
        return Math.Max(profile.ClimbRateInitial, estimated.Value);
    }

    /// <summary>
    /// Estimates sea-level climb rate from the aircraft's service ceiling using
    /// empirical ceiling/climb-rate ratios. Returns null for jets (no correction).
    /// </summary>
    private static double? EstimateClimbRateFromCeiling(double ceiling, FaaAircraftRecord acd)
    {
        string engine = acd.PhysicalClassEngine ?? "";
        int numEngines = acd.NumEngines ?? 1;

        if (engine.Contains("Piston", StringComparison.OrdinalIgnoreCase))
        {
            if (numEngines >= 2)
            {
                return ceiling / ClimbRateDivisorTwinPiston;
            }

            // Single-engine piston: turbocharged models have high ceilings (>20k)
            // but not proportionally higher climb rates, so use a larger divisor.
            return ceiling > TurboPistonCeilingThreshold ? ceiling / ClimbRateDivisorSinglePistonTurbo : ceiling / ClimbRateDivisorSinglePiston;
        }

        if (engine.Contains("Turboprop", StringComparison.OrdinalIgnoreCase) || engine.Contains("Turboshaft", StringComparison.OrdinalIgnoreCase))
        {
            return numEngines >= 2 ? ceiling / ClimbRateDivisorTwinTurboprop : ceiling / ClimbRateDivisorSingleTurboprop;
        }

        // Jets: profile climb rates are generally accurate, don't override.
        return null;
    }

    /// <summary>
    /// Selects a multiplier based on engine class from the FAA ACD record.
    /// </summary>
    private static double EngineClassMultiplier(FaaAircraftRecord acd, double piston, double turboprop, double jet)
    {
        string engine = acd.PhysicalClassEngine ?? "";

        if (engine.Contains("Piston", StringComparison.OrdinalIgnoreCase))
        {
            return piston;
        }

        if (engine.Contains("Turboprop", StringComparison.OrdinalIgnoreCase) || engine.Contains("Turboshaft", StringComparison.OrdinalIgnoreCase))
        {
            return turboprop;
        }

        return jet;
    }
}
