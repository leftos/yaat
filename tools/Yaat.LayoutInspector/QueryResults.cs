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
    double MaxSafeSpeedKts20,
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
