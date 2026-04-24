using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="PathPrimitiveBuilder.FromSegment"/>. Exercises
/// the pure-math layer of GroundNavigator V2 without any real airport data —
/// every fixture is a synthesised <see cref="GroundNode"/> + edge pair wrapped
/// in a <see cref="DirectionalEdge"/> + <see cref="TaxiRouteSegment"/>.
/// </summary>
public class PathPrimitiveBuilderTests
{
    // ---- Straight segments ----

    [Fact]
    public void FromSegment_Straight_ProducesStraightPrimitiveWithCorrectGeometry()
    {
        // Two nodes 100 ft apart heading east from anchor (37.0, -122.0).
        const double fromLat = 37.0;
        const double fromLon = -122.0;
        var (toLat, toLon) = GeoMath.ProjectPoint(fromLat, fromLon, new TrueHeading(90.0), 100.0 / GeoMath.FeetPerNm);

        var fromNode = new GroundNode
        {
            Id = 1,
            Latitude = fromLat,
            Longitude = fromLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var toNode = new GroundNode
        {
            Id = 2,
            Latitude = toLat,
            Longitude = toLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [fromNode, toNode],
            TaxiwayName = "A",
            DistanceNm = 100.0 / GeoMath.FeetPerNm,
        };
        var directed = new DirectionalEdge
        {
            Edge = edge,
            FromNode = fromNode,
            ToNode = toNode,
        };
        var segment = new TaxiRouteSegment { Edge = directed, TaxiwayName = "A" };

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var straight = Assert.IsType<PathPrimitiveStraight>(primitive);
        Assert.Equal(PathPrimitiveKind.Straight, straight.Kind);
        Assert.Equal(2, straight.ToNodeId);
        Assert.Equal(fromLat, straight.FromLat);
        Assert.Equal(fromLon, straight.FromLon);
        Assert.Equal(toLat, straight.ToLat);
        Assert.Equal(toLon, straight.ToLon);
        Assert.InRange(straight.LengthFt, 99.5, 100.5);
        Assert.InRange(straight.BearingDeg, 89.9, 90.1);
    }

    [Fact]
    public void FromSegment_StraightReversed_FlipsBearing()
    {
        // Construct a straight edge from A to B, but wrap it in a DirectionalEdge
        // traversing B to A. The resulting primitive should have bearing +180°
        // from the underlying edge direction.
        const double aLat = 37.0;
        const double aLon = -122.0;
        var (bLat, bLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 100.0 / GeoMath.FeetPerNm);

        var aNode = new GroundNode
        {
            Id = 1,
            Latitude = aLat,
            Longitude = aLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var bNode = new GroundNode
        {
            Id = 2,
            Latitude = bLat,
            Longitude = bLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [aNode, bNode],
            TaxiwayName = "A",
            DistanceNm = 100.0 / GeoMath.FeetPerNm,
        };
        // Traverse B → A.
        var directed = new DirectionalEdge
        {
            Edge = edge,
            FromNode = bNode,
            ToNode = aNode,
        };
        var segment = new TaxiRouteSegment { Edge = directed, TaxiwayName = "A" };

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var straight = Assert.IsType<PathPrimitiveStraight>(primitive);
        Assert.Equal(1, straight.ToNodeId);
        // Bearing from B to A is 270° (west) since B is east of A.
        Assert.InRange(straight.BearingDeg, 269.9, 270.1);
    }

    // ---- Arc segments ----

    [Fact]
    public void FromSegment_Arc90DegreeRightTurn_ProducesCorrectCircle()
    {
        // Scenario: aircraft enters arc at P0 heading north (0°), turns 90° right,
        // exits at P3 heading east (90°). Radius 70 ft. Centre is 70 ft east of P0
        // (perpendicular-right of north).
        const double p0Lat = 37.0;
        const double p0Lon = -122.0;
        const double radiusFt = 70.0;
        double rNm = radiusFt / GeoMath.FeetPerNm;

        // Compute the circle centre and exit point from the expected geometry.
        var (centerLat, centerLon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(90.0), rNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(0.0), rNm);

        // Bezier control points using the kappa formula (4/3·tan(θ/4)) for a 90° arc.
        double kappa = (4.0 / 3.0) * Math.Tan(Math.PI / 8.0);
        // P1: P0 projected along entry tangent (north, 0°) by kappa·r.
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(0.0), kappa * rNm);
        // P2: P3 projected along reverse of exit tangent. Exit tangent is east (90°),
        // reverse is west (270°) by kappa·r.
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3Lat, p3Lon, new TrueHeading(270.0), kappa * rNm);

        var node0 = new GroundNode
        {
            Id = 10,
            Latitude = p0Lat,
            Longitude = p0Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 11,
            Latitude = p3Lat,
            Longitude = p3Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        // Arc length for a 90° sweep at r=70 ft is (π/2)·r ≈ 110 ft.
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

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var arcPrim = Assert.IsType<PathPrimitiveArc>(primitive);
        Assert.Equal(PathPrimitiveKind.Arc, arcPrim.Kind);
        Assert.Equal(11, arcPrim.ToNodeId);
        Assert.Equal(radiusFt, arcPrim.RadiusFt);
        Assert.True(arcPrim.RightTurn, "0°→90° short way is a right turn");
        Assert.InRange(arcPrim.SweepDeg, 89.0, 91.0);
        Assert.InRange(arcPrim.EntryTangentBearingDeg, -0.5, 0.5);
        Assert.InRange(arcPrim.ExitTangentBearingDeg, 89.5, 90.5);
        // Length should be ~π/2 · 70 ≈ 110 ft.
        Assert.InRange(arcPrim.LengthFt, 108.0, 112.0);

        // Centre should be at (centerLat, centerLon) — 70 ft east of P0.
        double centerErrFt = GeoMath.DistanceNm(arcPrim.CenterLat, arcPrim.CenterLon, centerLat, centerLon) * GeoMath.FeetPerNm;
        Assert.True(centerErrFt < 1.0, $"centre off expected by {centerErrFt:F3}ft");

        // StartBearingFromCenterDeg: centre is east of P0, so P0 is west of
        // centre → bearing from centre to P0 is 270°.
        Assert.InRange(arcPrim.StartBearingFromCenterDeg, 269.5, 270.5);
    }

    [Fact]
    public void FromSegment_Arc90DegreeLeftTurn_ProducesCorrectCircle()
    {
        // Mirror: enter heading north, turn 90° left, exit heading west (270°).
        // Centre is 70 ft west of P0.
        const double p0Lat = 37.0;
        const double p0Lon = -122.0;
        const double radiusFt = 70.0;
        double rNm = radiusFt / GeoMath.FeetPerNm;

        var (centerLat, centerLon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(270.0), rNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(0.0), rNm);

        double kappa = (4.0 / 3.0) * Math.Tan(Math.PI / 8.0);
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(0.0), kappa * rNm);
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3Lat, p3Lon, new TrueHeading(90.0), kappa * rNm);

