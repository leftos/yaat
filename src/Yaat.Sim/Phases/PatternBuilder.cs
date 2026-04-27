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
