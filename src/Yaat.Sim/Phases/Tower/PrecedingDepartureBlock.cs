using Yaat.Sim.Simulation;
using Yaat.Sim.Training;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Whether a preceding <see cref="RunwayUseKind.Departing"/> aircraft on the same runway stops the
/// departure behind it, for <see cref="RejectedTakeoff.FindBlockingOccupant"/>. Two regimes, because
/// the doctrine changes the moment the wheels start turning:
///
/// Before the roll begins, 7110.65 §3-9-6.a governs — "does not begin takeoff roll until" the other
/// aircraft "has departed and crossed the runway end", or is airborne with the §3-9-6.a landmark
/// distance. The leader is projected <see cref="RejectedTakeoff.ReactionSeconds"/> ahead (§3-9-5 lets
/// the clearance anticipate the separation that will exist when the roll starts) and judged there by
/// <see cref="SameRunwaySeparation.DepartureBehindDepartureSatisfied"/>.
///
/// Once the trailer is rolling, §3-9-6 is spent — it constrains beginning the roll, not continuing
/// one — and the remaining question is a collision one (AIM 4-4-1.a: a clearance never authorizes
/// unsafe operation; 14 CFR 91.3(a)). The two rolls are projected forward and the leader blocks only
/// if the trailer would reach it, minus <see cref="RejectedTakeoff.StopMarginFt"/>, while the leader
/// is still on the ground; a leader that flies or outruns the trailer is no obstacle.
///
/// An opposite-direction aircraft rolling toward the trailer on the same pavement blocks outright:
/// <see cref="RunwayOccupancy.Classify"/> reads the axis modulo 180 so it is Departing here too, and
/// every distance in §3-9-6.a is credit for running <em>away</em> down the runway. It is reported at
/// its closing distance, not its present one — the overfly test that decides a high-speed reject
/// would otherwise weigh a head-on aircraft as if it were parked.
/// </summary>
internal static class PrecedingDepartureBlock
{
    /// <summary>How far ahead the two rolls are projected when looking for a rendezvous — beyond it any aircraft on this runway has flown or stopped.</summary>
    private const double RendezvousHorizonSeconds = 60.0;

    /// <summary>Rendezvous search step. Fine enough that the crossing instant is found within ~60 ft of ground run at rotation speed.</summary>
    private const double RendezvousStepSeconds = 0.25;

    /// <summary>kt·s → ft (a kt·s product is a distance in nautical-mile-per-hour seconds).</summary>
    private const double KtSecondsToFt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>
    /// Whether <paramref name="leader"/>, a departing aircraft <paramref name="dFt"/> ahead of
    /// <paramref name="departure"/> on <paramref name="runway"/>, blocks it (7110.65 §3-9-6.a before
    /// the roll starts, AIM 4-4-1.a once it is underway). <paramref name="projectedDistanceFt"/> is
    /// where the leader is projected to be when the decision bites — the distance the reject/decline
    /// chain measures its stop against — and equals <paramref name="dFt"/> when nothing blocks.
    /// </summary>
    public static bool Blocks(AircraftState departure, AircraftState leader, RunwayInfo runway, double dFt, out double projectedDistanceFt)
    {
        if (IsOppositeDirection(leader, runway))
        {
            projectedDistanceFt = ClosingDistanceFt(departure, leader, dFt);
            return true;
        }

        return departure.GroundSpeed < RejectedTakeoff.RollUnderwayMinKts
            ? BlocksBeforeRollStarts(departure, leader, runway, dFt, out projectedDistanceFt)
            : BlocksOnceRolling(departure, leader, dFt, out projectedDistanceFt);
    }

    /// <summary>
    /// §3-9-6.a: the trailer may not begin its roll unless the leader, projected one reaction window
    /// ahead, has departed <em>and</em> crossed the runway end, or is airborne with the §3-9-6.a
    /// distance behind it. A leader stopped past the end has not "departed", so the crossed-the-end
    /// half carries the same airborne test the spacing half does. The runway end is the pavement end
    /// opposite the threshold in use, measured from the trailer's own position so that it and the
    /// leader's projected spacing share one origin.
    /// </summary>
    private static bool BlocksBeforeRollStarts(
        AircraftState departure,
        AircraftState leader,
        RunwayInfo runway,
        double dFt,
        out double projectedDistanceFt
    )
    {
        double spacingFt = dFt + ProjectedGroundRunFt(leader, RejectedTakeoff.ReactionSeconds);
        projectedDistanceFt = spacingFt;

        var runwayEnd = new LatLon(runway.EndLatitude, runway.EndLongitude);
        double runwayEndFt = GeoMath.AlongTrackDistanceNm(runwayEnd, departure.Position, runway.TrueHeading) * GeoMath.FeetPerNm;
        bool departed = SameRunwaySeparation.WillBeFlying(leader, RejectedTakeoff.ReactionSeconds);

        return !SameRunwaySeparation.DepartureBehindDepartureSatisfied(
            precedingCrossedRunwayEnd: departed && (spacingFt >= runwayEndFt),
            precedingAirborne: departed,
            spacingFt,
            SameRunwaySeparation.ResolveSrsCategory(leader),
            SameRunwaySeparation.ResolveSrsCategory(departure)
        );
    }

