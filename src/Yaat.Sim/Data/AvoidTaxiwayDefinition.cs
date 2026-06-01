using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// One taxiway an ARTCC wants the AUTO taxi pathfinder to avoid at a given airport, loaded from a
/// per-airport JSON file under <c>Data/ARTCCs/{ARTCC}/AvoidTaxiways/</c>. The auto-router skips the
/// named taxiway unless the destination is only reachable through it (see
/// <see cref="Yaat.Sim.Data.Airport.TaxiPathfinder"/>). Explicit <c>TAXI</c> commands that name the
/// taxiway are unaffected.
/// </summary>
public sealed class AvoidTaxiwayEntry
{
    /// <summary>Taxiway name to avoid, e.g. <c>"S"</c>. Matched case-insensitively against edge taxiway names.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Optional human-readable rationale (SOP reference, condition). Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One airport's worth of avoided taxiways, as produced by <see cref="AvoidTaxiwayLoader"/> from a
/// single JSON file.
/// </summary>
public sealed record AvoidTaxiwayAirport(string AirportId, IReadOnlyList<AvoidTaxiwayEntry> Taxiways);

/// <summary>
/// On-disk file shape: one airport's avoided-taxiway list per JSON file.
/// </summary>
internal sealed class AvoidTaxiwaysFile
{
    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("taxiways")]
    public List<AvoidTaxiwayEntry> Taxiways { get; set; } = [];
}
