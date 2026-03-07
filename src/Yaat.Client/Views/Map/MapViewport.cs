namespace Yaat.Client.Views.Map;

/// <summary>
/// Converts between lat/lon and screen pixel coordinates using equirectangular projection.
/// At airport/TRACON scale (&lt;120nm), distortion vs Mercator is negligible (&lt;0.1%).
/// </summary>
public sealed class MapViewport
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 10000.0;
    private const double DefaultPixelsPerDeg = 5000.0;

    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double Zoom { get; set; } = 1.0;
    public float PixelWidth { get; set; }
    public float PixelHeight { get; set; }

    /// <summary>
    /// Clockwise rotation in degrees applied to the display.
    /// Set to magnetic declination (east positive) to show magnetic north up.
    /// </summary>
    public double RotationDeg { get; set; }

    private double PixelsPerDeg => DefaultPixelsPerDeg * Zoom;
    private double CosCenter => Math.Cos(CenterLat * Math.PI / 180.0);

    public (float X, float Y) LatLonToScreen(double lat, double lon)
    {
        var ppd = PixelsPerDeg;
        var cos = CosCenter;
        var rx = (lon - CenterLon) * cos * ppd;
        var ry = -(lat - CenterLat) * ppd;

        if (RotationDeg != 0)
        {
            var rad = -RotationDeg * Math.PI / 180.0;
            var cosR = Math.Cos(rad);
            var sinR = Math.Sin(rad);
            var rotX = rx * cosR - ry * sinR;
            var rotY = rx * sinR + ry * cosR;
            rx = rotX;
            ry = rotY;
        }

        return ((float)(rx + PixelWidth / 2.0), (float)(ry + PixelHeight / 2.0));
    }

    public (double Lat, double Lon) ScreenToLatLon(float x, float y)
    {
        var ppd = PixelsPerDeg;
        var cos = CosCenter;
        if (cos < 1e-10)
        {
            cos = 1e-10;
        }

        var sx = x - PixelWidth / 2.0;
        var sy = y - PixelHeight / 2.0;

        if (RotationDeg != 0)
        {
            var rad = RotationDeg * Math.PI / 180.0;
            var cosR = Math.Cos(rad);
            var sinR = Math.Sin(rad);
            var ux = sx * cosR - sy * sinR;
            var uy = sx * sinR + sy * cosR;
            sx = ux;
            sy = uy;
        }

        var lon = sx / (cos * ppd) + CenterLon;
        var lat = -sy / ppd + CenterLat;
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

        var dx = (double)deltaScreenX;
        var dy = (double)deltaScreenY;

        if (RotationDeg != 0)
        {
            var rad = RotationDeg * Math.PI / 180.0;
            var cosR = Math.Cos(rad);
            var sinR = Math.Sin(rad);
            var udx = dx * cosR - dy * sinR;
            var udy = dx * sinR + dy * cosR;
            dx = udx;
            dy = udy;
        }

        CenterLon -= dx / (cos * ppd);
        CenterLat += dy / ppd;
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

        var sx = screenX - PixelWidth / 2.0;
        var sy = screenY - PixelHeight / 2.0;

        if (RotationDeg != 0)
        {
            var rad = RotationDeg * Math.PI / 180.0;
            var cosR = Math.Cos(rad);
            var sinR = Math.Sin(rad);
            var ux = sx * cosR - sy * sinR;
            var uy = sx * sinR + sy * cosR;
            sx = ux;
            sy = uy;
        }

        CenterLon = lon - sx / (cos * ppd);
        CenterLat = lat + sy / ppd;
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
            RotationDeg = RotationDeg,
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
