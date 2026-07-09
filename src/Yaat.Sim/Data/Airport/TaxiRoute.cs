using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A resolved taxi route: an ordered sequence of segments with hold-short points.
/// </summary>
public sealed class TaxiRoute
{
    public required List<TaxiRouteSegment> Segments { get; init; }
    public required List<HoldShortPoint> HoldShortPoints { get; init; }
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// Number of mandatory connector insertions the resolver had to bridge between cleared taxiways
    /// that shared no direct junction (the "X and Y do not connect directly — taxi via Z" case). A
    /// route that honors the clearance without any blind detour has 0; used by
    /// <see cref="Pathfinding.SegmentExpander.Run"/> to prefer a clearance-honoring variant (e.g. one
    /// threaded through a curated connector) over a shorter route that had to blind-detour.
    /// </summary>
    public int MandatoryConnectorCount { get; init; }

    /// <summary>Parking destination name (@ prefix), if any.</summary>
    public string? DestinationParking { get; init; }

    /// <summary>Spot destination name ($ prefix), if any.</summary>
    public string? DestinationSpot { get; init; }

    public double TotalDistanceNm => Segments.Sum(s => s.Edge.DistanceNm);

    /// <summary>
    /// The cleared taxiways in order for operator-facing display — distinct consecutive segment
    /// names with junction/membership arcs (<c>"D - RAMP"</c>) excluded. Those arcs are transitions
    /// between taxiways, not a leg of one, so they must never appear as a named part of the route:
    /// a route through RAMP, the RAMP↔D corner, then D C B reads <c>"RAMP D C B"</c>, not
    /// <c>"RAMP D - RAMP D C B"</c>. Drives the Aircraft List Info column and the DTO TaxiRoute field.
    /// </summary>
    public string FormatTaxiwaySequence() => string.Join(" ", TaxiwaySequence([]).Select(t => t.Display));

    /// <summary>
    /// The cleared taxiways in order: distinct consecutive segment names with junction/membership
    /// arcs (<c>"C - E"</c>) excluded. Shared by <see cref="FormatTaxiwaySequence"/> and
    /// <see cref="ToSummary"/> so both render the same sequence — an arc is a transition between
    /// taxiways, not a leg of one, and must never surface as a named token.
    /// </summary>
    private List<(string Display, bool IsRunway)> TaxiwaySequence(IReadOnlyCollection<string> clearedRunways)
    {
        var taxiways = new List<(string, bool)>();
        string? lastRaw = null;
        foreach (var seg in Segments)
        {
            if (seg.Edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            if (seg.TaxiwayName == lastRaw)
            {
                continue;
            }

            lastRaw = seg.TaxiwayName;
            taxiways.Add(seg.Edge.Edge.IsRunwayCenterline ? (RunwayDisplay(seg, clearedRunways), true) : (seg.TaxiwayName, false));
        }

        return taxiways;
    }

    /// <summary>
    /// Operator-facing token for a runway taxied ALONG. The segment carries the internal combined
    /// centerline name (<c>"RWY28R/10L"</c>); show the single FAA end the controller cleared (matched
    /// from <paramref name="clearedRunways"/> — the command path) de-padded to <c>"28R"</c>. With no
    /// command context (snapshot/Aircraft List), fall back to the de-padded combined id (<c>"28R/10L"</c>).
    /// </summary>
    private static string RunwayDisplay(TaxiRouteSegment seg, IReadOnlyCollection<string> clearedRunways)
    {
        foreach (string designator in clearedRunways)
        {
            if (seg.Edge.Edge.MatchesRunway(designator))
            {
                return RunwayIdentifier.ToDisplayDesignator(designator);
            }
        }

        string name = seg.TaxiwayName;
        return name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) ? RunwayIdentifier.ToDisplayDesignator(name[3..]) : name;
    }

    /// <summary>
    /// Returns a shallow copy of this route truncated to end at the segment whose
    /// ToNodeId matches <paramref name="nodeId"/>. If the node is not found, returns this route.
    /// </summary>
    public TaxiRoute TruncateAt(int nodeId)
    {
        for (int i = 0; i < Segments.Count; i++)
        {
            if (Segments[i].ToNodeId == nodeId)
            {
                return new TaxiRoute
                {
                    Segments = Segments.Take(i + 1).ToList(),
                    HoldShortPoints = HoldShortPoints.Where(hs => Segments.Take(i + 1).Any(s => s.ToNodeId == hs.NodeId)).ToList(),
                    Warnings = Warnings,
                };
            }
        }

        return this;
    }

