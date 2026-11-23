using Avalonia;
using Xunit;
using Yaat.Client.Views.Authoring;
using Yaat.Client.Views.Map;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The parking picker's hit test over the real KOAK stands: the nearest stand within the screen-pixel range, nothing
/// outside it, the same range at every zoom.
/// </summary>
public class ParkingPickerHitTestTests
{
    [Fact]
    public void ClickWithinRadius_PicksNearestStand()
    {
        (ParkingPickerScene scene, MapViewport viewport) = Fitted();
        (ParkingPickerStand a, ParkingPickerStand b, double d) = ClosestPair(scene, viewport);
        (float ax, float ay) = viewport.LatLonToScreen(a.Lat, a.Lon);
        viewport.ZoomAt(ax, ay, 16 / d);
        (ax, ay) = viewport.LatLonToScreen(a.Lat, a.Lon);
        (float bx, float by) = viewport.LatLonToScreen(b.Lat, b.Lon);

        // Both stands are within range of each point; the nearer one wins.
        Assert.Same(a, ParkingPickerHitTest.Find(scene, viewport, ax + (0.3f * (bx - ax)), ay + (0.3f * (by - ay))));
        Assert.Same(b, ParkingPickerHitTest.Find(scene, viewport, ax + (0.7f * (bx - ax)), ay + (0.7f * (by - ay))));
    }

    [Fact]
    public void ClickOutside_PicksNothing()
    {
        (ParkingPickerScene scene, MapViewport viewport) = Fitted();
        (_, Point at) = OakParkingLayout.IsolatedStand(scene, viewport);

        Assert.Null(ParkingPickerHitTest.Find(scene, viewport, 3, 3));
        Assert.Null(ParkingPickerHitTest.Find(scene, viewport, (float)at.X + ParkingPickerHitTest.Range + 2, (float)at.Y));
    }

    [Fact]
    public void PicksAtAnyZoom()
    {
        (ParkingPickerScene scene, MapViewport fitted) = Fitted();
        (ParkingPickerStand stand, _) = OakParkingLayout.IsolatedStand(scene, fitted);
        foreach (double factor in new[] { 1.0, 4.0, 32.0, 256.0 })
        {
            MapViewport viewport = fitted.Clone();
            viewport.ZoomAt(viewport.PixelWidth / 2, viewport.PixelHeight / 2, factor);
            (float x, float y) = viewport.LatLonToScreen(stand.Lat, stand.Lon);

            Assert.Same(stand, ParkingPickerHitTest.Find(scene, viewport, x + ParkingPickerHitTest.Range - 1, y));
            Assert.Same(stand, ParkingPickerHitTest.Find(scene, viewport, x, y - ParkingPickerHitTest.Range + 1));
            Assert.Null(ParkingPickerHitTest.Find(scene, viewport, x + ParkingPickerHitTest.Range + 1, y));
        }

        MapViewport zoomedOut = fitted.Clone();
        zoomedOut.ZoomAt(zoomedOut.PixelWidth / 2, zoomedOut.PixelHeight / 2, 0.25);
        (float ox, float oy) = zoomedOut.LatLonToScreen(stand.Lat, stand.Lon);
        Assert.NotNull(ParkingPickerHitTest.Find(scene, zoomedOut, ox, oy));
    }

    [Fact]
    public void AClickExactlyAtTheRange_Hits_AndJustBeyondItMisses()
    {
        (ParkingPickerScene scene, MapViewport viewport) = Fitted();
        (ParkingPickerStand stand, _) = OakParkingLayout.IsolatedStand(scene, viewport);
        viewport.ZoomAt(viewport.PixelWidth / 2, viewport.PixelHeight / 2, 8);
        (float x, float y) = viewport.LatLonToScreen(stand.Lat, stand.Lon);

        Assert.Same(stand, ParkingPickerHitTest.Find(scene, viewport, x + ParkingPickerHitTest.Range, y));
        Assert.Same(stand, ParkingPickerHitTest.Find(scene, viewport, x, y - ParkingPickerHitTest.Range));
        Assert.Null(ParkingPickerHitTest.Find(scene, viewport, x + ParkingPickerHitTest.Range + 0.05f, y));
        Assert.Null(ParkingPickerHitTest.Find(scene, viewport, x, y - ParkingPickerHitTest.Range - 0.05f));
    }

    private static (ParkingPickerScene Scene, MapViewport Viewport) Fitted()
    {
        var scene = ParkingPickerScene.Build(OakParkingLayout.Layout, null);
        var viewport = new MapViewport { PixelWidth = 800, PixelHeight = 600 };
        ParkingPickerBounds bounds = scene.Bounds!;
        viewport.FitBounds(bounds.MinLat, bounds.MaxLat, bounds.MinLon, bounds.MaxLon);
        return (scene, viewport);
    }

    // The two stands nearest each other on the screen, of those with no other stand within a tenth of a pixel.
    private static (ParkingPickerStand A, ParkingPickerStand B, double Distance) ClosestPair(ParkingPickerScene scene, MapViewport viewport)
    {
        List<(ParkingPickerStand Stand, Point At)> projected =
        [
            .. scene.Stands.Select(s =>
            {
                (float x, float y) = viewport.LatLonToScreen(s.Lat, s.Lon);
                return (s, new Point(x, y));
            }),
        ];
        // A stand with another on the same pixel would tie with it; such stands are left out.
        projected =
        [
            .. projected.Where(p => !projected.Any(q => !ReferenceEquals(p.Stand, q.Stand) && (OakParkingLayout.Distance(p.At, q.At) <= 0.1))),
        ];
        (ParkingPickerStand, ParkingPickerStand, double) best = (projected[0].Stand, projected[1].Stand, double.MaxValue);
        for (int i = 0; i < projected.Count; i++)
        {
            for (int j = i + 1; j < projected.Count; j++)
            {
                double d = OakParkingLayout.Distance(projected[i].At, projected[j].At);
                if (d < best.Item3)
                {
                    best = (projected[i].Stand, projected[j].Stand, d);
                }
            }
        }

        return best;
    }
}
