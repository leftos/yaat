using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector;

public sealed class LayoutAnalyzer
{
    public AirportGroundLayout Layout { get; }
    public string AirportId { get; }

    public LayoutAnalyzer(AirportGroundLayout layout)
    {
        Layout = layout;
        AirportId = layout.AirportId;
    }

    public static LayoutAnalyzer Load(string geoJsonPath, string? airportCode)
    {
        string geoJson = File.ReadAllText(geoJsonPath);
        string airportId = Path.GetFileNameWithoutExtension(geoJsonPath).ToUpperInvariant();
        var layout = GeoJsonParser.Parse(airportId, geoJson, airportCode);
        return new LayoutAnalyzer(layout);
    }

    public OverviewResult GetOverview()
    {
        var countsByType = new Dictionary<string, int>();
        foreach (var node in Layout.Nodes.Values)
        {
            string typeName = node.Type.ToString();
            countsByType[typeName] = countsByType.GetValueOrDefault(typeName) + 1;
        }

        var taxiwayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in Layout.Edges)
        {
            if (!edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                taxiwayNames.Add(edge.TaxiwayName);
            }
        }

        var runwayWidths = Layout.Runways.Select(r => new RunwayWidthInfo(r.Name, r.WidthFt)).ToList();

        return new OverviewResult(
            AirportId,
            Layout.Nodes.Count,
            countsByType,
            Layout.Edges.Count,
            taxiwayNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            Layout.Runways.Select(r => r.Name).ToList(),
            runwayWidths
        );
    }

    // Stub query methods — implemented in later tasks
    public NodeInfo? GetNodeDetail(int id) => null;

    public TaxiwayResult GetTaxiwayDetail(string name) => new(name, [], [], 0);

    public RunwayResult GetRunwayDetail(string designator) => new(designator, [], []);

    public ExitsResult GetExits(string designator) => new(designator, []);

    public BfsPathResult GetBfsPath(int nodeId, string taxiway) => new(nodeId, taxiway, [], null, null, null);

    public List<NodeInfo> GetParking() => [];

    public List<NodeInfo> GetSpots() => [];
}
