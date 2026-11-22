using System.Net;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Data.Vnas;

/// <summary>
/// <see cref="ArtccPositionCatalog"/> lists every position of an ARTCC's facility tree, then its neighbouring centers',
/// as the Scenario Editor's position pickers show them; <see cref="ArtccNeighbourLoader"/> reads the neighbouring
/// centers named in the config and lists those it could not read. Over the committed ZOA and ZLA snapshots.
/// </summary>
public class ArtccPositionCatalogTests : IDisposable
{
    private const string ZoaSector14Id = "01GEAT1S60WNVZ3VQXP7TSQF84"; // OAK_14_CTR, ZOA's own ERAM facility
    private const string SfoLocalWestId = "01GEAKXT8XMTHGDP3VQJ4C8KS6"; // SFO_TWR "LC SFOW", STARS
    private const string FatRampId = "01K8VYH7DY9NBEKP85NRWZBYCW"; // FAT_RMP, ATIS, neither ERAM nor STARS
    private const string ZlaSector16Id = "01G90YC6C0HZEGQ3FYCDPVFDQW"; // LAX_16_CTR

    private const string ZoaMissing = "the committed ZOA snapshot is not in the test output";
    private const string ZlaMissing = "the committed ZLA snapshot is not in the test output";

    private static readonly string ZlaSnapshotPath = Path.Combine(AppContext.BaseDirectory, "TestData", "artcc-zla-snapshot.json");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-artcc-catalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Frequency_IsInHertz_AndShownInMegahertz()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.SkipUnless(zoa is not null, ZoaMissing);

