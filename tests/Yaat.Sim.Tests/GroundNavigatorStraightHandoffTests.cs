using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// A fillet or slow-turn primitive mirrors its tangent into the physics target heading so physics does not
/// fight the closed-form playback. When a straight takes over, the navigator steers by writing the heading
/// directly, so that target must be released: left in place, physics turns the nose back toward the stale
/// tangent every substep, the navigator nudges it out again, the aircraft drifts off a straight that leaves
/// the fillet at a fraction of a degree, and the orbit guard — which sees only the navigator's half of the
/// tug-of-war — declares a full circle on an aircraft that never turned (OAK 30-departure scenario, a
/// queued crawl toward the W/W1 junction).
/// </summary>
public class GroundNavigatorStraightHandoffTests
{
    private static GroundNode Node(int id, LatLon position) =>
        new()
        {
            Id = id,
            Position = position,
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture(LatLon position, double headingDeg, double speedKts)
    {
        var aircraft = new AircraftState
        {
            Callsign = "NAVHND",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            IndicatedAirspeed = speedKts,
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

    /// <summary>A 90° fillet (r ≈ 75 ft) followed by a 1,500 ft straight that leaves the fillet 0.3° off its exit tangent.</summary>
    private static (TaxiRoute Route, GroundNode Exit, double EntryBearing) FilletThenLongStraight()
    {
        var p0 = new LatLon(37.700, -122.200);
        double radiusNm = 75.0 / GeoMath.FeetPerNm;
        const double kappa = 0.5523;
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0, new TrueHeading(90.0), kappa * radiusNm);
        var (cornerLat, cornerLon) = GeoMath.ProjectPoint(p0, new TrueHeading(90.0), radiusNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(new LatLon(cornerLat, cornerLon), new TrueHeading(180.0), radiusNm);
        var p3 = new LatLon(p3Lat, p3Lon);
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3, new TrueHeading(0.0), kappa * radiusNm);
        var from = Node(1, p0);
        var to = Node(2, p3);
        var curve = new CubicBezier(p0.Lat, p0.Lon, p1Lat, p1Lon, p2Lat, p2Lon, p3.Lat, p3.Lon);
        var arc = new GroundArc
        {
            Nodes = [from, to],
            TaxiwayNames = ["W", "B"],
            DistanceNm = curve.ArcLengthNm(64),
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = curve.MinRadiusOfCurvatureFt(p0.Lat, 64),
            TurnAngleDeg = 90.0,
        };
        from.Edges.Add(arc);
        to.Edges.Add(arc);
        var arcSegment = new TaxiRouteSegment
        {
            Edge = new DirectionalEdge
            {
                Edge = arc,
                FromNode = from,
                ToNode = to,
            },
            TaxiwayName = "W - B",
        };

        double exitTangent = arcSegment.Edge.ArrivalBearing;
        var (exitLat, exitLon) = GeoMath.ProjectPoint(to.Position, new TrueHeading(exitTangent + 0.3), 1500.0 / GeoMath.FeetPerNm);
        var exit = Node(3, new LatLon(exitLat, exitLon));
        var straight = new GroundEdge
        {
            Nodes = [to, exit],
            TaxiwayName = "W",
            DistanceNm = GeoMath.DistanceNm(to.Position, exit.Position),
        };
        to.Edges.Add(straight);
        exit.Edges.Add(straight);
        var straightSegment = new TaxiRouteSegment
        {
            Edge = new DirectionalEdge
            {
                Edge = straight,
                FromNode = to,
                ToNode = exit,
            },
            TaxiwayName = "W",
        };
        var route = new TaxiRoute { Segments = [arcSegment, straightSegment], HoldShortPoints = [] };
        return (route, exit, arcSegment.Edge.DepartureBearing);
    }

    [Fact]
    public void Straight_AfterAFillet_OwnsTheHeadingAndReachesItsNodeAtACrawl()
    {
        var (route, exit, entryBearing) = FilletThenLongStraight();
        var (aircraft, ctx) = MakeFixture(route.Segments[0].Edge.FromNode.Position, entryBearing, speedKts: 5.0);
        var nav = new GroundNavigator { MaxSpeedKts = 5.0 };
        nav.SetupSegment(route, ctx, _ => true);

        bool onStraight = false;
        bool arrived = false;
        double maxCrossTrackFt = 0;
        int ticks = 0;
        for (; ticks < 2400 && !arrived; ticks++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var result = nav.Tick(ctx, isLastSegment: onStraight, _ => true);
            if (onStraight)
            {
                Assert.Null(aircraft.Targets.TargetTrueHeading);
                var straight = route.Segments[1].Edge;
                maxCrossTrackFt = Math.Max(
                    maxCrossTrackFt,
                    Math.Abs(
                        GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, straight.FromNode.Position, new TrueHeading(straight.DepartureBearing))
                    ) * GeoMath.FeetPerNm
                );
            }

            if (result != NavigatorResult.ArrivedAtNode)
            {
                continue;
            }

            if (onStraight)
            {
                arrived = true;
            }
            else
            {
                onStraight = true;
                route.CurrentSegmentIndex = 1;
                nav.SetupSegment(route, ctx, _ => true);
            }
        }

        Assert.True(arrived, $"navigator never reached the straight's node {exit.Id} in {ticks * ctx.DeltaSeconds:F0} s");
        Assert.True(
            maxCrossTrackFt < 5.0,
            $"the aircraft drifted {maxCrossTrackFt:F1} ft off the straight — physics is holding the fillet's stale exit tangent"
        );
    }
}