        var node0 = new GroundNode
        {
            Id = 20,
            Latitude = p0Lat,
            Longitude = p0Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 21,
            Latitude = p3Lat,
            Longitude = p3Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
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

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var arcPrim = Assert.IsType<PathPrimitiveArc>(primitive);
        Assert.False(arcPrim.RightTurn, "0°→270° short way is a left turn");
        Assert.InRange(arcPrim.SweepDeg, 89.0, 91.0);
        Assert.InRange(arcPrim.EntryTangentBearingDeg, -0.5, 0.5);
        Assert.InRange(arcPrim.ExitTangentBearingDeg, 269.5, 270.5);

        // Centre is west of P0 → bearing from centre to P0 is 90° (east).
        Assert.InRange(arcPrim.StartBearingFromCenterDeg, 89.5, 90.5);
    }

    // ---- SlowTurn primitive ----

    [Fact]
    public void SlowTurn_RightTurn_ProducesRightArcWithCorrectGeometry()
    {
        // 90° right turn: entry heading north (0°), exit heading east (90°).
        // Centre of the turn lies 25 ft east of entry (perpendicular-right of
        // north heading). Start bearing from centre → entry is 270° (west,
        // opposite of perpHdg 90°). Sweep 90°. Right turn.
        const double fromLat = 37.0;
        const double fromLon = -122.0;
        const double radiusFt = 25.0;
        const double maxSpeedKts = 3.0;

        var slow = PathPrimitiveBuilder.SlowTurn(
            fromLat: fromLat,
            fromLon: fromLon,
            fromHdgDeg: 0.0,
            toHdgDeg: 90.0,
            radiusFt: radiusFt,
            maxSpeedKts: maxSpeedKts,
            toNodeId: 99
        );

        Assert.Equal(PathPrimitiveKind.SlowTurn, slow.Kind);
        Assert.Equal(99, slow.ToNodeId);
        Assert.Equal(radiusFt, slow.RadiusFt);
        Assert.Equal(maxSpeedKts, slow.MaxSpeedKts);
        Assert.True(slow.RightTurn, "0°→90° short way is a right turn");
        Assert.InRange(slow.SweepDeg, 89.9, 90.1);
        Assert.InRange(slow.EntryTangentBearingDeg, -0.1, 0.1);
        Assert.InRange(slow.ExitTangentBearingDeg, 89.9, 90.1);
        Assert.InRange(slow.StartBearingFromCenterDeg, 269.5, 270.5);

        // Arc length = 90° × 25 ft × π/180 ≈ 39.27 ft
        Assert.InRange(slow.LengthFt, 39.0, 39.5);
    }

