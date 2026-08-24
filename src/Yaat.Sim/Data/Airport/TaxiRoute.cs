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
    /// The cleared taxiways in order for operator-facing display. Junction/membership arcs
    /// (<c>"D - RAMP"</c>) are transitions between taxiways, not a leg of one, so they never appear as
    /// a named part of the route, and ramp pavement is dropped: a route out of a stand through the
    /// RAMP↔D corner and on down D, C, B reads <c>"D C B"</c>. Drives the Aircraft List Info column
    /// and the DTO TaxiRoute field.
    /// </summary>
    public string FormatTaxiwaySequence() => string.Join(" ", TaxiwaySequence([]).Select(t => t.Display));

    /// <summary>
    /// The cleared taxiways in order, from the shared <see cref="TaxiRouteFormatter.TaxiwayLegs"/>
    /// walk — composite junction labels (<c>"C - E"</c>) decomposed to the taxiway actually being
    /// followed, ramp edges dropped. A runway taxied along is rewritten to its operator-facing end.
    /// </summary>
    private List<(string Display, bool IsRunway)> TaxiwaySequence(IReadOnlyCollection<string> clearedRunways)
    {
        var taxiways = new List<(string, bool)>();
        foreach (var leg in TaxiRouteFormatter.TaxiwayLegs(this))
        {
            taxiways.Add(leg.IsRunway ? (RunwayDisplay(leg.Segment, clearedRunways), true) : (leg.Name, false));
        }

        return taxiways;
    }

    /// <summary>
    /// Operator-facing token for a runway taxied ALONG. The segment carries the internal combined
    /// centerline name (<c>"RWY28R/10L"</c>); show the single FAA end the controller cleared (matched
    /// from <paramref name="clearedRunways"/> — the command path) de-padded to <c>"28R"</c>. With no
    /// command context — a snapshot, the Aircraft List, or a drawn route whose path is all node
    /// references — name the end the aircraft is travelling toward instead.
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
        if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var id = RunwayIdentifier.Parse(name[3..]);
        return RunwayIdentifier.ToDisplayDesignator(EndTravelledToward(seg, id));
    }

    /// <summary>
    /// Which end of a runway the aircraft is taxiing toward. Designators are magnetic and the two ends
    /// are reciprocal, so comparing them against the segment's true bearing is safe — magnetic
    /// variation never approaches the 90° needed to flip the choice. Falls back to
    /// <see cref="RunwayIdentifier.End1"/> when a designator carries no leading number.
    /// </summary>
    private static string EndTravelledToward(TaxiRouteSegment seg, RunwayIdentifier id)
    {
        double travelBearing = GeoMath.BearingTo(seg.Edge.FromNode.Position, seg.Edge.ToNode.Position);
        double? end1 = DesignatorHeading(id.End1);
        double? end2 = DesignatorHeading(id.End2);
        if ((end1 is null) || (end2 is null))
        {
            return id.End1;
        }

        return
            Math.Abs(GeoMath.SignedBearingDifference(end1.Value, travelBearing))
            <= Math.Abs(GeoMath.SignedBearingDifference(end2.Value, travelBearing))
            ? id.End1
            : id.End2;
    }

    /// <summary>Approximate magnetic heading a runway designator encodes ("28R" → 280°); null when it has no number.</summary>
    private static double? DesignatorHeading(string designator)
    {
        int digits = 0;
        foreach (char c in designator)
        {
            if (!char.IsAsciiDigit(c))
            {
                break;
            }

            digits = (digits * 10) + (c - '0');
        }

        return digits is > 0 and <= 36 ? digits * 10.0 : null;
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

        // Emit each explicit hold-short. A runway bar is located with "at (taxiway)" per 7110.65
        // §3-7-2 phraseology, and shows the single commanded end when the command context names one
        // ("HS 33 at F, HS 33 at C" — two distinct stops, never collapsed into one entry; hiding an
        // armed bar from the echo is how a hold-short gets missed). Only true duplicates of the same
        // bar text collapse — a taxiway hold-short annotated at several adjacent nodes still reads
        // "HS B" once.
        string? lastHoldShort = null;
        for (int i = 0; i < HoldShortPoints.Count; i++)
        {
            var hs = HoldShortPoints[i];
            if (hs.Reason != HoldShortReason.ExplicitHoldShort || hs.TargetName is null)
            {
                continue;
            }

            string entry = DisplayHoldShortTarget(hs, clearedRunways);
            if (string.Equals(entry, lastHoldShort, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lastHoldShort is not null)
            {
                parts[^1] += ",";
            }

            parts.Add("HS");
            parts.Add(entry);
            lastHoldShort = entry;
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

    /// <summary>
    /// Operator-facing text for one explicit hold-short. A runway bar shows the single commanded
    /// end when the command's path names one (else the combined pair) and is located with
    /// "at (taxiway)" so two bars for the same runway on one route read as the two distinct stops
    /// they are. A taxiway hold-short stays the bare name.
    /// </summary>
    private string DisplayHoldShortTarget(HoldShortPoint hs, IReadOnlyCollection<string> clearedRunways)
    {
        string target = hs.TargetName!;
        var node = FindRouteNode(hs.NodeId);
        if (node is null || node.Type != GroundNodeType.RunwayHoldShort)
        {
            return target;
        }

        string display = target;
        var id = RunwayIdentifier.Parse(target);
        foreach (string token in clearedRunways)
        {
            if (id.Contains(token))
            {
                display = RunwayIdentifier.ToDisplayDesignator(token);
                break;
            }
        }

        foreach (var edge in node.Edges)
        {
            string name = edge.TaxiwayName;
            if (edge is GroundArc arc)
            {
                string? arcName = Array.Find(arc.TaxiwayNames, n => !IsRunwayOrRampName(n));
                if (arcName is null)
                {
                    continue;
                }

                name = arcName;
            }

            if (!IsRunwayOrRampName(name))
            {
                return $"{display} at {name}";
            }
        }

        return display;
    }

    private static bool IsRunwayOrRampName(string name) =>
        name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase);

    /// <summary>The route's node instance for <paramref name="nodeId"/>, or null when no segment touches it.</summary>
    private GroundNode? FindRouteNode(int nodeId)
    {
        foreach (var seg in Segments)
        {
            if (seg.FromNodeId == nodeId)
            {
                return seg.Edge.FromNode;
            }

            if (seg.ToNodeId == nodeId)
            {
                return seg.Edge.ToNode;
            }
        }

        return null;
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
                    FromLatitude = s.FromNodeId < 0 ? s.Edge.FromNode.Position.Lat : null,
                    FromLongitude = s.FromNodeId < 0 ? s.Edge.FromNode.Position.Lon : null,
                    ToLatitude = s.ToNodeId < 0 ? s.Edge.ToNode.Position.Lat : null,
                    ToLongitude = s.ToNodeId < 0 ? s.Edge.ToNode.Position.Lon : null,
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

    /// <summary>
    /// A snapshot segment endpoint: the layout node by id, or — for a virtual node the layout never held —
    /// a fresh <see cref="VirtualNode"/> at the recorded position. Null when neither is available.
    /// </summary>
    private static GroundNode? ResolveSnapshotNode(AirportGroundLayout layout, int nodeId, double? latitude, double? longitude)
    {
        if (layout.Nodes.TryGetValue(nodeId, out var node))
        {
            return node;
        }

        return latitude is { } lat && longitude is { } lon ? VirtualNode.Create(lat, lon) : null;
    }

    public static TaxiRoute? FromSnapshot(TaxiRouteDto dto, AirportGroundLayout? layout)
    {
        if (layout is null)
        {
            return null;
        }

        var segments = new List<TaxiRouteSegment>();
        foreach (var seg in dto.Segments)
        {
            var fromNode = ResolveSnapshotNode(layout, seg.FromNodeId, seg.FromLatitude, seg.FromLongitude);
            var toNode = ResolveSnapshotNode(layout, seg.ToNodeId, seg.ToLatitude, seg.ToLongitude);
            if (fromNode is null || toNode is null)
            {
                return null;
            }

            // A free-space leg (ramp-lane cut) has no layout edge; rebuild the virtual one from its endpoints.
            if (fromNode.Id < 0 || toNode.Id < 0)
            {
                segments.Add(VirtualNode.CreateSegment(fromNode, toNode, seg.TaxiwayName ?? ""));
                continue;
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
