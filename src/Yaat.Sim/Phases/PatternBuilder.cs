using Yaat.Sim.Data.Airport;
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
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        PatternEntryLeg entryLeg,
        bool touchAndGo,
        double? finalDistanceNm,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? authoredRunway
    )
    {
        var waypoints = PatternGeometry.Compute(
            runway,
            category,
            aircraftType,
            windSpeedKt,
            direction,
            patternSizeNm,
            altitudeOverrideFt,
            airportRunways,
            authoredRunway
        );
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
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        PatternEntryLeg exitLeg,
        int? assignedAltitude,
        int cruiseAltitude,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? authoredRunway
    )
    {
        var waypoints = PatternGeometry.Compute(
            runway,
            category,
            aircraftType,
            windSpeedKt,
            direction,
            patternSizeNm,
            altitudeOverrideFt,
            airportRunways,
            authoredRunway
        );

        // Altitude resolution (COMMANDS.md, CTO Departure Modifiers): an assigned altitude wins; else
        // the filed cruise altitude; else pattern altitude. A VFR departure without a filed cruise still
        // climbs toward pattern altitude — it must never target 0 ft MSL and fly the climb rate into
        // the ground. (CruiseAltitude defaults to 0 for a VFR aircraft with no filed cruise.)
        int climbTo = assignedAltitude ?? (cruiseAltitude > 0 ? cruiseAltitude : (int)Math.Round(waypoints.PatternAltitude));

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
                ClimbTargetFt = climbTo,
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
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? authoredRunway,
        bool touchAndGo
    )
    {
        return BuildCircuit(
            runway,
            category,
            aircraftType,
            windSpeedKt,
            direction,
            PatternEntryLeg.Upwind,
            touchAndGo,
            null,
            patternSizeNm,
            altitudeOverrideFt,
            airportRunways,
            authoredRunway
        );
    }

    /// <summary>
    /// Build the circuit for an aircraft that climbs out on <paramref name="flownRunway"/> and flies
    /// the pattern of <paramref name="patternRunway"/> — a cross-runway closed-traffic departure
    /// (takeoff runway 33, make right traffic runway 28R) or an in-pattern runway switch on the
    /// upwind leg. Downwind/base/final always belong to the pattern (landing) runway; only the
    /// upwind belongs to the runway the aircraft is actually flying (AIM 4-3-2). Subsequent circuits
    /// are built entirely from the pattern runway by the auto-cycle.
    ///
    /// <para><b>Close parallels</b> (<see cref="RunwayGeometry.AreCloseParallels"/>): the two
    /// centerlines are a few hundred feet apart on the same heading, so the aircraft simply continues
    /// its upwind and turns crosswind beyond both departure ends
    /// (<see cref="PatternGeometry.ComputeTransition"/>), then flies the pattern runway's crosswind
    /// onto its downwind. Crossing the field is neither needed nor desirable there.</para>
    ///
    /// <para><b>Crossing runways</b>: the upwind is flown on the flown runway's extended centerline
    /// and a <see cref="MidfieldCrossingPhase"/> — with the initial turn biased toward the pattern
    /// side — connects to the pattern runway's downwind.</para>
    /// </summary>
    public static List<Phase> BuildRunwayTransitionCircuit(
        RunwayInfo flownRunway,
        RunwayInfo patternRunway,
        AircraftCategory category,
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        bool touchAndGo,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? flownAuthoredRunway,
        GroundRunway? patternAuthoredRunway
    )
    {
        var patternWaypoints = PatternGeometry.Compute(
            patternRunway,
            category,
            aircraftType,
            windSpeedKt,
            direction,
            patternSizeNm,
            altitudeOverrideFt,
            airportRunways,
            patternAuthoredRunway
        );

        var phases = RunwayGeometry.AreCloseParallels(flownRunway, patternRunway)
            ? BuildParallelTransitionLegs(
                flownRunway,
                patternRunway,
                category,
                aircraftType,
                windSpeedKt,
                direction,
                patternSizeNm,
                altitudeOverrideFt,
                airportRunways,
                patternAuthoredRunway,
                patternWaypoints
            )
            : BuildCrossingTransitionLegs(
                flownRunway,
                category,
                aircraftType,
                windSpeedKt,
                direction,
                patternSizeNm,
                altitudeOverrideFt,
                airportRunways,
                flownAuthoredRunway,
                patternWaypoints
            );

        phases.Add(new BasePhase { Waypoints = patternWaypoints });
        phases.Add(new FinalApproachPhase());
        Phase landingPhase = category == AircraftCategory.Helicopter ? new HelicopterLandingPhase() : new LandingPhase();
        phases.Add(touchAndGo ? new TouchAndGoPhase() : landingPhase);

        return phases;
    }

    private static List<Phase> BuildParallelTransitionLegs(
        RunwayInfo flownRunway,
        RunwayInfo patternRunway,
        AircraftCategory category,
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? patternAuthoredRunway,
        PatternWaypoints patternWaypoints
    )
    {
        var transitionWaypoints = PatternGeometry.ComputeTransition(
            flownRunway,
            patternRunway,
            category,
            aircraftType,
            windSpeedKt,
            direction,
            patternSizeNm,
            altitudeOverrideFt,
            airportRunways,
            patternAuthoredRunway
        );

        return
        [
            new UpwindPhase { Waypoints = transitionWaypoints },
            new CrosswindPhase { Waypoints = transitionWaypoints },
            // The transition crosswind ends abeam its own (farther) turn point, off the pattern
            // runway's computed downwind track; re-intercept it so base/final roll out on centerline.
            new DownwindPhase { Waypoints = patternWaypoints, RejoinTrack = true },
        ];
    }

    private static List<Phase> BuildCrossingTransitionLegs(
        RunwayInfo flownRunway,
        AircraftCategory category,
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? flownAuthoredRunway,
        PatternWaypoints patternWaypoints
    )
    {
        var flownWaypoints = PatternGeometry.Compute(
            flownRunway,
            category,
            aircraftType,
            windSpeedKt,
            direction,
            patternSizeNm,
            altitudeOverrideFt,
            airportRunways,
            flownAuthoredRunway
        );

        return
        [
            new UpwindPhase { Waypoints = flownWaypoints },
            new MidfieldCrossingPhase
            {
                Waypoints = patternWaypoints,
                InitialTurn = direction == PatternDirection.Left ? TurnDirection.Left : TurnDirection.Right,
            },
            // The midfield crossing can drop the aircraft inside the pattern-runway downwind track;
            // re-intercept it so the base/final geometry rolls out on centerline.
            new DownwindPhase { Waypoints = patternWaypoints, RejoinTrack = true },
        ];
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
