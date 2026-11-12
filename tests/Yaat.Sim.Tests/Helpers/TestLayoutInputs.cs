using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// The shipped sidecar inputs of a real airport's layout (its stand lead-ins and spot-edge chains), as the production layout
/// build passes them to <see cref="GeoJsonParser"/>, so a test that builds a real airport's layout builds the shipping graph.
/// </summary>
internal static class TestLayoutInputs
{
    /// <summary>
    /// The airport's layout inputs from the loaded sidecar catalog.
    /// </summary>
    /// <param name="airportId">The airport, with or without its K prefix.</param>
    /// <param name="videoMap">The airport's tower-cab videomap, or null to build without one.</param>
    /// <returns>The layout inputs.</returns>
    internal static LayoutInputs For(string airportId, TowerCabVideoMap? videoMap)
    {
        TestVnasData.EnsureInitialized();
        AirportSidecarCatalog catalog = NavigationDatabase.Instance.AirportSidecars;
        return new LayoutInputs(catalog.GetStandLeadIns(airportId), videoMap, SpotEdges: catalog.GetSpotEdges(airportId));
    }
}
