namespace Yaat.LayoutInspector;

public sealed record OverviewResult(
    string AirportId,
    int NodeCount,
    Dictionary<string, int> NodeCountsByType,
    int EdgeCount,
    int ArcCount,
    List<string> TaxiwayNames,
    List<string> RunwayNames,
    List<RunwayWidthInfo> RunwayWidths
);

public sealed record RunwayWidthInfo(string Name, double WidthFt);

public sealed record NodeInfo(
    int Id,
    double Latitude,
    double Longitude,
    string Type,
    string? Name,
    string? RunwayId,
    double? HeadingDeg,
    List<EdgeInfo> Edges,
    string? Origin,
    int ArcCount
);

public sealed record EdgeInfo(
    int NeighborId,
    string TaxiwayName,
    double DistanceNm,
    string NeighborType,
    string? NeighborName,
    string? NeighborRunwayId,
    bool IsArc,
    bool IsRunway,
    bool IsRamp,
    /// <summary>Bearing from the parent node toward the neighbor, in degrees true.</summary>
    double BearingDeg,
    ArcDetail? Arc,
    string? Origin
);

/// <summary>
/// Arc-specific geometry details. Only populated when the edge is a <see cref="Yaat.Sim.Data.Airport.AirportGroundLayout.GroundArc"/>.
/// </summary>
public sealed record ArcDetail(
    string[] TaxiwayNames,
    double MinRadiusOfCurvatureFt,
    double MaxSafeSpeedKts,
    double ArcLengthNm,
    /// <summary>Tangent direction (degrees true) at the parent node. For bezier arcs, this is the
    /// direction from P0→P1 (when parent is Nodes[0]) or P3→P2 (when parent is Nodes[1]).</summary>
    double TangentAtParentDeg,
    double TurnAngleDeg,
    double EdgeBearingAtNode0Deg,
    double EdgeBearingAtNode1Deg,
    double P1Lat,
    double P1Lon,
    double P2Lat,
    double P2Lon
);

/// <summary>
/// Pairwise angle diagnostic for the edges fanning out of one node (via <c>--node-angles</c>).
/// Mirrors what <c>CornerPlanner</c> reasons about: for each pair of edges, the included fan
/// angle and the deflection (turn) an aircraft makes between them, plus the shortest alternate
/// path between the two arms that does NOT pass through this node — the bridging taxiway (e.g.
/// G bridges a C↔D corner), which makes a direct corner-chord between the pair redundant.
/// </summary>
public sealed record NodeAnglesResult(int NodeId, string NodeType, IReadOnlyList<EdgePairAngle> Pairs);

public sealed record EdgePairAngle(
    string TaxiwayA,
    int NeighborA,
    string TaxiwayB,
    int NeighborB,
    /// <summary>Included angle between the two outbound directions: ~0° = same direction (hairpin), ~180° = opposite (straight through).</summary>
    double FanAngleDeg,
    /// <summary>Deflection an aircraft makes turning between the two arms (180 − fan). High = sharp corner, hard/impossible to fillet.</summary>
    double TurnAngleDeg,
    BridgeInfo? Bridge
);

/// <summary>
/// Shortest alternate route between an edge pair's two neighbors that avoids the shared node.
/// <see cref="BridgeTaxiways"/> excludes the pair's own two taxiways, isolating the connector(s)
/// that already join the two arms (the reason a direct corner-chord would be redundant).
/// </summary>
public sealed record BridgeInfo(IReadOnlyList<string> BridgeTaxiways, IReadOnlyList<int> NodeIds, double DistanceFt, int Hops);

public sealed record TaxiwayIntersectionInfo(string OtherTaxiway, int NodeId);

public sealed record IntersectionResult(string Taxiway1, string Taxiway2, List<NodeInfo> Nodes);

/// <summary>
/// Great-circle (straight-line) distance and bearing between two node positions, from <c>--distance</c>.
/// <see cref="BearingDeg"/> is the bearing (°true) from <paramref name="FromNodeId"/> to <paramref name="ToNodeId"/>.
/// </summary>
public sealed record NodeDistanceResult(int FromNodeId, int ToNodeId, double StraightLineNm, double StraightLineFt, double BearingDeg);

