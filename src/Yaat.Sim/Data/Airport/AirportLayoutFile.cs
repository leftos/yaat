namespace Yaat.Sim.Data.Airport;

/// <summary>Loads an airport ground layout from a GeoJSON file on disk, parsed as <see cref="AirportLayoutDownloader"/> parses a download.</summary>
public static class AirportLayoutFile
{
    /// <summary>
    /// Parses the airport's ground layout from <paramref name="geoJsonPath"/>, with the airport's stand lead-ins from the loaded
    /// navigation database's sidecars (none when no database is loaded). A missing file or a parse error propagates.
    /// </summary>
    /// <param name="geoJsonPath">The ATCTrainer-format GeoJSON file.</param>
    /// <param name="airportId">The airport, FAA or ICAO form ("OAK" or "KOAK").</param>
    /// <returns>The parsed layout.</returns>
    public static AirportGroundLayout Load(string geoJsonPath, string airportId)
    {
        string faaCode = AirportLayoutDownloader.ToFaaCode(airportId);
        return GeoJsonParser.Parse(
            faaCode.ToLowerInvariant(),
            File.ReadAllText(geoJsonPath),
            faaCode,
            new LayoutInputs(
                (NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty).GetStandLeadIns(faaCode),
                VideoMap: null
            )
        );
    }
}
