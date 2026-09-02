using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// On a fillet arc the navigator holds the aircraft to the curve's <em>local</em> cornering speed, braking in
/// time for a tighter stretch further along it. A distorted cubic — a long gentle sweep ending in one tight
/// bend, which the fillet generator emits at asymmetric junctions — used to be capped at its tightest point for
/// its whole length: SFO junction J133's B fillet (107 ft, 56°, 22 ft minimum radius) was crawled at 3 kt for
/// 21 s once the pathfinder routed over it.
/// </summary>
public class GroundNavigatorArcSpeedProfileTests(ITestOutputHelper output)
{
    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture(LatLon position, double headingDeg, double speedKts)
    {
        var aircraft = new AircraftState
        {
            Callsign = "NAVARC",
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

    private static GroundNode Node(int id, LatLon position) =>
        new()
        {
            Id = id,
            Position = position,
            Type = GroundNodeType.TaxiwayIntersection,
        };

    /// <summary>~150 ft curve: nearly straight for most of its length, turning hard only in the last ~25 ft.</summary>
    private static (GroundArc Arc, GroundNode From, GroundNode To) DistortedArc()
    {
        var p0 = new LatLon(37.700, -122.200);
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0, new TrueHeading(90.0), 120.0 / GeoMath.FeetPerNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(p0, new TrueHeading(80.0), 150.0 / GeoMath.FeetPerNm);
        var p3 = new LatLon(p3Lat, p3Lon);
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3, new TrueHeading(200.0), 12.0 / GeoMath.FeetPerNm);
        var from = Node(1, p0);
        var to = Node(2, p3);
        var curve = new CubicBezier(p0.Lat, p0.Lon, p1Lat, p1Lon, p2Lat, p2Lon, p3.Lat, p3.Lon);
        var arc = new GroundArc
        {
            Nodes = [from, to],
            TaxiwayNames = ["B"],
            DistanceNm = curve.ArcLengthNm(64),
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = curve.MinRadiusOfCurvatureFt(p0.Lat, 64),
            TurnAngleDeg = 60.0,
        };
        from.Edges.Add(arc);
        to.Edges.Add(arc);
        return (arc, from, to);
    }

    [Fact]
    public void DistortedArc_RunsTheGentleSweepAtSpeed_AndSlowsForTheTightEnd()
    {
        var (arc, from, to) = DistortedArc();
        var segment = new TaxiRouteSegment
        {
            Edge = new DirectionalEdge
            {
                Edge = arc,
                FromNode = from,
                ToNode = to,
            },
            TaxiwayName = "B",
        };
        // A straight continues past the arc so its end is a corner, not a route stop (a stop brakes to zero).
        var (exitLat, exitLon) = GeoMath.ProjectPoint(to.Position, new TrueHeading(segment.Edge.ArrivalBearing), 200.0 / GeoMath.FeetPerNm);
        var exit = Node(3, new LatLon(exitLat, exitLon));
        var exitEdge = new GroundEdge
        {
            Nodes = [to, exit],
            TaxiwayName = "B",
            DistanceNm = GeoMath.DistanceNm(to.Position, exit.Position),
        };
        to.Edges.Add(exitEdge);
        exit.Edges.Add(exitEdge);
        var exitSegment = new TaxiRouteSegment
        {
            Edge = new DirectionalEdge
            {
                Edge = exitEdge,
                FromNode = to,
                ToNode = exit,
            },
            TaxiwayName = "B",
        };
        var route = new TaxiRoute { Segments = [segment, exitSegment], HoldShortPoints = [] };
        double entryBearing = segment.Edge.DepartureBearing;
        var (aircraft, ctx) = MakeFixture(from.Position, entryBearing, speedKts: 10.0);

        var nav = new GroundNavigator { MaxSpeedKts = 30.0 };
        nav.SetupSegment(route, ctx, _ => true);

        double lengthFt = arc.DistanceNm * GeoMath.FeetPerNm;
        double floorKts = arc.MaxSafeSpeedKts(AircraftCategory.Jet);
        double maxSpeedFirstHalfKts = 0;
        double minSpeedLastTenthKts = double.MaxValue;
        int ticks = 0;
        for (; ticks < 2000; ticks++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            var result = nav.Tick(ctx, isLastSegment: false, _ => true);
            double traveledFt = GeoMath.DistanceNm(from.Position, aircraft.Position) * GeoMath.FeetPerNm;
            if (traveledFt < lengthFt * 0.5)
            {
                maxSpeedFirstHalfKts = Math.Max(maxSpeedFirstHalfKts, aircraft.IndicatedAirspeed);
            }
            if (traveledFt > lengthFt * 0.9)
            {
                minSpeedLastTenthKts = Math.Min(minSpeedLastTenthKts, aircraft.IndicatedAirspeed);
            }
            if (result == NavigatorResult.ArrivedAtNode)
            {
                break;
            }
        }

        double seconds = ticks * ctx.DeltaSeconds;
        double crawlSeconds = lengthFt / (floorKts * GeoMath.FeetPerNm / 3600.0);
        output.WriteLine(
            $"arc {lengthFt:F0} ft floor {floorKts:F1} kt: firstHalfMax={maxSpeedFirstHalfKts:F1} kt lastTenthMin={minSpeedLastTenthKts:F1} kt in {seconds:F1} s (crawl {crawlSeconds:F1} s)"
        );

        Assert.True(ticks < 2000, "navigator never reached the arc's to-node");
        Assert.True(
            maxSpeedFirstHalfKts >= 2.0 * floorKts,
            $"the gentle first half should be flown well above the {floorKts:F1} kt tight-stretch cap, peaked at {maxSpeedFirstHalfKts:F1} kt"
        );
        Assert.True(
            minSpeedLastTenthKts <= floorKts + 1.0,
            $"the tight end should be flown near the {floorKts:F1} kt cap, was {minSpeedLastTenthKts:F1} kt"
        );
        Assert.True(seconds < 0.75 * crawlSeconds, $"traversal {seconds:F1} s should be well under the flat-cap crawl {crawlSeconds:F1} s");
    }
}
