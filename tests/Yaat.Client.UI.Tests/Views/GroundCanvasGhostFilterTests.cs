using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The Ground (surface) display must not paint or hit-test STARS "unsupported"/ghost tracks —
// they have no surface-radar return. The one exception is a ghost overlay still attached to a
// real aircraft that is physically on the ground (e.g. a departure pre-tagged to autotrack once
// airborne): that is a genuine surface target and stays visible. These drive the shared
// FilterActiveAircraft chokepoint through the public FindAircraftAtPoint hit-tester.
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
    public void GhostOverlayAirborne_IsHidden()
    {
        // Same overlay once the aircraft is airborne (N785Q at 2500 ft) — now a STARS ghost, not a
        // surface target, so it drops off the Ground View.
        Assert.False(
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
        canvas.Aircraft = [ac];

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        return ReferenceEquals(canvas.FindAircraftAtPoint(new Point(sx, sy)), ac);
    }
}
