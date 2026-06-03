using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// One taxiway an ARTCC wants the AUTO taxi pathfinder to avoid at a given airport, as a section of
/// the unified per-airport sidecar (<see cref="AirportSidecarFile"/>). The auto-router skips the named
/// taxiway unless the destination is only reachable through it; explicit <c>TAXI</c> commands that name
/// the taxiway are unaffected.
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
/// On-disk shape of a unified per-airport ground sidecar: one airport per JSON file under
/// <c>Data/ARTCCs/{ARTCC}/Airports/{airport}.json</c>. Consolidates the per-airport ground-routing
/// overrides (avoided taxiways, preset taxi routes) that were previously split across sibling category
/// folders. Unknown sections are ignored, so a file may carry any subset of the sections.
/// </summary>
internal sealed class AirportSidecarFile
{
    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("avoidTaxiways")]
    public List<AvoidTaxiwayEntry> AvoidTaxiways { get; set; } = [];

    [JsonPropertyName("taxiRoutes")]
    public List<TaxiRouteDefinition> TaxiRoutes { get; set; } = [];
}

/// <summary>
/// One airport's parsed, validated sidecar, produced by <see cref="AirportSidecarLoader"/> from a single
/// file. Sections default to empty so the loader and tests can populate only what a file declares.
/// </summary>
public sealed record AirportSidecar(string AirportId)
{
    public IReadOnlyList<AvoidTaxiwayEntry> AvoidTaxiways { get; init; } = [];
    public IReadOnlyList<TaxiRouteDefinition> TaxiRoutes { get; init; } = [];
}
