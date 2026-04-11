using System.Text.Json;

namespace Yaat.LayoutInspector;

public sealed class JsonFormatter(TextWriter writer) : IFormatter
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void WriteOverview(OverviewResult r) => writer.WriteLine(JsonSerializer.Serialize(r, Opts));

    public void WriteTaxiway(TaxiwayResult r) => writer.WriteLine(JsonSerializer.Serialize(r, Opts));

    public void WriteRunway(RunwayResult r) => writer.WriteLine(JsonSerializer.Serialize(r, Opts));

    public void WriteNode(NodeInfo n) => writer.WriteLine(JsonSerializer.Serialize(n, Opts));

    public void WriteExits(ExitsResult r) => writer.WriteLine(JsonSerializer.Serialize(r, Opts));

    public void WriteBfsPath(BfsPathResult r) => writer.WriteLine(JsonSerializer.Serialize(r, Opts));

    public void WriteNodeList(string title, List<NodeInfo> nodes) => writer.WriteLine(JsonSerializer.Serialize(new { title, nodes }, Opts));

    public void WriteIntersection(IntersectionResult r) => writer.WriteLine(JsonSerializer.Serialize(r, Opts));

    public void WriteValidation(ValidationResult r) => writer.WriteLine(JsonSerializer.Serialize(r, Opts));
}
