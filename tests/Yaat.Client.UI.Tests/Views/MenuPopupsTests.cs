using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
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
// Avalonia.Headless has a HeadlessWindowExtensions of its own, so the headless input helpers are named through an alias.
using Headless = Avalonia.Headless.HeadlessWindowExtensions;

namespace Yaat.Client.UI.Tests.Views;

// Coverage for MenuPopups, the popup service every view's menu host opens its pickers through: the free-text input
// (with an initial text and caret), the list and the type-to-filter list, each opening on its anchor's
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

        MenuPopups.ShowInput(anchor, Placeholder, "", 0, _ => Task.CompletedTask);
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

        MenuPopups.ShowInput(anchor, Placeholder, "TAXI A B", 5, _ => Task.CompletedTask);
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

    /// <summary>While a submit is still in flight, a second Enter does nothing: the send fires once.</summary>
    [AvaloniaFact]
    public void Enter_TwiceWhileTheSendIsInFlight_SendsOnce()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        var inFlight = new TaskCompletionSource();
        int sent = 0;
        MenuPopups.ShowInput(
            anchor,
            Placeholder,
            "",
            0,
            _ =>
            {
                sent++;
                return inFlight.Task;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "RH 3000";
        RaiseKey(textBox, Key.Enter);
        RaiseKey(textBox, Key.Enter);

        Assert.Equal(1, sent);

        inFlight.SetResult();
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.False(popup.IsOpen, "A completed submit should close the popup.");
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
    /// the DataGrid the right-click came from, so Data Block › Scratchpad… places its popup there.
    /// </summary>
    [AvaloniaFact]
    public void DataGridMenu_InputPopup_OpensOnTheGrid()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = AirborneJet();
        main.Aircraft.Add(ac);

        var grid = new DataGrid();
        new Window { Content = grid }.ShowAndRunLayout();

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, grid, ac, null, [ac]);
        MenuItem reportWhen = Submenu(Submenu(Submenu(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items, "Approach").Items, "Report when…");
        Click(Submenu(reportWhen.Items, "At fix…"));
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(grid);
        Assert.True(popup.IsOpen, "Clicking Report when… › At fix… should open the list's input popup.");
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

    private static AircraftModel AirborneJet() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = false,
            Altitude = 5000,
        };

    /// <summary>The menu item headed <paramref name="header"/> among <paramref name="items"/>.</summary>
    private static MenuItem Submenu(ItemCollection items, string header) => items.OfType<MenuItem>().Single(m => (m.Header as string) == header);

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
