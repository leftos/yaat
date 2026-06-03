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
/// One implicitly-allowed named connector taxiway. A connector taxiway (e.g. <c>"LF"</c>) is normally
/// a letter-only taxiway that the controller must name explicitly. This entry authorizes it implicitly,
/// but only contextually — when the controller's cleared sequence places the two <see cref="Between"/>
/// taxiways adjacent (unordered). So <c>{ connector: "LF", between: ["L","F"] }</c> authorizes <c>LF</c>
/// for <c>TAXI L F</c> (and <c>TAXI F L</c>) but not for <c>TAXI L A F</c>.
/// </summary>
public sealed class ImplicitConnectorEntry
{
    /// <summary>The connector taxiway name to authorize, e.g. <c>"LF"</c>.</summary>
    [JsonPropertyName("connector")]
    public string Connector { get; set; } = "";

    /// <summary>The two taxiways this connector bridges (exactly 2, unordered).</summary>
    [JsonPropertyName("between")]
    public List<string> Between { get; set; } = [];

    /// <summary>Optional human-readable rationale. Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// On-disk shape of a unified per-airport ground sidecar: one airport per JSON file under
/// <c>Data/ARTCCs/{ARTCC}/Airports/{airport}.json</c>. Consolidates the per-airport ground-routing
/// overrides (avoided taxiways, preset taxi routes, implicit connectors) that were previously split
/// across sibling category folders. Unknown sections are ignored, so a file may carry any subset.
/// </summary>
internal sealed class AirportSidecarFile
{
    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("avoidTaxiways")]
    public List<AvoidTaxiwayEntry> AvoidTaxiways { get; set; } = [];

    [JsonPropertyName("taxiRoutes")]
    public List<TaxiRouteDefinition> TaxiRoutes { get; set; } = [];

    [JsonPropertyName("implicitConnectors")]
    public List<ImplicitConnectorEntry> ImplicitConnectors { get; set; } = [];

    [JsonPropertyName("oneWayEdges")]
    public List<OneWayConstraintEntry> OneWayEdges { get; set; } = [];
}

/// <summary>
/// One airport's parsed, validated sidecar, produced by <see cref="AirportSidecarLoader"/> from a single
/// file. Sections default to empty so the loader and tests can populate only what a file declares.
/// </summary>
public sealed record AirportSidecar(string AirportId)
{
    public IReadOnlyList<AvoidTaxiwayEntry> AvoidTaxiways { get; init; } = [];
    public IReadOnlyList<TaxiRouteDefinition> TaxiRoutes { get; init; } = [];
    public IReadOnlyList<ImplicitConnectorEntry> ImplicitConnectors { get; init; } = [];
    public IReadOnlyList<OneWayConstraint> OneWayEdges { get; init; } = [];
}
