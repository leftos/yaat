using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim;

/// <summary>
/// Unified aircraft performance lookup: per-type profiles (AircraftProfiles.json) with
/// category-based fallback (CategoryPerformance). Replaces direct CategoryPerformance
/// calls for profile-covered fields (climb/descent rates, speeds, accel/decel, turn rate).
///
/// Profile values for approach speeds, climb speeds, pattern speeds, and climb rates can
/// be corrected at runtime via an <see cref="IProfileCorrectionAdapter"/>. The default
/// adapter passes values through unchanged; call <see cref="SetProfileCorrectionAdapter"/>
/// at startup to install a correction adapter (e.g. <see cref="EurocontrolProfileCorrectionAdapter"/>).
/// </summary>
public static class AircraftPerformance
{
    private static IProfileCorrectionAdapter _correctionAdapter = new PassthroughProfileCorrectionAdapter();

    /// <summary>
    /// Install a profile correction adapter. Call once at startup before any performance
    /// lookups. Pass null to revert to the default pass-through adapter.
    /// </summary>
    public static void SetProfileCorrectionAdapter(IProfileCorrectionAdapter? adapter)
    {
        _correctionAdapter = adapter ?? new PassthroughProfileCorrectionAdapter();
    }

    /// <summary>
    /// Resolve a speed value that may be Mach (values &lt; 1.0) to IAS at the given altitude.
    /// Values &gt;= 1.0 are treated as KIAS and returned as-is.
    /// </summary>
    public static double ResolveSpeed(double value, double altitudeFt)
    {
        return value > 0 && value < 1.0 ? WindInterpolator.MachToIas(value, altitudeFt) : value;
    }

    /// <summary>
    /// Linear interpolation between altitude-based breakpoints.
    /// Breakpoints must be sorted ascending by altitude.
    /// Clamps to first/last value outside the breakpoint range.
    /// Skips breakpoints with zero values (aircraft can't reach that altitude).
    /// </summary>
    public static double InterpolateByAltitude(double altitudeFt, ReadOnlySpan<(double Alt, double Value)> breakpoints)
    {
        // Find the last non-zero breakpoint as effective ceiling
        int lastValid = -1;
        for (int i = 0; i < breakpoints.Length; i++)
        {
            if (breakpoints[i].Value > 0)
            {
                lastValid = i;
            }
        }

        if (lastValid < 0)
        {
            return 0;
        }

        // Find first non-zero breakpoint
        int firstValid = 0;
        for (int i = 0; i < breakpoints.Length; i++)
        {
            if (breakpoints[i].Value > 0)
            {
                firstValid = i;
                break;
            }
        }

        if (altitudeFt <= breakpoints[firstValid].Alt)
        {
            return breakpoints[firstValid].Value;
        }

        if (altitudeFt >= breakpoints[lastValid].Alt)
        {
            return breakpoints[lastValid].Value;
        }

        // Find surrounding valid breakpoints and lerp
        for (int i = firstValid; i < lastValid; i++)
        {
            int next = i + 1;
            // Skip zero-value breakpoints
            while (next <= lastValid && breakpoints[next].Value <= 0)
            {
                next++;
            }

            if (next > lastValid)
            {
                return breakpoints[i].Value;
            }

            if (altitudeFt <= breakpoints[next].Alt)
            {
                double range = breakpoints[next].Alt - breakpoints[i].Alt;
                if (range <= 0)
                {
                    return breakpoints[i].Value;
                }

                double t = (altitudeFt - breakpoints[i].Alt) / range;
                return breakpoints[i].Value + t * (breakpoints[next].Value - breakpoints[i].Value);
            }
        }

        return breakpoints[lastValid].Value;
    }

