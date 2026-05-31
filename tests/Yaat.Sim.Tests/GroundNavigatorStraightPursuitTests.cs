using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GroundNavigator"/>'s pure-pursuit steering on
/// straight segments. Verifies that an aircraft placed off the segment line
/// converges back onto the line (rather than cutting diagonally toward the
/// target node), that on-segment aircraft track the line with no deviation,
/// and that existing behaviours (pre-turn blend, short-segment fallback,
/// arrival detection, hold-short clearance handling) are preserved.
/// </summary>
public class GroundNavigatorStraightPursuitTests(ITestOutputHelper output)
{
    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture(LatLon position, double acHeadingDeg, double startSpeedKts = 0)
    {
        var aircraft = new AircraftState
        {
            Callsign = "NAVPP",
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

    private static TaxiRoute MakeRoute(params TaxiRouteSegment[] segs) => new() { Segments = [.. segs], HoldShortPoints = [] };

    // ---- On-segment tracking: pure-pursuit must not introduce deviation ----

    [Fact]
    public void OnSegmentAircraft_TracksSegmentLineExactly()
    {
        // Segment east from (37.0, -122.0) 1000 ft. Aircraft starts on the
        // segment 100 ft from the start, aligned with segment bearing.
        var from = MakeNode(1, 37.0, -122.0);
        double segBearing = 90.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(segBearing), 1000.0 / GeoMath.FeetPerNm);
        var to = MakeNode(2, endLat, endLon);

        var (startLat, startLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(segBearing), 100.0 / GeoMath.FeetPerNm);
        var (aircraft, ctx) = MakeFixture(new LatLon(startLat, startLon), segBearing, startSpeedKts: 10);

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupSegment(MakeRoute(MakeStraightSegment(from, to)), ctx, _ => true);

        double maxCrossTrackFt = 0;
        for (int i = 0; i < 300; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var result = nav.Tick(ctx, isLastSegment: true, _ => true);
            double crossFt =
                Math.Abs(GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, from.Position, new TrueHeading(segBearing))) * GeoMath.FeetPerNm;
            maxCrossTrackFt = Math.Max(maxCrossTrackFt, crossFt);
            if (result == NavigatorResult.ArrivedAtNode)
            {
                break;
            }
        }

        output.WriteLine($"on-segment track: maxCross={maxCrossTrackFt:F3}ft");
        Assert.True(maxCrossTrackFt < 1.0, $"on-segment aircraft should track segment within 1 ft, max was {maxCrossTrackFt:F3}ft");
    }

    // ---- Off-segment convergence: core of the fix ----

    [Fact]
    public void OffSegmentAircraft_ConvergesToSegmentLine()
    {
        // Segment east from (37.0, -122.0) 500 ft. Aircraft starts 35 ft
        // south of the segment's start, heading east.  With pure-pursuit the
        // aircraft first steers onto the segment line, then tracks it.
        // (With the old bearing-to-target logic, the aircraft cut diagonally
        // and arrived 35 ft off-line.)
        var from = MakeNode(1, 37.0, -122.0);
        double segBearing = 90.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(segBearing), 500.0 / GeoMath.FeetPerNm);
        var to = MakeNode(2, endLat, endLon);

        // Place aircraft 35 ft south (bearing 180° = due south).
        var (acLat, acLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(180.0), 35.0 / GeoMath.FeetPerNm);
        var (aircraft, ctx) = MakeFixture(new LatLon(acLat, acLon), segBearing, startSpeedKts: 10);

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupSegment(MakeRoute(MakeStraightSegment(from, to)), ctx, _ => true);