    /// <summary>Current segment index being traversed.</summary>
    public int CurrentSegmentIndex { get; set; }

    public TaxiRouteSegment? CurrentSegment =>
        CurrentSegmentIndex >= 0 && CurrentSegmentIndex < Segments.Count ? Segments[CurrentSegmentIndex] : null;

    public bool IsComplete => CurrentSegmentIndex >= Segments.Count;

    /// <summary>
    /// Check if the given node is a hold-short point in this route.
    /// </summary>
    public HoldShortPoint? GetHoldShortAt(int nodeId)
    {
        foreach (var hs in HoldShortPoints)
        {
            if (hs.NodeId == nodeId)
            {
                return hs;
            }
        }

        return null;
    }

    /// <summary>
    /// Build a human-readable taxi route summary (e.g., "S T U W W1 HS 28L, RWY 30").
    /// </summary>
    public string ToSummary() => ToSummary(null, []);

    public string ToSummary(IReadOnlyDictionary<string, TurnDirection>? turnHints) => ToSummary(turnHints, []);

    /// <summary>
    /// Build a human-readable taxi route summary (e.g., "S T U W W1 HS 28L, RWY 30"). When
    /// <paramref name="turnHints"/> is supplied (keyed by taxiway name), a cleared taxiway the
    /// controller prefixed with a turn glyph (<c>&gt;A</c> / <c>&lt;C</c>) renders as "right on A" /
    /// "left on C" — matching the pilot readback — so the controller's echo confirms the requested turn.
    /// A runway taxied ALONG renders as "on 28R" (7110.65 §3-7-2.a "ON (runway)"), with the single cleared
    /// end resolved from <paramref name="clearedRunways"/> (pass the command's taxi path; non-runway tokens
    /// are ignored).
    /// </summary>
    public string ToSummary(IReadOnlyDictionary<string, TurnDirection>? turnHints, IReadOnlyCollection<string> clearedRunways)
    {
        var parts = new List<string>();
        foreach (var (twy, isRunway) in TaxiwaySequence(clearedRunways))
        {
            if (isRunway)
            {
                parts.Add($"on {twy}");
            }
            else
            {
                parts.Add(
                    turnHints is not null && turnHints.TryGetValue(twy, out var dir)
                        ? $"{(dir == TurnDirection.Left ? "left" : "right")} on {twy}"
                        : twy
                );
            }
        }

        // Emit each explicit hold-short once; collapse consecutive duplicates of the same target
        // so a route that touches one taxiway hold-short at several nodes reads "HS B", not
        // "HS B HS B HS B". The source annotator already de-duplicates; this is a display backstop.
        string? lastHoldShort = null;
        foreach (var hs in HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.ExplicitHoldShort && hs.TargetName is not null)
            {
                if (string.Equals(hs.TargetName, lastHoldShort, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add("HS");
                parts.Add(hs.TargetName);
                lastHoldShort = hs.TargetName;
            }
        }

        // Append destination runway assignment
        foreach (var hs in HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.DestinationRunway && hs.TargetName is not null)
            {
                parts.Add("RWY");
                parts.Add(hs.TargetName);
                break;
            }
        }

        // Append parking or spot destination
        if (DestinationParking is not null)
        {
            parts.Add($"@{DestinationParking}");
        }
        else if (DestinationSpot is not null)
        {
            parts.Add($"${DestinationSpot}");
        }

        return string.Join(" ", parts);
    }

    public TaxiRouteDto ToSnapshot() =>
        new()
        {
            Segments = Segments
                .Select(s => new TaxiSegmentDto
                {
                    FromNodeId = s.FromNodeId,
                    ToNodeId = s.ToNodeId,
                    TaxiwayName = s.TaxiwayName,
                })
                .ToList(),
            CurrentSegmentIndex = CurrentSegmentIndex,
            HoldShortPoints = HoldShortPoints
                .Select(hs => new HoldShortPointDto
                {
                    NodeId = hs.NodeId,
                    RunwayId = hs.TargetName ?? "",
                    IsSatisfied = hs.IsCleared,
                    Latitude = hs.Latitude,
                    Longitude = hs.Longitude,
                    Reason = hs.Reason,
                    ClearedByAutoCross = hs.ClearedByAutoCross,
                    TailOverRunwayNodeId = hs.TailOverRunwayNodeId,
                })
                .ToList(),
            Description = ToSummary(),
        };

