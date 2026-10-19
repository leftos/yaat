using System.Text.Json.Serialization;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Serializable representation of a resolved taxi route.
/// Node IDs reference the ground layout graph; on restore, the route
/// is re-resolved from the loaded ground layout.
/// </summary>
public sealed class TaxiRouteDto
{
    public required List<TaxiSegmentDto> Segments { get; init; }
    public required int CurrentSegmentIndex { get; init; }
    public List<HoldShortPointDto>? HoldShortPoints { get; init; }
    public string? Description { get; init; }
    public int? DestinationNodeId { get; init; }
    public string? DestinationParking { get; init; }
    public string? DestinationSpot { get; init; }

    /// <summary>
    /// The first segment of a spot line-up's slow pull up the lane (<c>TaxiRoute.SpotLineUpPullFromSegment</c>).
    /// Null on every other route and on older snapshots, which never held a line-up — additive, no schema bump.
    /// </summary>
    public int? SpotLineUpPullFromSegment { get; init; }

    /// <summary>
    /// How the route turns the aircraft about on the taxiway it stood on (<c>TaxiRoute.TurnAboutShape</c>).
    /// <see cref="TaxiTurnAboutShape.None"/> on every other route and on older snapshots, including one that carries the
    /// earlier <c>StartsWithTurnAbout</c> flag, which is not read — additive, no schema bump.
    /// </summary>
    public TaxiTurnAboutShape TurnAboutShape { get; init; }

    /// <summary>
    /// The node the turn about turns the aircraft toward (<c>TaxiRoute.TurnAboutTargetNodeId</c>). Null on a route without
    /// a turn about and on older snapshots.
    /// </summary>
    public int? TurnAboutTargetNodeId { get; init; }
}

public sealed class TaxiSegmentDto
{
    public required int FromNodeId { get; init; }
    public required int ToNodeId { get; init; }
    public string? TaxiwayName { get; init; }

    /// <summary>
    /// A free-space leg between two layout nodes the graph does not join — a destination-end ramp cut
    /// (issue #400). Restore rebuilds the virtual edge instead of looking one up; false (and absent on older
    /// snapshots) means the segment is a layout edge, and a missing edge still voids the route so a snapshot
    /// whose node ids no longer match the layout is dropped rather than turned into stray legs.
    /// </summary>
    public bool IsFreeSpace { get; init; }

    /// <summary>
    /// Position of a <see cref="Yaat.Sim.Data.Airport.VirtualNode"/> endpoint — a free-space leg such as a
    /// ramp-lane cut whose node is not in the ground layout. Null for graph nodes and on older snapshots;
    /// restore then resolves the endpoint by id.
    /// </summary>
    public double? FromLatitude { get; init; }
    public double? FromLongitude { get; init; }
    public double? ToLatitude { get; init; }
    public double? ToLongitude { get; init; }
}

public sealed class HoldShortPointDto
{
    public required int NodeId { get; init; }
    public required string RunwayId { get; init; }
    public required bool IsSatisfied { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>
    /// Why the route holds short here (destination runway / runway crossing / explicit HSC).
    /// Null on legacy snapshots (schema &lt; 12); restore then falls back to ExplicitHoldShort,
    /// the value those snapshots were reconstructed with before this field existed.
    /// </summary>
    public HoldShortReason? Reason { get; init; }

    /// <summary>
    /// Tracks whether <see cref="IsSatisfied"/> was driven by the AutoCrossRunway
    /// scenario toggle. Defaults to false on legacy snapshots (schema &lt; 7), which
    /// is correct: pre-feature recordings had no notion of AutoCross-attributed
    /// clearance, so a subsequent toggle-OFF on replay must not revert their
    /// hold-shorts.
    /// </summary>
    public bool ClearedByAutoCross { get; init; }

    /// <summary>
    /// True at a re-crossing of the runway just landed on, on a route stored during the landing rollout, which auto-cross never
    /// clears (<see cref="HoldShortPoint.RecrossesLandingRunway"/>). Null otherwise and on older snapshots, and not written while
    /// null, so every other bar — and every snapshot 0 — serializes as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RecrossesLandingRunway { get; init; }

    /// <summary>
    /// True when a <c>CROSS … EXP</c> expedited this crossing (<see cref="HoldShortPoint.IsExpedited"/>). Null otherwise and on older
    /// snapshots, and not written while null, so every other bar — and every snapshot 0 — serializes as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsExpedited { get; init; }

    /// <summary>
    /// Runway hold-short node the aircraft's tail hangs over while holding short of this taxiway
    /// (issue #172 tail-over-runway state). Null in the normal case and on legacy snapshots.
    /// </summary>
    public int? TailOverRunwayNodeId { get; init; }

    /// <summary>
    /// True when the bar could not be made when it was armed, so <see cref="Latitude"/>/<see cref="Longitude"/>
    /// hold the stop the aircraft can actually reach rather than the painted line. Defaults to false on legacy
    /// snapshots, which is correct: those routes carried only makeable bars.
    /// </summary>
    public bool Unable { get; init; }

    /// <summary>
    /// True once the taxi phase has moved this <see cref="Unable"/> bar's stop forward, so a restored taxi does not move
    /// it again. Null (absent) when it has not, and on legacy snapshots.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StopMoved { get; init; }

    /// <summary>
    /// The taxiway a shallow-merge hold holds on (<see cref="HoldShortPoint.MergeHold"/>); null when the bar has no merge
    /// hold, and on older snapshots, and not written while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MergeHoldOnTaxiway { get; init; }

    /// <summary>
    /// The taxiway a shallow-merge hold holds before (<see cref="HoldShortPoint.MergeHold"/>); null as
    /// <see cref="MergeHoldOnTaxiway"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MergeHoldBeforeTaxiway { get; init; }

    /// <summary>
    /// True when the merge hold was declined at arming (<see cref="HoldShortPoint.MergeHoldDeclined"/>); null otherwise and on
    /// older snapshots.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MergeHoldDeclined { get; init; }
}
