using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Rings tool's geometry: J-rings centred on the target and anchored to the map, cones along the ground track at ±2
/// degrees, the 3 NM DRI's four arcs with gaps on north, east, south and west, the ATPA cone toward the lead, CRC's size
/// grid, and where the size labels sit.
/// </summary>
public class ScopeRingGeometryTests
{
    private static readonly LatLon Origin = new(37.0, -122.0);

    // MapViewport draws 5000 px per degree of latitude at zoom 1: 5000 / 60 px per NM north-south.
    private const double PixelsPerNm = 5000.0 / 60.0;

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static AircraftModel Track(double headingDeg, double trackDeg) =>
        new()
        {
            Callsign = "UAL238",
            Position = Origin,
            Heading = new TrueHeading(headingDeg),
            TrueTrack = trackDeg,
            GroundSpeed = 250,
        };

    private static SKPoint Screen(MapViewport vp, LatLon p)
    {
        (float x, float y) = vp.LatLonToScreen(p.Lat, p.Lon);
        return new SKPoint(x, y);
    }

    private static SKPoint Expected(MapViewport vp, double bearing, double nm) =>
        Screen(vp, GeoMath.ProjectPoint(Origin, new TrueHeading(bearing), nm));

    private static void Near(SKPoint expected, SKPoint actual)
    {
        Assert.Equal(expected.X, actual.X, 0.01f);
        Assert.Equal(expected.Y, actual.Y, 0.01f);
    }

    [Fact]
    public void JRing_IsACircleOfItsRadiusInPixels_AtZoom1()
    {
        MapViewport vp = Viewport();
        ScopeRingCircle ring = ScopeRingGeometry.JRing(Track(0, 0), 3.0, new SKPoint(20, -20), vp);
        SKPoint symbol = Screen(vp, Origin);

        Assert.Equal(73, ring.Points.Count);
        Near(ring.Points[0], ring.Points[^1]);
        // Due north of the target, 3 NM is 250 px up the screen.
        Assert.Equal(3.0 * PixelsPerNm, symbol.Y - ring.Points[0].Y, 0.5);
        Assert.Equal(symbol.X, ring.Points[0].X, 0.5f);
        for (int i = 0; i < ring.Points.Count; i++)
        {
            Near(Expected(vp, i * 5, 3.0), ring.Points[i]);
        }
    }

    [Fact]
    public void JRing_DoublesInPixels_WhenTheMapZoomsIn()
    {
        MapViewport vp = Viewport();
        vp.Zoom = 2.0;
        ScopeRingCircle ring = ScopeRingGeometry.JRing(Track(0, 0), 3.0, new SKPoint(20, -20), vp);

        Assert.Equal(2 * 3.0 * PixelsPerNm, Screen(vp, Origin).Y - ring.Points[0].Y, 0.5);
    }

    [Fact]
    public void Cone_RunsAlongTheGroundTrack_NotTheHeading_InWind()
    {
        MapViewport vp = Viewport();
        ScopeWedge cone = ScopeRingGeometry.Cone(Track(headingDeg: 360, trackDeg: 10), 5.0, vp);

        Near(Screen(vp, Origin), cone.Apex);
        Near(Expected(vp, 10, 5.0), cone.Tip);
        Assert.True(SKPoint.Distance(cone.Tip, Expected(vp, 0, 5.0)) > 20f, "the cone follows the heading");
    }

    [Fact]
    public void Cone_IsTwoDegreesEitherSideOfItsAxis()
    {
        MapViewport vp = Viewport();
        ScopeWedge cone = ScopeRingGeometry.Cone(Track(0, 90), 10.0, vp);

        Assert.Equal(2.0, ScopeRingGeometry.ConeHalfAngleDeg);
        Near(Expected(vp, 88, 10.0), cone.Left);
        Near(Expected(vp, 90, 10.0), cone.Tip);
        Near(Expected(vp, 92, 10.0), cone.Right);
        Assert.Equal("10", cone.Label);
    }

    [Fact]
    public void Dri5_IsOneClosedFiveMileCircle()
    {
        MapViewport vp = Viewport();
        IReadOnlyList<SKPoint> circle = Assert.Single(ScopeRingGeometry.DriArcs(Origin, ScopeDriKind.Standard, vp));

        Assert.Equal(73, circle.Count);
        Near(Expected(vp, 0, 5.0), circle[0]);
        Near(Expected(vp, 180, 5.0), circle[36]);
    }

