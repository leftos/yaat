using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Taxi to runway tab flyout in the quick-command strip and in All Commands: one tab per runway end in the host's
/// order, above the selected tab's rows (each entry's title, then its variants as two-line rows, then the end's presets).
/// Focus lands on the first row; Up/Down and Tab walk the rows, Left/Right switch tabs without wrapping. A tab whose end
/// was not searched searches its runway when chosen, both of that runway's tabs spinning while the panel reads
/// "Finding runway entries…"; an empty end is dimmed with "No runway entry found". A row sends and closes, previews its
/// own route while pointed at or focused, and choosing another tab or closing the flyout clears the preview.
/// </summary>
public class TaxiToRunwayFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";

    private static readonly RunwayIdentifier Runway12 = new("12", "30");
    private static readonly RunwayIdentifier Runway10R = new("10R", "28L");
    private static readonly RunwayIdentifier Runway10L = new("10L", "28R");
    private static readonly RunwayIdentifier Runway15 = new("15", "33");

    // --- Tabs, and which ends are searched at open --------------------------------------------

    [AvaloniaFact]
    public void Opens_TabsInTheHostsOrder_TheSearchedEndsReadyAndNoSearchStarted()
    {
        FakeSearch search = new();
        RecordingMenuHost host = HostWith(OakEnds(), search);

        MenuFlyout flyout = OpenStripFlyout(host);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.Equal(["30 assigned", "28L departure", "10L", "10R", "12", "15", "28R", "33"], panel.Strip.Tabs.Select(tab => tab.Label));
        Assert.Equal(
            [
                FlyoutTabState.Ready,
                FlyoutTabState.Ready,
                FlyoutTabState.Ready,
                FlyoutTabState.Empty,
                FlyoutTabState.Empty,
                FlyoutTabState.Ready,
                FlyoutTabState.Ready,
                FlyoutTabState.Ready,
            ],
            panel.Strip.Tabs.Select(tab => tab.State)
        );
        Assert.Equal(0, panel.Strip.SelectedIndex);
        Assert.Empty(search.Asked);
        Assert.Equal([Callsign], host.TaxiToRunwayRequests);
    }

    /// <summary>
    /// On the real KOAK layout from gate 25, assigned 30: with 28L a departure runway its tab follows the assigned one, and
    /// the assigned and departure runways are searched at open; with no departure runway only the assigned runway is, and
    /// the rest follow by runway number, unsearched.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Opens_OnKoak_AssignedThenDepartureThenRunwayNumber_OnlyThoseSearched(bool withDeparture)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel ac, MainViewModel main) = AtKoakGate25();
        if (withDeparture)
        {
            main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["28L"] });
        }

        var host = new SendCapturingHost(new ClientMenuHost(main, ac, new Border()), Initials);
        MenuItem entry = MenuCatalog
            .Get(MenuIds.GroundTaxiToRunway)
            .Build(ac, TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None), host)!;

        entry.IsSubMenuOpen = true;
        TaxiToRunwayPanel panel = Panel(entry);

        List<string> expected = withDeparture
            ? ["30 assigned", "28L departure", "10L", "10R", "12", "15", "28R", "33"]
            : ["30 assigned", "10L", "10R", "12", "15", "28L", "28R", "33"];
        Assert.Equal(expected, panel.Strip.Tabs.Select(tab => tab.Label));
        IReadOnlyList<TaxiToRunwayEnd> ends = host.GetTaxiToRunwayChoices(Callsign).RunwayEnds.Ends;
        string[] searchedAtOpen = withDeparture ? ["30", "12", "28L", "10R"] : ["30", "12"];
        Assert.All(ends, end => Assert.Equal(searchedAtOpen.Contains(end.Designator), end.State != TaxiToRunwayEndState.NotSearched));
        Assert.StartsWith("RW At W1 · ", panel.Lines[0]);
    }

    [AvaloniaFact]
    public void Opens_FirstTabNotSearched_SearchesItsRunwayAtOnce()
    {
        FakeSearch search = new();
        RecordingMenuHost host = HostWith(
            [End("10L", Runway10L, TaxiToRunwayEndRole.Other), End("28R", Runway10L, TaxiToRunwayEndRole.Other)],
            search
        );

        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(host));

        Assert.Equal([Runway10L], search.Asked);
        Assert.Equal([FlyoutTabState.Searching, FlyoutTabState.Searching], panel.Strip.Tabs.Select(tab => tab.State));
        Assert.Equal([TaxiToRunwayFlyout.SearchingText], panel.Lines);
    }

    // --- Lazy search ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void ChoosingAnUnsearchedTab_ShowsSearching_ThenItsRows_AndFocusesTheFirst()
    {
        FakeSearch search = new();
        RecordingMenuHost host = HostWith(OakEnds(), search);
        MenuFlyout flyout = OpenStripFlyout(host);
        TaxiToRunwayPanel panel = Panel(flyout);

        panel.Strip.Select(TabIndex(panel, "28R"));
        Layout(flyout);

        Assert.Equal(FlyoutTabState.Searching, Tab(panel, "28R").State);
        Assert.Equal(FlyoutTabState.Searching, Tab(panel, "10L").State);
        Assert.Equal(FlyoutTabState.Ready, Tab(panel, "15").State);
        Assert.Equal([TaxiToRunwayFlyout.SearchingText], panel.Lines);
        Assert.Same(panel.Scroll, Focused(flyout));

        search.Land(Runway10L, ("28R", [Entry("At B", "28R", "B")]), ("10L", []));
        Layout(flyout);

        Assert.Equal(FlyoutTabState.Ready, Tab(panel, "28R").State);
        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "10L").State);
        Assert.Equal(
            ["RW At B · nearest · via B · ~2,300 ft [bold]", "For Departure 28R — TAXI B 28R", "---", "Hold short RWY 28R — TAXI B HS 28R"],
            panel.Lines
        );
        Assert.Equal("TAXI B 28R", FocusedCommand(flyout));
    }

    [AvaloniaFact]
    public void EmptyEnd_TabDimmedNone_PanelSaysNoRunwayEntryFound_FocusStaysInThePanel()
    {
        RecordingMenuHost host = HostWith(OakEnds(), new FakeSearch());
        MenuFlyout flyout = OpenStripFlyout(host);
        TaxiToRunwayPanel panel = Panel(flyout);

        panel.Strip.Select(TabIndex(panel, "12"));
        Layout(flyout);

        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "12").State);
        Assert.Equal([TaxiToRunwayFlyout.NoRunwayEntryText], panel.Lines);
        Assert.Same(panel.Scroll, Focused(flyout));
        Press(flyout, Key.Left);
        Assert.Equal(TabIndex(panel, "10R"), panel.Strip.SelectedIndex);
    }

    [AvaloniaFact]
    public void NoRunwayEndAtAll_NoTabs_PanelSaysNoRunwayEntryFound()
    {
        var host = new RecordingMenuHost("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundTaxiToRunway, null)],
                SoloAdvisoryPrefill.TrueValues,
                false
            ),
        };

        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(host));

        Assert.Empty(panel.Strip.Tabs);
        Assert.False(panel.Strip.IsVisible);
        Assert.Equal([TaxiToRunwayFlyout.NoRunwayEntryText], panel.Lines);
    }

    [AvaloniaFact]
    public void BothEndsOfOneRunway_ShareOneSearch()
    {
        FakeSearch search = new();
        RecordingMenuHost host = HostWith(OakEnds(), search);
        MenuFlyout flyout = OpenStripFlyout(host);
        TaxiToRunwayPanel panel = Panel(flyout);

        panel.Strip.Select(TabIndex(panel, "10L"));
        panel.Strip.Select(TabIndex(panel, "28R"));
        Layout(flyout);
        search.Land(Runway10L, ("28R", [Entry("At B", "28R", "B")]), ("10L", []));
        Layout(flyout);

        Assert.Equal([Runway10L], search.Asked);
        Assert.Equal("TAXI B 28R", FocusedCommand(flyout));
    }

    /// <summary>A search that throws leaves its tabs dimmed none and the panel saying no entry, never stuck Searching.</summary>
    [AvaloniaFact]
    public void FaultingSearch_LeavesTheEndEmpty_NotSearching()
    {
        var ends = new TaxiToRunwayEndList(
            [End("30", Runway12, TaxiToRunwayEndRole.Other), End("12", Runway12, TaxiToRunwayEndRole.Other)],
            _ => Task.FromException<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>(new InvalidOperationException("boom"))
        );
        var host = new RecordingMenuHost("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundTaxiToRunway, null)],
                SoloAdvisoryPrefill.TrueValues,
                false
            ),
            TaxiToRunway = new TaxiToRunwayMenu(ends),
        };

        MenuFlyout flyout = OpenStripFlyout(host);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.All(ends.Ends, end => Assert.Equal(TaxiToRunwayEndState.NotSearched, end.State));
        Assert.Equal([FlyoutTabState.Empty, FlyoutTabState.Empty], panel.Strip.Tabs.Select(tab => tab.State));
        Assert.Equal([TaxiToRunwayFlyout.NoRunwayEntryText], panel.Lines);
    }

    /// <summary>
    /// Arrowing away from a tab whose search failed and back onto it shows its cached none: no second search, and the failure
    /// logged once, for the one search that ran.
    /// </summary>
    [AvaloniaFact]
    public void ArrowingOntoATabWhoseSearchFailed_ShowsTheCachedNone_WithoutSearchingOrLoggingAgain()
    {
        FakeSearch search = new();
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), search));
        TaxiToRunwayPanel panel = Panel(flyout);
        long before = AppLog.RecentErrors.LastSequence;
        FailThe28RSearch(flyout, panel, search);

        Press(flyout, Key.Left);
        Assert.Equal(TabIndex(panel, "15"), panel.Strip.SelectedIndex);
        Press(flyout, Key.Right);

        Assert.Equal(TabIndex(panel, "28R"), panel.Strip.SelectedIndex);
        Assert.Equal(1, search.Asked.Count(runway => runway == Runway10L));
        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "28R").State);
        Assert.Equal([TaxiToRunwayFlyout.NoRunwayEntryText], panel.Lines);
        search.FailPending();
        Assert.Single(AppLog.RecentErrors.Since(before, 50), entry => entry.Message.Contains($"runway {Runway10L} failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A click from another tab on a tab whose search failed searches its runway once more even when the search lands at
    /// once, as one that no longer applies or that faults does: one search per click and, for a fault, one logged failure.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickingATabWhoseSearchFailed_FromAnotherTab_SearchesOnce_WhenTheSearchLandsAtOnce(bool faults)
    {
        InstantSearch search = new(faults);
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), search.Search));
        TaxiToRunwayPanel panel = Panel(flyout);
        panel.Strip.Select(TabIndex(panel, "28R"));
        Layout(flyout);
        Assert.Equal([Runway10L], search.Asked);
        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "28R").State);
        Press(flyout, Key.Left);
        Assert.Equal(TabIndex(panel, "15"), panel.Strip.SelectedIndex);
        long before = AppLog.RecentErrors.LastSequence;

        PressTab(flyout, panel, "28R, none");

        Assert.Equal(TabIndex(panel, "28R"), panel.Strip.SelectedIndex);
        Assert.Equal(2, search.Asked.Count(runway => runway == Runway10L));
        Assert.Equal(
            faults ? 1 : 0,
            AppLog.RecentErrors.Since(before, 50).Count(entry => entry.Message.Contains($"runway {Runway10L} failed", StringComparison.Ordinal))
        );
    }

    /// <summary>Hiding the flyout on a tab whose search failed and opening it again shows the cached none without searching.</summary>
    [AvaloniaFact]
    public void ReopeningTheFlyoutOnATabWhoseSearchFailed_ShowsTheCachedNone_WithoutSearchingAgain()
    {
        FakeSearch search = new();
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), search), out ContextMenu menu);
        TaxiToRunwayPanel panel = Panel(flyout);
        FailThe28RSearch(flyout, panel, search);
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        QuickCommandStrip.OpenSubmenu(StripButton(menu));
        Dispatcher.UIThread.RunJobs();
        Layout(flyout);

        Assert.True(flyout.IsOpen);
        Assert.Equal(TabIndex(panel, "28R"), panel.Strip.SelectedIndex);
        Assert.Equal([Runway10L], search.Asked);
        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "28R").State);
        Assert.Equal([TaxiToRunwayFlyout.NoRunwayEntryText], panel.Lines);
    }

    /// <summary>A right press on the selected tab whose search failed is no deliberate pick: it searches nothing.</summary>
    [AvaloniaFact]
    public void RightPressOnTheSelectedTabWhoseSearchFailed_DoesNotSearchAgain()
    {
        FakeSearch search = new();
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), search));
        TaxiToRunwayPanel panel = Panel(flyout);
        FailThe28RSearch(flyout, panel, search);

        PressTab(flyout, panel, "28R, none", MouseButton.Right);

        Assert.Equal(TabIndex(panel, "28R"), panel.Strip.SelectedIndex);
        Assert.Equal([Runway10L], search.Asked);
        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "28R").State);
    }

    /// <summary>
    /// A deliberate pick of a tab whose search failed searches its runway once more: a click on it while it is selected,
    /// Enter on it, or a click on it from another tab.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("click")]
    [InlineData("enter")]
    [InlineData("click from another tab")]
    public void DeliberatelyPickingATabWhoseSearchFailed_SearchesItsRunwayOnceMore(string pick)
    {
        FakeSearch search = new();
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), search));
        TaxiToRunwayPanel panel = Panel(flyout);
        FailThe28RSearch(flyout, panel, search);
        Press(flyout, Key.Left);
        if (pick != "click from another tab")
        {
            Press(flyout, Key.Right);
        }

        if (pick == "enter")
        {
            Press(flyout, Key.Enter);
        }
        else
        {
            PressTab(flyout, panel, "28R, none");
        }

        Assert.Equal(TabIndex(panel, "28R"), panel.Strip.SelectedIndex);
        Assert.Equal(2, search.Asked.Count(runway => runway == Runway10L));
        Assert.Equal(FlyoutTabState.Searching, Tab(panel, "28R").State);
        Assert.Equal([TaxiToRunwayFlyout.SearchingText], panel.Lines);
    }

    /// <summary>Chooses the 28R tab, whose runway's search then fails, leaving it selected and dimmed none.</summary>
    private static void FailThe28RSearch(MenuFlyout flyout, TaxiToRunwayPanel panel, FakeSearch search)
    {
        panel.Strip.Select(TabIndex(panel, "28R"));
        Layout(flyout);
        search.Fail(Runway10L);
        Layout(flyout);
        Assert.Equal([Runway10L], search.Asked);
        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "28R").State);
    }

    /// <summary>Rows that cannot be drawn once a search lands are logged, and the flyout stays open.</summary>
    [AvaloniaFact]
    public void RowsThatCannotBeShownAfterASearchLands_AreLogged_AndTheFlyoutStaysOpen()
    {
        FakeSearch search = new();
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), search));
        TaxiToRunwayPanel panel = Panel(flyout);
        panel.Strip.Select(TabIndex(panel, "28R"));
        Layout(flyout);
        TaxiRouteRow commandless = Entry("At B", "28R", "B") with { Variants = [new MenuCommandChoice("For Departure 28R", null, null, [])] };
        long before = AppLog.RecentErrors.LastSequence;

        search.Land(Runway10L, ("28R", [commandless]), ("10L", []));
        Layout(flyout);

        Assert.True(flyout.IsOpen);
        Assert.Contains(
            AppLog.RecentErrors.Since(before, 50),
            entry => (entry.Category == "TaxiToRunwayFlyout") && entry.Message.Contains("showing the rows", StringComparison.Ordinal)
        );
    }

    /// <summary>A search landing after the flyout closed draws nothing and moves no focus; reopening shows its rows.</summary>
    [AvaloniaFact]
    public void SearchLandingAfterTheFlyoutClosed_ChangesNothingOnScreen_ReopeningShowsItsRows()
    {
        FakeSearch search = new();
        RecordingMenuHost host = HostWith(OakEnds(), search);
        MenuFlyout flyout = OpenStripFlyout(host, out ContextMenu menu);
        TaxiToRunwayPanel panel = Panel(flyout);
        panel.Strip.Select(TabIndex(panel, "28R"));
        Layout(flyout);
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        IInputElement? focusBefore = FocusedIn(menu);
        int previews = host.RoutePreviews.Count;

        search.Land(Runway10L, ("28R", [Entry("At B", "28R", "B")]), ("10L", []));
        Layout(flyout);

        Assert.Equal([TaxiToRunwayFlyout.SearchingText], panel.Lines);
        Assert.Same(focusBefore, FocusedIn(menu));
        Assert.Equal(previews, host.RoutePreviews.Count);

        QuickCommandStrip.OpenSubmenu(StripButton(menu));
        Dispatcher.UIThread.RunJobs();
        Layout(flyout);

        Assert.True(flyout.IsOpen);
        Assert.Equal(FlyoutTabState.Ready, Tab(panel, "28R").State);
        Assert.Equal("TAXI B 28R", FocusedCommand(flyout));
    }

    /// <summary>A search landing after another tab was chosen updates the searched tabs and leaves the shown tab and focus.</summary>
    [AvaloniaFact]
    public void SearchLandingAfterAnotherTabWasChosen_UpdatesItsTabs_LeavesThePanelAndFocus()
    {
        FakeSearch search = new();
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), search));
        TaxiToRunwayPanel panel = Panel(flyout);
        panel.Strip.Select(TabIndex(panel, "28R"));
        Layout(flyout);
        panel.Strip.Select(TabIndex(panel, "30"));
        Layout(flyout);
        Press(flyout, Key.Down);
        Button first = panel.Rows[0];
        List<string> shown = [.. panel.Lines];

        search.Land(Runway10L, ("28R", [Entry("At B", "28R", "B")]), ("10L", []));
        Layout(flyout);

        Assert.Equal(FlyoutTabState.Ready, Tab(panel, "28R").State);
        Assert.Equal(FlyoutTabState.Empty, Tab(panel, "10L").State);
        Assert.Equal(TabIndex(panel, "30"), panel.Strip.SelectedIndex);
        Assert.Same(first, panel.Rows[0]);
        Assert.Equal(shown, panel.Lines);
        Assert.Equal("TAXI W W1 HS 30", FocusedCommand(flyout));
    }

    // --- Keyboard ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Opens_FocusesTheFirstRow()
    {
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), new FakeSearch()));

        Assert.Equal("TAXI W W1 30", FocusedCommand(flyout));
    }

    [AvaloniaFact]
    public void LeftRight_MoveTabsWithoutWrap_AndFocusTheNewTabsFirstRow()
    {
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), new FakeSearch()));
        TaxiToRunwayPanel panel = Panel(flyout);

        Press(flyout, Key.Left);
        Assert.Equal(0, panel.Strip.SelectedIndex);
        Assert.Equal("TAXI W W1 30", FocusedCommand(flyout));

        Press(flyout, Key.Right);
        Assert.Equal(1, panel.Strip.SelectedIndex);
        Assert.Equal("TAXI B 28L", FocusedCommand(flyout));

        Press(flyout, Key.Left);
        Assert.Equal(0, panel.Strip.SelectedIndex);
        Assert.Equal("TAXI W W1 30", FocusedCommand(flyout));

        panel.Strip.Select(panel.Strip.Tabs.Count - 1);
        Layout(flyout);
        Press(flyout, Key.Right);
        Assert.Equal(panel.Strip.Tabs.Count - 1, panel.Strip.SelectedIndex);
    }

    [AvaloniaFact]
    public void UpDownAndTab_WalkTheRows_SkippingTitles_StoppingAtTheEnds()
    {
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), new FakeSearch()));

        Press(flyout, Key.Up);
        Assert.Equal("TAXI W W1 30", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Assert.Equal("TAXI W W1 HS 30", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Assert.Equal("TAXI W W4 30", FocusedCommand(flyout));
        Press(flyout, Key.Tab);
        Assert.Equal("TAXI W W4 HS 30", FocusedCommand(flyout));
        Press(flyout, Key.Tab, KeyModifiers.Shift);
        Assert.Equal("TAXI W W4 30", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Press(flyout, Key.Down);
        Assert.Equal("TAXI T U W RWY 30", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Assert.Equal("TAXI T U W RWY 30", FocusedCommand(flyout));
    }

    /// <summary>Escape from a focused row, or from the panel of an empty tab, closes the flyout and puts focus back on the strip.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Escape_ClosesTheFlyout_KeepsTheMenuOpen_FocusBackOnTheStrip(bool fromAnEmptyTab)
    {
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), new FakeSearch()), out ContextMenu menu);
        TaxiToRunwayPanel panel = Panel(flyout);
        if (fromAnEmptyTab)
        {
            panel.Strip.Select(TabIndex(panel, "12"));
            Layout(flyout);
            Assert.Same(panel.Scroll, Focused(flyout));
        }
        else
        {
            Assert.Equal("TAXI W W1 30", FocusedCommand(flyout));
        }

        Press(flyout, Key.Escape);

        Assert.False(flyout.IsOpen);
        Assert.True(menu.IsOpen);
        Assert.True(StripRow(menu).IsKeyboardFocusWithin, $"Focus is on {FocusedIn(menu)}, not the strip.");
    }

    /// <summary>A left press selects the tab under the pointer and leaves keyboard focus in the panel, so the arrows still walk the rows.</summary>
    [AvaloniaFact]
    public void PressingTheSelectedTab_KeepsKeyboardFocusInThePanel()
    {
        MenuFlyout flyout = OpenStripFlyout(HostWith(OakEnds(), new FakeSearch()));
        TaxiToRunwayPanel panel = Panel(flyout);

        PressTab(flyout, panel, "28L departure");
        Assert.Equal(TabIndex(panel, "28L"), panel.Strip.SelectedIndex);

        PressTab(flyout, panel, "30 assigned");
        Assert.Equal(0, panel.Strip.SelectedIndex);
        Assert.True(panel.IsKeyboardFocusWithin, $"Focus is on {Focused(flyout)}, outside the panel.");
        Press(flyout, Key.Down);
        Assert.Equal("TAXI W W1 HS 30", FocusedCommand(flyout));
    }

    // --- Choosing and previewing ---------------------------------------------------------------

    [AvaloniaFact]
    public void Row_SendsItsCommand_AndClosesTheFlyoutAndTheMenu()
    {
        RecordingMenuHost host = HostWith(OakEnds(), new FakeSearch());
        MenuFlyout flyout = OpenStripFlyout(host, out ContextMenu menu);

        Press(flyout, Key.Down);
        Press(flyout, Key.Enter);

        Assert.Equal([(Callsign, "TAXI W W1 HS 30", Initials)], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void PointingOrFocusingARow_PreviewsItsRoute_ATabDrawsNothing_ChoosingATabAndClosingClear()
    {
        RecordingMenuHost host = HostWith(OakEnds(), new FakeSearch());
        MenuFlyout flyout = OpenStripFlyout(host);
        TaxiToRunwayPanel panel = Panel(flyout);
        Assert.Same(PreviewOf("TAXI W W1 30"), host.RoutePreviews[^1]);

        RaisePointer(Row(panel, "TAXI W W4 HS 30"), InputElement.PointerEnteredEvent);
        Assert.Same(PreviewOf("TAXI W W4 HS 30"), host.RoutePreviews[^1]);
        Press(flyout, Key.Down);
        Assert.Same(PreviewOf("TAXI W W1 HS 30"), host.RoutePreviews[^1]);

        int previews = host.RoutePreviews.Count;
        RaisePointer(TabView(panel, "28L departure"), InputElement.PointerEnteredEvent);
        Assert.Equal(previews, host.RoutePreviews.Count);

        panel.Strip.Select(TabIndex(panel, "28L"));
        Assert.Null(host.RoutePreviews[previews]);
        Layout(flyout);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(host.RoutePreviews[^1]);
    }

    // --- All Commands, scroll, automation ------------------------------------------------------

    [AvaloniaFact]
    public void AllCommands_SameContent_NoLiveRefill()
    {
        RecordingMenuHost host = HostWith(OakEnds(), new FakeSearch());
        TaxiToRunwayPanel strip = Panel(OpenStripFlyout(HostWith(OakEnds(), new FakeSearch())));
        MenuItem entry = AllCommandsEntry(BuildMenu(host));
        int timers = host.Clock.LiveTimerCount;

        entry.IsSubMenuOpen = true;
        TaxiToRunwayPanel panel = Panel(entry);

        Assert.Equal(strip.Strip.Tabs, panel.Strip.Tabs);
        Assert.Equal(strip.Lines, panel.Lines);
        Assert.Equal(timers, host.Clock.LiveTimerCount);

        host.TaxiToRunway = TaxiToRunwayMenu.Empty;
        entry.IsSubMenuOpen = false;
        entry.IsSubMenuOpen = true;

        Assert.Equal([Callsign], host.TaxiToRunwayRequests);
        Assert.Equal(strip.Lines, panel.Lines);

        panel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Assert.Equal(1, panel.Strip.SelectedIndex);
    }

    /// <summary>
    /// In a real menu's All Commands: the first row takes focus as the submenu opens, Right and Left from a focused row change
    /// tab with the submenu still open and focus the new tab's first row, and closing the submenu clears the preview.
    /// </summary>
    [AvaloniaFact]
    public void AllCommands_InAMenu_FirstRowFocused_LeftRightKeepTheSubmenuOpen_ClosingClearsThePreview()
    {
        RecordingMenuHost host = HostWith(OakEnds(), new FakeSearch());
        (ContextMenu _, MenuItem entry) = OpenAllCommandsEntry(host);
        TaxiToRunwayPanel panel = Panel(entry);

        Assert.Equal("TAXI W W1 30", FocusedCommandIn(panel));

        PressIn(panel, Key.Right);
        Assert.Equal(1, panel.Strip.SelectedIndex);
        Assert.True(entry.IsSubMenuOpen);
        Assert.Equal("TAXI B 28L", FocusedCommandIn(panel));

        PressIn(panel, Key.Left);
        Assert.Equal(0, panel.Strip.SelectedIndex);
        Assert.True(entry.IsSubMenuOpen);
        Assert.Equal("TAXI W W1 30", FocusedCommandIn(panel));
        Assert.Same(PreviewOf("TAXI W W1 30"), host.RoutePreviews[^1]);

        entry.IsSubMenuOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(host.RoutePreviews[^1]);
    }

    /// <summary>In a real menu's All Commands, choosing a row sends its command and closes the menu.</summary>
    [AvaloniaFact]
    public void AllCommands_InAMenu_ChoosingARow_SendsAndClosesTheMenu()
    {
        RecordingMenuHost host = HostWith(OakEnds(), new FakeSearch());
        (ContextMenu menu, MenuItem entry) = OpenAllCommandsEntry(host);
        TaxiToRunwayPanel panel = Panel(entry);

        PressIn(panel, Key.Down);
        PressIn(panel, Key.Enter);

        Assert.Equal([(Callsign, "TAXI W W1 HS 30", Initials)], host.Sent);
        Assert.False(menu.IsOpen);
        Assert.Null(host.RoutePreviews[^1]);
    }

    /// <summary>A tall tab (KSFO 28R from F12: five entries, 38 variants) scrolls under the pinned tabs, inside the cap.</summary>
    [AvaloniaFact]
    public void TallTab_ScrollsUnderThePinnedTabs_InsideTheCap()
    {
        string[] names = ["At C", "At C2", "At N", "At P", "At L"];
        TaxiRouteRow[] entries = [.. names.Select(name => EntryWithVariants(name, 8))];
        RecordingMenuHost host = HostWith(
            [End("28R", Runway10L, TaxiToRunwayEndRole.Assigned, entries), End("10L", Runway10L, TaxiToRunwayEndRole.Other)],
            new FakeSearch()
        );
        MenuFlyout flyout = OpenStripFlyout(host);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.Equal(40, panel.Rows.Count);
        Assert.True(panel.Scroll.Bounds.Height <= TaxiToRunwayFlyout.RowsMaxHeight + 0.5, $"The rows are {panel.Scroll.Bounds.Height:F1}px tall.");
        Assert.True(panel.Scroll.Extent.Height > panel.Scroll.Viewport.Height + 1, "The tall tab does not scroll.");
        Assert.True(panel.Bounds.Width <= MenuCommandRow.RowMaxWidth + 0.5, $"The panel is {panel.Bounds.Width:F1}px wide.");

        for (int i = 1; i < panel.Rows.Count; i++)
        {
            Press(flyout, Key.Down);
        }

        Assert.Same(panel.Rows[^1], Focused(flyout));
        Assert.True(panel.Scroll.Offset.Y > 0, "Focusing the last row did not scroll it into view.");
        Assert.True(panel.Strip.IsVisible && (panel.Strip.Bounds.Bottom <= panel.Scroll.Bounds.Top + 0.5), "The tabs are not pinned above the rows.");
    }

    [AvaloniaFact]
    public void Tab_FoundAndInvokedThroughUiAutomation_ByItsName()
    {
        FakeSearch search = new();
        RecordingMenuHost host = HostWith(OakEnds(), search);
        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(host));

        AutomationPeer tab = Assert.Single(
            panel.Strip.GetVisualDescendants().OfType<Control>().Select(ControlAutomationPeer.CreatePeerForElement),
            peer => (peer.GetAutomationControlType() == AutomationControlType.TabItem) && (peer.GetName() == "28R")
        );
        Assert.False(tab.IsKeyboardFocusable());
        Assert.Single(
            panel.Strip.GetVisualDescendants().OfType<Control>().Select(ControlAutomationPeer.CreatePeerForElement),
            peer => (peer.GetAutomationControlType() == AutomationControlType.TabItem) && (peer.GetName() == "12, none")
        );

        Assert.IsAssignableFrom<IInvokeProvider>(tab.GetProvider<IInvokeProvider>()).Invoke();

        Assert.Equal(TabIndex(panel, "28R"), panel.Strip.SelectedIndex);
        Assert.Equal([Runway10L], search.Asked);
        Assert.Equal("28R, searching", tab.GetName());
    }

    // --- Harness -------------------------------------------------------------------------------

    /// <summary>A runway search the test lands by hand: every runway asked for, in order, and its pending result.</summary>
    private sealed class FakeSearch
    {
        private readonly Dictionary<
            RunwayIdentifier,
            TaskCompletionSource<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>
        > _pending = [];

        public List<RunwayIdentifier> Asked { get; } = [];

        public Task<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?> Search(RunwayIdentifier runway)
        {
            Asked.Add(runway);
            var result = new TaskCompletionSource<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>();
            _pending[runway] = result;
            return result.Task;
        }

        public void Land(RunwayIdentifier runway, params (string Designator, IReadOnlyList<TaxiRouteRow> Rows)[] found)
        {
            _pending[runway].SetResult(found);
            Dispatcher.UIThread.RunJobs();
        }

        public void Fail(RunwayIdentifier runway)
        {
            _pending[runway].SetException(new InvalidOperationException($"The test failed the search of runway {runway}."));
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Fails every search still running, so each one asked for logs its failure.</summary>
        public void FailPending()
        {
            foreach (
                (
                    RunwayIdentifier runway,
                    TaskCompletionSource<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?> result
                ) in _pending
            )
            {
                result.TrySetException(new InvalidOperationException($"The test failed the search of runway {runway}."));
            }

            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A runway search that lands at once: no rows, as when the layout changed, or faulted. Every runway asked for, in order.</summary>
    private sealed class InstantSearch(bool faults)
    {
        public List<RunwayIdentifier> Asked { get; } = [];

        public Task<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?> Search(RunwayIdentifier runway)
        {
            Asked.Add(runway);
            return faults
                ? Task.FromException<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>(
                    new InvalidOperationException($"The test failed the search of runway {runway}.")
                )
                : Task.FromResult<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>(null);
        }
    }

    private static readonly Dictionary<string, TaxiRoute> Previews = [];

    /// <summary>The route a variant or preset sending <paramref name="command"/> previews, the same object every time.</summary>
    private static TaxiRoute PreviewOf(string command)
    {
        if (!Previews.TryGetValue(command, out TaxiRoute? route))
        {
            route = new TaxiRoute { Segments = [], HoldShortPoints = [] };
            Previews[command] = route;
        }

        return route;
    }

    /// <summary>
    /// KOAK's eight runway ends as opened assigned 30 with 28L a departure runway: 12/30 and 10R/28L searched (30 and 28L
    /// with rows, 12 and 10R empty), 10L/28R and 15/33 not searched.
    /// </summary>
    private static List<TaxiToRunwayEnd> OakEnds() =>
        [
            End(
                "30",
                Runway12,
                TaxiToRunwayEndRole.Assigned,
                [
                    Entry("At W1", "30", "W W1"),
                    Entry("At W4", "30", "W W4"),
                    new TaxiRouteRow(
                        TaxiRouteRow.PresetBadge,
                        "TERMINAL to 30",
                        null,
                        null,
                        false,
                        "via T U W",
                        5800,
                        "TAXI T U W RWY 30",
                        PreviewOf("TAXI T U W RWY 30"),
                        []
                    ),
                ]
            ),
            End("28L", Runway10R, TaxiToRunwayEndRole.Departure, [Entry("At B", "28L", "B")]),
            End("10L", Runway10L, TaxiToRunwayEndRole.Other),
            End("10R", Runway10R, TaxiToRunwayEndRole.Other, []),
            End("12", Runway12, TaxiToRunwayEndRole.Other, []),
            End("15", Runway15, TaxiToRunwayEndRole.Other),
            End("28R", Runway10L, TaxiToRunwayEndRole.Other),
            End("33", Runway15, TaxiToRunwayEndRole.Other),
        ];

    /// <summary>An end not searched yet.</summary>
    private static TaxiToRunwayEnd End(string designator, RunwayIdentifier runway, TaxiToRunwayEndRole role) =>
        new(designator, runway, role, TaxiToRunwayEndState.NotSearched, []);

    /// <summary>A searched end: Ready with <paramref name="rows"/>, Empty without.</summary>
    private static TaxiToRunwayEnd End(string designator, RunwayIdentifier runway, TaxiToRunwayEndRole role, IReadOnlyList<TaxiRouteRow> rows) =>
        new(designator, runway, role, (rows.Count > 0) ? TaxiToRunwayEndState.Ready : TaxiToRunwayEndState.Empty, rows);

    /// <summary>
    /// A runway entry to <paramref name="end"/> named <paramref name="name"/> along <paramref name="taxiways"/>, nearest,
    /// opening For departure and Hold short.
    /// </summary>
    private static TaxiRouteRow Entry(string name, string end, string taxiways)
    {
        string departure = $"TAXI {taxiways} {end}";
        string holdShort = $"TAXI {taxiways} HS {end}";
        return new TaxiRouteRow(
            TaxiRouteRow.RunwayEntryBadge,
            name,
            "nearest",
            null,
            true,
            $"via {taxiways}",
            2300,
            departure,
            PreviewOf(departure),
            [
                new MenuCommandChoice($"For Departure {end}", departure, PreviewOf(departure), []),
                MenuCommandChoice.Separator,
                new MenuCommandChoice($"Hold short RWY {end}", holdShort, PreviewOf(holdShort), []),
            ]
        );
    }

    /// <summary>A 28R entry with <paramref name="count"/> long crossing variants.</summary>
    private static TaxiRouteRow EntryWithVariants(string name, int count)
    {
        List<MenuCommandChoice> variants =
        [
            .. Enumerable
                .Range(1, count)
                .Select(i =>
                {
                    string command = $"TAXI T8 A F {name[3..]} HS {i}L RWY 28R, CROSS 1L, CROSS 1R, CROSS 10R";
                    return new MenuCommandChoice($"For Departure 28R, CROSS 1L 1R, HS {i}", command, PreviewOf(command), []);
                }),
        ];
        return new TaxiRouteRow(
            TaxiRouteRow.RunwayEntryBadge,
            name,
            null,
            9400,
            false,
            "via T8 A F",
            10550,
            variants[0].Command!,
            PreviewOf(variants[0].Command!),
            variants
        );
    }

    private static RecordingMenuHost HostWith(List<TaxiToRunwayEnd> ends, FakeSearch search) => HostWith(ends, search.Search);

    private static RecordingMenuHost HostWith(
        List<TaxiToRunwayEnd> ends,
        Func<RunwayIdentifier, Task<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>> search
    ) =>
        new("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundTaxiToRunway, null)],
                SoloAdvisoryPrefill.TrueValues,
                false
            ),
            TaxiToRunway = new TaxiToRunwayMenu(new TaxiToRunwayEndList(ends, search)),
        };

    /// <summary>A B738 parked at KOAK gate 25, assigned 30, on a view model holding the KOAK layout.</summary>
    private static (AircraftModel Aircraft, MainViewModel Main) AtKoakGate25()
    {
        GroundNodeDto gate = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == "Parking") && (n.Name == "25"));
        var ac = new AircraftModel
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
            Situation = AircraftSituation.AtParking,
            AssignedRunway = "30",
            Position = new LatLon(gate.Latitude, gate.Longitude),
        };
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        return (ac, main);
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

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host) => OpenStripFlyout(host, out _);

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host, out ContextMenu menu)
    {
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        menu = BuildMenu(host);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(b => (string)b.Tag! == MenuIds.GroundTaxiToRunway);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return flyout;
    }

    private static MenuItem StripRow(ContextMenu menu) => Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);

    private static Button StripButton(ContextMenu menu) =>
        QuickCommandStrip.Buttons(StripRow(menu)).Single(b => (string)b.Tag! == MenuIds.GroundTaxiToRunway);

    /// <summary>Opens the menu in a window, then All Commands › Taxi to runway through every submenu above it.</summary>
    private static (ContextMenu Menu, MenuItem Entry) OpenAllCommandsEntry(RecordingMenuHost host)
    {
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        ContextMenu menu = BuildMenu(host);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        MenuItem entry = AllCommandsEntry(menu);
        List<MenuItem> path = [];
        for (MenuItem? item = entry; item is not null; item = item.Parent as MenuItem)
        {
            path.Insert(0, (MenuItem)item);
        }

        foreach (MenuItem item in path)
        {
            item.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
        }

        LayoutAround(Panel(entry));
        return (menu, entry);
    }

    private static MenuItem AllCommandsEntry(ContextMenu menu) =>
        Assert.Single(
            Flatten(Assert.Single(menu.Items.OfType<MenuItem>(), item => (item.Header as string) == AircraftMenuBuilder.AllCommandsHeader).Items),
            item => (item.Header as string) == MenuCatalog.Get(MenuIds.GroundTaxiToRunway).Label
        );

    private static IEnumerable<MenuItem> Flatten(ItemCollection items) =>
        items.OfType<MenuItem>().SelectMany(item => (IEnumerable<MenuItem>)[item, .. Flatten(item.Items)]);

    private static TaxiToRunwayPanel Panel(MenuFlyout flyout) =>
        Assert.IsType<TaxiToRunwayPanel>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);

    private static TaxiToRunwayPanel Panel(MenuItem entry) => Assert.IsType<TaxiToRunwayPanel>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);

    private static FlyoutTab Tab(TaxiToRunwayPanel panel, string designator) => panel.Strip.Tabs[TabIndex(panel, designator)];

    private static int TabIndex(TaxiToRunwayPanel panel, string designator)
    {
        int index = panel.Strip.Tabs.ToList().FindIndex(tab => tab.Label.Split(' ')[0] == designator);
        Assert.True(index >= 0, $"No tab {designator}.");
        return index;
    }

    /// <summary>The tab's own view: the strip's descendant named for assistive technology by <paramref name="label"/>.</summary>
    private static Control TabView(TaxiToRunwayPanel panel, string label) =>
        panel.Strip.GetVisualDescendants().OfType<Border>().First(border => Avalonia.Automation.AutomationProperties.GetName(border) == label);

    /// <summary>Presses the left button at the centre of the tab shown as <paramref name="label"/>.</summary>
    private static void PressTab(MenuFlyout flyout, TaxiToRunwayPanel panel, string label) => PressTab(flyout, panel, label, MouseButton.Left);

    /// <summary>Presses <paramref name="button"/> at the centre of the tab shown as <paramref name="label"/>.</summary>
    private static void PressTab(MenuFlyout flyout, TaxiToRunwayPanel panel, string label, MouseButton button)
    {
        Control tab = TabView(panel, label);
        TopLevel popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(tab));
        Point centre = Assert.NotNull(tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), popup));
        popup.MouseDown(centre, button);
        popup.MouseUp(centre, button);
        Layout(flyout);
    }

    private static Button Row(TaxiToRunwayPanel panel, string command) =>
        Assert.Single(panel.Rows, row => MenuCommandText.GetCommand(row) == command);

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key) => Press(flyout, key, KeyModifiers.None);

    /// <summary>Presses <paramref name="key"/> with <paramref name="modifiers"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key, KeyModifiers modifiers)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
            }
        );
        Layout(flyout);
    }

    /// <summary>Lays out the top level holding <paramref name="visual"/> and runs the jobs it posts.</summary>
    private static void LayoutAround(Visual visual)
    {
        TopLevel.GetTopLevel(visual)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel(visual)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the top level holding <paramref name="visual"/>.</summary>
    private static void PressIn(Visual visual, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(FocusedIn(visual));
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        LayoutAround(visual);
    }

    private static IInputElement? Focused(MenuFlyout flyout) => FocusedIn(Assert.IsAssignableFrom<Visual>(flyout.Popup.Child));

    private static IInputElement? FocusedIn(Visual visual) => TopLevel.GetTopLevel(visual)?.FocusManager?.GetFocusedElement();

    private static string? FocusedCommand(MenuFlyout flyout) => CommandOf(Focused(flyout));

    private static string? FocusedCommandIn(Visual visual) => CommandOf(FocusedIn(visual));

    private static string? CommandOf(IInputElement? focused) =>
        ((focused as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault() is { } row) ? MenuCommandText.GetCommand(row) : null;

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
