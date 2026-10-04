using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
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

    private static SKRect DrawRectAtOrigin(AircraftModel ac, RadarCanvas canvas, float size, AircraftModel? atpaLead) =>
        RadarDatablockLayout
            .Compute(
                ac,
                new DatablockPlacement(0, 0, DrawStyleAt(size), canvas.FlashNoLandingClearance),
                new DatablockOverlays(canvas.ShowConflictAlerts, ConflictPeer: null, canvas.ShowAtpa, atpaLead),
                callsignMarker: ""
            )
            .Rect;

    [AvaloniaFact]
    public void HitTestRect_MatchesDrawRect_AtDefaultFontSize()
    {
        AircraftModel ac = CreateModel();
        var canvas = new RadarCanvas();

        Assert.Equal(DrawRectAtOrigin(ac, canvas, canvas.DatablockTextSize, atpaLead: null), canvas.ComputeStableRectAtOrigin(ac));
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
        AircraftModel ac = CreateModel();
        var canvas = new RadarCanvas { DatablockTextSize = size };

        Assert.Equal(DrawRectAtOrigin(ac, canvas, size, atpaLead: null), canvas.ComputeStableRectAtOrigin(ac));
    }

    [AvaloniaFact]
    public void HitTestRect_MatchesDrawRect_WhileIdenting()
    {
        // The ident swaps line 2's CWT/type token for "ID", which changes the block's widest line —
        // the hit rect has to follow it or clicks land off the visibly narrower block.
        AircraftModel ac = CreateModel();
        ac.IsIdenting = true;
        var canvas = new RadarCanvas();

        Assert.Equal(DrawRectAtOrigin(ac, canvas, canvas.DatablockTextSize, atpaLead: null), canvas.ComputeStableRectAtOrigin(ac));
    }

    [AvaloniaFact]
    public void HitTestRect_StableAcrossIdentFlashCycle()
    {
        // The ident dim-pulses instead of blanking, so unlike NoLndgClnc it needs no reserved slot —
        // but that only holds if the rect really is identical on both phases of the 500 ms cycle.
        AircraftModel ac = CreateModel();
        ac.IsIdenting = true;
        var canvas = new RadarCanvas();

        SKRect first = canvas.ComputeStableRectAtOrigin(ac);
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            Assert.Equal(first, canvas.ComputeStableRectAtOrigin(ac));
        }
    }

    [AvaloniaFact]
    public void HitTestRect_MatchesDrawRect_WithAtpaLead()
    {
        // The ATPA in-trail line adds a row to the block. The draw path resolves the lead from the
        // renderer's per-frame callsign index and the hit-test path scans the bound collection — both
        // have to end up measuring the same line, or clicks miss the bottom of a visibly taller block.
        AircraftModel ac = CreateModel();
        ac.AtpaLeadCallsign = "SWA1234";
        ac.AtpaAllowedSeparationNm = 3.0;
        AircraftModel lead = CreateModel();
        lead.Callsign = "SWA1234";
        lead.Position = new LatLon(37.0, -122.0 + (4.00704 / 60.0));
        var canvas = new RadarCanvas { ShowAtpa = true, Aircraft = [ac, lead] };

        SKRect hitRect = canvas.ComputeStableRectAtOrigin(ac);
        Assert.Equal(DrawRectAtOrigin(ac, canvas, canvas.DatablockTextSize, lead), hitRect);

        SKRect withoutAtpa = new RadarCanvas { Aircraft = [ac, lead] }.ComputeStableRectAtOrigin(ac);
        Assert.True(hitRect.Height > withoutAtpa.Height, "the ATPA in-trail line must add a row to the hit rect");
    }

    [AvaloniaFact]
    public void HitTestRect_MatchesDrawRect_WhenAtpaLeadFilteredFromDraw()
    {
        // ATPA in-trail pair on final: the lead touches down first, so it is on the ground and the
        // radar's FilterAircraft drops it from the draw pass — while the trailing aircraft is still
        // airborne and still carries AtpaLeadCallsign. The renderer indexes conflict/ATPA peers from
        // the *filtered* draw list (TargetRenderer._callsignIndex), so it resolves no lead and omits
        // the ATPA in-trail row. The hit-test path (ComputeStableRectAtOrigin → ResolveByCallsign)
        // scans the *unfiltered* bound collection, resolves the on-ground lead, and measures the
        // taller block. Draw and hit-test must still agree, or the block is clickable/draggable in an
        // empty region below what is drawn.
        AircraftModel ac = CreateModel();
        ac.AtpaLeadCallsign = "SWA1234";
        ac.AtpaAllowedSeparationNm = 3.0;
        AircraftModel lead = CreateModel();
        lead.Callsign = "SWA1234";
        lead.IsOnGround = true;
        lead.Position = new LatLon(37.0, -122.0 + (2.50 / 60.0));
        var canvas = new RadarCanvas { ShowAtpa = true, Aircraft = [ac, lead] };

        // The renderer's peer index is built from the same filtered list the draw pass uses, so the
        // on-ground lead is invisible to the draw path and the ATPA row is not drawn.
        IReadOnlyList<AircraftModel> drawn = RadarCanvas.FilterAircraft(
            canvas.Aircraft,
            canvas.ShowTopDown,
            canvas.ShowSpeechBubbles,
            canvas.AlwaysShowGroundBubblesOnRadar,
            groundShownAirportId: null,
            DateTime.UtcNow
        );
        AircraftModel? drawLead = drawn.FirstOrDefault(a => string.Equals(a.Callsign, "SWA1234", StringComparison.Ordinal));
        Assert.Null(drawLead);

        Assert.Equal(DrawRectAtOrigin(ac, canvas, canvas.DatablockTextSize, drawLead), canvas.ComputeStableRectAtOrigin(ac));
    }

    [AvaloniaFact]
    public void HitTestRect_GrowsWithFontSize()
    {
        AircraftModel ac = CreateModel();

        SKRect small = new RadarCanvas { DatablockTextSize = 9f }.ComputeStableRectAtOrigin(ac);
        SKRect large = new RadarCanvas { DatablockTextSize = 18f }.ComputeStableRectAtOrigin(ac);

        Assert.True(large.Width > small.Width, "a larger datablock font must produce a wider hit rect");
        Assert.True(large.Height > small.Height, "a larger datablock font must produce a taller hit rect");
    }

    [AvaloniaFact]
    public void HitTestOffset_MatchesNudgedDrawOffset_Radar() => AssertNudgedBlockParity(DatablockDeconflictMode.FreeForm);

    [AvaloniaFact]
    public void NudgeApplies_WhenDeconflictModeOff() => AssertNudgedBlockParity(DatablockDeconflictMode.Off);

    [AvaloniaFact]
    public void ManualOffsetBlock_NeverNudged_Radar()
    {
        // A dragged block over the CRC spot is fixed: the readout moves around it and the block ships no nudge delta.
        AircraftModel ac = CreateModel();
        var state = new RadarDataBlockViewState();
        var manual = new SKPoint(28f, -28f);
        state.ManualOffsets[ac.Callsign] = manual;
        var canvas = new RadarCanvas
        {
            Aircraft = [ac],
            DeconflictMode = DatablockDeconflictMode.Off,
            DataBlockState = state,
        };
        canvas.Viewport.CenterLat = ac.Position.Lat;
        canvas.Viewport.CenterLon = ac.Position.Lon;
        canvas.Viewport.PixelWidth = 800f;
        canvas.Viewport.PixelHeight = 600f;
        SKRect block = canvas.ComputeDataBlockPlacement(ac).Rect;
        (double bLat, double bLon) = canvas.Viewport.ScreenToLatLon(block.MidX, block.MidY);
        (double aLat, double aLon) = canvas.Viewport.ScreenToLatLon(100f, 500f);
        canvas.RangeBearingLines =
        [
            new RangeBearingLine(1, RblEndpoint.AtPoint(new LatLon(aLat, aLon), ""), RblEndpoint.AtPoint(new LatLon(bLat, bLon), ""), RblView.Radar),
        ];

        (IReadOnlyDictionary<string, SKPoint> offsets, IReadOnlyDictionary<int, SKRect> readouts) = canvas.CaptureSnapshotPlacement();

        Assert.False(offsets.ContainsKey(ac.Callsign));
        Assert.Equal(manual, canvas.ComputeDataBlockPlacement(ac).Offset);
        Assert.Contains(1, readouts);
    }

    /// <summary>
    /// Puts a measurement's far end at the centre of an auto-placed datablock, so every readout spot covers the block
    /// and the readout nudges it, then asserts the hit-test rect is the rect the snapshot's shipped offset draws.
    /// </summary>
    private static void AssertNudgedBlockParity(DatablockDeconflictMode mode)
    {
        AircraftModel ac = CreateModel();
        var canvas = new RadarCanvas { Aircraft = [ac], DeconflictMode = mode };
        canvas.Viewport.CenterLat = ac.Position.Lat;
        canvas.Viewport.CenterLon = ac.Position.Lon;
        canvas.Viewport.PixelWidth = 800f;
        canvas.Viewport.PixelHeight = 600f;
        canvas.CaptureSnapshotPlacement();
        canvas.CaptureSnapshotPlacement();
        (SKPoint unnudgedOffset, SKRect block) = canvas.ComputeDataBlockPlacement(ac);

        (double bLat, double bLon) = canvas.Viewport.ScreenToLatLon(block.MidX, block.MidY);
        (double aLat, double aLon) = canvas.Viewport.ScreenToLatLon(100f, 500f);
        canvas.RangeBearingLines =
        [
            new RangeBearingLine(1, RblEndpoint.AtPoint(new LatLon(aLat, aLon), ""), RblEndpoint.AtPoint(new LatLon(bLat, bLon), ""), RblView.Radar),
        ];
        (IReadOnlyDictionary<string, SKPoint> offsets, IReadOnlyDictionary<int, SKRect> readouts) = canvas.CaptureSnapshotPlacement();

        SKPoint shipped = Assert.Contains(ac.Callsign, offsets);
        Assert.NotEqual(unnudgedOffset, shipped);
        (float sx, float sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        SKRect rectAtOrigin = canvas.ComputeStableRectAtOrigin(ac);
        var drawn = new SKRect(
            rectAtOrigin.Left + sx + shipped.X,
            rectAtOrigin.Top + sy + shipped.Y,
            rectAtOrigin.Right + sx + shipped.X,
            rectAtOrigin.Bottom + sy + shipped.Y
        );
        (SKPoint hitOffset, SKRect hitRect) = canvas.ComputeDataBlockPlacement(ac);
        Assert.Equal(shipped, hitOffset);
        Assert.Equal(drawn, hitRect);
        Assert.False(
            SKRect.Intersect(Assert.Contains(1, readouts), hitRect) is { Width: > 0f, Height: > 0f },
            "the nudged block must clear the readout"
        );
    }
}