    [Fact]
    public void Dri3_IsFourArcsFrom7Point5To82Point5_WithGapsCentredOnNorthEastSouthWest()
    {
        MapViewport vp = Viewport();
        IReadOnlyList<IReadOnlyList<SKPoint>> arcs = ScopeRingGeometry.DriArcs(Origin, ScopeDriKind.Reduced, vp);

        Assert.Equal(4, arcs.Count);
        for (int q = 0; q < 4; q++)
        {
            Near(Expected(vp, 7.5 + (q * 90), 3.0), arcs[q][0]);
            Near(Expected(vp, 82.5 + (q * 90), 3.0), arcs[q][^1]);
        }

        // Each gap runs from one arc's end to the next arc's start, centred on a cardinal point.
        foreach (double cardinal in new[] { 0.0, 90.0, 180.0, 270.0 })
        {
            SKPoint gapCentre = Expected(vp, cardinal, 3.0);
            float nearest = arcs.SelectMany(a => a).Min(p => SKPoint.Distance(p, gapCentre));
            Assert.True(nearest > 15f, $"an arc point lies in the gap at {cardinal}");
        }
    }

    [Fact]
    public void AtpaCone_PointsAlongTheBearingFromTheTrailingTrackToTheLead_AsLongAsItsSeparation()
    {
        MapViewport vp = Viewport();
        AircraftModel trailing = Track(0, 0);
        trailing.AtpaAllowedSeparationNm = 2.5;
        var lead = new AircraftModel { Callsign = "SWA1234", Position = GeoMath.ProjectPoint(Origin, new TrueHeading(45), 6.0) };

        ScopeWedge cone = Assert.IsType<ScopeWedge>(ScopeRingGeometry.AtpaCone(trailing, lead, vp));

        double axis = GeoMath.BearingTo(Origin, lead.Position);
        Near(Expected(vp, axis, 2.5), cone.Tip);
        Near(Expected(vp, axis - 2, 2.5), cone.Left);
        Near(Expected(vp, axis + 2, 2.5), cone.Right);
        Assert.Equal("2.5", cone.Label);
    }

    [Fact]
    public void AtpaCone_WithNoSeparation_IsNone()
    {
        AircraftModel trailing = Track(0, 0);
        var lead = new AircraftModel { Callsign = "SWA1234", Position = GeoMath.ProjectPoint(Origin, new TrueHeading(45), 6.0) };

        Assert.Null(ScopeRingGeometry.AtpaCone(trailing, lead, Viewport()));
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(3.0, true)]
    [InlineData(9.9, true)]
    [InlineData(10.0, true)]
    [InlineData(30.0, true)]
    [InlineData(0.9, false)]
    [InlineData(10.5, false)]
    [InlineData(31.0, false)]
    [InlineData(2.55, false)]
    public void SizeGrid_TakesTenthsTo9Point9_ThenWholeMilesTo30(double nm, bool accepted) =>
        Assert.Equal(accepted, ScopeRingGeometry.IsRingSize(nm));

    [Theory]
    [InlineData(3.0, 1, 3.1)]
    [InlineData(9.9, 1, 10.0)]
    [InlineData(10.0, -1, 9.9)]
    [InlineData(30.0, 1, 30.0)]
    [InlineData(1.0, -1, 1.0)]
    [InlineData(12.0, 2, 14.0)]
    public void StepSize_WalksTheGrid_AndStopsAtItsEnds(double current, int steps, double expected) =>
        Assert.Equal(expected, ScopeRingGeometry.StepSize(current, steps), 6);

    [Theory]
    [InlineData(3.0, "3.0")]
    [InlineData(9.9, "9.9")]
    [InlineData(12.0, "12")]
    public void FormatSize_WritesTenthsBelow10_AndWholeMilesFrom10(double nm, string expected) =>
        Assert.Equal(expected, ScopeRingGeometry.FormatSize(nm));

    [Fact]
    public void JRingLabel_SitsOnTheRing_OppositeTheLeader()
    {
        MapViewport vp = Viewport();
        SKPoint symbol = Screen(vp, Origin);

        // A block up and to the right: the label goes down and to the left, on the ring.
        ScopeRingCircle ring = ScopeRingGeometry.JRing(Track(0, 0), 3.0, new SKPoint(30, -30), vp);
        Near(Expected(vp, 225, 3.0), ring.LabelAt);
        Assert.Equal("3", ring.Label);

        // A block to the left: the label goes right.
        ScopeRingCircle left = ScopeRingGeometry.JRing(Track(0, 0), 3.0, new SKPoint(-40, 0), vp);
        Near(Expected(vp, 90, 3.0), left.LabelAt);
        Assert.True(left.LabelAt.X > symbol.X);
    }

    [Fact]
    public void ConeLabel_SitsHalfwayAlongTheAxis()
    {
        MapViewport vp = Viewport();
        ScopeWedge cone = ScopeRingGeometry.Cone(Track(0, 120), 6.0, vp);

        Near(Expected(vp, 120, 3.0), cone.LabelAt);
        Assert.Equal("6", cone.Label);
    }

    [Theory]
    [InlineData(23000, true)]
    [InlineData(23100, false)]
    public void ReducedDri_NeedsFl230OrBelow(double altitudeFt, bool allowed) =>
        Assert.Equal(allowed, ScopeRingGeometry.AllowsReducedDri(altitudeFt));
}
