using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Pilot-initiated rejected takeoff for a departure whose runway is blocked ahead, plus the
/// shared install used by the controller's CTOC mid-roll abort — one construction site for both
/// entry points (the go-around's two-site divergence, issue #283, is the cautionary precedent).
///
/// The decision follows the FAA/industry Takeoff Safety Training Aid (the basis of AC 120-62):
/// below <see cref="CategoryPerformance.LowSpeedRejectThresholdKts"/> the crew rejects for any
/// blocking occupant ahead — stopping is cheap; at or above it a reject is reserved for an
/// airplane unable to fly, which a runway blocked inside the liftoff-plus-climb margin
/// (<see cref="CanOverfly"/>) is. V1 (14 CFR 25.107(a)(2)) bounds the controller's CTOC, not
/// this trigger: past V1 the roll continues over an overflyable occupant, but when collision is
/// otherwise certain the pilot rejects anyway (AIM 4-4-1.a — a clearance never authorizes
/// unsafe operation; 14 CFR 91.3(a) — the pilot in command is the final authority as to the
/// operation of the aircraft). Before the roll is underway at all the pilot simply declines the
/// clearance — §3-9-6.a constrains beginning the takeoff roll, and P/CG ABORT terminates a
/// maneuver that has begun — so a standstill blocker draws "unable" and a hold, not an abort.
///
/// Blocking occupants come from <see cref="RunwayOccupancy.Classify"/>: anything OnSurface or
/// Landing always blocks; a Crossing blocks unless projected past the runway holding-position
/// standoff (P/CG CLEAR OF THE RUNWAY) by the time the departure arrives; a preceding Departing
/// aircraft never blocks here — an interim simplification tracked as issue #416 (§3-9-5 only
/// authorizes issuing the clearance in anticipation of separation; §3-9-6.a's landmark
/// distances behind a preceding departure are not yet projected); airborne approach traffic
/// (ShortFinal/OnFinal) is the go-around logic's problem. Stated simplifications: the sim pilot
/// always sees the occupant (no visibility model), and occupants on intersecting runways
/// (§3-9-8) are not considered. <see cref="StopMarginFt"/> stands in for the unmodeled aircraft
/// length: every distance test measures to the occupant's reference point minus that margin, so
/// the roll is judged against its tail, not its GPS antenna.
/// </summary>
internal static class RejectedTakeoff
{
    private static readonly ILogger Log = SimLog.CreateLogger("RejectedTakeoff");

    /// <summary>
    /// Delay between the reject decision and the first braking action, during which the roll
    /// keeps accelerating. Sized from 14 CFR 25.109(a)(2)(iii)'s all-engines accelerate-stop
    /// element — the reg adds a *distance* equivalent to 2 seconds at V1 rather than modeling a
    /// delay; integrating 2 s of continued acceleration instead yields slightly more distance,
    /// i.e. errs conservative. Used in both the prediction and the execution.
    /// </summary>
    public const double ReactionSeconds = 2.0;

    /// <summary>Stop margin (ft) short of the occupant's reference point — a widebody length; the sim has no per-type length model.</summary>
    public const double StopMarginFt = 500.0;

    /// <summary>
    /// Groundspeed (kts) below which the takeoff roll has not meaningfully begun: a blocker found
    /// here is declined ("unable"), not aborted — there is no maneuver to terminate yet.
    /// </summary>
    public const double RollUnderwayMinKts = 5.0;

    /// <summary>
    /// Lateral standoff (ft) from the runway centerline a crossing aircraft must be projected past
    /// to count as clear: the runway holding-position marking datum (P/CG CLEAR OF THE RUNWAY;
    /// hold lines sit roughly this far out at transport airports), not merely the pavement edge —
    /// a fuselage just off the asphalt is not separation.
    /// </summary>
    public const double ClearOfRunwayStandoffFt = 250.0;

