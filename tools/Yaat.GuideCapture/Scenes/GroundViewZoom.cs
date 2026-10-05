using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.GuideCapture.Scenes;

// Zooms the ground view in on one point for a close-up shot and puts the view
// back afterwards. The ground view saves its centre and zoom to preferences,
// which every later MainWindow reads, so a scene that zooms calls Restore once
// its capture is taken.
internal sealed class GroundViewZoom
{
    private readonly GroundViewModel _ground;
    private readonly double _priorCenterLat;
    private readonly double _priorCenterLon;
    private readonly double _priorZoom;

    private GroundViewZoom(GroundViewModel ground)
    {
        _ground = ground;
        _priorCenterLat = ground.ViewCenterLat;
        _priorCenterLon = ground.ViewCenterLon;
        _priorZoom = ground.ViewZoom;
    }

    // Centres the view on the point at factor times the current zoom.
    public static GroundViewZoom Apply(GroundViewModel ground, LatLon center, double factor)
    {
        var zoom = new GroundViewZoom(ground);
        ground.ViewCenterLat = center.Lat;
        ground.ViewCenterLon = center.Lon;
        ground.ViewZoom = zoom._priorZoom * factor;
        Dispatcher.UIThread.RunJobs();
        return zoom;
    }

    public void Restore()
    {
        _ground.ViewCenterLat = _priorCenterLat;
        _ground.ViewCenterLon = _priorCenterLon;
        _ground.ViewZoom = _priorZoom;
    }
}
