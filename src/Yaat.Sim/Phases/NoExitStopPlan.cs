using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases;

/// <summary>
/// The stop on the remaining runway a rollout plans when it has no exit it can take (every exit ahead occupied, or none ahead at
/// all), outside LAHSO and a forced landing. One law shared by <see cref="Tower.LandingPhase"/>, which runs it from the moment it
/// knows there is no exit, and <see cref="Ground.RunwayExitPhase"/>, which runs it while rolling with no exit. It is a pure function
/// of the aircraft's speed and position and the runway's geometry, recomputed every tick, so it carries no state across a hand-off
/// or a snapshot.
/// <para>
/// The stop point is the runway end less <see cref="RunwayEndMarginFt"/> (the centroid), or <see cref="LastExitSetbackFt"/> short of
/// the last exit's turn-entry point when that lies nearer and stopping there needs no more than the firm rate; once the aircraft is
/// at or below the standard-exit speed that last-exit stop point holds while firm braking still stops it before the exit's
/// turn-entry point. The aircraft keeps
/// slowing at the routine rollout rate to the standard-exit speed, rolls at it toward the stop point, then follows the routine-rate
/// stopping curve to zero there. It brakes harder only when the distance needs it: up to firm for the stop point, then expedite
/// only when firm cannot keep the nose on the pavement.
/// </para>
/// </summary>
public static class NoExitStopPlan
{
    /// <summary>How far before the end of the landing distance (ft) the no-exit stop puts the centroid.</summary>
    public const double RunwayEndMarginFt = 300.0;

    /// <summary>How far short of the last exit's turn-entry point (ft) the no-exit stop puts the centroid, so the turn stays ahead.</summary>
    public const double LastExitSetbackFt = 50.0;

    /// <summary>The exit angle (degrees) past which an exit doubles back and is not one the aircraft rolls forward to.</summary>
    private const double BackExitAngleDeg = 100.0;

    /// <summary>The most exits the last-exit walk collects; a runway has far fewer.</summary>
    private const int MaxExitWalks = 64;

    /// <summary>
    /// One tick of the stop: the speed target and braking rate to publish, the stop point (ft along from the landing threshold)
    /// and whether it is the last exit's, whether the final stop to it has begun (the target is below the standard-exit speed), and
    /// whether even the max-effort rate cannot keep the nose on the pavement.
    /// </summary>
    public readonly record struct StopStep(
        double TargetSpeedKts,
        double DecelRateKtsPerSec,
        double StopAlongFt,
        bool AtLastExit,
        bool FinalStop,
        bool Excursion
    );

    /// <summary>
    /// The runway the stop plans on, along-track from the landing threshold (ft): the end of the landing distance
    /// (<paramref name="LandingDistanceFt"/>), the farthest the centroid may go with the nose still on the pavement
    /// (<paramref name="NoseLimitAlongFt"/>: the landing distance less half the airframe length), and the last exit's turn-entry
    /// point (<paramref name="LastExitTurnEntryAlongFt"/>, <see cref="LastExitTurnEntryAlongFt"/>; null when there is none).
    /// </summary>
    public readonly record struct StopDatums(double LandingDistanceFt, double NoseLimitAlongFt, double? LastExitTurnEntryAlongFt);

    /// <summary>
    /// The plan for an aircraft of <paramref name="category"/> at <paramref name="groundSpeedKts"/> with its centroid
    /// <paramref name="alongFt"/> past the landing threshold, on the runway <paramref name="datums"/> describes.
    /// </summary>
    public static StopStep Compute(AircraftCategory category, double groundSpeedKts, double alongFt, StopDatums datums)
    {
        double floorKts = CategoryPerformance.StandardExitSpeed(category);
        (double stopAlongFt, bool atLastExit) = StopPoint(category, groundSpeedKts, alongFt, datums);
        double toStopFt = stopAlongFt - alongFt;
        double target = Math.Min(floorKts, RolloutBraking.MaxEntrySpeedKts(toStopFt / GeoMath.FeetPerNm, RoutineRate(category)));
        (double rate, bool excursion) = BrakingRate(category, groundSpeedKts, toStopFt, datums.NoseLimitAlongFt - alongFt);
        return new StopStep(target, rate, stopAlongFt, atLastExit, target < floorKts, excursion);
    }

    /// <summary>
    /// The stop point (ft along) and whether it is the last exit's: <see cref="LastExitSetbackFt"/> short of the last exit's
    /// turn-entry point when that lies before the runway-end stop point and the turn-entry point is still ahead of the centroid,
    /// and either the aircraft is at or below the standard-exit speed with firm braking still able to stop it before that turn-entry
    /// point (the point then holds, past it included, so a stop that rolls a little long does not jump to the runway end) or
    /// stopping there needs no more than the firm rate; else the end of the
    /// landing distance less <see cref="RunwayEndMarginFt"/>.
    /// </summary>
    private static (double StopAlongFt, bool AtLastExit) StopPoint(
        AircraftCategory category,
        double groundSpeedKts,
        double alongFt,
        StopDatums datums
    )
    {
        double runwayEndStopFt = datums.LandingDistanceFt - RunwayEndMarginFt;
        if (datums.LastExitTurnEntryAlongFt is not { } turnEntryFt)
        {
            return (runwayEndStopFt, false);
        }

        double exitStopFt = turnEntryFt - LastExitSetbackFt;
        if ((exitStopFt >= runwayEndStopFt) || (turnEntryFt <= alongFt))
        {
            return (runwayEndStopFt, false);
        }

        double firm = CategoryPerformance.FirmBrakingRate(category);
        bool held =
            (groundSpeedKts <= CategoryPerformance.StandardExitSpeed(category)) && (alongFt + StopDistanceFt(groundSpeedKts, firm) <= turnEntryFt);
        bool makeable = (exitStopFt > alongFt) && (StopRate(groundSpeedKts, exitStopFt - alongFt) <= firm);
        return (held || makeable) ? (exitStopFt, true) : (runwayEndStopFt, false);
    }

