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
    /// Goes around when the session setting is on, the arrival is fixed-wing (§3-10-3.a.3 lets visual separation
    /// replace the distance minima for a helicopter), not under a forced landing, inside the decision window, above
    /// <see cref="MinimumAglFt"/>, and a blocking occupant is on its runway. Returns true when a go-around was installed.
    /// </summary>
    public static bool TryTrigger(PhaseContext ctx)
    {
        var arrival = ctx.Aircraft;
        var runway = ctx.Runway;
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

        var occupant = FindBlockingOccupant(ctx.ListAircraft(), arrival, runway, ctx.GroundLayout, seconds);
        if (occupant is null)
        {
            return false;
        }

        Log.LogDebug(
            "[OccupiedRunwayGoAround] {Callsign}: going around, {Occupant} on runway {Runway} ({Seconds:F0}s from threshold, {Agl:F0} ft AGL)",
            arrival.Callsign,
            occupant.Callsign,
            runway.Designator,
            seconds,
            agl
        );
        GoAroundHelper.Trigger(ctx, Pilot.PilotResponder.BuildGoingAroundTrafficOnRunway(arrival));
        return true;
    }

    /// <summary>
    /// The first aircraft on <paramref name="runway"/> the arrival may not cross the landing threshold behind,
    /// judged <paramref name="secondsToThreshold"/> from now with the occupant's present ground speed held.
    /// </summary>
    public static AircraftState? FindBlockingOccupant(
        IReadOnlyList<AircraftState> aircraft,
        AircraftState arrival,
        RunwayInfo runway,
        AirportGroundLayout? layout,
        double secondsToThreshold
    )
    {
        var landingThreshold = LandingThreshold.Resolve(runway, layout);
        double runwayEndFt = runway.PavementLengthFt - LandingThreshold.DisplacementFt(runway, layout);
        var arrivalCategory = SameRunwaySeparation.ResolveSrsCategory(arrival);

        foreach (var other in aircraft)
        {
            if (ReferenceEquals(other, arrival))
            {
                continue;
            }

            var use = RunwayOccupancy.Classify(other, runway, layout);
            if ((use is null) || (use.Kind == RunwayUseKind.ShortFinal))
            {
                continue;
            }

            if (IsBlocking(other, use.Kind, runway, landingThreshold, runwayEndFt, arrivalCategory, secondsToThreshold))
            {
                return other;
            }
        }

        return null;
    }

    private static bool IsBlocking(
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
            return true;
        }

        var occupantCategory = SameRunwaySeparation.ResolveSrsCategory(occupant);
        double downfieldNowFt = GeoMath.AlongTrackDistanceNm(occupant.Position, landingThreshold, runway.TrueHeading) * GeoMath.FeetPerNm;
        double projectedFt = downfieldNowFt + ((occupant.GroundSpeed * GeoMath.FeetPerNm / 3600.0) * secondsToThreshold);

        if (LandedHere(occupant, kind, runway))
        {
            double? exceptionFt = SameRunwaySeparation.RequiredLandingBehindLandingExceptionFt(occupantCategory, arrivalCategory);
            return (exceptionFt is null) || (projectedFt < exceptionFt.Value);
        }

        if (DepartedHere(occupant, kind, runway))
        {
            bool crossedRunwayEnd = projectedFt >= runwayEndFt;
            bool willBeFlying = (!occupant.IsOnGround) || (occupant.GroundSpeed >= RunwayOccupancy.DepartingMinGroundSpeedKts);
            double requiredFt = SameRunwaySeparation.RequiredArrivalBehindDepartureFt(occupantCategory, arrivalCategory);
            return !(crossedRunwayEnd || (willBeFlying && (projectedFt >= requiredFt)));
        }

        return true;
    }

    /// <summary>§3-10-3.a.1 applies: the occupant landed on this runway and is rolling out, exiting, or stopped after landing.</summary>
    private static bool LandedHere(AircraftState occupant, RunwayUseKind kind, RunwayInfo runway) =>
        occupant.Phases?.CurrentPhase switch
        {
            LandingPhase or RunwayExitPhase or StopAndGoPhase or TouchAndGoPhase => UsesThisRunway(occupant, runway),
            null => kind == RunwayUseKind.Landing,
            _ => false,
        };

    /// <summary>§3-10-3.a.2 applies: the occupant is departing from this runway.</summary>
    private static bool DepartedHere(AircraftState occupant, RunwayUseKind kind, RunwayInfo runway) =>
        occupant.Phases?.CurrentPhase switch
        {
            TakeoffPhase or InitialClimbPhase => UsesThisRunway(occupant, runway),
            null => kind == RunwayUseKind.Departing,
            _ => false,
        };

    /// <summary>The occupant's phase runway (departure, else assigned) is this pavement — an exit from a crossing runway earns no credit here.</summary>
    private static bool UsesThisRunway(AircraftState occupant, RunwayInfo runway)
    {
        var own = occupant.Phases?.DepartureRunway ?? occupant.Phases?.AssignedRunway;
        return (own is not null) && Data.NavigationDatabase.AirportIdsMatch(own.AirportId, runway.AirportId) && own.Id.Overlaps(runway.Id);
    }
}
