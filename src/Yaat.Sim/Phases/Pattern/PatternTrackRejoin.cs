namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Steering back onto a pattern leg's ground track after the aircraft has been displaced off it: the downwind after a wrong-side
/// or cross-runway join or a boundary hold, and the upwind after a boundary hold.
/// </summary>
internal static class PatternTrackRejoin
{
    /// <summary>Within this cross-track distance (nm) of the line the leg heading itself is flown.</summary>
    internal const double TrackToleranceNm = 0.03;

    /// <summary>Intercept angle (degrees) per nm of cross-track distance.</summary>
    internal const double InterceptGainDegPerNm = 200.0;

    /// <summary>The largest intercept angle (degrees) flown toward the line.</summary>
    internal const double MaxInterceptDeg = 45.0;

    /// <summary>
    /// The heading that brings <paramref name="position"/> back onto the line through <paramref name="linePoint"/> on
    /// <paramref name="legHeading"/>: the leg heading turned toward the line by a bounded intercept angle that decreases to zero
    /// as the aircraft re-establishes, and the leg heading itself within <see cref="TrackToleranceNm"/> of it.
    /// </summary>
    internal static TrueHeading HeadingToRejoin(LatLon position, LatLon linePoint, TrueHeading legHeading)
    {
        double xtk = GeoMath.SignedCrossTrackDistanceNm(position, linePoint, legHeading);
        if (Math.Abs(xtk) <= TrackToleranceNm)
        {
            return legHeading;
        }

        double intercept = Math.Min(MaxInterceptDeg, Math.Abs(xtk) * InterceptGainDegPerNm);
        return new TrueHeading(legHeading.Degrees + (xtk > 0 ? -intercept : intercept));
    }
}
