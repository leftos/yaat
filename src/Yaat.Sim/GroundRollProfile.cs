using Yaat.Sim.Phases;

namespace Yaat.Sim;

/// <summary>
/// The acceleration ramp of a takeoff roll: thrust is not at its takeoff value when the brakes are
/// released. The crew stabilises at a part-power setting, releases, and the engines spool to takeoff
/// thrust over a few seconds, so the acceleration rises from an idle-roll value to the steady rate:
/// <c>a(t) = idle + (steady - idle) * min(t / spool, 1)</c>, with <c>t</c> the seconds since the roll
/// started. Past the spool the acceleration is constant at the steady rate.
/// </summary>
/// <param name="IdleRateKtPerSec">Acceleration (kt/s) at brake release, before the engines spool up.</param>
/// <param name="SteadyRateKtPerSec">Acceleration (kt/s) once takeoff thrust is set.</param>
/// <param name="SpoolSeconds">Seconds from brake release to takeoff thrust; 0 means the steady rate applies immediately.</param>
public readonly record struct GroundRollProfile(double IdleRateKtPerSec, double SteadyRateKtPerSec, double SpoolSeconds)
{
    /// <summary>The ramp a type of this category flies: the type's steady rate on the category's idle rate and spool time.</summary>
    public static GroundRollProfile For(string aircraftType, AircraftCategory cat) =>
        new(
            CategoryPerformance.GroundAccelIdleRate(cat),
            AircraftPerformance.GroundAccelRate(aircraftType, cat),
            CategoryPerformance.GroundAccelSpoolSeconds(cat)
        );

    /// <summary>
    /// A rate that never ramps — the measured acceleration of a live-traffic shadow, which already
    /// carries whatever spool state the real aircraft is in. Lets every predictor run one code path.
    /// </summary>
    public static GroundRollProfile Constant(double rateKtPerSec) => new(rateKtPerSec, rateKtPerSec, 0);

    /// <summary>
    /// Where on the ramp <paramref name="aircraft"/> is: the exact clock of the roll phase flying it
    /// (<see cref="IGroundRollClock"/>), or — for an aircraft no roll phase owns, a lined-up-and-waiting
    /// leader or a live-traffic shadow — the ramp position its present groundspeed implies. Predictors
    /// call this instead of <see cref="TimeAtSpeed"/> so a roll that is under way is projected on the
    /// acceleration it actually has rather than the one its speed would suggest.
    /// </summary>
    public static double RollClockSeconds(AircraftState aircraft, GroundRollProfile profile) =>
        aircraft.Phases?.CurrentPhase is IGroundRollClock clock ? clock.RollElapsedSeconds : profile.TimeAtSpeed(aircraft.GroundSpeed);

    /// <summary>Speed (kt) the roll has reached at the end of the spool, where the ramp hands over to the steady rate.</summary>
    private double SpeedAtSpool => (IdleRateKtPerSec + SteadyRateKtPerSec) * SpoolSeconds / 2.0;

    /// <summary>Distance (kt·s) covered by the end of the spool.</summary>
    private double DistanceAtSpool => ((2.0 * IdleRateKtPerSec) + SteadyRateKtPerSec) * SpoolSeconds * SpoolSeconds / 6.0;

    /// <summary>Acceleration (kt/s) at <paramref name="t"/> seconds into the roll.</summary>
    public double AccelAt(double t)
    {
        double elapsed = Math.Max(0, t);
        if (SpoolSeconds <= 0)
        {
            return SteadyRateKtPerSec;
        }

        return IdleRateKtPerSec + ((SteadyRateKtPerSec - IdleRateKtPerSec) * Math.Min(elapsed / SpoolSeconds, 1.0));
    }

    /// <summary>Speed (kt) gained by <paramref name="t"/> seconds into the roll, from a standing start.</summary>
    public double SpeedAt(double t)
    {
        double elapsed = Math.Max(0, t);
        if (SpoolSeconds <= 0)
        {
            return SteadyRateKtPerSec * elapsed;
        }

        if (elapsed <= SpoolSeconds)
        {
            return (IdleRateKtPerSec * elapsed) + ((SteadyRateKtPerSec - IdleRateKtPerSec) * elapsed * elapsed / (2.0 * SpoolSeconds));
        }

        return SpeedAtSpool + (SteadyRateKtPerSec * (elapsed - SpoolSeconds));
    }

    /// <summary>
    /// Distance covered by <paramref name="t"/> seconds into the roll, in kt·s — callers multiply by
    /// their own knot-second-to-feet constant.
    /// </summary>
    public double DistanceKtSecondsAt(double t)
    {
        double elapsed = Math.Max(0, t);
        if (SpoolSeconds <= 0)
        {
            return 0.5 * SteadyRateKtPerSec * elapsed * elapsed;
        }

        if (elapsed <= SpoolSeconds)
        {
            return (IdleRateKtPerSec * elapsed * elapsed / 2.0)
                + ((SteadyRateKtPerSec - IdleRateKtPerSec) * elapsed * elapsed * elapsed / (6.0 * SpoolSeconds));
        }

        double beyondSpool = elapsed - SpoolSeconds;
        return DistanceAtSpool + (SpeedAtSpool * beyondSpool) + (0.5 * SteadyRateKtPerSec * beyondSpool * beyondSpool);
    }

    /// <summary>
    /// The seconds from <paramref name="startTime"/> into the roll needed to cover a further
    /// <paramref name="ktSeconds"/> of distance: the smallest <c>t &gt;= 0</c> for which
    /// <c>DistanceKtSecondsAt(startTime + t) - DistanceKtSecondsAt(startTime) &gt;= ktSeconds</c>.
    /// Bisected rather than solved in closed form because distance is cubic in <c>t</c> while the
    /// roll is on the spool ramp; distance is monotone in <c>t</c>, so a bracket grown by doubling
    /// and bisected to 1e-6 s is deterministic. A roll that never covers the distance — a standstill
    /// on a profile with no acceleration — answers <see cref="double.PositiveInfinity"/>.
    /// </summary>
    public double TimeToCoverKtSeconds(double startTime, double ktSeconds)
    {
        if (ktSeconds <= 0)
        {
            return 0;
        }

        double start = Math.Max(0, startTime);
        if ((SteadyRateKtPerSec <= 0) && (SpeedAt(start) <= 0))
        {
            return double.PositiveInfinity;
        }

        double startDistance = DistanceKtSecondsAt(start);
        double upper = BracketCovering(start, startDistance, ktSeconds);
        if (double.IsPositiveInfinity(upper))
        {
            return double.PositiveInfinity;
        }

        double lower = 0;
        while ((upper - lower) > 1e-6)
        {
            double mid = (lower + upper) / 2.0;
            if ((DistanceKtSecondsAt(start + mid) - startDistance) >= ktSeconds)
            {
                upper = mid;
            }
            else
            {
                lower = mid;
            }
        }

        return upper;
    }

    /// <summary>
    /// A one-second bracket doubled until it covers <paramref name="ktSeconds"/> from
    /// <paramref name="start"/>, or <see cref="double.PositiveInfinity"/> once a doubling stops
    /// gaining distance — a ramp that decelerates to a standstill never covers it.
    /// </summary>
    private double BracketCovering(double start, double startDistance, double ktSeconds)
    {
        double upper = 1.0;
        double covered = DistanceKtSecondsAt(start + upper) - startDistance;
        while (covered < ktSeconds)
        {
            double previous = covered;
            upper *= 2.0;
            covered = DistanceKtSecondsAt(start + upper) - startDistance;
            if (covered <= previous)
            {
                return double.PositiveInfinity;
            }
        }

        return upper;
    }

    /// <summary>
    /// Inverse of <see cref="SpeedAt"/>: the seconds into the roll at which it reaches
    /// <paramref name="speedKts"/> — the quadratic root on the ramp, linear past the spool. Lets a
    /// predictor that only sees a speed place an aircraft on the ramp. A speed at or below zero is
    /// the start of the roll; a profile that never reaches the speed (a non-positive rate, which the
    /// callers gate on) answers the time at which it stops gaining rather than infinity.
    /// </summary>
    public double TimeAtSpeed(double speedKts)
    {
        if (speedKts <= 0)
        {
            return 0;
        }

        if (SpoolSeconds <= 0)
        {
            return SteadyRateKtPerSec > 0 ? speedKts / SteadyRateKtPerSec : 0;
        }

        if (speedKts >= SpeedAtSpool)
        {
            return SteadyRateKtPerSec > 0 ? SpoolSeconds + ((speedKts - SpeedAtSpool) / SteadyRateKtPerSec) : SpoolSeconds;
        }

        // v = idle*t + (steady - idle)*t^2 / (2*spool)  →  quadratic in t with a = (steady - idle) / (2*spool).
        double a = (SteadyRateKtPerSec - IdleRateKtPerSec) / (2.0 * SpoolSeconds);
        if (Math.Abs(a) < 1e-12)
        {
            return IdleRateKtPerSec > 0 ? speedKts / IdleRateKtPerSec : 0;
        }

        double discriminant = (IdleRateKtPerSec * IdleRateKtPerSec) + (4.0 * a * speedKts);
        return (-IdleRateKtPerSec + Math.Sqrt(Math.Max(discriminant, 0))) / (2.0 * a);
    }
}
