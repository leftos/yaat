using Avalonia.Headless.XUnit;
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
/// Measure lines on the Scope: the Scope resolves only the lines taken on it and only an anchor picked on it, places each
/// readout clear of the Scope's blocks, and ships the lines in its render snapshot.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeMeasureTests
{
    private static readonly LatLon West = new(37.0, -122.2);
    private static readonly LatLon East = new(37.0, -121.8);

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static RangeBearingLine Line(int slot, RblView view) => new(slot, RblEndpoint.AtPoint(West, "W"), RblEndpoint.AtPoint(East, "E"), view);

    private static readonly RblTrackLookup NoTracks = _ => null;

    private static ScopeFrame Build(MapViewport viewport, params AircraftModel[] aircraft)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var input = new ScopeFrameInput(
            aircraft,
            null,
            viewport,
            ScopeTemplates.StarsId,
            metrics,
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
            IntentPaths: new ScopeIntentPathCache(),
            null
        );
        return ScopeFrame.Build(input);
    }

    [Fact]
    public void Resolve_WithTheScopeView_ReturnsOnlyTheScopesLines()
    {
        RangeBearingLine[] lines = [Line(1, RblView.Radar), Line(2, RblView.Ground), Line(3, RblView.Scope)];

        List<ResolvedRbl> resolved = RangeBearingLineResolver.Resolve(lines, NoTracks, RblUnits.NauticalMiles, RblView.Scope);

        Assert.Equal(3, Assert.Single(resolved).Slot);
    }

    [AvaloniaFact]
    public void TheSnapshot_CarriesTheScopesLines_AndOnlyAnAnchorPickedOnTheScope()
    {
        var canvas = new ScopeCanvas
        {
            RangeBearingLines = [Line(1, RblView.Radar), Line(2, RblView.Scope)],
            MeasureCursor = new Avalonia.Point(10, 10),
        };
        try
        {
            ScopeRenderSnapshot snapshot = canvas.RenderSnapshotForTesting();
            Assert.Equal(2, Assert.Single(snapshot.RangeBearingLines).Slot);
            Assert.Null(snapshot.PendingRangeBearingLine);

            canvas.MeasureAnchor = new RblPendingAnchor(RblEndpoint.AtPoint(West, "W"), RblView.Radar);
            Assert.Null(canvas.RenderSnapshotForTesting().PendingRangeBearingLine);

            canvas.MeasureAnchor = new RblPendingAnchor(RblEndpoint.AtPoint(West, "W"), RblView.Scope);
            ResolvedRbl? pending = canvas.RenderSnapshotForTesting().PendingRangeBearingLine;
            Assert.NotNull(pending);
            Assert.Equal(0, pending.Slot);
            Assert.Equal(West, pending.A);
        }
        finally
        {
            canvas.Dispose();
        }
    }

    [Fact]
    public void AReadout_OverAScopeFrame_SitsClearOfTheBlock()
    {
        MapViewport viewport = Viewport();
        ScopeFrame frame = Build(viewport, ScopeTestAircraft.CreateModel());
        SKRect block = Assert.Single(frame.Tracks).Block.Bounds;
        // The far end sits just left of the block, so CRC's East spot (9 px right of it) lands on the block.
        (double farLat, double farLon) = viewport.ScreenToLatLon(block.Left - RblReadoutPlacement.SpotGapPx + 2, block.MidY);
        (double nearLat, double nearLon) = viewport.ScreenToLatLon(block.Left - 300, block.MidY);
        var line = new ResolvedRbl(1, new LatLon(nearLat, nearLon), new LatLon(farLat, farLon), "090/1.00-1", false, false);
        using SKFont font = PlatformHelper.MonospaceFont(RangeBearingRenderer.LabelFontSize);
        List<RblReadout> readouts = RangeBearingRenderer.BuildReadouts([line], null, viewport, font);

        SKRect unobstructed = RblReadoutPlacement.PlaceAll(readouts, ScopeCanvas.BuildRblObstacles(Build(viewport), viewport), [], [])[1];
        SKRect placed = RblReadoutPlacement.PlaceAll(readouts, ScopeCanvas.BuildRblObstacles(frame, viewport), [], [])[1];

        Assert.True(unobstructed.IntersectsWith(block));
        Assert.False(placed.IntersectsWith(block));
    }
}
