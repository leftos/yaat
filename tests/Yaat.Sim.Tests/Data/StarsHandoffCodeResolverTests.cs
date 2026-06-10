using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Decode coverage for <see cref="ArtccConfigResolver.ResolveStarsHandoffCode"/> — the STARS
/// interfacility handoff entry CRC sends when a controller types the triangle/delta symbol
/// (<c>`</c>/tilde key) plus a code. The leading digit is the handoff number, looked up against the
/// sender facility's <c>starsHandoffIds</c> to find the receiving facility; any remainder is the
/// receiving TCP code. Config shape mirrors ZOA's real adaptation: NCT hands off
/// <c>1 → SUU, 3 → FAT, 4 → NLC, 5 → NFL</c>; FAT carries sectors 1F (Friant)/1H (Chandler); SUU
/// carries 1N (North)/1S (South).
/// </summary>
public sealed class StarsHandoffCodeResolverTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // NCT is the sender (its starsHandoffIds match the ZOA NCT SOP table). FAT/SUU/NFL/NLC are the
    // receiving terminal facilities with their own TCP sectors. No positions are declared, so a
    // resolved TCP collapses to a facility-level STARS owner (empty callsign) — enough to assert the
    // facility/subset/sector the code decoded to.
    private const string ZoaShapeJson = """
        {
          "id": "ZOA",
          "facility": {
            "id": "ZOA",
            "type": "Artcc",
            "name": "Oakland Center",
            "childFacilities": [
              {
                "id": "NCT",
                "type": "Tracon",
                "name": "NorCal TRACON",
                "starsConfiguration": {
                  "tcps": [ { "subset": 3, "sectorId": "O", "id": "tcp-nct-3o" } ],
                  "starsHandoffIds": [
                    { "id": "h-suu", "facilityId": "SUU", "handoffNumber": 1 },
                    { "id": "h-fat", "facilityId": "FAT", "handoffNumber": 3 },
                    { "id": "h-nlc", "facilityId": "NLC", "handoffNumber": 4 },
                    { "id": "h-nfl", "facilityId": "NFL", "handoffNumber": 5 }
                  ]
                }
              },
              {
                "id": "FAT",
                "type": "AtctTracon",
                "name": "Fresno ATCT",
                "starsConfiguration": {
                  "tcps": [
                    { "subset": 1, "sectorId": "F", "id": "tcp-fat-1f" },
                    { "subset": 1, "sectorId": "H", "id": "tcp-fat-1h" }
                  ],
                  "starsHandoffIds": []
                }
              },
              {
                "id": "SUU",
                "type": "AtctRapcon",
                "name": "Travis AFB RAPCON",
                "starsConfiguration": {
                  "tcps": [
                    { "subset": 1, "sectorId": "A", "id": "tcp-suu-1a" },
                    { "subset": 1, "sectorId": "N", "id": "tcp-suu-1n" },
                    { "subset": 1, "sectorId": "S", "id": "tcp-suu-1s" }
                  ],
                  "starsHandoffIds": []
                }
              },
              {
                "id": "NFL",
                "type": "AtctRapcon",
                "name": "Fallon RAPCON",
                "starsConfiguration": {
                  "tcps": [ { "subset": 1, "sectorId": "F", "id": "tcp-nfl-1f" } ],
                  "starsHandoffIds": []
                }
              },
              {
                "id": "NLC",
                "type": "AtctRapcon",
                "name": "Lemoore RAPCON",
                "starsConfiguration": {
                  "tcps": [ { "subset": 1, "sectorId": "L", "id": "tcp-nlc-1l" } ],
                  "starsHandoffIds": []
                }
              }
            ]
          }
        }
        """;

    private static ArtccConfigRoot Config() => JsonSerializer.Deserialize<ArtccConfigRoot>(ZoaShapeJson, JsonOptions)!;

    [Theory]
    [InlineData("`1", "SUU", 1, "A")] // facility default → SUU primary sector (first TCP)
    [InlineData("`3", "FAT", 1, "F")] // facility default → FAT primary sector (first TCP)
    [InlineData("`4", "NLC", 1, "L")]
    [InlineData("`5", "NFL", 1, "F")]
    [InlineData("`31H", "FAT", 1, "H")] // FAT Chandler
    [InlineData("`31F", "FAT", 1, "F")] // FAT Friant
    [InlineData("`11N", "SUU", 1, "N")] // SUU North
    [InlineData("`11S", "SUU", 1, "S")] // SUU South
    public void ResolveStarsHandoffCode_ValidEntry_ResolvesReceivingFacilityAndSector(
        string code,
        string expectedFacility,
        int expectedSubset,
        string expectedSector
    )
    {
        var owner = Config().ResolveStarsHandoffCode("NCT", code);

        Assert.NotNull(owner);
        Assert.Equal(TrackOwnerType.Stars, owner.OwnerType);
        Assert.Equal(expectedFacility, owner.FacilityId);
        Assert.Equal(expectedSubset, owner.Subset);
        Assert.Equal(expectedSector, owner.SectorId);
    }

    [Theory]
    [InlineData("`99")] // 9 is not a configured handoff number
    [InlineData("`7Z")] // 7 is not configured
    [InlineData("`31Z")] // FAT has no sector Z
    [InlineData("`")] // bare delta, no code
    [InlineData("OAK")] // no delta prefix — a real primary scratchpad value, not a handoff
    [InlineData("3")] // digits without the delta discriminator are not interfacility handoffs
    [InlineData("")]
    public void ResolveStarsHandoffCode_InvalidEntry_ReturnsNull(string code)
    {
        Assert.Null(Config().ResolveStarsHandoffCode("NCT", code));
    }

    [Fact]
    public void ResolveStarsHandoffCode_SenderWithoutHandoffIds_ReturnsNull()
    {
        // FAT in this fixture has an empty starsHandoffIds list, so it cannot originate `3.
        Assert.Null(Config().ResolveStarsHandoffCode("FAT", "`3"));
    }

    [Fact]
    public void ResolveStarsHandoffCode_UnknownSenderFacility_ReturnsNull()
    {
        Assert.Null(Config().ResolveStarsHandoffCode("ZZZ", "`3"));
    }
}
