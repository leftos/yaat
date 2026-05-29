using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

public enum GroundNodeType
{
    TaxiwayIntersection,
    Parking,
    Spot,
    RunwayHoldShort,
    Helipad,
}

public sealed class GroundNode
{
    public required int Id { get; init; }

    /// <summary>Geographic position of the node.</summary>
    public required LatLon Position { get; init; }

    public required GroundNodeType Type { get; set; }
    public string? Name { get; init; }

    /// <summary>
    /// Parking heading (nose-in direction, degrees true). Only set for Parking nodes.
    /// </summary>
    public TrueHeading? TrueHeading { get; init; }

    /// <summary>
    /// Runway ID that this hold-short node protects. Only set for RunwayHoldShort nodes.
    /// </summary>
    public RunwayIdentifier? RunwayId { get; set; }

    /// <summary>
    /// Adjacent edges for graph traversal. Populated during layout construction.
    /// Not serialized — rebuilt by <see cref="AirportGroundLayout.RebuildAdjacencyLists"/> after deserialization.
    /// </summary>
    [JsonIgnore]
    public List<IGroundEdge> Edges { get; init; } = [];

    /// <summary>
    /// Diagnostic provenance: which code path created or last modified this node.
    /// Not serialized — only populated during layout construction for debugging.
    /// </summary>
    [JsonIgnore]
    public string? Origin { get; set; }

    /// <summary>
    /// Typed fillet-pipeline provenance for nodes created by
    /// <see cref="FilletArcGenerator"/>. Null for non-fillet nodes. Cleanup passes
    /// pattern-match on the concrete record type instead of parsing
    /// <see cref="Origin"/>. Not serialized.
    /// </summary>
    [JsonIgnore]
    public FilletProvenance? FilletProvenance { get; set; }

    /// <summary>
    /// For tangent-point nodes created by <see cref="FilletArcGenerator"/>: the position of
    /// the intersection node this tangent was created for. Used during coincident-node merging
    /// to position the merged node at the midpoint between two source intersections.
    /// Null for non-tangent nodes.
    /// </summary>
    [JsonIgnore]
    public (double Lat, double Lon)? SourceIntersectionPosition { get; set; }
}

/// <summary>
/// Common interface for ground graph edges — straight lines and circular arcs.
/// Both <see cref="GroundEdge"/> and <see cref="GroundArc"/> implement this.
/// </summary>
public interface IGroundEdge
{
    /// <summary>The two endpoint nodes. Fixed-size 2, no implied direction.</summary>
    GroundNode[] Nodes { get; }
    string TaxiwayName { get; }
    double DistanceNm { get; }

    /// <summary>
    /// Returns true if this edge belongs to the given taxiway.
    /// For <see cref="GroundArc"/>s at junctions this also checks the secondary taxiway name.
    /// </summary>
    bool MatchesTaxiway(string name);

    /// <summary>
    /// True if this edge is a runway centerline segment — an edge that exists purely
    /// on the runway surface. For straight edges: TaxiwayName starts with "RWY".
    /// For arcs: true only if ALL taxiway names are RWY (same-taxiway runway arc).
    /// Junction arcs between a runway and a taxiway return false — they are transitions,
    /// not centerline segments.
    /// </summary>
    bool IsRunwayCenterline { get; }

    /// <summary>
    /// Returns true if this edge is a runway edge for the given designator.
    /// Runway edge names are "RWY{end1}/{end2}" (e.g., "RWY10L/28R");
    /// this checks if <paramref name="designator"/> matches either end.
    /// </summary>
    bool MatchesRunway(string designator);

    /// <summary>True if this edge is a ramp connection (TaxiwayName is "RAMP").</summary>
    bool IsRamp { get; }

    /// <summary>
    /// Maximum safe speed (kts) for traversing this edge given an aircraft ground turn rate.
    /// Straight edges return <see cref="double.MaxValue"/>; arcs compute from radius and turn rate.
    /// </summary>
    double MaxSafeSpeedKts(double turnRateDegPerSec);

    /// <summary>
    /// Returns true if this edge shares any taxiway name with <paramref name="other"/>.
    /// W overlaps W/W3 → true. Use for "could these be part of the same route?" checks.
    /// </summary>
    bool SharesTaxiway(IGroundEdge other);

    /// <summary>
    /// Returns true if this edge has the exact same taxiway identity as <paramref name="other"/>.
    /// W == W → true, W != W/W3 → false. Use for "is this the same taxiway continuing?" checks.
    /// </summary>
    bool SameTaxiway(IGroundEdge other);

    GroundNode OtherNode(GroundNode node);
    int OtherNodeId(int nodeId);
    bool HasNode(int nodeId);

    /// <summary>
    /// Diagnostic provenance: which code path created or last modified this edge/arc.
    /// Not serialized — only populated during layout construction for debugging.
    /// </summary>
    string? Origin { get; set; }

    /// <summary>
    /// Typed fillet-pipeline provenance for edges/arcs created by
    /// <see cref="FilletArcGenerator"/>. Null for non-fillet elements. Cleanup
    /// passes pattern-match on the concrete record type instead of parsing
    /// <see cref="Origin"/>. Not serialized.
    /// </summary>
    FilletProvenance? FilletProvenance { get; set; }

