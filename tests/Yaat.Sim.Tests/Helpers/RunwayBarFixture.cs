using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// A real airport's runway with its TestData ground layout and the lowest-numbered hold-short bar abeam the pavement (its foot of
/// perpendicular not off either end), or null when the layout or the runway is unavailable.
/// </summary>
public static class RunwayBarFixture
{
    /// <summary>The runway, its layout, and that bar.</summary>
    /// <param name="airport">The airport identifier (ICAO or short code).</param>
    /// <param name="designator">The runway designator.</param>
    /// <returns>The layout, runway and bar, or null when unavailable.</returns>
    public static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar)? RunwayBar(string airport, string designator)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway(airport, designator);
        if ((layout is null) || (runway is null))
        {
            return null;
        }

        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        GroundNode? bar = RunwayHoldBars
            .RunwayBars(layout, runway)
            .Where(b => !GeoMath.FootOfPerpendicular(b.Position, end1, end2).Clamped)
            .OrderBy(b => b.Id)
            .FirstOrDefault();
        return bar is null ? null : (layout, runway, bar);
    }
}
