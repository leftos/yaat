using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Sim;
using Yaat.Sim.Commands;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

// Coverage for MenuPopups, the popup service every view's menu host opens its pickers through: the free-text input
// (with an initial text and caret), the list, the type-to-filter list and the warp popup, each opening on its anchor's
// overlay, handing back what the controller picked or typed, and closing without sending on a blank submit, the Clear
// button or Escape.
public class MenuPopupsTests
{
    private const string Placeholder = "CTO arg (e.g. RH 3000, LT 270, DCT BERKS)";
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    // --- The input popup ----------------------------------------------------------------------

    [AvaloniaFact]
    public void Open_ShowsPopupWithPlaceholderAndFocus()
    {
        (Window _, Control anchor) = ShowAnchorWindow();

        MenuPopups.ShowInput(anchor, Placeholder, "", 0, BlankInput.Closes, _ => Task.CompletedTask);
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        Assert.True(popup.IsOpen, "The input popup should open on its anchor.");
        TextBox textBox = FindTextBox(anchor);
        Assert.Equal(Placeholder, textBox.PlaceholderText);
        Assert.True(textBox.IsFocused, "The input popup's TextBox should receive focus when the popup opens.");
    }

    [AvaloniaFact]
    public void Input_InitialTextAndCaret()
    {
        (Window _, Control anchor) = ShowAnchorWindow();

        MenuPopups.ShowInput(anchor, Placeholder, "TAXI A B", 5, BlankInput.Closes, _ => Task.CompletedTask);
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        Assert.Equal("TAXI A B", textBox.Text);
        Assert.True(textBox.IsFocused, "The input popup's TextBox should receive focus when the popup opens.");
        Assert.Equal(5, textBox.CaretIndex);
        Assert.True(string.IsNullOrEmpty(textBox.SelectedText), "An initial caret should leave nothing selected.");
    }

