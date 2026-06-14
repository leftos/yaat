using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases;

/// <summary>
/// Entry leg for pattern construction.
/// </summary>
public enum PatternEntryLeg
{
    Upwind,
    Crosswind,
    Downwind,
    Base,
    Final,
}

/// <summary>
/// Builds phase sequences for traffic pattern circuits.
/// </summary>
public static class PatternBuilder
{
    /// <summary>
    /// Build a pattern circuit starting from a specific entry leg.
    /// When <paramref name="touchAndGo"/> is true, the approach ends
    /// with a TouchAndGoPhase instead of a LandingPhase.
    /// </summary>
    public static List<Phase> BuildCircuit(
        RunwayInfo runway,
        AircraftCategory category,
        PatternDirection direction,
        PatternEntryLeg entryLeg,
        bool touchAndGo,
        double? finalDistanceNm,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways
    )
    {
        var waypoints = PatternGeometry.Compute(runway, category, direction, patternSizeNm, altitudeOverrideFt, airportRunways);
        var phases = new List<Phase>();

        switch (entryLeg)
        {
            case PatternEntryLeg.Upwind:
                phases.Add(new UpwindPhase { Waypoints = waypoints });
                phases.Add(new CrosswindPhase { Waypoints = waypoints });
                phases.Add(new DownwindPhase { Waypoints = waypoints });
                phases.Add(new BasePhase { Waypoints = waypoints });
                break;

            case PatternEntryLeg.Crosswind:
                phases.Add(new CrosswindPhase { Waypoints = waypoints });
                phases.Add(new DownwindPhase { Waypoints = waypoints });
                phases.Add(new BasePhase { Waypoints = waypoints });
                break;

            case PatternEntryLeg.Downwind:
                phases.Add(new DownwindPhase { Waypoints = waypoints });
                phases.Add(new BasePhase { Waypoints = waypoints });
                break;

            case PatternEntryLeg.Base:
                phases.Add(new BasePhase { Waypoints = waypoints, FinalDistanceNm = finalDistanceNm });
                break;

            case PatternEntryLeg.Final:
                break;
        }

        phases.Add(new FinalApproachPhase());
        Phase landingPhase = category == AircraftCategory.Helicopter ? new HelicopterLandingPhase() : new LandingPhase();
        phases.Add(touchAndGo ? new TouchAndGoPhase() : landingPhase);

        return phases;
    }

    /// <summary>
    /// Build a VFR pattern-exit departure (CTO MRC/MRD/MLC/MLD): the legs up to and including the
    /// exit leg, with no base/final/landing tail. A crosswind exit flies upwind then turns crosswind;
    /// a downwind exit flies upwind, crosswind, then downwind. The legs keep a continuous takeoff-rate
    /// climb toward <paramref name="assignedAltitude"/> ?? <paramref name="cruiseAltitude"/> (no level-off
    /// at pattern altitude), and a terminal <see cref="PatternExitPhase"/> rolls the aircraft out on the
    /// exit-leg heading and departs the area.
    /// </summary>
    public static List<Phase> BuildPatternExitCircuit(
        RunwayInfo runway,
        AircraftCategory category,
        PatternDirection direction,
        PatternEntryLeg exitLeg,
        int? assignedAltitude,
        int cruiseAltitude,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways
    )
    {
        var waypoints = PatternGeometry.Compute(runway, category, direction, patternSizeNm, altitudeOverrideFt, airportRunways);
        int climbTo = assignedAltitude ?? cruiseAltitude;

        var phases = new List<Phase>
        {
            new UpwindPhase { Waypoints = waypoints, DepartureClimbTargetFt = climbTo },
        };

        TrueHeading exitHeading;
        if (exitLeg == PatternEntryLeg.Downwind)
        {
            phases.Add(new CrosswindPhase { Waypoints = waypoints, DepartureClimbTargetFt = climbTo });
            exitHeading = waypoints.DownwindHeading;
        }
        else
        {
            // Crosswind exit: depart on the crosswind heading straight off the upwind turn.
            exitHeading = waypoints.CrosswindHeading;
        }

        phases.Add(
            new PatternExitPhase
            {
                ExitHeading = exitHeading,
                Direction = direction,
                AssignedAltitude = assignedAltitude,
                CruiseAltitude = cruiseAltitude,
            }
        );

        return phases;
    }

