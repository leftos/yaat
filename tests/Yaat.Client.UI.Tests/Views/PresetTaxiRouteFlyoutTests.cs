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
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Preset taxi route flyout in the quick-command strip and in All Commands: one flat two-line row per preset, nearest
/// first, each led by the preset glyph with its name over its taxiways in the monospace font, then its distance and its
/// <c>TAXI</c> command. The strip's flyout opens on the first row and refills once a second: distances count down in
/// place; with the pointer inside, the order is held and a preset the host stops returning greys out; when the pointer
/// leaves, the list is re-sorted nearest first and the dropped preset goes. All Commands shows the same rows and refills
/// only as it opens. With no preset the entry is hidden.
/// </summary>
public class PresetTaxiRouteFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";

    [AvaloniaFact]
    public void Opens_RowsNearestFirst_FocusesTheFirst()
    {
        RecordingMenuHost host = HostWith([
            Preset("Cargo to spot 5", "via W V", 3150),
            Preset("South ramp to 30", "via W", 2300),
            Preset("North field 28L", "via W U L A B", 6800),
        ]);

        MenuFlyout flyout = OpenStripFlyout(host);

        Assert.Equal(
            [
                "South ramp to 30 · via W · ~2,300 ft — TAXI South ramp to 30",
                "Cargo to spot 5 · via W V · ~3,150 ft — TAXI Cargo to spot 5",
                "North field 28L · via W U L A B · ~6,800 ft — TAXI North field 28L",
            ],
            flyout.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
        );
        Assert.All(
            Rows(flyout),
            row =>
            {
                Assert.Equal(MenuDetailPlacement.Route, row.DetailPlacement);
                Assert.Equal(QuickCommandGlyphs.For(MenuIds.GroundTaxiPreset), row.Glyph);
                Assert.True(row.EmphasizeName);
            }
        );
        Assert.Equal("TAXI South ramp to 30", FocusedCommand(flyout));
    }

    [AvaloniaFact]
    public void LongRow_StaysInsideTheCap_TrimmedWithTheWholeRowInTheTooltip()
    {
        const string Taxiways = "W U L A B C D E F G H J K M N P Q R S T V W1 W2 W3 W4 W5 W6 W7 Y Z A1 A2 B1 B2 C1 C2";
        const string LongVia = $"via {Taxiways}";
        const string LongCommand = $"TAXI {Taxiways} RWY 30";
        TaxiRouteRow longWay = Preset("The long way round from the south ramp to runway 30", LongVia, 9000) with { Command = LongCommand };
        RecordingMenuHost host = HostWith([longWay, Preset("Short", "via W", 1000)]);
        MenuFlyout flyout = OpenStripFlyout(host);

        MenuItem longRow = RowItem(flyout, LongCommand);
        TextBlock via = longRow.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == LongVia);
        TextBlock command = longRow.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == LongCommand);
        Grid grid = via.GetVisualAncestors().OfType<Grid>().First(candidate => candidate.ColumnDefinitions.Count == 4);

        Assert.True(grid.Bounds.Width <= MenuCommandRow.RowMaxWidth + 0.5, $"The row is {grid.Bounds.Width:F1}px wide.");
        foreach (TextBlock block in grid.GetVisualDescendants().OfType<TextBlock>())
        {
            double right = block.TranslatePoint(new Point(block.Bounds.Width, 0), grid)!.Value.X;
            Assert.True(right <= grid.Bounds.Width + 0.5, $"'{block.Text}' ends at x={right:F1}, past the row's {grid.Bounds.Width:F1}px.");
        }

        Assert.True(via.Bounds.Width >= 1, "The taxiway line should keep some room.");
        Assert.Contains(via.TextLayout.TextLines, line => line.HasCollapsed);
        Assert.Contains(command.TextLayout.TextLines, line => line.HasCollapsed);
        TextBlock name = longRow.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == longWay.Name);
        Assert.Contains(name.TextLayout.TextLines, line => line.HasCollapsed);
        Assert.Equal(Assert.IsType<MenuCommandRow>(longRow.Header).ToString(), ToolTip.GetTip(grid));

        MenuItem shortRow = RowItem(flyout, "TAXI Short");
        TextBlock shortVia = shortRow.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == "via W");
        Assert.DoesNotContain(shortVia.TextLayout.TextLines, line => line.HasCollapsed);
        Assert.Null(ToolTip.GetTip(shortVia.GetVisualAncestors().OfType<Grid>().First(candidate => candidate.ColumnDefinitions.Count == 4)));
    }

    [AvaloniaFact]
    public void PointedOrFocusedRow_PreviewsItsRoute()
    {
        TaxiRouteRow near = Preset("Near", "via W", 1000);
        TaxiRouteRow far = Preset("Far", "via W V", 2000);
        RecordingMenuHost host = HostWith([far, near]);
        MenuFlyout flyout = OpenStripFlyout(host);

        RaisePointer(RowItem(flyout, "TAXI Far"), InputElement.PointerEnteredEvent);
        Assert.Same(far.Preview, host.RoutePreviews[^1]);

        RowItem(flyout, "TAXI Near").RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        Assert.Same(near.Preview, host.RoutePreviews[^1]);
    }

    [AvaloniaFact]
    public void Row_SendsItsCommand()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000)]);
        MenuFlyout flyout = OpenStripFlyout(host);

        RowItem(flyout, "TAXI Near").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "TAXI Near", Initials)], host.Sent);
        Assert.False(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void NoPresets_EntryHidden()
    {
        RecordingMenuHost host = HostWith([]);
        ContextMenu menu = BuildMenu(host);

        // With nothing else on the list there is no strip at all; either way no button stands for the presets.
        Assert.DoesNotContain(
            menu.Items.OfType<MenuItem>().Where(QuickCommandStrip.IsStrip).SelectMany(QuickCommandStrip.Buttons),
            b => (string)b.Tag! == MenuIds.GroundTaxiPreset
        );
        MenuItem allCommands = Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader);
        Assert.DoesNotContain(Flatten(allCommands.Items), item => (item.Header as string) == MenuCatalog.Get(MenuIds.GroundTaxiPreset).Label);
    }

    [AvaloniaFact]
    public void AllCommands_SameRows_NoLiveRefill()
    {
        RecordingMenuHost host = HostWith([Preset("Far", "via W V", 2000), Preset("Near", "via W", 1000)]);
        MenuItem presets = AllCommandsPresets(BuildMenu(host));
        List<string?> strip =
        [
            .. OpenStripFlyout(HostWith([Preset("Far", "via W V", 2000), Preset("Near", "via W", 1000)])).Items.OfType<MenuItem>().Select(Text),
        ];
        Assert.Equal(strip, presets.Items.OfType<MenuItem>().Select(Text));
        int timers = host.Clock.LiveTimerCount;

        presets.IsSubMenuOpen = true;

        Assert.Equal(timers, host.Clock.LiveTimerCount);
        SetPresets(host, [Preset("Near", "via W", 500)]);
        presets.IsSubMenuOpen = false;
        presets.IsSubMenuOpen = true;

        Assert.Equal(["TAXI Near"], Commands(presets.Items));
        Assert.Equal("Near · via W · ~500 ft — TAXI Near", Text(Assert.Single(presets.Items.OfType<MenuItem>())));

        SetPresets(host, []);
        presets.IsSubMenuOpen = false;
        presets.IsSubMenuOpen = true;

        MenuItem muted = Assert.Single(presets.Items.OfType<MenuItem>());
        Assert.Equal("No preset taxi routes from here.", muted.Header);
        Assert.False(muted.IsEnabled);
    }

    [AvaloniaFact]
    public void ClosingTheFlyout_ClearsTheRoutePreview()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        RaisePointer(RowItem(flyout, "TAXI Near"), InputElement.PointerEnteredEvent);
        Assert.NotNull(host.RoutePreviews[^1]);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.RoutePreviews[^1]);
    }

    /// <summary>A radar or list menu (no ground view's own clearing) clears a row's route preview as the menu closes.</summary>
    [AvaloniaFact]
    public void ClosingTheMenu_ClearsAnAllCommandsRowsRoutePreview()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000)]);
        ContextMenu menu = BuildMenu(host);
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        RaisePointer(Assert.Single(AllCommandsPresets(menu).Items.OfType<MenuItem>()), InputElement.PointerEnteredEvent);
        Assert.NotNull(host.RoutePreviews[^1]);

        menu.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.RoutePreviews[^1]);
    }

    [AvaloniaFact]
    public void ClosingTheFlyout_StopsTheTick_AndABeatQueuedBeforeDoesNothing()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000)]);
        int timersBeforeOpen = host.Clock.LiveTimerCount;
        MenuFlyout flyout = OpenStripFlyout(host);
        Assert.Equal(timersBeforeOpen + 1, host.Clock.LiveTimerCount);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        flyout.Hide();
        SetPresets(host, [Preset("Near", "via W", 400)]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(timersBeforeOpen, host.Clock.LiveTimerCount);
        Assert.Equal(["Near · via W · ~1,000 ft — TAXI Near"], flyout.Items.OfType<MenuItem>().Select(Text));
        Tick(host);
        Assert.Equal(["Near · via W · ~1,000 ft — TAXI Near"], flyout.Items.OfType<MenuItem>().Select(Text));
    }

    [AvaloniaFact]
    public void HostReturnsNothing_PointerOutside_ShowsTheMutedRow()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000)]);
        MenuFlyout flyout = OpenStripFlyout(host);

        SetPresets(host, []);
        Tick(host);
        Layout(flyout);

        MenuItem muted = Assert.Single(flyout.Items.OfType<MenuItem>());
        Assert.Equal("No preset taxi routes from here.", muted.Header);
        Assert.False(muted.IsEnabled);
    }

    [AvaloniaFact]
    public void GoneRow_PreviewsNothing()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Far", "via W V", 2000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        PointerIn(flyout);
        SetPresets(host, [Preset("Far", "via W V", 1900)]);
        Tick(host);
        Layout(flyout);
        int previews = host.RoutePreviews.Count;

        RaisePointer(RowItem(flyout, "TAXI Near"), InputElement.PointerEnteredEvent);

        Assert.Equal(previews, host.RoutePreviews.Count);
    }

    [AvaloniaFact]
    public void PreviewedPresetGone_OnRefill_ClearsTheRoutePreview()
    {
        // One preset, so no other row holds focus and takes the preview over once it goes.
        RecordingMenuHost host = HostWith([Preset("Far", "via W V", 2000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        PointerIn(flyout);
        RaisePointer(RowItem(flyout, "TAXI Far"), InputElement.PointerEnteredEvent);
        Assert.NotNull(host.RoutePreviews[^1]);

        SetPresets(host, []);
        Tick(host);

        Assert.False(RowItem(flyout, "TAXI Far").IsEnabled);

        Assert.Null(host.RoutePreviews[^1]);
    }

    [AvaloniaFact]
    public void Tick_ResendsThePointedRowsFreshRoute()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Far", "via W V", 2000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        RaisePointer(RowItem(flyout, "TAXI Far"), InputElement.PointerEnteredEvent);
        TaxiRouteRow fresh = Preset("Far", "via W V", 1900);

        SetPresets(host, [Preset("Near", "via W", 900), fresh]);
        Tick(host);

        Assert.Same(fresh.Preview, host.RoutePreviews[^1]);
    }

    [AvaloniaFact]
    public void Tick_DistancesCountDownInPlace()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Far", "via W V", 2000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        MenuItem near = RowItem(flyout, "TAXI Near");

        SetPresets(host, [Preset("Near", "via W", 900), Preset("Far", "via W V", 1900)]);
        Tick(host);
        Layout(flyout);

        Assert.Same(near, RowItem(flyout, "TAXI Near"));
        Assert.Equal(["Near · via W · ~900 ft — TAXI Near", "Far · via W V · ~1,900 ft — TAXI Far"], flyout.Items.OfType<MenuItem>().Select(Text));
    }

    [AvaloniaFact]
    public void PointerOutside_Tick_ResortsNearestFirst()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Far", "via W V", 2000)]);
        MenuFlyout flyout = OpenStripFlyout(host);

        SetPresets(host, [Preset("Near", "via W", 1000), Preset("Far", "via W V", 400)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["TAXI Far", "TAXI Near"], Commands(flyout.Items));
    }

    [AvaloniaFact]
    public void PointerInside_OrderFrozen_DroppedPresetGreys()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Mid", "via W V", 2000), Preset("Far", "via W U", 3000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        PointerIn(flyout);

        SetPresets(host, [Preset("Far", "via W U", 500), Preset("Mid", "via W V", 1900)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["TAXI Near", "TAXI Mid", "TAXI Far"], Commands(flyout.Items));
        MenuItem gone = RowItem(flyout, "TAXI Near");
        Assert.Equal("Near · via W · gone — TAXI Near", Text(gone));
        Assert.False(gone.IsEnabled);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, gone.Opacity);
        Assert.Equal("Far · via W U · ~500 ft — TAXI Far", Text(RowItem(flyout, "TAXI Far")));

        gone.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void PointerInside_NewPresetJoinsTheBottom()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Far", "via W V", 2000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        PointerIn(flyout);

        SetPresets(host, [Preset("New", "via B", 100), Preset("Near", "via W", 1000), Preset("Far", "via W V", 2000)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["TAXI Near", "TAXI Far", "TAXI New"], Commands(flyout.Items));
    }

    [AvaloniaFact]
    public void PointerLeaves_ResortsNearestFirst_AndDropsTheGone()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Mid", "via W V", 2000), Preset("Far", "via W U", 3000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        PointerIn(flyout);
        SetPresets(host, [Preset("Far", "via W U", 500), Preset("Mid", "via W V", 1900)]);
        Tick(host);
        Layout(flyout);
        Assert.Equal(["TAXI Near", "TAXI Mid", "TAXI Far"], Commands(flyout.Items));

        PointerOut(flyout);
        Layout(flyout);

        Assert.Equal(["TAXI Far", "TAXI Mid"], Commands(flyout.Items));
        Assert.All(flyout.Items.OfType<MenuItem>(), row => Assert.True(row.IsEnabled));
    }

    [AvaloniaFact]
    public void Keyboard_SkipsTheGoneRow_AndEnterSends()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Mid", "via W V", 2000), Preset("Far", "via W U", 3000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        Assert.Equal("TAXI Near", FocusedCommand(flyout));
        PointerIn(flyout);
        SetPresets(host, [Preset("Near", "via W", 900), Preset("Far", "via W U", 2900)]);
        Tick(host);
        Layout(flyout);

        Press(flyout, Key.Down);
        Assert.Equal("TAXI Far", FocusedCommand(flyout));
        Press(flyout, Key.Up);
        Assert.Equal("TAXI Near", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Press(flyout, Key.Enter);

        Assert.Equal([(Callsign, "TAXI Far", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void FocusedPresetDropped_HandsFocusToTheNextLiveRow()
    {
        RecordingMenuHost host = HostWith([Preset("Near", "via W", 1000), Preset("Mid", "via W V", 2000)]);
        MenuFlyout flyout = OpenStripFlyout(host);
        Assert.Equal("TAXI Near", FocusedCommand(flyout));
        PointerIn(flyout);

        SetPresets(host, [Preset("Mid", "via W V", 1900)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal("TAXI Mid", FocusedCommand(flyout));
        Assert.False(RowItem(flyout, "TAXI Near").IsEnabled);
    }

    // --- Harness ----------------------------------------------------------------------------

    private static void PointerIn(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerEnteredEvent);

    private static void PointerOut(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerExitedEvent);

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Layout(flyout);
    }

    /// <summary>
    /// A preset named <paramref name="name"/> along <paramref name="via"/>, <paramref name="feet"/> away, sending <c>TAXI name</c>.
    /// </summary>
    private static TaxiRouteRow Preset(string name, string via, double feet) =>
        new(TaxiRouteRow.PresetBadge, name, null, null, false, via, feet, $"TAXI {name}", new TaxiRoute { Segments = [], HoldShortPoints = [] }, []);

    private static RecordingMenuHost HostWith(IEnumerable<TaxiRouteRow> presets)
    {
        var host = new RecordingMenuHost("")
        {
            Session = new(Initials, false, VfrCommandsForIfr.None, _ => [new CatalogQuickCommandEntry(MenuIds.GroundTaxiPreset, null)]),
        };
        host.PresetTaxiChoices.AddRange(presets);
        return host;
    }

    private static void SetPresets(RecordingMenuHost host, IEnumerable<TaxiRouteRow> presets)
    {
        host.PresetTaxiChoices.Clear();
        host.PresetTaxiChoices.AddRange(presets);
    }

    /// <summary>One second passes: the flyout's tick fires and the refill it posts runs.</summary>
    private static void Tick(RecordingMenuHost host)
    {
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
    }

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

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host)
    {
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        ContextMenu menu = BuildMenu(host);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundTaxiPreset);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return flyout;
    }

    private static MenuItem Strip(ContextMenu menu) => Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);

    /// <summary>The All Commands submenu's Preset taxi route entry.</summary>
    private static MenuItem AllCommandsPresets(ContextMenu menu) =>
        Assert.Single(
            Flatten(Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items),
            item => (item.Header as string) == MenuCatalog.Get(MenuIds.GroundTaxiPreset).Label
        );

    /// <summary>Every menu item under <paramref name="items"/>, at any depth.</summary>
    private static IEnumerable<MenuItem> Flatten(ItemCollection items) =>
        items.OfType<MenuItem>().SelectMany(item => (IEnumerable<MenuItem>)[item, .. Flatten(item.Items)]);

    private static MenuItem Item(ItemCollection items, string header) =>
        Assert.Single(items.OfType<MenuItem>(), item => (item.Header as string) == header);

    private static string? Text(MenuItem item) => item.Header?.ToString();

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<MenuCommandRow> Rows(MenuFlyout flyout) =>
        [.. flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<MenuCommandRow>()];

    private static List<string> Commands(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Select(item => item.Header).OfType<MenuCommandRow>().Select(row => row.Command)];

    private static MenuItem RowItem(MenuFlyout flyout, string command) =>
        Assert.Single(flyout.Items.OfType<MenuItem>(), item => (item.Header as MenuCommandRow)?.Command == command);

    private static IInputElement? Focused(MenuFlyout flyout) => TopLevel.GetTopLevel(flyout.Popup.Child)?.FocusManager?.GetFocusedElement();

    private static string? FocusedCommand(MenuFlyout flyout) =>
        ((Focused(flyout) as Visual)?.GetSelfAndVisualAncestors().OfType<MenuItem>().FirstOrDefault()?.Header as MenuCommandRow)?.Command;

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
