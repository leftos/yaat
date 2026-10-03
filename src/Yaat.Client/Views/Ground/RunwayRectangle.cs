using SkiaSharp;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Ground;

/// <summary>
/// A runway's painted rectangle on the ground view: the centerline between its first and last coordinates, half the
/// runway's width either side. The renderer draws it and the canvas hit-tests the same shape.
/// </summary>
internal static class RunwayRectangle
{
    /// <summary>
    /// The screen corners of <paramref name="rwy"/>'s rectangle under <paramref name="vp"/>, in drawing order: both sides
    /// of the first coordinate, then both sides of the last. Null when the runway has fewer than two usable coordinates.
    /// </summary>
    public static SKPoint[]? ScreenCorners(GroundRunwayDto rwy, MapViewport vp)
    {
        if (rwy.Coordinates.Count < 2)
        {
            return null;
        }

        double[] first = rwy.Coordinates[0];
        double[] last = rwy.Coordinates[^1];
        if (first.Length < 2 || last.Length < 2)
        {
            return null;
        }

        double heading = GeoMath.BearingTo(first[0], first[1], last[0], last[1]);
        double halfWidthNm = (rwy.WidthFt / 2.0) / GeoMath.FeetPerNm;

        // Perpendicular angle (heading + 90)
        double perpRad = (heading + 90.0) * Math.PI / 180.0;
        double dLat = halfWidthNm / 60.0 * Math.Cos(perpRad);
        double dLon = halfWidthNm / 60.0 * Math.Sin(perpRad) / Math.Cos(first[0] * Math.PI / 180.0);

        (float x1, float y1) = vp.LatLonToScreen(first[0] + dLat, first[1] + dLon);
        (float x2, float y2) = vp.LatLonToScreen(first[0] - dLat, first[1] - dLon);
        (float x3, float y3) = vp.LatLonToScreen(last[0] - dLat, last[1] - dLon);
        (float x4, float y4) = vp.LatLonToScreen(last[0] + dLat, last[1] + dLon);
        return [new SKPoint(x1, y1), new SKPoint(x2, y2), new SKPoint(x3, y3), new SKPoint(x4, y4)];
    }
}
