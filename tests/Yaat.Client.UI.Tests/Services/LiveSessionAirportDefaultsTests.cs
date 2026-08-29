using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests.Services;

/// <summary>
/// Shaped after ZOA's real vNAS tree: NCT (SFO first among its towers), O90 (a TRACON with STARS airports but no child
/// facilities), MC1 (an AtctTracon whose id is not an airport), and FAT listed before NCT under the ARTCC.
/// </summary>
public class LiveSessionAirportDefaultsTests
{
    private static PositionSummaryDto Pos(string id, string callsign) => new(id, callsign, callsign, 0, false);

    private static FacilityTreeDto Node(
        string id,
        string type,
        string? airportId,
        string? primary,
        List<string> airports,
        List<PositionSummaryDto> positions,
        List<FacilityTreeDto> children
    ) => new(id, type, id, airportId, primary, airports, positions, children);

    private static FacilityTreeDto Tree() =>
        Node(
            "ZOA",
            "Artcc",
            null,
            null,
            [],
            [Pos("ctr", "OAK_CTR")],
            [
                Node("FAT", "AtctTracon", "FAT", "FAT", ["FAT", "VIS"], [Pos("fat-app", "FAT_APP"), Pos("fat-twr", "FAT_TWR")], []),
                Node(
                    "NCT",
                    "Tracon",
                    null,
                    "SFO",
                    ["SFO", "OAK", "SJC", "SMF"],
                    [Pos("nct-app", "NCT_APP"), Pos("nct-dep", "NCT_DEP"), Pos("nct-fin", "NCT_FIN")],
                    [
                        Node("SFO", "Atct", "SFO", null, [], [Pos("sfo-twr", "SFO_TWR")], []),
                        Node("OAK", "Atct", "OAK", null, [], [Pos("oak-twr", "OAK_TWR")], []),
                        Node("MC1", "AtctTracon", null, "SMF", ["SMF", "MHR", "SAC"], [Pos("mc1-app", "MC1_APP")], []),
                    ]
                ),
                Node("O90", "Tracon", null, "SFO", ["HWD", "NUQ", "OAK", "SFO", "SJC"], [Pos("o90-app", "O90_APP")], []),
                Node("FSS", "Fss", null, null, [], [Pos("fss", "OAK_FSS")], []),
            ]
        );

    [Fact]
    public void Tower_OffersItself_First()
    {
        var choice = LiveSessionAirportDefaults.Resolve(Tree(), "oak-twr");
        Assert.Equal(["OAK"], choice.Airports);
        Assert.Equal("OAK", choice.Default);
    }

    [Fact]
    public void Tracon_DefaultsToItsStarsPrimary_AndOffersItsAirportsPlusTowers()
    {
        var choice = LiveSessionAirportDefaults.Resolve(Tree(), "nct-app");
        Assert.Equal("SFO", choice.Default);
        Assert.Equal(["SFO", "OAK", "SJC", "SMF", "MHR", "SAC"], choice.Airports);
    }

    [Fact]
    public void TraconWithoutChildFacilities_UsesItsStarsAirports_NotTheWholeArtcc()
    {
        var choice = LiveSessionAirportDefaults.Resolve(Tree(), "o90-app");
        Assert.Equal("SFO", choice.Default);
        Assert.Equal(["HWD", "NUQ", "OAK", "SFO", "SJC"], choice.Airports);
    }

    [Fact]
    public void AtctTracon_WhoseIdIsNotAnAirport_NeverOffersItsId()
    {
        var choice = LiveSessionAirportDefaults.Resolve(Tree(), "mc1-app");
        Assert.Equal("SMF", choice.Default);
        Assert.DoesNotContain("MC1", choice.Airports);
        Assert.Equal(["SMF", "MHR", "SAC"], choice.Airports);
    }

    [Fact]
    public void Center_DefaultsToTheBusiestTraconsPrimary_NotTheFirstTowerInTreeOrder()
    {
        var choice = LiveSessionAirportDefaults.Resolve(Tree(), "ctr");
        Assert.Equal("SFO", choice.Default);
        Assert.Equal("FAT", choice.Airports[0]);
        Assert.Contains("SMF", choice.Airports);
        Assert.DoesNotContain("MC1", choice.Airports);
    }

    [Fact]
    public void FacilityWithoutAirports_FallsBackToTheArtcc()
    {
        var choice = LiveSessionAirportDefaults.Resolve(Tree(), "fss");
        Assert.Equal("SFO", choice.Default);
        Assert.Contains("FAT", choice.Airports);
    }

    [Fact]
    public void UnknownPosition_ResolvesAsTheArtcc()
    {
        Assert.Null(LiveSessionAirportDefaults.FindFacilityOfPosition(Tree(), "nope"));
        var choice = LiveSessionAirportDefaults.Resolve(Tree(), "nope");
        Assert.Equal("SFO", choice.Default);
    }
}
