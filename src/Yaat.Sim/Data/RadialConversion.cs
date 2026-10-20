namespace Yaat.Sim.Data;

/// <summary>
/// Converts a magnetic radial from a fix to the true bearing from that fix, and back. A radial is referenced to the
/// facility's variation of record (P/CG RADIAL; AIM 1-1-17b.5(j)(1)): the station declination when the fix is a navaid
/// that has one, else the modelled declination at the fix (<see cref="NavigationDatabase.GetPublishedCourseDeclination"/>
/// with no airport). FAA CIFP theta/rho legs and vNAS FRD-named fixes read their radials the same way. The two directions
/// use the same declination, so they are exact inverses.
/// </summary>
public static class RadialConversion
{
    /// <summary>The true bearing from <paramref name="fixName"/> that its <paramref name="magneticRadial"/> lies on.</summary>
    public static TrueHeading ToTrue(NavigationDatabase navDb, string fixName, LatLon fixPosition, double magneticRadial) =>
        new MagneticHeading(magneticRadial).ToTrue(Declination(navDb, fixName, fixPosition));

    /// <summary>The magnetic radial from <paramref name="fixName"/> that a <paramref name="trueBearing"/> from it lies on.</summary>
    public static MagneticHeading ToMagnetic(NavigationDatabase navDb, string fixName, LatLon fixPosition, TrueHeading trueBearing) =>
        trueBearing.ToMagnetic(Declination(navDb, fixName, fixPosition));

    private static double Declination(NavigationDatabase navDb, string fixName, LatLon fixPosition) =>
        navDb.GetPublishedCourseDeclination(fixName, string.Empty, fixPosition);
}
