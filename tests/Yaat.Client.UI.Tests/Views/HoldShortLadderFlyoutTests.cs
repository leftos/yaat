using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Hold short of… route ladder in the quick-command strip's flyout and in All Commands: the title over the route
/// line, the line for the aircraft itself, one stop per bar, nearest first, and the dimmed destination row. The strip's
/// flyout opens on the first stop and refills once a second: with the pointer inside, the order freezes, a bar the route
/// has left greys out as gone and a new one joins the bottom; the keyboard passes gone stops and never reaches the
/// destination row; the ladder catches up as the pointer leaves. All Commands shows the same rows and refills only as it opens.
/// </summary>
public class HoldShortLadderFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";
    private const string MeLine = "now, taxiing on D";
    private const string RouteLine = "route B W · spot 5";

    private static readonly HoldShortDestination Spot5 = new("Spot 5", false, 1650);

    [AvaloniaFact]
    public void Opens_FocusesFirstStop()
    {
        RecordingMenuHost host = HostWith([Stop("A", 300), Stop("W", 600), Stop("V", 900)]);

        MenuFlyout flyout = OpenStripFlyout(host).Flyout;

        Assert.Equal(
            [
                $"{HoldShortLadder.Title} · {RouteLine}",
                $"{Callsign} · {MeLine}",
                "TW A · crossing on B · ~300 ft — HS A",
                "TW W · crossing on B · ~600 ft — HS W",
                "TW V · crossing on B · ~900 ft — HS V",
                "Spot 5 · route ends here",
            ],
            flyout.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
        );
        Assert.Equal("HS A", FocusedCommand(flyout));
        MenuItem end = EndRow(flyout);
        Assert.False(end.IsEnabled);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, end.Opacity);
    }

    [AvaloniaFact]
    public void PointerInside_OrderFrozen_PassedBarGreysOut()
    {
        RecordingMenuHost host = HostWith([Stop("A", 300), Stop("W", 600), Stop("V", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host).Flyout;
        PointerIn(flyout);

        SetStops(host, [Stop("W", 250), Stop("V", 550)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["HS A", "HS W", "HS V"], Commands(flyout));
        MenuItem gone = StopRow(flyout, "HS A");
        HoldShortLadderStop goneStop = Assert.IsType<HoldShortLadderStop>(gone.Header);
        Assert.True(goneStop.IsGone);
        Assert.Equal("TW A · gone — HS A", goneStop.ToString());
        Assert.False(gone.IsEnabled);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, gone.Opacity);
        Assert.Equal(250, Assert.IsType<HoldShortLadderStop>(StopRow(flyout, "HS W").Header).DistanceFt);

        gone.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void PointerInside_NewStopAppended()
    {
        RecordingMenuHost host = HostWith([Stop("A", 300), Stop("W", 600)]);
        MenuFlyout flyout = OpenStripFlyout(host).Flyout;
        PointerIn(flyout);

        SetStops(host, [Stop("Z", 100), Stop("A", 300), Stop("W", 600)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["HS A", "HS W", "HS Z"], Commands(flyout));
        Assert.IsType<HoldShortDestination>(flyout.Items.OfType<MenuItem>().Last().Header);
    }

    [AvaloniaFact]
    public void PointerLeaves_CatchesUp()
    {
        RecordingMenuHost host = HostWith([Stop("A", 300), Stop("W", 600), Stop("V", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host).Flyout;
        PointerIn(flyout);
        SetStops(host, [Stop("Z", 100), Stop("W", 250), Stop("V", 550)]);
        Tick(host);
        Layout(flyout);
        Assert.Equal(["HS A", "HS W", "HS V", "HS Z"], Commands(flyout));

        PointerOut(flyout);
        Layout(flyout);

        Assert.Equal(["HS Z", "HS W", "HS V"], Commands(flyout));
        Assert.All(StopRows(flyout), row => Assert.True(row.IsEnabled));
    }

    [AvaloniaFact]
    public void Keyboard_SkipsGoneAndDestinationRows()
    {
        RecordingMenuHost host = HostWith([Stop("A", 300), Stop("W", 600), Stop("V", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host).Flyout;
        Assert.Equal("HS A", FocusedCommand(flyout));
        PointerIn(flyout);
        SetStops(host, [Stop("A", 250), Stop("V", 850)]);
        Tick(host);
        Layout(flyout);
        Assert.Equal(["HS A", "HS W", "HS V"], Commands(flyout));

        Press(flyout, Key.Down);
        Assert.Equal("HS V", FocusedCommand(flyout));

        Press(flyout, Key.Up);
        Assert.Equal("HS A", FocusedCommand(flyout));

        Press(flyout, Key.Down);
        Assert.Equal("HS V", FocusedCommand(flyout));
    }

    [AvaloniaFact]
    public void AllCommands_SameRows_NoLiveRefill()
    {
        RecordingMenuHost host = HostWith([Stop("A", 300), Stop("W", 600)]);
        MenuItem holdShort = AllCommandsHoldShort(BuildMenu(host));
        List<string?> strip = [.. OpenStripFlyout(HostWith([Stop("A", 300), Stop("W", 600)])).Flyout.Items.OfType<MenuItem>().Select(Text)];
        Assert.Equal(strip, holdShort.Items.OfType<MenuItem>().Select(Text));
        holdShort.IsSubMenuOpen = true;

        SetStops(host, [Stop("W", 500)]);
        Tick(host);
        Tick(host);

        Assert.Equal(["HS A", "HS W"], Commands(holdShort.Items));

        holdShort.IsSubMenuOpen = false;
        holdShort.IsSubMenuOpen = true;

        Assert.Equal(["HS W"], Commands(holdShort.Items));
    }

    [AvaloniaFact]
    public void GoneStop_PreviewsNothing()
    {
        var aBar = new LatLon(37.7101, -122.2101);
        RecordingMenuHost host = HostWith([Stop("A", 300) with { BarPoint = aBar }, Stop("W", 600)]);
        MenuFlyout flyout = OpenStripFlyout(host).Flyout;
        PointerIn(flyout);
        MenuItem a = StopRow(flyout, "HS A");
        RaisePointer(a, InputElement.PointerEnteredEvent);
        Assert.Equal(aBar, host.ScopePreviews[^1]?.BarPoint);

        SetStops(host, [Stop("W", 550)]);
        Tick(host);
        Layout(flyout);

        // The focus A held on opening has gone on to W, whose bar the preview now shows; A's is gone with it.
        Assert.NotEqual(aBar, host.ScopePreviews[^1]?.BarPoint);
        RaisePointer(a, InputElement.PointerEnteredEvent);
        Assert.Null(host.ScopePreviews[^1]);
    }

    [AvaloniaFact]
    public void FocusedStop_SendsTheBarPreview()
    {
        var bar = new LatLon(37.7231, -122.2189);
        RecordingMenuHost host = HostWith([Stop("A", 300)]);
        host.HoldShortChoices.Add(Stop("W", 600) with { BarPoint = bar });
        MenuFlyout flyout = OpenStripFlyout(host).Flyout;

        StopRow(flyout, "HS W").RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));

        Assert.Equal(new ScopePreview(null, null, null, bar, null), host.ScopePreviews[^1]);
    }

    // --- Harness ----------------------------------------------------------------------------

    /// <summary>A taxiway stop named <paramref name="name"/> <paramref name="feet"/> along the route, sending <c>HS name</c>.</summary>
    private static HoldShortChoice Stop(string name, double feet) =>
        new(
            new HoldShortRowLabel(HoldShortChoice.TaxiwayBadge, name, "crossing on B", feet),
            $"HS {name}",
            new TaxiRoute { Segments = [], HoldShortPoints = [] },
            new LatLon(37.72, -122.22)
        );

    private static RecordingMenuHost HostWith(IEnumerable<HoldShortChoice> stops)
    {
        var host = new RecordingMenuHost("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundHoldShort, null)],
                SoloAdvisoryPrefill.TrueValues,
                false
            ),
            HoldShortRouteLine = RouteLine,
            HoldShortMeLine = MeLine,
            HoldShortDestination = Spot5,
        };
        host.HoldShortChoices.AddRange(stops);
        return host;
    }

    private static void SetStops(RecordingMenuHost host, IEnumerable<HoldShortChoice> stops)
    {
        host.HoldShortChoices.Clear();
        host.HoldShortChoices.AddRange(stops);
    }

    /// <summary>One second passes: the flyout's tick fires and the refill it posts runs.</summary>
    private static void Tick(RecordingMenuHost host)
    {
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
    }

    private static void PointerIn(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerEnteredEvent);

    private static void PointerOut(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerExitedEvent);

    private static ContextMenu BuildMenu(RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(
            new AircraftModel
            {
                Callsign = Callsign,
                AircraftType = "A320",
                FlightRules = "IFR",
                IsOnGround = true,
                CurrentPhase = "Taxiing",
                Situation = AircraftSituation.Taxiing,
                HasActiveTaxiRoute = true,
            },
            new MenuClick(Callsign, null, null, []),
            host,
            _ => []
        );

    private static (ContextMenu Menu, MenuFlyout Flyout) OpenStripFlyout(RecordingMenuHost host)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        ContextMenu menu = BuildMenu(host);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(b => (string)b.Tag! == MenuIds.GroundHoldShort);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return (menu, flyout);
    }

    /// <summary>The All Commands submenu's Hold short of… entry.</summary>
    private static MenuItem AllCommandsHoldShort(ContextMenu menu) =>
        Item(Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items, MenuCatalog.Get(MenuIds.GroundHoldShort).Label);

    private static MenuItem Item(ItemCollection items, string header) =>
        Assert.Single(items.OfType<MenuItem>(), item => (item.Header as string) == header);

    private static string? Text(MenuItem item) => item.Header?.ToString();

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> Commands(MenuFlyout flyout) => Commands(flyout.Items);

    private static List<string> Commands(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Select(item => item.Header).OfType<HoldShortLadderStop>().Select(stop => stop.Command)];

    private static List<MenuItem> StopRows(MenuFlyout flyout) =>
        [.. flyout.Items.OfType<MenuItem>().Where(item => item.Header is HoldShortLadderStop)];

    private static MenuItem StopRow(MenuFlyout flyout, string command) =>
        Assert.Single(StopRows(flyout), item => ((HoldShortLadderStop)item.Header!).Command == command);

    private static MenuItem EndRow(MenuFlyout flyout) => Assert.Single(flyout.Items.OfType<MenuItem>(), item => item.Header is HoldShortDestination);

    private static IInputElement? Focused(MenuFlyout flyout) => TopLevel.GetTopLevel(flyout.Popup.Child)?.FocusManager?.GetFocusedElement();

    /// <summary>The menu row holding keyboard focus, itself or the row a control inside it belongs to.</summary>
    private static MenuItem? FocusedItem(MenuFlyout flyout) =>
        (Focused(flyout) as Visual)?.GetSelfAndVisualAncestors().OfType<MenuItem>().FirstOrDefault();

    private static string? FocusedCommand(MenuFlyout flyout) => (FocusedItem(flyout)?.Header as HoldShortLadderStop)?.Command;

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Layout(flyout);
    }

    private static void RaisePointer(Control target, RoutedEvent routedEvent) =>
        target.RaiseEvent(
            new PointerEventArgs(
                routedEvent,
                target,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );
}
