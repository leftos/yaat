using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Guards the default quick-command lists and the glyph table against the catalog: every classified situation has a
/// list, every listed and glyph-bearing identifier is a catalog action, and no list repeats an action or names a display
/// item.
/// </summary>
public class QuickCommandDefaultsTests
{
    public static TheoryData<AircraftSituation> ClassifiedSituations() =>
        [.. Enum.GetValues<AircraftSituation>().Where(situation => situation != AircraftSituation.Unknown)];

    private static readonly HashSet<string> CatalogIds = [.. MenuCatalog.All.Select(entry => entry.Id)];

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void EveryClassifiedSituation_HasANonEmptyList(AircraftSituation situation) => Assert.NotEmpty(QuickCommandDefaults.For(situation));

    [Fact]
    public void Unknown_HasNoList()
    {
        Assert.Empty(QuickCommandDefaults.For(AircraftSituation.Unknown));
        Assert.False(QuickCommandDefaults.Lists.ContainsKey(AircraftSituation.Unknown));
    }

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void EveryListedId_IsACatalogAction(AircraftSituation situation)
    {
        List<string> missing = [.. QuickCommandDefaults.For(situation).Select(entry => entry.CatalogId).Where(id => !CatalogIds.Contains(id))];
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void NoList_NamesADisplayItem(AircraftSituation situation)
    {
        List<string> display =
        [
            .. QuickCommandDefaults.For(situation).Select(entry => entry.CatalogId).Where(id => id.StartsWith("display.", StringComparison.Ordinal)),
        ];
        Assert.Empty(display);
    }

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void NoList_RepeatsAnId(AircraftSituation situation)
    {
        List<string> repeated =
        [
            .. QuickCommandDefaults.For(situation).GroupBy(entry => entry.CatalogId).Where(group => group.Count() > 1).Select(group => group.Key),
        ];
        Assert.Empty(repeated);
    }

    [Fact]
    public void EveryGlyphKey_IsACatalogAction()
    {
        List<string> missing = [.. QuickCommandGlyphs.ById.Keys.Where(id => !CatalogIds.Contains(id))];
        Assert.Empty(missing);
    }
}