    public static TaxiRoute? FromSnapshot(TaxiRouteDto dto, AirportGroundLayout? layout)
    {
        if (layout is null)
        {
            return null;
        }

        var segments = new List<TaxiRouteSegment>();
        foreach (var seg in dto.Segments)
        {
            if (!layout.Nodes.TryGetValue(seg.FromNodeId, out var fromNode))
            {
                return null;
            }

            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out var toNode))
            {
                return null;
            }

            IGroundEdge? edge = null;
            foreach (var e in fromNode.Edges)
            {
                if (e.HasNode(seg.ToNodeId))
                {
                    edge = e;
                    break;
                }
            }

            if (edge is null)
            {
                return null;
            }

            segments.Add(new TaxiRouteSegment { TaxiwayName = seg.TaxiwayName ?? edge.TaxiwayName, Edge = edge.Directed(fromNode, toNode) });
        }

        var holdShorts = new List<HoldShortPoint>();
        if (dto.HoldShortPoints is not null)
        {
            foreach (var hs in dto.HoldShortPoints)
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = hs.NodeId,
                        Reason = hs.Reason ?? HoldShortReason.ExplicitHoldShort,
                        TargetName = hs.RunwayId,
                        IsCleared = hs.IsSatisfied,
                        ClearedByAutoCross = hs.ClearedByAutoCross,
                        Latitude = hs.Latitude,
                        Longitude = hs.Longitude,
                        TailOverRunwayNodeId = hs.TailOverRunwayNodeId,
                    }
                );
            }
        }

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            CurrentSegmentIndex = dto.CurrentSegmentIndex,
        };
    }
}

public sealed class TaxiRouteSegment
{
    public required DirectionalEdge Edge { get; init; }
    public required string TaxiwayName { get; init; }

    public int FromNodeId => Edge.FromNodeId;
    public int ToNodeId => Edge.ToNodeId;
}

public enum HoldShortReason
{
    RunwayCrossing,
    ExplicitHoldShort,
    DestinationRunway,
}

public sealed class HoldShortPoint
{
    public required int NodeId { get; init; }
    public required HoldShortReason Reason { get; set; }

    /// <summary>Runway ID or taxiway name this hold-short protects.</summary>
    public string? TargetName { get; init; }

    /// <summary>Whether this hold-short has been cleared (e.g., CROSS command issued).</summary>
    public bool IsCleared { get; set; }

    /// <summary>
    /// True when <see cref="IsCleared"/> was set by the AutoCrossRunway scenario toggle
    /// (either at TAXI-resolution time or via a mid-session toggle that re-evaluated
    /// already-active routes). Distinguishes AutoCross-driven clearance from other
    /// sources (first-crossing-resume, explicit CROSS keyword, future user CTO commands)
    /// so toggling AutoCross OFF only reverts the clearances it owns.
    /// </summary>
    public bool ClearedByAutoCross { get; set; }

    /// <summary>
    /// Computed hold-short position. For taxiway hold-shorts, this is offset from the
    /// intersection node by the aircraft's fuselage length + buffer. For runway hold-shorts,
    /// this is the node position itself. Null when not yet computed (legacy snapshots).
    /// </summary>
    public double? Latitude { get; set; }

    /// <summary>
    /// Computed hold-short position longitude. See <see cref="Latitude"/>.
    /// </summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// When this taxiway hold-short sits within a fuselage length past a runway the route crosses, the
    /// aircraft holds at the taxiway line with its tail over the runway's hold-short bars and cannot
    /// fully clear the runway (issue #172). This is the runway hold-short node the tail hangs over; the
    /// runway is "not clear" while the aircraft holds here. Null in the normal case.
    /// </summary>
    public int? TailOverRunwayNodeId { get; set; }
}
