using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Creates <see cref="RunwayInfo"/> instances for tests with sensible defaults.
/// Only specify the fields the test cares about.
/// </summary>
internal static class TestRunwayFactory
{
    internal static RunwayInfo Make(
        string designator = "28",
        string airportId = "KTEST",
        double thresholdLat = 37.0,
        double thresholdLon = -122.0,
        double endLat = 37.01,
        double endLon = -122.01,
        double heading = 280,
        double elevationFt = 0,
        double endElevationFt = 0,
        double lengthFt = 10000,
        double widthFt = 150
    )
    {
        var id = new RunwayIdentifier(designator);
        double oppositeHeading = (heading + 180) % 360;

        return new RunwayInfo
        {
            AirportId = airportId,
            Id = id,
            Designator = designator,
            Lat1 = thresholdLat,
            Lon1 = thresholdLon,
            Heading1 = heading,
            Elevation1Ft = elevationFt,
            Lat2 = endLat,
            Lon2 = endLon,
            Heading2 = oppositeHeading,
            Elevation2Ft = endElevationFt,
            LengthFt = lengthFt,
            WidthFt = widthFt,
        };
    }
}
