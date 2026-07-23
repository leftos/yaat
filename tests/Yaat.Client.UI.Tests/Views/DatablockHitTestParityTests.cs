using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Guards the draw-vs-hit-test geometry contract for radar datablocks.
/// <para>
/// The rect is produced twice per aircraft: the renderer measures it while drawing
/// (<c>TargetRenderer</c> → <see cref="RadarDatablockLayout.Compute"/>) and the canvas measures it
/// again for hit-testing and drag (<see cref="RadarCanvas.ComputeStableRectAtOrigin"/>), against a
/// separate font/paint pair. Nothing in the compiler ties the two together — if they drift, clicks
/// and drags silently miss the block that is visibly on screen.
/// </para>
/// <para>
/// docs/radar-rendering.md called this out as an unenforced invariant with no parity test. It is
/// enforced here.
/// </para>
/// </summary>
public class DatablockHitTestParityTests
{
    private static AircraftModel CreateModel() =>
        new()
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0),
            Altitude = 23000,
            GroundSpeed = 250,
            CwtCode = "D",
            Scratchpad1 = "SFO",
        };

    /// <summary>
    /// The renderer's measuring pair at a given size, mirroring how <c>TargetRenderer</c> builds its
    /// datablock font (bold monospace, subpixel positioning).
    /// </summary>
    private static TextStyle DrawStyleAt(float size) => new(PlatformHelper.MonospaceFontBold(size), new SKPaint());

    private static SKRect DrawRectAtOrigin(AircraftModel ac, RadarCanvas canvas, float size) =>
        RadarDatablockLayout
            .Compute(ac, 0, 0, DrawStyleAt(size), canvas.FlashNoLandingClearance, canvas.ShowConflictAlerts, conflictPeer: null, callsignMarker: "")
            .Rect;

    [AvaloniaFact]
    public void HitTestRect_MatchesDrawRect_AtDefaultFontSize()
    {
        var ac = CreateModel();
        var canvas = new RadarCanvas();

        Assert.Equal(DrawRectAtOrigin(ac, canvas, canvas.DatablockTextSize), canvas.ComputeStableRectAtOrigin(ac));
    }

    /// <summary>
    /// The regression that motivated this suite: the hit-test font used to be pinned at 12 px while
    /// the draw font followed <c>UserPreferences.RadarDatablockFontSize</c>, so every non-default
    /// datablock size measured the click rect against different metrics than the drawn glyphs.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(9f)]
    [InlineData(14f)]
    [InlineData(18f)]
    public void HitTestRect_TracksDrawRect_WhenDatablockFontSizeChanges(float size)
    {
        var ac = CreateModel();
        var canvas = new RadarCanvas { DatablockTextSize = size };

        Assert.Equal(DrawRectAtOrigin(ac, canvas, size), canvas.ComputeStableRectAtOrigin(ac));
    }

    [AvaloniaFact]
    public void HitTestRect_GrowsWithFontSize()
    {
        var ac = CreateModel();

        var small = new RadarCanvas { DatablockTextSize = 9f }.ComputeStableRectAtOrigin(ac);
        var large = new RadarCanvas { DatablockTextSize = 18f }.ComputeStableRectAtOrigin(ac);

        Assert.True(large.Width > small.Width, "a larger datablock font must produce a wider hit rect");
        Assert.True(large.Height > small.Height, "a larger datablock font must produce a taller hit rect");
    }
}
