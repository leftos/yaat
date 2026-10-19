using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// The shipped sidecar stand lead-ins of a real airport, as the production layout build passes them to
/// <see cref="Yaat.Sim.Data.Airport.GeoJsonParser"/>, so a test that builds a real airport's layout builds the shipping graph.
/// </summary>
internal static class TestStandLeadIns
{
    /// <summary>
    /// The airport's stand lead-ins from the loaded sidecar catalog, or none when no navdata loaded (the harness's
    /// silent-skip convention).
    /// </summary>
    /// <param name="airportId">The airport, with or without its K prefix.</param>
    /// <returns>The lead-ins.</returns>
    internal static IReadOnlyList<StandLeadIn> For(string airportId)
    {
        TestVnasData.EnsureInitialized();
        return (NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty).GetStandLeadIns(airportId);
    }
}
