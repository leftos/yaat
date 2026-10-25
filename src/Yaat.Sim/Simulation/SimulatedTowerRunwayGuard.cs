using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Whether the silently simulated tower may clear a departure for takeoff right now. It waits while:
/// <list type="bullet">
/// <item>anyone else is departing, landing, on, crossing, on short final to or fouling the same physical runway, either end
/// (7110.65 3-9-6.a/b, 3-1-3);</item>
/// <item>an arrival to that runway is inside the controller-AI crossing gate's final gate
/// (<see cref="RunwayCrossingGate.FinalGateNm"/> or <see cref="RunwayCrossingGate.FinalGateSeconds"/> from the threshold,
/// 7110.65 3-10-3.a);</item>
/// <item>the departure's wake turbulence interval behind the preceding departure from the same runway, timed from that
/// departure's start of takeoff roll, has not run (7110.65 3-9-6.f/g);</item>
/// <item>on a runway that physically crosses the departure runway, someone departing, landing or on it has not yet passed the
/// intersection, or an arrival to it is inside the final gate (7110.65 3-9-8.b).</item>
/// </list>
/// Converging non-intersecting runways, opposite-direction and intersection-departure intervals are not checked.
/// </summary>
internal static class SimulatedTowerRunwayGuard
{
    /// <summary>
    /// Why <paramref name="departure"/> may not be cleared for takeoff on <paramref name="runway"/> at
    /// <paramref name="nowSeconds"/>, naming the first aircraft that keeps it waiting; null when the runway is clear.
    /// </summary>
    /// <param name="departure">The departure the simulated tower would clear; skipped among the traffic.</param>
    /// <param name="runway">The departure's runway.</param>
    /// <param name="traffic">Every aircraft in the world.</param>
    /// <param name="layout">The airport's ground layout, for the landing threshold and the hold-short bars; may be null.</param>
    /// <param name="nowSeconds">Scenario-elapsed seconds now, for the wake turbulence interval.</param>
    /// <returns>The reason to wait, or null.</returns>
    public static string? ReasonToWait(
        AircraftState departure,
        RunwayInfo runway,
        IReadOnlyList<AircraftState> traffic,
        AirportGroundLayout? layout,
        double nowSeconds
    )
    {
        List<RunwayInfo> crossingRunways = IntersectingRunways(runway);
        foreach (AircraftState other in traffic)
        {
            if (string.Equals(other.Callsign, departure.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? reason = SameRunwayReason(other, runway, layout) ?? CrossingRunwaysReason(other, runway, crossingRunways, layout);
            if (reason is not null)
            {
                return reason;
            }
        }

        return WakeIntervalReason(departure, runway, traffic, nowSeconds);
    }

    /// <summary>Someone on, crossing, landing on, on short final to or fouling the runway, or an arrival to it inside the final gate.</summary>
    private static string? SameRunwayReason(AircraftState other, RunwayInfo runway, AirportGroundLayout? layout)
    {
        RunwayInfo end = RunwayOccupancy.AlignedEnd(other.TrueTrack.Degrees, runway);
        RunwayUseKind? kind = RunwayOccupancy.Classify(other, end, layout)?.Kind;
        if (
            kind
            is RunwayUseKind.Departing
                or RunwayUseKind.Landing
                or RunwayUseKind.OnSurface
                or RunwayUseKind.Crossing
                or RunwayUseKind.ShortFinal
                or RunwayUseKind.Fouling
        )
        {
            return $"{other.Callsign} {kind} runway {end.Designator}";
        }

        return IsArrivalInsideTheGate(other, runway, layout) ? $"{other.Callsign} inside the final gate for runway {end.Designator}" : null;
    }

    /// <summary>The first runway crossing <paramref name="runway"/> on which <paramref name="other"/> keeps the departure waiting.</summary>
    private static string? CrossingRunwaysReason(
        AircraftState other,
        RunwayInfo runway,
        List<RunwayInfo> crossingRunways,
        AirportGroundLayout? layout
    )
    {
        foreach (RunwayInfo pavement in crossingRunways)
        {
            RunwayInfo end = RunwayOccupancy.AlignedEnd(other.TrueTrack.Degrees, pavement);
            RunwayUseKind? kind = RunwayOccupancy.Classify(other, end, layout)?.Kind;
            bool usingIt = kind is RunwayUseKind.Departing or RunwayUseKind.Landing or RunwayUseKind.OnSurface or RunwayUseKind.ShortFinal;
            if (usingIt && !HasPassedTheIntersection(other, end, runway))
            {
                return $"{other.Callsign} {kind} runway {end.Designator}, short of runway {runway.Designator}";
            }

            if (IsArrivalInsideTheGate(other, pavement, layout))
            {
                return $"{other.Callsign} inside the final gate for runway {end.Designator}, which crosses runway {runway.Designator}";
            }
        }

        return null;
    }

    /// <summary>The other runways at the airport whose pavement physically crosses <paramref name="runway"/>'s, one end each.</summary>
    private static List<RunwayInfo> IntersectingRunways(RunwayInfo runway)
    {
        var crossing = new List<RunwayInfo>();
        foreach (RunwayInfo candidate in RunwayOccupancy.AirportRunways(runway.AirportId))
        {
            bool alreadyListed = crossing.Exists(r => r.Id.Overlaps(candidate.Id));
            if (
                !alreadyListed
                && !RunwayDepartureSeparation.IsSamePhysicalRunway(candidate, runway)
                && (RunwayDepartureSeparation.TryResolveRunwayRelation(candidate, runway) is { Kind: RunwayRelationKind.Intersecting })
            )
            {
                crossing.Add(candidate);
            }
        }

        return crossing;
    }

    /// <summary>Whether <paramref name="other"/>, moving along <paramref name="end"/>, is past where it crosses <paramref name="runway"/>.</summary>
    private static bool HasPassedTheIntersection(AircraftState other, RunwayInfo end, RunwayInfo runway)
    {
        if (RunwayDepartureSeparation.TryResolveRunwayRelation(end, runway) is not { PrecedingIntersectionFt: { } intersectionFt })
        {
            return false;
        }

        double alongFt =
            GeoMath.AlongTrackDistanceNm(other.Position, new LatLon(end.ThresholdLatitude, end.ThresholdLongitude), end.TrueHeading)
            * GeoMath.FeetPerNm;
        return alongFt > intersectionFt;
    }

    /// <summary>
    /// An airborne arrival assigned to <paramref name="pavement"/>, low enough to be landing, inside the controller-AI crossing
    /// gate's final gate: on final within <see cref="RunwayCrossingGate.FinalGateNm"/>, or on final within
    /// <see cref="RunwayCrossingGate.TimeGateMaxNm"/> and at most <see cref="RunwayCrossingGate.FinalGateSeconds"/> from the
    /// threshold.
    /// </summary>
    private static bool IsArrivalInsideTheGate(AircraftState other, RunwayInfo pavement, AirportGroundLayout? layout)
    {
        bool arrivalToIt = !other.IsOnGround && (other.Phases?.AssignedRunway is { } assigned) && assigned.Id.Overlaps(pavement.Id);
        if (!arrivalToIt || (other.Altitude - pavement.AirportElevationFt > RunwayCrossingGate.FinalGateMaxAglFt))
        {
            return false;
        }

        RunwayInfo end = RunwayOccupancy.AlignedEnd(other.TrueTrack.Degrees, pavement);
        bool insideDistance = RunwayOccupancy.IsOnFinal(other, end, layout, RunwayCrossingGate.FinalGateNm);
        bool insideTime =
            RunwayOccupancy.IsOnFinal(other, end, layout, RunwayCrossingGate.TimeGateMaxNm)
            && (RunwayOccupancy.SecondsToLandingThreshold(other, end, layout) is > 0 and <= RunwayCrossingGate.FinalGateSeconds);
        return insideDistance || insideTime;
    }

    /// <summary>
    /// The wake turbulence interval (<see cref="RunwayDepartureSeparation.DepartureWakeIntervalFor"/>) behind the preceding
    /// departure: the other aircraft departing the same physical runway (<see cref="RunwaySpawnCall.RunwayOf"/>) whose roll
    /// began latest (<see cref="AircraftGroundOps.TakeoffRollStartedAtSeconds"/>), while it has not run.
    /// </summary>
    private static string? WakeIntervalReason(AircraftState departure, RunwayInfo runway, IReadOnlyList<AircraftState> traffic, double nowSeconds)
    {
        AircraftState? preceding = null;
        double precedingRollStart = double.NegativeInfinity;
        foreach (AircraftState other in traffic)
        {
            bool sameRunway =
                (RunwaySpawnCall.RunwayOf(other) is { } otherRunway) && RunwayDepartureSeparation.IsSamePhysicalRunway(otherRunway, runway);
            bool later = other.Ground.TakeoffRollStartedAtSeconds is { } start && (start > precedingRollStart);
            if (sameRunway && later && !string.Equals(other.Callsign, departure.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                preceding = other;
                precedingRollStart = other.Ground.TakeoffRollStartedAtSeconds!.Value;
            }
        }

        if (preceding is null)
        {
            return null;
        }

        DepartureWakeInterval? interval = RunwayDepartureSeparation.DepartureWakeIntervalFor(
            RunwayDepartureSeparation.CwtCategoryOf(preceding.AircraftType),
            RunwayDepartureSeparation.CwtCategoryOf(departure.AircraftType),
            sameRunwayOrParallelUnder700Ft: true
        );
        if ((interval is null) || (nowSeconds >= precedingRollStart + interval.Seconds))
        {
            return null;
        }

        return $"{preceding.Callsign} began its takeoff roll {nowSeconds - precedingRollStart:0} s ago; wake interval {interval.Seconds:0} s ({interval.RuleReference})";
    }
}
