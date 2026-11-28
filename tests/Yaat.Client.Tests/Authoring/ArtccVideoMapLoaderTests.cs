using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The route composer's map loader over an ARTCC's vNAS config: the maps it lists, their check-box order and names with no
/// download at all, and the one map a ticked layer downloads, described the way the config describes it.
/// </summary>
public sealed class ArtccVideoMapLoaderTests
{
    private const string Zoa = "ZOA";

    [Fact]
    public async Task TwoMaps_AreListedInStarsIdThenNameOrder()
    {
        using var artcc = new ConfigFolder();
        artcc.Write(Zoa, Map("B", "Brook", "", 2), Map("A", "Alpha", "", 7));

        IReadOnlyList<RouteComposerMap> maps = await ArtccVideoMapLoader.ListAsync(
            ScenarioEditorFixture.OfflineConfigs(artcc.Root),
            Zoa,
            CancellationToken.None
        );

        Assert.Equal(["Brook", "Alpha"], maps.Select(m => m.Name));
        Assert.Equal(["B", "A"], maps.Select(m => m.MapId));
    }

    [Fact]
    public async Task AMapWithNoStarsId_SortsLast_AndNamesDifferingOnlyInCaseKeepTheirConfigOrder()
    {
        using var artcc = new ConfigFolder();
        artcc.Write(Zoa, Map("CAP", "Alpha", "", 5), Map("NONE", "zeta", "", 0), Map("LOW", "alpha", "", 5));

        IReadOnlyList<RouteComposerMap> maps = await ArtccVideoMapLoader.ListAsync(
            ScenarioEditorFixture.OfflineConfigs(artcc.Root),
            Zoa,
            CancellationToken.None
        );

        Assert.Equal(["Alpha", "alpha", "zeta"], maps.Select(m => m.Name));
        Assert.Equal(["CAP", "LOW", "NONE"], maps.Select(m => m.MapId));
    }

    [Fact]
    public async Task ABlankName_FallsBackToTheShortName_ThenTheId()
    {
        using var artcc = new ConfigFolder();
        artcc.Write(Zoa, Map("FULL", "Full name", "Full short name", 1), Map("SHORT", "", "Short name", 2), Map("ID", "", "", 3));

        IReadOnlyList<RouteComposerMap> maps = await ArtccVideoMapLoader.ListAsync(
            ScenarioEditorFixture.OfflineConfigs(artcc.Root),
            Zoa,
            CancellationToken.None
        );

        Assert.Equal(["Full name", "Short name", "ID"], maps.Select(m => m.Name));
    }

    [Fact]
    public async Task AnUnreachableConfigWithNoSavedCopy_ThrowsWithTheLoadsProblem()
    {
        using var artcc = new ConfigFolder();

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArtccVideoMapLoader.ListAsync(ScenarioEditorFixture.OfflineConfigs(artcc.Root), Zoa, CancellationToken.None)
        );

        Assert.Equal("data-api could not be reached for ZOA", thrown.Message);
    }

    [Fact]
    public async Task AConfigWithNoMaps_ReturnsEmpty()
    {
        using var artcc = new ConfigFolder();
        artcc.Write(Zoa);

        IReadOnlyList<RouteComposerMap> maps = await ArtccVideoMapLoader.ListAsync(
            ScenarioEditorFixture.OfflineConfigs(artcc.Root),
            Zoa,
            CancellationToken.None
        );

        Assert.Empty(maps);
    }

    [Fact]
    public async Task LoadOneAsync_DownloadsTheOneMapAsTheConfigDescribesIt()
    {
        using var artcc = new ConfigFolder();
        artcc.Write(Zoa, Map("EAST", "East map", "EAST SHORT", 4), Map("WEST", "West map", "", 9));
        var asked = new List<(string Artcc, VideoMapInfoDto Map)>();

        VideoMapData? loaded = await ArtccVideoMapLoader.LoadOneAsync(
            (artccId, map, _) =>
            {
                asked.Add((artccId, map));
                return Task.FromResult<VideoMapData?>(new VideoMapData { MapId = map.Id, Lines = [] });
            },
            Zoa,
            "EAST",
            ScenarioEditorFixture.OfflineConfigs(artcc.Root),
            CancellationToken.None
        );

        (string artccId, VideoMapInfoDto map) = Assert.Single(asked);
        Assert.Equal(("ZOA", "EAST", "East map", "EAST SHORT", 4), (artccId, map.Id, map.Name, map.ShortName, map.StarsId));
        Assert.Equal("EAST", loaded!.MapId);
    }

    [Fact]
    public async Task LoadOneAsync_ForAMapTheConfigNoLongerLists_Throws()
    {
        using var artcc = new ConfigFolder();
        artcc.Write(Zoa, Map("EAST", "East map", "", 4));

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArtccVideoMapLoader.LoadOneAsync(
                (_, _, _) => Task.FromResult<VideoMapData?>(null),
                Zoa,
                "GONE",
                ScenarioEditorFixture.OfflineConfigs(artcc.Root),
                CancellationToken.None
            )
        );

        Assert.Equal("The ARTCC config no longer lists the video map GONE.", thrown.Message);
    }

    private static VideoMapConfig Map(string id, string name, string shortName, int starsId) =>
        new()
        {
            Id = id,
            Name = name,
            ShortName = shortName,
            StarsId = starsId,
        };

    // A cache folder holding one ARTCC config per ARTCC, read by an offline config source as a fresh copy.
    private sealed class ConfigFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "yaat-artcc-video-maps", Guid.NewGuid().ToString("N"));

        public void Write(string artccId, params VideoMapConfig[] maps)
        {
            Directory.CreateDirectory(Root);
            var root = new ArtccConfigRoot
            {
                Id = artccId,
                Facility = new FacilityConfig
                {
                    Id = artccId,
                    Type = "Artcc",
                    Name = "Oakland ARTCC",
                },
                VideoMaps = [.. maps],
            };
            File.WriteAllText(Path.Combine(Root, $"{artccId}.json"), JsonSerializer.Serialize(root));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
