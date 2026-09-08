using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests.Services;

/// <summary>
/// Shaped after ZOA's real vNAS tree: NCT (SFO first among its towers), O90 (a TRACON with STARS airports but no child
/// facilities), MC1 (an AtctTracon whose id is not an airport), and FAT listed before NCT under the ARTCC. Callsigns in
/// <see cref="Tree"/> deliberately do not start with an offered airport, so each case exercises the facility fallbacks.
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
            [Pos("ctr", "ZOA_CTR")],
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
                Node("FSS", "Fss", null, null, [], [Pos("fss", "ZOA_FSS")], []),
            ]
        );

    /// <summary>NCT as the live ZOA config has it: SJC is the STARS primary, while the positions name other airports.</summary>
    private static FacilityTreeDto PrefixTree() =>
        Node(
            "ZOA",
            "Artcc",
            null,
            null,
            [],
            [],
            [
                Node(
                    "NCT",
                    "Tracon",
                    null,
                    "SJC",
                    ["SJC", "SFO", "OAK", "HAF"],
                    [Pos("oak-app", "OAK_APP"), Pos("sfo-app", "SFO_B_APP"), Pos("nct-app", "NCT_APP"), Pos("lax-app", "LAX_APP")],
                    []
                ),
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

    [Fact]
    public void PositionCallsignPrefix_PicksThatAirport_OverTheFacilityPrimary()
    {
        var tree = PrefixTree();
        Assert.Equal("OAK", LiveSessionAirportDefaults.Resolve(tree, "oak-app").Default);
        Assert.Equal("SFO", LiveSessionAirportDefaults.Resolve(tree, "sfo-app").Default);
        Assert.Equal("SJC", LiveSessionAirportDefaults.Resolve(tree, "nct-app").Default);
    }

    [Fact]
    public void PositionCallsignPrefix_NotInTheFacilityAirports_FallsBackToPrimary()
    {
        Assert.Equal("SJC", LiveSessionAirportDefaults.Resolve(PrefixTree(), "lax-app").Default);
    }
}
