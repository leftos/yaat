using System.Text.Json.Nodes;
using Avalonia;
using Xunit;
using Yaat.Client.Views.Authoring;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The parking picker's scene over the real KOAK vNAS map: every named parking, spot and helipad node and nothing else,
/// the centerlines for context, the box around the stands, and the current stand matched in any case.
/// </summary>
public class ParkingPickerSceneTests
{
    [Fact]
    public void StandScene_OnRealOakLayout_ListsParkingSpotAndHelipadNodesByName()
    {
        AirportGroundLayout layout = OakParkingLayout.Layout;
        GroundNode current = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && !string.IsNullOrWhiteSpace(n.Name))
            .OrderBy(n => n.Id)
            .First();

        var scene = ParkingPickerScene.Build(layout, " " + current.Name!.ToLowerInvariant() + " ");

        List<string> expected =
        [
            .. layout
                .Nodes.Values.Where(n =>
                    (n.Type is GroundNodeType.Parking or GroundNodeType.Spot or GroundNodeType.Helipad) && !string.IsNullOrWhiteSpace(n.Name)
                )
                .Select(n => $"{n.Type}:{n.Name}")
                .Order(StringComparer.Ordinal),
        ];
        Assert.Equal(expected, scene.Stands.Select(s => $"{s.Type}:{s.Name}").Order(StringComparer.Ordinal));
        Assert.Contains(scene.Stands, s => s.Type == GroundNodeType.Parking);
        Assert.Contains(scene.Stands, s => s.Type == GroundNodeType.Spot);
        Assert.Equal(scene.Stands.Select(s => s.Name), scene.Stands.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(current.Name, scene.CurrentStand);
        Assert.Null(ParkingPickerScene.Build(layout, "NO SUCH STAND").CurrentStand);

        ParkingPickerBounds bounds = scene.Bounds!;
        Assert.Equal(scene.Stands.Min(s => s.Lat), bounds.MinLat);
        Assert.Equal(scene.Stands.Max(s => s.Lat), bounds.MaxLat);
        Assert.Equal(scene.Stands.Min(s => s.Lon), bounds.MinLon);
        Assert.Equal(scene.Stands.Max(s => s.Lon), bounds.MaxLon);
        Assert.Contains(scene.Lines, l => l.Kind == ParkingPickerLineKind.Taxiway);
        Assert.Contains(scene.Lines, l => l.Kind == ParkingPickerLineKind.Runway);
    }

    // No committed vNAS map has a helipad-typed feature (AVL's and LZU's "HELIPAD" are parking), so the case is the real
    // KOAK map with one parking feature retyped to helipad, as the Airport Editor's scene tests retype a taxiway.
    [Fact]
    public void StandScene_OnOakWithAParkingRetypedAsAHelipad_ListsItAsAHelipad()
    {
        JsonNode root = JsonNode.Parse(OakParkingLayout.GeoJson)!;
        JsonObject retyped = root["features"]!
            .AsArray()
            .Select(f => f!["properties"]!.AsObject())
            .First(p => ((string?)p["type"] == "parking") && !string.IsNullOrWhiteSpace((string?)p["name"]));
        retyped["type"] = "helipad";
        string name = (string)retyped["name"]!;
        AirportGroundLayout layout = GeoJsonParser.Parse(
            "oak",
            root.ToJsonString(),
            "OAK",
            new LayoutInputs([], VideoMap: null, SpotEdges: [], CornerFillets: CornerFilletOverrides.None)
        );

        var scene = ParkingPickerScene.Build(layout, null);

        ParkingPickerStand helipad = Assert.Single(scene.Stands, s => s.Type == GroundNodeType.Helipad);
        Assert.Equal(name, helipad.Name);
    }
}

/// <summary>The committed real KOAK vNAS map, parsed once as the downloader parses it (no sidecar inputs).</summary>
internal static class OakParkingLayout
{
    private static readonly Lazy<string> Text = new(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson")));

    private static readonly Lazy<AirportGroundLayout> Parsed = new(() =>
        GeoJsonParser.Parse("oak", Text.Value, "OAK", new LayoutInputs([], VideoMap: null, SpotEdges: [], CornerFillets: CornerFilletOverrides.None))
    );

    /// <summary>Gets the map's text.</summary>
    public static string GeoJson => Text.Value;

    /// <summary>Gets the parsed layout.</summary>
    public static AirportGroundLayout Layout => Parsed.Value;

    /// <summary>Gets the name of the named parking node with the lowest id.</summary>
    public static string FirstParkingName =>
        Layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && !string.IsNullOrWhiteSpace(n.Name)).OrderBy(n => n.Id).First().Name!;

    /// <summary>A stand whose nearest other stand is at least three hit ranges away on the screen, with its position.</summary>
    public static (ParkingPickerStand Stand, Point At) IsolatedStand(ParkingPickerScene scene, MapViewport viewport, string? notNamed = null)
    {
        List<(ParkingPickerStand Stand, Point At)> projected =
        [
            .. scene.Stands.Select(s =>
            {
                (float x, float y) = viewport.LatLonToScreen(s.Lat, s.Lon);
                return (s, new Point(x, y));
            }),
        ];
        foreach ((ParkingPickerStand stand, Point at) in projected)
        {
            bool inView = (at.X > 40) && (at.Y > 40) && (at.X < viewport.PixelWidth - 40) && (at.Y < viewport.PixelHeight - 40);
            if (!inView || string.Equals(stand.Name, notNamed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double nearest = projected.Where(p => !ReferenceEquals(p.Stand, stand)).Min(p => Distance(p.At, at));
            if (nearest >= 3 * ParkingPickerHitTest.Range)
            {
                return (stand, at);
            }
        }

        throw new InvalidOperationException("no isolated stand in view");
    }

    public static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