/// <summary>
/// One leg of a <c>--path-distance</c> walk. <see cref="Mode"/> is <c>"edge"</c> when a direct graph
/// edge connects the two nodes (so <see cref="Nm"/> is the true arc-aware travel distance) or
/// <c>"straight"</c> when no edge exists and the great-circle distance is used as a fallback.
/// <see cref="BearingDeg"/> is the great-circle bearing (°true) from this leg's start node to its end node.
/// </summary>
public sealed record PathDistanceLeg(int FromNodeId, int ToNodeId, string Mode, double Nm, double Ft, double BearingDeg);

/// <summary>
/// Cumulative distance and bearing along a node sequence (<c>--path-distance N1 N2 …</c>): per-leg
/// distance + bearing, the total distance, and two curvature summaries — <see cref="HeadingRangeDeg"/>
/// (max−min leg bearing) and <see cref="TotalTurnDeg"/> (sum of absolute bearing change between adjacent
/// legs). A near-zero turn means the chain is straight (a beeline); a large turn means it tracks a curve.
/// </summary>
public sealed record PathDistanceResult(
    IReadOnlyList<int> NodeIds,
    IReadOnlyList<PathDistanceLeg> Legs,
    double TotalNm,
    double TotalFt,
    double HeadingRangeDeg,
    double TotalTurnDeg
);

public sealed record ValidationResult(int WarningCount, List<ValidationWarningDto> Warnings);

public sealed record ValidationWarningDto(string Code, string Message, string? Origin);

public sealed record TaxiwayResult(
    string Name,
    List<NodeInfo> Nodes,
    List<string> ConnectedTaxiways,
    int HoldShortCount,
    List<TaxiwayIntersectionInfo> Intersections
);

public sealed record RunwayResult(string Designator, List<NodeInfo> CenterlineNodes, List<NodeInfo> HoldShortNodes);

public sealed record ExitCandidate(
    int CenterlineNodeId,
    int HoldShortNodeId,
    string Taxiway,
    int PathLength,
    double TotalDistanceNm,
    double? AngleDeg,
    string Side,
    bool IsHighSpeed,
    List<int> PathNodeIds
);

public sealed record ExitsResult(
    string Designator,
    List<ExitCandidate> Exits,
    int HighSpeedLeft,
    int HighSpeedRight,
    double AvgParkingDistLeft,
    double AvgParkingDistRight,
    int ReachableParkingLeft,
    int ReachableParkingRight,
    string? ParallelHsSide,
    string? InferredDefaultSide
);

public sealed record BfsStep(int NodeId, string NodeType, int Depth, List<BfsEdgeExplored> EdgesExplored);

public sealed record BfsEdgeExplored(int NeighborId, string TaxiwayName, double DistanceNm, string NeighborType, string Action, string Reason);

public sealed record RawEdgeInfo(int From, int To, string TaxiwayName, double DistanceNm, bool IsRunway, string? Origin);

public sealed record RawArcInfo(
    int From,
    int To,
    string TaxiwayName,
    string[] TaxiwayNames,
    double DistanceNm,
    double MinRadiusFt,
    double TurnAngleDeg,
    string? Origin
);

public sealed record FullDumpResult(
    OverviewResult Overview,
    Dictionary<int, NodeInfo> Nodes,
    Dictionary<string, TaxiwayResult> Taxiways,
    Dictionary<string, RunwayResult> Runways,
    Dictionary<string, ExitsResult> Exits,
    List<NodeInfo> Parking,
    List<NodeInfo> Spots,
    List<RawEdgeInfo> Edges,
    List<RawArcInfo> Arcs
);

public sealed record BfsPathResult(
    int FromNodeId,
    string Taxiway,
    List<BfsStep> Steps,
    List<int>? FoundPath,
    double? TotalDistanceNm,
    string? HoldShortRunwayId
);

public sealed record PathfinderSegment(string TaxiwayName, int FromNodeId, int ToNodeId);

public sealed record PathfinderResult(
    int FromNodeId,
    List<string> Taxiways,
    List<string> DiagnosticLog,
    List<PathfinderSegment>? Segments,
    string? FailReason
);