    /// <summary>
    /// The distance (ft) braking at <paramref name="rateKtsPerSec"/> takes to stop from <paramref name="groundSpeedKts"/>: v²/(2·a).
    /// </summary>
    private static double StopDistanceFt(double groundSpeedKts, double rateKtsPerSec)
    {
        double speedFps = groundSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double rateFps2 = rateKtsPerSec * GeoMath.FeetPerNm / 3600.0;
        return speedFps * speedFps / (2.0 * rateFps2);
    }

    /// <summary>
    /// The rate for the stop: routine while that stops the aircraft by the stop point, the rate the distance needs up to firm, firm
    /// when only the margin behind the stop point is left to stop in, and past that (or past the stop point) the rate that keeps the
    /// nose on the pavement, no less than firm and no more than the category's max-effort rate; an excursion when even that cannot.
    /// </summary>
    private static (double Rate, bool Excursion) BrakingRate(AircraftCategory category, double groundSpeedKts, double toStopFt, double toNoseEndFt)
    {
        double firm = CategoryPerformance.FirmBrakingRate(category);
        double toEndRate = toNoseEndFt > 0 ? StopRate(groundSpeedKts, toNoseEndFt) : double.PositiveInfinity;
        if (toStopFt > 0)
        {
            double toStopRate = StopRate(groundSpeedKts, toStopFt);
            double routine = RoutineRate(category);
            if (toStopRate <= routine)
            {
                return (routine, false);
            }

            if ((toStopRate <= firm) || (toEndRate <= firm))
            {
                return (Math.Min(toStopRate, firm), false);
            }
        }

        double max = CategoryPerformance.ExpediteExitDecelRate(category);
        return (Math.Min(Math.Max(toEndRate, firm), max), (groundSpeedKts > 0) && (toEndRate > max));
    }

    /// <summary>
    /// The routine rollout rate the stop slows at (<see cref="CategoryPerformance.RolloutDecelRate"/>); a helicopter, whose routine
    /// rate is zero, uses its firm rate so the stop is ever made.
    /// </summary>
    public static double RoutineRate(AircraftCategory category)
    {
        double routine = CategoryPerformance.RolloutDecelRate(category);
        return routine > 0 ? routine : CategoryPerformance.FirmBrakingRate(category);
    }

    private static double StopRate(double groundSpeedKts, double distFt) =>
        RolloutBraking.DecelOverDistanceKtsPerSec(groundSpeedKts, 0, distFt / GeoMath.FeetPerNm);

    /// <summary>
    /// The turn-entry point (ft along from <paramref name="threshold"/>) of the last forward exit ahead of
    /// <paramref name="position"/> off <paramref name="runway"/> (along its true heading), occupied or not, from the layout's
    /// uninstructed exit walk (an exit doubling back past <see cref="BackExitAngleDeg"/> left out); null when there is none ahead. The
    /// turn-entry point is the branch less <see cref="RolloutBraking.TurnEntryLeadNm"/> at the exit's turn-off speed for
    /// <paramref name="category"/>.
    /// </summary>
    public static double? LastExitTurnEntryAlongFt(
        AirportGroundLayout layout,
        RunwayInfo runway,
        LatLon threshold,
        LatLon position,
        AircraftCategory category
    )
    {
        string runwayDesignator = runway.Designator;
        TrueHeading runwayHeading = runway.TrueHeading;
        double? lastFt = null;
        HashSet<int> found = [];
        for (int walk = 0; walk < MaxExitWalks; walk++)
        {
            AirportGroundLayout.CenterlineExitResult? exit = layout.FindOnSidePreferredExit(
                position.Lat,
                position.Lon,
                runwayHeading,
                runwayDesignator,
                preference: null,
                sidePref: null,
                excludeBranchPoints: null,
                excludeHoldShortNodes: found,
                filter: candidate =>
                    candidate.ExitAngle > BackExitAngleDeg ? AirportGroundLayout.CandidateVerdict.Skip : AirportGroundLayout.CandidateVerdict.Accept,
                maxIterations: AirportGroundLayout.DefaultMaxIterations
            );
            if ((exit is not { } hit) || !found.Add(hit.HoldShort.Id))
            {
                break;
            }

            double turnOff = CategoryPerformance.ExitTurnOffSpeed(category, hit.ExitAngle);
            double leadFt = RolloutBraking.TurnEntryLeadNm(category, turnOff, hit.ExitAngle) * GeoMath.FeetPerNm;
            double turnEntryFt = (GeoMath.AlongTrackDistanceNm(hit.Path[0].Position, threshold, runwayHeading) * GeoMath.FeetPerNm) - leadFt;
            lastFt = Math.Max(lastFt ?? double.MinValue, turnEntryFt);
        }

        return lastFt;
    }
}
