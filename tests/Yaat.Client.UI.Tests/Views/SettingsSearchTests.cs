using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Settings search's matching over the real catalog: aliases, every word required, a repeated label told apart
/// by its heading, a shared setting found at its home and at each view's link, and an empty query that filters nothing.
/// </summary>
public class SettingsSearchTests
{
    [Fact]
    public void AnAliasQuery_FindsTheEntryItNames()
    {
        // "Main Window" under "Always on Top" in General: neither the label, the heading nor the section says "topmost".
        SettingsSearchResult result = Search("topmost");

        Assert.True(result.IsFiltered);
        Assert.Contains(Entry(SettingsSectionId.General, "MainWindowTopmost"), result.Matches);
        Assert.Contains(Entry(SettingsSectionId.Ground, "GroundBackgroundColor"), Search("colour").Matches);
    }

    [Fact]
    public void ATwoWordQuery_NeedsBothWords()
    {
        SettingsSearchEntry radarDatablock = Entry(SettingsSectionId.Appearance, "RadarDatablockFontSize");
        SettingsSearchEntry terminal = Entry(SettingsSectionId.Appearance, "TerminalFontSize");

        Assert.Contains(terminal, Search("font").Matches);

        SettingsSearchResult result = Search("  radar   FONT ");
        Assert.Contains(radarDatablock, result.Matches);
        Assert.DoesNotContain(terminal, result.Matches);
        Assert.Empty(Search("radar xyzzy").Matches);
    }

    [Fact]
    public void ALabelThatRepeatsInASection_IsToldApartByItsHeading()
    {
        SettingsSearchEntry assigned = Entry(SettingsSectionId.Radar, "AssignmentTintColor");
        SettingsSearchEntry unassigned = Entry(SettingsSectionId.Radar, "UnassignedTintColor");
        Assert.Equal(assigned.Label, unassigned.Label);
        Assert.NotEqual(assigned.Within, unassigned.Within);

        SettingsSearchResult result = Search("unassigned color");
        Assert.Contains(unassigned, result.Matches);
        Assert.DoesNotContain(assigned, result.Matches);
    }

    [Fact]
    public void Font_FindsTheAppearanceHome_AndAViewSectionsLink()
    {
        SettingsSearchResult result = Search("font");

        Assert.True(result.CountFor(SettingsSectionId.Appearance) > 0);
        Assert.Contains(result.Matches, m => (m.Section == SettingsSectionId.Appearance) && !m.IsLink);

        SettingsSectionId[] views = [SettingsSectionId.Radar, SettingsSectionId.Ground, SettingsSectionId.AircraftList, SettingsSectionId.Terminal];
        Assert.Contains(result.Matches, m => views.Contains(m.Section) && (m.LinkTarget == SettingsSectionId.Appearance));
        Assert.Contains(views, view => result.CountFor(view) > 0);
        Assert.Equal(result.Matches.Count, result.CountsBySection.Values.Sum());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyQuery_FiltersNothing(string query)
    {
        SettingsSearchResult result = Search(query);

        Assert.False(result.IsFiltered);
        Assert.Empty(result.Matches);
        Assert.Equal(SettingsNavigation.Items, result.NavItems());
    }

    private static SettingsSearchResult Search(string query) => SettingsSearch.Run(query, SettingsSearchCatalog.Entries);

    private static SettingsSearchEntry Entry(SettingsSectionId section, string bindingPath) =>
        SettingsSearchCatalog.Entries.Single(e => (e.Section == section) && (e.Key == bindingPath));
}
