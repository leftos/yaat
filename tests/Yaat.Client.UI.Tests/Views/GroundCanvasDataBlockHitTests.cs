using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// Datablock hit-testing on the Ground view. An airborne aircraft's datablock carries an extra
// altitude line that an on-ground block does not, so the hit rect must use the same airborne flag
// the renderer draws with — otherwise clicks near the bottom of an airborne block miss.
public class GroundCanvasDataBlockHitTests
{
    private const double FieldLat = 37.62;
    private const double FieldLon = -122.39;

    // DataBlockLayout geometry: default offset (30, -25), 12 px text, 14 px line height, 3 px pad.
    // On-ground block = 2 lines (bottom at blockY + 17); airborne adds an altitude line (bottom at
    // blockY + 31). A click at blockY + 24 lands in that extra altitude row.
    private const float OffsetX = 30f;
    private const float OffsetY = -25f;
    private const float AltitudeRowY = 24f;

    [AvaloniaFact]
    public void FindDataBlockAtPoint_AirborneAircraft_ClickOnAltitudeLine_Hits()
    {
        var ac = MakeAircraft(onGround: false, altitude: 1500);
        var canvas = MakeCanvas(ac);

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        var click = new Point(sx + OffsetX + 1f, sy + OffsetY + AltitudeRowY);

        var hit = canvas.FindDataBlockAtPoint(click);

        Assert.NotNull(hit);
        Assert.Equal("UAL238", hit!.Callsign);
    }

    [AvaloniaFact]
    public void FindDataBlockAtPoint_OnGroundAircraft_ClickBelowBlock_Misses()
    {
        // Control: the same screen point is below the shorter on-ground block, so it must not hit.
        var ac = MakeAircraft(onGround: true, altitude: 0);
        var canvas = MakeCanvas(ac);

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        var click = new Point(sx + OffsetX + 1f, sy + OffsetY + AltitudeRowY);

        Assert.Null(canvas.FindDataBlockAtPoint(click));
    }

    private static AircraftModel MakeAircraft(bool onGround, double altitude)
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            Destination = "KLAX",
            FlightRules = "IFR",
            TransponderMode = "C", // avoid the SqStby line so line counts are 2 (ground) / 3 (airborne)
        };
        ac.IsOnGround = onGround;
        ac.Altitude = altitude;
        ac.Position = new LatLon(FieldLat, FieldLon);
        return ac;
    }

    private static GroundCanvas MakeCanvas(AircraftModel ac)
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = FieldLat;
        canvas.Viewport.CenterLon = FieldLon;
        canvas.Viewport.Zoom = 1.0;
        canvas.Viewport.PixelWidth = 800f;
        canvas.Viewport.PixelHeight = 600f;
        canvas.AirportCenterLat = FieldLat;
        canvas.AirportCenterLon = FieldLon;
        canvas.AirportElevation = 0;
        canvas.Aircraft = new[] { ac };
        return canvas;
    }
}
