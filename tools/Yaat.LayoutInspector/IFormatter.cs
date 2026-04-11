namespace Yaat.LayoutInspector;

public interface IFormatter
{
    void WriteOverview(OverviewResult result);
    void WriteTaxiway(TaxiwayResult result);
    void WriteRunway(RunwayResult result);
    void WriteNode(NodeInfo node);
    void WriteExits(ExitsResult result);
    void WriteBfsPath(BfsPathResult result);
    void WriteNodeList(string title, List<NodeInfo> nodes);
    void WriteIntersection(IntersectionResult result);
    void WriteValidation(ValidationResult result);
    void WritePathfinder(PathfinderResult result);
}
