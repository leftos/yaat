using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GroundNavigator"/> on synthetic fixtures. No
/// real airport data; each test builds a minimal <c>PhaseContext</c> + route
/// inline and drives the navigator through <c>Tick</c> loops.
/// </summary>
public class GroundNavigatorTests
{
    private readonly ITestOutputHelper _out;

    public GroundNavigatorTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture(LatLon position, double acHeadingDeg, double startSpeedKts = 0)
    {
        var aircraft = new AircraftState
        {
            Callsign = "NAV",
            AircraftType = "B738",
            Position = position,
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
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static TaxiRouteSegment MakeStraightSegment(GroundNode from, GroundNode to, string name = "A")
    {
        double distNm = GeoMath.DistanceNm(from.Position, to.Position);
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

    // ---- Snapshot round-trip ----

    [Fact]
    public void FromSnapshot_RestoresTargetState()
    {
        var dto = new GroundNavigatorDto
        {
            TargetNodeId = 42,
            TargetLat = 37.0,
            TargetLon = -122.0,
            SegmentFromLat = 36.999,
            SegmentFromLon = -121.999,
            PrevDistToTarget = 0.001,
            CurrentNodeRequiredSpeed = 15,
            MaxSpeedKts = 30,
        };

        var nav = GroundNavigator.FromSnapshot(dto);
        Assert.Equal(42, nav.TargetNodeId);
        Assert.Equal(30, nav.MaxSpeedKts);
    }

    // ---- Straight segment drive ----

    [Fact]
    public void StraightSegment_DrivesAircraftToTargetNode()
    {
        // 200 ft straight east; aircraft starts at From heading east with 0 speed.
        var fromNode = MakeNode(1, 37.0, -122.0);
        var (toLat, toLon) = GeoMath.ProjectPoint(fromNode.Position, new TrueHeading(90.0), 200.0 / GeoMath.FeetPerNm);
        var toNode = MakeNode(2, toLat, toLon);

        var route = new TaxiRoute { Segments = [MakeStraightSegment(fromNode, toNode)], HoldShortPoints = [] };

        var (aircraft, ctx) = MakeFixture(fromNode.Position, 90.0);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
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

        double distToTargetFt = GeoMath.DistanceNm(aircraft.Position, toNode.Position) * GeoMath.FeetPerNm;
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
        var (aircraft, ctx) = MakeFixture(new LatLon(p0Lat, p0Lon), 0.0, startSpeedKts: 10.0);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
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
        double finalDistToNode1Ft = GeoMath.DistanceNm(aircraft.Position, node1.Position) * GeoMath.FeetPerNm;
        _out.WriteLine($"Arc drive: arrived={arrived}, finalHdg={finalHdg:F1}°, distToExit={finalDistToNode1Ft:F2}ft");

        Assert.True(arrived, "navigator did not reach ArrivedAtNode within 200 ticks");
        // Exit heading should be ~east (90°) — the aircraft was rotated through
        // the closed-form arc, so by construction heading matches the exit tangent.
        double hdgDiffFromEast = Math.Abs(((finalHdg - 90.0 + 540.0) % 360.0) - 180.0);
        Assert.True(hdgDiffFromEast < 1.0, $"final heading {finalHdg:F1}° not close to 90° east (diff {hdgDiffFromEast:F2}°)");
        // Aircraft should be close to the arc exit node position.
        Assert.True(finalDistToNode1Ft < 5.0, $"final dist to exit node {finalDistToNode1Ft:F1}ft > 5ft");
    }

    // ---- Entry alignment slow-turn ----

    /// <summary>
    /// Aircraft is heading 103° but the route's first segment heads 284° (a
    /// 181° flip). Without entry alignment, TickArc/TickStraight would either
    /// snap the heading or pure-pursuit cuts a wide loop. With entry
    /// alignment, GroundNavigator builds a slow-turn from the aircraft's
    /// current pose to the segment start tangent — heading rotates smoothly
    /// at GroundTurnRate-bounded sub-tick steps without any per-tick jump &gt;
    /// the rate * dt.
    /// </summary>
    [Fact]
    public void StraightSegment_MisalignedAircraft_RotatesSmoothlyWithoutSnap()
    {
        // 200 ft straight east; aircraft starts at From heading west (180° off).
        var fromNode = MakeNode(1, 37.0, -122.0);
        var (toLat, toLon) = GeoMath.ProjectPoint(fromNode.Position, new TrueHeading(90.0), 200.0 / GeoMath.FeetPerNm);
        var toNode = MakeNode(2, toLat, toLon);

        var route = new TaxiRoute { Segments = [MakeStraightSegment(fromNode, toNode)], HoldShortPoints = [] };

        // Aircraft heading 270° (west) vs segment heading 90° (east) = 180° delta.
        var (aircraft, ctx) = MakeFixture(fromNode.Position, acHeadingDeg: 270.0);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        // Drive a few ticks and capture per-tick heading deltas. With entry
        // alignment, the slow-turn arc geometry rotates the heading by the
        // arc's per-tick sweep — bounded by `MaxSpeedKts / radius` — instead
        // of a single 180° snap.
        double prevHeading = aircraft.TrueHeading.Degrees;
        double maxTickDelta = 0;
        for (int tick = 0; tick < 200; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            nav.Tick(ctx, isLastSegment: true, _ => true);
            double tickDelta = new TrueHeading(prevHeading).AbsAngleTo(aircraft.TrueHeading);
            if (tickDelta > maxTickDelta)
            {
                maxTickDelta = tickDelta;
            }
            prevHeading = aircraft.TrueHeading.Degrees;
        }

        _out.WriteLine($"Misaligned-entry: maxTickHeadingDelta={maxTickDelta:F2}° per 0.25s tick, finalHdg={aircraft.TrueHeading.Degrees:F1}°");

        // Per-tick heading change must stay below an upper bound that catches
        // the snap. SlowTurnSpeedKts (3 kt) on jet NoseWheel (25 ft) gives an
        // arc rate of ~12°/s = ~3°/tick at dt=0.25s. The pre-bug snap was
        // ~180° in one tick. A 30°/tick threshold catches the snap with
        // margin and is well above any possible legitimate slow-turn step.
        Assert.True(maxTickDelta < 30.0, $"per-tick heading delta {maxTickDelta:F1}° exceeds 30° — heading is snapping");
    }

    /// <summary>
    /// Aircraft already aligned with the first segment within tolerance —
    /// entry alignment must NOT inject a slow-turn. Verify by asserting the
    /// aircraft starts moving forward immediately (would be deferred at
    /// SlowTurnSpeedKts=3 if alignment had been injected).
    /// </summary>
    [Fact]
    public void StraightSegment_AlignedAircraft_NoEntryAlignmentInjected()
    {
        var fromNode = MakeNode(1, 37.0, -122.0);
        var (toLat, toLon) = GeoMath.ProjectPoint(fromNode.Position, new TrueHeading(90.0), 200.0 / GeoMath.FeetPerNm);
        var toNode = MakeNode(2, toLat, toLon);

        var route = new TaxiRoute { Segments = [MakeStraightSegment(fromNode, toNode)], HoldShortPoints = [] };

        // Aircraft heading 95° vs segment 90° = only 5° off, well under the
        // 30° entry alignment threshold.
        var (aircraft, ctx) = MakeFixture(fromNode.Position, acHeadingDeg: 95.0);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        // Run for 30 ticks (~7.5 s). With direct segment engagement (no
        // slow-turn) the aircraft should have accelerated past 5 kt within
        // a few seconds — the slow-turn cap is 3 kt, so exceeding it confirms
        // the segment-level taxi speed is in effect.
        for (int tick = 0; tick < 30; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            nav.Tick(ctx, isLastSegment: true, _ => true);
        }

        _out.WriteLine($"Aligned-entry: ias={aircraft.IndicatedAirspeed:F2}kt after 30 ticks");
        Assert.True(aircraft.IndicatedAirspeed > 5.0, $"aligned aircraft should taxi above slow-turn cap; ias={aircraft.IndicatedAirspeed:F2}kt");
    }
}
