using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The frame's plural hit tests: every block a point lands on, topmost first, and every symbol within the click radius,
/// nearest first, as a right click over stacked tracks lists them.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeFrameHitsTests
{
    private static MapViewport Viewport()
    {
        var vp = new MapViewport
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };
        return vp;
    }

    private static ScopeFrame Build(params AircraftModel[] aircraft)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            null,
            Viewport(),
            ScopeTemplates.StarsId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            OthersLevel: ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache()
        );
        return ScopeFrame.Build(input);
    }

    [Fact]
    public void BlocksAt_TwoBlocksDrawnOverEachOther_ListsThemTopmostFirst()
    {
        AircraftModel lower = ScopeTestAircraft.CreateModel();
        AircraftModel upper = ScopeTestAircraft.CreateModel();
        upper.Callsign = "DAL55";
        ScopeFrame frame = Build(lower, upper);
        ScopeTrack top = frame.Tracks[^1];
        float x = top.Block.Bounds.MidX;
        float y = top.Block.Bounds.MidY;

        IReadOnlyList<AircraftModel> hits = frame.BlocksAt(x, y);

        Assert.Same(top.Aircraft, hits[0]);
        Assert.Equal(frame.Tracks.Count(t => t.Block.Bounds.Contains(x, y)), hits.Count);
        Assert.All(hits, ac => Assert.Contains(frame.Tracks, t => ReferenceEquals(t.Aircraft, ac) && t.Block.Bounds.Contains(x, y)));
    }

    [Fact]
    public void BlocksAt_OffEveryBlock_ReturnsEmpty()
    {
        ScopeFrame frame = Build(ScopeTestAircraft.CreateModel());

        Assert.Empty(frame.BlocksAt(-50f, -50f));
    }

    /// <summary>
    /// The ATPA trailer's block carries the distance at line 2's right end, so it ends at line 2: a click on the band below,
    /// where the Radar's own ATPA row was, no longer hits it.
    /// </summary>
    [Fact]
    public void BlocksAt_TheOldAtpaRowsBand_MissesTheShorterAtpaBlock()
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        float lineHeight = new TextStyle(font, paint).LineHeight;
        ScopeFrame frame = Build(ScopeTestAircraft.CreateAtpaTrailer(), ScopeTestAircraft.CreateAtpaLead());
        ScopeTrack trailer = Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == "UAL238");
        SKRect bounds = trailer.Block.Bounds;
        FieldRect atpa = Assert.Single(trailer.Block.Fields, f => f.Slot == ScopeFieldSlot.Atpa);
        Assert.Equal(trailer.Block.Fields.Max(f => f.Rect.Top), atpa.Rect.Top);
        float x = bounds.Left + 2f;

        Assert.Contains(trailer.Aircraft, frame.BlocksAt(x, bounds.MidY));
        Assert.DoesNotContain(trailer.Aircraft, frame.BlocksAt(x, bounds.Bottom + (lineHeight / 2f)));
    }

    [Fact]
    public void TargetsNear_SymbolsInsideTheClickRadius_ListsThemNearestFirst()
    {
        AircraftModel centre = ScopeTestAircraft.CreateModel();
        AircraftModel near = ScopeTestAircraft.CreateModel();
        near.Callsign = "DAL55";
        AircraftModel far = ScopeTestAircraft.CreateModel();
        far.Callsign = "SWA12";
        far.Position = new LatLon(centre.Position.Lat + 0.05, centre.Position.Lon);
        ScopeFrame positioned = Build(centre, near, far);
        SKPoint symbol = positioned.Tracks.Single(t => ReferenceEquals(t.Aircraft, centre)).Symbol;
        MapViewport viewport = Viewport();
        (double nearLat, double nearLon) = viewport.ScreenToLatLon(symbol.X + 15f, symbol.Y);
        near.Position = new LatLon(nearLat, nearLon);

        ScopeFrame frame = Build(centre, near, far);

        IReadOnlyList<AircraftModel> hits = frame.TargetsNear(symbol.X, symbol.Y);

        Assert.Equal([centre, near], hits);
    }

    [Fact]
    public void TargetsNear_OffEverySymbol_ReturnsEmpty()
    {
        ScopeFrame frame = Build(ScopeTestAircraft.CreateModel());

        Assert.Empty(frame.TargetsNear(-50f, -50f));
    }
}
