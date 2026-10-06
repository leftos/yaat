using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Guards what the Quick Commands editor offers: the catalog actions a list may name (every default one, never a canvas,
/// spawn, two-aircraft or free-text action) and the name each classified situation shows.
/// </summary>
public class QuickCommandCatalogTests
{
    private static readonly string[] ExcludedPrefixes = ["point.", "spawn.", "relative.", "ground.relative-"];

    private static readonly string[] ExcludedIds =
    [
        MenuIds.AircraftCommand,
        MenuIds.AircraftNote,
        MenuIds.FavoritesMenu,
        MenuIds.LiveTrafficAssumeSelected,
    ];

    [Fact]
    public void EveryDefaultListId_IsEligible()
    {
        List<string> ineligible =
        [
            .. QuickCommandDefaults
                .Lists.Values.SelectMany(list => list)
                .Select(entry => Assert.IsType<CatalogQuickCommandEntry>(entry).CatalogId)
                .Where(id => !QuickCommandCatalog.IsEligible(id))
                .Distinct(),
        ];

        Assert.Empty(ineligible);
    }

    [Fact]
    public void NoExcludedPrefixOrId_IsEligible()
    {
        List<string> leaked =
        [
            .. QuickCommandCatalog
                .Eligible.Select(item => item.Id)
                .Where(id => ExcludedPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal)) || ExcludedIds.Contains(id)),
        ];

        Assert.Empty(leaked);
        Assert.All(ExcludedIds, id => Assert.False(QuickCommandCatalog.IsEligible(id)));
    }

    [Fact]
    public void ExcludedIds_AreCatalogActions() => Assert.All(ExcludedIds, id => Assert.Equal(id, MenuCatalog.Get(id).Id));

    [Fact]
    public void EveryEligibleEntry_HasItsFamilyLabelAndGlyph()
    {
        Assert.NotEmpty(QuickCommandCatalog.Eligible);
        Assert.All(
            QuickCommandCatalog.Eligible,
            item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Family));
                Assert.Equal(item.Id[..item.Id.IndexOf('.', StringComparison.Ordinal)], item.Family);
                Assert.Equal(MenuCatalog.Get(item.Id).Label, item.Label);
                Assert.Equal(MenuCatalog.Get(item.Id).DefaultFlightRules, item.DefaultFlightRules);
                Assert.Equal(QuickCommandGlyphs.For(item.Id), item.Glyph);
            }
        );
    }

    [Fact]
    public void Eligible_KeepsCatalogOrder_AndHasNoDuplicates()
    {
        List<string> ids = [.. QuickCommandCatalog.Eligible.Select(item => item.Id)];
        List<string> catalogOrder = [.. MenuCatalog.All.Select(entry => entry.Id).Where(ids.Contains)];

        Assert.Equal(catalogOrder, ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Find_ReturnsTheEligibleEntry_AndNullForAnExcludedOne()
    {
        Assert.Equal(MenuIds.TowerClearedToLand, QuickCommandCatalog.Find(MenuIds.TowerClearedToLand)?.Id);
        Assert.Null(QuickCommandCatalog.Find(MenuIds.PointDirectTo));
        Assert.Null(QuickCommandCatalog.Find("no.such-id"));
    }

    [Fact]
    public void EveryClassifiedSituation_HasADistinctNonBlankName_InEnumOrder()
    {
        AircraftSituation[] classified = [.. Enum.GetValues<AircraftSituation>().Where(situation => situation != AircraftSituation.Unknown)];

        Assert.Equal(classified, QuickCommandSituationNames.Classified);
        List<string> names = [.. classified.Select(situation => QuickCommandSituationNames.NameOf(situation) ?? "")];
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Unknown_HasNoName() => Assert.Null(QuickCommandSituationNames.NameOf(AircraftSituation.Unknown));

    [Theory]
    [InlineData(AircraftSituation.AtParking, "At parking")]
    [InlineData(AircraftSituation.IfrEnroute, "IFR enroute")]
    [InlineData(AircraftSituation.VfrFlightFollowing, "VFR flight following")]
    public void NameOf_UsesThePlanTablesName(AircraftSituation situation, string name) =>
        Assert.Equal(name, QuickCommandSituationNames.NameOf(situation));
}
