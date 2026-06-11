using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The Tower Cab (out-the-window) display shows real aircraft. The only thing it drops is a pure
// phantom — a CRC DA/VP data block typed for a callsign with no real aircraft body — because there
// is no aircraft in space to see. A ghost overlay is attached to a real scenario aircraft, so it is
// shown like any other aircraft whether on the ground or airborne (the renderer bounds it to 10 nm /
// ceiling-or-6,000 ft AGL). These drive the shared FilterActiveAircraft chokepoint through the
// public FindAircraftAtPoint hit-tester.
public class GroundCanvasGhostFilterTests
{
    private const double CenterLat = 37.72;
    private const double CenterLon = -122.22;

    [AvaloniaFact]
    public void NormalTrack_IsVisible()
    {
        Assert.True(IsVisibleOnGroundView(new AircraftModel { Callsign = "SWA1", IsUnsupported = false }));
    }

    [AvaloniaFact]
    public void PurePhantom_IsHidden()
    {
        // CRC DA/VP datablock for a callsign with no real aircraft body.
        Assert.False(
            IsVisibleOnGroundView(
                new AircraftModel
                {
                    Callsign = "GHOST1",
                    IsUnsupported = true,
                    IsGhostOverlay = false,
                }
            )
        );
    }

    [AvaloniaFact]
    public void GhostOverlayOnGround_IsVisible()
    {
        // Real aircraft taxiing with a ghost overlay set up so it autotracks once airborne — the
        // N785Q-while-taxiing case. The overlay is STARS-only; the real ground target stays.
        Assert.True(
            IsVisibleOnGroundView(
                new AircraftModel
                {
                    Callsign = "N785Q",
                    IsUnsupported = true,
                    IsGhostOverlay = true,
                    IsOnGround = true,
                }
            )
        );
    }

    [AvaloniaFact]
    public void GhostOverlayAirborne_IsVisible()
    {
        // A pre-tagged ghost-overlay departure that has lifted off is still a real aircraft you'd see
        // out the tower window — it stays on the view (the renderer bounds it by range/altitude). It
        // must not flicker off at rotation just because IsOnGround flipped.
        Assert.True(
            IsVisibleOnGroundView(
                new AircraftModel
                {
                    Callsign = "N785Q",
                    IsUnsupported = true,
                    IsGhostOverlay = true,
                    IsOnGround = false,
                }
            )
        );
    }

    [AvaloniaFact]
    public void DelayedSpawn_IsHidden()
    {
        Assert.False(IsVisibleOnGroundView(new AircraftModel { Callsign = "DAL2", Status = "Delayed (30s)" }));
    }

    // Places the single aircraft at the viewport center and clicks its projected screen position;
    // FindAircraftAtPoint returns it only if FilterActiveAircraft kept it on the Ground View.
    private static bool IsVisibleOnGroundView(AircraftModel ac)
    {
        ac.Position = new LatLon(CenterLat, CenterLon);

        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = CenterLat;
        canvas.Viewport.CenterLon = CenterLon;
        canvas.Viewport.Zoom = 1.0;
        canvas.Viewport.PixelWidth = 800f;
        canvas.Viewport.PixelHeight = 600f;
        // The aircraft sits at the field; airborne ones (altitude 0) are then inside the 10 nm /
        // 6,000 ft AGL bound the filter now applies, so the test isolates the membership rules.
        canvas.AirportCenterLat = CenterLat;
        canvas.AirportCenterLon = CenterLon;
        canvas.AirportElevation = 0;
        canvas.Aircraft = [ac];

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        return ReferenceEquals(canvas.FindAircraftAtPoint(new Point(sx, sy)), ac);
    }
}