        Assert.Equal(134_550_000L, zoa.FindPosition(ZoaSector14Id).Position?.Frequency);
        ArtccPositionRow row = Row(ArtccPositionCatalog.Build(zoa, [], []), ZoaSector14Id);
        Assert.Equal(134.55, row.FrequencyMhz);
    }

    [Fact]
    public void KnownZoaPositions_CarryTheirKindAndOwningFacility()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.SkipUnless(zoa is not null, ZoaMissing);

        var catalog = ArtccPositionCatalog.Build(zoa, [], []);

        ArtccPositionRow center = Row(catalog, ZoaSector14Id);
        Assert.Equal((ArtccPositionKind.Eram, "ZOA", "Artcc", "OAK_14_CTR"), (center.Kind, center.FacilityId, center.FacilityType, center.Callsign));
        ArtccPositionRow tower = Row(catalog, SfoLocalWestId);
        Assert.Equal((ArtccPositionKind.Stars, "SFO", "Atct", "LC SFOW"), (tower.Kind, tower.FacilityId, tower.FacilityType, tower.Name));
        ArtccPositionRow ramp = Row(catalog, FatRampId);
        Assert.Equal((ArtccPositionKind.Other, "FAT", "AtctTracon"), (ramp.Kind, ramp.FacilityId, ramp.FacilityType));
        Assert.All(catalog.Positions, r => Assert.Equal(("ZOA", false), (r.ArtccId, r.IsNeighbour)));
    }

    [Fact]
    public void NeighbourRows_FollowTheOwnRows_FlaggedAsNeighbours()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        ArtccConfigRoot? zla = TestArtccConfig.LoadZla();
        Assert.SkipUnless((zoa is not null) && (zla is not null), ZlaMissing);

        var catalog = ArtccPositionCatalog.Build(zoa, [zla], []);

        ArtccPositionRow neighbour = Row(catalog, ZlaSector16Id);
        Assert.Equal(("ZLA", "ZLA", true, ArtccPositionKind.Eram), (neighbour.ArtccId, neighbour.FacilityId, neighbour.IsNeighbour, neighbour.Kind));
        int lastOwn = catalog.Positions.ToList().FindLastIndex(r => !r.IsNeighbour);
        int firstNeighbour = catalog.Positions.ToList().FindIndex(r => r.IsNeighbour);
        Assert.True(lastOwn < firstNeighbour, $"own rows end at {lastOwn}, neighbour rows start at {firstNeighbour}");
        Assert.Equal("ZOA", catalog.Positions[0].ArtccId);
    }

    [Fact]
    public void NeighbourArtccIds_KeepTheCentersOnly()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.SkipUnless(zoa is not null, ZoaMissing);

        // ZOA's neighbours are BFL, JCF, LSV, SBA, ZLA, ZLC and ZSE; the terminal facilities are not ARTCCs.
        Assert.Equal(["ZLA", "ZLC", "ZSE"], ArtccNeighbourLoader.NeighbourArtccIds(zoa));
    }

    [Fact]
    public async Task LoadCatalog_ReadsTheNeighbouringCenters_AndListsTheOnesItCouldNotRead()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.SkipUnless((zoa is not null) && File.Exists(ZlaSnapshotPath), ZlaMissing);
        var handler = new FakeArtccHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/api/artccs/ZLA" => ArtccConfigSourceTests.Ok(File.ReadAllText(ZlaSnapshotPath)),
                "/api/artccs/ZLC" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => throw new HttpRequestException("offline"),
            }
        );
        var source = new ArtccConfigSource(new HttpClient(handler), _root, () => DateTime.UtcNow);

        ArtccPositionCatalog catalog = await ArtccNeighbourLoader.LoadCatalogAsync(source, zoa, TestContext.Current.CancellationToken);

        Assert.Equal(["ZLC", "ZSE"], catalog.UnreadNeighbours);
        Assert.True(Row(catalog, ZlaSector16Id).IsNeighbour);
        Assert.Equal(["/api/artccs/ZLA", "/api/artccs/ZLC", "/api/artccs/ZSE"], handler.Requests.Select(u => u.AbsolutePath).Order());
    }

    [Fact]
    public async Task LoadCatalog_ASevenHourOldNeighbourFile_IsReadWithNoRequest()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.SkipUnless((zoa is not null) && File.Exists(ZlaSnapshotPath), ZlaMissing);
        DateTime now = DateTime.UtcNow;
        Directory.CreateDirectory(_root);
        string zlaPath = Path.Combine(_root, "ZLA.json");
        File.Copy(ZlaSnapshotPath, zlaPath);
        File.SetLastWriteTimeUtc(zlaPath, now - TimeSpan.FromHours(7));
        var handler = new FakeArtccHandler(_ => throw new HttpRequestException("offline"));
        var source = new ArtccConfigSource(new HttpClient(handler), _root, () => now);

        ArtccPositionCatalog catalog = await ArtccNeighbourLoader.LoadCatalogAsync(source, zoa, TestContext.Current.CancellationToken);

        // Seven hours is past the own-config TTL but inside the neighbour one.
        Assert.True(Row(catalog, ZlaSector16Id).IsNeighbour);
        Assert.Equal(["ZLC", "ZSE"], catalog.UnreadNeighbours);
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath == "/api/artccs/ZLA");
    }

    [Fact]
    public void Contains_AKnownPosition_AndNotAnUnknownOne()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.SkipUnless(zoa is not null, ZoaMissing);

        var catalog = ArtccPositionCatalog.Build(zoa, [], []);

        Assert.True(catalog.Contains("zoa", "SFO", SfoLocalWestId));
        Assert.False(catalog.Contains("ZOA", "OAK", SfoLocalWestId));
        Assert.False(catalog.Contains("ZOA", "SFO", "01AAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void OneCallsignUnderTwoFacilities_IsTwoRows()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.SkipUnless(zoa is not null, ZoaMissing);

        // ZOA lists OAK_TWR under the OAK tower and under O90: two positions with their own ids.
        List<ArtccPositionRow> rows = [.. ArtccPositionCatalog.Build(zoa, [], []).Positions.Where(r => r.Callsign == "OAK_TWR")];

        Assert.Equal(["O90", "OAK"], rows.Select(r => r.FacilityId).Order());
        Assert.Equal(2, rows.Select(r => r.PositionId).Distinct().Count());
    }

    private static ArtccPositionRow Row(ArtccPositionCatalog catalog, string positionId) =>
        Assert.Single(catalog.Positions, r => r.PositionId == positionId);
}