    public static double ClimbRate(string aircraftType, AircraftCategory cat, double altitudeFt)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.ClimbRate(cat, altitudeFt);
        }

        var acd = FaaAircraftDatabase.Get(aircraftType);
        double correctedInitial = _correctionAdapter.ClimbRateInitial(p, acd);

        ReadOnlySpan<(double, double)> breakpoints =
        [
            (0, correctedInitial),
            (15000, p.ClimbRateFl150),
            (24000, p.ClimbRateFl240),
            (p.Ceiling, p.ClimbRateFinal),
        ];
        return InterpolateByAltitude(altitudeFt, breakpoints);
    }

    public static double DescentRate(string aircraftType, AircraftCategory cat, double altitudeFt)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DescentRate(cat);
        }

        // "Initial" is the initial descent *from cruise* (the gentle top-of-descent pushover)
        // and "Approach" the below-FL100 segment — the Eurocontrol source data reads top-down,
        // mirroring the DescentSpeed ladder below. B738: approach 1500 / FL100 3500 / initial 800.
        ReadOnlySpan<(double, double)> breakpoints = [(0, p.DescentRateApproach), (10000, p.DescentRateFl100), (p.Ceiling, p.DescentRateInitial)];
        return InterpolateByAltitude(altitudeFt, breakpoints);
    }

    /// <summary>
    /// Altitude-aware climb speed schedule (KIAS). Respects 250kt limit below 10k unless waived.
    /// </summary>
    public static double ClimbSpeed(string aircraftType, AircraftCategory cat, double altitudeFt)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DefaultSpeed(cat, altitudeFt);
        }

        var acd = FaaAircraftDatabase.Get(aircraftType);
        double correctedInitial = _correctionAdapter.ClimbSpeedInitial(p, acd);

        ReadOnlySpan<(double, double)> breakpoints =
        [
            (0, correctedInitial),
            (15000, ResolveSpeed(p.ClimbSpeedFl150, 15000)),
            (24000, ResolveSpeed(p.ClimbSpeedFl240, 24000)),
            (p.Ceiling, ResolveSpeed(p.ClimbSpeedFinal, Math.Max(altitudeFt, 24000))),
        ];
        double speed = InterpolateByAltitude(altitudeFt, breakpoints);

        if (altitudeFt < 10000 && !p.IsSpeedLimitWaived)
        {
            speed = Math.Min(speed, 250);
        }

        return speed;
    }

    /// <summary>
    /// Altitude-aware descent speed schedule (KIAS). Respects 250kt limit below 10k unless waived.
    /// </summary>
    public static double DescentSpeed(string aircraftType, AircraftCategory cat, double altitudeFt)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DefaultSpeed(cat, altitudeFt);
        }

        var acd = FaaAircraftDatabase.Get(aircraftType);
        double correctedIas = _correctionAdapter.InitialApproachSpeed(p, acd);

        ReadOnlySpan<(double, double)> breakpoints =
        [
            (0, correctedIas),
            (10000, ResolveSpeed(p.DescentSpeedFl100, 10000)),
            (p.Ceiling, ResolveSpeed(p.DescentSpeedInitial, Math.Max(altitudeFt, 24000))),
        ];
        double speed = InterpolateByAltitude(altitudeFt, breakpoints);

        if (altitudeFt < 10000 && !p.IsSpeedLimitWaived)
        {
            speed = Math.Min(speed, 250);
        }

        return speed;
    }

    /// <summary>
    /// Auto speed schedule: uses climb or descent speed profile based on whether the aircraft
    /// is above or below its target altitude. Falls back to cruise speed when level.
    /// </summary>
    public static double DefaultSpeed(string aircraftType, AircraftCategory cat, double altitudeFt, double? targetAltitude)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DefaultSpeed(cat, altitudeFt);
        }

        if (targetAltitude is not null)
        {
            bool isClimbing = targetAltitude.Value > altitudeFt;
            return isClimbing ? ClimbSpeed(aircraftType, cat, altitudeFt) : DescentSpeed(aircraftType, cat, altitudeFt);
        }

        // Level flight: cruise speed. Profile stores TAS in knots (e.g. CL60 = 460 KTAS at
        // its reference altitude p.CruiseAltitude) or Mach when < 1.0. Aircraft cruise at
        // roughly constant IAS, so resolve TAS once at the reference altitude and use the
        // resulting IAS at every altitude. Mach is altitude-dependent by definition and stays
        // resolved against the current altitude.
        double cruise = p.CruiseSpeed;
        if (cruise > 0 && cruise < 1.0)
        {
            cruise = WindInterpolator.MachToIas(cruise, altitudeFt);
        }
        else if (cruise > 0)
        {
            cruise = WindInterpolator.TasToIas(cruise, p.CruiseAltitude);
        }

        if (altitudeFt < 10000 && !p.IsSpeedLimitWaived)
        {
            cruise = Math.Min(cruise, 250);
        }

        return cruise;
    }

    public static double AccelRate(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.AirborneAccelRate : CategoryPerformance.AccelRate(cat);
    }

    public static double DecelRate(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.AirborneDecelRate : CategoryPerformance.DecelRate(cat);
    }

    public static double GroundAccelRate(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.GroundAccelRate : CategoryPerformance.GroundAccelRate(cat);
    }

    public static double RotationSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.RotateSpeed : CategoryPerformance.RotationSpeed(cat);
    }

    /// <summary>
    /// Decision speed (V1) — past V1, an aborted takeoff is no longer
    /// guaranteed to stop on remaining runway, so a takeoff is "go" instead.
    /// Approximated as Vr − 5 kts per FAA expert anchored to 14 CFR Part 25.
    /// </summary>
    public static double DecisionSpeed(string aircraftType, AircraftCategory cat)
    {
        return Math.Max(0, RotationSpeed(aircraftType, cat) - 5);
    }

    public static double InitialClimbSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.InitialClimbSpeed(cat);
        }

        var acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.ClimbSpeedInitial(p, acd);
    }

    public static double InitialClimbRate(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.InitialClimbRate(cat);
        }

        var acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.ClimbRateInitial(p, acd);
    }

    public static double TurnRate(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is not null && p.StandardTurnRateOverride > 0)
        {
            return p.StandardTurnRateOverride;
        }

        return CategoryPerformance.TurnRate(cat);
    }

    /// <summary>
    /// Final approach speed. Priority: ACD-corrected profile -> FAA ACD -> category default.
    /// </summary>
    public static double ApproachSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is not null && p.FinalApproachSpeed > 0)
        {
            var acd = FaaAircraftDatabase.Get(aircraftType);
            return _correctionAdapter.FinalApproachSpeed(p, acd);
        }

        // Fall back to FAA ACD approach speed
        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record?.ApproachSpeedKnot is { } faaSpeed)
        {
            return faaSpeed;
        }

        return CategoryPerformance.ApproachSpeed(cat);
    }

    /// <summary>Boeing FCTM / Airbus FCOM cap on the wind additive applied to Vref.</summary>
    private const double MaxGustIncrementKts = 20.0;

    /// <summary>
    /// Approach-speed wind additive for gusty conditions: pilots fly Vref plus half the
    /// gust increment (reported gust minus sustained wind), the increment capped at 20 kt
    /// (Boeing FCTM / Airbus FCOM technique). Zero in steady or VRB (light) wind. Uses the
    /// surface layer's resolved gust excess so a spread-only layer that the sim gusts by
    /// cross-derivation gets the same additive pilots would fly from its reported G group.
    /// </summary>
    public static double GustApproachAdditive(WeatherProfile? weather)
    {
        if (weather is null || weather.WindLayers.Count == 0)
        {
            return 0;
        }

        var surface = weather.WindLayers[0];
        if (surface.Variable ?? false)
        {
            return 0;
        }

        var (gustExcess, _) = WindVariation.ResolveAmplitudes(surface.Speed, surface.Gusts, surface.DirectionVariabilityDeg, variable: false);
        return Math.Min(gustExcess, MaxGustIncrementKts) / 2.0;
    }

    public static double TouchdownSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.LandingSpeed : CategoryPerformance.TouchdownSpeed(cat);
    }

    public static double DownwindSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DownwindSpeed(cat);
        }

        var acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.PatternSpeed(p, acd);
    }

    /// <summary>
    /// Base leg speed. Derived as midpoint between corrected pattern and approach speeds.
    /// </summary>
    public static double BaseSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.BaseSpeed(cat);
        }

        var acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.BaseSpeed(p, acd);
    }

    /// <summary>
    /// Holding speed. Uses profile value clamped to AIM altitude-band maximums.
    /// </summary>
    public static double HoldingSpeed(string aircraftType, double altitudeFt)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        double maxHolding = CategoryPerformance.MaxHoldingSpeed(altitudeFt);

        if (p is not null && p.HoldingSpeed > 0)
        {
            return Math.Min(p.HoldingSpeed, maxHolding);
        }

        return maxHolding;
    }

    /// <summary>
    /// Whether 14 CFR 91.117 (250kt below 10k) is waived for this aircraft type.
    /// </summary>
    public static bool IsSpeedLimitWaived(string aircraftType)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null && p.IsSpeedLimitWaived;
    }

    /// <summary>
    /// Approximate minimum safe airspeed (kt) for 14 CFR 91.117(d), which permits that speed when it
    /// exceeds any cap in the section — including 91.117(c)'s 200 kt Class B shelf limit.
    ///
    /// The profile database publishes no minimum-safe-speed field, so the type's initial approach
    /// speed stands in: it is the slowest speed the profile asserts the aircraft is flown at in
    /// clean-ish configuration. Only types flagged <see cref="IsSpeedLimitWaived"/> consult this.
    /// </summary>
    public static double MinimumSafeSpeedKts(string aircraftType)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p?.InitialApproachSpeed ?? 0;
    }

    /// <summary>
    /// Airspeed (KIAS) flown on an IR or VR military training route, by category.
    ///
    /// Operating above 250 knots is what defines the program — the P/CG entry for Military Training
    /// Routes and AIM 3-5-2.c describe routes flown in excess of 250 KIAS, and AP/1B chapter 1 §I
    /// grants the 14 CFR 91.117(a) waiver that permits it. The waiver only lifts the cap, though;
    /// nothing publishes a speed, and AP/1B carries none per route. So this is a category default,
    /// not route data: a tactical jet works the route fast, and anything else has no reason to
    /// exceed the ordinary limit. A controller <c>SPD</c> assignment overrides it.
    /// </summary>
    public static double MilitaryRouteSpeedKts(AircraftCategory category) => category == AircraftCategory.Jet ? 400 : 250;

    /// <summary>
    /// Service ceiling (ft). Returns null if no profile exists.
    /// </summary>
    public static double? Ceiling(string aircraftType)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p?.Ceiling;
    }
}
