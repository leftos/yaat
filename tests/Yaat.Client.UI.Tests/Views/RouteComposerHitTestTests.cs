using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The route composer's hit test over the committed navdata around SFO: a drawn fix symbol is hit within 10 screen pixels,
/// a route point drawn over a fix wins the hit, nothing past the range is hit, and fixes are drawn (so can be hit) only
/// while the view spans 60 NM or less.
/// </summary>
public class RouteComposerHitTestTests
{
    private static NavigationDatabase NavDb => NavigationDatabase.Instance;

    [Fact]
    public void AClickThreePixelsFromSfosSymbol_HitsTheFixSfo()
    {
        MapViewport viewport = ViewAround(FixPosition("SFO"), 20);
        var scene = RouteComposerScene.Build([], RouteComposerScene.FixesInView(NavDb, viewport));
        (float x, float y) = viewport.LatLonToScreen(FixPosition("SFO").Lat, FixPosition("SFO").Lon);

        RouteComposerHit? hit = RouteComposerHitTest.Find(scene, viewport, x + 3, y);

        Assert.NotNull(hit);
        Assert.Null(hit.PointIndex);
        Assert.Equal("SFO", hit.Fix?.Name);
    }

    [Fact]
    public void ARoutePointOverAFix_WinsTheHit()
    {
        LatLon sfo = FixPosition("SFO");
        MapViewport viewport = ViewAround(sfo, 20);
        RouteComposerPoint point = new(RoutePoint.AtFix("SFO"), sfo, null, new RoutePointResult(sfo, "SFO", null, null));
        var scene = RouteComposerScene.Build([point], RouteComposerScene.FixesInView(NavDb, viewport));
        (float x, float y) = viewport.LatLonToScreen(sfo.Lat, sfo.Lon);

        RouteComposerHit? hit = RouteComposerHitTest.Find(scene, viewport, x + 2, y + 2);

        Assert.NotNull(hit);
        Assert.Equal(0, hit.PointIndex);
        Assert.Null(hit.Fix);
    }

    [Fact]
    public void TheHitRadius_IsTenPixels()
    {
        Assert.Equal(10f, RouteComposerHitTest.Range);
        LatLon sfo = FixPosition("SFO");
        MapViewport viewport = ViewAround(sfo, 20);
        RouteComposerPoint point = new(RoutePoint.AtFix("SFO"), sfo, null, new RoutePointResult(sfo, "SFO", null, null));
        var scene = RouteComposerScene.Build([point], []);
        (float x, float y) = viewport.LatLonToScreen(sfo.Lat, sfo.Lon);

        Assert.Equal(0, RouteComposerHitTest.Find(scene, viewport, x + 10, y)?.PointIndex);
        Assert.Equal(0, RouteComposerHitTest.Find(scene, viewport, x, y - 9.5f)?.PointIndex);
        Assert.Null(RouteComposerHitTest.Find(scene, viewport, x + 10.5f, y));
    }

    [Fact]
    public void Fixes_AreDrawnAtSixtyNmOrLess_AndHiddenAboveIt()
    {
        LatLon sfo = FixPosition("SFO");
        MapViewport near = ViewAround(sfo, 59);
        MapViewport far = ViewAround(sfo, 80);

        Assert.True(RouteComposerScene.SpanNm(near) <= 60.0 + 1e-6);
        Assert.Contains(RouteComposerScene.FixesInView(NavDb, near), f => f.Name == "SFO");
        Assert.Empty(RouteComposerScene.FixesInView(NavDb, far));
        Assert.Empty(RouteComposerScene.FixesInView(null, near));
    }

    // An 800 by 600 view centred on a point whose wider side spans the given NM.
    private static MapViewport ViewAround(LatLon center, double spanNm)
    {
        var viewport = new MapViewport
        {
            PixelWidth = 800,
            PixelHeight = 600,
            CenterLat = center.Lat,
            CenterLon = center.Lon,
        };
        viewport.Zoom *= RouteComposerScene.SpanNm(viewport) / spanNm;
        return viewport;
    }

    private static LatLon FixPosition(string name)
    {
        (double Lat, double Lon) pos = NavDb.GetFixPosition(name) ?? throw new InvalidOperationException(name + " is not in navdata");
        return new LatLon(pos.Lat, pos.Lon);
    }
}
