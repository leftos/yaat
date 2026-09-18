using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class RunwayAwareExitTests
{
    private const string TestDataDir = "TestData";

    private static AirportGroundLayout? LoadAirportLayout(string airportId, string subdir)
    {
        string path = Path.Combine(TestDataDir, $"{subdir}.geojson");
        if (File.Exists(path))
        {
            return GeoJsonParser.Parse(airportId, File.ReadAllText(path), null);
        }

        return null;
    }

    [Fact]
    public void OAK_FindNearestExit_28L_ReturnsExitOnCorrectRunway()
    {
        AirportGroundLayout? layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Get 28L runway centerline to compute a midpoint position on 28L
        GroundRunway? rwy28L = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy28L);

        // Compute a midpoint along 28L's centerline
        List<(double Lat, double Lon)> coords = rwy28L.Coordinates;
        int midIdx = coords.Count / 2;
        double midLat = coords[midIdx].Lat;
        double midLon = coords[midIdx].Lon;

        // Runway heading for 28L (~280°)
        double heading = 280.0;

        GroundNode? exitNode = layout.FindNearestExit(midLat, midLon, new TrueHeading(heading), "28L");
        Assert.NotNull(exitNode);

        // Verify: the exit node should be closer to 28L than to 28R
        GroundRunway? rwy28R = layout.FindGroundRunway("28R");
        Assert.NotNull(rwy28R);

        double distTo28L = MinDistToRunway(exitNode, rwy28L);
        double distTo28R = MinDistToRunway(exitNode, rwy28R);

        Assert.True(
            distTo28L <= distTo28R,
            $"Exit node {exitNode.Id} at ({exitNode.Position.Lat:F6},{exitNode.Position.Lon:F6}) is closer to 28R ({distTo28R:F4}nm) than 28L ({distTo28L:F4}nm)"
        );
    }

    [Fact]
    public void OAK_FindNearestExit_28R_ReturnsExitOnCorrectRunway()
    {
        AirportGroundLayout? layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        GroundRunway? rwy28R = layout.FindGroundRunway("28R");
        Assert.NotNull(rwy28R);

        List<(double Lat, double Lon)> coords = rwy28R.Coordinates;
        int midIdx = coords.Count / 2;
        double midLat = coords[midIdx].Lat;
        double midLon = coords[midIdx].Lon;

        double heading = 280.0;

        GroundNode? exitNode = layout.FindNearestExit(midLat, midLon, new TrueHeading(heading), "28R");
        Assert.NotNull(exitNode);

        GroundRunway? rwy28L = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy28L);

        double distTo28R = MinDistToRunway(exitNode, rwy28R);
        double distTo28L = MinDistToRunway(exitNode, rwy28L);

        Assert.True(
            distTo28R <= distTo28L,
            $"Exit node {exitNode.Id} at ({exitNode.Position.Lat:F6},{exitNode.Position.Lon:F6}) is closer to 28L ({distTo28L:F4}nm) than 28R ({distTo28R:F4}nm)"
        );
    }

    [Fact]
    public void OAK_FindNearestExit_NullDesignator_ReturnsSomeExit()
    {
        AirportGroundLayout? layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        GroundRunway? rwy28L = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy28L);

        List<(double Lat, double Lon)> coords = rwy28L.Coordinates;
        int midIdx = coords.Count / 2;
        double midLat = coords[midIdx].Lat;
        double midLon = coords[midIdx].Lon;

        // Without a runway designator, should still return an exit (current behavior)
        GroundNode? exitNode = layout.FindNearestExit(midLat, midLon, new TrueHeading(280.0), null);
        Assert.NotNull(exitNode);
    }

    [Fact]
    public void OAK_FindNearestExit_28R_PrefersRightSideTowardParking()
    {
        AirportGroundLayout? layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // 28R at OAK has abundant parking to the right (north) side.
        // Exits to the left just connect to 28L with no nearby parking.
        // The parking proximity bias should cause FindNearestExit to prefer
        // exits on the right (parking) side.
        GroundRunway? rwy28R = layout.FindGroundRunway("28R");
        Assert.NotNull(rwy28R);

        List<(double Lat, double Lon)> coords = rwy28R.Coordinates;
        int midIdx = coords.Count / 2;
        double midLat = coords[midIdx].Lat;
        double midLon = coords[midIdx].Lon;
        double heading = 280.0;

        GroundNode? exitNode = layout.FindNearestExit(midLat, midLon, new TrueHeading(heading), "28R");
        Assert.NotNull(exitNode);

        // The exit should be to the right (north) of the runway heading.
        // Right of heading 280 = positive signed angle.
        double bearing = GeoMath.BearingTo(new LatLon(midLat, midLon), exitNode.Position);
        double relative = new TrueHeading(heading).SignedAngleTo(new TrueHeading(bearing));

        Assert.True(relative > 0, $"Exit node {exitNode.Id} is to the LEFT of 28R heading (relative={relative:F1}°), expected RIGHT toward parking");
    }

    [Fact]
    public void FindGroundRunway_Finds28L()
    {
        AirportGroundLayout? layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        GroundRunway? rwy = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy);
        Assert.Contains("28L", rwy.Name);
    }

    [Fact]
    public void FindGroundRunway_Finds10R()
    {
        AirportGroundLayout? layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // 10R is the opposite end of 28L
        GroundRunway? rwy = layout.FindGroundRunway("10R");
        Assert.NotNull(rwy);
    }

    private static double MinDistToRunway(GroundNode node, GroundRunway runway)
    {
        double minDist = double.MaxValue;
        List<(double Lat, double Lon)> coords = runway.Coordinates;

        for (int i = 0; i < coords.Count - 1; i++)
        {
            double dist = GeoMath.DistanceNm(node.Position, new LatLon(coords[i].Lat, coords[i].Lon));
            if (dist < minDist)
            {
                minDist = dist;
            }
        }

        if (coords.Count > 0)
        {
            double lastDist = GeoMath.DistanceNm(node.Position, new LatLon(coords[^1].Lat, coords[^1].Lon));
            if (lastDist < minDist)
            {
                minDist = lastDist;
            }
        }

        return minDist;
    }
}
