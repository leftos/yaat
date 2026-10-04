using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Guards the draw-vs-hit-test offset contract for ground datablocks a measurement readout has nudged: the hit-test
/// offset (<see cref="GroundCanvas.ResolvedDataBlockOffset"/>) must be the offset the render snapshot ships to the draw.
/// </summary>
public class GroundDatablockHitTestParityTests
{
    private const double FieldLat = 37.62;
    private const double FieldLon = -122.39;

    [AvaloniaFact]
    public void HitTestOffset_MatchesNudgedDrawOffset_Ground()
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            Destination = "KLAX",
            FlightRules = "IFR",
            TransponderMode = "C",
            IsOnGround = true,
            Altitude = 0,
            Position = new LatLon(FieldLat, FieldLon),
        };
        GroundCanvas canvas = MakeCanvas(ac);
        canvas.CaptureSnapshotPlacement();
        canvas.CaptureSnapshotPlacement();
        SKPoint unnudgedOffset = canvas.ResolvedDataBlockOffset(ac.Callsign);

        // The far end 20 px into the block's first line: every readout spot covers the block, so the readout nudges it.
        (float sx, float sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        (double bLat, double bLon) = canvas.Viewport.ScreenToLatLon(sx + unnudgedOffset.X + 20f, sy + unnudgedOffset.Y + 1f);
        (double aLat, double aLon) = canvas.Viewport.ScreenToLatLon(100f, 500f);
        canvas.RangeBearingLines =
        [
            new RangeBearingLine(1, RblEndpoint.AtPoint(new LatLon(aLat, aLon), ""), RblEndpoint.AtPoint(new LatLon(bLat, bLon), ""), RblView.Ground),
        ];
        (IReadOnlyDictionary<string, SKPoint> offsets, IReadOnlyDictionary<int, SKRect> readouts) = canvas.CaptureSnapshotPlacement();

        SKPoint shipped = Assert.Contains(ac.Callsign, offsets);
        Assert.NotEqual(unnudgedOffset, shipped);
        Assert.Equal(shipped, canvas.ResolvedDataBlockOffset(ac.Callsign));
        SKRect drawn = DataBlockLayout.Compute(ac, sx, sy, shipped, canvas.HitTestStyle, isAirborne: false).Rect;
        SKRect hitRect = canvas.DataBlockRect(ac);
        Assert.Equal(drawn, hitRect);
        Assert.False(
            SKRect.Intersect(Assert.Contains(1, readouts), hitRect) is { Width: > 0f, Height: > 0f },
            "the nudged block must clear the readout"
        );
    }

    private static GroundCanvas MakeCanvas(AircraftModel ac)
    {
        var canvas = new GroundCanvas { DeconflictMode = DatablockDeconflictMode.FreeForm };
        canvas.Viewport.CenterLat = FieldLat;
        canvas.Viewport.CenterLon = FieldLon;
        canvas.Viewport.Zoom = 1.0;
        canvas.Viewport.PixelWidth = 800f;
        canvas.Viewport.PixelHeight = 600f;
        canvas.AirportCenterLat = FieldLat;
        canvas.AirportCenterLon = FieldLon;
        canvas.AirportElevation = 0;
        canvas.Aircraft = [ac];
        return canvas;
    }
}
