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
    List<EdgeInfo> Edges
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
    bool IsRamp
);

public sealed record TaxiwayResult(string Name, List<NodeInfo> Nodes, List<string> ConnectedTaxiways, int HoldShortCount);

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

public sealed record FullDumpResult(
    OverviewResult Overview,
    Dictionary<int, NodeInfo> Nodes,
    Dictionary<string, TaxiwayResult> Taxiways,
    Dictionary<string, RunwayResult> Runways,
    Dictionary<string, ExitsResult> Exits,
    List<NodeInfo> Parking,
    List<NodeInfo> Spots
);

public sealed record BfsPathResult(
    int FromNodeId,
    string Taxiway,
    List<BfsStep> Steps,
    List<int>? FoundPath,
    double? TotalDistanceNm,
    string? HoldShortRunwayId
);
