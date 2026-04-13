using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GroundNavigatorV2"/> on synthetic fixtures. No
/// real airport data; each test builds a minimal <c>PhaseContext</c> + route
/// inline and drives the navigator through <c>Tick</c> loops.
/// </summary>
public class GroundNavigatorV2Tests
{
    private readonly ITestOutputHelper _out;

    public GroundNavigatorV2Tests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture(double acLat, double acLon, double acHeadingDeg, double startSpeedKts = 0)
    {
        var aircraft = new AircraftState
        {
            Callsign = "NAVV2",
            AircraftType = "B738",
            Latitude = acLat,
            Longitude = acLon,
            TrueHeading = new TrueHeading(acHeadingDeg),
            IndicatedAirspeed = startSpeedKts,
            IsOnGround = true,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };
        return (aircraft, ctx);
    }

    private static GroundNode MakeNode(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Latitude = lat,
            Longitude = lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static TaxiRouteSegment MakeStraightSegment(GroundNode from, GroundNode to, string name = "A")
    {
        double distNm = GeoMath.DistanceNm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
        var edge = new GroundEdge
        {
            Nodes = [from, to],
            TaxiwayName = name,
            DistanceNm = distNm,
        };
        var directed = new DirectionalEdge
        {
            Edge = edge,
            FromNode = from,
            ToNode = to,
        };
        return new TaxiRouteSegment { Edge = directed, TaxiwayName = name };
    }

    // ---- Factory dispatch ----

    [Fact]
    public void Factory_CreateV2_ReturnsGroundNavigatorV2()
    {
        var nav = GroundNavigatorFactory.Create(GroundNavigatorImpl.V2);
        Assert.IsType<GroundNavigatorV2>(nav);
        Assert.IsAssignableFrom<IGroundNavigator>(nav);
    }

    [Fact]
    public void Factory_FromSnapshot_V2_ReturnsGroundNavigatorV2()
    {
        var dto = new GroundNavigatorDto
        {
            ImplVersion = 2,
            TargetNodeId = 42,
            TargetLat = 37.0,
            TargetLon = -122.0,
            SegmentFromLat = 36.999,
            SegmentFromLon = -121.999,
            PrevDistToTarget = 0.001,
            CurrentNodeRequiredSpeed = 15,
            MaxSpeedKts = 30,
        };

        var nav = GroundNavigatorFactory.FromSnapshot(dto);
        Assert.IsType<GroundNavigatorV2>(nav);
        Assert.Equal(42, nav.TargetNodeId);
        Assert.Equal(30, nav.MaxSpeedKts);
    }

    [Fact]
    public void Factory_Override_ScopesToExecutionContext()
    {
        Assert.Equal(GroundNavigatorImpl.V1, GroundNavigatorFactory.CurrentImpl);
        using (GroundNavigatorFactory.Override(GroundNavigatorImpl.V2))
        {
            Assert.Equal(GroundNavigatorImpl.V2, GroundNavigatorFactory.CurrentImpl);
            var nav = GroundNavigatorFactory.Create();
            Assert.IsType<GroundNavigatorV2>(nav);
        }
        Assert.Equal(GroundNavigatorImpl.V1, GroundNavigatorFactory.CurrentImpl);
    }

    // ---- Straight segment drive ----

    [Fact]
    public void StraightSegment_DrivesAircraftToTargetNode()
    {
        // 200 ft straight east; aircraft starts at From heading east with 0 speed.
        var fromNode = MakeNode(1, 37.0, -122.0);
        var (toLat, toLon) = GeoMath.ProjectPoint(fromNode.Latitude, fromNode.Longitude, new TrueHeading(90.0), 200.0 / GeoMath.FeetPerNm);
        var toNode = MakeNode(2, toLat, toLon);

        var route = new TaxiRoute { Segments = [MakeStraightSegment(fromNode, toNode)], HoldShortPoints = [] };

        var (aircraft, ctx) = MakeFixture(fromNode.Latitude, fromNode.Longitude, 90.0);
        var nav = new GroundNavigatorV2 { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        Assert.Equal(2, nav.TargetNodeId);

        bool arrived = false;
        for (int tick = 0; tick < 200; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var result = nav.Tick(ctx, isLastSegment: true, _ => true);
            if (result == NavigatorResult.ArrivedAtNode)
            {
                arrived = true;
                break;
            }
        }

        double distToTargetFt = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, toNode.Latitude, toNode.Longitude) * GeoMath.FeetPerNm;
        _out.WriteLine($"Straight drive: arrived={arrived}, distToTarget={distToTargetFt:F2}ft, gs={aircraft.IndicatedAirspeed:F2}kt");

        Assert.True(arrived, "navigator did not reach ArrivedAtNode within 200 ticks");
        Assert.True(distToTargetFt < 10.0, $"final dist to target {distToTargetFt:F1}ft > 10ft");
    }

    // ---- Arc segment drive ----

    [Fact]
    public void ArcSegment_DrivesAircraftThroughCircularArc()
    {
        // Synthesised 90° right turn at radius 70 ft. Aircraft enters at the
        // arc entry heading north (0°), should exit heading east (90°).
        const double p0Lat = 37.0;
        const double p0Lon = -122.0;
        const double radiusFt = 70.0;
        double rNm = radiusFt / GeoMath.FeetPerNm;

        var (centerLat, centerLon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(90.0), rNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(0.0), rNm);

        double kappa = (4.0 / 3.0) * Math.Tan(Math.PI / 8.0);
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(0.0), kappa * rNm);
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3Lat, p3Lon, new TrueHeading(270.0), kappa * rNm);

