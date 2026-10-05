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
/// The foot every aircraft menu ends with: a separator, Delete, then the RPO items, with Warp… and the
/// release-to-live-feed item closing All Commands, built through each view's whole-menu builder over the golden fixtures. The aircraft is marked as
/// assumed from the live feed so the release item shows, and the room has one other member so the RPO items show.
/// </summary>
public class MenuFootTests
{
    private const string OtherMemberInitials = "XY";

    private readonly NavigationDatabase _navDb = MenuGoldenFixtures.EnsureNavData();

    [AvaloniaFact]
    public void RadarMenu_EndsWithDeleteThenRpoItems_WarpAndReleaseCloseAllCommands()
    {
        (List<string> items, List<string> commands) = TopLevelAndCommands(MenuView.Radar, "ifr-enroute", assumedFromLiveFeed: true);

        Assert.Equal(["All Commands", "---", "Delete", "---", "Give control", "Unassign"], items[^6..]);
        Assert.Equal(["---", "Warp…", "Release to live feed"], commands[^3..]);
    }

    [AvaloniaFact]
    public void RadarMenu_HasNoSimControlSubmenu()
    {
        (List<string> items, List<string> commands) = TopLevelAndCommands(MenuView.Radar, "taxiing", assumedFromLiveFeed: false);

        Assert.DoesNotContain("Sim Control", items);
        Assert.DoesNotContain("Sim Control", commands);
        Assert.DoesNotContain("Warp…", items);
        Assert.Single(commands, i => i == "Warp…");
    }

    // A surface shadow is never assumable, so the warp, which goes through the command path, stays out of its menu.
    [AvaloniaFact]
    public void RadarSurfaceShadowMenu_OffersNoWarp()
    {
        (List<string> items, List<string> commands) = TopLevelAndCommands(MenuView.Radar, "live-traffic-surface", assumedFromLiveFeed: false);

        Assert.Equal(["Display", "Favorite Commands", "All Commands", "---", "Delete", "---", "Give control", "Unassign"], items[^8..]);
        Assert.DoesNotContain("Warp…", commands);
    }

    [AvaloniaFact]
    public void GroundMenu_EndsWithDeleteThenRpoItems_WarpAndReleaseCloseAllCommands()
    {
        (List<string> items, List<string> commands) = TopLevelAndCommands(MenuView.Ground, "taxiing", assumedFromLiveFeed: true);

        Assert.Equal(["Display", "Favorite Commands", "All Commands", "---", "Delete", "---", "Give control", "Unassign"], items[^8..]);
        Assert.Equal(["---", "Warp…", "Release to live feed"], commands[^3..]);
    }

    // The list's view section is empty, so Favorites follows Squawk; Edit flight plan stays in the command tree, before
    // the sim-control items.
    [AvaloniaFact]
    public void ListMenu_EndsWithDeleteThenRpoItems_EditFlightPlanStaysInCommandBlock()
    {
        (List<string> items, List<string> commands) = TopLevelAndCommands(MenuView.List, "taxiing", assumedFromLiveFeed: true);

        Assert.Equal(["Squawk", "Favorite Commands", "All Commands", "---", "Delete", "---", "Give control", "Unassign"], items[^8..]);
        Assert.Equal(["Coordination", "---", "Edit flight plan", "---", "Warp…", "Release to live feed"], commands[^6..]);
    }

    [AvaloniaFact]
    public void ListDelayedSpawnMenu_KeepsItsOwnFoot()
    {
        List<string> items = TopLevel(MenuView.List, "delayed-spawn", assumedFromLiveFeed: true);

        Assert.Equal(["---", "Spawn now", "Change spawn delay", "Delete"], items[^4..]);
    }

    private static MenuFixture Fixture(MenuView view, string name) => MenuGoldenFixtures.For(view).Single(f => f.Name == name);

    /// <summary>The top-level items of one fixture's menu on <paramref name="view"/>: each item's header, a separator as <c>---</c>.</summary>
    private List<string> TopLevel(MenuView view, string fixtureName, bool assumedFromLiveFeed) =>
        [.. Menu(view, fixtureName, assumedFromLiveFeed).Items.Select(Describe)];

    /// <summary>The top-level items and the All Commands submenu's items of one fixture's menu, described as <see cref="TopLevel"/> does.</summary>
    private (List<string> Items, List<string> Commands) TopLevelAndCommands(MenuView view, string fixtureName, bool assumedFromLiveFeed)
    {
        ContextMenu menu = Menu(view, fixtureName, assumedFromLiveFeed);
        MenuItem allCommands = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        return ([.. menu.Items.Select(Describe)], [.. allCommands.Items.Select(Describe)]);
    }

    private ContextMenu Menu(MenuView view, string fixtureName, bool assumedFromLiveFeed)
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

        return view switch
        {
            MenuView.Radar => MenuHostHarness.BuildRadarMenu(main, ac, null),
            MenuView.Ground => MenuHostHarness.BuildGroundMenu(main, ac, null),
            _ => DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, null, [ac]),
        };
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
