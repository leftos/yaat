using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Creates <see cref="RunwayInfo"/> instances for tests with sensible defaults.
/// Only specify the fields the test cares about.
///
/// There is no length parameter: <see cref="RunwayInfo.PavementLengthFt"/> is derived from the two ends'
/// coordinates, so a fixture that cares how long the runway is has to place <c>endLat</c>/<c>endLon</c>
/// at that distance (see <c>LineUpGeometryTests</c>, which projects them from the heading).
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
        // Defaults to the threshold's, so a fixture that states one elevation gets a level runway
        // rather than one sloping to sea level (which would drag AirportElevationFt to the mean).
        double? endElevationFt = null,
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
            TrueHeading1 = new TrueHeading(heading),
            Elevation1Ft = elevationFt,
            Lat2 = endLat,
            Lon2 = endLon,
            TrueHeading2 = new TrueHeading(oppositeHeading),
            Elevation2Ft = endElevationFt ?? elevationFt,
            WidthFt = widthFt,
        };
    }
}
