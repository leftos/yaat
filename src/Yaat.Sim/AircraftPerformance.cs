using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim;

/// <summary>
/// Unified aircraft performance lookup: per-type profiles (AircraftProfiles.json) with
/// category-based fallback (CategoryPerformance). Replaces direct CategoryPerformance
/// calls for profile-covered fields (climb/descent rates, speeds, accel/decel, turn rate).
/// </summary>
public static class AircraftPerformance
{
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

        ReadOnlySpan<(double, double)> breakpoints =
        [
            (0, p.ClimbRateInitial),
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

        ReadOnlySpan<(double, double)> breakpoints = [(0, p.DescentRateInitial), (10000, p.DescentRateFl100), (p.Ceiling, p.DescentRateApproach)];
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

        ReadOnlySpan<(double, double)> breakpoints =
        [
            (0, p.ClimbSpeedInitial),
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

        ReadOnlySpan<(double, double)> breakpoints =
        [
            (0, p.InitialApproachSpeed),
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

        // Level flight: cruise speed
        double cruise = p.CruiseSpeed;
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

    public static double InitialClimbSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.ClimbSpeedInitial : CategoryPerformance.InitialClimbSpeed(cat);
    }

    public static double InitialClimbRate(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.ClimbRateInitial : CategoryPerformance.InitialClimbRate(cat);
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
    /// Final approach speed. Priority: profile -> FAA ACD -> category default.
    /// </summary>
    public static double ApproachSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is not null && p.FinalApproachSpeed > 0)
        {
            return p.FinalApproachSpeed;
        }

        // Fall back to FAA ACD approach speed
        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record?.ApproachSpeedKnot is { } faaSpeed)
        {
            return faaSpeed;
        }

        return CategoryPerformance.ApproachSpeed(cat);
    }

    public static double TouchdownSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.LandingSpeed : CategoryPerformance.TouchdownSpeed(cat);
    }

    public static double DownwindSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p is not null ? p.PatternSpeed : CategoryPerformance.DownwindSpeed(cat);
    }

    /// <summary>
    /// Base leg speed. Profile doesn't have a dedicated base speed, so we derive it
    /// as midpoint between pattern speed and final approach speed.
    /// </summary>
    public static double BaseSpeed(string aircraftType, AircraftCategory cat)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        if (p is null)
        {
            return CategoryPerformance.BaseSpeed(cat);
        }

        return (p.PatternSpeed + p.FinalApproachSpeed) / 2.0;
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
    /// Service ceiling (ft). Returns null if no profile exists.
    /// </summary>
    public static double? Ceiling(string aircraftType)
    {
        var p = AircraftProfileDatabase.Get(aircraftType);
        return p?.Ceiling;
    }
}