    [Fact]
    public void SlowTurn_LeftTurn_ProducesLeftArcWithCorrectGeometry()
    {
        // 90° left turn: entry heading east (90°), exit heading north (0°).
        // Centre sits 25 ft north of entry (perpendicular-left of east heading).
        const double fromLat = 37.0;
        const double fromLon = -122.0;

        var slow = PathPrimitiveBuilder.SlowTurn(
            fromLat: fromLat,
            fromLon: fromLon,
            fromHdgDeg: 90.0,
            toHdgDeg: 0.0,
            radiusFt: 25.0,
            maxSpeedKts: 3.0,
            toNodeId: 100
        );

        Assert.False(slow.RightTurn, "90°→0° short way is a left turn");
        Assert.InRange(slow.SweepDeg, 89.9, 90.1);
        Assert.InRange(slow.EntryTangentBearingDeg, 89.9, 90.1);
        Assert.InRange(slow.ExitTangentBearingDeg, -0.1, 0.1);
    }

    [Fact]
    public void SlowTurn_TightRadiusIsSmallerThanLineUpTurnRadius()
    {
        // Regression guard: NoseWheelTurnRadiusFt must be substantially
        // tighter than LineUpTurnRadiusFt for every category. The SlowTurn
        // primitive is the one callers reach for when they need a footprint
        // smaller than a normal lineup arc.
        foreach (var cat in new[] { AircraftCategory.Jet, AircraftCategory.Turboprop, AircraftCategory.Piston, AircraftCategory.Helicopter })
        {
            double nose = CategoryPerformance.NoseWheelTurnRadiusFt(cat);
            double lineup = CategoryPerformance.LineUpTurnRadiusFt(cat);
            Assert.True(
                nose < lineup * 0.6,
                $"{cat}: nose-wheel radius ({nose} ft) should be substantially tighter than lineup radius ({lineup} ft)"
            );
            Assert.True(nose > 0, $"{cat}: nose-wheel radius must be positive");
        }
    }

    [Fact]
    public void SlowTurn_SpeedCapIsWalkingPace()
    {
        // SlowTurnSpeedKts should be ~walking pace — low enough that full
        // nose-wheel deflection is mechanically usable without tyre scrub
        // dominating. Guard against accidental future tuning into an
        // unrealistic regime.
        Assert.InRange(CategoryPerformance.SlowTurnSpeedKts, 1.0, 5.0);
    }

    // ---- GroundNavigator dispatch for SlowTurn ----

