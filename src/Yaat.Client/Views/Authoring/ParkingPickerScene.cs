using System.Collections.Immutable;
using Yaat.Sim;
using Yaat.Sim.Authoring.Gates;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Authoring;

/// <summary>A stand the parking picker offers: a named parking, spot or helipad node of the airport's layout.</summary>
/// <param name="Name">The node's name, as the scenario's parking value names it.</param>
/// <param name="Lat">The stand's latitude.</param>
/// <param name="Lon">The stand's longitude.</param>
/// <param name="Type">The node's type: parking, spot or helipad.</param>
public sealed record ParkingPickerStand(string Name, double Lat, double Lon, GroundNodeType Type);

/// <summary>What a context line of the parking picker is.</summary>
public enum ParkingPickerLineKind
{
    /// <summary>A taxiway centerline segment.</summary>
    Taxiway,

    /// <summary>A runway centerline segment.</summary>
    Runway,
}

/// <summary>One straight segment of a taxiway or runway centerline, drawn for context under the stands.</summary>
/// <param name="Kind">Taxiway or runway.</param>
/// <param name="FromLat">The first end's latitude.</param>
/// <param name="FromLon">The first end's longitude.</param>
/// <param name="ToLat">The second end's latitude.</param>
/// <param name="ToLon">The second end's longitude.</param>
public sealed record ParkingPickerLine(ParkingPickerLineKind Kind, double FromLat, double FromLon, double ToLat, double ToLon);

/// <summary>The box around the picker's stands.</summary>
/// <param name="MinLat">The southern edge.</param>
/// <param name="MaxLat">The northern edge.</param>
/// <param name="MinLon">The western edge.</param>
/// <param name="MaxLon">The eastern edge.</param>
public sealed record ParkingPickerBounds(double MinLat, double MaxLat, double MinLon, double MaxLon);

/// <summary>
/// What the parking picker draws, built once from an airport's ground layout: every named parking, spot and helipad node
/// (the nodes a scenario's parking value resolves to), the taxiway and runway centerlines for context, the box around the
/// stands, and the stand the aircraft parks at now when the layout lists it. Plain data, no UI types.
/// </summary>
/// <param name="Stands">The stands, ordered by name.</param>
/// <param name="Lines">The centerline segments.</param>
/// <param name="Bounds">The box around the stands, or null when there are none.</param>
/// <param name="CurrentStand">The listed stand's name the current parking value names (any case), or null.</param>
/// <param name="Status">
/// Each ranked stand's reason (<see cref="GateRanking.Rank"/>) by trimmed name, any case; empty for a scene with no ranking.
/// </param>
public sealed record ParkingPickerScene(
    ImmutableArray<ParkingPickerStand> Stands,
    ImmutableArray<ParkingPickerLine> Lines,
    ParkingPickerBounds? Bounds,
    string? CurrentStand,
    ImmutableDictionary<string, GateReason> Status
)
{
    /// <summary>Gets the status map with no stand in it.</summary>
    public static ImmutableDictionary<string, GateReason> NoStatus { get; } =
        ImmutableDictionary.Create<string, GateReason>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the scene with nothing in it.</summary>
    public static ParkingPickerScene Empty { get; } = new([], [], null, null, NoStatus);

    /// <summary>Builds the scene from a layout.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="currentParking">The aircraft's parking value now, or null.</param>
    /// <returns>The scene.</returns>
    public static ParkingPickerScene Build(AirportGroundLayout layout, string? currentParking)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ImmutableArray<ParkingPickerStand> stands =
        [
            .. layout
                .Nodes.Values.Where(IsStand)
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Id)
                .Select(n => new ParkingPickerStand(n.Name!, n.Position.Lat, n.Position.Lon, n.Type)),
        ];
        ImmutableArray<ParkingPickerLine> lines = [.. layout.AllEdges.Select(ToLine)];
        string? wanted = currentParking?.Trim();
        string? current = string.IsNullOrEmpty(wanted)
            ? null
            : stands.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Name;
        return new ParkingPickerScene(stands, lines, BoundsOf(stands), current, NoStatus);
    }

    /// <summary>The scene with each ranked stand's reason as its status.</summary>
    /// <param name="ranked">A ranking of the layout's stands (<see cref="GateRanking.Rank"/>).</param>
    /// <returns>The scene with the status map.</returns>
    public ParkingPickerScene WithRanking(IReadOnlyList<GateCandidate> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        return this with { Status = NoStatus.AddRange(ranked.Select(c => KeyValuePair.Create(c.Stand, c.Reason))) };
    }

    /// <summary>A stand's ranked reason.</summary>
    /// <param name="name">The stand's name.</param>
    /// <returns>Its reason, or null when the ranking did not cover it (a spot, a helipad, or no ranking).</returns>
    public GateReason? StatusOf(string? name) => ((name is not null) && Status.TryGetValue(name.Trim(), out GateReason reason)) ? reason : null;

    /// <summary>The stand a name names, trimmed, in any case.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The first such stand, or null.</returns>
    public ParkingPickerStand? FindAnyCase(string? name) =>
        (name is null) ? null : Stands.FirstOrDefault(s => string.Equals(s.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a node is one the picker offers: a parking, spot or helipad node with a name.</summary>
    /// <param name="node">The node.</param>
    /// <returns>True for a named parking, spot or helipad node.</returns>
    public static bool IsStand(GroundNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return (node.Type is GroundNodeType.Parking or GroundNodeType.Spot or GroundNodeType.Helipad) && !string.IsNullOrWhiteSpace(node.Name);
    }

    /// <summary>The stand of a name, matched exactly.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The first stand of that name, or null.</returns>
    public ParkingPickerStand? Find(string? name) =>
        (name is null) ? null : Stands.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    private static ParkingPickerLine ToLine(IGroundEdge edge)
    {
        ParkingPickerLineKind kind = edge.IsRunwayCenterline ? ParkingPickerLineKind.Runway : ParkingPickerLineKind.Taxiway;
        LatLon from = edge.Nodes[0].Position;
        LatLon to = edge.Nodes[1].Position;
        return new ParkingPickerLine(kind, from.Lat, from.Lon, to.Lat, to.Lon);
    }

    private static ParkingPickerBounds? BoundsOf(ImmutableArray<ParkingPickerStand> stands) =>
        stands.IsEmpty
            ? null
            : new ParkingPickerBounds(stands.Min(s => s.Lat), stands.Max(s => s.Lat), stands.Min(s => s.Lon), stands.Max(s => s.Lon));
}
