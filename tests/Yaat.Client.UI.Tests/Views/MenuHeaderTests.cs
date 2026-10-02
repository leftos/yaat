using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The header every aircraft menu opens with (<see cref="SharedMenuGroups.AddHeader"/>): the bold title naming the
/// callsign and the filed type, any view-side rows under it, then the free-text Command… and Note…, which ask the host
/// to open its command and note flyouts.
/// </summary>
public class MenuHeaderTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    [AvaloniaFact]
    public void Title_IsCallsignAndFiledType_BoldAndDisabled()
    {
        AircraftModel ac = Jet();
        ac.FiledAircraftType = "B739";

        MenuItem title = Assert.IsType<MenuItem>(Header(ac, new RecordingMenuHost(""), [])[0]);

        Assert.Equal("SWA104 — B739", title.Header as string);
        Assert.False(title.IsEnabled);
        Assert.Equal(FontWeight.Bold, title.FontWeight);
    }

    [AvaloniaFact]
    public void Title_WithNoFiledType_ShowsTheAircraftType()
    {
        MenuItem title = Assert.IsType<MenuItem>(Header(Jet(), new RecordingMenuHost(""), [])[0]);

        Assert.Equal("SWA104 — B738", title.Header as string);
    }

    [AvaloniaFact]
    public void Title_WithNoAircraftModel_IsTheBareCallsign()
    {
        MenuItem title = Assert.IsType<MenuItem>(Header(null, new RecordingMenuHost(""), [])[0]);

        Assert.Equal(Callsign, title.Header as string);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_WithABlankType_IsTheBareCallsign(string type)
    {
        AircraftModel ac = Jet();
        ac.AircraftType = type;
        ac.FiledAircraftType = type;

        MenuItem title = Assert.IsType<MenuItem>(Header(ac, new RecordingMenuHost(""), [])[0]);

        Assert.Equal(Callsign, title.Header as string);
    }

    [AvaloniaFact]
    public void Header_IsTitleRowsSeparatorCommandNoteSeparator()
    {
        var row = new MenuItem { Header = "View row" };

        List<string> items = [.. Header(Jet(), new RecordingMenuHost(""), [row]).Select(Describe)];

        Assert.Equal(["SWA104 — B738", "View row", "---", "Command…", "Note…", "---"], items);
    }

    [AvaloniaFact]
    public void Command_AsksTheHostForItsCommandFlyout()
    {
        var host = new RecordingMenuHost("");

        Click(Item(Header(Jet(), host, []), "Command…"));

        Assert.Equal([(Callsign, Initials)], host.CommandFlyouts);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public async Task Note_AsksTheHostForItsNoteFlyout_PrefilledWithTheCurrentNote_AndSendsWhatItSubmits()
    {
        AircraftModel ac = Jet();
        ac.Note = "OLD NOTE";
        var host = new RecordingMenuHost("");

        Click(Item(Header(ac, host, []), "Note…"));

        (string callsign, string currentNote) = Assert.Single(host.NoteFlyouts);
        Assert.Equal((Callsign, "OLD NOTE"), (callsign, currentNote));
        Assert.NotNull(host.NoteSubmit);
        await host.NoteSubmit("NOTE NEW NOTE");
        Assert.Equal([(Callsign, "NOTE NEW NOTE", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Note_WithNoAircraftModel_OpensAnEmptyNoteFlyout()
    {
        var host = new RecordingMenuHost("");

        Click(Item(Header(null, host, []), "Note…"));

        Assert.Equal([(Callsign, "")], host.NoteFlyouts);
    }

    /// <summary>
    /// Through the ground's real host: Note… opens the note popup on the ground canvas, prefilled with the current note
    /// and showing the 40-character limit, and sends <c>NOTE {text}</c> for what the controller typed.
    /// </summary>
    [AvaloniaFact]
    public void GroundHost_Note_OpensThePrefilledNotePopup_AndSendsNoteText()
    {
        (GroundView view, List<(string Callsign, string Command, string Initials)> sent, AircraftModel ac) = GroundHostFixture();
        ac.Note = "OLD NOTE";
        var host = new GroundMenuHost(view, (GroundViewModel)view.DataContext!, null, ac);

        Click(Item(Header(ac, host, []), "Note…"));
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(view);
        Assert.Equal("OLD NOTE", textBox.Text);
        Assert.Contains("max 40", textBox.PlaceholderText, StringComparison.Ordinal);
        textBox.Text = " NEW NOTE ";
        RaiseKey(textBox, Key.Enter);

        Assert.Equal([(Callsign, "NOTE NEW NOTE", Initials)], sent);
    }

    /// <summary>Through the ground's real host: Command… opens the command popup on the ground canvas and sends what was typed.</summary>
    [AvaloniaFact]
    public void GroundHost_Command_OpensTheCommandPopup_AndSendsTheTypedCommand()
    {
        (GroundView view, List<(string Callsign, string Command, string Initials)> sent, AircraftModel ac) = GroundHostFixture();
        var host = new GroundMenuHost(view, (GroundViewModel)view.DataContext!, null, ac);

        Click(Item(Header(ac, host, []), "Command…"));
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(view);
        textBox.Text = "HOLD";
        RaiseKey(textBox, Key.Enter);

        Assert.Equal([(Callsign, "HOLD", Initials)], sent);
    }

    // --- Each host's two flyout members, over a real main view model -----------------------

    /// <summary>A VFR-only command typed for an IFR aircraft, which the VFR gate refuses under every setting but "All".</summary>
    private const string VfrOnlyCommand = "ELD 28L";

    [AvaloniaFact]
    public void RadarHost_Command_OpensOnTheRadarAndGoesThroughTheVfrGate()
    {
        (MainViewModel main, AircraftModel ac) = GatedMain();
        var view = new RadarView { DataContext = main.Radar };
        new Window { DataContext = main, Content = view }.ShowAndRunLayout();

        AssertCommandRefusedByTheGate(new RadarMenuHost(view, main.Radar, main, ac), view, main);
    }

    [AvaloniaFact]
    public void GroundHost_Command_WithAMainViewModel_GoesThroughTheVfrGate_NotTheRawSend()
    {
        (MainViewModel main, AircraftModel ac) = GatedMain();
        (GroundView view, List<(string Callsign, string Command, string Initials)> sent, AircraftModel _) = GroundHostFixture();

        AssertCommandRefusedByTheGate(new GroundMenuHost(view, (GroundViewModel)view.DataContext!, main, ac), view, main);
        Assert.Empty(sent);
    }

    [AvaloniaFact]
    public void ListHost_Command_OpensOnTheFlyoutAnchorAndGoesThroughTheVfrGate()
    {
        (MainViewModel main, AircraftModel ac) = GatedMain();
        var anchor = new Border();
        new Window { Content = anchor }.ShowAndRunLayout();

        AssertCommandRefusedByTheGate(new ListMenuHost(main, ac, anchor), anchor, main);
    }

    [AvaloniaFact]
    public void RadarHost_Note_OpensThePrefilledPopupOnTheRadar_AndHandsBackTheNoteCommand()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new RadarView { DataContext = main.Radar };
        new Window { DataContext = main, Content = view }.ShowAndRunLayout();

        AssertNotePopup(new RadarMenuHost(view, main.Radar, main, Jet()), view);
    }

    [AvaloniaFact]
    public void ListHost_Note_OpensThePrefilledPopupOnTheFlyoutAnchor_AndHandsBackTheNoteCommand()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var anchor = new Border();
        new Window { Content = anchor }.ShowAndRunLayout();

        AssertNotePopup(new ListMenuHost(main, Jet(), anchor), anchor);
    }

    /// <summary>
    /// A main view model holding the IFR jet the menu is on, on a setting that refuses <see cref="VfrOnlyCommand"/> for it.
    /// The setting is read, never written: it is a saved preference, and changing it would leak into every later test that
    /// builds a main view model (the radar goldens offer Pattern only under the default).
    /// </summary>
    private static (MainViewModel Main, AircraftModel Aircraft) GatedMain()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        Assert.NotEqual(VfrCommandsForIfr.All, main.VfrCommandsForIfr);
        AircraftModel ac = Jet();
        main.Aircraft.Add(ac);
        return (main, ac);
    }

    /// <summary>
    /// Opens the host's command popup, asserts it is on <paramref name="anchor"/>, types a VFR-only command for the IFR
    /// aircraft and asserts the main view model's VFR gate refused it.
    /// </summary>
    private static void AssertCommandRefusedByTheGate(IMenuHost host, Control anchor, MainViewModel main)
    {
        host.ShowCommandFlyout(Callsign, Initials);
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        textBox.Text = VfrOnlyCommand;
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Contains($"requires a VFR aircraft — use CIFR on {Callsign}", main.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opens the host's note popup with a current note, asserts it is on <paramref name="anchor"/> and prefilled, and that
    /// it hands the finished <c>NOTE</c> command to the sink it was given.
    /// </summary>
    private static void AssertNotePopup(IMenuHost host, Control anchor)
    {
        var commands = new List<string>();
        host.ShowNoteFlyout(
            Callsign,
            "OLD NOTE",
            command =>
            {
                commands.Add(command);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        Assert.Equal("OLD NOTE", textBox.Text);
        textBox.Text = "NEW NOTE";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal(["NOTE NEW NOTE"], commands);
    }

    // --- The views ----------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundMenu_StartsWithTheSharedHeaderThenFavorites() => AssertStartsWithHeaderThenFavorites(MenuView.Ground);

    [AvaloniaFact]
    public void ListMenu_StartsWithTheSharedHeaderThenFavorites() => AssertStartsWithHeaderThenFavorites(MenuView.List);

    private void AssertStartsWithHeaderThenFavorites(MenuView view)
    {
        List<string> items = TopLevel(view, "taxiing", out ContextMenu _);

        Assert.Equal(["SWA104 — B738", "---", "Command…", "Note…", "---", "Favorite Commands", "---"], items[..7]);
    }

    [AvaloniaFact]
    public void RadarMenu_StartsWithTheSharedHeader_ItsOwnRowsUnderTheTitle_ThenFavorites()
    {
        List<string> items = TopLevel(MenuView.Radar, "ifr-enroute", out ContextMenu _);

        Assert.Equal("AAL202 — B738", items[0]);
        int command = items.IndexOf("Command…");
        Assert.True(command > 1, "Command… should follow the title, any radar rows and a separator.");
        Assert.Equal("---", items[command - 1]);
        Assert.DoesNotContain("---", items[1..(command - 1)]);
        Assert.Equal(["Command…", "Note…", "---", "Favorite Commands", "---"], items[command..(command + 5)]);
        Assert.Single(items, i => i == "Command…");
        Assert.Single(items, i => i == "Note…");
    }

    [AvaloniaFact]
    public void RadarDataBlockSubmenu_HasNoNote()
    {
        TopLevel(MenuView.Radar, "ifr-enroute", out ContextMenu menu);

        MenuItem dataBlock = menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == "Data Block");
        List<string> children = [.. dataBlock.Items.Select(Describe)];
        Assert.Contains("Scratchpad...", children);
        Assert.DoesNotContain(children, c => c.StartsWith("Note", StringComparison.Ordinal));
    }

    /// <summary>The top-level items of one golden fixture's menu on <paramref name="view"/>: each item's header, a separator as <c>---</c>.</summary>
    private List<string> TopLevel(MenuView view, string fixtureName, out ContextMenu menu)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);

        AircraftModel ac = MenuGoldenFixtures.For(view).Single(f => f.Name == fixtureName).Aircraft;
        main.Aircraft.Add(ac);

        menu = view switch
        {
            MenuView.Radar => MenuHostHarness.BuildRadarMenu(main, ac, null, MenuGoldenFixtures.Initials),
            MenuView.Ground => MenuHostHarness.BuildGroundMenu(main, ac, null, MenuGoldenFixtures.Initials),
            _ => DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, [ac], MenuGoldenFixtures.Initials),
        };
        return [.. menu.Items.Select(Describe)];
    }

    // --- Fixtures -----------------------------------------------------------------------------

    private readonly NavigationDatabase _navDb = MenuGoldenFixtures.EnsureNavData();

    private static AircraftModel Jet() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "Taxiing",
        };

    private static ItemCollection Header(AircraftModel? ac, IMenuHost host, IReadOnlyList<MenuItem> titleRows)
    {
        var context = new MenuContext(Callsign, Initials, null, false, VfrCommandsForIfr.None, CatalogMenuView.Ground);
        var menu = new ContextMenu();
        SharedMenuGroups.AddHeader(menu.Items, ac, context, host, titleRows);
        return menu.Items;
    }

    private static (GroundView View, List<(string Callsign, string Command, string Initials)> Sent, AircraftModel Aircraft) GroundHostFixture()
    {
        var sent = new List<(string Callsign, string Command, string Initials)>();
        var ground = new GroundViewModel(
            new ServerConnection(),
            sendCommand: (callsign, command, initials) =>
            {
                sent.Add((callsign, command, initials));
                return Task.CompletedTask;
            }
        );
        var view = new GroundView { DataContext = ground };
        var window = new Window { Content = view };
        window.ShowAndRunLayout();
        return (view, sent, Jet());
    }

    private static MenuItem Item(ItemCollection items, string header) => items.OfType<MenuItem>().Single(i => (i.Header as string) == header);

    private static string Describe(object? item) =>
        item switch
        {
            Separator => "---",
            MenuItem { Header: string header } => header,
            _ => item?.GetType().Name ?? "",
        };

    private static TextBox FindTextBox(Control anchor)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        Assert.NotNull(overlay);
        Popup? popup = overlay!.Children.OfType<Popup>().LastOrDefault();
        Assert.NotNull(popup);
        TextBox? textBox = popup!.Child?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);
        return textBox!;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void RaiseKey(TextBox textBox, Key key) =>
        textBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
}
