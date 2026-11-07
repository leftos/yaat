using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases;

/// <summary>
/// The stop on the remaining runway a rollout plans when it has no exit it can take (every exit ahead occupied, or none ahead at
/// all), outside a forced landing. One law shared by <see cref="Tower.LandingPhase"/>, which runs it from the moment it
/// knows there is no exit (outside LAHSO, whose landing stops at its own point), and <see cref="Ground.RunwayExitPhase"/>, which runs it
/// while rolling with no exit, a LAHSO limit carried past the hand-off included (<see cref="StopDatums.LahsoStopAlongFt"/>). It is a pure function
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
    /// whether even the max-effort rate cannot keep the nose on the pavement (short of the LAHSO point under LAHSO), and whether the
    /// stop point is the LAHSO stop point (<paramref name="AtLahsoPoint"/>, <see cref="StopDatums.LahsoStopAlongFt"/>).
    /// </summary>
    public readonly record struct StopStep(
        double TargetSpeedKts,
        double DecelRateKtsPerSec,
        double StopAlongFt,
        bool AtLastExit,
        bool FinalStop,
        bool Excursion,
        bool AtLahsoPoint
    );

    /// <summary>
    /// The runway the stop plans on, along-track from the landing threshold (ft): the end of the landing distance
    /// (<paramref name="LandingDistanceFt"/>), the farthest the centroid may go with the nose still on the pavement
    /// (<paramref name="NoseLimitAlongFt"/>: the landing distance less half the airframe length; under LAHSO the nearer of that and
    /// the hold-short point less half the airframe length), the last exit's turn-entry point (<paramref name="LastExitTurnEntryAlongFt"/>,
    /// <see cref="LastExitTurnEntryAlongFt"/>; null when there is none), and under a LAHSO clearance the centroid's stop point short of
    /// the hold-short point (<paramref name="LahsoStopAlongFt"/>; null without LAHSO), and after a LAHSO clearance was voided by the
    /// overrun call the stretch the centroid must not come to rest in (<paramref name="KeepOutAlongFt"/>: the crossing runway's
    /// pavement widened by half the airframe each side, so neither nose nor tail rests on it; null otherwise).
    /// </summary>
    public readonly record struct StopDatums(
        double LandingDistanceFt,
        double NoseLimitAlongFt,
        double? LastExitTurnEntryAlongFt,
        double? LahsoStopAlongFt,
        (double StartFt, double EndFt)? KeepOutAlongFt
    );

    /// <summary>
    /// The plan for an aircraft of <paramref name="category"/> at <paramref name="groundSpeedKts"/> with its centroid
    /// <paramref name="alongFt"/> past the landing threshold, on the runway <paramref name="datums"/> describes.
    /// </summary>
    public static StopStep Compute(AircraftCategory category, double groundSpeedKts, double alongFt, StopDatums datums)
    {
        double floorKts = CategoryPerformance.StandardExitSpeed(category);
        (double stopAlongFt, bool atLastExit) = StopPoint(category, groundSpeedKts, alongFt, datums);
        bool atLahsoPoint = false;

        // A LAHSO clearance never lets the stop pass the hold-short point (AIM 4-3-11.b.6): the stop point is the nearer of the
        // plan's and the LAHSO stop point, so a plan giving up on the last exit falls back to the LAHSO point, not the runway end.
        if ((datums.LahsoStopAlongFt is { } lahsoStopFt) && (lahsoStopFt < stopAlongFt))
        {
            (stopAlongFt, atLastExit, atLahsoPoint) = (lahsoStopFt, false, true);
        }

        double noseLimitAlongFt = datums.NoseLimitAlongFt;
        if (KeepOff(category, groundSpeedKts, alongFt, datums, stopAlongFt) is { } moved)
        {
            (stopAlongFt, atLastExit, noseLimitAlongFt) = (moved.StopAlongFt, false, Math.Min(noseLimitAlongFt, moved.NoseLimitAlongFt));
        }

        double toStopFt = stopAlongFt - alongFt;
        double target = Math.Min(floorKts, RolloutBraking.MaxEntrySpeedKts(toStopFt / GeoMath.FeetPerNm, RoutineRate(category)));
        (double rate, bool excursion) = BrakingRate(category, groundSpeedKts, toStopFt, noseLimitAlongFt - alongFt);
        return new StopStep(target, rate, stopAlongFt, atLastExit, target < floorKts, excursion, atLahsoPoint);
    }

    /// <summary>
    /// After a voided LAHSO clearance the stop never rests on the crossing runway (AIM 4-3-21.b): a stop point
    /// (<paramref name="stopAlongFt"/>) inside the keep-out (<see cref="StopDatums.KeepOutAlongFt"/>) moves short of it while max
    /// effort still stops there, the nose then limited to its start, else beyond it (no new nose limit: +∞). Null when the stop point
    /// is not inside a keep-out.
    /// </summary>
    private static (double StopAlongFt, double NoseLimitAlongFt)? KeepOff(
        AircraftCategory category,
        double groundSpeedKts,
        double alongFt,
        StopDatums datums,
        double stopAlongFt
    )
    {
        if ((datums.KeepOutAlongFt is not { } keepOut) || (stopAlongFt <= keepOut.StartFt) || (stopAlongFt >= keepOut.EndFt))
        {
            return null;
        }

        double shortFt = keepOut.StartFt - alongFt;
        bool canStopShort = (shortFt > 0) && (StopRate(groundSpeedKts, shortFt) <= CategoryPerformance.ExpediteExitDecelRate(category));
        return canStopShort ? (keepOut.StartFt, keepOut.StartFt) : (keepOut.EndFt, double.PositiveInfinity);
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
    /// Where <see cref="LastExitTurnEntryAlongFt"/> looks: the <paramref name="Layout"/> and <paramref name="Runway"/> rolled on, the
    /// landing <paramref name="Threshold"/> distances are measured from, the aircraft's <paramref name="Position"/>, and its
    /// <paramref name="Category"/> (for the turn-off speed and lead).
    /// </summary>
    public readonly record struct LastExitQuery(
        AirportGroundLayout Layout,
        RunwayInfo Runway,
        LatLon Threshold,
        LatLon Position,
        AircraftCategory Category
    );

    /// <summary>
    /// The turn-entry point (ft along from the query's threshold) of the last forward exit ahead of the query's position off its
    /// runway (along the runway's true heading), occupied or not, from the layout's uninstructed exit walk (an exit doubling back past
    /// <see cref="BackExitAngleDeg"/> left out); null when there is none ahead. The turn-entry point is the branch less
    /// <see cref="RolloutBraking.TurnEntryLeadNm"/> at the exit's turn-off speed for the query's category. With
    /// <paramref name="qualifies"/> only an exit it accepts counts (under LAHSO, one whose turn-off fits before the hold-short point:
    /// an exit past it can never be taken, so it is never the last exit either); null accepts every forward exit.
    /// </summary>
    public static double? LastExitTurnEntryAlongFt(LastExitQuery query, Func<AirportGroundLayout.CenterlineExitResult, bool>? qualifies)
    {
        (AirportGroundLayout layout, RunwayInfo runway, LatLon threshold, LatLon position, AircraftCategory category) = query;
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
                    ((candidate.ExitAngle > BackExitAngleDeg) || ((qualifies is not null) && !qualifies(candidate)))
                        ? AirportGroundLayout.CandidateVerdict.Skip
                        : AirportGroundLayout.CandidateVerdict.Accept,
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