    /// <summary>kt·s → ft (a kt²/(kt/s) speed-squared-over-decel quotient is in kt·s).</summary>
    private const double KtSecondsToFt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>
    /// Rejects (or, from a standstill, declines) the takeoff when a blocking occupant sits on the
    /// runway ahead and doctrine says stop. Called from <see cref="TakeoffPhase"/> each
    /// ground-roll tick; gated by the session setting. Helicopters never auto-reject (no rolling
    /// takeoff, Vr = 0 degenerates the whole decision) and live-traffic shadows are feed-driven.
    /// Returns true when the takeoff phase is over (reject installed or clearance declined).
    /// </summary>
    public static bool TryTrigger(PhaseContext ctx)
    {
        var dep = ctx.Aircraft;
        if ((!ctx.AutoRejectTakeoffOnOccupiedRunway) || (ctx.ListAircraft is null) || (ctx.Category == AircraftCategory.Helicopter) || (dep.IsShadow))
        {
            return false;
        }

        var runway = dep.Phases?.DepartureRunway ?? ctx.Runway;
        if (runway is null)
        {
            return false;
        }

        var occupant = FindBlockingOccupant(ctx.ListAircraft(), dep, runway, ctx.GroundLayout, out double distanceFt);
        if (occupant is null)
        {
            return false;
        }

        double effectiveDistanceFt = distanceFt - StopMarginFt;
        if (!ShouldReject(dep, ctx.Category, effectiveDistanceFt))
        {
            return false;
        }

        if (dep.GroundSpeed < RollUnderwayMinKts)
        {
            // The roll has not begun — the pilot declines the clearance and holds (§3-9-6.a is a
            // "does not begin takeoff roll" rule; there is no maneuver to abort yet).
            Log.LogDebug(
                "[RejectedTakeoff] {Callsign}: declining takeoff clearance, {Occupant} on runway {Runway} {Distance:F0} ft ahead",
                dep.Callsign,
                occupant.Callsign,
                runway.Designator,
                distanceFt
            );
            Route(ctx, Pilot.PilotResponder.BuildUnable(dep, "traffic on the runway"));
            Install(ctx);
            return true;
        }

        Log.LogDebug(
            "[RejectedTakeoff] {Callsign}: rejecting takeoff, {Occupant} on runway {Runway} {Distance:F0} ft ahead (gs={Gs:F0} kt)",
            dep.Callsign,
            occupant.Callsign,
            runway.Designator,
            distanceFt,
            dep.GroundSpeed
        );
        Route(ctx, Pilot.PilotResponder.BuildRejectingTakeoffTrafficOnRunway(dep));
        var phase = Install(ctx);
        if (phase is not null)
        {
            phase.AutoTriggered = true;
            WarnIfCannotStopShort(dep, ctx.Category, occupant, effectiveDistanceFt, phase);
        }

        return true;
    }

