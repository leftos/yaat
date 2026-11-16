using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The ring layer, framed and drawn: a placed ring draws on its own track only and a ring whose aircraft is not drawn draws
/// nothing; ATPA cones draw with ATPA DIST on and not off, hide the track's manual cone and leave its J-ring; RINGS dims
/// every ring and ATPA cone, 0 drawing no ring pixel, and nothing else.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeFrameRingsTests
{
    private const int Width = 1000;
    private const int Height = 800;
    private const float FontSize = 12f;

    // The renderer's ring colour (the Radar's TPA blue), duplicated rather than exposed.
    private static readonly SKColor RingBlue = new(90, 180, 255);
    private static readonly SKColor SymbolBlue = new(0, 176, 255);

    private static readonly ScopeAlertOptions NoAtpa = ScopeTestAircraft.AllAlerts with { ShowAtpa = false };

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static ScopeFrame Frame(ScopeAlertOptions alerts, IReadOnlyDictionary<string, ScopeTrackRings> rings, params AircraftModel[] aircraft)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            null,
            Viewport(),
            ScopeTemplates.StarsId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            alerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.All,
            HistoryCount: 0,
            LocalRings: rings
        );
        return ScopeFrame.Build(input);
    }

    private static Dictionary<string, ScopeTrackRings> Rings(string callsign, ScopeTrackRings rings) => new() { [callsign] = rings };

    private static ScopeTrack TrackOf(ScopeFrame frame, string callsign) => Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign);

    private static SKBitmap Render(ScopeFrame frame, ScopeBrightness brightness)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new ScopeRenderer();
        renderer.Render(
            canvas,
            Viewport(),
            new ScopeRenderSnapshot(frame, [], new Dictionary<string, string>(), brightness, FontSize, new ScopePalette(null, null, SKColors.White))
        );
        canvas.Flush();
        return bitmap;
    }

    private static SKRectI Around(SKPoint p, int r = 2)
    {
        int x = (int)MathF.Round(p.X);
        int y = (int)MathF.Round(p.Y);
        return new SKRectI(x - r, y - r, x + r + 1, y + r + 1);
    }

    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap, SKRectI area)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                yield return bitmap.GetPixel(x, y);
            }
        }
    }

    private static bool AnyPixel(SKBitmap bitmap, SKRectI area, SKColor color) => Pixels(bitmap, area).Any(c => c == color);

    private static bool AnyLit(SKBitmap bitmap, SKRectI area) => Pixels(bitmap, area).Any(c => (c.Red + c.Green + c.Blue) > 0);

    private static AircraftModel Plain(string callsign)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = callsign;
        return ac;
    }

    [Fact]
    public void APlacedRing_DrawsOnItsOwnTrackOnly()
    {
        AircraftModel other = Plain("OTH1");
        other.Position = new LatLon(37.1, -122.1);
        ScopeFrame frame = Frame(NoAtpa, Rings("UAL238", new ScopeTrackRings(3.0, 4.0, ScopeDriKind.Reduced)), Plain("UAL238"), other);

        ScopeRingShapes rings = TrackOf(frame, "UAL238").Rings;
        Assert.NotNull(rings.JRing);
        Assert.NotNull(rings.Cone);
        Assert.Equal(4, rings.DriArcs.Count);
        Assert.True(TrackOf(frame, "OTH1").Rings.IsEmpty);
    }

    [Fact]
    public void ARingWhoseAircraftIsGone_DrawsNothing()
    {
        ScopeFrame frame = Frame(NoAtpa, Rings("GONE1", new ScopeTrackRings(3.0, null, null)), Plain("UAL238"));

        Assert.All(frame.Tracks, t => Assert.True(t.Rings.IsEmpty));
    }

    [Fact]
    public void AtpaCone_DrawsWithAtpaDistOn_AndNotOff()
    {
        AircraftModel trailer = ScopeTestAircraft.CreateAtpaTrailer();
        AircraftModel lead = ScopeTestAircraft.CreateAtpaLead();

        ScopeRingShapes on = TrackOf(Frame(ScopeTestAircraft.AllAlerts, new Dictionary<string, ScopeTrackRings>(), trailer, lead), "UAL238").Rings;
        ScopeRingShapes off = TrackOf(Frame(NoAtpa, new Dictionary<string, ScopeTrackRings>(), trailer, lead), "UAL238").Rings;

        ScopeWedge cone = Assert.IsType<ScopeWedge>(on.AtpaCone);
        Assert.Equal("3", cone.Label);
        Assert.Equal(Yaat.Sim.Data.Vnas.AtpaConeState.Warning, on.AtpaState);
        Assert.True(off.IsEmpty);
        Assert.True(TrackOf(Frame(ScopeTestAircraft.AllAlerts, new Dictionary<string, ScopeTrackRings>(), trailer, lead), "SWA1234").Rings.IsEmpty);
    }

    [Fact]
    public void AtpaCone_WithItsLeadNotDrawn_OrNoAtpaState_IsNone()
    {
        ScopeFrame noLead = Frame(ScopeTestAircraft.AllAlerts, new Dictionary<string, ScopeTrackRings>(), ScopeTestAircraft.CreateAtpaTrailer());
        ScopeFrame noState = Frame(
            ScopeTestAircraft.AllAlerts,
            new Dictionary<string, ScopeTrackRings>(),
            Plain("UAL238"),
            ScopeTestAircraft.CreateAtpaLead()
        );

        Assert.True(Assert.Single(noLead.Tracks).Rings.IsEmpty);
        Assert.True(TrackOf(noState, "UAL238").Rings.IsEmpty);
    }

    [Fact]
    public void AtpaCone_HidesTheManualCone_AndTheJRingStillDraws()
    {
        Dictionary<string, ScopeTrackRings> rings = Rings("UAL238", new ScopeTrackRings(3.0, 5.0, null));

        ScopeRingShapes withAtpa = TrackOf(
            Frame(ScopeTestAircraft.AllAlerts, rings, ScopeTestAircraft.CreateAtpaTrailer(), ScopeTestAircraft.CreateAtpaLead()),
            "UAL238"
        ).Rings;
        ScopeRingShapes withoutAtpa = TrackOf(
            Frame(NoAtpa, rings, ScopeTestAircraft.CreateAtpaTrailer(), ScopeTestAircraft.CreateAtpaLead()),
            "UAL238"
        ).Rings;

        Assert.NotNull(withAtpa.AtpaCone);
        Assert.Null(withAtpa.Cone);
        Assert.NotNull(withAtpa.JRing);
        Assert.NotNull(withoutAtpa.Cone);
        Assert.NotNull(withoutAtpa.JRing);
    }

    [Fact]
    public void AJRing_LightsItsPixels_InTheTpaBlue_AndRings0DrawsNone()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Frame(NoAtpa, Rings("UAL238", new ScopeTrackRings(3.0, null, null)), Plain("UAL238"));
        ScopeTrack track = Assert.Single(frame.Tracks);
        ScopeRingCircle ring = Assert.IsType<ScopeRingCircle>(track.Rings.JRing);
        // Due west of the target: clear of the block, the leader and the label.
        SKRectI west = Around(ring.Points[54]);

        using SKBitmap full = Render(frame, ScopeBrightness.Default);
        using SKBitmap off = Render(frame, ScopeBrightness.Default with { Rings = 0 });

        Assert.True(AnyPixel(full, west, RingBlue), "no ring-blue pixel on the J-ring");
        Assert.False(AnyLit(off, west), "RINGS 0 still drew the J-ring");
    }

    [Fact]
    public void Rings_DimsTheRingOnly_NotTheSymbol()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Frame(NoAtpa, Rings("UAL238", new ScopeTrackRings(3.0, null, ScopeDriKind.Standard)), Plain("UAL238"));
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Rings = 0 });

        SKRectI symbolLeft = Around(new SKPoint(track.Symbol.X - 5f, track.Symbol.Y), 1);
        Assert.True(AnyPixel(bitmap, symbolLeft, SymbolBlue), "RINGS dimmed the symbol");
        Assert.False(AnyLit(bitmap, Around(track.Rings.DriArcs[0][54])), "RINGS 0 still drew the DRI");
    }

    [Fact]
    public void AWarningAtpaCone_DrawsYellow_AndRings0DrawsNone()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Frame(
            ScopeTestAircraft.AllAlerts,
            new Dictionary<string, ScopeTrackRings>(),
            ScopeTestAircraft.CreateAtpaTrailer(),
            ScopeTestAircraft.CreateAtpaLead()
        );
        ScopeWedge cone = Assert.IsType<ScopeWedge>(TrackOf(frame, "UAL238").Rings.AtpaCone);
        SKRectI tip = Around(cone.Tip);

        using SKBitmap full = Render(frame, ScopeBrightness.Default);
        using SKBitmap off = Render(frame, ScopeBrightness.Default with { Rings = 0 });

        Assert.True(AnyPixel(full, tip, SKColors.Yellow), "no yellow pixel at the warning cone's tip");
        Assert.False(AnyLit(off, tip), "RINGS 0 still drew the ATPA cone");
    }
}
