namespace Yaat.LayoutInspector;

public sealed class TextFormatter(TextWriter writer) : IFormatter
{
    public void WriteOverview(OverviewResult r) =>
        writer.WriteLine($"Airport: {r.AirportId} — {r.NodeCount} nodes, {r.EdgeCount} edges");

    public void WriteTaxiway(TaxiwayResult r) => writer.WriteLine($"Taxiway: {r.Name} — {r.Nodes.Count} nodes");

    public void WriteRunway(RunwayResult r) => writer.WriteLine($"Runway: {r.Designator}");

    public void WriteNode(NodeInfo n) => writer.WriteLine($"Node {n.Id}: {n.Type}");

    public void WriteExits(ExitsResult r) => writer.WriteLine($"Exits for {r.Designator}: {r.Exits.Count}");

    public void WriteBfsPath(BfsPathResult r) => writer.WriteLine($"BFS from {r.FromNodeId} via {r.Taxiway}");

    public void WriteNodeList(string title, List<NodeInfo> nodes) => writer.WriteLine($"{title}: {nodes.Count} nodes");
}
