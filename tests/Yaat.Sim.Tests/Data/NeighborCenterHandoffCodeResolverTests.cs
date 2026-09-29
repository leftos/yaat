using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Data;

/// <summary>
/// Decode coverage for <see cref="ArtccConfigResolver.ResolveEramToNeighborCenterHandoffCode"/>: the center-and-sector
/// form of ERAM field 16, <c>L((d)dd)</c> (EDSM SRS §C.1 field 16), where <c>L</c> is a neighbouring center's letter
/// (its own <c>eramConfiguration.nasId</c>, carried on the room's config as
/// <see cref="ArtccConfigRoot.NeighborCenterNasIds"/>). ZOA hands a track to ZLA sector 25 as <c>L25</c>; a sector
/// number of <c>00</c>/<c>000</c> is an undirected handoff to that center. The sector is named by its number, at least
/// two digits (<c>L025</c> is <c>L25</c>), and is not checked: another center's
/// sectors are not adapted here.
/// </summary>
public sealed class NeighborCenterHandoffCodeResolverTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ZOA's own letter is "O"; NCT's ERAM-to-STARS prefix is "Q". ZLA ("L") is the neighbour the tests hand off to;
    // the other three neighbour entries deliberately collide with the own-centre "C", the NCT prefix "Q" and ZOA's own
    // "O", which keep their existing meaning.
    private const string ZoaShapeJson = """
        {
          "id": "ZOA",
          "facility": {
            "id": "ZOA",
            "type": "Artcc",
            "name": "Oakland Center",
            "neighboringFacilityIds": [ "BFL", "ZLA", "ZLC", "ZSE", "ZAB" ],
            "eramConfiguration": {
              "nasId": "O",
              "neighboringStarsConfigurations": [ { "facilityId": "NCT", "singleCharacterStarsId": "Q" } ]
            },
            "childFacilities": [
              {
                "id": "NCT",
                "type": "Tracon",
                "name": "NorCal TRACON",
                "starsConfiguration": { "tcps": [ { "subset": 2, "sectorId": "B", "id": "tcp-nct-2b" } ] }
              }
            ]
          },
          "neighborCenterNasIds": { "ZLA": "L", "ZLC": "C", "ZSE": "Q", "ZAB": "O" }
        }
        """;

    private static ArtccConfigRoot Config() => JsonSerializer.Deserialize<ArtccConfigRoot>(ZoaShapeJson, JsonOptions)!;

    [Fact]
    public void VnasFields_AreRead()
    {
        ArtccConfigRoot config = Config();

        Assert.Equal(["BFL", "ZLA", "ZLC", "ZSE", "ZAB"], config.Facility.NeighboringFacilityIds);
        Assert.Equal("O", config.Facility.EramConfiguration!.NasId);
        Assert.Equal("L", config.NeighborCenterNasIds["ZLA"]);
    }

    [Theory]
    [InlineData("L25", "25")]
    [InlineData("L025", "25")] // the sector by number, at least two digits: L025 is L25
    [InlineData("L128", "128")] // the highest sector number
    [InlineData("L00", "00")] // undirected handoff to the center
    [InlineData("L000", "00")]
    [InlineData("l25", "25")] // the letter matches case-insensitively
    public void NeighbourLetterAndSector_ResolvesToThatCentersSector(string code, string sector)
    {
        TrackOwner? owner = Config().ResolveEramToNeighborCenterHandoffCode(code);

        Assert.Equal(TrackOwner.CreateEram($"ZLA_{sector}_CTR", "ZLA", sector), owner);
    }

    [Theory]
    [InlineData("L129")] // over the highest sector number
    [InlineData("L5")] // one digit is not (d)dd
    [InlineData("L1234")]
    [InlineData("L2X")]
    [InlineData("L")] // no sector
    [InlineData("")]
    [InlineData("X25")] // no neighbour's letter
    [InlineData("Z44")]
    [InlineData("C44")] // the own-centre prefix keeps its meaning
    [InlineData("Q25")] // a neighbouring STARS facility's prefix keeps its meaning
    [InlineData("O25")] // the own center's letter names no neighbour
    public void AnythingElse_IsNull(string code) => Assert.Null(Config().ResolveEramToNeighborCenterHandoffCode(code));

    [Fact]
    public void WithoutNeighbourLetters_NothingResolves()
    {
        ArtccConfigRoot config = Config();
        config.NeighborCenterNasIds.Clear();

        Assert.Null(config.ResolveEramToNeighborCenterHandoffCode("L25"));
    }

    [Fact]
    public void NeighbourLetters_SurviveTheRecordingArchive()
    {
        // The server bundles the room's config into a recording as JsonSerializer.Serialize(config) with default
        // naming; replay and bug bundles read it back through RecordingArchive.
        string artccJson = JsonSerializer.Serialize(Config(), new JsonSerializerOptions { WriteIndented = false });
        var recording = new SessionRecording
        {
            ScenarioJson = "{}",
            RngSeed = 0,
            Actions = [],
            TotalElapsedSeconds = 0,
            ArtccConfigJson = artccJson,
        };

        using var stream = new MemoryStream(RecordingArchiveWriter.WriteToBytes(recording));
        using var archive = RecordingArchive.Open(stream);
        ArtccConfigRoot? restored = archive.DeserializeArtccConfig();

        Assert.NotNull(restored);
        Assert.Equal("L", restored.NeighborCenterNasIds["ZLA"]);
        Assert.Equal(TrackOwner.CreateEram("ZLA_25_CTR", "ZLA", "25"), restored.ResolveEramToNeighborCenterHandoffCode("L25"));
    }
}
