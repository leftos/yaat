using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Imagery;

/// <summary>
/// The areas the USGS NAIPPlus imagery is asked for, as latitude and longitude boxes (edges inclusive): the lower 48,
/// Alaska, Hawaii, Puerto Rico and the US Virgin Islands, Guam and the Northern Mariana Islands, and American Samoa.
/// The extents are generous; a tile outside the real coverage comes back blank and is dropped as no coverage.
/// </summary>
public static class UsgsCoverage
{
    private static readonly ILogger Log = SimLog.CreateLogger("UsgsCoverage");

    private static readonly CoverageBox[] Boxes =
    [
        // The lower 48 up to the 49th parallel, and Minnesota's Northwest Angle above it.
        new(24.0, 49.0, -125.0, -66.5),
        new(49.0, 49.5, -95.5, -94.5),
        // Alaska, and the Aleutians west of the antimeridian.
        new(51.0, 71.5, -180.0, -129.5),
        new(51.0, 53.5, 172.0, 180.0),
        new(18.5, 22.5, -160.5, -154.5),
        // Puerto Rico and the US Virgin Islands.
        new(17.5, 18.7, -67.5, -64.3),
        // Guam and the Northern Mariana Islands.
        new(13.0, 20.7, 144.5, 146.2),
        // American Samoa.
        new(-14.7, -11.0, -171.2, -168.0),
    ];

    /// <summary>True when the point lies in one of the covered areas.</summary>
    public static bool Contains(double latDeg, double lonDeg)
    {
        foreach (CoverageBox box in Boxes)
        {
            if ((latDeg >= box.South) && (latDeg <= box.North) && (lonDeg >= box.West) && (lonDeg <= box.East))
            {
                return true;
            }
        }

        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("{Lat},{Lon} is outside the USGS imagery coverage", latDeg, lonDeg);
        }
        return false;
    }

    private readonly record struct CoverageBox(double South, double North, double West, double East);
}