    /// <summary>
    /// Build the next full pattern circuit (from upwind) for an aircraft cycling in the pattern.
    /// Auto-cycle callers choose <paramref name="touchAndGo"/> based on the previous circuit's
    /// intent: true after a touch-and-go completion (TG cycling), false after a go-around from
    /// a landing-intent approach (the aircraft keeps trying to land full-stop).
    /// </summary>
    public static List<Phase> BuildNextCircuit(
        RunwayInfo runway,
        AircraftCategory category,
        PatternDirection direction,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        bool touchAndGo
    )
    {
        return BuildCircuit(runway, category, direction, PatternEntryLeg.Upwind, touchAndGo, null, patternSizeNm, altitudeOverrideFt, airportRunways);
    }

    /// <summary>
    /// Build the first circuit for a cross-runway closed-traffic departure (e.g. takeoff
    /// runway 33, make right traffic runway 28R). The upwind leg is flown on the
    /// <paramref name="departureRunway"/>'s extended centerline (where the aircraft actually
    /// lifts off); a <see cref="MidfieldCrossingPhase"/> then connects to the
    /// <paramref name="patternRunway"/>'s downwind, and the rest of the circuit
    /// (downwind/base/final) belongs to the pattern runway. Subsequent circuits are built
    /// entirely from the pattern runway by the auto-cycle.
    ///
    /// Per AIM 4-3-2: departure/upwind belong to the departure runway; downwind/base/final
    /// belong to the landing (pattern) runway.
    /// </summary>
    public static List<Phase> BuildCrossRunwayDepartureCircuit(
        RunwayInfo departureRunway,
        RunwayInfo patternRunway,
        AircraftCategory category,
        PatternDirection direction,
        bool touchAndGo,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways
    )
    {
        var departureWaypoints = PatternGeometry.Compute(departureRunway, category, direction, patternSizeNm, altitudeOverrideFt, airportRunways);
        var patternWaypoints = PatternGeometry.Compute(patternRunway, category, direction, patternSizeNm, altitudeOverrideFt, airportRunways);

        var phases = new List<Phase>
        {
            new UpwindPhase { Waypoints = departureWaypoints },
            new MidfieldCrossingPhase { Waypoints = patternWaypoints, BiasTurnToPatternSide = true },
            new DownwindPhase { Waypoints = patternWaypoints },
            new BasePhase { Waypoints = patternWaypoints },
            new FinalApproachPhase(),
        };
        Phase landingPhase = category == AircraftCategory.Helicopter ? new HelicopterLandingPhase() : new LandingPhase();
        phases.Add(touchAndGo ? new TouchAndGoPhase() : landingPhase);

        return phases;
    }

    /// <summary>
    /// Update waypoints on all active/pending pattern phases in the list.
    /// Returns true if any pattern phases were found.
    /// </summary>
    public static bool UpdateWaypoints(PhaseList phaseList, PatternWaypoints waypoints)
    {
        bool found = false;
        foreach (var phase in phaseList.Phases)
        {
            if (phase.Status is not PhaseStatus.Pending and not PhaseStatus.Active)
            {
                continue;
            }

            switch (phase)
            {
                case UpwindPhase up:
                    up.Waypoints = waypoints;
                    found = true;
                    break;
                case CrosswindPhase cw:
                    cw.Waypoints = waypoints;
                    found = true;
                    break;
                case DownwindPhase dw:
                    dw.Waypoints = waypoints;
                    found = true;
                    break;
                case BasePhase bp:
                    bp.Waypoints = waypoints;
                    found = true;
                    break;
                case MidfieldCrossingPhase mc:
                    mc.Waypoints = waypoints;
                    found = true;
                    break;
            }
        }

        return found;
    }
}
