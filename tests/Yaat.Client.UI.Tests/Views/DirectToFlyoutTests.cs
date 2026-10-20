using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Direct to flyout (<see cref="DirectToFlyout"/>), one builder for Direct to… and Append direct to…: the fix box on top,
/// the remaining route as rows (distance, magnetic bearing, command, ● on the next fix; a passed fix greyed in place and still
/// sending), then as you type every other matching fix nearest first; the Direct to / Append to route toggle while the
/// aircraft navigates to a fix.
/// </summary>
public partial class DirectToFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";

    public DirectToFlyoutTests() => TestVnasData.EnsureInitialized();

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    private static LatLon OakVor()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("OAK")!.Value;
        return new LatLon(lat, lon);
    }

    private static FakeMenuAircraft Navigating() =>
        new()
        {
            Callsign = Callsign,
            Position = OakVor(),
            NavigatingTo = "SUNOL",
            NavigationRoute = ["SUNOL", "ECA"],
        };

    private static DirectToFlyoutPanel Open(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        entry.IsSubMenuOpen = true;
        return PanelOf(entry);
    }

    /// <summary>The panel an opened Direct to entry's one menu row holds.</summary>
    internal static DirectToFlyoutPanel PanelOf(MenuItem entry) =>
        Assert.IsType<DirectToFlyoutPanel>(Assert.IsType<DirectToFlyoutHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void RouteRows_ListTheRemainingRoute_WithDistanceMagneticBearingAndCommand_TheNextFixMarked()
    {
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), new RecordingMenuHost(""));

        Assert.Equal(["SUNOL", "ECA"], panel.RouteRows.Select(row => row.Fix));
        Assert.True(panel.RouteRows[0].IsNext);
        Assert.False(panel.RouteRows[1].IsNext);
        Assert.Equal("DCT SUNOL", panel.RouteRows[0].SentCommand);
        Assert.Matches(DistanceAndBearing(), panel.RouteRows[0].Note);
        Assert.Equal(FixList.DistanceAndBearing(OakVor(), "SUNOL"), panel.RouteRows[0].Note);
        Assert.Contains(panel.Lines, line => line.StartsWith("● SUNOL · ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bearing is magnetic by the declination at the aircraft, so it reads as the heading the aircraft would fly: from OAK
    /// (about 13° east) to KJFK (about 13° west) the declination at the fix would read some 26° off.
    /// </summary>
    [AvaloniaFact]
    public void Bearing_IsMagnetic_NotTrue()
    {
        LatLon oak = OakVor();
        LatLon kjfk = Position("KJFK");
        double trueBearing = GeoMath.BearingTo(oak, kjfk);
        int magnetic = HeadingFlyout.WholeHeading(MagneticDeclination.TrueToMagnetic(trueBearing, oak));

        string note = FixList.DistanceAndBearing(oak, "KJFK")!;

        Assert.EndsWith($"{magnetic:D3}°", note);
        Assert.NotEqual(magnetic, HeadingFlyout.WholeHeading(trueBearing));
        Assert.NotEqual(magnetic, HeadingFlyout.WholeHeading(MagneticDeclination.TrueToMagnetic(trueBearing, kjfk)));
    }

    [AvaloniaFact]
    public void ClickingARouteRow_SendsDirectToIt()
    {
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), host);

        Click(panel.RouteRows[1]);

        Assert.Equal([(Callsign, "DCT ECA", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void PassedFix_GreysInPlace_AndStillSends()
    {
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            NavigatingTo = "SUNOL",
            NavigationRoute = ["SUNOL", "ECA"],
        };
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, aircraft, host);

        aircraft.NavigatingTo = "ECA";
        aircraft.NavigationRoute = ["ECA"];
        panel.Refresh();

        Assert.Equal(["SUNOL", "ECA"], panel.RouteRows.Select(row => row.Fix));
        Assert.True(panel.RouteRows[0].IsPassed);
        Assert.Equal("passed", panel.RouteRows[0].Note);
        Assert.False(panel.RouteRows[0].IsNext);
        Assert.True(panel.RouteRows[1].IsNext);
        Click(panel.RouteRows[0]);
        Assert.Equal([(Callsign, "DCT SUNOL", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Toggle_ShowsWhileNavigating_DirectToOpensOnDct_AppendOpensOnAdct()
    {
        DirectToFlyoutPanel direct = Open(MenuIds.NavigationDirectTo, Navigating(), new RecordingMenuHost(""));
        DirectToFlyoutPanel append = Open(MenuIds.NavigationAppendDirectTo, Navigating(), new RecordingMenuHost(""));

        Assert.True(direct.VerbToggles.IsVisible);
        Assert.Equal(DirectToVerb.DirectTo, direct.Verb);
        Assert.Equal(DirectToVerb.AppendDirectTo, append.Verb);
        Assert.Equal("ADCT SUNOL", append.RouteRows[0].SentCommand);
        Assert.Contains("Direct to DCT | [Append to route ADCT]", append.Lines);
    }

    [AvaloniaFact]
    public void Toggle_IsHiddenWhenNotNavigatingToAFix()
    {
        DirectToFlyoutPanel panel = Open(
            MenuIds.NavigationDirectTo,
            Navigating() with
            {
                NavigatingTo = "",
                NavigationRoute = [],
            },
            new RecordingMenuHost("")
        );

        Assert.False(panel.VerbToggles.IsVisible);
        Assert.Equal(DirectToVerb.DirectTo, panel.Verb);
    }

    [AvaloniaFact]
    public void ClickingAppendToRoute_SwitchesEveryCommandToAdct()
    {
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), host);

        Click(panel.Toggle(DirectToVerb.AppendDirectTo));
        Click(panel.RouteRows[1]);

        Assert.Equal(DirectToVerb.AppendDirectTo, panel.Verb);
        Assert.Equal([(Callsign, "ADCT ECA", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Box_AcceptsAnFrd_AndEnterSendsIt()
    {
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), host);

        panel.Box.Text = "sunol090010";
        Assert.Equal("DCT SUNOL090010", panel.BoxCommandText.Text);
        Assert.False(panel.BoxError.IsVisible);
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Equal([(Callsign, "DCT SUNOL090010", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Box_WithAnUnknownFix_ShowsTheRefusal_AndEnterSendsNothing()
    {
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), host);

        panel.Box.Text = "QQQZZ";
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Null(panel.BoxCommand);
        Assert.True(panel.BoxError.IsVisible);
        Assert.Equal(DirectToFlyout.UnknownFix, panel.BoxError.Text);
        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// A military route point (IR149A) resolves as a fix but is no match row, so no row is shown or outlined; Enter still sends the
    /// typed name, upper-cased.
    /// </summary>
    [AvaloniaFact]
    public void Box_WithAMilitaryRoutePoint_ShowsNoRow_AndEnterSendsTheTypedName()
    {
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), host);

        panel.Box.Text = "ir149a";

        Assert.Empty(panel.MatchRows);
        Assert.Null(panel.OutlinedRow);
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal([(Callsign, "DCT IR149A", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Typing_FiltersTheRouteRowsInPlace_AndListsEveryOtherMatchNearestFirst()
    {
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), host);

        panel.Box.Text = "su";

        Assert.True(panel.RouteRows[0].IsVisible);
        Assert.False(panel.RouteRows[1].IsVisible);
        Assert.NotEmpty(panel.MatchRows);
        Assert.InRange(panel.MatchRows.Count, 1, FixList.MaxMatches);
        Assert.All(panel.MatchRows, row => Assert.StartsWith("SU", row.Fix, StringComparison.Ordinal));
        Assert.DoesNotContain(panel.MatchRows, row => row.Fix == "SUNOL");
        double[] distances = [.. panel.MatchRows.Select(row => GeoMath.DistanceNm(OakVor(), Position(row.Fix)))];
        Assert.Equal(distances.Order(), distances);
        Click(panel.MatchRows[0]);
        Assert.Equal([(Callsign, $"DCT {panel.MatchRows[0].Fix}", Initials)], host.Sent);
    }

    /// <summary>Once passed, a row stays passed: when the aircraft stops navigating (FH, vectors) the ● goes and no row changes.</summary>
    [AvaloniaFact]
    public void PassedFix_StaysPassed_WhenNavigationStops_AndTheMarkGoes()
    {
        AircraftModel aircraft = NavigatingModel();
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, aircraft, new RecordingMenuHost(""));
        aircraft.NavigatingTo = "ECA";
        aircraft.NavigationRoute = ["ECA"];
        panel.Refresh();

        aircraft.NavigatingTo = "";
        aircraft.NavigationRoute = [];
        panel.Refresh();

        Assert.Equal(["SUNOL", "ECA"], panel.RouteRows.Select(row => row.Fix));
        Assert.True(panel.RouteRows[0].IsPassed);
        Assert.False(panel.RouteRows[1].IsPassed);
        Assert.DoesNotContain(panel.RouteRows, row => row.IsNext);
    }

    /// <summary>Passing advances only along the route: a direct to a fix off it, or no fix at all, turns no row passed.</summary>
    [AvaloniaFact]
    public void NavigatingOffTheRoute_OrNowhere_PassesNoRow()
    {
        AircraftModel aircraft = NavigatingModel();
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, aircraft, new RecordingMenuHost(""));

        aircraft.NavigatingTo = "SAC";
        aircraft.NavigationRoute = ["SAC"];
        panel.Refresh();
        Assert.DoesNotContain(panel.RouteRows, row => row.IsPassed || row.IsNext);

        aircraft.NavigatingTo = "";
        aircraft.NavigationRoute = [];
        panel.Refresh();
        Assert.DoesNotContain(panel.RouteRows, row => row.IsPassed || row.IsNext);
    }

    /// <summary>Opened on Append to route, the flyout falls back to Direct to, its toggle hidden, when the aircraft stops navigating.</summary>
    [AvaloniaFact]
    public void AppendToRoute_TurnsToDirectTo_WhenNavigationStopsWhileOpen()
    {
        AircraftModel aircraft = NavigatingModel();
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationAppendDirectTo, aircraft, new RecordingMenuHost(""));
        Assert.Equal(DirectToVerb.AppendDirectTo, panel.Verb);

        aircraft.NavigatingTo = "";
        aircraft.NavigationRoute = [];
        panel.Refresh();

        Assert.Equal(DirectToVerb.DirectTo, panel.Verb);
        Assert.False(panel.VerbToggles.IsVisible);
        Assert.Equal("DCT SUNOL", panel.RouteRows[0].SentCommand);
        Assert.Equal("Direct to", panel.Lines[0]);
    }

    /// <summary>One letter matches thousands of fixes; the list shows the nearest 50.</summary>
    [AvaloniaFact]
    public void Typing_OneLetter_ListsExactlyTheNearestFifty()
    {
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), new RecordingMenuHost(""));

        panel.Box.Text = "s";

        Assert.Equal(FixList.MaxMatches, panel.MatchRows.Count);
        Assert.Contains("All fixes · nearest first (50)", panel.Lines);
    }

    /// <summary>The matches are every fix in the navigation data, nearest first however far: from OAK, KJF lists KJFK.</summary>
    [AvaloniaFact]
    public void Typing_ListsAFixAcrossTheCountry()
    {
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), new RecordingMenuHost(""));

        panel.Box.Text = "KJF";

        Assert.Contains(panel.MatchRows, row => row.Fix == "KJFK");
    }

    /// <summary>A refresh rewrites each row's distance and bearing as the aircraft moves, and moves no row.</summary>
    [AvaloniaFact]
    public void Refresh_UpdatesDistances_WithoutReordering()
    {
        AircraftModel aircraft = NavigatingModel();
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, aircraft, new RecordingMenuHost(""));
        panel.Box.Text = "sa";
        string[] order = [.. panel.MatchRows.Select(row => row.Fix)];
        string[] notes = [.. panel.MatchRows.Select(row => row.Note)];
        string sunolNote = panel.RouteRows[0].Note;

        aircraft.Position = Position("SAC");
        panel.Refresh();

        Assert.Equal(order, panel.MatchRows.Select(row => row.Fix));
        Assert.NotEqual(notes, panel.MatchRows.Select(row => row.Note));
        Assert.Equal(FixList.DistanceAndBearing(Position("SAC"), order[0]), panel.MatchRows[0].Note);
        Assert.NotEqual(sunolNote, panel.RouteRows[0].Note);
    }

    /// <summary>Typing outlines the first shown row, which Enter in the box sends: here the nearest fix starting with SA.</summary>
    [AvaloniaFact]
    public void Typing_OutlinesTheFirstMatch_AndEnterSendsIt()
    {
        var host = new RecordingMenuHost("");
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), host);

        panel.Box.Text = "sa";

        DirectToFixRow first = panel.MatchRows[0];
        Assert.Same(first, panel.OutlinedRow);
        Assert.True(first.IsOutlined);
        Assert.Single(panel.RouteRows.Concat(panel.MatchRows), row => row.IsOutlined);
        Assert.Equal($"DCT {first.Fix}", panel.BoxCommandText.Text);
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal([(Callsign, $"DCT {first.Fix}", Initials)], host.Sent);
    }

    /// <summary>A matching route row comes first and is outlined; the row naming the typed text exactly wins over a nearer one.</summary>
    [AvaloniaFact]
    public void Typing_OutlinesTheRouteRowFirst_AndAnExactNameOverANearerMatch()
    {
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), new RecordingMenuHost(""));

        panel.Box.Text = "su";
        Assert.Same(panel.RouteRows[0], panel.OutlinedRow);

        panel.Box.Text = "sac";
        Assert.Equal("SAC", panel.OutlinedRow?.Fix);
        Assert.Equal("DCT SAC", panel.BoxCommand);
    }

    /// <summary>An FRD typed in the box gets its own row, first among the matches: the FRD, its distance and bearing, its command.</summary>
    [AvaloniaFact]
    public void Typing_AnFrd_GivesItsOwnRow_FirstAmongTheMatches()
    {
        DirectToFlyoutPanel panel = Open(MenuIds.NavigationDirectTo, Navigating(), new RecordingMenuHost(""));

        panel.Box.Text = "sunol090010";

        DirectToFixRow frd = panel.MatchRows[0];
        Assert.Equal("SUNOL090010", frd.Fix);
        Assert.Equal("DCT SUNOL090010", frd.SentCommand);
        Assert.Matches(DistanceAndBearing(), frd.Note);
        Assert.Equal(FixList.DistanceAndBearing(OakVor(), "SUNOL090010"), frd.Note);
        Assert.Same(frd, panel.OutlinedRow);
        Assert.DoesNotContain(panel.RouteRows, row => row.IsVisible);
    }

    /// <summary>
    /// The box holds focus at open; Down focuses the first shown row, Down walks the route rows into the matches, Enter sends the
    /// focused row.
    /// </summary>
    [AvaloniaFact]
    public void Keyboard_DownFromTheBox_FocusesTheFirstShownRow_WalksIntoTheMatches_EnterSends()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(Navigating(), host);
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "the fix box does not hold focus at open");
        open.Panel.Box.Text = "e";
        Layout(open.Flyout);

        Press(open.Flyout, Key.Down);
        Assert.True(open.Panel.RouteRows[1].IsKeyboardFocusWithin, "Down from the box does not focus the first shown row");
        Press(open.Flyout, Key.Down);
        DirectToFixRow match = open.Panel.MatchRows[0];
        Assert.True(match.IsKeyboardFocusWithin, "Down does not walk from the route rows into the matches");
        Assert.Empty(host.Sent);

        Press(open.Flyout, Key.Enter);

        Assert.Equal([(Callsign, $"DCT {match.Fix}", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Down from the box lands on the ● row when it is shown.</summary>
    [AvaloniaFact]
    public void Keyboard_DownFromTheBox_FocusesTheMarkedRow()
    {
        using OpenedFlyout open = OpenStrip(Navigating(), new RecordingMenuHost(""));

        Press(open.Flyout, Key.Down);

        Assert.True(open.Panel.RouteRows[0].IsNext);
        Assert.True(open.Panel.RouteRows[0].IsKeyboardFocusWithin, "Down from the box does not focus the ● row");
    }

    /// <summary>Up from a row walks back up the list; Up from the top row returns to the box.</summary>
    [AvaloniaFact]
    public void Keyboard_UpFromTheTopRow_ReturnsToTheBox()
    {
        using OpenedFlyout open = OpenStrip(Navigating(), new RecordingMenuHost(""));
        open.Panel.RouteRows[1].Focus(NavigationMethod.Directional);
        Layout(open.Flyout);

        Press(open.Flyout, Key.Up);
        Assert.True(open.Panel.RouteRows[0].IsKeyboardFocusWithin, "Up does not reach the row above");
        Press(open.Flyout, Key.Up);

        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "Up from the top row does not return to the box");
    }

    /// <summary>Escape closes the flyout back to the strip button and leaves the menu open, sending nothing.</summary>
    [AvaloniaFact]
    public void Keyboard_Escape_ClosesTheFlyout_MenuStaysOpen()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(Navigating(), host);

        Press(open.Flyout, Key.Escape);

        Assert.False(open.Flyout.IsOpen);
        Assert.True(open.Menu.IsOpen);
        Assert.Empty(host.Sent);
    }

    private static AircraftModel NavigatingModel() =>
        new()
        {
            Callsign = Callsign,
            Position = OakVor(),
            NavigatingTo = "SUNOL",
            NavigationRoute = ["SUNOL", "ECA"],
        };

    /// <summary>The Direct to flyout opened from a strip button over a shown window. Disposing closes the menu, then the window.</summary>
    private sealed record OpenedFlyout(Window Window, ContextMenu Menu, MenuFlyout Flyout, DirectToFlyoutPanel Panel) : IDisposable
    {
        public void Dispose()
        {
            Menu.Close();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
        }
    }

    private static OpenedFlyout OpenStrip(IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.NavigationDirectTo);
        MenuItem built = entry.Build(aircraft, Context(), host) ?? throw new InvalidOperationException("no Direct to item");
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(MenuIds.NavigationDirectTo) ?? throw new InvalidOperationException("no Direct to glyph");
        var menu = new ContextMenu();
        MenuItem? strip = QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, glyph), built)]);
        Assert.NotNull(strip);
        menu.Items.Add(strip);
        var window = new Window { Width = 900, Height = 900 };
        try
        {
            window.Show();
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            menu.UpdateLayout();
            Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
            QuickCommandStrip.OpenSubmenu(button);
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Layout(flyout);
            DirectToFlyoutHost holder = Assert.IsType<DirectToFlyoutHost>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);
            return new OpenedFlyout(window, menu, flyout, Assert.IsType<DirectToFlyoutPanel>(holder.Child));
        }
        catch
        {
            menu.Close();
            window.Close();
            throw;
        }
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        IInputElement? focused = TopLevel.GetTopLevel(Assert.IsAssignableFrom<Visual>(flyout.Popup.Child))?.FocusManager?.GetFocusedElement();
        Assert.IsAssignableFrom<Control>(focused).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Dispatcher.UIThread.RunJobs();
        Layout(flyout);
    }

    private static LatLon Position(string fix)
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(fix)!.Value;
        return new LatLon(lat, lon);
    }

    [GeneratedRegex(@"^\d+ NM · \d{3}°$")]
    private static partial Regex DistanceAndBearing();
}
