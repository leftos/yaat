using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The four validated inputs, driven through the real client host: Squawk… through the Squawk flyout's code box row, Temporary
/// altitude… and Cruise… through their one-box flyouts, and Speed… through the speed flyout's box. Text an input cannot read
/// sends nothing, keeps the flyout open and shows its one-line reason (on the line under the Squawk box, in the flyout's
/// command column), and every form it does read sends the command it should.
/// </summary>
public class MenuInputValidationTests
{
    private const string Callsign = "SWA104";

    private const string BadAltitude = "Not a valid altitude";
    private const string BadSquawk = "Not a valid squawk code (0000-7777)";
    private const string BadSpeed = "Not a valid speed";

    // --- Temporary altitude… and Cruise… --------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("050", "TEMPALT 50")]
    [InlineData("230", "TEMPALT 230")]
    [InlineData("FL230", "TEMPALT 230")]
    [InlineData("fl 230", "TEMPALT 230")]
    [InlineData("5,000", "TEMPALT 50")]
    [InlineData("23,000", "TEMPALT 230")]
    [InlineData("FL999", "TEMPALT 999")]
    [InlineData("99,900", "TEMPALT 999")]
    public void TemporaryAltitude_AcceptedForms_SendTheAltitude(string typed, string command) =>
        AssertBoxSends(MenuIds.DataBlockTempAltitude, typed, command);

    [AvaloniaTheory]
    [InlineData("050", "CRUISE 50")]
    [InlineData("230", "CRUISE 230")]
    [InlineData("FL230", "CRUISE 230")]
    [InlineData("5,000", "CRUISE 50")]
    [InlineData("23,000", "CRUISE 230")]
    public void Cruise_AcceptedForms_SendTheAltitude(string typed, string command) => AssertBoxSends(MenuIds.DataBlockCruise, typed, command);

    [AvaloniaTheory]
    [InlineData(MenuIds.DataBlockTempAltitude)]
    [InlineData(MenuIds.DataBlockCruise)]
    public void Altitude_TextItCannotRead_SendsNothingAndKeepsTheFlyoutOpen(string id) => AssertBoxRefuses(id, "FL", BadAltitude);

    /// <summary>
    /// Text the picker reads as an altitude the sim cannot fly is refused too: past 99,900 ft the hundreds argument would
    /// wrap into a reading of feet, a flight level is at most three digits, and a comma must group a whole thousand.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.DataBlockTempAltitude, "100000")]
    [InlineData(MenuIds.DataBlockTempAltitude, "FL1200")]
    [InlineData(MenuIds.DataBlockTempAltitude, "2,30")]
    [InlineData(MenuIds.DataBlockCruise, "100000")]
    [InlineData(MenuIds.DataBlockCruise, "FL1200")]
    public void Altitude_UnflyableText_SendsNothingAndKeepsTheFlyoutOpen(string id, string typed) => AssertBoxRefuses(id, typed, BadAltitude);

    /// <summary>
    /// An AGL altitude keeps an airport code that starts with FL: the prefix comes off only in front of a flight level, so
    /// FLL reaches the resolver as itself rather than as the airport "L".
    /// </summary>
    [AvaloniaFact]
    public void TemporaryAltitude_AglAtAnAirportStartingWithFl_KeepsItsCode()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        double elevation =
            NavigationDatabase.Instance.GetAirportElevation("FLL")
            ?? throw new InvalidOperationException("The test navigation data has no elevation for FLL");
        int hundreds = ((int)Math.Round(elevation) + 1000) / 100;

