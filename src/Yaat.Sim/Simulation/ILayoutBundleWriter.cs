using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Simulation;

/// <summary>
/// An archive writer that bundles a room's pinned airport ground layouts (a recording archive or a room checkpoint), so
/// one caller writes the same set into either: every pinned layout under its own airport ID, the source GeoJSON of each
/// that has one, and the airports pinned with no map.
/// </summary>
public interface ILayoutBundleWriter
{
    /// <summary>
    /// Writes <paramref name="layout"/> under its own <see cref="AirportGroundLayout.AirportId"/> and lists that ID in the
    /// manifest.
    /// </summary>
    void WriteLayout(AirportGroundLayout layout);

    /// <summary>
    /// Writes the source GeoJSON <paramref name="airportId"/>'s layout was parsed from and lists the ID in the manifest. A
    /// blank ID or GeoJSON is rejected with an <see cref="ArgumentException"/>; a second write for an ID already written,
    /// compared ignoring case, is ignored.
    /// </summary>
    void WriteAirportGeoJson(string airportId, string geoJson);

    /// <summary>Lists the airports (FAA codes) pinned with no map in the manifest, so that a load of the archive pins them with none.</summary>
    void WriteMissingLayoutAirportIds(IEnumerable<string> faaCodes);
}