        var node0 = MakeNode(10, p0Lat, p0Lon);
        var node1 = MakeNode(11, p3Lat, p3Lon);
        double arcLenFt = (Math.PI / 2.0) * radiusFt;
        var arc = new GroundArc
        {
            Nodes = [node0, node1],
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = radiusFt,
            DistanceNm = arcLenFt / GeoMath.FeetPerNm,
            TaxiwayNames = ["A"],
        };
        var directed = new DirectionalEdge
        {
            Edge = arc,
            FromNode = node0,
            ToNode = node1,
        };
        var segment = new TaxiRouteSegment { Edge = directed, TaxiwayName = "A" };
        var route = new TaxiRoute { Segments = [segment], HoldShortPoints = [] };

        // Start with some forward speed so the arc integrator advances from tick 0.
        var (aircraft, ctx) = MakeFixture(p0Lat, p0Lon, 0.0, startSpeedKts: 10.0);
        var nav = new GroundNavigatorV2 { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        bool arrived = false;
        for (int tick = 0; tick < 200; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var result = nav.Tick(ctx, isLastSegment: true, _ => true);
            if (result == NavigatorResult.ArrivedAtNode)
            {
                arrived = true;
                break;
            }
        }

        double finalHdg = aircraft.TrueHeading.Degrees;
        double finalDistToNode1Ft = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, node1.Latitude, node1.Longitude) * GeoMath.FeetPerNm;
        _out.WriteLine($"Arc drive: arrived={arrived}, finalHdg={finalHdg:F1}°, distToExit={finalDistToNode1Ft:F2}ft");

        Assert.True(arrived, "navigator did not reach ArrivedAtNode within 200 ticks");
        // Exit heading should be ~east (90°) — the aircraft was rotated through
        // the closed-form arc, so by construction heading matches the exit tangent.
        double hdgDiffFromEast = Math.Abs(((finalHdg - 90.0 + 540.0) % 360.0) - 180.0);
        Assert.True(hdgDiffFromEast < 1.0, $"final heading {finalHdg:F1}° not close to 90° east (diff {hdgDiffFromEast:F2}°)");
        // Aircraft should be close to the arc exit node position.
        Assert.True(finalDistToNode1Ft < 5.0, $"final dist to exit node {finalDistToNode1Ft:F1}ft > 5ft");
    }
}
