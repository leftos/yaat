using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Training;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Pilot-initiated go-around for an arrival on short final whose runway is occupied. AIM 5-2-5.9: "never land on a
/// runway that is occupied by another aircraft, even if a landing clearance was issued"; AIM 5-5-5.a.1(b)/a.2: the
/// pilot goes around when a safe landing is not possible and says why. The trigger is the tower's own unit —
/// seconds to the landing threshold (§3-9-6, §3-9-7 are written in time) — so a 70-kt trainer and a 140-kt jet get
/// the same decision window (about 235 ft AGL for the trainer, 420 ft for the jet on a 3° path).
///
/// §3-10-3 is a "does not cross the landing threshold until" rule, so the occupant is judged where it will be when the
/// arrival reaches the threshold (present ground speed held), and the exception that applies depends on what the
/// occupant did on this runway: landed here → a.1 (landmark distance, none when either aircraft is Category III);
/// departed here → a.2 (airborne, or still rolling and projected past the landmark); anything else on the pavement
/// (lined up, holding in position, crossing, parked) has no exception at any distance. Simplifications, stated:
/// a.1's exception is a daytime rule and the sim has no clock; intersecting runways (§3-10-4) are not classified;
/// a rollout is projected at constant speed; an altitude-restricted low approach (§3-10-10) is not exempted because
/// the sim's low approach is flown well below the 500 ft that paragraph requires; there is no balked landing from
/// the flare (the window closes at threshold-crossing height).
/// </summary>
internal static class OccupiedRunwayGoAround
{
    private static readonly ILogger Log = SimLog.CreateLogger("OccupiedRunwayGoAround");

    /// <summary>Decision window: the pilot commits to going around inside this many seconds from the threshold.</summary>
    public const double DecisionWindowSeconds = 30.0;

    /// <summary>Below threshold-crossing height the aircraft is landing; it no longer goes around for traffic.</summary>
    public const double MinimumAglFt = RunwayOccupancy.LandingAglCeilingFt;

    /// <summary>
    /// Ground speed (kts) at or below which an occupant counts as stopped and can never be projected clear of the
    /// runway, whatever its exit plan says. The vacate arithmetic already returns "never" for an aircraft with
    /// distance left and no speed; this closes the one case it cannot — an aircraft standing still past its exit's
    /// branch point, where only the exit leg is left and that leg is flown at the exit's planned turn-off speed.
    /// </summary>
    private const double StoppedGroundSpeedKts = 1.0;

    /// <summary>
    /// The occupant that refuses the arrival its threshold crossing, with the §3-10-3 branch that refused it —
    /// "landing rollout, 4,700 ft down, not clear in 30 s" — for the terminal line. The pilot's transmission stays
    /// the generic "going around, traffic on the runway" (a pilot does not read out the blocker's callsign), so this
    /// is the only place the instructor is told which aircraft it was.
    /// </summary>
    public sealed record BlockingOccupant(AircraftState Aircraft, string Reason);

    /// <summary>
    /// Goes around when the session setting is on, the arrival is fixed-wing (§3-10-3.a.3 lets visual separation
    /// replace the distance minima for a helicopter), not under a forced landing, inside the decision window, above
    /// <see cref="MinimumAglFt"/>, and a blocking occupant is on its runway. Returns true when a go-around was installed.
    /// </summary>
    public static bool TryTrigger(PhaseContext ctx)
    {
        AircraftState arrival = ctx.Aircraft;
        RunwayInfo? runway = ctx.Runway;
        if (
            (!ctx.AutoGoAroundOnOccupiedRunway)
            || (ctx.ListAircraft is null)
            || (runway is null)
            || (arrival.Phases?.ForceLanding == true)
            || (ctx.Category == AircraftCategory.Helicopter)
        )
        {
            return false;
        }

        double agl = arrival.Altitude - runway.ElevationFt;
        double seconds = RunwayOccupancy.SecondsToLandingThreshold(arrival, runway, ctx.GroundLayout);
        if ((agl < MinimumAglFt) || (seconds <= 0) || (seconds > DecisionWindowSeconds))
        {
            return false;
        }

        BlockingOccupant? blocker = FindBlockingOccupant(ctx.ListAircraft(), arrival, runway, ctx.GroundLayout, seconds);
        if (blocker is null)
        {
            return false;
        }

        Log.LogDebug(
            "[OccupiedRunwayGoAround] {Callsign}: going around, {Occupant} on runway {Runway} — {Reason} ({Seconds:F0}s from threshold, {Agl:F0} ft AGL)",
            arrival.Callsign,
            blocker.Aircraft.Callsign,
            runway.Designator,
            blocker.Reason,
            seconds,
            agl
        );
        GoAroundHelper.Trigger(ctx, Pilot.PilotResponder.BuildGoingAroundTrafficOnRunway(arrival));
        arrival.PendingWarnings.Add($"{arrival.Callsign} go-around: {blocker.Aircraft.Callsign} on {runway.Designator} ({blocker.Reason})");
        return true;
    }