    /// <summary>
    /// Create a <see cref="DirectionalEdge"/> capturing a specific traversal direction.
    /// </summary>
    DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode);

    /// <summary>
    /// Checks if a runway edge name (e.g., "RWY10L/28R") contains the given designator
    /// as an exact segment match. Strips the "RWY" prefix, splits by "/", and checks each part.
    /// </summary>
    static bool RunwayNameContainsDesignator(string rwyEdgeName, string designator)
    {
        // "RWY10L/28R" → "10L/28R" → ["10L", "28R"]
        ReadOnlySpan<char> name = rwyEdgeName.AsSpan();
        if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
        {
            name = name[3..];
        }

        foreach (var part in name.Split('/'))
        {
            if (name[part].Equals(designator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A non-directional straight edge in the airport ground graph connecting two nodes.
/// <c>Nodes[0]</c> and <c>Nodes[1]</c> are the two endpoints — no implied direction.
/// For directional traversal (routes, navigation), wrap via <see cref="Directed"/>.
/// </summary>
public sealed class GroundEdge : IGroundEdge
{
    /// <summary>The two endpoint nodes. Fixed-size 2, no implied direction.</summary>
    public required GroundNode[] Nodes { get; init; }
    public required string TaxiwayName { get; init; }
    public required double DistanceNm { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string? Origin { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public FilletProvenance? FilletProvenance { get; set; }

    /// <summary>
    /// Intermediate coordinates along this edge (lat, lon pairs) for curved paths.
    /// Does NOT include endpoint node positions — those are looked up from <see cref="Nodes"/>.
    /// </summary>
    public List<(double Lat, double Lon)> IntermediatePoints { get; init; } = [];

    public bool MatchesTaxiway(string name) => string.Equals(TaxiwayName, name, StringComparison.OrdinalIgnoreCase);

    public double MaxSafeSpeedKts(double turnRateDegPerSec) => double.MaxValue;

    public bool SharesTaxiway(IGroundEdge other) => other.MatchesTaxiway(TaxiwayName);

    public bool SameTaxiway(IGroundEdge other) =>
        other switch
        {
            GroundEdge e => string.Equals(TaxiwayName, e.TaxiwayName, StringComparison.OrdinalIgnoreCase),
            GroundArc { TaxiwayNames.Length: 1 } a => string.Equals(TaxiwayName, a.TaxiwayNames[0], StringComparison.OrdinalIgnoreCase),
            _ => false, // junction arc has multiple names — never "same" as a single-name edge
        };

    public bool IsRunwayCenterline => TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !TaxiwayName.Contains(":link");

    /// <summary>
    /// True if this edge is a runway-crossing connector (<c>RWY…:link</c>) joining a taxiway
    /// hold-short representative to the runway centerline. These are connectivity artifacts, not
    /// taxi corners — fillet must ignore them (never curve a taxiway onto a runway crossing link).
    /// </summary>
    public bool IsRunwayCrossingLink => TaxiwayName.Contains(":link", StringComparison.OrdinalIgnoreCase);

    public bool MatchesRunway(string designator) => IsRunwayCenterline && IGroundEdge.RunwayNameContainsDesignator(TaxiwayName, designator);

    public bool IsRamp => string.Equals(TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase);

    public GroundNode OtherNode(GroundNode node) => Nodes[0].Id == node.Id ? Nodes[1] : Nodes[0];

    public int OtherNodeId(int nodeId) => Nodes[0].Id == nodeId ? Nodes[1].Id : Nodes[0].Id;

    public bool HasNode(int nodeId) => Nodes[0].Id == nodeId || Nodes[1].Id == nodeId;

    public DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode) =>
        new()
        {
            Edge = this,
            FromNode = fromNode,
            ToNode = toNode,
        };
}

/// <summary>
/// A circular arc edge connecting two tangent-point nodes at a filleted intersection.
/// Bidirectional — can be traversed in either direction. The navigator follows
/// the curve using lookahead-based path tracking rather than point-to-point steering.
/// <para>
/// The arc is always the minor arc (shorter path around the circle, ≤180°).
/// Sweep direction is determined at traversal time by which node is "from" vs "to".
/// Start/end angles are derived from <c>BearingTo(Center, Node)</c> — not stored.
/// </para>
/// </summary>
public sealed class GroundArc : IGroundEdge
{
    public required GroundNode[] Nodes { get; init; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string? Origin { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public FilletProvenance? FilletProvenance { get; set; }

    /// <summary>
    /// Bezier control points P1 and P2. P0 = Nodes[0].Lat/Lon, P3 = Nodes[1].Lat/Lon.
    /// P1 lies along edge-A direction from P0; P2 lies along edge-B direction from P3.
    /// </summary>
    public required double P1Lat { get; set; }
    public required double P1Lon { get; set; }
    public required double P2Lat { get; set; }
    public required double P2Lon { get; set; }

    /// <summary>
    /// Tightest radius of curvature along the bezier, precomputed at construction time.
    /// Used for worst-case speed constraint back-propagation.
    /// </summary>
    public required double MinRadiusOfCurvatureFt { get; set; }

    // --- Fillet construction parameters (non-serialized) ---
    // Stored so that later passes (e.g., MergeCoincidentNodes) can recompute P1/P2
    // from the new node positions instead of translating stale control points.

    /// <summary>
    /// Bearing (degrees true) from Nodes[0] toward the fillet intersection center.
    /// This is the direction P1 was projected along during construction. May differ
    /// from the simple reverse of the outbound edge bearing when the tangent point
    /// was placed past shape-point nodes during the taxiway walk.
    /// </summary>
    [JsonIgnore]
    public double EdgeBearingAtNode0Deg { get; set; }

    /// <summary>
    /// Bearing (degrees true) from Nodes[1] toward the fillet intersection center.
    /// This is the direction P2 was projected along during construction.
    /// </summary>
    [JsonIgnore]
    public double EdgeBearingAtNode1Deg { get; set; }

    /// <summary>
    /// Turn angle (degrees) between the two edges that this arc bridges.
    /// Used with kappa = (4/3) * tan(sweep/4) to compute control point depth.
    /// </summary>
    [JsonIgnore]
    public double TurnAngleDeg { get; set; }

    public required double DistanceNm { get; set; }

    /// <summary>
    /// The taxiway(s) this arc belongs to. Length 1 when both edges share the same name,
    /// length 2 at a junction between different taxiways (e.g., ["W", "W3"]).
    /// No implied precedence — the arc belongs equally to both.
    /// </summary>
    public required string[] TaxiwayNames { get; init; }

    /// <summary>
    /// Display name for the arc: single name for same-taxiway arcs, "W - W3" for junctions.
    /// Uses " - " separator to avoid collision with "/" in runway identifiers (e.g., "RWY30/12").
    /// For membership checks, use <see cref="MatchesTaxiway"/> instead.
    /// </summary>
    public string TaxiwayName => TaxiwayNames.Length == 1 ? TaxiwayNames[0] : string.Join(" - ", TaxiwayNames);

    public bool MatchesTaxiway(string name)
    {
        foreach (string twName in TaxiwayNames)
        {
            if (string.Equals(twName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Construct a <see cref="CubicBezier"/> from this arc's control points and node positions.
    /// </summary>
    public CubicBezier ToBezier() =>
        new(Nodes[0].Position.Lat, Nodes[0].Position.Lon, P1Lat, P1Lon, P2Lat, P2Lon, Nodes[1].Position.Lat, Nodes[1].Position.Lon);

    public double MaxSafeSpeedKts(double turnRateDegPerSec)
    {
        double turnRateRadSec = turnRateDegPerSec * (Math.PI / 180.0);
        double radiusNm = MinRadiusOfCurvatureFt / GeoMath.FeetPerNm;
        return turnRateRadSec * radiusNm * 3600.0;
    }

    public bool SharesTaxiway(IGroundEdge other)
    {
        foreach (string twName in TaxiwayNames)
        {
            if (other.MatchesTaxiway(twName))
            {
                return true;
            }
        }

        return false;
    }

    public bool SameTaxiway(IGroundEdge other)
    {
        if (other is GroundArc otherArc)
        {
            // Same if name sets are identical
            if (TaxiwayNames.Length != otherArc.TaxiwayNames.Length)
            {
                return false;
            }

            foreach (string name in TaxiwayNames)
            {
                if (!otherArc.MatchesTaxiway(name))
                {
                    return false;
                }
            }

            return true;
        }

        // Arc vs straight edge: same only if the arc has a single name that matches
        return TaxiwayNames.Length == 1 && other.MatchesTaxiway(TaxiwayNames[0]);
    }

    /// <summary>
    /// Always false — a runway centerline is straight by definition. Arcs are never
    /// centerline segments; they are either taxiway junctions or same-taxiway curves.
    /// </summary>
    public bool IsRunwayCenterline => false;

    /// <summary>
    /// True if this arc connects a runway edge to a taxiway edge — the transition
    /// between the runway surface and a taxiway. Exactly one name is RWY, at least one is not.
    /// </summary>
    public bool IsRunwayJunction
    {
        get
        {
            if (TaxiwayNames.Length < 2)
            {
                return false;
            }

            bool hasRwy = false;
            bool hasNonRwy = false;
            foreach (string name in TaxiwayNames)
            {
                if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    hasRwy = true;
                }
                else
                {
                    hasNonRwy = true;
                }
            }

            return hasRwy && hasNonRwy;
        }
    }

    /// <summary>
    /// True if this is a membership junction arc between two TAXIWAYS (e.g. "A - Q1", "A - RAMP")
    /// — a turn OFF the current taxiway onto a crossing one, not a continuation of it. Excludes
    /// runway-crossing arcs (<see cref="IsRunwayJunction"/>, e.g. "H - RWY01L/19R"), which DO
    /// continue the taxiway across a runway. Used by requirement ① to rank a single-name
    /// continuation above such an arc when walking a named taxiway.
    /// </summary>
    public bool IsMembershipTaxiwayJunctionArc => TaxiwayNames.Length >= 2 && !IsRunwayJunction;

    public bool MatchesRunway(string designator)
    {
        foreach (string name in TaxiwayNames)
        {
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && IGroundEdge.RunwayNameContainsDesignator(name, designator))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsRamp => string.Equals(TaxiwayNames[0], "RAMP", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the first non-runway taxiway name from this arc.
    /// E.g., for TaxiwayNames = ["G", "RWY28R/10L"], returns "G".
    /// Falls back to TaxiwayName if all names are runway names.
    /// </summary>
    public string FirstNonRunwayName()
    {
        foreach (string name in TaxiwayNames)
        {
            if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return TaxiwayName;
    }

    public GroundNode OtherNode(GroundNode node) => Nodes[0].Id == node.Id ? Nodes[1] : Nodes[0];

    public int OtherNodeId(int nodeId) => Nodes[0].Id == nodeId ? Nodes[1].Id : Nodes[0].Id;

    public bool HasNode(int nodeId) => Nodes[0].Id == nodeId || Nodes[1].Id == nodeId;

    public DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode) =>
        new()
        {
            Edge = this,
            FromNode = fromNode,
            ToNode = toNode,
        };

    /// <summary>
    /// Returns the tangent bearing at <paramref name="atNode"/> when traversing the arc
    /// from <paramref name="fromNode"/> to <paramref name="toNode"/>.
    /// Computed from the bezier tangent direction at the relevant endpoint.
    /// <paramref name="atNode"/> must be one of <paramref name="fromNode"/> or <paramref name="toNode"/>.
    /// </summary>
    public double TangentBearingAt(GroundNode atNode, GroundNode fromNode, GroundNode toNode)
    {
        var bezier = ToBezier();
        bool forward = fromNode.Id == Nodes[0].Id;

        if (forward)
        {
            // Forward traversal: P0→P3. t=0 at fromNode, t=1 at toNode.
            return atNode.Id == fromNode.Id ? bezier.TangentBearing(0.0) : bezier.TangentBearing(1.0);
        }

        // Reversed traversal: P3→P0. Tangent directions flip 180°.
        double t = atNode.Id == fromNode.Id ? 1.0 : 0.0;
        return (bezier.TangentBearing(t) + 180.0) % 360.0;
    }
}

/// <summary>
/// A directional view of an <see cref="IGroundEdge"/> — captures a specific traversal direction.
/// Created when building routes/paths. Multiple instances can reference the same edge
/// (different directions, or same direction for U-turns).
/// </summary>
public sealed class DirectionalEdge
{
    public required IGroundEdge Edge { get; init; }
    public required GroundNode FromNode { get; init; }
    public required GroundNode ToNode { get; init; }

    public string TaxiwayName => Edge.TaxiwayName;
    public double DistanceNm => Edge.DistanceNm;
    public int FromNodeId => FromNode.Id;
    public int ToNodeId => ToNode.Id;

    /// <summary>
    /// Bearing at the start of traversal (departing FromNode).
    /// For arcs: tangent at FromNode in the sweep direction.
    /// For straight edges: bearing from FromNode to ToNode.
    /// </summary>
    public double DepartureBearing =>
        Edge is GroundArc arc ? arc.TangentBearingAt(FromNode, FromNode, ToNode) : GeoMath.BearingTo(FromNode.Position, ToNode.Position);

    /// <summary>
    /// Bearing at the end of traversal (arriving at ToNode).
    /// For arcs: tangent at ToNode continuing in the same sweep direction as the traversal.
    /// For straight edges: bearing from FromNode to ToNode (same as departure).
    /// </summary>
    public double ArrivalBearing =>
        Edge is GroundArc arc ? arc.TangentBearingAt(ToNode, FromNode, ToNode) : GeoMath.BearingTo(FromNode.Position, ToNode.Position);
}

public sealed class GroundRunway
{
    public required string Name { get; init; }
    public required List<(double Lat, double Lon)> Coordinates { get; init; }
    public required double WidthFt { get; init; }

    /// <summary>
    /// The two end designators parsed from <see cref="Name"/> (e.g. "28R - 10L" → ["28R", "10L"]).
    /// Accepts the canonical dash-with-spaces form authored in GeoJSON as well as a bare "X-Y"
    /// fallback for any publisher that omits the spaces. Use this everywhere a runway name needs
    /// to be split into ends so the separator stays consistent across the codebase.
    /// </summary>
    public IReadOnlyList<string> EndDesignators => Name.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Author-specified preferred turn-off side per landing-end designator (left/right of nose at rollout).
    /// In ATCTrainer airport files, "turnoff" is one value per physical runway, expressed relative to the
    /// first-named end's heading. Parsing flips it for the second end so the same physical side resolves
    /// regardless of which direction the aircraft lands. Empty when no turnoff is authored.
    /// </summary>
    public IReadOnlyDictionary<string, ExitSide> TurnoffByEnd { get; init; } = new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Author-specified pattern altitude in feet AGL above field elevation. Null when unset.</summary>
    public double? PatternAltitudeAglFt { get; init; }

    /// <summary>Author-specified downwind offset from runway centerline in nm. Null when unset.</summary>
    public double? PatternSizeNm { get; init; }

    /// <summary>
    /// Forbidden exit taxiways keyed by landing end designator (e.g. "10L"). Exact-name match.
    /// Empty for ends without restrictions.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NoTurnoffByEnd { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed class AirportGroundLayout
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirportGroundLayout");

    public required string AirportId { get; init; }

    public Dictionary<int, GroundNode> Nodes { get; init; } = [];
    public List<GroundEdge> Edges { get; init; } = [];
    public List<GroundArc> Arcs { get; init; } = [];
    public List<GroundRunway> Runways { get; init; } = [];

    /// <summary>
    /// Find the runway whose two-end name (e.g. "10L - 28R") matches the given designator on either end.
    /// Returns null when no runway in the layout names this end.
    /// </summary>
    public GroundRunway? FindRunway(string designator)
    {
        foreach (var rwy in Runways)
        {
            var ends = rwy.EndDesignators;
            if (
                (ends.Count == 2)
                && (ends[0].Equals(designator, StringComparison.OrdinalIgnoreCase) || ends[1].Equals(designator, StringComparison.OrdinalIgnoreCase))
            )
            {
                return rwy;
            }
        }
        return null;
    }

    /// <summary>All edges (straight and arc) for iteration.</summary>
    public IEnumerable<IGroundEdge> AllEdges => Edges.Cast<IGroundEdge>().Concat(Arcs);

    /// <summary>
    /// Returns true if the node with <paramref name="nodeId"/> has any edge
    /// whose taxiway matches <paramref name="taxiwayName"/> (case-insensitive,
    /// includes secondary names on junction arcs). Returns false when the node
    /// is not in this layout.
    /// </summary>
    public bool NodeHasEdgeTo(int nodeId, string taxiwayName)
    {
        if (!Nodes.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        foreach (var edge in node.Edges)
        {
            if (edge.MatchesTaxiway(taxiwayName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuild <see cref="GroundNode.Edges"/> adjacency lists from the <see cref="Edges"/> collection.
    /// Call after constructing all edges (e.g., in tests or client-side layout reconstruction).
    /// </summary>
    public void RebuildAdjacencyLists()
    {
        foreach (var node in Nodes.Values)
        {
            node.Edges.Clear();
        }

        foreach (var edge in AllEdges)
        {
            if (Nodes.TryGetValue(edge.Nodes[0].Id, out var nodeA))
            {
                nodeA.Edges.Add(edge);
            }

            if (Nodes.TryGetValue(edge.Nodes[1].Id, out var nodeB))
            {
                nodeB.Edges.Add(edge);
            }
        }

        // Build the taxiway-node index eagerly so concurrent readers don't race.
        var index = new Dictionary<string, List<GroundNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Nodes.Values)
        {
            foreach (var edge in node.Edges)
            {
                if (!index.TryGetValue(edge.TaxiwayName, out var list))
                {
                    list = [];
                    index[edge.TaxiwayName] = list;
                }

                if (list.Count == 0 || list[^1].Id != node.Id)
                {
                    list.Add(node);
                }
            }
        }

        _nodesByTaxiway = index;
    }

    /// <summary>
    /// Returns all nodes that have at least one edge on the named taxiway.
    /// Index is built eagerly by <see cref="RebuildAdjacencyLists"/>.
    /// </summary>
    public List<GroundNode> GetNodesOnTaxiway(string taxiwayName)
    {
        return _nodesByTaxiway?.GetValueOrDefault(taxiwayName) ?? [];
    }

    private Dictionary<string, List<GroundNode>>? _nodesByTaxiway;

    public GroundNode? FindParkingByName(string name)
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Parking && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    public GroundNode? FindHelipadByName(string name)
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Helipad && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a named spot, searching helipads first, then parking, then spot nodes.
    /// Used by LAND command to resolve destination by name.
    /// </summary>
    public GroundNode? FindSpotByName(string name)
    {
        return FindHelipadByName(name) ?? FindParkingByName(name) ?? FindSpotNodeByName(name);
    }

    /// <summary>
    /// Find a named spot node (GroundNodeType.Spot only).
    /// Used by $ prefix commands to resolve spot-only destinations.
    /// </summary>
    public GroundNode? FindSpotNodeByName(string name)
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Spot && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the node where two named taxiways cross. Scans nodes on
    /// <paramref name="taxiA"/> and returns one whose adjacent edges include at least one
    /// matching <paramref name="taxiB"/>. When multiple candidates exist and
    /// <paramref name="near"/> is provided, returns the closest by great-circle distance;
    /// otherwise returns the lowest node id for determinism.
    /// </summary>
    public GroundNode? FindIntersectionNode(string taxiA, string taxiB, LatLon? near = null)
    {
        if (string.IsNullOrEmpty(taxiA) || string.IsNullOrEmpty(taxiB))
        {
            return null;
        }

        var candidates = GetNodesOnTaxiway(taxiA);
        GroundNode? best = null;
        double bestMetric = double.MaxValue;

        foreach (var node in candidates)
        {
            bool matchesB = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway(taxiB))
                {
                    matchesB = true;
                    break;
                }
            }
            if (!matchesB)
            {
                continue;
            }

            double metric = near is { } pos ? GeoMath.DistanceNm(pos, node.Position) : node.Id;
            if (metric < bestMetric)
            {
                bestMetric = metric;
                best = node;
            }
        }

        return best;
    }

    public GroundNode? FindNearestNode(double lat, double lon)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Result of a nearest-taxi-edge lookup.
    /// </summary>
    /// <param name="Edge">The straight <see cref="GroundEdge"/> nearest to the query point.</param>
    /// <param name="DistNm">Perpendicular distance from the query point to the foot-of-perpendicular on the edge (clamped to endpoints).</param>
    /// <param name="FootLat">Latitude of the foot-of-perpendicular.</param>
    /// <param name="FootLon">Longitude of the foot-of-perpendicular.</param>
    /// <param name="AlongNm">Distance from <c>Edge.Nodes[0]</c> to the foot along the edge direction.</param>
    public readonly record struct NearestTaxiEdge(GroundEdge Edge, double DistNm, double FootLat, double FootLon, double AlongNm);

    /// <summary>
    /// Find the nearest straight taxi edge to a query point. Filters out:
    /// <list type="bullet">
    /// <item><see cref="GroundArc"/> (fillet curves at junctions — aircraft can't sit mid-arc)</item>
    /// <item>runway-centerline edges (<see cref="IGroundEdge.IsRunwayCenterline"/>)</item>
    /// <item>ramp connector edges (<see cref="IGroundEdge.IsRamp"/>)</item>
    /// </list>
    /// Used by the ground-spawn snap to realign off-graph ground-coord aircraft
    /// onto a taxi surface before the first tick fires.
    /// </summary>
    public NearestTaxiEdge? FindNearestTaxiEdge(double lat, double lon)
    {
        GroundEdge? bestEdge = null;
        double bestDistNm = double.MaxValue;
        double bestFootLat = 0;
        double bestFootLon = 0;
        double bestAlongNm = 0;

        var seen = new HashSet<IGroundEdge>();
        foreach (var node in Nodes.Values)
        {
            foreach (var edge in node.Edges)
            {
                if (!seen.Add(edge))
                {
                    continue;
                }
                if (edge is not GroundEdge straight)
                {
                    continue;
                }
                if (edge.IsRunwayCenterline || edge.IsRamp)
                {
                    continue;
                }

                var (footLat, footLon, alongNm, _) = GeoMath.FootOfPerpendicular(
                    lat,
                    lon,
                    straight.Nodes[0].Position.Lat,
                    straight.Nodes[0].Position.Lon,
                    straight.Nodes[1].Position.Lat,
                    straight.Nodes[1].Position.Lon
                );
                double distNm = GeoMath.DistanceNm(lat, lon, footLat, footLon);
                if (distNm < bestDistNm)
                {
                    bestDistNm = distNm;
                    bestEdge = straight;
                    bestFootLat = footLat;
                    bestFootLon = footLon;
                    bestAlongNm = alongNm;
                }
            }
        }

        return bestEdge is null ? null : new NearestTaxiEdge(bestEdge, bestDistNm, bestFootLat, bestFootLon, bestAlongNm);
    }

    /// <summary>
    /// Pick the start node for a taxi command, biased by heading. Unlike
    /// <see cref="FindNearestNode(LatLon)"/> — which returns the absolute
    /// nearest node and can land on the wrong branch when an aircraft rests
    /// between graph nodes after a directional pushback (issue #161) — this
    /// returns the nearest node within <paramref name="maxDistFt"/> that has
    /// at least one non-RAMP, non-runway-centerline outbound edge whose
    /// bearing is within 90° of the aircraft's <paramref name="heading"/>.
    /// <para>
    /// Returns null when no qualifying node exists in the radius — the caller
    /// should fall back to <see cref="FindNearestNode(LatLon)"/>. The maximum
    /// distance is generous because the helper is only a discriminator across
    /// the small set of candidate nodes any near-graph aircraft has within
    /// reach; selection is by closest-of-the-qualifying, not by absolute
    /// nearest, so a far node with the right alignment never beats a close
    /// one.
    /// </para>
    /// <para>
    /// Aircraft positioned <em>at</em> a node (HoldingShortPhase fuselage tip
    /// at the hold-short line, AtParkingPhase at the spot) still resolve to
    /// that node because their forward heading aligns with the outbound edge
    /// the route continues along — the existing-edge test admits the same
    /// node <see cref="FindNearestNode(LatLon)"/> would have picked.
    /// </para>
    /// </summary>
    public GroundNode? FindNearestNodeForTaxi(LatLon position, TrueHeading heading, double maxDistFt = 100.0)
    {
        double maxDistNm = maxDistFt / GeoMath.FeetPerNm;

        // Below this distance the candidate node and the aircraft are
        // effectively co-located — bearing-to-node is undefined and the
        // existing HoldingShortPhase / AtParkingPhase behaviour (start at the
        // node the aircraft is sitting at) must be preserved.
        double atNodeNm = 15.0 / GeoMath.FeetPerNm;

        GroundNode? best = null;
        double bestDistNm = double.MaxValue;

        // Fast path: if the aircraft is essentially at a Parking/Helipad node,
        // prefer it as the startNode UNLESS it has a co-located non-parking
        // neighbor (a fillet phase-d-shorten endpoint at near-zero distance).
        // The co-located neighbor is the natural exit point — using it lets
        // the route skip the degenerate near-zero parking-exit edge while
        // still keeping the route's first segment anchored at the aircraft's
        // actual position. When no co-located neighbor exists (e.g. SFO 42-4
        // where the only edge from 1047 is a 42 ft RAMP to 2718), use the
        // parking node directly so the route's first segment IS the
        // parking-exit RAMP — otherwise the resolver picks a fillet vertex
        // 90+ ft away, leaving the aircraft off-line from segment 0 and
        // unable to converge under the short-route speed cap (slow-creep
        // spin observed at SFO 42-4 → 10L).
        foreach (var parkingNode in Nodes.Values)
        {
            if (parkingNode.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad))
            {
                continue;
            }
            if (GeoMath.DistanceNm(position, parkingNode.Position) > atNodeNm)
            {
                continue;
            }

            GroundNode? colocatedNeighbor = null;
            foreach (var edge in parkingNode.Edges)
            {
                var other = edge.OtherNode(parkingNode);
                if (other.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
                {
                    continue;
                }
                if (GeoMath.DistanceNm(parkingNode.Position, other.Position) <= atNodeNm)
                {
                    colocatedNeighbor = other;
                    break;
                }
            }

            return colocatedNeighbor ?? parkingNode;
        }

        foreach (var node in Nodes.Values)
        {
            if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(position, node.Position);
            if (dist > maxDistNm || dist >= bestDistNm)
            {
                continue;
            }

            // Reject candidates behind the aircraft. Skipped when essentially
            // at the node so an aircraft parked on a hold-short still starts
            // there even though "bearing to self" is meaningless.
            if (dist > atNodeNm)
            {
                double bearingToNode = GeoMath.BearingTo(position, node.Position);
                if (GeoMath.AbsBearingDifference(bearingToNode, heading.Degrees) >= 90.0)
                {
                    continue;
                }
            }

            if (!HasHeadingAlignedTaxiEdge(node, heading))
            {
                continue;
            }

            bestDistNm = dist;
            best = node;
        }

        return best;
    }

    /// <summary>
    /// Returns true when <paramref name="node"/> has at least one outbound
    /// taxi edge (not RAMP, not runway centerline) whose bearing from the
    /// node toward its neighbor is within 90° of <paramref name="heading"/>.
    /// Used by <see cref="FindNearestNodeForTaxi"/> to reject candidate
    /// start nodes whose only taxi connections head away from the aircraft.
    /// </summary>
    private static bool HasHeadingAlignedTaxiEdge(GroundNode node, TrueHeading heading)
    {
        foreach (var edge in node.Edges)
        {
            if (edge.IsRunwayCenterline || edge.IsRamp)
            {
                continue;
            }

            var other = edge.OtherNode(node);
            double bearing = GeoMath.BearingTo(node.Position, other.Position);
            if (GeoMath.AbsBearingDifference(bearing, heading.Degrees) < 90.0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Find the nearest runway centerline node that is ahead of or abeam the
    /// aircraft along the given heading. When <paramref name="runwayDesignator"/>
    /// is provided, only considers nodes with RWY edges matching that runway.
    /// Falls back to the nearest matching centerline node if none is ahead.
    /// </summary>
    public GroundNode? FindNearestCenterlineNode(double lat, double lon, TrueHeading runwayHeading, string? runwayDesignator = null)
    {
        GroundNode? bestAhead = null;
        double bestAheadDist = double.MaxValue;
        GroundNode? bestAny = null;
        double bestAnyDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            if (!HasRunwayCenterlineEdge(node))
            {
                continue;
            }

            // Filter to edges matching the specific runway if designator provided
            if (runwayDesignator is not null && !HasRunwayEdgeForDesignator(node, runwayDesignator))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);

            if (dist < bestAnyDist)
            {
                bestAnyDist = dist;
                bestAny = node;
            }

            double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            if (diff <= 90 && dist < bestAheadDist)
            {
                bestAheadDist = dist;
                bestAhead = node;
            }
        }

        return bestAhead ?? bestAny;
    }

    /// <summary>
    /// Returns true if the node has a RWY edge whose name contains the given
    /// runway designator (e.g., "RWY10L/28R" contains "28R").
    /// </summary>
    private static bool HasRunwayEdgeForDesignator(GroundNode node, string designator)
    {
        foreach (var edge in node.Edges)
        {
            if (edge.MatchesRunway(designator))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// From a runway centerline node, find the next centerline node ahead along
    /// the given heading. Walks RWY-prefixed edges and picks the neighbor whose
    /// bearing is closest to the runway heading (within 90°).
    /// </summary>
    public GroundNode? FindCenterlineNeighborAhead(GroundNode currentNode, TrueHeading runwayHeading, string? runwayDesignator = null)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;

        foreach (var edge in currentNode.Edges)
        {
            if (!edge.IsRunwayCenterline)
            {
                continue;
            }

            if (runwayDesignator is not null && !edge.MatchesRunway(runwayDesignator))
            {
                continue;
            }

            var neighbor = edge.OtherNode(currentNode);

            double bearing = GeoMath.BearingTo(currentNode.Position, neighbor.Position);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            if (diff < 90 && diff < bestDiff)
            {
                bestDiff = diff;
                best = neighbor;
            }
        }

        return best;
    }

    /// <summary>Result of <see cref="FindExitFromCenterline"/> — a single hold-short with metadata.</summary>
    public readonly record struct CenterlineExitResult(
        GroundNode HoldShort,
        string Taxiway,
        List<GroundNode> Path,
        double ExitAngle,
        ExitSide Side,
        GroundNode WalkCenterline
    );

    /// <summary>
    /// Caller-supplied verdict on a candidate exit during
    /// <see cref="FindOnSidePreferredExit"/>. <c>Accept</c> commits, <c>Skip</c>
    /// excludes the entire taxiway and continues, <c>Defer</c> remembers it as
    /// an off-side fallback (used internally for the off-side rule and may be
    /// returned by callers that want the same fallback semantics for their own
    /// predicate).
    /// </summary>
    public enum CandidateVerdict
    {
        Accept,
        Skip,
        Defer,
    }

    /// <summary>
    /// Walk centerlines ahead of the aircraft and pick the next exit that
    /// satisfies the side preference. Off-side candidates (relative to
    /// <paramref name="sidePref"/>) are deferred — the search continues, and
    /// the deferred candidate is only committed if no on-side option is found.
    /// The optional <paramref name="filter"/> lets the caller veto candidates
    /// (e.g. comfort-braking checks); a returned <see cref="CandidateVerdict.Skip"/>
    /// excludes the candidate's taxiway from subsequent iterations in this call.
    /// Returns <see langword="null"/> when no exit (on-side or off-side fallback)
    /// is found.
    /// </summary>
    public CenterlineExitResult? FindOnSidePreferredExit(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference,
        ExitSide? sidePref,
        HashSet<int>? excludeBranchPoints = null,
        HashSet<int>? excludeHoldShortNodes = null,
        Func<CenterlineExitResult, CandidateVerdict>? filter = null,
        int maxIterations = 30
    )
    {
        var localTaxiwayExclusion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CenterlineExitResult? deferredOffSide = null;

        for (int i = 0; i < maxIterations; i++)
        {
            var raw = FindExitFromCenterline(
                lat,
                lon,
                runwayHeading,
                runwayDesignator,
                preference,
                excludeBranchPoints,
                excludeHoldShortNodes,
                localTaxiwayExclusion.Count > 0 ? localTaxiwayExclusion : null
            );
            if (raw is null)
            {
                break;
            }

            var candidate = new CenterlineExitResult(
                raw.Value.HoldShort,
                raw.Value.Taxiway,
                raw.Value.Path,
                raw.Value.ExitAngle,
                raw.Value.Side,
                raw.Value.WalkCenterline
            );

            if (filter is not null)
            {
                var verdict = filter(candidate);
                if (verdict == CandidateVerdict.Skip)
                {
                    localTaxiwayExclusion.Add(candidate.Taxiway);
                    continue;
                }
                if (verdict == CandidateVerdict.Defer)
                {
                    deferredOffSide ??= candidate;
                    localTaxiwayExclusion.Add(candidate.Taxiway);
                    continue;
                }
            }

            // Off-side relative to the side preference: defer and keep walking.
            // Excluding the entire taxiway from subsequent iterations is required
            // because BFS clusters runway centerlines that are tangent-link
            // neighbors — excluding only the walking centerline still re-finds
            // the same hold-short via the next centerline's cluster expansion.
            if ((sidePref is not null) && (candidate.Side != sidePref))
            {
                deferredOffSide ??= candidate;
                localTaxiwayExclusion.Add(candidate.Taxiway);
                continue;
            }

            return candidate;
        }

        return deferredOffSide;
    }

    /// <summary>
    /// Walk centerline nodes ahead of the aircraft and search outward at each one
    /// for an exit matching the preference. Returns the first match with its path
    /// (starting at the centerline branch point, ending at the hold-short).
    /// This is the correct search direction: runway → taxiway → hold-short.
    /// </summary>
    public (
        GroundNode HoldShort,
        string Taxiway,
        List<GroundNode> Path,
        double ExitAngle,
        ExitSide Side,
        GroundNode WalkCenterline
    )? FindExitFromCenterline(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference,
        HashSet<int>? excludeBranchPoints = null,
        HashSet<int>? excludeHoldShortNodes = null,
        HashSet<string>? excludeTaxiways = null
    )
    {
        var startNode = FindNearestCenterlineNode(lat, lon, runwayHeading, runwayDesignator);
        if (startNode is null)
        {
            return null;
        }

        // Authored noTurnoff: forbid named taxiways for this landing direction. Applied only
        // when the controller hasn't explicitly named a taxiway — explicit EXIT commands win.
        HashSet<string>? forbiddenTaxiways = null;
        if ((preference?.Taxiway is null) && (FindRunway(runwayDesignator) is { } authoredRwy))
        {
            if (authoredRwy.NoTurnoffByEnd.TryGetValue(runwayDesignator, out var forbidden) && (forbidden.Count > 0))
            {
                forbiddenTaxiways = new HashSet<string>(forbidden, StringComparer.OrdinalIgnoreCase);
            }
        }

        // Caller-supplied exclusion (e.g. LandingPhase deferring an entire taxiway
        // after seeing an off-side hold-short there) merges with the airport noTurnoff list.
        if (excludeTaxiways is { Count: > 0 })
        {
            forbiddenTaxiways = forbiddenTaxiways is null
                ? new HashSet<string>(excludeTaxiways, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(forbiddenTaxiways.Concat(excludeTaxiways), StringComparer.OrdinalIgnoreCase);
        }

        // Walk along-track: only consider centerline nodes ahead of the aircraft.
        // When no taxiway preference is set, defer any back-exit (>100°) and keep
        // walking — a real pilot wouldn't U-turn on the runway to reach E if G or
        // H is available further ahead. Commit to the deferred back-exit only if
        // nothing forward turns up.
        const int maxCenterlineHops = 30;
        const double BackExitAngleThreshold = 100.0;
        var current = startNode;
        (GroundNode Node, string Taxiway, List<GroundNode> Path, double ExitAngle, ExitSide Side, GroundNode WalkCenterline)? deferredBackExit = null;
        for (int hop = 0; hop < maxCenterlineHops && current is not null; hop++)
        {
            double alongTrack = GeoMath.AlongTrackDistanceNm(current.Position, new LatLon(lat, lon), runwayHeading);
            if (alongTrack < -0.005)
            {
                // Node is behind the aircraft — skip
                current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
                continue;
            }

            // Skip centerline nodes where the aircraft already declared "unable"
            if ((excludeBranchPoints is not null) && excludeBranchPoints.Contains(current.Id))
            {
                current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
                continue;
            }

            Log.LogDebug(
                "[ExitCL] Checking centerline node #{Id} at ({Lat:F6}, {Lon:F6}), pref={PrefTwy}/{PrefSide}",
                current.Id,
                current.Position.Lat,
                current.Position.Lon,
                preference?.Taxiway ?? "any",
                preference?.Side?.ToString() ?? "any"
            );
            var result = FindAdjacentHoldShort(current, runwayDesignator, runwayHeading, preference, excludeHoldShortNodes, forbiddenTaxiways);
            if (result is not null)
            {
                double? exitAngle = ComputeExitAngle(result.Value.Node, result.Value.Taxiway, runwayHeading);
                Log.LogDebug(
                    "[ExitCL] Found exit: twy={Twy} HS=#{HsId} angle={Angle:F0}° path=[{Path}]",
                    result.Value.Taxiway,
                    result.Value.Node.Id,
                    exitAngle,
                    string.Join("→", result.Value.Path.Select(n => n.Id))
                );

                bool isBackExit = (exitAngle is not null) && (exitAngle.Value > BackExitAngleThreshold);
                bool hasTaxiwayPreference = preference?.Taxiway is not null;
                if (isBackExit && !hasTaxiwayPreference)
                {
                    // Remember the nearest back-exit but keep walking for a forward one.
                    deferredBackExit ??= (result.Value.Node, result.Value.Taxiway, result.Value.Path, exitAngle!.Value, result.Value.Side, current);
                    current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
                    continue;
                }

                return (result.Value.Node, result.Value.Taxiway, result.Value.Path, exitAngle ?? 90, result.Value.Side, current);
            }

            current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
        }

        return deferredBackExit;
    }

    /// <summary>
    /// From a runway centerline node, find a hold-short node reachable via
    /// non-RWY edges using BFS (max 12 hops). Each branch is constrained by
    /// the taxiway name of its first non-RWY edge. Optionally filters by
    /// runway designator, exit side, or taxiway name preference.
    /// Returns the hold-short node, taxiway name, and path from centerline.
    /// </summary>
    public (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? FindAdjacentHoldShort(
        GroundNode centerlineNode,
        string? runwayDesignator,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        HashSet<int>? excludeHoldShortNodes = null,
        HashSet<string>? forbiddenTaxiways = null
    )
    {
        const int maxDepth = 20;

        // Track two best candidates: on-side and off-side. Return on-side if any;
        // fall back to off-side only when no on-side exit exists (e.g., C3 at SFO).
        GroundNode? bestOnSide = null;
        string? bestOnSideTaxiway = null;
        List<GroundNode>? bestOnSidePath = null;
        ExitSide bestOnSideSide = ExitSide.Right;
        double bestOnSideScore = double.MaxValue;

        GroundNode? bestOffSide = null;
        string? bestOffSideTaxiway = null;
        List<GroundNode>? bestOffSidePath = null;
        ExitSide bestOffSideSide = ExitSide.Right;
        double bestOffSideScore = double.MaxValue;

        // Expand starting node to include all nodes reachable via short runway
        // tangent-link edges. Fillets create separate tangent nodes for each arc
        // pair at the same intersection — e.g., #1293 connects to the south arc
        // while #1289 connects to the north arc. Both are part of the same
        // crossing and the BFS must see arcs from all of them.
        const double tangentLinkThresholdNm = 0.03;
        var clusterNodes = new List<GroundNode> { centerlineNode };
        var visited = new HashSet<int> { centerlineNode.Id };
        for (int ci = 0; ci < clusterNodes.Count; ci++)
        {
            foreach (var edge in clusterNodes[ci].Edges)
            {
                if (!edge.IsRunwayCenterline)
                {
                    continue;
                }

                var neighbor = edge.OtherNode(clusterNodes[ci]);
                if (edge.DistanceNm <= tangentLinkThresholdNm && visited.Add(neighbor.Id))
                {
                    clusterNodes.Add(neighbor);
                }
            }
        }

        Log.LogDebug(
            "[ExitBFS] Cluster from #{CL}: [{Nodes}] pref={PrefTwy}/{PrefSide}",
            centerlineNode.Id,
            string.Join(",", clusterNodes.Select(n => n.Id)),
            preference?.Taxiway ?? "any",
            preference?.Side?.ToString() ?? "any"
        );

        var queue = new Queue<(GroundNode Node, string Taxiway, List<GroundNode> Path, double TotalDist, int Depth)>();

        // Seed in two passes: arcs first, then straight edges. Fillet arcs are the
        // geometrically correct path through intersections — the straight edges
        // preserved by the fillet generator are shortcuts that skip the curve.
        // By seeding arcs first, they claim the visited set and straights to the
        // same node are skipped.
        SeedEdgesFromCluster(clusterNodes, visited, queue, runwayHeading, preference, arcsOnly: true);
        SeedEdgesFromCluster(clusterNodes, visited, queue, runwayHeading, preference, arcsOnly: false);

        while (queue.Count > 0)
        {
            var (current, branchTwy, path, totalDist, depth) = queue.Dequeue();
            Log.LogDebug(
                "[ExitBFS] dequeue #{Id} twy={Twy} depth={Depth} dist={Dist:F4} type={Type}",
                current.Id,
                branchTwy,
                depth,
                totalDist,
                current.Type
            );

            if (current.Type == GroundNodeType.RunwayHoldShort)
            {
                if (runwayDesignator is not null && current.RunwayId is { } rwyId && !rwyId.Contains(runwayDesignator))
                {
                    Log.LogDebug("[ExitBFS] HS #{Id} rwy={Rwy}: skip (wrong runway)", current.Id, rwyId);
                    continue;
                }

                // Skip hold-short nodes already occupied by another aircraft
                if ((excludeHoldShortNodes is not null) && excludeHoldShortNodes.Contains(current.Id))
                {
                    Log.LogDebug("[ExitBFS] HS #{Id}: skip (occupied)", current.Id);
                    continue;
                }

                // Skip hold-shorts on a forbidden taxiway (per-end noTurnoff from airport file).
                if ((forbiddenTaxiways is not null) && forbiddenTaxiways.Contains(branchTwy))
                {
                    Log.LogDebug("[ExitBFS] HS #{Id} twy={Twy}: skip (noTurnoff)", current.Id, branchTwy);
                    continue;
                }

                // Determine the absolute side this hold-short lies on relative to the
                // runway heading. Negative cross-track = Left, positive = Right.
                double absBearing = GeoMath.BearingTo(centerlineNode.Position, current.Position);
                double absRelative = runwayHeading.SignedAngleTo(new TrueHeading(absBearing));
                ExitSide actualSide = absRelative < 0 ? ExitSide.Left : ExitSide.Right;

                // Determine if this candidate is on the preferred side (inferred or explicit).
                bool onRequestedSide = (preference?.Side is not { } side) || (actualSide == side);

                double parkingBias = AverageNearestParkingDistanceNm(current, ParkingSampleCount) * ParkingProximityWeight;

                // Penalize exits that go backward (>100° from runway heading).
                // Without this, a short backward exit (e.g. E at 111° from node 230
                // at SFO) can outscore a longer forward exit (T at 19°) due to
                // distance alone, causing the caller to filter the result and miss
                // the valid forward exit entirely.
                double anglePenalty = 0;
                double? exitAngle = ComputeExitAngle(current, branchTwy, runwayHeading);
                if ((exitAngle is not null) && (exitAngle.Value > 100) && (preference?.Taxiway is null))
                {
                    anglePenalty = 10.0;
                }

                // Bonus for high-speed exits (≤45°): these have higher turn-off speeds
                // (30kts vs 15kts) and gentler turns, making them strongly preferred for
                // default selection. The bonus ensures T (19°, 0.11nm) beats E (70°, 0.03nm)
                // at the same centerline node.
                double highSpeedBonus = 0;
                if ((exitAngle is not null) && (exitAngle.Value <= 45.0) && (preference?.Taxiway is null))
                {
                    highSpeedBonus = HighSpeedExitBonus;
                }

                double score = totalDist + parkingBias + anglePenalty - highSpeedBonus;
                bool isNewBest = onRequestedSide ? (score < bestOnSideScore) : (score < bestOffSideScore);
                Log.LogDebug(
                    "[ExitBFS] HS #{Id} twy={Twy} angle={ExAngle:F0}° side={Side}: score={Score:F4} "
                        + "(dist={Dist:F4} parking={Park:F4} anglePen={AngPen:F2} hsBonus={Hs:F2}){Result}",
                    current.Id,
                    branchTwy,
                    exitAngle ?? 0,
                    onRequestedSide ? "ON" : "OFF",
                    score,
                    totalDist,
                    parkingBias,
                    anglePenalty,
                    highSpeedBonus,
                    isNewBest ? " [NEW BEST]" : ""
                );
                if (isNewBest)
                {
                    if (onRequestedSide)
                    {
                        bestOnSideScore = score;
                        bestOnSide = current;
                        bestOnSideTaxiway = branchTwy;
                        bestOnSidePath = path;
                        bestOnSideSide = actualSide;
                    }
                    else
                    {
                        bestOffSideScore = score;
                        bestOffSide = current;
                        bestOffSideTaxiway = branchTwy;
                        bestOffSidePath = path;
                        bestOffSideSide = actualSide;
                    }
                }

                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var edge in current.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }

                if (!edge.MatchesTaxiway(branchTwy))
                {
                    Log.LogDebug(
                        "[ExitBFS]   skip walk #{From}→#{To}: twy {Twy} != {Branch}",
                        current.Id,
                        edge.OtherNode(current).Id,
                        edge.TaxiwayName,
                        branchTwy
                    );
                    continue;
                }

                var next = edge.OtherNode(current);
                if (!visited.Add(next.Id))
                {
                    Log.LogDebug("[ExitBFS]   skip walk #{From}→#{To}: already visited", current.Id, next.Id);
                    continue;
                }

                var nextPath = new List<GroundNode>(path) { next };
                queue.Enqueue((next, branchTwy, nextPath, totalDist + edge.DistanceNm, depth + 1));
                Log.LogDebug(
                    "[ExitBFS]   walk #{From}→#{To} via {Twy} depth={Depth} type={Type}",
                    current.Id,
                    next.Id,
                    branchTwy,
                    depth + 1,
                    next.Type
                );
            }
        }

        // Prefer on-side; fall back to off-side (for single-sided taxiways like C3).
        GroundNode? best = bestOnSide ?? bestOffSide;
        string? bestTaxiway = bestOnSideTaxiway ?? bestOffSideTaxiway;
        List<GroundNode>? bestPath = bestOnSidePath ?? bestOffSidePath;
        ExitSide bestSide = bestOnSide is not null ? bestOnSideSide : bestOffSideSide;

        if (best is null || bestTaxiway is null || bestPath is null)
        {
            Log.LogDebug("[ExitBFS] RESULT: no exit for centerline #{Id} pref={Pref}", centerlineNode.Id, preference?.Taxiway ?? "any");
            return null;
        }

        Log.LogDebug(
            "[ExitBFS] RESULT: centerline #{CL} → HS #{HS} via {Twy} onSide={OnSide} actualSide={Side} path=[{Path}]",
            centerlineNode.Id,
            best.Id,
            bestTaxiway,
            bestOnSide is not null,
            bestSide,
            string.Join("→", bestPath.Select(n => n.Id))
        );
        return (best, bestTaxiway, bestPath, bestSide);
    }

    /// <summary>
    /// Seed the BFS queue from cluster nodes. When <paramref name="arcsOnly"/> is true,
    /// only arc edges are seeded; when false, only straight edges. Called in two passes
    /// (arcs first) so arcs claim the visited set before straight shortcuts can.
    /// </summary>
    private void SeedEdgesFromCluster(
        List<GroundNode> clusterNodes,
        HashSet<int> visited,
        Queue<(GroundNode Node, string Taxiway, List<GroundNode> Path, double TotalDist, int Depth)> queue,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        bool arcsOnly
    )
    {
        foreach (var clusterNode in clusterNodes)
        {
            foreach (var edge in clusterNode.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }

                bool isArc = edge is GroundArc;
                if (arcsOnly != isArc)
                {
                    continue;
                }

                var neighbor = edge.OtherNode(clusterNode);

                if (visited.Contains(neighbor.Id))
                {
                    Log.LogDebug("[ExitBFS]   skip #{From}->{To} via {Twy}: already visited", clusterNode.Id, neighbor.Id, edge.TaxiwayName);
                    continue;
                }

                if (edge is GroundArc arc)
                {
                    double departureBearing = arc.TangentBearingAt(clusterNode, clusterNode, neighbor);
                    double bearingDiff = runwayHeading.AbsAngleTo(new TrueHeading(departureBearing));
                    if (bearingDiff > 95)
                    {
                        Log.LogDebug(
                            "[ExitBFS]   skip arc #{From}->{To} via {Twy}: departure {Dep:F1} diff={Diff:F1} > 95",
                            clusterNode.Id,
                            neighbor.Id,
                            edge.TaxiwayName,
                            departureBearing,
                            bearingDiff
                        );
                        continue;
                    }

                    Log.LogDebug(
                        "[ExitBFS]   seed arc #{From}->{To} via {Twy}: departure {Dep:F1} diff={Diff:F1}",
                        clusterNode.Id,
                        neighbor.Id,
                        edge.TaxiwayName,
                        departureBearing,
                        bearingDiff
                    );
                }
                else
                {
                    Log.LogDebug("[ExitBFS]   seed edge #{From}->{To} via {Twy}", clusterNode.Id, neighbor.Id, edge.TaxiwayName);
                }

                if (preference?.Taxiway is { } prefTwy && !edge.MatchesTaxiway(prefTwy))
                {
                    Log.LogDebug(
                        "[ExitBFS]   skip #{From}->{To}: taxiway {Twy} doesn't match pref {Pref}",
                        clusterNode.Id,
                        neighbor.Id,
                        edge.TaxiwayName,
                        prefTwy
                    );
                    continue;
                }

                string branchName = edge is GroundArc { IsRunwayJunction: true } ja ? ja.FirstNonRunwayName() : edge.TaxiwayName;
                visited.Add(neighbor.Id);
                queue.Enqueue((neighbor, branchName, [clusterNode, neighbor], edge.DistanceNm, 1));
            }
        }
    }

    /// <summary>
    /// Walk backward from a hold-short node through same-name taxiway edges
    /// until reaching a runway centerline node. Returns the ordered path
    /// [branch-point, intermediates..., hold-short]. Returns null if no path
    /// to the centerline is found.
    /// </summary>
    public List<GroundNode>? FindExitPath(GroundNode holdShortNode, string taxiwayName)
    {
        const int maxDepth = 15;
        var visited = new HashSet<int> { holdShortNode.Id };
        var queue = new Queue<(GroundNode Node, List<GroundNode> Path)>();
        queue.Enqueue((holdShortNode, [holdShortNode]));

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();
            if (path.Count > maxDepth)
            {
                continue;
            }

            foreach (var edge in current.Edges)
            {
                if (!edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                var neighbor = edge.OtherNode(current);
                if (!visited.Add(neighbor.Id))
                {
                    continue;
                }

                var nextPath = new List<GroundNode>(path) { neighbor };

                bool onCenterline = false;
                foreach (var nEdge in neighbor.Edges)
                {
                    if (nEdge.IsRunwayCenterline)
                    {
                        onCenterline = true;
                        break;
                    }
                }

                if (onCenterline)
                {
                    nextPath.Reverse();
                    return nextPath;
                }

                queue.Enqueue((neighbor, nextPath));
            }
        }

        return null;
    }

    /// <summary>
    /// Find the nearest ahead hold-short node for the given runway, measured by
    /// along-track distance. Used by LandingPhase for exit-aware braking.
    /// </summary>
    public GroundNode? FindNearestHoldShortAhead(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference
    )
    {
        GroundNode? best = null;
        double bestAlongTrack = double.MaxValue;

        foreach (var node in GetRunwayHoldShortNodes(runwayDesignator))
        {
            double alongTrack = GeoMath.AlongTrackDistanceNm(node.Position, new LatLon(lat, lon), runwayHeading);
            if (alongTrack <= 0)
            {
                continue;
            }

            if (preference?.Side is { } side)
            {
                double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
                double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));
                bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
                if (!isOnRequestedSide)
                {
                    continue;
                }
            }

            if (preference?.Taxiway is { } taxiway)
            {
                bool hasMatchingEdge = false;
                foreach (var edge in node.Edges)
                {
                    if (!edge.IsRunwayCenterline && edge.MatchesTaxiway(taxiway))
                    {
                        hasMatchingEdge = true;
                        break;
                    }
                }

                if (!hasMatchingEdge)
                {
                    continue;
                }
            }

            // Score by along-track distance + parking proximity bias
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = alongTrack + parkingBias;
            if (score < bestAlongTrack)
            {
                bestAlongTrack = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find a GroundRunway where either end matches the given designator (e.g., "28L").
    /// GroundRunway.Name format: "10R/28L".
    /// </summary>
    public GroundRunway? FindGroundRunway(string designator)
    {
        foreach (var rwy in Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);
            if (id.Contains(designator))
            {
                return rwy;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the nearest taxiway node suitable as a runway exit, considering aircraft heading.
    /// Prefers exits that don't require turns greater than 90 degrees.
    /// When <paramref name="runwayDesignator"/> is provided, filters out exits that are closer
    /// to a different parallel runway's centerline.
    /// </summary>
    public GroundNode? FindNearestExit(double lat, double lon, TrueHeading runwayHeading, string? runwayDesignator, double maxSearchNm = 0.5)
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (var node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
            double turnAngle = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0) + parkingBias;

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find the nearest exit on the specified side of the runway heading.
    /// Falls back to FindNearestExit if no exits match the requested side.
    /// </summary>
    public GroundNode? FindExitBySide(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        ExitSide side,
        string? runwayDesignator,
        double maxSearchNm = 0.5
    )
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (var node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
            double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));

            // Left = negative relative angle, Right = positive
            bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
            if (!isOnRequestedSide)
            {
                continue;
            }

            double turnAngle = Math.Abs(relative);
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0) + parkingBias;

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        // Fall back to nearest exit if none found on the requested side
        return best ?? FindNearestExit(lat, lon, runwayHeading, runwayDesignator, maxSearchNm);
    }

    /// <summary>
    /// Find an exit node connected to the named taxiway.
    /// Uses a wider search radius since the taxiway might be further ahead.
    /// </summary>
    public GroundNode? FindExitByTaxiway(double lat, double lon, string taxiwayName, double maxSearchNm = 1.0)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
            {
                continue;
            }

            // Skip nodes that sit on the runway surface — they're not valid exit points
            if (HasRunwayCenterlineEdge(node))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            // Only count straight GroundEdges, not GroundArcs. A node connected to the
            // requested taxiway only via fillet arcs (e.g. the curved entry from a RAMP
            // into T5B) is not a useful pushback target — the aircraft center would
            // stop on the curve instead of on the taxiway proper. Issue #162.
            bool hasMatchingEdge = false;
            foreach (var edge in node.Edges)
            {
                if (edge is GroundEdge straight && !straight.IsRunwayCenterline && straight.MatchesTaxiway(taxiwayName))
                {
                    hasMatchingEdge = true;
                    break;
                }
            }

            if (!hasMatchingEdge)
            {
                continue;
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Get the heading along the named taxiway at the given node, choosing the
    /// direction closest to <paramref name="preferredBearing"/>.
    /// Returns null if no matching taxiway edge exists at the node.
    /// </summary>
    public double? GetEdgeBearingForTaxiway(GroundNode node, string taxiwayName, double preferredBearing)
    {
        double? bestBearing = null;
        double bestDiff = double.MaxValue;

        foreach (var edge in node.Edges)
        {
            // Skip arcs — only straight GroundEdges define a meaningful taxiway bearing.
            // An arc's chord bearing is not the taxiway's direction. Issue #162.
            if (edge is not GroundEdge straight)
            {
                continue;
            }

            if (straight.IsRunwayCenterline)
            {
                continue;
            }

            if (!straight.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            var otherNode = straight.OtherNode(node);

            double bearing = GeoMath.BearingTo(node.Position, otherNode.Position);
            double diff = GeoMath.AbsBearingDifference(bearing, preferredBearing);

            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestBearing = bearing;
            }
        }

        return bestBearing;
    }

    /// <summary>
    /// Get the taxiway name for the edge connected to a node that leads away from the runway.
    /// </summary>
    public string? GetExitTaxiwayName(GroundNode exitNode)
    {
        foreach (var edge in exitNode.Edges)
        {
            if (!edge.IsRunwayCenterline)
            {
                return edge.TaxiwayName;
            }
        }

        return null;
    }

    /// <summary>
    /// Get all hold-short nodes for a specific runway.
    /// </summary>
    public List<GroundNode> GetRunwayHoldShortNodes(string runwayId)
    {
        var result = new List<GroundNode>();
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } id && id.Contains(runwayId))
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// Find the next node along the taxiway past the exit intersection, so the
    /// aircraft can roll clear of the runway surface. Follows the non-runway edge
    /// whose heading is closest to the aircraft's exit bearing.
    /// </summary>
    public GroundNode? FindClearNode(GroundNode exitNode, string taxiwayName, TrueHeading runwayHeading)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;

        foreach (var edge in exitNode.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            var otherNode = edge.OtherNode(exitNode);

            // Prefer the direction that doesn't require turning back toward the runway
            double bearing = GeoMath.BearingTo(exitNode.Position, otherNode.Position);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));

            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = otherNode;
            }
        }

        return best;
    }

    /// <summary>
    /// Compute the angle between the runway heading and the exit taxiway at the given node.
    /// Returns the absolute angle in degrees (0 = aligned with runway, 90 = perpendicular).
    /// Returns null if no taxiway edge heading can be determined.
    /// </summary>
    public double? ComputeExitAngle(GroundNode exitNode, string taxiwayName, TrueHeading runwayHeading)
    {
        // Find the edge that leads AWAY from the runway (neighbor is not on the
        // centerline) and return its angle from the runway heading. This is the
        // actual exit direction — a high-speed exit has a small angle (~30°), a
        // standard exit has a larger angle (~90°).
        double? bestAngle = null;

        foreach (var edge in exitNode.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            var otherNode = edge.OtherNode(exitNode);

            // Skip edges going toward the runway centerline — we want the away direction
            if (HasRunwayCenterlineEdge(otherNode))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(exitNode.Position, otherNode.Position);
            double angle = runwayHeading.AbsAngleTo(new TrueHeading(bearing));

            if (bestAngle is null || angle < bestAngle.Value)
            {
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>
    /// Returns the preferred exit side for a runway. Priority:
    /// 1. Airport-authored <see cref="GroundRunway.PreferredTurnoff"/> (from GeoJSON "turnoff")
    /// 2. High-speed exits (≤45°), validated by parking proximity
    /// 3. Parallel runway HS inheritance (for runways with no HS exits)
    /// 4. Parking proximity (fallback)
    /// Returns null if no preference can be determined.
    /// </summary>
    public ExitSide? InferPreferredExitSide(string runwayDesignator, TrueHeading runwayHeading)
    {
        // Authored data wins: when the airport file specifies a side for this end, trust it over inference.
        if ((FindRunway(runwayDesignator) is { } rwy) && rwy.TurnoffByEnd.TryGetValue(runwayDesignator, out var authored))
        {
            return authored;
        }

        // Enumerate all exits on both sides
        var exits = EnumerateExitsBothSides(runwayDesignator, runwayHeading);
        if (exits.Count == 0)
        {
            return null;
        }

        int hsLeft = exits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Left));
        int hsRight = exits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Right));

        // Parking proximity per side: average distance from hold-shorts to nearest parking
        double avgParkLeft = AvgParkingDistForSide(exits.Where(e => e.Side == ExitSide.Left));
        double avgParkRight = AvgParkingDistForSide(exits.Where(e => e.Side == ExitSide.Right));

        ExitSide? hsSide =
            (hsLeft > hsRight) ? ExitSide.Left
            : (hsRight > hsLeft) ? ExitSide.Right
            : null;
        ExitSide? parkingSide =
            (avgParkLeft < avgParkRight) ? ExitSide.Left
            : (avgParkRight < avgParkLeft) ? ExitSide.Right
            : null;

        if (hsSide is not null)
        {
            // HS exits are a strong signal, but if parking proximity favors the
            // other side, the HS exit leads to a dead end (e.g., OAK 28R J exits
            // left toward 28L with no parking). Override with parking side.
            return (parkingSide is not null) && (parkingSide != hsSide) ? parkingSide : hsSide;
        }

        // Parking proximity is the strongest non-HS signal — airports are designed
        // so exit taxiways lead toward the terminal/ramp area. Only fall back to
        // parallel-runway HS inference when parking proximity is inconclusive.
        if (parkingSide is not null)
        {
            return parkingSide;
        }

        ExitSide? parallelHsSide = FindParallelRunwayHsSide(runwayDesignator, runwayHeading);
        if (parallelHsSide is not null)
        {
            return parallelHsSide;
        }

        return null;
    }

    /// <summary>
    /// Enumerate all exits for a runway, searching both sides per taxiway.
    /// </summary>
    private List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> EnumerateExitsBothSides(string designator, TrueHeading rwyHeading)
    {
        var exits = new List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)>();
        var seen = new HashSet<int>();

        foreach (var node in Nodes.Values)
        {
            bool isCenterline = node.Edges.Any(e => e.MatchesRunway(designator));
            if (!isCenterline)
            {
                continue;
            }

            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in node.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }

                if (!searched.Add(edge.TaxiwayName))
                {
                    continue;
                }

                ExitSide[] sides = [ExitSide.Left, ExitSide.Right];
                foreach (var side in sides)
                {
                    var pref = new ExitPreference { Taxiway = edge.TaxiwayName, Side = side };
                    var result = FindAdjacentHoldShort(node, designator, rwyHeading, pref);
                    if (result is null)
                    {
                        continue;
                    }

                    // FindAdjacentHoldShort falls back to off-side when no on-side match
                    // exists. For enumeration we need strict side matching.
                    if (result.Value.Side != side)
                    {
                        continue;
                    }

                    if (!seen.Add(result.Value.Node.Id))
                    {
                        continue;
                    }

                    double? angle = ComputeExitAngle(result.Value.Node, result.Value.Taxiway, rwyHeading);
                    bool isHighSpeed = (angle is not null) && (angle.Value <= 45.0);
                    exits.Add((result.Value.Node.Id, side, isHighSpeed));
                }
            }
        }

        return exits;
    }

    /// <summary>
    /// Average distance from a side's hold-short nodes to their 3 nearest parking nodes.
    /// </summary>
    private double AvgParkingDistForSide(IEnumerable<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> sideExits)
    {
        var holdShortIds = sideExits.Select(e => e.HoldShortId).Distinct().ToList();
        if (holdShortIds.Count == 0)
        {
            return double.MaxValue;
        }

        double totalAvg = 0;
        int counted = 0;
        foreach (int hsId in holdShortIds)
        {
            if (!Nodes.TryGetValue(hsId, out var hsNode))
            {
                continue;
            }

            totalAvg += AverageNearestParkingDistanceNm(hsNode, ParkingSampleCount);
            counted++;
        }

        return counted > 0 ? totalAvg / counted : double.MaxValue;
    }

    /// <summary>
    /// Find a parallel runway (same heading ±10°) and return the side where its
    /// high-speed exits are. Used for traffic flow inheritance when this runway
    /// has no high-speed exits of its own.
    /// </summary>
    public ExitSide? FindParallelRunwayHsSide(string designator, TrueHeading runwayHeading)
    {
        foreach (var rwy in Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);

            if (
                string.Equals(id.End1, designator, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id.End2, designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            double rwBearing = GeoMath.BearingTo(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon, rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);
            double end1Heading = rwBearing;
            double end2Heading = (rwBearing + 180) % 360;

            double diff1 = Math.Abs(new TrueHeading(end1Heading).SignedAngleTo(runwayHeading));
            double diff2 = Math.Abs(new TrueHeading(end2Heading).SignedAngleTo(runwayHeading));

            string? parallelDesignator = null;
            double parallelBearing = 0;
            if (diff1 <= 10)
            {
                parallelDesignator = id.End1;
                parallelBearing = end1Heading;
            }
            else if (diff2 <= 10)
            {
                parallelDesignator = id.End2;
                parallelBearing = end2Heading;
            }

            if (parallelDesignator is null)
            {
                continue;
            }

            var parallelExits = EnumerateExitsBothSides(parallelDesignator, new TrueHeading(parallelBearing));
            int pHsLeft = parallelExits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Left));
            int pHsRight = parallelExits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Right));

            if (pHsLeft > pHsRight)
            {
                return ExitSide.Left;
            }

            if (pHsRight > pHsLeft)
            {
                return ExitSide.Right;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a runway exit that is ahead of the aircraft along the runway heading.
    /// Applies the given exit preference (taxiway name, side, or nearest).
    /// Returns the exit node and its taxiway name, or null if no suitable exit is ahead.
    /// </summary>
    public (GroundNode Node, string Taxiway)? FindExitAheadOnRunway(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        string? runwayDesignator,
        double maxSearchNm = 1.5
    )
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (var node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            // Only consider exits ahead of the aircraft along the runway
            double alongTrack = GeoMath.AlongTrackDistanceNm(node.Position, new LatLon(lat, lon), runwayHeading);
            if (alongTrack <= 0)
            {
                continue;
            }

            // Check for taxiway preference match
            bool matchesPreference = false;

            if (preference?.Taxiway is { } taxiway)
            {
                foreach (var edge in node.Edges)
                {
                    if (!edge.IsRunwayCenterline && edge.MatchesTaxiway(taxiway))
                    {
                        matchesPreference = true;
                        break;
                    }
                }
            }

            // Apply preference filters
            if (preference?.Taxiway is not null && !matchesPreference)
            {
                continue;
            }

            if (preference?.Side is { } side)
            {
                double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
                double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));
                bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
                if (!isOnRequestedSide)
                {
                    continue;
                }
            }

            // Score by along-track distance (prefer nearest ahead exit), biased toward parking
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = alongTrack + parkingBias;
            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        if (best is null)
        {
            return null;
        }

        string? taxiwayName = GetExitTaxiwayName(best);
        if (taxiwayName is null)
        {
            return null;
        }

        return (best, taxiwayName);
    }

    /// <summary>
    /// Number of nearest parking nodes to average when computing parking proximity bias.
    /// </summary>
    private const int ParkingSampleCount = 3;

    /// <summary>
    /// Weight applied to average parking distance when scoring exit candidates.
    /// Higher values make exits near parking more strongly preferred.
    /// </summary>
    private const double ParkingProximityWeight = 2.0;

    /// <summary>
    /// Score bonus (subtracted from score) for high-speed exits (≤45°) when no
    /// specific taxiway is requested. Ensures high-speed exits beat steeper exits
    /// at the same centerline node despite longer taxiway paths.
    /// </summary>
    private const double HighSpeedExitBonus = 0.15;

    /// <summary>
    /// Compute the average distance from a node to the N nearest parking nodes.
    /// Returns 0 if there are no parking nodes in the layout.
    /// </summary>
    private double AverageNearestParkingDistanceNm(GroundNode exitNode, int count)
    {
        // Collect distances to all parking nodes, keep the N smallest
        Span<double> nearest = stackalloc double[count];
        nearest.Fill(double.MaxValue);

        bool anyParking = false;
        foreach (var node in Nodes.Values)
        {
            if (node.Type != GroundNodeType.Parking)
            {
                continue;
            }

            anyParking = true;
            double dist = GeoMath.DistanceNm(exitNode.Position, node.Position);

            // Insert into sorted top-N if smaller than the current largest
            if (dist < nearest[count - 1])
            {
                nearest[count - 1] = dist;
                // Bubble down to maintain sorted order
                for (int i = count - 2; i >= 0; i--)
                {
                    if (nearest[i] > nearest[i + 1])
                    {
                        (nearest[i], nearest[i + 1]) = (nearest[i + 1], nearest[i]);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        if (!anyParking)
        {
            return 0;
        }

        // Average only the slots that were filled (handles layouts with fewer than N parking nodes)
        double sum = 0;
        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            if (nearest[i] < double.MaxValue)
            {
                sum += nearest[i];
                filled++;
            }
        }

        return filled > 0 ? sum / filled : 0;
    }

    /// <summary>
    /// Returns true if the node is closer to <paramref name="targetRunway"/>'s centerline
    /// than to any other runway's centerline. If there are no other runways, returns true.
    /// </summary>
    private bool IsCloserToRunway(GroundNode node, GroundRunway targetRunway)
    {
        double targetDist = MinDistanceToRunwayCenterline(node, targetRunway);

        foreach (var rwy in Runways)
        {
            if (ReferenceEquals(rwy, targetRunway))
            {
                continue;
            }

            double otherDist = MinDistanceToRunwayCenterline(node, rwy);
            if (otherDist < targetDist)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compute the minimum distance from a node to a runway's centerline polyline.
    /// Uses point-to-segment distances for each consecutive pair of coordinates.
    /// </summary>
    private static double MinDistanceToRunwayCenterline(GroundNode node, GroundRunway runway)
    {
        double minDist = double.MaxValue;
        var coords = runway.Coordinates;

        for (int i = 0; i < coords.Count - 1; i++)
        {
            double dist = PointToSegmentDistanceNm(
                node.Position.Lat,
                node.Position.Lon,
                coords[i].Lat,
                coords[i].Lon,
                coords[i + 1].Lat,
                coords[i + 1].Lon
            );
            if (dist < minDist)
            {
                minDist = dist;
            }
        }

        // Fallback: if runway has only one coordinate, use point-to-point distance
        if (coords.Count == 1)
        {
            minDist = GeoMath.DistanceNm(node.Position, new LatLon(coords[0].Lat, coords[0].Lon));
        }

        return minDist;
    }

    /// <summary>
    /// Approximate distance from a point to a line segment on the Earth's surface.
    /// Projects the point onto the segment and returns the distance to the nearest point
    /// (endpoint or projected point).
    /// </summary>
    private static double PointToSegmentDistanceNm(double pLat, double pLon, double aLat, double aLon, double bLat, double bLon)
    {
        // Use a flat-earth approximation (valid for short distances like runway widths)
        double cosLat = Math.Cos(pLat * Math.PI / 180.0);
        double dx = (bLon - aLon) * cosLat;
        double dy = bLat - aLat;
        double px = (pLon - aLon) * cosLat;
        double py = pLat - aLat;

        double segLenSq = (dx * dx) + (dy * dy);
        if (segLenSq < 1e-20)
        {
            return GeoMath.DistanceNm(pLat, pLon, aLat, aLon);
        }

        double t = Math.Clamp(((px * dx) + (py * dy)) / segLenSq, 0.0, 1.0);
        double closestLat = aLat + (t * (bLat - aLat));
        double closestLon = aLon + (t * (bLon - aLon));

        return GeoMath.DistanceNm(pLat, pLon, closestLat, closestLon);
    }

    /// <summary>
    /// Returns true if the node is a valid runway exit candidate. Filters out:
    /// - Parking/Helipad nodes
    /// - Nodes on the runway centerline (with RWY edges)
    /// - Nodes with no taxiway edges
    /// - Nodes that are closer to a different parallel runway
    /// - Nodes within the runway surface width (intermediate routing vertices
    ///   from GeoJSON LineStrings that sit just off the centerline but aren't
    ///   real taxiway exit points)
    /// </summary>
    private bool IsValidExitCandidate(GroundNode node, GroundRunway? targetRunway)
    {
        if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
        {
            return false;
        }

        if (HasRunwayCenterlineEdge(node))
        {
            return false;
        }

        bool hasTaxiwayEdge = false;
        foreach (var edge in node.Edges)
        {
            if (!edge.IsRunwayCenterline)
            {
                hasTaxiwayEdge = true;
                break;
            }
        }

        if (!hasTaxiwayEdge)
        {
            return false;
        }

        if (targetRunway is not null && !IsCloserToRunway(node, targetRunway))
        {
            return false;
        }

        // Filter out nodes within the runway surface. These are intermediate
        // GeoJSON routing vertices that sit just off the centerline but aren't
        // real taxiway intersections where an aircraft can exit.
        if (targetRunway is not null)
        {
            double crossTrackNm = MinDistanceToRunwayCenterline(node, targetRunway);
            double runwayHalfWidthNm = (targetRunway.WidthFt / 2.0) / 6076.12;
            double minExitDistanceNm = runwayHalfWidthNm + (50.0 / 6076.12);
            if (crossTrackNm < minExitDistanceNm)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRunwayCenterlineEdge(GroundNode node)
    {
        foreach (var edge in node.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                return true;
            }
        }

        return false;
    }

    // LatLon-shaped overloads of the find methods. Thin wrappers around the scalar forms above.

    public GroundNode? FindNearestNode(LatLon position) => FindNearestNode(position.Lat, position.Lon);

    public NearestTaxiEdge? FindNearestTaxiEdge(LatLon position) => FindNearestTaxiEdge(position.Lat, position.Lon);

    public GroundNode? FindNearestCenterlineNode(LatLon position, TrueHeading runwayHeading, string? runwayDesignator = null) =>
        FindNearestCenterlineNode(position.Lat, position.Lon, runwayHeading, runwayDesignator);

    public GroundNode? FindNearestExit(LatLon position, TrueHeading runwayHeading, string? runwayDesignator, double maxSearchNm = 0.5) =>
        FindNearestExit(position.Lat, position.Lon, runwayHeading, runwayDesignator, maxSearchNm);

    public GroundNode? FindExitByTaxiway(LatLon position, string taxiwayName, double maxSearchNm = 1.0) =>
        FindExitByTaxiway(position.Lat, position.Lon, taxiwayName, maxSearchNm);
}