    [AvaloniaFact]
    public async Task Enter_SubmitsTheTypedText()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        var submitted = new TaskCompletionSource<string>();
        MenuPopups.ShowInput(
            anchor,
            Placeholder,
            "",
            0,
            BlankInput.Closes,
            value =>
            {
                submitted.TrySetResult(value);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "  RH 3000 ";
        RaiseKey(textBox, Key.Enter);

        Assert.Equal("RH 3000", await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.False(popup.IsOpen, "Submitting should close the popup.");
    }

    [AvaloniaFact]
    public void Enter_WithBlankText_ClosesWithoutSubmitting()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        bool submitted = false;
        MenuPopups.ShowInput(
            anchor,
            Placeholder,
            "",
            0,
            BlankInput.Closes,
            _ =>
            {
                submitted = true;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "   ";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.False(submitted, "Blank/whitespace input must not invoke the submit callback.");
        Assert.False(popup.IsOpen, "Enter on a blank box should still dismiss the popup.");
    }

    [AvaloniaFact]
    public void ClearButton_ClosesWithoutSubmitting()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        bool submitted = false;
        MenuPopups.ShowInput(
            anchor,
            Placeholder,
            "",
            0,
            BlankInput.Closes,
            _ =>
            {
                submitted = true;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        FindButton(anchor, "Clear").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.False(submitted, "The Clear button must not invoke the submit callback.");
        Assert.False(popup.IsOpen, "The Clear button should dismiss the popup.");
    }

    [AvaloniaFact]
    public void Escape_ClosesWithoutSubmitting()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        bool submitted = false;
        MenuPopups.ShowInput(
            anchor,
            Placeholder,
            "",
            0,
            BlankInput.Closes,
            _ =>
            {
                submitted = true;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "LT 270";
        RaiseKey(textBox, Key.Escape);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.False(submitted, "Escape must not invoke the submit callback.");
        Assert.False(popup.IsOpen, "Escape should dismiss the popup.");
        Assert.DoesNotContain(popup, OverlayLayer.GetOverlayLayer(anchor)!.Children);
    }

    /// <summary>A field opened with <see cref="BlankInput.Submits"/> hands "" to the callback on a blank submit.</summary>
    [AvaloniaFact]
    public async Task Input_BlankSubmit_Submits_HandsEmptyToTheCallback()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        var submitted = new TaskCompletionSource<string>();

        MenuPopups.ShowInput(
            anchor,
            Placeholder,
            "",
            0,
            BlankInput.Submits,
            value =>
            {
                submitted.TrySetResult(value);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "   ";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal("", await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(popup.IsOpen, "A blank submit should still close the popup.");
    }

    /// <summary>A field opened with <see cref="BlankInput.Closes"/> sends nothing on a blank submit.</summary>
    [AvaloniaFact]
    public void Input_BlankSubmit_Closes_SendsNothing()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        bool submitted = false;

        MenuPopups.ShowInput(
            anchor,
            Placeholder,
            "",
            0,
            BlankInput.Closes,
            _ =>
            {
                submitted = true;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "   ";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.False(submitted, "A Closes field must not invoke the submit callback on blank.");
        Assert.False(popup.IsOpen, "A blank submit should still close the popup.");
    }

    /// <summary>The note flyout opens on its anchor, prefilled with the current note, and sends the NOTE command.</summary>
    [AvaloniaFact]
    public async Task ShowNote_OpensTheNoteFlyoutAtTheAnchor()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        var submitted = new TaskCompletionSource<string>();

        MenuPopups.ShowNote(
            anchor,
            Callsign,
            "HOLD SHORT",
            command =>
            {
                submitted.TrySetResult(command);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        Assert.True(popup.IsOpen, "The note flyout should open on its anchor.");
        TextBox textBox = FindTextBox(anchor);
        Assert.Equal("HOLD SHORT", textBox.Text);
        textBox.Text = "MONITOR 121.5";
        RaiseKey(textBox, Key.Enter);

        Assert.Equal("NOTE MONITOR 121.5", await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    // --- The list, filtered-list and warp popups ------------------------------------------------

    /// <summary>The list popup seeds the current value and hands back the item a click selects, then closes.</summary>
    [AvaloniaFact]
    public void List_ClickPicksTheItem()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        object? picked = null;
        MenuPopups.ShowList(
            anchor,
            [3000, 4000, 5000],
            4100,
            value =>
            {
                picked = value;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        Assert.True(popup.IsOpen, "The list popup should open on its anchor.");
        ListBox list = FindListBox(anchor);
        Assert.Equal<object?>(4000, list.SelectedItem);
        Assert.Null(picked);

        list.SelectedIndex = 2;
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal<object?>(5000, picked);
        Assert.False(popup.IsOpen, "A pick should close the list popup.");
    }

    /// <summary>
    /// The filtered list shows the priority items first, narrows to the names starting with the typed prefix (case
    /// folded), and Enter picks the first match.
    /// </summary>
    [AvaloniaFact]
    public void FilteredList_TypeThenEnterPicksTheMatch()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        string? picked = null;
        MenuPopups.ShowFilteredList(
            anchor,
            ["ECA", "OAK", "OAKLE", "SFO"],
            ["SFO"],
            value =>
            {
                picked = value;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        ListBox list = FindListBox(anchor);
        Assert.True(textBox.IsFocused, "The filter box should receive focus when the popup opens.");
        Assert.Equal(["SFO"], list.Items.Cast<object>().Select(i => i.ToString()));

        textBox.Text = "oa";
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.Equal(["OAK", "OAKLE"], list.Items.Cast<object>().Select(i => i.ToString()));
        Assert.Null(picked);

        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal("OAK", picked);
        Assert.False(popup.IsOpen, "Enter should close the filtered-list popup.");
    }

    /// <summary>Up and Down move the filtered list's selection within the matches, stopping at either end.</summary>
    [AvaloniaFact]
    public void FilteredList_UpDownMoveTheSelection()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        MenuPopups.ShowFilteredList(anchor, ["ECA", "OAK", "OAKLE", "SFO"], null, _ => Task.CompletedTask);
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        ListBox list = FindListBox(anchor);
        textBox.Text = "OA";
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.Equal<object?>("OAK", list.SelectedItem);

        RaiseKey(textBox, Key.Down);
        Assert.Equal<object?>("OAKLE", list.SelectedItem);
        RaiseKey(textBox, Key.Down);
        Assert.Equal<object?>("OAKLE", list.SelectedItem);
        RaiseKey(textBox, Key.Up);
        Assert.Equal<object?>("OAK", list.SelectedItem);
        RaiseKey(textBox, Key.Up);
        Assert.Equal<object?>("OAK", list.SelectedItem);
    }

    /// <summary>With no name matching, Enter picks the typed text upper-cased.</summary>
    [AvaloniaFact]
    public void FilteredList_NoMatch_EnterPicksTheTypedTextUpperCased()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        string? picked = null;
        MenuPopups.ShowFilteredList(
            anchor,
            ["ECA", "OAK", "OAKLE", "SFO"],
            null,
            value =>
            {
                picked = value;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        textBox.Text = " zz ";
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.Empty(FindListBox(anchor).Items);

        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal("ZZ", picked);
    }

    /// <summary>The warp popup opens seeded with the aircraft's values and hands back FRD, heading, altitude and speed.</summary>
    [AvaloniaFact]
    public void Warp_SubmitHandsBackFrdHeadingAltitudeSpeed()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        (string Frd, int Heading, int Altitude, int Speed)? submitted = null;
        MenuPopups.ShowWarp(
            anchor,
            new MenuPopups.WarpSeed(Callsign, "SFO", 90, 5000, 250),
            (frd, heading, altitude, speed) =>
            {
                submitted = (frd, heading, altitude, speed);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        Assert.Contains(popup.Child!.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == $"Warp {Callsign}");
        TextBox[] boxes = [.. popup.Child!.GetLogicalDescendants().OfType<TextBox>()];
        Assert.Equal(["SFO", "90", "5000", "250"], boxes.Select(b => b.Text));
        Assert.True(boxes[0].IsFocused, "The FRD box should receive focus when the popup opens.");

        boxes[0].Text = " OAK180010 ";
        boxes[3].Text = "210";
        RaiseKey(boxes[1], Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal(("OAK180010", 90, 5000, 210), submitted);
        Assert.False(popup.IsOpen, "Submitting should close the warp popup.");
    }

    /// <summary>
    /// The warp popup closes without submitting on a blank FRD, a heading that is not a number, Cancel and Escape.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("blank-frd")]
    [InlineData("bad-heading")]
    [InlineData("cancel")]
    [InlineData("escape")]
    public void Warp_ClosesWithoutSubmitting(string how)
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        bool submitted = false;
        MenuPopups.ShowWarp(
            anchor,
            new MenuPopups.WarpSeed(Callsign, "SFO", 90, 5000, 250),
            (_, _, _, _) =>
            {
                submitted = true;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox[] boxes = [.. popup.Child!.GetLogicalDescendants().OfType<TextBox>()];
        switch (how)
        {
            case "blank-frd":
                boxes[0].Text = "   ";
                RaiseKey(boxes[0], Key.Enter);
                break;
            case "bad-heading":
                boxes[1].Text = "abc";
                RaiseKey(boxes[1], Key.Enter);
                break;
            case "cancel":
                FindButton(anchor, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            default:
                RaiseKey(boxes[2], Key.Escape);
                break;
        }

        HeadlessWindowExtensions.PumpDispatcher();

        Assert.False(submitted, $"The warp popup must not submit on {how}.");
        Assert.False(popup.IsOpen, $"The warp popup should close on {how}.");
    }

    // --- The client host's Custom… on the ground --------------------------------------------

    /// <summary>
    /// On the ground canvas, the client host's takeoff Custom… opens the input popup there, and a submit goes to the main
    /// view model's send. That send needs a server a test cannot reach (a real <c>ServerConnection</c> with no seam), so
    /// the failure it shows in the status line proves the hand-off; <c>MenuCatalogCommandTests</c>' recording host pins
    /// the composed command text.
    /// </summary>
    [AvaloniaFact]
    public void ClientHost_CustomTakeoff_OnTheGround_OpensTheInputPopupAndSendsThroughMain()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new GroundView { DataContext = main.Ground };
        var window = new Window { DataContext = main, Content = view };
        window.ShowAndRunLayout();

        AircraftModel ac = TaxiingJet();
        var host = new ClientMenuHost(main, ac, view.Canvas);

        Click(CustomTakeoff(ac, CatalogMenuView.Ground, host));
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(view);
        Assert.True(popup.IsOpen, "Clicking Custom… should open the input popup on the ground canvas.");
        TextBox textBox = FindTextBox(view);
        Assert.Equal(Placeholder, textBox.PlaceholderText);
        textBox.Text = "  LT 270 ";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.StartsWith("Command error:", main.StatusText);
    }

    // --- Custom… on the menu's anchor ----------------------------------------------------------

    /// <summary>
    /// The takeoff Custom… opens the input popup on the control the menu was built with. The composed command text stays
    /// pinned by <c>MenuCatalogCommandTests</c>' recording host; this test pins the popup the menu opens.
    /// </summary>
    [AvaloniaFact]
    public void ClientHost_CustomTakeoff_OpensThePopupOnTheMenuAnchor()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.ShowAndRunLayout();

        AircraftModel ac = TaxiingJet();
        main.Aircraft.Add(ac);
        var host = new ClientMenuHost(main, ac, anchor);

        Click(CustomTakeoff(ac, CatalogMenuView.List, host));
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        Assert.True(popup.IsOpen, "Clicking Custom… should open the list's input popup on its flyout anchor.");
        Assert.Equal(Placeholder, FindTextBox(anchor).PlaceholderText);

        RaiseKey(FindTextBox(anchor), Key.Escape);
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.False(popup.IsOpen, "Escape should close the list's input popup.");
    }

    /// <summary>
    /// The client host's input popup, opened on the control the menu was built with, hands the text the controller typed
    /// to the submit callback.
    /// </summary>
    [AvaloniaFact]
    public void ClientHost_ShowInputPopup_OnTheMenuAnchor_SubmitsTheTypedText()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.ShowAndRunLayout();

        AircraftModel ac = TaxiingJet();
        main.Aircraft.Add(ac);
        var host = new ClientMenuHost(main, ac, anchor);

        string? got = null;
        host.ShowInputPopup(
            Placeholder,
            BlankInput.Closes,
            "",
            0,
            v =>
            {
                got = v;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "LT 270";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal("LT 270", got);
    }

    // --- The list's own menu anchors its popups on the grid -----------------------------------

    /// <summary>
    /// The aircraft list's menu, built through <see cref="DataGridView.BuildAircraftMenu"/>, opens its input popups on
    /// the DataGrid the right-click came from, so the takeoff Custom… places its popup there.
    /// </summary>
    [AvaloniaFact]
    public void DataGridMenu_CustomInputPopup_OpensOnTheGrid()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = TaxiingJet();
        main.Aircraft.Add(ac);

        var grid = new DataGrid();
        new Window { Content = grid }.ShowAndRunLayout();

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, grid, ac, null, [ac]);
        MenuItem tower = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == "Tower");
        MenuItem takeoff = tower
            .Items.OfType<MenuItem>()
            .Single(m => ((m.Header as string) ?? "").StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        Click(Assert.IsType<MenuItem>(takeoff.Items[^1]));
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(grid);
        Assert.True(popup.IsOpen, "Clicking Custom… should open the list's input popup.");
        Assert.Same(grid, popup.PlacementTarget);
    }

    // --- Fixtures -----------------------------------------------------------------------------

    private static AircraftModel TaxiingJet() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "Taxiing",
            AssignedRunway = "30",
        };

    /// <summary>The takeoff submenu's trailing Custom… item over <paramref name="host"/>, asserting it is there.</summary>
    private static MenuItem CustomTakeoff(AircraftModel aircraft, CatalogMenuView view, IMenuHost host)
    {
        MenuContext context = TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None, view);
        MenuItem? cto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(aircraft, context, host);
        Assert.NotNull(cto);
        MenuItem custom = Assert.IsType<MenuItem>(cto.Items[^1]);
        Assert.Equal("Custom...", custom.Header as string);
        return custom;
    }

    private static (Window window, Control anchor) ShowAnchorWindow()
    {
        var anchor = new Border();
        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = anchor,
        };
        window.ShowAndRunLayout();
        return (window, anchor);
    }

    private static Popup FindPopup(Control anchor)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        Assert.NotNull(overlay);
        Popup? popup = overlay!.Children.OfType<Popup>().LastOrDefault();
        Assert.NotNull(popup);
        return popup!;
    }

    private static TextBox FindTextBox(Control anchor)
    {
        TextBox? textBox = FindPopup(anchor).Child?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);
        return textBox!;
    }

    private static ListBox FindListBox(Control anchor)
    {
        ListBox? listBox = FindPopup(anchor).Child?.GetLogicalDescendants().OfType<ListBox>().FirstOrDefault();
        Assert.NotNull(listBox);
        return listBox!;
    }

    private static Button FindButton(Control anchor, string content)
    {
        Button? button = FindPopup(anchor).Child?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string) == content);
        Assert.NotNull(button);
        return button!;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void RaiseKey(Control control, Key key) =>
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
}
