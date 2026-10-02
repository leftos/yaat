using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The foot every aircraft menu ends with: a separator, Warp… (radar only), the release-to-live-feed item, Delete, then
/// the RPO items, built through each view's whole-menu builder over the golden fixtures. The aircraft is marked as
/// assumed from the live feed so the release item shows, and the room has one other member so the RPO items show.
/// </summary>
public class MenuFootTests
{
    private const string OtherMemberInitials = "XY";

    private readonly NavigationDatabase _navDb = MenuGoldenFixtures.EnsureNavData();

    [AvaloniaFact]
    public void RadarMenu_EndsWithSeparatorWarpReleaseDeleteThenRpoItems()
    {
        List<string> items = TopLevel(MenuView.Radar, "ifr-enroute", assumedFromLiveFeed: true);

        Assert.Equal(["Display", "---", "Warp...", "Release to live feed", "Delete", "---", "Give control", "Unassign"], items[^8..]);
    }

    [AvaloniaFact]
    public void RadarMenu_HasNoSimControlSubmenu()
    {
        List<string> items = TopLevel(MenuView.Radar, "taxiing", assumedFromLiveFeed: false);

        Assert.DoesNotContain("Sim Control", items);
        Assert.Single(items, i => i == "Warp...");
    }

    // A surface shadow is never assumable, so the warp, which goes through the command path, stays out of its foot.
    [AvaloniaFact]
    public void RadarSurfaceShadowMenu_FootOffersNoWarp()
    {
        List<string> items = TopLevel(MenuView.Radar, "live-traffic-surface", assumedFromLiveFeed: false);

        Assert.Equal(["Display", "---", "Delete", "---", "Give control", "Unassign"], items[^6..]);
    }

    [AvaloniaFact]
    public void GroundMenu_EndsWithSeparatorReleaseDeleteThenRpoItems()
    {
        List<string> items = TopLevel(MenuView.Ground, "taxiing", assumedFromLiveFeed: true);

        Assert.Equal(
            ["Hide datablock", "Measure from SWA104", "---", "Release to live feed", "Delete", "---", "Give control", "Unassign"],
            items[^8..]
        );
    }

    [AvaloniaFact]
    public void GroundMenu_MeasureIsAFlatItemAfterTheDisplayItems_NotInTheHeader()
    {
        List<string> items = TopLevel(MenuView.Ground, "taxiing", assumedFromLiveFeed: false);

        int favorites = items.IndexOf("Favorite Commands");
        Assert.DoesNotContain(items[..favorites], i => i.StartsWith("Measure", StringComparison.Ordinal));
        Assert.Equal(items.IndexOf("Hide datablock") + 1, items.IndexOf("Measure from SWA104"));
    }

    [AvaloniaFact]
    public void ListMenu_EndsWithSeparatorReleaseDeleteThenRpoItems_EditFlightPlanStaysInCommandBlock()
    {
        List<string> items = TopLevel(MenuView.List, "taxiing", assumedFromLiveFeed: true);

        Assert.Equal(
            ["Coordination", "---", "Edit flight plan", "---", "Release to live feed", "Delete", "---", "Give control", "Unassign"],
            items[^9..]
        );
        Assert.DoesNotContain("Warp...", items);
    }

    [AvaloniaFact]
    public void ListDelayedSpawnMenu_KeepsItsOwnFoot()
    {
        List<string> items = TopLevel(MenuView.List, "delayed-spawn", assumedFromLiveFeed: true);

        Assert.Equal(["---", "Spawn now", "Change spawn delay", "Delete"], items[^4..]);
    }

    [AvaloniaFact]
    public void ListMenuHost_GetMeasureState_ReturnsNone()
    {
        MenuFixture fixture = Fixture(MenuView.List, "taxiing");
        var host = new ListMenuHost(new MainViewModel(new FakeFilePickerService()), fixture.Aircraft, new Border());

        Assert.Equal(MenuMeasureState.None, host.GetMeasureState());
    }

    private static MenuFixture Fixture(MenuView view, string name) => MenuGoldenFixtures.For(view).Single(f => f.Name == name);

    /// <summary>The top-level items of one fixture's menu on <paramref name="view"/>: each item's header, a separator as <c>---</c>.</summary>
    private List<string> TopLevel(MenuView view, string fixtureName, bool assumedFromLiveFeed)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        main.AssignableMembers.Add(new AssignableMemberDto("conn-xy", OtherMemberInitials));

        MenuFixture fixture = Fixture(view, fixtureName);
        AircraftModel ac = fixture.Aircraft;
        ac.AssumedFromLiveTraffic = assumedFromLiveFeed;
        main.Aircraft.Add(ac);

        ContextMenu menu = view switch
        {
            MenuView.Radar => MenuHostHarness.BuildRadarMenu(main, ac, null, MenuGoldenFixtures.Initials),
            MenuView.Ground => MenuHostHarness.BuildGroundMenu(main, ac, null, MenuGoldenFixtures.Initials),
            _ => DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, [ac], MenuGoldenFixtures.Initials),
        };
        return [.. menu.Items.Select(Describe)];
    }

    private static string Describe(object? item) =>
        item switch
        {
            Separator => "---",
            MenuItem { Header: string header } => header,
            MenuItem other => other.Header?.ToString() ?? "",
            _ => item?.GetType().Name ?? "",
        };
}
