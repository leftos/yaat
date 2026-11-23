using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Finds the parking picker's stand under a screen point: the nearest stand within <see cref="Range"/> pixels, measured on
/// the screen so it holds at any zoom; an exact tie goes to the later stand (drawn on top), a distance equal to the range
/// is a hit, and nothing in range is null.
/// </summary>
public static class ParkingPickerHitTest
{
    /// <summary>How near, in pixels, a stand is hit.</summary>
    public const float Range = 12f;

    // Projections land on floats, so a click placed at exactly the range can measure a hair past it.
    private const double RoundingAllowance = 1e-3;

    /// <summary>Finds the stand under a screen point.</summary>
    /// <param name="scene">The picker's scene.</param>
    /// <param name="viewport">The view the point is in.</param>
    /// <param name="x">The point's screen x, in pixels.</param>
    /// <param name="y">The point's screen y, in pixels.</param>
    /// <returns>The nearest stand in range, or null.</returns>
    public static ParkingPickerStand? Find(ParkingPickerScene scene, MapViewport viewport, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        ParkingPickerStand? best = null;
        double bestDistance = double.MaxValue;
        foreach (ParkingPickerStand stand in scene.Stands)
        {
            (float sx, float sy) = viewport.LatLonToScreen(stand.Lat, stand.Lon);
            double d = Math.Sqrt(((sx - x) * (double)(sx - x)) + ((sy - y) * (double)(sy - y)));
            if ((d <= Range + RoundingAllowance) && (d <= bestDistance))
            {
                best = stand;
                bestDistance = d;
            }
        }

        return best;
    }
}