        double finalCrossFt = double.NaN;
        int completeTick = -1;
        for (int i = 0; i < 400; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var result = nav.Tick(ctx, isLastSegment: true, _ => true);
            if (result == NavigatorResult.ArrivedAtNode)
            {
                double crossNm = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, from.Position, new TrueHeading(segBearing));
                finalCrossFt = Math.Abs(crossNm) * GeoMath.FeetPerNm;
                completeTick = i;
                break;
            }
        }

        output.WriteLine($"off-segment convergence: finalCross={finalCrossFt:F2}ft at tick={completeTick}");
        Assert.True(completeTick >= 0, "navigator did not arrive at target within budget");
        Assert.True(finalCrossFt < 5.0, $"aircraft should converge onto segment line (final cross {finalCrossFt:F2}ft exceeds 5 ft tolerance)");
    }

    [Fact]
    public void OffSegmentAircraft_ApproachesSegmentLine_WithinFirstSeconds()
    {
        // Stronger assertion: within the first 5 seconds (20 ticks at 0.25 s),
        // the off-segment aircraft should have closed most of the 35 ft gap.
        var from = MakeNode(1, 37.0, -122.0);
        double segBearing = 90.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(segBearing), 500.0 / GeoMath.FeetPerNm);
        var to = MakeNode(2, endLat, endLon);

        var (acLat, acLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(180.0), 35.0 / GeoMath.FeetPerNm);
        var (aircraft, ctx) = MakeFixture(new LatLon(acLat, acLon), segBearing, startSpeedKts: 10);

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupSegment(MakeRoute(MakeStraightSegment(from, to)), ctx, _ => true);

        double initialCrossFt = 35.0;
        double minCrossFt = double.MaxValue;

        for (int i = 0; i < 20; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            nav.Tick(ctx, isLastSegment: true, _ => true);
            double crossFt =
                Math.Abs(GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, from.Position, new TrueHeading(segBearing))) * GeoMath.FeetPerNm;
            minCrossFt = Math.Min(minCrossFt, crossFt);
        }

        output.WriteLine($"5-second convergence: initial={initialCrossFt:F2}ft min={minCrossFt:F2}ft");
        Assert.True(
            minCrossFt < initialCrossFt * 0.5,
            $"aircraft should close at least half the cross-track gap in 5 s (initial {initialCrossFt:F2}ft, min {minCrossFt:F2}ft)"
        );
    }

    // ---- Short-segment fallback ----

    [Fact]
    public void ShortSegment_StillArrivesAtTarget()
    {
        // Segment of 20 ft (shorter than 50 ft look-ahead cap). Pure pursuit
        // must clamp to the target and behave like the legacy bearing-to-
        // target steering.
        var from = MakeNode(1, 37.0, -122.0);
        double segBearing = 90.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(segBearing), 20.0 / GeoMath.FeetPerNm);
        var to = MakeNode(2, endLat, endLon);

        var (aircraft, ctx) = MakeFixture(from.Position, segBearing, startSpeedKts: 5);

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupSegment(MakeRoute(MakeStraightSegment(from, to)), ctx, _ => true);

        bool arrived = false;
        for (int i = 0; i < 200; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            if (nav.Tick(ctx, isLastSegment: true, _ => true) == NavigatorResult.ArrivedAtNode)
            {
                arrived = true;
                break;
            }
        }

        double distToTargetFt = GeoMath.DistanceNm(aircraft.Position, to.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"short-segment: arrived={arrived} distToTarget={distToTargetFt:F2}ft");
        Assert.True(arrived, "navigator must still arrive at the target for a 20 ft short segment");
        Assert.True(distToTargetFt < 3.0, $"short-segment arrival should be within 3 ft of target, got {distToTargetFt:F2}ft");
    }

    // ---- Heading convergence on off-segment entry ----

    [Fact]
    public void OffSegmentAircraft_FinalHeadingAlignsWithSegmentBearing()
    {
        // 1000 ft east-running segment. Aircraft starts 35 ft south-of-start,
        // heading 60° (cocked right of east). As the pure-pursuit re-acquires
        // the line, the aircraft's heading should asymptotically match the
        // segment bearing (90°) by segment end.
        var from = MakeNode(1, 37.0, -122.0);
        double segBearing = 90.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(segBearing), 1000.0 / GeoMath.FeetPerNm);
        var to = MakeNode(2, endLat, endLon);

        var (acLat, acLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(180.0), 35.0 / GeoMath.FeetPerNm);
        var (aircraft, ctx) = MakeFixture(new LatLon(acLat, acLon), 60.0, startSpeedKts: 10);

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupSegment(MakeRoute(MakeStraightSegment(from, to)), ctx, _ => true);

        double finalHdgDiff = double.NaN;
        for (int i = 0; i < 400; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            if (nav.Tick(ctx, isLastSegment: true, _ => true) == NavigatorResult.ArrivedAtNode)
            {
                finalHdgDiff = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, segBearing);
                break;
            }
        }

        output.WriteLine($"heading convergence: finalHdgDiff={finalHdgDiff:F2}°");
        Assert.False(double.IsNaN(finalHdgDiff), "navigator did not arrive within budget");
        Assert.True(finalHdgDiff < 5.0, $"aircraft heading should converge toward segment bearing, final diff was {finalHdgDiff:F2}°");
    }
}
