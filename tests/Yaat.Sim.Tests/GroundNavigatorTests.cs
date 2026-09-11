using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
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
public class GroundNavigatorTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

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

    // ---- Short-connector transit (issue #236) ----

    /// <summary>
    /// Lane change across parallel taxiways: a south leg (A) → 90° onto a short east connector (F1) → 90°
    /// onto a south leg (B). The 200 ft connector straight is bracketed by two ~90° turns within the
    /// short-connector length, so the navigator holds the corner flow speed across it instead of accelerating
    /// on the straight and braking back down for the second turn (issue #236 — SFO A→F1→B). Without the fix
    /// the braking curve alone permits ~20+ kt on the connector straight. The peak is measured only once the
    /// aircraft is aligned to the connector centerline (heading within 20° of the 90° east bearing), i.e. on
    /// the straight itself — the entry corner is a separate slow-turn whose speed depends on leg geometry.
    /// </summary>
    [Fact]
    public void ShortConnector_HoldsSteadyLowSpeed_NoSurge()
    {
        var n0 = MakeNode(1, 37.0, -122.0);
        var (l1, o1) = GeoMath.ProjectPoint(n0.Position, new TrueHeading(180.0), 120.0 / GeoMath.FeetPerNm);
        var n1 = MakeNode(2, l1, o1);
        var (l2, o2) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 200.0 / GeoMath.FeetPerNm);
        var n2 = MakeNode(3, l2, o2);
        var (l3, o3) = GeoMath.ProjectPoint(n2.Position, new TrueHeading(180.0), 200.0 / GeoMath.FeetPerNm);
        var n3 = MakeNode(4, l3, o3);

        var route = new TaxiRoute
        {
            Segments = [MakeStraightSegment(n0, n1, "A"), MakeStraightSegment(n1, n2, "F1"), MakeStraightSegment(n2, n3, "B")],
            HoldShortPoints = [],
        };

        var (aircraft, ctx) = MakeFixture(n0.Position, acHeadingDeg: 180.0, startSpeedKts: 10.0);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        double peakOnConnectorStraight = 0.0;
        int straightTicks = 0;
        for (int tick = 0; tick < 4000; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            bool last = route.CurrentSegmentIndex == route.Segments.Count - 1;
            var result = nav.Tick(ctx, last, _ => true);

            bool alignedToConnector = new TrueHeading(aircraft.TrueHeading.Degrees).AbsAngleTo(new TrueHeading(90.0)) < 20.0;
            if (route.CurrentSegmentIndex == 1 && alignedToConnector)
            {
                peakOnConnectorStraight = Math.Max(peakOnConnectorStraight, aircraft.IndicatedAirspeed);
                straightTicks++;
            }

            if (result == NavigatorResult.ArrivedAtNode)
            {
                if (last)
                {
                    break;
                }
                route.CurrentSegmentIndex++;
                nav.SetupSegment(route, ctx, _ => true);
            }
        }

        _out.WriteLine($"ShortConnector: straightTicks={straightTicks} peakOnConnectorStraight={peakOnConnectorStraight:F1}kt");

        Assert.True(straightTicks > 0, "aircraft never traversed the connector straight aligned to its centerline");
        Assert.True(
            peakOnConnectorStraight <= 8.0,
            $"short connector straight should hold a steady low speed (~5 kt), not surge; peaked at {peakOnConnectorStraight:F1} kt"
        );
    }

    /// <summary>
    /// The connector slowdown is gated on <c>ShortConnectorMaxLenFt</c> (250 ft): above it a genuine straight segment
    /// exists and the normal accelerate-then-brake profile is correct, so "the length window alone never forces a
    /// slowdown". This is the negative case for <see cref="ShortConnector_HoldsSteadyLowSpeed_NoSurge"/>, whose 200 ft
    /// connector sits inside the window and therefore cannot detect a missing length gate. Same bracketing 90° corners,
    /// a 1500 ft straight between them — the aircraft must accelerate rather than crawl the whole leg at corner speed.
    /// </summary>
    [Fact]
    public void LongStraightBetweenSharpCorners_IsNotTreatedAsShortConnector()
    {
        const double LongStraightFt = 1500.0;

        var n0 = MakeNode(1, 37.0, -122.0);
        var (l1, o1) = GeoMath.ProjectPoint(n0.Position, new TrueHeading(180.0), 120.0 / GeoMath.FeetPerNm);
        var n1 = MakeNode(2, l1, o1);
        var (l2, o2) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), LongStraightFt / GeoMath.FeetPerNm);
        var n2 = MakeNode(3, l2, o2);
        var (l3, o3) = GeoMath.ProjectPoint(n2.Position, new TrueHeading(180.0), 200.0 / GeoMath.FeetPerNm);
        var n3 = MakeNode(4, l3, o3);

        var route = new TaxiRoute
        {
            Segments = [MakeStraightSegment(n0, n1, "A"), MakeStraightSegment(n1, n2, "F1"), MakeStraightSegment(n2, n3, "B")],
            HoldShortPoints = [],
        };

        var (aircraft, ctx) = MakeFixture(n0.Position, acHeadingDeg: 180.0, startSpeedKts: 10.0);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        double peakOnLongStraight = 0.0;
        int straightTicks = 0;
        for (int tick = 0; tick < 4000; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            bool last = route.CurrentSegmentIndex == route.Segments.Count - 1;
            var result = nav.Tick(ctx, last, _ => true);

            bool alignedToStraight = new TrueHeading(aircraft.TrueHeading.Degrees).AbsAngleTo(new TrueHeading(90.0)) < 20.0;
            if (route.CurrentSegmentIndex == 1 && alignedToStraight)
            {
                peakOnLongStraight = Math.Max(peakOnLongStraight, aircraft.IndicatedAirspeed);
                straightTicks++;
            }

            if (result == NavigatorResult.ArrivedAtNode)
            {
                if (last)
                {
                    break;
                }

                route.CurrentSegmentIndex++;
                nav.SetupSegment(route, ctx, _ => true);
            }
        }

        _out.WriteLine($"LongStraight: straightTicks={straightTicks} peakOnLongStraight={peakOnLongStraight:F1}kt");

        Assert.True(straightTicks > 0, "aircraft never traversed the long straight aligned to its centerline");
        Assert.True(
            peakOnLongStraight > 10.0,
            $"a {LongStraightFt:F0} ft straight is past the {250} ft connector window, so the aircraft should accelerate "
                + $"along it instead of holding corner speed; peaked at only {peakOnLongStraight:F1} kt"
        );
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

    // ---- Free-space first leg (TaxiApproachLeg): the alignment arc aims AT the node ----

    /// <summary>The OAK gate-15 pose after a plain PUSH.</summary>
    private const double PushedLat = 37.710217680439534;

    private const double PushedLon = -122.21728593336832;

    private const double PushedHeadingDeg = 53.0;

    /// <summary>Node 763 — the node the TAXI route starts at, ~105 ft behind the pushed aircraft.</summary>
    private const int StartNodeId = 763;

    private const double StartNodeLat = 37.70999962916134;

    private const double StartNodeLon = -122.21752272724868;

    /// <summary>The point a slow-turn rolls out at: its centre projected at the final centre-bearing by the radius.</summary>
    private static LatLon ExitPoint(PathPrimitiveSlowTurn turn)
    {
        double finalCenterBearingDeg = turn.StartBearingFromCenterDeg + (turn.RightTurn ? turn.SweepDeg : -turn.SweepDeg);
        return GeoMath.ProjectPoint(
            new LatLon(turn.CenterLat, turn.CenterLon),
            new TrueHeading(finalCenterBearingDeg),
            turn.RadiusFt / GeoMath.FeetPerNm
        );
    }

    /// <summary>
    /// A route whose first segment is a free-space leg from the aircraft's own position (a virtual node) has no
    /// painted centerline to exit onto, so the entry-alignment arc must roll out on a line THROUGH the leg's
    /// to-node rather than merely on the leg's bearing — a bearing-aimed arc off a 105 ft leg finishes tens of
    /// feet abeam and pure pursuit has to re-acquire from there.
    /// </summary>
    [Fact]
    public void SetupSegment_FreeSpaceFirstLeg_AlignmentArcExitsOnTheLineThroughTheNode()
    {
        var startNode = MakeNode(StartNodeId, StartNodeLat, StartNodeLon);
        var route = new TaxiRoute
        {
            Segments = [VirtualNode.CreateSegment(VirtualNode.Create(PushedLat, PushedLon), startNode, "RAMP")],
            HoldShortPoints = [],
        };

        var (_, ctx) = MakeFixture(new LatLon(PushedLat, PushedLon), PushedHeadingDeg);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        var turn = Assert.IsType<PathPrimitiveSlowTurn>(nav.CurrentPrimitive);

        var exit = ExitPoint(turn);
        var target = new LatLon(StartNodeLat, StartNodeLon);
        double distFt = GeoMath.DistanceNm(exit, target) * GeoMath.FeetPerNm;
        double deltaRad = GeoMath.SignedBearingDifference(turn.ExitTangentBearingDeg, GeoMath.BearingTo(exit, target)) * Math.PI / 180.0;
        double abeamFt = distFt * Math.Sin(deltaRad);
        double alongFt = distFt * Math.Cos(deltaRad);

        _out.WriteLine(
            $"alignment arc: r={turn.RadiusFt:F0}ft sweep={turn.SweepDeg:F1}° right={turn.RightTurn} "
                + $"exitHdg={turn.ExitTangentBearingDeg:F1}°; node {StartNodeId} is {abeamFt:F1} ft abeam, {alongFt:F1} ft along"
        );

        // aim=node: the point aim turns at the comfortable nose-wheel radius, the bearing-aimed fallback at the
        // adaptive radius, which this pose tightens to the 15 ft floor — so the radius identifies which fired.
        Assert.Equal(CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet), turn.RadiusFt, 3);
        Assert.True(alongFt > 0, $"node {StartNodeId} must lie ahead of the arc's roll-out point; it is {alongFt:F1} ft along");
        Assert.True(
            Math.Abs(abeamFt) <= 0.5,
            $"the alignment arc rolls out {abeamFt:F1} ft abeam the line to node {StartNodeId} — a free-space leg's arc must exit "
                + "on the line through its node"
        );
    }

    // ---- Arc entry from off the curve: the playback must DRIVE there, never write the aircraft there ----

    /// <summary>OAK node 763 — the ramp node whose fillet arc the entry-offset tests play back.</summary>
    private const int FilletFromNodeId = 763;

    /// <summary>The preferred far end of that fillet ("T - RAMP").</summary>
    private const int FilletToNodeId = 762;

    /// <summary>Lateral offset (ft) from the fillet's start point the aircraft begins at.</summary>
    private const double EntryOffsetFt = 12.0;

    /// <summary>Entry speed and navigator speed ceiling (kt) for the entry-offset drives — a fillet crawl.</summary>
    private const double EntrySpeedKts = 3.0;

    /// <summary>Feet covered per second at one knot.</summary>
    private const double FtPerSecPerKt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>Slack (ft) allowed on top of v·dt for a single sub-tick's displacement.</summary>
    private const double StepToleranceFt = 0.5;

    /// <summary>How far short of the curve's start point, along its entry tangent, the lead-in fixture starts.</summary>
    private const double LeadInShortfallFt = 17.0;

    /// <summary>The cross-track part of that fixture's displacement — a genuine entry offset, blended off as usual.</summary>
    private const double LeadInCrossTrackFt = 3.0;

    /// <summary>
    /// Entry speed (kt) for the lead-in fixture — the regime the shortfall was found in (SFO KLM605 covered
    /// 6.3 ft per sub-tick). A 17 ft shortfall bled off over <c>ArcEntryBlendFt</c> adds offset·ds/50 to each
    /// sub-tick, which only clears <see cref="StepToleranceFt"/> once the aircraft is driving faster than a
    /// crawl: 1.4 ft at 10 kt, 0.4 ft at 3 kt.
    /// </summary>
    private const double LeadInEntrySpeedKts = 10.0;

    /// <summary>
    /// A lateral offset (ft) far beyond the entry blend: bleeding it off over 50 ft of travel moves the
    /// aircraft sideways several times faster than it drives forward, which is exactly the write the
    /// no-teleport guard exists to surface.
    /// </summary>
    private const double FarOffsetFt = 200.0;

    /// <summary>
    /// Lateral offset (ft) for the rate-capped blend drive: far enough that bleeding it off over the flat
    /// <c>ArcEntryBlendFt</c> floor alone steps the aircraft sideways further than the no-teleport guard's
    /// slack allows at <see cref="RateCapEntrySpeedKts"/>, and well inside
    /// <see cref="GroundNavigator.MaxArcEntryOffsetFt"/> so the entry is blended rather than refused.
    /// </summary>
    private const double RateCapOffsetFt = 20.0;

    /// <summary>Entry speed (kt) for the rate-capped blend drive — an ordinary taxi speed, ~6.3 ft of travel per sub-tick.</summary>
    private const double RateCapEntrySpeedKts = 15.0;

    /// <summary>
    /// Slack (ft) the navigator's own no-teleport guard allows on one sub-tick's write (its
    /// <c>TeleportToleranceFt</c>). The blend must stay inside it at taxi speed, not merely avoid throwing.
    /// </summary>
    private const double GuardToleranceFt = 2.0;

    /// <summary>Distance (ft) the direct no-teleport-guard unit writes the aircraft.</summary>
    private const double GuardJumpFt = 40.0;

    /// <summary>Distance (ft) the integrator claims to have advanced for that write — far less than the jump.</summary>
    private const double GuardJumpTravelFt = 2.0;

    /// <summary>
    /// Curve parameter the part-way-along-the-curve test starts the aircraft at. A quarter of the way in,
    /// not half: the fixture fillet turns 100°, so the tangent at its midpoint is ~50° off the segment's
    /// departure bearing and <see cref="GroundNavigator.EntryAlignmentThresholdDeg"/> would install an
    /// alignment slow-turn instead of the Bézier under test. A quarter in, the pose is still well past the
    /// at-node tolerance from the curve's start point and the Bézier becomes current directly.
    /// </summary>
    private const double PartWayAlongT = 0.25;

    /// <summary>
    /// The real OAK fillet arc at <see cref="FilletFromNodeId"/> as a one-segment route traversed from that
    /// node (preferring the arc to <see cref="FilletToNodeId"/>). Null when the OAK layout is unavailable —
    /// the suite's silent-skip convention for missing test data.
    /// </summary>
    private (GroundArc Arc, GroundNode From, GroundNode To, TaxiRoute Route)? OakFilletArcRoute()
    {
        var layout = new Helpers.TestAirportGroundData().GetLayout("OAK");
        if ((layout is null) || !layout.Nodes.TryGetValue(FilletFromNodeId, out var from))
        {
            return null;
        }

        var arc =
            layout.Arcs.FirstOrDefault(a => a.HasNode(FilletFromNodeId) && a.HasNode(FilletToNodeId))
            ?? layout.Arcs.Where(a => a.HasNode(FilletFromNodeId)).OrderBy(a => a.OtherNodeId(FilletFromNodeId)).FirstOrDefault();
        Assert.True(arc is not null, $"OAK node {FilletFromNodeId} carries no fillet arc to play back");

        var to = arc!.OtherNode(from);
        var directed = new DirectionalEdge
        {
            Edge = arc,
            FromNode = from,
            ToNode = to,
        };
        var route = new TaxiRoute { Segments = [new TaxiRouteSegment { Edge = directed, TaxiwayName = arc.TaxiwayName }], HoldShortPoints = [] };

        _out.WriteLine(
            $"OAK fillet {from.Id}->{to.Id} [{string.Join(" - ", arc.TaxiwayNames)}]: len={arc.DistanceNm * GeoMath.FeetPerNm:F1}ft "
                + $"minR={arc.MinRadiusOfCurvatureFt:F1}ft depBrg={directed.DepartureBearing:F1} arrBrg={directed.ArrivalBearing:F1} "
                + $"turn={GeoMath.AbsBearingDifference(directed.DepartureBearing, directed.ArrivalBearing):F1}"
        );

        return (arc, from, to, route);
    }

    /// <summary>
    /// A Bézier fillet whose playback begins with the aircraft off the curve's start point must bleed that
    /// offset off while driving the curve, never write it away in one sub-tick. Closed-form playback writes
    /// position absolutely (invariant I2), so any residual cross-track at a fillet entry — or a snapshot
    /// restore taken mid-arc — used to be applied whole on the first tick: a 12 ft sideways teleport onto the
    /// painted line at a 3 kt crawl.
    /// </summary>
    [Fact]
    public void BezierEntry_OffStartPoint_DoesNotTeleport()
    {
        var fixture = OakFilletArcRoute();
        if (fixture is null)
        {
            return;
        }

        var (_, from, to, route) = fixture.Value;
        double departureBrg = route.Segments[0].Edge.DepartureBearing;
        var start = GeoMath.ProjectPoint(from.Position, new TrueHeading(departureBrg + 90.0), EntryOffsetFt / GeoMath.FeetPerNm);

        var (aircraft, ctx) = MakeFixture(start, departureBrg, startSpeedKts: EntrySpeedKts);
        var nav = new GroundNavigator { MaxSpeedKts = EntrySpeedKts };
        nav.SetupSegment(route, ctx, _ => true);
        Assert.IsType<PathPrimitiveBezier>(nav.CurrentPrimitive);

        double maxStepFt = 0.0;
        double worstExcessFt = double.MinValue;
        int worstTick = -1;
        int ticks = 0;
        bool arrived = false;
        for (int tick = 0; (tick < 400) && !arrived; tick++)
        {
            // Physics integrates position as well, so the displacement under test is the navigator's own
            // write: measure from the position it starts the tick at — what the no-teleport guard checks.
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var before = aircraft.Position;
            double iasKts = aircraft.IndicatedAirspeed;
            arrived = nav.Tick(ctx, isLastSegment: true, _ => true) == NavigatorResult.ArrivedAtNode;

            double stepFt = GeoMath.DistanceNm(before, aircraft.Position) * GeoMath.FeetPerNm;
            double excessFt = stepFt - ((iasKts * FtPerSecPerKt * ctx.DeltaSeconds) + StepToleranceFt);
            maxStepFt = Math.Max(maxStepFt, stepFt);
            if (excessFt > worstExcessFt)
            {
                worstExcessFt = excessFt;
                worstTick = tick;
            }

            ticks = tick + 1;
        }

        double endDistFt = GeoMath.DistanceNm(aircraft.Position, to.Position) * GeoMath.FeetPerNm;
        _out.WriteLine(
            $"BezierEntry: ticks={ticks} arrived={arrived} maxStep={maxStepFt:F2}ft worstExcess={worstExcessFt:F2}ft @tick{worstTick} "
                + $"endDistToNode{to.Id}={endDistFt:F2}ft"
        );

        Assert.True(
            worstExcessFt <= 0.0,
            $"sub-tick {worstTick} moved {worstExcessFt:F2} ft further than the aircraft drove (max step {maxStepFt:F2} ft at "
                + $"~{EntrySpeedKts:F1} kt) — the arc entry offset was written away instead of bled off"
        );
        Assert.True(arrived, $"the fillet never completed within {ticks} sub-ticks");
        Assert.True(endDistFt <= 1.0, $"playback ended {endDistFt:F2} ft from node {to.Id} (the curve's P3)");
    }

    /// <summary>
    /// A Bézier primitive that becomes current while the aircraft already stands part-way along the curve —
    /// the snapshot-restore case, where <c>SetupSegment</c> rebuilds the primitive from the route's segment
    /// index — must start its progress from where the aircraft is, not from t = 0. Restarting at the from-node
    /// rewinds the aircraft to the curve's start point on the first tick.
    /// </summary>
    [Fact]
    public void BezierStart_OffP0_InitialisesProgressFromPosition()
    {
        var fixture = OakFilletArcRoute();
        if (fixture is null)
        {
            return;
        }

        var (_, _, to, route) = fixture.Value;
        var prim = Assert.IsType<PathPrimitiveBezier>(PathPrimitiveBuilder.FromSegment(route.Segments[0]));
        var (startLat, startLon) = prim.Curve.Evaluate(PartWayAlongT);
        double startTangent = prim.Curve.TangentBearing(PartWayAlongT);
        var curveStart = new LatLon(prim.Curve.P0Lat, prim.Curve.P0Lon);
        double fromP0Ft = GeoMath.DistanceNm(new LatLon(startLat, startLon), curveStart) * GeoMath.FeetPerNm;

        var (aircraft, ctx) = MakeFixture(new LatLon(startLat, startLon), startTangent, startSpeedKts: EntrySpeedKts);
        var nav = new GroundNavigator { MaxSpeedKts = EntrySpeedKts };
        nav.SetupSegment(route, ctx, _ => true);
        Assert.IsType<PathPrimitiveBezier>(nav.CurrentPrimitive);
        Assert.True(
            fromP0Ft > AirportGroundLayout.AtNodeToleranceFt,
            $"the fixture must start the aircraft clear of the curve's start point; it is only {fromP0Ft:F1} ft from it"
        );

        double beforeFt = GeoMath.DistanceNm(aircraft.Position, to.Position) * GeoMath.FeetPerNm;
        FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
        nav.Tick(ctx, isLastSegment: true, _ => true);
        double afterFt = GeoMath.DistanceNm(aircraft.Position, to.Position) * GeoMath.FeetPerNm;

        _out.WriteLine(
            $"BezierStart at t={PartWayAlongT:F2} ({fromP0Ft:F1}ft past P0, tangent {startTangent:F1}): "
                + $"distToNode{to.Id} {beforeFt:F2}ft -> {afterFt:F2}ft"
        );

        Assert.True(
            afterFt < beforeFt,
            $"the first tick moved the aircraft from {beforeFt:F2} ft to {afterFt:F2} ft from node {to.Id} — playback rewound it "
                + "toward the curve's start point instead of resuming from where it stands"
        );
    }

    /// <summary>
    /// Signed along-track distance (ft) from <paramref name="position"/> to <paramref name="target"/> measured
    /// along <paramref name="tangentDeg"/>: positive while the target is still ahead, negative once past it.
    /// </summary>
    private static double AlongTrackToFt(LatLon position, LatLon target, double tangentDeg)
    {
        double distFt = GeoMath.DistanceNm(position, target) * GeoMath.FeetPerNm;
        double deltaDeg = GeoMath.SignedBearingDifference(GeoMath.BearingTo(position, target), tangentDeg);
        return distFt * Math.Cos(deltaDeg * Math.PI / 180.0);
    }

    /// <summary>
    /// An aircraft that reaches a fillet still short of the curve's start point has not yet driven that
    /// distance, so the playback must drive it: roll up the entry tangent to the start point first, and only
    /// then advance the curve. Blending the shortfall away like a cross-track offset instead hands the
    /// aircraft free ground speed — it covers more distance per sub-tick than it drove (the SFO KLM605
    /// hand-off: 17 ft short of a mid-route fillet, closed at 2.1 ft per sub-tick on top of its own 6.3 ft).
    /// The cross-track part of the same displacement is a real entry offset and is still blended off.
    /// </summary>
    [Fact]
    public void BezierEntry_ShortOfStartPoint_DrivesTheLeadInBeforeTheCurve()
    {
        var fixture = OakFilletArcRoute();
        if (fixture is null)
        {
            return;
        }

        var (_, _, to, route) = fixture.Value;
        var prim = Assert.IsType<PathPrimitiveBezier>(PathPrimitiveBuilder.FromSegment(route.Segments[0]));
        var curveStart = new LatLon(prim.Curve.P0Lat, prim.Curve.P0Lon);
        double entryTangent = prim.Curve.TangentBearing(0.0);

        var behind = GeoMath.ProjectPoint(curveStart, new TrueHeading((entryTangent + 180.0) % 360.0), LeadInShortfallFt / GeoMath.FeetPerNm);
        var start = GeoMath.ProjectPoint(behind, new TrueHeading((entryTangent + 90.0) % 360.0), LeadInCrossTrackFt / GeoMath.FeetPerNm);

        var (aircraft, ctx) = MakeFixture(start, entryTangent, startSpeedKts: LeadInEntrySpeedKts);
        var nav = new GroundNavigator { MaxSpeedKts = LeadInEntrySpeedKts };
        nav.SetupSegment(route, ctx, _ => true);
        Assert.IsType<PathPrimitiveBezier>(nav.CurrentPrimitive);

        double worstExcessFt = double.MinValue;
        int worstTick = -1;
        double maxStepFt = 0.0;
        double closestToStartFt = double.MaxValue;
        int ticks = 0;
        bool arrived = false;
        for (int tick = 0; (tick < 400) && !arrived; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var before = aircraft.Position;
            double iasKts = aircraft.IndicatedAirspeed;
            arrived = nav.Tick(ctx, isLastSegment: true, _ => true) == NavigatorResult.ArrivedAtNode;

            double stepFt = GeoMath.DistanceNm(before, aircraft.Position) * GeoMath.FeetPerNm;
            double excessFt = stepFt - ((iasKts * FtPerSecPerKt * ctx.DeltaSeconds) + StepToleranceFt);
            maxStepFt = Math.Max(maxStepFt, stepFt);
            if (excessFt > worstExcessFt)
            {
                worstExcessFt = excessFt;
                worstTick = tick;
            }

            closestToStartFt = Math.Min(closestToStartFt, Math.Abs(AlongTrackToFt(aircraft.Position, curveStart, entryTangent)));
            ticks = tick + 1;
        }

        double endDistFt = GeoMath.DistanceNm(aircraft.Position, to.Position) * GeoMath.FeetPerNm;
        _out.WriteLine(
            $"LeadIn: ticks={ticks} arrived={arrived} worstExcess={worstExcessFt:F2}ft @tick{worstTick} maxStep={maxStepFt:F2}ft "
                + $"closestAlongTrackToP0={closestToStartFt:F2}ft endDistToNode{to.Id}={endDistFt:F2}ft"
        );

        Assert.True(
            worstExcessFt <= 0.0,
            $"sub-tick {worstTick} moved {worstExcessFt:F2} ft further than the aircraft drove — the along-track shortfall was blended "
                + "away instead of driven"
        );

        // Position is sampled once per sub-tick, so the aircraft can only be observed within one step of the
        // curve's start point, never exactly on it.
        Assert.True(
            closestToStartFt <= maxStepFt,
            $"the aircraft never reached the curve's start point; closest along-track {closestToStartFt:F2} ft, one sub-tick is {maxStepFt:F2} ft"
        );
        Assert.True(arrived, $"the fillet never completed within {ticks} sub-ticks");
        Assert.True(endDistFt <= 1.0, $"playback ended {endDistFt:F2} ft from node {to.Id} (the curve's P3)");
    }

    /// <summary>
    /// An entry offset past <see cref="GroundNavigator.MaxArcEntryOffsetFt"/> is refused at capture rather
    /// than blended off: an aircraft that far from the curve it was told to play is not merely off the
    /// painted line — the route started a curve the aircraft is not on, and blending 200 ft away over any
    /// distance drives it sideways across ground it never taxied. <c>ModuleInit</c> arms
    /// <see cref="GroundNavigator.ThrowOnTeleport"/> for the whole assembly; the shipping app leaves it
    /// false, logs the refusal and blends anyway rather than crashing a live session.
    /// </summary>
    [Fact]
    public void ArcEntry_BeyondMaxOffset_Refused()
    {
        var fixture = OakFilletArcRoute();
        if (fixture is null)
        {
            return;
        }

        var (_, from, _, route) = fixture.Value;
        double departureBrg = route.Segments[0].Edge.DepartureBearing;
        var start = GeoMath.ProjectPoint(from.Position, new TrueHeading(departureBrg + 90.0), FarOffsetFt / GeoMath.FeetPerNm);

        var (aircraft, ctx) = MakeFixture(start, departureBrg, startSpeedKts: EntrySpeedKts);
        var nav = new GroundNavigator { MaxSpeedKts = EntrySpeedKts };
        nav.SetupSegment(route, ctx, _ => true);
        FlightPhysics.Update(aircraft, ctx.DeltaSeconds);

        var ex = Assert.Throws<InvalidOperationException>(() => nav.Tick(ctx, isLastSegment: true, _ => true));

        _out.WriteLine(ex.Message);
        Assert.Contains("arc entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A 20 ft arc-entry offset carried into a fillet at an ordinary 15 kt taxi must be blended off while the
    /// aircraft drives the curve, with every write staying inside the no-teleport guard's slack. Bleeding the
    /// offset over a flat 50 ft of travel moved the aircraft offset·ds/50 sideways per sub-tick — 2.5 ft at
    /// 15 kt, past the guard's 2 ft — so the guard fired at an undocumented, speed-dependent offset (~16 ft at
    /// this speed) on an aircraft that was only a wingspan off the painted line, and the implied track diverged
    /// 11° from the heading the playback wrote. The blend distance now stretches with the offset so that
    /// divergence stays within <c>MaxBlendTrackDeg</c>.
    /// </summary>
    [Fact]
    public void BezierEntry_20ftOffAt15kt_BlendsWithoutTrippingTheGuard()
    {
        var fixture = OakFilletArcRoute();
        if (fixture is null)
        {
            return;
        }

        var (_, from, to, route) = fixture.Value;
        double departureBrg = route.Segments[0].Edge.DepartureBearing;
        var start = GeoMath.ProjectPoint(from.Position, new TrueHeading(departureBrg + 90.0), RateCapOffsetFt / GeoMath.FeetPerNm);

        var (aircraft, ctx) = MakeFixture(start, departureBrg, startSpeedKts: RateCapEntrySpeedKts);
        var nav = new GroundNavigator { MaxSpeedKts = RateCapEntrySpeedKts };
        nav.SetupSegment(route, ctx, _ => true);
        Assert.IsType<PathPrimitiveBezier>(nav.CurrentPrimitive);

        double maxStepFt = 0.0;
        double worstExcessFt = double.MinValue;
        int worstTick = -1;
        int ticks = 0;
        bool arrived = false;
        for (int tick = 0; (tick < 400) && !arrived; tick++)
        {
            // The displacement under test is the navigator's own write, so measure from the position it
            // starts the sub-tick at — exactly what the no-teleport guard compares against v·dt.
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var before = aircraft.Position;
            double iasKts = aircraft.IndicatedAirspeed;
            arrived = nav.Tick(ctx, isLastSegment: true, _ => true) == NavigatorResult.ArrivedAtNode;

            double stepFt = GeoMath.DistanceNm(before, aircraft.Position) * GeoMath.FeetPerNm;
            double excessFt = stepFt - ((iasKts * FtPerSecPerKt * ctx.DeltaSeconds) + GuardToleranceFt);
            maxStepFt = Math.Max(maxStepFt, stepFt);
            if (excessFt > worstExcessFt)
            {
                worstExcessFt = excessFt;
                worstTick = tick;
            }

            ticks = tick + 1;
        }

        double endDistFt = GeoMath.DistanceNm(aircraft.Position, to.Position) * GeoMath.FeetPerNm;
        _out.WriteLine(
            $"BezierEntry20ft: ticks={ticks} arrived={arrived} maxStep={maxStepFt:F2}ft worstExcess={worstExcessFt:F2}ft @tick{worstTick} "
                + $"endDistToNode{to.Id}={endDistFt:F2}ft"
        );

        Assert.True(
            worstExcessFt <= 0.0,
            $"sub-tick {worstTick} wrote the aircraft {worstExcessFt:F2} ft further than it drove (max step {maxStepFt:F2} ft at "
                + $"~{RateCapEntrySpeedKts:F1} kt) — a {RateCapOffsetFt:F0} ft entry offset was bled off faster than the aircraft taxis"
        );
        Assert.True(arrived, $"the fillet never completed within {ticks} sub-ticks");
    }

    /// <summary>
    /// The no-teleport guard as a unit: an arc write further than the integrator advanced is a position the
    /// aircraft did not drive to, and it throws with the teleport message under
    /// <see cref="GroundNavigator.ThrowOnTeleport"/>. Distinct from the arc-entry refusal, which catches the
    /// same class of failure one step earlier — at capture, before any position is written.
    /// </summary>
    [Fact]
    public void CheckNoTeleport_WriteBeyondTheTravel_Throws()
    {
        var before = new LatLon(StartNodeLat, StartNodeLon);
        var after = GeoMath.ProjectPoint(before, new TrueHeading(90.0), GuardJumpFt / GeoMath.FeetPerNm);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GroundNavigator.CheckNoTeleport("NAV", before, after, GuardJumpTravelFt, PathPrimitiveKind.Bezier)
        );

        _out.WriteLine(ex.Message);
        Assert.Contains("teleport", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The runway-crossing speed floor never outruns the cornering cap of the primitive being played ----

    /// <summary>The jet runway-crossing floor a <see cref="Phases.Ground.CrossingRunwayPhase"/> installs on its navigator.</summary>
    private static readonly double CrossingFloorKts = CategoryPerformance.RunwayCrossingSpeed(AircraftCategory.Jet);

    /// <summary>Floating-point slack (kt) on a published target compared against a cap computed the same way.</summary>
    private const double SpeedCapToleranceKts = 0.01;

    /// <summary>A speed ceiling well below the crossing floor, so only the floor can set the target on a straight.</summary>
    private const double BelowFloorCeilingKts = 5.0;

    /// <summary>Length (ft) of the straight the floor test drives, long enough that no braking curve binds at its start.</summary>
    private const double FloorStraightFt = 600.0;

    /// <summary>
    /// A runway crossing keeps <see cref="GroundNavigator.MinSpeedKts"/> as a no-stop floor, but the floor is not
    /// licence to corner: the tail-clearance extension routinely takes a whole fillet, and a 15 kt jet floor around
    /// the OAK 763→762 corner (49 ft minimum radius) is more than twice the lateral-acceleration budget the arc's
    /// speed profile allows anywhere along it. Two bounds, both of which the floor broke: the published target
    /// never exceeds the fastest cornering speed the curve permits at any point, and it does come down to the
    /// tightest point's own cap (<see cref="GroundArc.MaxSafeSpeedKts"/>) while the aircraft is on the curve. The
    /// cap is local rather than the arc's minimum throughout, so a fillet flattening out at its exit is allowed to
    /// accelerate again — which is why the peak is measured against the profile rather than the minimum.
    /// </summary>
    [Fact]
    public void CrossingFloor_OnAFillet_NeverExceedsTheArcsSafeSpeed()
    {
        var fixture = OakFilletArcRoute();
        if (fixture is null)
        {
            return;
        }

        var (arc, from, _, route) = fixture.Value;
        double departureBrg = route.Segments[0].Edge.DepartureBearing;
        var (aircraft, ctx) = MakeFixture(from.Position, departureBrg, startSpeedKts: CrossingFloorKts);
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(AircraftCategory.Jet), MinSpeedKts = CrossingFloorKts };
        nav.SetupSegment(route, ctx, _ => true);
        Assert.IsType<PathPrimitiveBezier>(nav.CurrentPrimitive);

        double tightestCapKts = arc.MaxSafeSpeedKts(AircraftCategory.Jet);
        double profileMaxKts = arc.SpeedProfile(AircraftCategory.Jet).Max(sample => sample.SpeedKts);
        double peakKts = 0.0;
        double troughKts = double.MaxValue;
        int peakTick = -1;
        int ticks = 0;
        bool arrived = false;
        for (int tick = 0; (tick < 400) && !arrived; tick++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            arrived = nav.Tick(ctx, isLastSegment: true, _ => true) == NavigatorResult.ArrivedAtNode;
            double publishedKts = ctx.Targets.TargetSpeed ?? 0.0;
            if (publishedKts > peakKts)
            {
                peakKts = publishedKts;
                peakTick = tick;
            }
            troughKts = Math.Min(troughKts, publishedKts);
            ticks = tick + 1;
        }

        _out.WriteLine(
            $"fillet minR={arc.MinRadiusOfCurvatureFt:F1}ft tightestCap={tightestCapKts:F2}kt profileMax={profileMaxKts:F2}kt "
                + $"floor={CrossingFloorKts:F0}kt: published peak {peakKts:F2}kt at tick {peakTick}, trough {troughKts:F2}kt "
                + $"over {ticks} ticks, arrived={arrived}"
        );

        Assert.True(arrived, $"the fillet never completed within {ticks} ticks");
        Assert.True(
            peakKts <= profileMaxKts + SpeedCapToleranceKts,
            $"tick {peakTick} published {peakKts:F2} kt around a fillet whose cornering speed never exceeds "
                + $"{profileMaxKts:F2} kt — the {CrossingFloorKts:F0} kt crossing floor overrode the arc-speed cap"
        );
        Assert.True(
            troughKts <= tightestCapKts + SpeedCapToleranceKts,
            $"the published target never came below {troughKts:F2} kt on a fillet whose tightest point allows "
                + $"{tightestCapKts:F2} kt — the {CrossingFloorKts:F0} kt crossing floor held it above the corner"
        );
    }

    /// <summary>
    /// The floor still does its job where there is no curve to cap it: on a straight the crossing floor lifts a
    /// target below it, so an aircraft never brakes toward a stop on the runway.
    /// </summary>
    [Fact]
    public void CrossingFloor_OnAStraight_StillLiftsTheTarget()
    {
        var n0 = MakeNode(1, 37.0, -122.0);
        var (l1, o1) = GeoMath.ProjectPoint(n0.Position, new TrueHeading(180.0), FloorStraightFt / GeoMath.FeetPerNm);
        var n1 = MakeNode(2, l1, o1);

        var route = new TaxiRoute { Segments = [MakeStraightSegment(n0, n1, "A")], HoldShortPoints = [] };
        var (aircraft, ctx) = MakeFixture(n0.Position, acHeadingDeg: 180.0, startSpeedKts: CrossingFloorKts);
        var nav = new GroundNavigator { MaxSpeedKts = BelowFloorCeilingKts, MinSpeedKts = CrossingFloorKts };
        nav.SetupSegment(route, ctx, _ => true);

        FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
        nav.Tick(ctx, isLastSegment: false, _ => true);

        double publishedKts = ctx.Targets.TargetSpeed ?? 0.0;
        _out.WriteLine($"straight: ceiling {BelowFloorCeilingKts:F0}kt, floor {CrossingFloorKts:F0}kt, published {publishedKts:F2}kt");
        Assert.Equal(CrossingFloorKts, publishedKts, 3);
    }
}
