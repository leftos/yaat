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

        string wanted = NavigationDatabase.NormalizeAirport(airportId);
        IEnumerable<string> files = AirportSidecarLoader
            .SidecarFilesInLoadOrder(artccsBaseDir)
            .Where(file => (SidecarAirport(file) is { } id) && (NavigationDatabase.NormalizeAirport(id) == wanted));

        var concatenated = new StringBuilder();
        foreach (string file in files)
        {
            concatenated.Append(Sha256Hex(File.ReadAllBytes(file)));
        }

        return Sha256Hex(Encoding.ASCII.GetBytes(concatenated.ToString()));
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
