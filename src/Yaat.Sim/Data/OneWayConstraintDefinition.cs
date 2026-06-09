using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// One waypoint of a one-way constraint path. <see cref="Point"/> is <c>[lon, lat]</c> (GeoJSON order,
/// copy-pasteable straight from the airport map). <see cref="Taxiway"/> is the taxiway the author expects
/// this vertex to land on — a validation hint (a warning is logged if a future map shifts the vertex off
/// that taxiway); it is not used for routing.
/// </summary>
public sealed class OneWayWaypoint
{
    [JsonPropertyName("point")]
    public double[] Point { get; set; } = [];

    [JsonPropertyName("taxiway")]
    public string? Taxiway { get; set; }
}

/// <summary>
/// On-disk shape of one one-way taxiway constraint in the sidecar's <c>oneWayEdges</c> section. The
/// allowed travel direction is the order of <see cref="Path"/> (first → last). <see cref="Block"/> is
/// <c>"reverse"</c> (default — forbid only the against-order direction, a true one-way) or <c>"both"</c>
/// (forbid travel in either direction — a closed segment / forbidden turn). Consecutive waypoints need
/// not share a taxiway, so the same construct expresses one-way transitions and forbidden turns across a
/// junction; a path of N points traces a curve, and two endpoints on one taxiway let the resolver fill
/// the span between them.
/// </summary>
public sealed class OneWayConstraintEntry
{
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("block")]
    public string? Block { get; set; }

    [JsonPropertyName("path")]
    public List<OneWayWaypoint> Path { get; set; } = [];
}

/// <summary>A parsed one-way waypoint: a geographic point and its expected taxiway (validation hint).</summary>
public sealed record OneWayPoint(double Lat, double Lon, string? Taxiway);

/// <summary>
/// On-disk shape of one blocked turn in the sidecar's <c>blockedTurns</c> section. <see cref="Path"/> is
/// an ordered coordinate polyline (≥3 points, reusing the <see cref="OneWayWaypoint"/> <c>[lon,lat]</c>
/// shape) describing one side of an intersection — an L-shape an aircraft must never turn through. Unlike
/// <see cref="OneWayConstraintEntry"/> there is no <c>block</c> mode: a blocked turn is always
/// bidirectional and hard (it represents a corner with no painted line; aircraft must use the connector
/// instead). The pathfinder never routes the turn (AUTO and explicit <c>TAXI</c> alike) and Ground View
/// omits the corner's fillet arc.
/// </summary>
public sealed class BlockedTurnEntry
{
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("path")]
    public List<OneWayWaypoint> Path { get; set; } = [];
}

/// <summary>
/// A parsed, validated blocked turn produced by <see cref="AirportSidecarLoader"/>. Resolved against a
/// concrete layout into forbidden turn-triples / arc moves and hidden corner arcs by
/// <see cref="Yaat.Sim.Data.Airport.Pathfinding.BlockedTurnResolver"/>.
/// </summary>
public sealed record BlockedTurn(IReadOnlyList<OneWayPoint> Path, string? Notes);

/// <summary>
/// A parsed, validated one-way constraint produced by <see cref="AirportSidecarLoader"/>. The allowed
/// travel direction is the order of <see cref="Path"/>; <see cref="BlockBoth"/> closes the span in both
/// directions. Resolved against a concrete layout into forbidden directed node moves by
/// <see cref="Yaat.Sim.Data.Airport.Pathfinding.OneWayResolver"/>.
/// </summary>
public sealed record OneWayConstraint(IReadOnlyList<OneWayPoint> Path, bool BlockBoth, string? Notes);