    /// <summary>
    /// The first instant inside the horizon at which the rolling trailer reaches the leader (less the
    /// stop margin) while the leader is still on the ground. No such instant — the leader flies first
    /// or stays ahead — and there is nothing to reject for.
    /// </summary>
    private static bool BlocksOnceRolling(AircraftState departure, AircraftState leader, double dFt, out double projectedDistanceFt)
    {
        projectedDistanceFt = dFt;
        for (double t = 0; t <= RendezvousHorizonSeconds; t += RendezvousStepSeconds)
        {
            if (SameRunwaySeparation.WillBeFlying(leader, t))
            {
                continue;
            }

            double leaderFt = dFt + ProjectedGroundRunFt(leader, t);
            if (ProjectedGroundRunFt(departure, t) >= (leaderFt - RejectedTakeoff.StopMarginFt))
            {
                projectedDistanceFt = leaderFt;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How far ahead a head-on roller is when the two runs meet: the first instant inside the horizon
    /// at which the trailer's projected run reaches it, less <see cref="RejectedTakeoff.StopMarginFt"/>,
    /// or the horizon's own closure when they never do. Reporting the present distance instead would
    /// hand <see cref="RejectedTakeoff.CanOverfly"/> a closing aircraft weighed as a parked one, and a
    /// takeoff would continue over ground the other aircraft is already using. A standstill trailer
    /// closes the distance only by the leader's own run, which the same sweep gives.
    /// </summary>
    private static double ClosingDistanceFt(AircraftState departure, AircraftState leader, double dFt)
    {
        for (double t = 0; t <= RendezvousHorizonSeconds; t += RendezvousStepSeconds)
        {
            double leaderFt = dFt - ProjectedGroundRunFt(leader, t);
            if (ProjectedGroundRunFt(departure, t) >= (leaderFt - RejectedTakeoff.StopMarginFt))
            {
                return leaderFt;
            }
        }

        return dFt - ProjectedGroundRunFt(leader, RendezvousHorizonSeconds);
    }

    /// <summary>Rolling toward the trailer on the same pavement rather than away from it (the classifier's axis test is modulo 180).</summary>
    private static bool IsOppositeDirection(AircraftState leader, RunwayInfo runway) =>
        GeoMath.AbsBearingDifference(leader.TrueHeading.Degrees, runway.TrueHeading.Degrees) > RunwayOccupancy.SurfaceAxisToleranceDeg;

    /// <summary>
    /// Along-runway distance (ft) the aircraft covers in <paramref name="seconds"/> at its present
    /// ground speed and acceleration, held between a stop and its liftoff ground speed — past
    /// rotation it is flying, not running, and a braking aircraft does not roll backwards.
    /// </summary>
    private static double ProjectedGroundRunFt(AircraftState aircraft, double seconds)
    {
        double speedKts = aircraft.GroundSpeed;
        double accel = AccelerationKtPerSec(aircraft);
        double capKts = Math.Max(speedKts, LiftoffGroundSpeedKts(aircraft));

        if (accel > 0)
        {
            double toCapSeconds = Math.Min(seconds, (capKts - speedKts) / accel);
            double acceleratingKtSeconds = (speedKts * toCapSeconds) + (0.5 * accel * toCapSeconds * toCapSeconds);
            return (acceleratingKtSeconds + (capKts * (seconds - toCapSeconds))) * KtSecondsToFt;
        }

        double movingSeconds = accel < 0 ? Math.Min(seconds, speedKts / -accel) : seconds;
        return ((speedKts * movingSeconds) + (0.5 * accel * movingSeconds * movingSeconds)) * KtSecondsToFt;
    }

    /// <summary>
    /// Ground acceleration (kt/s): the measured value (feed history — negative for a braking shadow)
    /// wins over the type's, as in <see cref="SameRunwaySeparation.WillBeFlying"/>. A simulated
    /// rejected takeoff never arrives here: <see cref="RunwayOccupancy.Classify"/> reads
    /// <see cref="RejectedTakeoffPhase"/> as OnSurface, which blocks outright.
    /// </summary>
    private static double AccelerationKtPerSec(AircraftState aircraft) =>
        RunwayOccupancy.GroundAccelerationKtPerSec(aircraft)
        ?? AircraftPerformance.GroundAccelRate(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));

    /// <summary>Vr in the ground frame (TAS at field altitude minus the headwind) — the speed at which the ground run ends.</summary>
    private static double LiftoffGroundSpeedKts(AircraftState aircraft)
    {
        double vr = AircraftPerformance.RotationSpeed(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
        return WindInterpolator.IasToTas(vr, aircraft.Altitude) - aircraft.HeadwindKts;
    }
}
