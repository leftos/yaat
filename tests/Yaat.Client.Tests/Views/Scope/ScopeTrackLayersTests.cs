using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Scope's vector and history geometry: a vector ends ground speed times minutes along the ground track, never the
/// heading, and draws nothing at 0 minutes or under 1 kt; the history is the newest N samples, oldest first.
/// </summary>
public class ScopeTrackLayersTests
{
    private static readonly LatLon Origin = new(37.0, -122.0);

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static AircraftModel Track(double headingDeg, double trackDeg, double groundSpeed, bool onGround = false) =>
        new()
        {
            Callsign = "UAL238",
            Position = Origin,
            Heading = new TrueHeading(headingDeg),
            TrueTrack = trackDeg,
            GroundSpeed = groundSpeed,
            IsOnGround = onGround,
        };

    private static SKPoint ScreenOf(MapViewport vp, LatLon point)
    {
        (float x, float y) = vp.LatLonToScreen(point.Lat, point.Lon);
        return new SKPoint(x, y);
    }

    private static double[][] Samples(int count) => [.. Enumerable.Range(0, count).Select(i => new[] { 37.0 + (i * 0.01), -122.0 })];

    [Fact]
    public void VectorEnd_ProjectsAlongTheGroundTrack_NotTheHeading()
    {
        MapViewport vp = Viewport();

        SKPoint? end = ScopeTrackLayers.VectorEnd(Track(headingDeg: 360, trackDeg: 10, groundSpeed: 300), 1, vp);

        SKPoint expected = ScreenOf(vp, GeoMath.ProjectPoint(Origin, new TrueHeading(10), 5.0));
        SKPoint alongHeading = ScreenOf(vp, GeoMath.ProjectPoint(Origin, new TrueHeading(360), 5.0));
        Assert.NotNull(end);
        Assert.Equal(expected.X, end.Value.X, 0.01f);
        Assert.Equal(expected.Y, end.Value.Y, 0.01f);
        Assert.True(SKPoint.Distance(end.Value, alongHeading) > 1f);
    }

    [Fact]
    public void VectorEnd_ZeroMinutes_DrawsNoLine() => Assert.Null(ScopeTrackLayers.VectorEnd(Track(10, 10, 300), 0, Viewport()));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.99)]
    public void VectorEnd_UnderOneKnot_DrawsNoLine(double groundSpeed) =>
        Assert.Null(ScopeTrackLayers.VectorEnd(Track(10, 10, groundSpeed), 1, Viewport()));

    [Fact]
    public void VectorEnd_OnTheGroundMovingFiveKnots_DrawsALineAlongItsTrack()
    {
        MapViewport vp = Viewport();

        SKPoint? end = ScopeTrackLayers.VectorEnd(Track(headingDeg: 90, trackDeg: 100, groundSpeed: 5, onGround: true), 8, vp);

        SKPoint expected = ScreenOf(vp, GeoMath.ProjectPoint(Origin, new TrueHeading(100), 5.0 * 8 / 60.0));
        Assert.NotNull(end);
        Assert.Equal(expected.X, end.Value.X, 0.01f);
        Assert.Equal(expected.Y, end.Value.Y, 0.01f);
    }

    [Theory]
    [InlineData(ScopeVectorOwnership.All, true, true)]
    [InlineData(ScopeVectorOwnership.All, false, true)]
    [InlineData(ScopeVectorOwnership.Own, true, true)]
    [InlineData(ScopeVectorOwnership.Own, false, false)]
    public void ShowsVector_OwnDrawsOnlyOwnTracks_AllDrawsEvery(ScopeVectorOwnership ownership, bool isOwn, bool expected) =>
        Assert.Equal(expected, ScopeTrackLayers.ShowsVector(ownership, isOwn));

    [Fact]
    public void HistoryDots_AreTheNewestNSamples_OldestFirst()
    {
        MapViewport vp = Viewport();
        AircraftModel ac = Track(10, 10, 300);
        double[][] samples = Samples(10);
        ac.PositionHistory = samples;

        IReadOnlyList<SKPoint> dots = ScopeTrackLayers.HistoryDots(ac, 3, vp);

        Assert.Equal([.. samples[7..].Select(s => ScreenOf(vp, new LatLon(s[0], s[1])))], dots);
    }

    [Fact]
    public void HistoryDots_ZeroCount_DrawsNone()
    {
        AircraftModel ac = Track(10, 10, 300);
        ac.PositionHistory = Samples(10);

        Assert.Empty(ScopeTrackLayers.HistoryDots(ac, 0, Viewport()));
    }

    [Fact]
    public void HistoryDots_NoSamples_DrawsNone()
    {
        AircraftModel ac = Track(10, 10, 300);
        Assert.Empty(ScopeTrackLayers.HistoryDots(ac, 5, Viewport()));

        ac.PositionHistory = [];
        Assert.Empty(ScopeTrackLayers.HistoryDots(ac, 5, Viewport()));
    }

    [Fact]
    public void HistoryDots_CountAboveTheSamples_DrawsWhatExists()
    {
        MapViewport vp = Viewport();
        AircraftModel ac = Track(10, 10, 300);
        double[][] samples = Samples(4);
        ac.PositionHistory = samples;

        IReadOnlyList<SKPoint> dots = ScopeTrackLayers.HistoryDots(ac, 10, vp);

        Assert.Equal([.. samples.Select(s => ScreenOf(vp, new LatLon(s[0], s[1])))], dots);
    }
}
