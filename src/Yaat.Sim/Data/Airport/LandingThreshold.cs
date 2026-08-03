using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Resolves a runway end's landing threshold — where an arrival may first touch down.
///
/// <see cref="RunwayInfo"/> carries pavement ends: the nav database builds every runway from the vNAS
/// <c>start_location</c>/<c>end_location</c>, which sit on the physical pavement regardless of any
/// displacement. The displacement itself lives on the airport map, parsed into
/// <see cref="GroundRunway.ThresholdDisplacementForEnd"/>, so an end's landing datum only exists once a
/// layout is in hand. Callers that have one (approach, landing, pattern geometry) resolve through here;
/// callers that legitimately want the pavement — runway rectangles, takeoff rolls, hold-short bars — keep
/// using <see cref="RunwayInfo.ThresholdLatitude"/> directly.
///
/// AIM 2-3-3.b.8.2: the pavement behind a displaced threshold is usable for takeoff in either direction
/// and for rollout from the opposite end, but not for landing in that direction.
/// </summary>
public static class LandingThreshold
{
    /// <summary>
    /// Published threshold displacement in feet for <paramref name="runway"/>'s active end, or 0 when
    /// there is no layout, the layout is for another airport, or the end is not displaced.
    /// </summary>
    public static double DisplacementFt(RunwayInfo runway, AirportGroundLayout? layout)
    {
        if (layout is null)
        {
            return 0;
        }

        // Designators repeat across airports, so a layout for the wrong field would otherwise apply
        // some other runway's displacement.
        if (!NavigationDatabase.AirportIdsMatch(runway.AirportId, layout.AirportId))
        {
            return 0;
        }

        return DisplacementFt(runway, layout.FindRunway(runway.Designator));
    }

    /// <summary>
    /// Published threshold displacement in feet for <paramref name="runway"/>'s active end, read from an
    /// already-resolved <paramref name="authoredRunway"/>. Callers that looked the ground runway up for
    /// other authored data (pattern size/altitude) use this overload rather than resolving twice; it
    /// trusts the caller to have resolved it from the right airport's layout.
    /// </summary>
    public static double DisplacementFt(RunwayInfo runway, GroundRunway? authoredRunway) =>
        authoredRunway?.ThresholdDisplacementForEnd(runway.Designator) ?? 0;

    /// <summary>
    /// <paramref name="runway"/>'s landing threshold: its pavement threshold shifted downfield by
    /// <see cref="DisplacementFt(RunwayInfo, AirportGroundLayout?)"/>. Falls back to the pavement
    /// threshold whenever the displacement is unknown or zero.
    /// </summary>
    public static LatLon Resolve(RunwayInfo runway, AirportGroundLayout? layout) => Project(runway, DisplacementFt(runway, layout));

    /// <summary>
    /// <paramref name="runway"/>'s landing threshold, taking the displacement from an already-resolved
    /// <paramref name="authoredRunway"/>.
    /// </summary>
    public static LatLon Resolve(RunwayInfo runway, GroundRunway? authoredRunway) => Project(runway, DisplacementFt(runway, authoredRunway));

    private static LatLon Project(RunwayInfo runway, double displacementFt)
    {
        var pavement = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        if (displacementFt <= 0)
        {
            return pavement;
        }

        // Project along the RunwayInfo's own course, not the layout centerline's: everything downstream
        // measures along-track and cross-track against this heading, and the two datasets disagree by a
        // fraction of a degree.
        return GeoMath.ProjectPoint(pavement, runway.TrueHeading, displacementFt / GeoMath.FeetPerNm);
    }
}
