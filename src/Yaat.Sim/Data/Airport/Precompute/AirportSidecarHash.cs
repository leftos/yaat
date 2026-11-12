using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The hash of an airport's ground sidecars (<c>{ARTCCs}/{ARTCC}/Airports/*.json</c>), which the push targets depend on
/// through the movement-area classification, so a sidecar edit stales the push half of a precompute entry.
/// </summary>
public static class AirportSidecarHash
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirportSidecarHash");

    /// <summary>
    /// The SHA-256 of the airport's sidecars: each file whose inner <c>airportId</c> normalises
    /// (<see cref="NavigationDatabase.NormalizeAirport"/>) to the airport is SHA-256'd over its bytes; the lowercase hex
    /// hashes, ordered by the files' forward-slash paths relative to <paramref name="artccsBaseDir"/> (ordinal), are
    /// concatenated and that ASCII string is SHA-256'd again. With no matching file, the SHA-256 of the empty string. A file
    /// that is not JSON or names no airport is skipped with a warning, as the sidecar loader skips it.
    /// </summary>
    /// <param name="artccsBaseDir">The ARTCCs data directory.</param>
    /// <param name="airportId">The airport, with or without its K prefix.</param>
    /// <returns>The hash, lowercase hex.</returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="artccsBaseDir"/> does not exist.</exception>
    public static string For(string artccsBaseDir, string airportId)
    {
        if (!Directory.Exists(artccsBaseDir))
        {
            throw new DirectoryNotFoundException($"ARTCCs directory not found: {artccsBaseDir}");
        }

        var concatenated = new StringBuilder();
        foreach (string file in SidecarFilesOf(artccsBaseDir, airportId))
        {
            concatenated.Append(Sha256Hex(File.ReadAllBytes(file)));
        }

        return Sha256Hex(Encoding.ASCII.GetBytes(concatenated.ToString()));
    }

    /// <summary>
    /// The hash of the sidecar sections the layout build reads, the stand lead-ins and the spot-edge chains: the airport's
    /// sidecars (the files <see cref="For"/> hashes) are loaded and their merged sections hashed by
    /// <see cref="OfLayoutSidecar"/>, so an edit to either stales the layout half of a precompute entry while any other
    /// sidecar edit leaves it current.
    /// </summary>
    /// <param name="artccsBaseDir">The ARTCCs data directory.</param>
    /// <param name="airportId">The airport, with or without its K prefix.</param>
    /// <returns>The hash, lowercase hex.</returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="artccsBaseDir"/> does not exist.</exception>
    public static string ForLayoutSidecar(string artccsBaseDir, string airportId)
    {
        if (!Directory.Exists(artccsBaseDir))
        {
            throw new DirectoryNotFoundException($"ARTCCs directory not found: {artccsBaseDir}");
        }

        // The load's warnings are dropped here: the sidecar catalog's own load reports them.
        var catalog = new AirportSidecarCatalog(AirportSidecarLoader.LoadFiles(SidecarFilesOf(artccsBaseDir, airportId)).Airports);
        return OfLayoutSidecar(catalog.GetStandLeadIns(airportId), catalog.GetSpotEdges(airportId));
    }

    /// <summary>
    /// The SHA-256 of a canonical form of <paramref name="leadIns"/> and <paramref name="spotEdges"/>: one line per lead-in,
    /// in order, of its stand, taxiway, latitude and longitude (round-trip invariant text), tab-separated; then, only when
    /// there are chains, a <c>spotEdges</c> line and per chain, in order, a <c>chain</c> line with its name followed by one
    /// tab-led line per point of its kind, spot, junction taxiways, taxiway, latitude and longitude (empty when absent).
    /// Notes are left out, since they do not shape the layout. An airport without chains so hashes exactly as its lead-ins
    /// alone; no lead-ins and no chains give the SHA-256 of the empty string.
    /// </summary>
    /// <param name="leadIns">An airport's stand lead-ins (<see cref="AirportSidecarCatalog.GetStandLeadIns"/>).</param>
    /// <param name="spotEdges">An airport's spot-edge chains (<see cref="AirportSidecarCatalog.GetSpotEdges"/>).</param>
    /// <returns>The hash, lowercase hex.</returns>
    public static string OfLayoutSidecar(IReadOnlyList<StandLeadIn> leadIns, IReadOnlyList<SpotEdgeChain> spotEdges)
    {
        var canonical = new StringBuilder();
        foreach (StandLeadIn leadIn in leadIns)
        {
            canonical.Append(CultureInfo.InvariantCulture, $"{leadIn.Stand}\t{leadIn.Taxiway}\t{leadIn.Point.Lat:R}\t{leadIn.Point.Lon:R}\n");
        }

        if (spotEdges.Count > 0)
        {
            canonical.Append("spotEdges\n");
            foreach (SpotEdgeChain chain in spotEdges)
            {
                AppendChain(canonical, chain);
            }
        }

        return Sha256Hex(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static void AppendChain(StringBuilder canonical, SpotEdgeChain chain)
    {
        canonical.Append(CultureInfo.InvariantCulture, $"chain\t{chain.Name}\n");
        foreach (SpotEdgePoint point in chain.Path)
        {
            string lat = string.Empty;
            string lon = string.Empty;
            if (point.Position is { } position)
            {
                lat = position.Lat.ToString("R", CultureInfo.InvariantCulture);
                lon = position.Lon.ToString("R", CultureInfo.InvariantCulture);
            }

            canonical.Append(
                CultureInfo.InvariantCulture,
                $"\t{point.Kind}\t{point.Spot}\t{point.JunctionA}\t{point.JunctionB}\t{point.Taxiway}\t{lat}\t{lon}\n"
            );
        }
    }

    private static IEnumerable<string> SidecarFilesOf(string artccsBaseDir, string airportId)
    {
        string wanted = NavigationDatabase.NormalizeAirport(airportId);
        return AirportSidecarLoader
            .SidecarFilesInLoadOrder(artccsBaseDir)
            .Where(file => (SidecarAirport(file) is { } id) && (NavigationDatabase.NormalizeAirport(id) == wanted));
    }

    private static string? SidecarAirport(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!property.Name.Equals("airportId", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }

                    Log.LogWarning(
                        "Sidecar {File} has a {Kind} airportId, not a string; it is left out of every airport's sidecar hash",
                        file,
                        property.Value.ValueKind
                    );
                    return null;
                }
            }
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Sidecar {File} is not valid JSON; it is left out of its airport's sidecar hash", file);
            return null;
        }

        Log.LogWarning("Sidecar {File} names no airportId; it is left out of every airport's sidecar hash", file);
        return null;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
