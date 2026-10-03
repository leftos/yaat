using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

// The one view-independent aircraft menu builder: its tree from the aircraft, the click and the host, with the view's
// own section placed where the builder puts it.
public class AircraftMenuBuilderTests
{
    private const string ViewItem = "View item";

    [AvaloniaFact]
    public void GroundAircraft_OffersTheGroundBlock()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        host.GroundTraffic.Add("SWA602");
        host.PushbackToChoices.Add(new MenuCommandChoice("Gate 26", "PUSH 26", null));
        host.PresetTaxiChoices.Add(new MenuCommandChoice("Via B", "TAXI B 30", null));

        List<string> atParking = Sequence(Build(Fixture("at-parking"), host, _ => []));
        AssertBeforeTrack(atParking, Label(MenuIds.GroundPushback), Label(MenuIds.GroundPushbackTo), Label(MenuIds.GroundFollow));

        List<string> taxiing = Sequence(Build(Fixture("taxiing"), host, _ => []));
        AssertBeforeTrack(taxiing, Label(MenuIds.GroundHoldPosition), Label(MenuIds.GroundBreakConflict), Label(MenuIds.GroundTaxiPreset));
    }

    [AvaloniaFact]
    public void GroundAircraft_OffersDrawTaxiRouteAndPushRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        host.PushbackToChoices.Add(new MenuCommandChoice("Gate 26", "PUSH 26", null));
        host.PresetTaxiChoices.Add(new MenuCommandChoice("Via B", "TAXI B 30", null));

        List<string> atParking = Sequence(Build(Fixture("at-parking"), host, _ => []));
        Assert.True(
            atParking.IndexOf(Label(MenuIds.GroundPushRoute)) == atParking.IndexOf(Label(MenuIds.GroundPushbackTo)) + 1,
            $"Push route… should follow Push back to… in: {string.Join(" | ", atParking)}"
        );

        List<string> taxiing = Sequence(Build(Fixture("taxiing"), host, _ => []));
        Assert.True(
            taxiing.IndexOf(Label(MenuIds.GroundDrawTaxiRoute)) == taxiing.IndexOf(Label(MenuIds.GroundTaxiPreset)) + 1,
            $"Draw taxi route… should follow Preset taxi route in: {string.Join(" | ", taxiing)}"
        );
        Assert.DoesNotContain(Label(MenuIds.GroundPushRoute), taxiing);
    }

    [AvaloniaFact]
    public void ViewSection_SitsAboveTheFoot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        AircraftModel ac = Fixture("ifr-enroute");

        List<string> items = Sequence(Build(ac, host, _ => [new MenuItem { Header = ViewItem }]));

        int view = items.IndexOf(ViewItem);
        Assert.True(view > items.IndexOf("Coordination"), string.Join(" | ", items));
        Assert.Equal(["---", ViewItem, "---", Label(MenuIds.SimControlWarp)], items[(view - 1)..(view + 3)]);
        Assert.Equal(Label(MenuIds.SimControlDelete), items[^1]);
        Assert.Equal([ac.Callsign], Assert.Single(host.RpoRequests));
    }

    [AvaloniaFact]
    public void DelayedSpawn_SkipsTheViewSection()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        bool sectionBuilt = false;

        List<string> items = Sequence(
            Build(
                MenuGoldenFixtures.For(MenuView.List).Single(f => f.Name == "delayed-spawn").Aircraft,
                host,
                _ =>
                {
                    sectionBuilt = true;
                    return [new MenuItem { Header = ViewItem }];
                }
            )
        );

        Assert.False(sectionBuilt);
        Assert.Empty(host.RpoRequests);
        Assert.DoesNotContain(ViewItem, items);
        Assert.DoesNotContain("Track", items);
        Assert.Equal(Label(MenuIds.SimControlDelete), items[^1]);
        Assert.Contains(Label(MenuIds.SpawnNow), items);
    }

    private static ContextMenu Build(AircraftModel ac, RecordingMenuHost host, Func<MenuContext, IReadOnlyList<Control>> section) =>
        AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, []), host, section);

    /// <summary>The radar golden fixture of that name: its aircraft, the same on every view.</summary>
    private static AircraftModel Fixture(string name) => MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == name).Aircraft;

    private static string Label(string id) => MenuCatalog.Get(id).Label;

    /// <summary>Each top-level item as its header text, with a separator written as <c>---</c>.</summary>
    private static List<string> Sequence(ContextMenu menu) =>
        [.. menu.Items.Select(item => item is Separator ? "---" : (item as MenuItem)?.Header as string ?? "")];

    /// <summary>Every one of <paramref name="labels"/> is a top-level item above the Track submenu.</summary>
    private static void AssertBeforeTrack(List<string> items, params string[] labels)
    {
        int track = items.IndexOf("Track");
        Assert.True(track >= 0, string.Join(" | ", items));
        foreach (string label in labels)
        {
            int at = items.IndexOf(label);
            Assert.True((at >= 0) && (at < track), $"'{label}' is not above Track in: {string.Join(" | ", items)}");
        }
    }
}
