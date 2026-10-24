using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.Tests;

/// <summary>
/// Guards the category map: every catalog id is in exactly one category or explicitly in none, each category's label is
/// its group's header, and the categories run in All Commands' order.
/// </summary>
public class MenuCategoriesTests
{
    [Fact]
    public void EveryCatalogId_IsInExactlyOneCategoryOrUncategorized()
    {
        List<string> unplaced = [];
        List<string> twice = [];
        foreach (MenuCatalogEntry entry in MenuCatalog.All)
        {
            bool categorized = MenuCategories.Of(entry.Id) is not null;
            bool uncategorized = MenuCategories.Uncategorized.Contains(entry.Id);
            if (categorized && uncategorized)
            {
                twice.Add(entry.Id);
            }
            else if (!categorized && !uncategorized)
            {
                unplaced.Add(entry.Id);
            }
        }

        Assert.True(unplaced.Count == 0, $"ids in neither the map nor Uncategorized: {string.Join(", ", unplaced)}");
        Assert.True(twice.Count == 0, $"ids in both the map and Uncategorized: {string.Join(", ", twice)}");
    }

    [Fact]
    public void Uncategorized_HoldsOnlyCatalogIds()
    {
        HashSet<string> catalog = [.. MenuCatalog.All.Select(entry => entry.Id)];

        Assert.All(MenuCategories.Uncategorized, id => Assert.Contains(id, catalog));
    }

    [Theory]
    [InlineData(MenuCategory.Runway, "Runway")]
    [InlineData(MenuCategory.Taxi, "Taxi")]
    [InlineData(MenuCategory.Vectors, "Vectors")]
    [InlineData(MenuCategory.Approach, "Approach")]
    [InlineData(MenuCategory.Pattern, "Pattern")]
    [InlineData(MenuCategory.PilotReports, "Pilot reports")]
    [InlineData(MenuCategory.TrackAndCoordination, "Track and coordination")]
    [InlineData(MenuCategory.LiveTraffic, "Live traffic")]
    [InlineData(MenuCategory.Sim, "Sim")]
    public void Label_IsTheGroupHeader(MenuCategory category, string label) => Assert.Equal(label, MenuCategories.Label(category));

    [Fact]
    public void Categories_RunInAllCommandsOrder() =>
        Assert.Equal(
            [
                MenuCategory.LiveTraffic,
                MenuCategory.Runway,
                MenuCategory.Taxi,
                MenuCategory.Vectors,
                MenuCategory.Approach,
                MenuCategory.Pattern,
                MenuCategory.PilotReports,
                MenuCategory.TrackAndCoordination,
                MenuCategory.Sim,
            ],
            Enum.GetValues<MenuCategory>()
        );

    [Theory]
    [InlineData(MenuIds.PatternMakeLeftTraffic, MenuCategory.Pattern)]
    [InlineData(MenuIds.PatternMakeRightTraffic, MenuCategory.Pattern)]
    [InlineData(MenuIds.PatternFollow, MenuCategory.Pattern)]
    [InlineData(MenuIds.NavigationOnCourse, MenuCategory.Vectors)]
    [InlineData(MenuIds.ApproachReportNMileFinal, MenuCategory.PilotReports)]
    [InlineData(MenuIds.ApproachReportAtFix, MenuCategory.PilotReports)]
    [InlineData(MenuIds.TowerClearedForTakeoff, MenuCategory.Runway)]
    [InlineData(MenuIds.GroundDrawTaxiRoute, MenuCategory.Taxi)]
    [InlineData(MenuIds.HoldPattern, MenuCategory.Approach)]
    [InlineData(MenuIds.AircraftEditFlightPlan, MenuCategory.TrackAndCoordination)]
    [InlineData(MenuIds.CoordinationReleaseHeld, MenuCategory.TrackAndCoordination)]
    [InlineData(MenuIds.CoordinationCheckReleaseWindow, MenuCategory.TrackAndCoordination)]
    [InlineData(MenuIds.LiveTrafficAssume, MenuCategory.LiveTraffic)]
    [InlineData(MenuIds.LiveTrafficAssumeAndTrack, MenuCategory.LiveTraffic)]
    [InlineData(MenuIds.LiveTrafficAssumeSelected, MenuCategory.LiveTraffic)]
    [InlineData(MenuIds.LiveTrafficUnassume, MenuCategory.LiveTraffic)]
    [InlineData(MenuIds.SimControlWarp, MenuCategory.Sim)]
    [InlineData(MenuIds.SimControlDelete, MenuCategory.Sim)]
    public void Of_PlacesTheFormerlyUngroupedAndMovedIds(string id, MenuCategory category) => Assert.Equal(category, MenuCategories.Of(id));
}
