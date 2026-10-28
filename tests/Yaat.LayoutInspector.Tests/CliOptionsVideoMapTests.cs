using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;

namespace Yaat.LayoutInspector.Tests;

/// <summary>
/// <c>--videomap &lt;path&gt;</c> names the tower-cab videomap GeoJSON the airport is parsed with. Without it the layout is
/// parsed with no map, as before the flag existed.
/// </summary>
public class CliOptionsVideoMapTests
{
    [Fact]
    public void VideoMap_Flag_SetsThePath()
    {
        CliOptions options = Parse("--videomap", "some/map.geojson");

        Assert.Equal("some/map.geojson", options.VideoMapPath);
    }

    [Fact]
    public void NoVideoMapFlag_LeavesThePathNull()
    {
        CliOptions options = Parse("--fillet-mode", "standard");

        Assert.Null(options.VideoMapPath);
    }

    /// <summary>
    /// The OAK graph parsed with its tower-cab videomap seats hold-short bars on the painted bars, so the inspector can show
    /// the graph the server builds instead of the rule-seated one.
    /// </summary>
    [Fact]
    public void Load_WithTheOakVideoMap_MovesHoldShortBars()
    {
        string testData =
            Bootstrap.FindDefaultNavDataDir() ?? throw new Xunit.Sdk.XunitException($"no yaat.slnx at or above {AppContext.BaseDirectory}");
        TestVnasData.SetTestDataDir(testData);
        TestVnasData.EnsureInitialized();
        string layoutPath = Path.Combine(testData, "oak.geojson");
        string mapPath = Path.Combine(testData, "TowerCabMaps", "oak.geojson");

        var map = TowerCabVideoMap.Read(File.ReadAllText(mapPath));
        HashSet<(double Lat, double Lon)> rule = HoldShortPositions(LayoutAnalyzer.Load(layoutPath, "OAK", FilletMode.Standard, null));
        HashSet<(double Lat, double Lon)> snapped = HoldShortPositions(LayoutAnalyzer.Load(layoutPath, "OAK", FilletMode.Standard, map));

        Assert.False(
            rule.SetEquals(snapped),
            $"the videomap did not move any hold-short bar: {snapped.Count} bar positions with the map, {rule.Count} without"
        );
    }

    private static HashSet<(double Lat, double Lon)> HoldShortPositions(LayoutAnalyzer analyzer) =>
        [
            .. analyzer
                .Layout.Nodes.Values.Where(node => node.Type == GroundNodeType.RunwayHoldShort)
                .Select(node => (Lat: Math.Round(node.Position.Lat, 7), Lon: Math.Round(node.Position.Lon, 7))),
        ];

    private static CliOptions Parse(params string[] args)
    {
        string[] all = ["oak.geojson", .. args];
        Assert.True(CliOptions.TryParse(all, out CliOptions options, out string? error), error);
        return options;
    }
}
