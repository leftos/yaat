using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Views.Authoring;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Airport Editor reads a feature's type and name as the layout parser does: the type matched exactly, so a
/// <c>Taxiway</c> is not a taxiway; the name under any case of its key, a number as its JSON text. The cases are real
/// KOAK taxiways with that one member changed, read by both the editor's scene and <see cref="GeoJsonParser"/>.
/// </summary>
public class AirportEditorSceneTests
{
    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");

    [Fact]
    public void ATaxiwayTypedFeature_IsNotDrawn_AndANumberUnderName_IsTheLabel_AsTheParserReadsThem()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(OakPath))!;
        List<JsonObject> taxiways =
        [
            .. root["features"]!.AsArray().Select(f => f!["properties"]!.AsObject()).Where(p => (string?)p["type"] == "taxiway"),
        ];
        int retypedIndex = IndexOf(root, taxiways[0]);
        taxiways[0]["type"] = "Taxiway";
        taxiways[1].Remove("name");
        taxiways[1]["Name"] = 7;
        string text = root.ToJsonString();

        var scene = AirportEditorScene.Build(AirportMapDocument.Read(text));
        (AirportGroundLayout layout, IReadOnlyList<GroundMapIssue> issues, _) = GeoJsonParser.ParseWithDiagnostics(
            "OAK",
            text,
            runwayAirportCode: null,
            new LayoutInputs([], VideoMap: null, SpotEdges: [], CornerFillets: CornerFilletOverrides.None)
        );

        Assert.Equal(taxiways.Count - 1, scene.Shapes.Count(s => s.Kind == AirportEditorFeatureKind.Taxiway));
        Assert.Contains(issues, i => (i.Kind == GroundMapIssueKind.UnknownFeatureType) && (i.FeatureIndex == retypedIndex));

        Assert.Contains(scene.Shapes, s => (s.Kind == AirportEditorFeatureKind.Taxiway) && (s.Label == "7"));
        Assert.Contains(layout.Edges, e => e.TaxiwayName == "7");
    }

    private static int IndexOf(JsonNode root, JsonObject properties) =>
        root["features"]!.AsArray().Select((f, i) => (f, i)).First(x => ReferenceEquals(x.f!["properties"], properties)).i;
}