    /// <summary>
    /// The first aircraft on <paramref name="runway"/> the arrival may not cross the landing threshold behind,
    /// judged <paramref name="secondsToThreshold"/> from now with the occupant's present ground speed held.
    /// </summary>
    public static BlockingOccupant? FindBlockingOccupant(
        IReadOnlyList<AircraftState> aircraft,
        AircraftState arrival,
        RunwayInfo runway,
        AirportGroundLayout? layout,
        double secondsToThreshold
    )
    {
        LatLon landingThreshold = LandingThreshold.Resolve(runway, layout);
        double runwayEndFt = runway.PavementLengthFt - LandingThreshold.DisplacementFt(runway, layout);
        SrsCategory arrivalCategory = SameRunwaySeparation.ResolveSrsCategory(arrival);

        foreach (AircraftState other in aircraft)
        {
            if (ReferenceEquals(other, arrival))
            {
                continue;
            }

            RunwayUse? use = RunwayOccupancy.Classify(other, runway, layout);
            if ((use is null) || (use.Kind == RunwayUseKind.ShortFinal))
            {
                continue;
            }

            if (BlockingReason(other, use.Kind, runway, landingThreshold, runwayEndFt, arrivalCategory, secondsToThreshold) is { } reason)
            {
                return new BlockingOccupant(other, reason);
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="occupant"/> refuses the arrival its threshold crossing, or null when §3-10-3 is satisfied
    /// and it may land behind it.
    /// </summary>
    private static string? BlockingReason(
        AircraftState occupant,
        RunwayUseKind kind,
        RunwayInfo runway,
        LatLon landingThreshold,
        double runwayEndFt,
        SrsCategory arrivalCategory,
        double secondsToThreshold
    )
    {
        // §3-10-3.a.1/a.2's exceptions are written in SRS Categories I–III — fixed-wing classes — and a.3's helicopter
        // relief is for the succeeding aircraft only. A preceding rotorcraft (hovering, descending, air-taxiing) has no
        // codified exception at any distance: the runway must be clear.
        if (RunwayOccupancy.IsRotorcraft(occupant))
        {
            return "rotorcraft on the runway";
        }

        SrsCategory occupantCategory = SameRunwaySeparation.ResolveSrsCategory(occupant);
        double downfieldNowFt = GeoMath.AlongTrackDistanceNm(occupant.Position, landingThreshold, runway.TrueHeading) * GeoMath.FeetPerNm;
        double projectedFt = downfieldNowFt + ((occupant.GroundSpeed * GeoMath.FeetPerNm / 3600.0) * secondsToThreshold);

        // The occupant is judged where it will be when the arrival crosses the threshold: still on the pavement then
        // (the classifier already said so now) and, for a departure, flying by then if it is airborne or rolling now.
        if (LandedHere(occupant, kind, runway))
        {
            bool satisfied = SameRunwaySeparation.ArrivalBehindLandingSatisfied(
                WillBeClearOfRunway(occupant, secondsToThreshold),
                landerOnGround: true,
                projectedFt,
                occupantCategory,
                arrivalCategory
            );
            return satisfied ? null : $"landing rollout, {RoundedFeet(downfieldNowFt)} ft down, not clear in {secondsToThreshold:F0} s";
        }

        if (DepartedHere(occupant, kind, runway))
        {
            bool satisfied = SameRunwaySeparation.ArrivalBehindDepartureSatisfied(
                departureCrossedRunwayEnd: projectedFt >= runwayEndFt,
                SameRunwaySeparation.WillBeFlying(occupant, secondsToThreshold),
                projectedFt,
                occupantCategory,
                arrivalCategory
            );
            return satisfied ? null : $"departing, {RoundedFeet(downfieldNowFt)} ft down";
        }

        // Everything else on the pavement — lined up, holding in position, crossing, parked — has no §3-10-3
        // exception at any distance, and is refused without projecting where it will be. That is also what keeps the
        // §3-10-6.b carve-out satisfied: anticipating separation must not be applied to LUAW operations, and a
        // lined-up occupant classifies OnSurface and reaches this branch instead of WillBeClearOfRunway. Extending
        // the projection here would break that.
        return $"{DescribeUse(kind)}, {RoundedFeet(downfieldNowFt)} ft down";
    }

    /// <summary>
    /// Will the occupant be clear of the runway by the time the arrival crosses the threshold? §3-10-3.a.1 is a
    /// threshold-crossing rule — "the arriving aircraft does not cross the landing threshold until … the other
    /// aircraft has landed and is clear of the runway" — so the question is asked about the moment of the crossing,
    /// not about now, <see cref="DecisionWindowSeconds"/> earlier. §3-10-6.a is what authorises <em>anticipating</em>
    /// that separation rather than waiting to observe it: "landing clearance to succeeding aircraft in a landing
    /// sequence need not be withheld if you observe the positions of the aircraft and determine that prescribed
    /// runway separation will exist when the aircraft crosses the landing threshold". Requiring the leader to be
    /// physically clear at the decision point is stricter than both and than tower practice, where the arrival is
    /// told to continue and the landing clearance comes late.
    ///
    /// <para>The estimate comes from <see cref="SameRunwayArrivalProtection.TryBuildRollout"/> — the same arithmetic
    /// the simulated-TRACON spacing pass uses to decide when a leader is off the runway, so the two cannot disagree.
    /// Fail closed: an occupant with no resolved exit, no ground layout to resolve one from, or no ground speed to
    /// cover the distance is <em>not</em> clear, and the go-around fires exactly as it did before.</para>
    /// </summary>
    private static bool WillBeClearOfRunway(AircraftState occupant, double secondsToThreshold) =>
        (occupant.GroundSpeed > StoppedGroundSpeedKts)
        && (SameRunwayArrivalProtection.TryBuildRollout(occupant, elapsedSinceThresholdSeconds: 0.0) is { } rollout)
        && (SameRunwayArrivalProtection.SecondsToRunwayClear(rollout) <= secondsToThreshold);

    /// <summary>The occupant's runway use in the instructor's words, for an occupant with no §3-10-3 exception at all.</summary>
    private static string DescribeUse(RunwayUseKind kind) =>
        kind switch
        {
            RunwayUseKind.Departing => "departure roll",
            RunwayUseKind.Landing => "over the runway",
            RunwayUseKind.Crossing => "crossing the runway",
            _ => "on the runway",
        };

    /// <summary>Distance down the runway to the nearest 100 ft — the terminal line carries no false precision.</summary>
    private static string RoundedFeet(double feet) =>
        (Math.Round(feet / 100.0) * 100.0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>§3-10-3.a.1 applies: the occupant landed on this runway and is rolling out, exiting, or stopped after landing.</summary>
    private static bool LandedHere(AircraftState occupant, RunwayUseKind kind, RunwayInfo runway) =>
        SameRunwaySeparation.IsLandingFamilyOccupant(occupant.Phases?.CurrentPhase, kind)
        && ((occupant.Phases is null) || UsesThisRunway(occupant, runway));

    /// <summary>§3-10-3.a.2 applies: the occupant is departing from this runway.</summary>
    private static bool DepartedHere(AircraftState occupant, RunwayUseKind kind, RunwayInfo runway) =>
        SameRunwaySeparation.IsDepartureFamilyOccupant(occupant.Phases?.CurrentPhase, kind)
        && ((occupant.Phases is null) || UsesThisRunway(occupant, runway));

    /// <summary>The occupant's phase runway (departure, else assigned) is this pavement — an exit from a crossing runway earns no credit here.</summary>
    private static bool UsesThisRunway(AircraftState occupant, RunwayInfo runway)
    {
        RunwayInfo? own = occupant.Phases?.DepartureRunway ?? occupant.Phases?.AssignedRunway;
        return (own is not null) && Data.NavigationDatabase.AirportIdsMatch(own.AirportId, runway.AirportId) && own.Id.Overlaps(runway.Id);
    }
}
