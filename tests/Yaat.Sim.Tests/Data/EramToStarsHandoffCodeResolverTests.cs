using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Decode coverage for <see cref="ArtccConfigResolver.ResolveEramToStarsHandoffCode"/> — the code an ERAM
/// (Center) controller types to hand off to a position in a neighboring STARS facility. The leading
/// character is the facility's <c>singleCharacterStarsId</c> from the ARTCC's
/// <c>eramConfiguration.neighboringStarsConfigurations</c> (ZOA → NCT "Q", FAT "F"; SBA uses the optional
/// <c>fieldELetter</c> override "S"); the remainder is the receiving TCP code (subset+sector). So NCT's
/// Boulder position (subset 2, sector B) is reached as <c>Q2B</c>.
/// </summary>
public sealed class EramToStarsHandoffCodeResolverTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ZOA holds the neighbor prefix table on its eramConfiguration. NCT/FAT/SBA are the receiving terminal
    // facilities with their own TCP sectors. No positions are declared, so a resolved TCP collapses to a
    // facility-level STARS owner (empty callsign) — enough to assert the facility/subset/sector decoded.
    private const string ZoaShapeJson = """
        {
          "id": "ZOA",
          "facility": {
            "id": "ZOA",
            "type": "Artcc",
            "name": "Oakland Center",
            "eramConfiguration": {
              "neighboringStarsConfigurations": [
                { "facilityId": "NCT", "singleCharacterStarsId": "Q" },
                { "facilityId": "FAT", "singleCharacterStarsId": "F" },
                { "facilityId": "SBA", "singleCharacterStarsId": "B", "fieldELetter": "S" }
              ]
            },
            "childFacilities": [
              {
                "id": "NCT",
                "type": "Tracon",
                "name": "NorCal TRACON",
                "starsConfiguration": {
                  "tcps": [
                    { "subset": 2, "sectorId": "B", "id": "tcp-nct-2b" },
                    { "subset": 2, "sectorId": "W", "id": "tcp-nct-2w" }
                  ]
                }
              },
              {
                "id": "FAT",
                "type": "AtctTracon",
                "name": "Fresno ATCT",
                "starsConfiguration": {
                  "tcps": [ { "subset": 1, "sectorId": "F", "id": "tcp-fat-1f" } ]
                }
              },
              {
                "id": "SBA",
                "type": "Tracon",
                "name": "Santa Barbara TRACON",
                "starsConfiguration": {
                  "tcps": [ { "subset": 1, "sectorId": "R", "id": "tcp-sba-1r" } ]
                }
              }
            ]
          }
        }
        """;

    private static ArtccConfigRoot Config() => JsonSerializer.Deserialize<ArtccConfigRoot>(ZoaShapeJson, JsonOptions)!;

    [Theory]
    [InlineData("Q2B", "NCT", 2, "B")] // NCT Boulder — the issue #216 case
    [InlineData("Q2W", "NCT", 2, "W")]
    [InlineData("F1F", "FAT", 1, "F")]
    [InlineData("q2b", "NCT", 2, "B")] // prefix match is case-insensitive
    [InlineData("S1R", "SBA", 1, "R")] // resolves via the fieldELetter override, not singleCharacterStarsId
    public void ResolveEramToStarsHandoffCode_ValidPrefix_ResolvesReceivingFacilityAndSector(
        string code,
        string expectedFacility,
        int expectedSubset,
        string expectedSector
    )
    {
        var owner = Config().ResolveEramToStarsHandoffCode(code);

        Assert.NotNull(owner);
        Assert.Equal(TrackOwnerType.Stars, owner.OwnerType);
        Assert.Equal(expectedFacility, owner.FacilityId);
        Assert.Equal(expectedSubset, owner.Subset);
        Assert.Equal(expectedSector, owner.SectorId);
    }

    [Theory]
    [InlineData("Z2B")] // Z is not a configured neighbor prefix
    [InlineData("Q9Z")] // valid prefix, but NCT has no sector 9Z
    [InlineData("2B")] // bare TCP — '2' is not a neighbor prefix (this resolves via ResolveTcpCode, not here)
    [InlineData("Q")] // too short to carry a TCP
    [InlineData("")]
    public void ResolveEramToStarsHandoffCode_InvalidCode_ReturnsNull(string code)
    {
        Assert.Null(Config().ResolveEramToStarsHandoffCode(code));
    }

    [Fact]
    public void ResolveTcpCode_BareTcp_StillResolvesDirectly()
    {
        // Sanity: the bare TCP path (no prefix) is unaffected — 2B resolves within NCT directly.
        var owner = Config().ResolveTcpCode("NCT", "2B");
        Assert.NotNull(owner);
        Assert.Equal("NCT", owner.FacilityId);
        Assert.Equal(2, owner.Subset);
        Assert.Equal("B", owner.SectorId);
    }
}
