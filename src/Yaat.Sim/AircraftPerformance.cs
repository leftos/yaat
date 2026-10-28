using System.Collections.Concurrent;
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
        ClearSpeedEnvelopeCache();
    }

    /// <summary>
    /// Resolve a speed value that may be Mach (values &lt; 1.0) to IAS at the given altitude.
    /// Values &gt;= 1.0 are treated as KIAS and returned as-is.
    /// </summary>
    public static double ResolveSpeed(double value, double altitudeFt) =>
        value > 0 && value < 1.0 ? WindInterpolator.MachToIas(value, altitudeFt) : value;

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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.ClimbRate(cat, altitudeFt);
        }

        FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DefaultSpeed(cat, altitudeFt);
        }

        FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DefaultSpeed(cat, altitudeFt);
        }

        FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.AirborneAccelRate : CategoryPerformance.AccelRate(cat);
    }

    public static double DecelRate(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.AirborneDecelRate : CategoryPerformance.DecelRate(cat);
    }

    /// <summary>
    /// Steady takeoff-roll acceleration (kt/s). Most profiles carry no value — the source database
    /// has no such field — so the category rate applies unless the type was hand-checked.
    /// </summary>
    public static double GroundAccelRate(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        return p?.GroundAccelRate ?? CategoryPerformance.GroundAccelRate(cat);
    }

    public static double RotationSpeed(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.RotateSpeed : CategoryPerformance.RotationSpeed(cat);
    }

    /// <summary>
    /// Decision speed (V1) — past V1, an aborted takeoff is no longer
    /// guaranteed to stop on remaining runway, so a takeoff is "go" instead.
    /// Approximated as Vr − 5 kts per FAA expert anchored to 14 CFR Part 25.
    /// </summary>
    public static double DecisionSpeed(string aircraftType, AircraftCategory cat) => Math.Max(0, RotationSpeed(aircraftType, cat) - 5);

    public static double InitialClimbSpeed(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.InitialClimbSpeed(cat);
        }

        FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.ClimbSpeedInitial(p, acd);
    }

    public static double InitialClimbRate(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.InitialClimbRate(cat);
        }

        FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.ClimbRateInitial(p, acd);
    }

    public static double TurnRate(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is not null && p.FinalApproachSpeed > 0)
        {
            FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
            return _correctionAdapter.FinalApproachSpeed(p, acd);
        }

        // Fall back to FAA ACD approach speed
        FaaAircraftRecord? record = FaaAircraftDatabase.Get(aircraftType);
        if (record?.ApproachSpeedKnot is { } faaSpeed)
        {
            return faaSpeed;
        }

        return CategoryPerformance.ApproachSpeed(cat);
    }

    /// <summary>Boeing FCTM / Airbus FCOM cap on the total wind additive applied to Vref.</summary>
    private const double MaxWindAdditiveKts = 20.0;

    /// <summary>
    /// Full approach-speed wind additive (Boeing FCTM / Airbus FCOM, FSF ALAR 8.2):
    /// Vapp = Vref + half the steady headwind component + the full gust increment
    /// (reported gust minus sustained), total capped at 20 kt. Tailwind never subtracts.
    /// The gust increment comes from the layer's AUTHORED gusts only — pilots fly the
    /// additive from the reported G group, so a spread-only wind with no G group adds
    /// nothing. The runway heading is true while the layer direction is magnetic, matching
    /// the sim's existing WCA convention (the declination error in the cosine is small
    /// for a wind anywhere near the runway axis).
    /// </summary>
    public static double WindApproachAdditive(WeatherProfile? weather, double runwayTrueHeadingDeg)
    {
        if (weather is null || weather.WindLayers.Count == 0)
        {
            return 0;
        }

        WindLayer surface = weather.WindLayers[0];
        if (surface.Variable ?? false)
        {
            return 0;
        }

        double headwind = Math.Max(0, WindInterpolator.HeadwindComponentKts(surface.Direction, surface.Speed, runwayTrueHeadingDeg));
        return Math.Min((0.5 * headwind) + AuthoredGustExcess(surface), MaxWindAdditiveKts);
    }

    /// <summary>
    /// The gust-only part of the wind additive, maintained to touchdown per the FCTM
    /// (only the steady-headwind half bleeds off in the flare).
    /// </summary>
    public static double GustApproachAdditive(WeatherProfile? weather)
    {
        if (weather is null || weather.WindLayers.Count == 0)
        {
            return 0;
        }

        WindLayer surface = weather.WindLayers[0];
        if (surface.Variable ?? false)
        {
            return 0;
        }

        return Math.Min(AuthoredGustExcess(surface), MaxWindAdditiveKts);
    }

    private static double AuthoredGustExcess(WindLayer surface) => surface.Gusts is { } gust && gust > surface.Speed ? gust - surface.Speed : 0;

    public static double TouchdownSpeed(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.LandingSpeed : CategoryPerformance.TouchdownSpeed(cat);
    }

    public static double DownwindSpeed(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.DownwindSpeed(cat);
        }

        FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.PatternSpeed(p, acd);
    }

    /// <summary>
    /// Base leg speed. Derived as midpoint between corrected pattern and approach speeds.
    /// </summary>
    public static double BaseSpeed(string aircraftType, AircraftCategory cat)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.BaseSpeed(cat);
        }

        FaaAircraftRecord? acd = FaaAircraftDatabase.Get(aircraftType);
        return _correctionAdapter.BaseSpeed(p, acd);
    }

    /// <summary>
    /// Holding speed. Uses profile value clamped to AIM altitude-band maximums.
    /// </summary>
    public static double HoldingSpeed(string aircraftType, double altitudeFt)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
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
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
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
    /// Maximum operating indicated airspeed (KIAS) at <paramref name="altitudeFt"/>: the lower of Vmo and Mmo's IAS
    /// equivalent there, or Vmo alone where the type has no Mmo. Null when the type has neither a speed envelope class
    /// nor an override figure, so nothing is refused for a type whose limit is unknown.
    /// </summary>
    public static double? MaxOperatingIasKts(string aircraftType, double altitudeFt)
    {
        (double? vmo, double? mmo) = ResolveSpeedEnvelope(aircraftType);
        if ((vmo is null) && (mmo is null))
        {
            return null;
        }

        double max = vmo ?? double.PositiveInfinity;
        if (mmo is { } mach)
        {
            max = Math.Min(max, WindInterpolator.MachToIas(mach, altitudeFt));
        }

        return max;
    }

    /// <summary>Maximum operating Mach number, or null when the type has none (helicopters, unknown types).</summary>
    public static double? MaxOperatingMach(string aircraftType) => ResolveSpeedEnvelope(aircraftType).Mmo;

    /// <summary>The slowest speed the type is flown at: its final approach speed, at every altitude.</summary>
    public static double MinOperatingIasKts(string aircraftType, AircraftCategory cat) => ApproachSpeed(aircraftType, cat);

    private const double ScheduleFloorStepFt = 100;

    private static readonly ConcurrentDictionary<string, (double? Vmo, double? Mmo)> _speedEnvelopeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Forget every resolved Vmo/Mmo. Called whenever a table the resolution reads is replaced (the profiles, the
    /// envelope classes, the correction adapter), so the next lookup resolves against the new one.
    /// </summary>
    internal static void ClearSpeedEnvelopeCache() => _speedEnvelopeCache.Clear();

    /// <summary>
    /// Vmo and Mmo for the type, resolved once and remembered until a table they come from is replaced
    /// (<see cref="ClearSpeedEnvelopeCache"/>).
    /// </summary>
    private static (double? Vmo, double? Mmo) ResolveSpeedEnvelope(string aircraftType) =>
        _speedEnvelopeCache.GetOrAdd(aircraftType, static type => ComputeSpeedEnvelope(type));

    /// <summary>
    /// Vmo and Mmo for the type: an override figure (TCDS or OpenAP) first, then the class figure, each then raised to
    /// the type's profile schedule so the limit never falls below a speed the profile itself flies
    /// (<see cref="ProfileScheduleFloor"/>).
    /// </summary>
    private static (double? Vmo, double? Mmo) ComputeSpeedEnvelope(string aircraftType)
    {
        (double? overrideVmo, double? overrideMmo) = AircraftProfileDatabase.SpeedEnvelopeOverride(aircraftType);
        SpeedEnvelopeClass? cls = AircraftSpeedEnvelope.ClassFor(aircraftType);
        double? vmo = overrideVmo ?? cls?.VmoKts;
        double? mmo = overrideMmo ?? cls?.Mmo;

        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        if ((p is null) || ((vmo is null) && (mmo is null)))
        {
            return (vmo, mmo);
        }

        (double floorIas, double floorMach) = ProfileScheduleFloor(aircraftType, p);
        return (vmo is { } v ? Math.Max(v, floorIas) : null, mmo is { } m ? Math.Max(m, floorMach) : null);
    }

    /// <summary>
    /// The highest IAS and the highest Mach the profile's schedule flies: the climb and descent schedules at every
    /// 100 ft from the ground to the ceiling and at each altitude the schedule changes band at (10,000, FL150, FL240, the
    /// cruise altitude and the ceiling), and level cruise up to the profile's cruise altitude. The fine grid catches a
    /// Mach peak between band points, where a constant IAS climbs toward Mmo as the air thins.
    /// </summary>
    private static (double Ias, double Mach) ProfileScheduleFloor(string aircraftType, AircraftProfile p)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraftType);
        double highestIas = 0;
        double highestMach = 0;
        SortedSet<double> altitudes = [10_000, 15_000, 24_000, p.CruiseAltitude, p.Ceiling];
        for (double grid = 0; grid <= p.Ceiling; grid += ScheduleFloorStepFt)
        {
            altitudes.Add(grid);
        }

        foreach (double alt in altitudes.Where(a => a <= p.Ceiling))
        {
            double flown = Math.Max(ClimbSpeed(aircraftType, cat, alt), DescentSpeed(aircraftType, cat, alt));

            // Above its cruise altitude the profile defines no cruise: the constant-IAS cruise there is an extrapolation
            // the cap holds, not a speed the type flies.
            if (alt <= p.CruiseAltitude)
            {
                flown = Math.Max(flown, DefaultSpeed(aircraftType, cat, alt, targetAltitude: null));
            }

            highestIas = Math.Max(highestIas, flown);
            highestMach = Math.Max(highestMach, WindInterpolator.IasToMach(flown, alt));
        }

        return (highestIas, highestMach);
    }

    /// <summary>
    /// Service ceiling (ft). Returns null if no profile exists.
    /// </summary>
    public static double? Ceiling(string aircraftType)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(aircraftType);
        return p?.Ceiling;
    }
}
