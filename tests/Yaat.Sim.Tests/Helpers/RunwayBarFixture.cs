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
    public static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar)? RunwayBar(string airport, string designator) =>
        LowestBar(airport, designator, _ => true);

    /// <summary>
    /// The runway, its layout, and the lowest-numbered such bar on <paramref name="taxiway"/>: a fixture whose meaning depends on
    /// which bar it stands at (how far its fouler sits from the pavement) names the taxiway rather than trusting the id order.
    /// </summary>
    /// <param name="airport">The airport identifier (ICAO or short code).</param>
    /// <param name="designator">The runway designator.</param>
    /// <param name="taxiway">The taxiway the bar sits on.</param>
    /// <returns>The layout, runway and bar, or null when unavailable.</returns>
    public static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar)? RunwayBar(string airport, string designator, string taxiway) =>
        LowestBar(airport, designator, b => b.Edges.Any(e => string.Equals(e.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase)));

    private static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar)? LowestBar(
        string airport,
        string designator,
        Func<GroundNode, bool> onTaxiway
    )
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
            .Where(onTaxiway)
            .OrderBy(b => b.Id)
            .FirstOrDefault();
        return bar is null ? null : (layout, runway, bar);
    }
}
