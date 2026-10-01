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

// Coverage for InputFlyout, the shared free-text popup behind a catalog "Custom..." item on the ground view and the
// aircraft list: it opens on its anchor with the item's placeholder, submits what the controller typed, and closes
// without sending on a blank submit, the Clear button or Escape.
public class InputFlyoutTests
{
    private const string Placeholder = "CTO arg (e.g. RH 3000, LT 270, DCT BERKS)";
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    // --- The flyout on its own --------------------------------------------------------------

    [AvaloniaFact]
    public void Open_ShowsPopupWithPlaceholderAndFocus()
    {
        (Window _, Control anchor) = ShowAnchorWindow();

        InputFlyout.Open(anchor, Placeholder, _ => Task.CompletedTask);
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        Assert.True(popup.IsOpen, "The input popup should open on its anchor.");
        TextBox textBox = FindTextBox(anchor);
        Assert.Equal(Placeholder, textBox.PlaceholderText);
        Assert.True(textBox.IsFocused, "The input popup's TextBox should receive focus when the popup opens.");
    }

    [AvaloniaFact]
    public void Open_HasNoEmptyTitleRow()
    {
        (Window _, Control anchor) = ShowAnchorWindow();

        InputFlyout.Open(anchor, Placeholder, _ => Task.CompletedTask);
        HeadlessWindowExtensions.PumpDispatcher();

        IEnumerable<TextBlock> textBlocks = FindPopup(anchor).Child?.GetLogicalDescendants().OfType<TextBlock>() ?? [];
        Assert.DoesNotContain(textBlocks, block => string.IsNullOrEmpty(block.Text));
    }

    [AvaloniaFact]
    public async Task Enter_SubmitsTheTypedText()
    {
        (Window _, Control anchor) = ShowAnchorWindow();
        var submitted = new TaskCompletionSource<string>();
        InputFlyout.Open(
            anchor,
            Placeholder,
            value =>
            {
                submitted.TrySetResult(value);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(anchor);
        TextBox textBox = FindTextBox(anchor);
        textBox.Text = "RH 3000";
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
        InputFlyout.Open(
            anchor,
            Placeholder,
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
        InputFlyout.Open(
            anchor,
            Placeholder,
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
        InputFlyout.Open(
            anchor,
            Placeholder,
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
    }

    // --- The ground host's Custom… ------------------------------------------------------------

    /// <summary>
    /// The ground host's takeoff Custom… opens the input flyout on the ground canvas and sends the clearance the
    /// controller typed, trimmed by the catalog's own compose step.
    /// </summary>
    [AvaloniaFact]
    public void GroundMenuHost_CustomTakeoff_SendsCtoWithTheTypedArgument()
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

        AircraftModel ac = TaxiingJet();
        var host = new GroundMenuHost(view, ground, null, ac);

        Click(CustomTakeoff(ac, CatalogMenuView.Ground, host));
        HeadlessWindowExtensions.PumpDispatcher();

        Popup popup = FindPopup(view);
        Assert.True(popup.IsOpen, "Clicking Custom… should open the ground's input popup.");
        TextBox textBox = FindTextBox(view);
        Assert.Equal(Placeholder, textBox.PlaceholderText);
        textBox.Text = "  LT 270 ";
        RaiseKey(textBox, Key.Enter);

        Assert.Equal([(Callsign, "CTO LT 270", Initials)], sent);
    }

    // --- The list host's Custom… --------------------------------------------------------------

    /// <summary>
    /// The aircraft list's takeoff Custom… opens the input flyout on the anchor the list menu already passes its
    /// Command… and Note… flyouts. The list sends through the main view model's own connection, which a test cannot
    /// reach (a real <c>ServerConnection</c> with no seam), so the composed command text stays pinned by
    /// <c>MenuCatalogCommandTests</c>' recording host and this test pins the popup the list opens.
    /// </summary>
    [AvaloniaFact]
    public void ListMenuHost_CustomTakeoff_OpensThePopup()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.ShowAndRunLayout();

        AircraftModel ac = TaxiingJet();
        main.Aircraft.Add(ac);
        var host = new ListMenuHost(main, ac, anchor);

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
    /// The list host's input popup, opened on its flyout anchor, hands the text the controller typed to the submit
    /// callback.
    /// </summary>
    [AvaloniaFact]
    public void ListMenuHost_ShowInputPopup_SubmitsTheTypedText()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.ShowAndRunLayout();

        AircraftModel ac = TaxiingJet();
        main.Aircraft.Add(ac);
        var host = new ListMenuHost(main, ac, anchor);

        string? got = null;
        host.ShowInputPopup(
            Placeholder,
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
        var context = new MenuContext(Callsign, Initials, null, false, VfrCommandsForIfr.None, view);
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

    private static Button FindButton(Control anchor, string content)
    {
        Button? button = FindPopup(anchor).Child?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string) == content);
        Assert.NotNull(button);
        return button!;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void RaiseKey(TextBox textBox, Key key) =>
        textBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
}