        AssertBoxSends(MenuIds.DataBlockTempAltitude, "FLL+010", $"TEMPALT {hundreds}");
    }

    /// <summary>A refusal leaves the flyout open and usable: the next, readable submit sends and closes the menu.</summary>
    [AvaloniaFact]
    public void TemporaryAltitude_AfterARefusal_AReadableFormStillSends()
    {
        BoxFlyout open = OpenBox(MenuIds.DataBlockTempAltitude);
        try
        {
            SubmitBox(open.Box, "five thousand");

            Assert.Empty(open.Sent.Sent);
            Assert.Equal(BadAltitude, open.Message);

            SubmitBox(open.Box, "FL230");

            Assert.Equal([(Callsign, "TEMPALT 230", "AB")], open.Sent.Sent);
            Assert.False(open.Menu.IsOpen);
        }
        finally
        {
            open.Close();
        }
    }

    // --- Squawk… --------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Squawk_FourOctalDigits_SendsTheCodeAndClosesTheMenu()
    {
        SquawkFlyout open = OpenSquawk();
        try
        {
            SubmitBox(Assert.IsType<TextBox>(open.Row.Input), "1234");

            Assert.Equal([(Callsign, "SQ 1234", "AB")], open.Sent.Sent);
            Assert.False(open.Menu.IsOpen);
        }
        finally
        {
            open.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("1298")]
    [InlineData("ABCD")]
    public void Squawk_NotFourOctalDigits_SendsNothingAndKeepsTheFlyoutOpen(string typed)
    {
        SquawkFlyout open = OpenSquawk();
        try
        {
            SubmitBox(Assert.IsType<TextBox>(open.Row.Input), typed);

            Assert.Empty(open.Sent.Sent);
            Assert.Equal(BadSquawk, open.Row.Refusal.ReasonLine.Text);
            Assert.True(open.Menu.IsOpen, "A refused code should leave the flyout open.");
        }
        finally
        {
            open.Close();
        }
    }

    // --- Speed… ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Speed_APositiveNumber_SendsIt() => AssertBoxSends(MenuIds.SpeedCustom, "210", "SPD 210");

    [AvaloniaTheory]
    [InlineData("fast")]
    [InlineData("2,10")]
    [InlineData("0")]
    [InlineData("-5")]
    public void Speed_NotAPositiveWholeNumber_SendsNothingAndKeepsTheFlyoutOpen(string typed) =>
        AssertBoxRefuses(MenuIds.SpeedCustom, typed, BadSpeed);

    // --- Fixtures -------------------------------------------------------------------------------

    private static void AssertBoxSends(string id, string typed, string command)
    {
        BoxFlyout open = OpenBox(id);
        try
        {
            SubmitBox(open.Box, typed);

            Assert.Equal([(Callsign, command, "AB")], open.Sent.Sent);
            Assert.False(open.Menu.IsOpen);
        }
        finally
        {
            open.Close();
        }
    }

    private static void AssertBoxRefuses(string id, string typed, string message)
    {
        BoxFlyout open = OpenBox(id);
        try
        {
            SubmitBox(open.Box, typed);

            Assert.Empty(open.Sent.Sent);
            Assert.Equal(message, open.Message);
            Assert.True(open.Menu.IsOpen, "A refused submit should leave the flyout open.");
        }
        finally
        {
            open.Close();
        }
    }

    /// <summary>
    /// A one-box altitude flyout or the speed flyout opened under a context menu on a shown window, through the real client host:
    /// its box, and the message its command column or readout shows.
    /// </summary>
    private sealed record BoxFlyout(Window Window, ContextMenu Menu, Control Panel, SendCapturingHost Sent)
    {
        public TextBox Box =>
            Panel switch
            {
                AltitudeBoxPanel altitude => altitude.Box,
                SpeedFlyoutPanel speed => speed.Box,
                _ => throw new InvalidOperationException($"unexpected panel {Panel.GetType().Name}"),
            };

        public string? Message =>
            Panel switch
            {
                AltitudeBoxPanel altitude => altitude.BoxCommandText.Text,
                SpeedFlyoutPanel speed => speed.Readout.Text,
                _ => throw new InvalidOperationException($"unexpected panel {Panel.GetType().Name}"),
            };

        public void Close()
        {
            Menu.Close();
            Window.Close();
        }
    }

    private static BoxFlyout OpenBox(string id)
    {
        (MainViewModel main, AircraftModel aircraft) = MainWithAircraft();
        var anchor = new Border();
        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = anchor,
        };
        window.ShowAndRunLayout();

        var sent = new SendCapturingHost(new ClientMenuHost(main, aircraft, anchor), "AB");
        MenuContext context = TestMenuContext.Create(Callsign, "AB", null, false, VfrCommandsForIfr.None);
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, context, sent));
        var menu = new ContextMenu { Items = { entry } };
        menu.Open(window);
        HeadlessWindowExtensions.PumpDispatcher();
        entry.IsSubMenuOpen = true;
        HeadlessWindowExtensions.PumpDispatcher();
        FlyoutPanelHost holder = Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);
        return new BoxFlyout(window, menu, Assert.IsAssignableFrom<Control>(holder.Child), sent);
    }

    private static void SubmitBox(TextBox box, string typed)
    {
        box.Text = typed;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        HeadlessWindowExtensions.PumpDispatcher();
    }

    private static (MainViewModel Main, AircraftModel Aircraft) MainWithAircraft()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = false,
        };
        main.Aircraft.Add(aircraft);
        return (main, aircraft);
    }

    /// <summary>The Squawk flyout opened under a context menu on a shown window, through the real client host: its code box row.</summary>
    private sealed record SquawkFlyout(Window Window, ContextMenu Menu, InlineBoxRow Row, SendCapturingHost Sent)
    {
        public void Close()
        {
            Menu.Close();
            Window.Close();
        }
    }

    private static SquawkFlyout OpenSquawk()
    {
        (MainViewModel main, AircraftModel aircraft) = MainWithAircraft();
        var anchor = new Border();
        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = anchor,
        };
        window.ShowAndRunLayout();

        var sent = new SendCapturingHost(new ClientMenuHost(main, aircraft, anchor), "AB");
        MenuContext context = TestMenuContext.Create(Callsign, "AB", null, false, VfrCommandsForIfr.None);
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.SquawkCode).Build(aircraft, context, sent));
        var menu = new ContextMenu { Items = { entry } };
        menu.Open(window);
        HeadlessWindowExtensions.PumpDispatcher();
        entry.IsSubMenuOpen = true;
        HeadlessWindowExtensions.PumpDispatcher();
        MenuItem row = Assert.Single(entry.Items.OfType<MenuItem>(), item => item.Header is InlineBoxRow);
        return new SquawkFlyout(window, menu, (InlineBoxRow)row.Header!, sent);
    }
}
