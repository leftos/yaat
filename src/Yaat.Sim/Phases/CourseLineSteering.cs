namespace Yaat.Sim.Phases;

/// <summary>
/// Steers an aircraft onto a course line through a fix: the heading is the course, corrected toward the line in
/// proportion to the cross-track error and capped at a normal intercept angle, so the aircraft converges on the
/// line from either side and never turns more than <see cref="MaxInterceptDeg"/> away from the course.
/// </summary>
public static class CourseLineSteering
{
    /// <summary>The largest cut onto the course line, in degrees.</summary>
    public const double MaxInterceptDeg = 45.0;

    /// <summary>Heading correction per nautical mile of cross-track error, in degrees.</summary>
    public const double CrossTrackGainDegPerNm = 25.0;

    /// <summary>
    /// The heading that brings an aircraft at <paramref name="position"/> onto the line through
    /// <paramref name="anchor"/> along <paramref name="course"/>.
    /// </summary>
    /// <param name="position">The aircraft's position.</param>
    /// <param name="anchor">Any point on the course line, usually the fix the course runs to or from.</param>
    /// <param name="course">The course to track (true).</param>
    /// <returns>The true heading to fly: the course when on the line, up to <see cref="MaxInterceptDeg"/> off it otherwise.</returns>
    public static TrueHeading HeadingToward(LatLon position, LatLon anchor, TrueHeading course)
    {
        double signedCrossTrackNm = GeoMath.SignedCrossTrackDistanceNm(position, anchor, course);
        double correction = Math.Clamp(signedCrossTrackNm * CrossTrackGainDegPerNm, -MaxInterceptDeg, MaxInterceptDeg);
        return new TrueHeading(course.Degrees - correction);
    }
}
