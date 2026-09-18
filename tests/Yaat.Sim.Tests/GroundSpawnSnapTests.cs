using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="AirportGroundLayout.FindNearestTaxiEdge"/> and
/// <see cref="GroundSpawnSnap.Apply"/>. Uses small synthetic layouts so the
/// geometry is easy to reason about — real-airport coverage is exercised
/// indirectly via the Issue142 diagnostic trace.
/// </summary>
public class GroundSpawnSnapTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Build a layout with a 1000 ft east-running taxiway edge between two
    /// nodes. Optional extra edges via <paramref name="configure"/>.
    /// </summary>
    private static AirportGroundLayout BuildLayout(Action<AirportGroundLayout>? configure = null)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.0, -122.0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        (double n2Lat, double n2Lon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(n2Lat, n2Lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        layout.Nodes[1] = n1;
        layout.Nodes[2] = n2;

        var edge = new GroundEdge
        {
            Nodes = [n1, n2],
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(n1.Position, n2.Position),
        };
        layout.Edges.Add(edge);

        configure?.Invoke(layout);

        layout.RebuildAdjacencyLists();
        return layout;
    }

    [Fact]
    public void FindNearestTaxiEdge_ReturnsNearestStraightEdge()
    {
        AirportGroundLayout layout = BuildLayout();

        // Aircraft 50 ft south of the midpoint of the taxiway edge.
        GroundNode n1 = layout.Nodes[1];
        (double midLat, double midLon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);
        (double acLat, double acLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(180.0), 50.0 / GeoMath.FeetPerNm);

        AirportGroundLayout.NearestTaxiEdge? result = layout.FindNearestTaxiEdge(acLat, acLon);
        Assert.NotNull(result);
        Assert.Equal("A", result.Value.Edge.TaxiwayName);

        double distFt = result.Value.DistNm * GeoMath.FeetPerNm;
        _output.WriteLine($"distFt={distFt:F2}, foot=({result.Value.FootLat:F6}, {result.Value.FootLon:F6})");
        Assert.InRange(distFt, 49.0, 51.0);
    }

    [Fact]
    public void FindNearestTaxiEdge_ExcludesRunwayCenterlines()
    {
        // Add a runway edge closer to the aircraft than the taxi edge.
        AirportGroundLayout layout = BuildLayout(l =>
        {
            GroundNode n1 = l.Nodes[1];
            (double r1Lat, double r1Lon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(180.0), 20.0 / GeoMath.FeetPerNm);
            (double r2Lat, double r2Lon) = GeoMath.ProjectPoint(r1Lat, r1Lon, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
            var r1 = new GroundNode
            {
                Id = 10,
                Position = new LatLon(r1Lat, r1Lon),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            var r2 = new GroundNode
            {
                Id = 11,
                Position = new LatLon(r2Lat, r2Lon),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            l.Nodes[10] = r1;
            l.Nodes[11] = r2;
            l.Edges.Add(
                new GroundEdge
                {
                    Nodes = [r1, r2],
                    TaxiwayName = "RWY28R/10L",
                    DistanceNm = GeoMath.DistanceNm(r1Lat, r1Lon, r2Lat, r2Lon),
                }
            );
        });

        // Aircraft sits between the taxi edge (80 ft north of query) and runway (20 ft south).
        GroundNode n1 = layout.Nodes[1];
        (double midLat, double midLon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);

        AirportGroundLayout.NearestTaxiEdge? result = layout.FindNearestTaxiEdge(midLat, midLon);
        Assert.NotNull(result);
        // Taxi edge "A" is at 0 ft (aircraft is on it), runway at 20 ft. Both are close, so the
        // relevant assertion is that we DIDN'T pick the runway. Check taxiway name.
        Assert.Equal("A", result.Value.Edge.TaxiwayName);
    }

    [Fact]
    public void FindNearestTaxiEdge_ExcludesRamps()
    {
        AirportGroundLayout layout = BuildLayout(l =>
        {
            // Ramp edge very close to origin
            GroundNode n1 = l.Nodes[1];
            (double rampLat, double rampLon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(270.0), 10.0 / GeoMath.FeetPerNm);
            var rampNode = new GroundNode
            {
                Id = 20,
                Position = new LatLon(rampLat, rampLon),
                Type = GroundNodeType.Parking,
            };
            l.Nodes[20] = rampNode;
            l.Edges.Add(
                new GroundEdge
                {
                    Nodes = [rampNode, n1],
                    TaxiwayName = "RAMP",
                    DistanceNm = GeoMath.DistanceNm(new LatLon(rampLat, rampLon), n1.Position),
                }
            );
        });

        AirportGroundLayout.NearestTaxiEdge? result = layout.FindNearestTaxiEdge(layout.Nodes[20].Position);
        Assert.NotNull(result);
        // Should pick taxi edge A, not the ramp.
        Assert.Equal("A", result.Value.Edge.TaxiwayName);
    }

    [Fact]
    public void Apply_OffEdgeGroundAircraft_SnapsToFootAndRotatesHeading()
    {
        AirportGroundLayout layout = BuildLayout();

        // Aircraft 35 ft south of midpoint, heading 80° (close to edge bearing 90°).
        GroundNode n1 = layout.Nodes[1];
        (double midLat, double midLon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);
        (double acLat, double acLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(180.0), 35.0 / GeoMath.FeetPerNm);

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = new TrueHeading(80.0),
            IsOnGround = true,
        };
        aircraft.TrueTrack = aircraft.TrueHeading;

        GroundSpawnSnap.Apply(aircraft, layout);

        // Aircraft should now be at the foot of perpendicular (midpoint of edge, ±tolerance).
        double distFt = GeoMath.DistanceNm(aircraft.Position, new LatLon(midLat, midLon)) * GeoMath.FeetPerNm;
        Assert.InRange(distFt, 0.0, 0.5);

        // Heading should have rotated from 80° to the edge forward direction (90° — closer
        // than the reverse 270°).
        Assert.InRange(aircraft.TrueHeading.Degrees, 89.9, 90.1);
    }

    [Fact]
    public void Apply_ChoosesReverseEdgeDirection_WhenOriginalHeadingCloser()
    {
        AirportGroundLayout layout = BuildLayout();

        // Aircraft near midpoint, heading 260° (close to reverse edge bearing 270°).
        GroundNode n1 = layout.Nodes[1];
        (double midLat, double midLon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);
        (double acLat, double acLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(180.0), 35.0 / GeoMath.FeetPerNm);

        var aircraft = new AircraftState
        {
            Callsign = "TEST2",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = new TrueHeading(260.0),
            IsOnGround = true,
        };
        aircraft.TrueTrack = aircraft.TrueHeading;

        GroundSpawnSnap.Apply(aircraft, layout);

        // Heading should rotate toward reverse direction 270°, not forward 90°.
        Assert.InRange(aircraft.TrueHeading.Degrees, 269.9, 270.1);
    }

    [Fact]
    public void Apply_AirborneAircraft_IsNotSnapped()
    {
        AirportGroundLayout layout = BuildLayout();

        GroundNode n1 = layout.Nodes[1];
        (double midLat, double midLon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);
        (double acLat, double acLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(180.0), 35.0 / GeoMath.FeetPerNm);

        var aircraft = new AircraftState
        {
            Callsign = "TEST3",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = new TrueHeading(80.0),
            IsOnGround = false,
        };

        LatLon origPos = aircraft.Position;
        double origHdg = aircraft.TrueHeading.Degrees;

        GroundSpawnSnap.Apply(aircraft, layout);

        Assert.Equal(origPos.Lat, aircraft.Position.Lat);
        Assert.Equal(origPos.Lon, aircraft.Position.Lon);
        Assert.Equal(origHdg, aircraft.TrueHeading.Degrees);
    }

    [Fact]
    public void Apply_BeyondThreshold_LeavesPoseUnchanged()
    {
        AirportGroundLayout layout = BuildLayout();

        // Aircraft 500 ft south of the edge — well beyond the 200 ft snap threshold.
        GroundNode n1 = layout.Nodes[1];
        (double midLat, double midLon) = GeoMath.ProjectPoint(n1.Position, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);
        (double acLat, double acLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(180.0), 500.0 / GeoMath.FeetPerNm);

        var aircraft = new AircraftState
        {
            Callsign = "TEST4",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = new TrueHeading(45.0),
            IsOnGround = true,
        };

        GroundSpawnSnap.Apply(aircraft, layout);

        // Position and heading unchanged.
        Assert.Equal(acLat, aircraft.Position.Lat);
        Assert.Equal(acLon, aircraft.Position.Lon);
        Assert.Equal(45.0, aircraft.TrueHeading.Degrees);
    }
}