    private static void Route(PhaseContext ctx, Pilot.PilotSpeechText speech)
    {
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            speech,
            Pilot.PilotResponder.SoloPositionsTower
        );
    }

    /// <summary>
    /// The nearest aircraft ahead on <paramref name="runway"/> the departure may not roll toward,
    /// with its along-runway distance (ft) from the departure's position. Occupants behind the
    /// departure point (an intersection departure's full-length queue) fall out on the
    /// along-track sign; an opposite-direction aircraft lined up on the far end of the same
    /// pavement correctly blocks.
    /// </summary>
    public static AircraftState? FindBlockingOccupant(
        IReadOnlyList<AircraftState> aircraft,
        AircraftState departure,
        RunwayInfo runway,
        AirportGroundLayout? layout,
        out double distanceFt
    )
    {
        distanceFt = double.MaxValue;
        AircraftState? nearest = null;

        foreach (var other in aircraft)
        {
            if (ReferenceEquals(other, departure))
            {
                continue;
            }

            var use = RunwayOccupancy.Classify(other, runway, layout);
            if (use is null)
            {
                continue;
            }

            double dFt = GeoMath.AlongTrackDistanceNm(other.Position, departure.Position, runway.TrueHeading) * GeoMath.FeetPerNm;
            if (dFt <= 0)
            {
                continue;
            }

            bool blocks = use.Kind switch
            {
                RunwayUseKind.OnSurface => true,
                RunwayUseKind.Landing => true,
                RunwayUseKind.Crossing => !ProjectedClearOfRunway(other, runway, ArrivalTimeSeconds(departure, dFt)),
                _ => false,
            };

            if (blocks && (dFt < distanceFt))
            {
                distanceFt = dFt;
                nearest = other;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Whether a rolling departure with a blocking occupant <paramref name="effectiveDistanceFt"/>
    /// ahead rejects: always in the low-speed regime; in the high-speed regime (V1 included) only
    /// when the occupant cannot be overflown.
    /// </summary>
    public static bool ShouldReject(AircraftState departure, AircraftCategory cat, double effectiveDistanceFt)
    {
        double ias = GroundFrame.IasForGroundSpeed(departure, departure.IndicatedAirspeed);
        if (ias < CategoryPerformance.LowSpeedRejectThresholdKts(cat))
        {
            return true;
        }

        return !CanOverfly(departure, cat, effectiveDistanceFt);
    }

    /// <summary>
    /// The departure will be past its liftoff point by more than the category's climb margin
    /// before reaching the occupant: distance to reach Vr (ground frame, v_f² = v_i² + 2ad) plus
    /// <see cref="CategoryPerformance.RejectedTakeoffOverflyMarginFt"/> fits inside
    /// <paramref name="effectiveDistanceFt"/>.
    /// </summary>
    public static bool CanOverfly(AircraftState departure, AircraftCategory cat, double effectiveDistanceFt)
    {
        double vr = AircraftPerformance.RotationSpeed(departure.AircraftType, cat);
        double accel = AircraftPerformance.GroundAccelRate(departure.AircraftType, cat);
        if ((accel <= 0) || (vr <= 0))
        {
            return false;
        }

        // Liftoff happens at Vr indicated; the roll integrates groundspeed, so convert the Vr
        // endpoint into the ground frame (TAS at field altitude minus headwind).
        double gs = departure.GroundSpeed;
        double gsAtVr = Math.Max(gs, WindInterpolator.IasToTas(vr, departure.Altitude) - departure.HeadwindKts);
        double liftDistanceFt = ((gsAtVr * gsAtVr) - (gs * gs)) / (2.0 * accel) * KtSecondsToFt;
        return (liftDistanceFt + CategoryPerformance.RejectedTakeoffOverflyMarginFt(cat)) <= effectiveDistanceFt;
    }

    /// <summary>
    /// Whether a reject started now stops inside <paramref name="effectiveDistanceFt"/>: the
    /// reaction window's continued acceleration plus the max-effort braking run (v²/2a).
    /// </summary>
    public static bool CanStopShort(AircraftState departure, AircraftCategory cat, double effectiveDistanceFt)
    {
        double accel = AircraftPerformance.GroundAccelRate(departure.AircraftType, cat);
        double gs = departure.GroundSpeed;
        double gsAfterReaction = gs + Math.Max(0, accel) * ReactionSeconds;
        double reactionFt = (gs + gsAfterReaction) / 2.0 * ReactionSeconds * KtSecondsToFt;
        double decel = Math.Max(CategoryPerformance.RejectedTakeoffDecelRate(cat), 1.0);
        double brakeFt = (gsAfterReaction * gsAfterReaction) / (2.0 * decel) * KtSecondsToFt;
        return (reactionFt + brakeFt) <= effectiveDistanceFt;
    }

    /// <summary>
    /// Surfaces the outcome the feature exists to prevent: a reject whose predicted stop point
    /// runs past the blocking occupant. Non-blocking — the physics stays honest and the braking
    /// proceeds — but the instructor gets an amber line now and the solo evaluator a Safety
    /// finding (§3-9-6.a) via <see cref="RejectedTakeoffPhase.CannotStopShortOf"/>.
    /// </summary>
    public static void WarnIfCannotStopShort(
        AircraftState departure,
        AircraftCategory cat,
        AircraftState occupant,
        double effectiveDistanceFt,
        RejectedTakeoffPhase phase
    )
    {
        if (CanStopShort(departure, cat, effectiveDistanceFt))
        {
            return;
        }

        phase.CannotStopShortOf = occupant.Callsign;
        departure.PendingWarnings.Add(
            $"{departure.Callsign} is rejecting the takeoff but cannot stop short of {occupant.Callsign} on the runway (7110.65 3-9-6.a)"
        );
        Log.LogDebug("[RejectedTakeoff] {Callsign}: cannot stop short of {Occupant}", departure.Callsign, occupant.Callsign);
    }

    /// <summary>
    /// CTOC support: the blocking occupant ahead that cannot be overflown — the one condition
    /// under which a cancellation at or past V1 is accepted rather than refused with "unable".
    /// Without a world list or a runway there is no occupant to weigh, so the normal past-V1
    /// refusal stands. Outputs the occupant and the margin-adjusted distance so the caller can
    /// run the same cannot-stop-short check the automatic path does.
    /// </summary>
    public static bool TryFindBlockedBeyondOverfly(
        AircraftState departure,
        AircraftCategory cat,
        Commands.DispatchContext ctx,
        out AircraftState? occupant,
        out double effectiveDistanceFt
    )
    {
        occupant = null;
        effectiveDistanceFt = double.MaxValue;
        if (ctx.ListAircraft is null)
        {
            return false;
        }

        var runway = departure.Phases?.DepartureRunway ?? departure.Phases?.AssignedRunway;
        if (runway is null)
        {
            return false;
        }

        occupant = FindBlockingOccupant(ctx.ListAircraft(), departure, runway, ctx.GroundLayout, out double distanceFt);
        effectiveDistanceFt = distanceFt - StopMarginFt;
        return (occupant is not null) && !CanOverfly(departure, cat, effectiveDistanceFt);
    }

    /// <summary>
    /// Replaces the upcoming phases with the rejected-takeoff braking phase and its terminal
    /// hold: brake on the centerline to a stop, then <see cref="HoldingInPositionPhase"/>
    /// awaiting an ATC instruction (movement on the movement area needs ATC approval,
    /// AIM 4-3-18.a / §3-7-2 — the sim's crew always waits rather than self-vacating). A
    /// standstill (below <see cref="RollUnderwayMinKts"/>) skips the braking phase and holds
    /// where it is. The takeoff clearance is spent either way: a fresh CTO (or LUAW, then CTO)
    /// is required for another attempt. Shared by the automatic trigger and the CTOC handler;
    /// returns the braking phase, or null for the standstill hold.
    /// </summary>
    public static RejectedTakeoffPhase? Install(PhaseContext ctx)
    {
        var dep = ctx.Aircraft;
        if (dep.Phases is null)
        {
            return null;
        }

        dep.Targets.AssignedAltitude = null;
        dep.Targets.TargetSpeed = null;

        if (dep.GroundSpeed < RollUnderwayMinKts)
        {
            dep.IndicatedAirspeed = 0;
            dep.Phases.ReplaceUpcoming([new HoldingInPositionPhase()]);
            dep.Phases.AdvanceToNext(ctx);
            FlightPhysics.NotifyPhaseAdvanced(dep);
            return null;
        }

        var reject = new RejectedTakeoffPhase();
        dep.Phases.ReplaceUpcoming([reject, new HoldingInPositionPhase()]);
        dep.Phases.AdvanceToNext(ctx);
        FlightPhysics.NotifyPhaseAdvanced(dep);
        return reject;
    }

    /// <summary>
    /// Seconds for the departure to cover <paramref name="dFt"/> at its present groundspeed while
    /// still accelerating at takeoff thrust (d = v·t + ½at²).
    /// </summary>
    private static double ArrivalTimeSeconds(AircraftState departure, double dFt)
    {
        double vFps = departure.GroundSpeed * KtSecondsToFt;
        double aFps2 =
            AircraftPerformance.GroundAccelRate(departure.AircraftType, AircraftCategorization.Categorize(departure.AircraftType)) * KtSecondsToFt;
        if (aFps2 <= 0)
        {
            return vFps > 0 ? dFt / vFps : double.PositiveInfinity;
        }

        return (-vFps + Math.Sqrt((vFps * vFps) + (2.0 * aFps2 * dFt))) / aFps2;
    }

    /// <summary>
    /// A crossing aircraft projected at its present speed and heading is past the runway
    /// holding-position standoff (<see cref="ClearOfRunwayStandoffFt"/>) by the time the
    /// departure arrives — clear of the runway in the P/CG sense, not merely off the asphalt.
    /// </summary>
    private static bool ProjectedClearOfRunway(AircraftState occupant, RunwayInfo runway, double arrivalSeconds)
    {
        if (double.IsInfinity(arrivalSeconds))
        {
            return false;
        }

        var projected = GeoMath.ProjectPoint(
            occupant.Position.Lat,
            occupant.Position.Lon,
            occupant.TrueHeading,
            occupant.GroundSpeed * arrivalSeconds / 3600.0
        );
        double lateralFt = GeoMath.DistanceToSegmentFt(
            new LatLon(projected.Lat, projected.Lon),
            new LatLon(runway.Lat1, runway.Lon1),
            new LatLon(runway.Lat2, runway.Lon2)
        );
        return lateralFt > ClearOfRunwayStandoffFt;
    }
}
