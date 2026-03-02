namespace Yaat.Client.Views.Map;

/// <summary>
/// Converts between lat/lon and screen pixel coordinates using equirectangular projection.
/// At airport/TRACON scale (&lt;120nm), distortion vs Mercator is negligible (&lt;0.1%).
/// </summary>
public sealed class MapViewport
{
    private const double MinZoom = 0.5;
    private const double MaxZoom = 10000.0;
    private const double DefaultPixelsPerDeg = 5000.0;

    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double Zoom { get; set; } = 1.0;
    public float PixelWidth { get; set; }
    public float PixelHeight { get; set; }

    private double PixelsPerDeg => DefaultPixelsPerDeg * Zoom;
    private double CosCenter => Math.Cos(CenterLat * Math.PI / 180.0);

    public (float X, float Y) LatLonToScreen(double lat, double lon)
    {
        var ppd = PixelsPerDeg;
        var cos = CosCenter;
        var x = (float)((lon - CenterLon) * cos * ppd + PixelWidth / 2.0);
        var y = (float)(-(lat - CenterLat) * ppd + PixelHeight / 2.0);
        return (x, y);
    }

    public (double Lat, double Lon) ScreenToLatLon(float x, float y)
    {
        var ppd = PixelsPerDeg;
        var cos = CosCenter;
        if (cos < 1e-10)
        {
            cos = 1e-10;
        }

        var lon = (x - PixelWidth / 2.0) / (cos * ppd) + CenterLon;
        var lat = -(y - PixelHeight / 2.0) / ppd + CenterLat;
        return (lat, lon);
    }

    public void Pan(float deltaScreenX, float deltaScreenY)
    {
        var ppd = PixelsPerDeg;
        var cos = CosCenter;
        if (cos < 1e-10)
        {
            cos = 1e-10;
        }

        CenterLon -= deltaScreenX / (cos * ppd);
        CenterLat += deltaScreenY / ppd;
    }

    public void ZoomAt(float screenX, float screenY, double factor)
    {
        var (lat, lon) = ScreenToLatLon(screenX, screenY);
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        // After zoom, adjust center so the point under cursor stays fixed
        var ppd = PixelsPerDeg;
        var cos = CosCenter;
        if (cos < 1e-10)
        {
            cos = 1e-10;
        }

        CenterLon = lon - (screenX - PixelWidth / 2.0) / (cos * ppd);
        CenterLat = lat + (screenY - PixelHeight / 2.0) / ppd;
    }

    public MapViewport Clone()
    {
        return new MapViewport
        {
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            Zoom = Zoom,
            PixelWidth = PixelWidth,
            PixelHeight = PixelHeight,
        };
    }

    public void FitBounds(double minLat, double maxLat, double minLon, double maxLon)
    {
        CenterLat = (minLat + maxLat) / 2.0;
        CenterLon = (minLon + maxLon) / 2.0;

        var latSpan = maxLat - minLat;
        var lonSpan = maxLon - minLon;

        if (latSpan < 1e-8 && lonSpan < 1e-8)
        {
            Zoom = 1.0;
            return;
        }

        var cos = CosCenter;
        if (cos < 1e-10)
        {
            cos = 1e-10;
        }

        // Calculate zoom to fit both dimensions with 10% padding
        var padW = PixelWidth * 0.9;
        var padH = PixelHeight * 0.9;
        if (padW < 1)
        {
            padW = 1;
        }

        if (padH < 1)
        {
            padH = 1;
        }

        var zoomLat = latSpan > 1e-8 ? padH / (latSpan * DefaultPixelsPerDeg) : MaxZoom;
        var zoomLon = lonSpan > 1e-8 ? padW / (lonSpan * cos * DefaultPixelsPerDeg) : MaxZoom;
        Zoom = Math.Clamp(Math.Min(zoomLat, zoomLon), MinZoom, MaxZoom);
    }
}