    private static (AircraftState Aircraft, PhaseContext Ctx) MakeSlowTurnFixture(double acLat, double acLon, double acHdgDeg)
    {
        var aircraft = new AircraftState
        {
            Callsign = "SLOWTEST",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = new TrueHeading(acHdgDeg),
            IndicatedAirspeed = 0,
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

    [Fact]
    public void GroundNavigator_SlowTurnDispatch_AdvancesOnCircleAndHoldsSpeedCap()
    {
        // Plant the aircraft at the entry of a 90° right SlowTurn and tick
        // forward until the sweep completes. Assert: (1) position stays on
        // the arc circle (invariant I2), (2) target speed is held at the
        // primitive's MaxSpeedKts cap, (3) final heading matches exit tangent.
        const double fromLat = 37.0;
        const double fromLon = -122.0;
        const double radiusFt = 25.0;
        const double maxSpeedKts = 3.0;
        var slow = PathPrimitiveBuilder.SlowTurn(fromLat, fromLon, 0.0, 90.0, radiusFt, maxSpeedKts, toNodeId: 99);

        // Place the aircraft at the arc exit for target lat/lon. The primitive's
        // closed-form playback drives position directly — the "target" is just
        // used for distance-to-target diagnostics and arrival handling.
        double radiusNm = radiusFt / GeoMath.FeetPerNm;
        var (toLat, toLon) = GeoMath.ProjectPoint(slow.CenterLat, slow.CenterLon, new TrueHeading(0.0), radiusNm);

        var (aircraft, ctx) = MakeSlowTurnFixture(fromLat, fromLon, 0.0);
        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupPrimitive(slow, fromLat, fromLon, toLat, toLon, nextSegmentBearingDeg: null);

        bool arrived = false;
        for (int i = 0; i < 400; i++)
        {
            // Speed the physics integration with a simple accelerator — without
            // a real FlightPhysics.Update call the aircraft's IAS never leaves
            // zero and the I7 floor blocks arc advance forever.
            double accelTarget = ctx.Targets.TargetSpeed ?? 0;
            if (aircraft.IndicatedAirspeed < accelTarget)
            {
                aircraft.IndicatedAirspeed = Math.Min(accelTarget, aircraft.IndicatedAirspeed + 3.0 * ctx.DeltaSeconds);
            }

            var result = nav.Tick(ctx, isLastSegment: true, _ => true);
            if (result == NavigatorResult.ArrivedAtNode)
            {
                arrived = true;
                break;
            }

            // Invariant: position must lie on the arc circle of radius
            // radiusFt around (CenterLat, CenterLon).
            double distToCenterFt = GeoMath.DistanceNm(aircraft.Position, new LatLon(slow.CenterLat, slow.CenterLon)) * GeoMath.FeetPerNm;
            Assert.InRange(distToCenterFt, radiusFt - 0.1, radiusFt + 0.1);

            // Target speed is capped at the primitive's MaxSpeedKts.
            Assert.NotNull(ctx.Targets.TargetSpeed);
            Assert.InRange(ctx.Targets.TargetSpeed!.Value, 0.0, maxSpeedKts + 0.001);
        }

        Assert.True(arrived, "SlowTurn dispatch never reached ArrivedAtNode within 400 ticks");
        // Final heading matches exit tangent (90° = east).
        double finalHdg = aircraft.TrueHeading.Degrees;
        Assert.InRange(finalHdg, 89.5, 90.5);
    }

    [Fact]
    public void GroundNavigator_SlowTurnDispatch_RefusesToAdvanceBelowSpeedFloor()
    {
        // Aircraft stationary at arc entry. The navigator must set target speed
        // but must NOT advance the arc on this tick (I7 — no pivot-in-place).
        // Note: the navigator's own AdjustSpeed will accelerate the aircraft
        // toward the cap during this tick, so the speed floor only blocks arc
        // advance on the tick where IAS enters at < floor — subsequent ticks
        // have IAS above the floor and proceed normally.
        const double fromLat = 37.0;
        const double fromLon = -122.0;
        var slow = PathPrimitiveBuilder.SlowTurn(fromLat, fromLon, 0.0, 90.0, 25.0, 3.0, toNodeId: 99);

        var (aircraft, ctx) = MakeSlowTurnFixture(fromLat, fromLon, 0.0);
        aircraft.IndicatedAirspeed = 0;

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupPrimitive(slow, fromLat, fromLon, slow.CenterLat, slow.CenterLon, nextSegmentBearingDeg: null);

        nav.Tick(ctx, isLastSegment: true, _ => true);

        // Position unchanged — the arc integrator did not advance this tick.
        Assert.Equal(fromLat, aircraft.Position.Lat);
        Assert.Equal(fromLon, aircraft.Position.Lon);
        // Target speed nonzero so physics (or AdjustSpeed) can re-accelerate.
        Assert.NotNull(ctx.Targets.TargetSpeed);
        Assert.True(ctx.Targets.TargetSpeed!.Value > 0);
    }
}
