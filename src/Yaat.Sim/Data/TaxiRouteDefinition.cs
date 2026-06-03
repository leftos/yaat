using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// One named taxi route loaded from a per-airport JSON file under
/// <c>Data/TaxiRoutes/{ARTCC}/</c>. The path is a whitespace-separated list of taxiway
/// names plus an optional destination (runway hold-short, parking, or spot). At dispatch
/// time it is reconstructed into an equivalent <c>TAXI</c> command and fed through the
/// existing <see cref="Yaat.Sim.Data.Airport.TaxiPathfinder.ResolveExplicitPath"/> path —
/// no new command verb or pathfinder, just a UI shortcut for routes controllers tend to
/// issue repeatedly per local SOP.
/// </summary>
public sealed class TaxiRouteDefinition
{
    private static readonly char[] PathSeparators = [' ', '\t'];

    /// <summary>ICAO of the airport this route applies to. Stamped by the loader from the
    /// enclosing file's <c>airportId</c>; not deserialized from the route entry itself.</summary>
    [JsonIgnore]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Whitespace-separated taxiway names, e.g. <c>"T T3 B"</c>. Whatever you'd
    /// type after <c>TAXI</c> in the command bar (minus the destination keywords). The order
    /// matters; whitespace is collapsed at split time so multiple spaces are harmless.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("destinationRunway")]
    public string? DestinationRunway { get; set; }

    [JsonPropertyName("destinationParking")]
    public string? DestinationParking { get; set; }

    [JsonPropertyName("destinationSpot")]
    public string? DestinationSpot { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Splits the whitespace-separated <see cref="Path"/> into individual taxiway tokens.
    /// Returns a fresh list each call (cheap; only invoked at menu-build time).
    /// </summary>
    public List<string> GetPathTokens() => [.. Path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Reconstructs the canonical <c>TAXI</c> command string this preset dispatches.
    /// At most one destination is set (loader-validated) so the suffixes are mutually exclusive.
    /// Examples:
    /// <list type="bullet">
    /// <item><c>{ path: "W", destinationRunway: "30" }</c> → <c>TAXI W RWY 30</c></item>
    /// <item><c>{ path: "A B", destinationParking: "G7" }</c> → <c>TAXI A B @G7</c></item>
    /// <item><c>{ path: "K W" }</c> → <c>TAXI K W</c></item>
    /// </list>
    /// </summary>
    public string ToCanonicalCommand()
    {
        var sb = new System.Text.StringBuilder("TAXI ");
        sb.Append(string.Join(' ', GetPathTokens()));

        if (!string.IsNullOrWhiteSpace(DestinationRunway))
        {
            sb.Append(" RWY ").Append(DestinationRunway);
        }
        else if (!string.IsNullOrWhiteSpace(DestinationParking))
        {
            sb.Append(" @").Append(DestinationParking);
        }
        else if (!string.IsNullOrWhiteSpace(DestinationSpot))
        {
            sb.Append(" $").Append(DestinationSpot);
        }

        return sb.ToString();
    }
}
