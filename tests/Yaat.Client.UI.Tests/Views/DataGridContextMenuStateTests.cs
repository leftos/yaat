using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the context-menu cleanup: the aircraft-list right-click menu must only
// offer commands that fit the aircraft's state. Previously an airborne aircraft was shown
// a "Tower" submenu containing "Cleared for takeoff" (and landing departures showed landing
// clearances). The phase-aware items now flow through AircraftCommandApplicability.
public class DataGridContextMenuStateTests
{
    /// <summary>Every item header in <paramref name="menu"/>'s whole tree, submenus included, depth first.</summary>
    private static List<string> Headers(ContextMenu menu) => [.. HeadersIn(menu.Items)];

    private static IEnumerable<string> HeadersIn(ItemCollection items)
    {
        foreach (MenuItem item in items.OfType<MenuItem>())
        {
            if (item.Header is string header)
            {
                yield return header;
            }

            foreach (string child in HeadersIn(item.Items))
            {
                yield return child;
            }
        }
    }

    /// <summary>The aircraft list's right-click menu for <paramref name="ac"/>, through the list's whole-menu builder.</summary>
    private static ContextMenu Build(AircraftModel ac)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Aircraft.Add(ac);
        return DataGridView.BuildAircraftMenu(vm, new DataGrid(), ac, null, [ac]);
    }

    [AvaloniaFact]
    public void AirborneIfrOnFinal_ShowsLandingAndGoAround_NotDepartures()
    {
        ContextMenu menu = Build(
            new AircraftModel
            {
                Callsign = "AAL123",
                IsOnGround = false,
                CurrentPhase = "FinalApproach",
                FlightRules = "IFR",
                AssignedRunway = "28R",
            }
        );

        List<string> headers = Headers(menu);
        Assert.Contains("Cleared to land 28R", headers);
        Assert.Contains("Go around 28R", headers);

        // No departure clearances for an arriving aircraft.
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));

        // VFR-only option clearances hidden for IFR.
        Assert.DoesNotContain(headers, h => h.StartsWith("Touch and go", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Stop and go", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Low approach", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for the option", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void AirborneVfrOnFinal_ShowsOptionClearances()
    {
        ContextMenu menu = Build(
            new AircraftModel
            {
                Callsign = "N12345",
                IsOnGround = false,
                CurrentPhase = "FinalApproach",
                FlightRules = "VFR",
                AssignedRunway = "28L",
            }
        );

        List<string> headers = Headers(menu);
        Assert.Contains("Cleared to land 28L", headers);
        Assert.Contains("Touch and go 28L", headers);
        Assert.Contains("Stop and go 28L", headers);
        Assert.Contains("Low approach 28L", headers);
        Assert.Contains("Cleared for the option 28L", headers);
    }

    [AvaloniaFact]
    public void AirborneDeparture_ShowsNoTowerClearances()
    {
        ContextMenu menu = Build(
            new AircraftModel
            {
                Callsign = "UAL456",
                IsOnGround = false,
                CurrentPhase = "InitialClimb",
                FlightRules = "IFR",
                AssignedRunway = "1L",
            }
        );

        List<string> headers = Headers(menu);
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Go around", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Exit ", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void GroundDeparture_ShowsTakeoffClearances_NotLanding()
    {
        ContextMenu menu = Build(
            new AircraftModel
            {
                Callsign = "SWA789",
                IsOnGround = true,
                CurrentPhase = "LinedUpAndWaiting",
                FlightRules = "IFR",
                AssignedRunway = "30",
            }
        );

        List<string> headers = Headers(menu);
        Assert.Contains("Cleared for takeoff 30", headers);
        Assert.Contains("Cancel takeoff clearance", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Landing_ShowsExits_NotTowerClearances()
    {
        ContextMenu menu = Build(
            new AircraftModel
            {
                Callsign = "DAL111",
                IsOnGround = true,
                CurrentPhase = "Landing",
                FlightRules = "IFR",
                AssignedRunway = "28R",
            }
        );

        List<string> headers = Headers(menu);
        Assert.Contains("Exit left", headers);
        Assert.Contains("Exit right", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
    }

    /// <summary>The whole top-level sequence as text: a separator is "---", an item its header.</summary>
    private static List<string> Sequence(ContextMenu menu)
    {
        var items = new List<string>(menu.Items.Count);
        foreach (object? item in menu.Items)
        {
            items.Add(
                item switch
                {
                    Separator => "---",
                    MenuItem menuItem => menuItem.Header as string ?? "(unnamed)",
                    _ => "(unnamed)",
                }
            );
        }

        return items;
    }

    private static AircraftModel AirborneIfr(string callsign, string phase) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = false,
            FlightRules = "IFR",
            CurrentPhase = phase,
        };

    // Right-clicking another row than the selected one keeps the selected aircraft as the sender of the relative
    // items, as the radar and the ground do.
    [AvaloniaFact]
    public void ListMenu_RightClickOnAnotherRow_OffersTheRelativeItems()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel clicked = AirborneIfr("AAL601", "ApproachNav");
        AircraftModel selected = AirborneIfr("AAL602", "ApproachNav");
        selected.LastReportedTrafficCallsign = clicked.Callsign;
        main.Aircraft.Add(clicked);
        main.Aircraft.Add(selected);

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), clicked, selected, [clicked]);

        List<string> sequence = Sequence(menu);
        Assert.Contains("↪ AAL602:", sequence);
        Assert.Contains("AAL602: report AAL601 in sight", sequence);
        Assert.Contains("AAL602: follow AAL601", sequence);
    }

    [AvaloniaFact]
    public void ListMenu_RightClickOnTheSelectedRow_OffersNoRelativeItems()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel clicked = AirborneIfr("AAL601", "ApproachNav");
        main.Aircraft.Add(clicked);

        (AircraftModel resolved, AircraftModel? previous) = DataGridView.ResolveRightClick(clicked, clicked);
        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), resolved, previous, [resolved]);

        Assert.DoesNotContain(Sequence(menu), item => item.StartsWith('↪'));
    }

    [Fact]
    public void ResolveRightClick_AnotherRowSelected_PreviousIsTheSelectedRow()
    {
        AircraftModel clicked = AirborneIfr("AAL601", "ApproachNav");
        AircraftModel selected = AirborneIfr("AAL602", "ApproachNav");

        (AircraftModel resolved, AircraftModel? previous) = DataGridView.ResolveRightClick(clicked, selected);

        Assert.Same(clicked, resolved);
        Assert.Same(selected, previous);
    }

    [Fact]
    public void ResolveRightClick_SameRowSelected_PreviousIsNull()
    {
        AircraftModel clicked = AirborneIfr("AAL601", "ApproachNav");

        (AircraftModel resolved, AircraftModel? previous) = DataGridView.ResolveRightClick(clicked, clicked);

        Assert.Same(clicked, resolved);
        Assert.Null(previous);
    }

    [Fact]
    public void ResolveRightClick_NothingSelected_PreviousIsNull()
    {
        AircraftModel clicked = AirborneIfr("AAL601", "ApproachNav");

        (AircraftModel resolved, AircraftModel? previous) = DataGridView.ResolveRightClick(clicked, null);

        Assert.Same(clicked, resolved);
        Assert.Null(previous);
    }

    [Fact]
    public void ResolveRightClick_SameCallsignInAnotherCase_PreviousIsNull()
    {
        AircraftModel clicked = AirborneIfr("aal601", "ApproachNav");
        AircraftModel selected = AirborneIfr("AAL601", "ApproachNav");

        (AircraftModel resolved, AircraftModel? previous) = DataGridView.ResolveRightClick(clicked, selected);

        Assert.Same(clicked, resolved);
        Assert.Null(previous);
    }

    // A taxiing pair on the list offers the ground relative items, as the ground and radar menus do.
    [AvaloniaFact]
    public void ListMenu_GroundPair_OffersGiveWayAndFollow()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel clicked = AirborneIfr("SWA104", "Taxiing");
        clicked.IsOnGround = true;
        AircraftModel selected = AirborneIfr("SWA602", "Taxiing");
        selected.IsOnGround = true;
        main.Aircraft.Add(clicked);
        main.Aircraft.Add(selected);

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), clicked, selected, [clicked]);

        List<string> sequence = Sequence(menu);
        Assert.Contains("↪ SWA602:", sequence);
        Assert.Contains("SWA602: give way to SWA104", sequence);
        Assert.Contains("SWA602: follow SWA104", sequence);
        Assert.DoesNotContain(sequence, item => item.Contains("report SWA104 in sight", StringComparison.Ordinal));
    }

    // --- The real list: the headless mouse device on a hosted DataGridView ---

    private static AircraftDto Dto(string callsign) =>
        new(
            Callsign: callsign,
            AircraftType: "B738",
            Latitude: 37.62,
            Longitude: -122.22,
            Heading: 90,
            Altitude: 3000,
            GroundSpeed: 180,
            BeaconCode: 1200,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "LAX",
            Destination: "OAK",
            Route: "",
            FlightRules: "IFR",
            Status: "Active"
        );

    /// <summary>
    /// A hosted aircraft list with two airborne IFR arrivals, AAL601 and AAL602, where AAL602 has reported AAL601 in
    /// sight, so a menu for AAL601 sent from AAL602 offers report in sight and follow.
    /// </summary>
    private static (Window Window, MainViewModel Main, DataGrid Grid) HostList()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = "list-right-click",
                ScenarioName = "List right-click",
                PrimaryAirportId = "OAK",
                PositionDisplayConfig = null,
                FlightStripsConfig = null,
                Aircraft = [Dto("AAL601"), Dto("AAL602")],
                ElapsedSeconds = 0,
            }
        );
        foreach (AircraftModel ac in main.Aircraft)
        {
            ac.IsOnGround = false;
            ac.CurrentPhase = "ApproachNav";
        }

        Find(main, "AAL602").LastReportedTrafficCallsign = "AAL601";

        var view = new DataGridView { DataContext = main };
        var window = new Window
        {
            Width = 800,
            Height = 400,
            Content = view,
        };
        window.ShowAndRunLayout();
        return (window, main, view.GetDataGrid()!);
    }

    private static AircraftModel Find(MainViewModel main, string callsign) => main.Aircraft.Single(a => a.Callsign == callsign);

    /// <summary>
    /// Presses and releases <paramref name="button"/> with the headless mouse device on <paramref name="target"/>, at
    /// most 40 px from its left edge: a row is as wide as all its columns, so its centre can lie outside the window.
    /// </summary>
    private static void ClickAt(Window window, Control target, MouseButton button)
    {
        Point? center = target.TranslatePoint(new Point(Math.Min(40, target.Bounds.Width / 2), target.Bounds.Height / 2), window);
        Assert.NotNull(center);
        window.MouseDown(center.Value, button);
        window.MouseUp(center.Value, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static DataGridRow Row(DataGrid grid, AircraftModel ac) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => ReferenceEquals(r.DataContext, ac));

    [AvaloniaFact]
    public void ListRightClick_OnAnotherRow_KeepsTheSelectionAndOffersTheRelativeItems()
    {
        (Window window, MainViewModel main, DataGrid grid) = HostList();
        try
        {
            main.SelectedAircraft = Find(main, "AAL602");
            Dispatcher.UIThread.RunJobs();

            ClickAt(window, Row(grid, Find(main, "AAL601")), MouseButton.Right);

            Assert.Equal("AAL602", main.SelectedAircraft?.Callsign);
            Assert.NotNull(grid.ContextMenu);
            List<string> sequence = Sequence(grid.ContextMenu);
            Assert.Contains("↪ AAL602:", sequence);
            Assert.Contains("AAL602: report AAL601 in sight", sequence);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ListLeftClick_OnAnotherRow_MovesTheSelection()
    {
        (Window window, MainViewModel main, DataGrid grid) = HostList();
        try
        {
            main.SelectedAircraft = Find(main, "AAL602");
            Dispatcher.UIThread.RunJobs();

            ClickAt(window, Row(grid, Find(main, "AAL601")), MouseButton.Left);

            Assert.Equal("AAL601", main.SelectedAircraft?.Callsign);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ListRightClick_WithNothingSelected_SelectsTheRowAndOffersNoRelativeItems()
    {
        (Window window, MainViewModel main, DataGrid grid) = HostList();
        try
        {
            Assert.Null(main.SelectedAircraft);

            ClickAt(window, Row(grid, Find(main, "AAL601")), MouseButton.Right);

            Assert.Equal("AAL601", main.SelectedAircraft?.Callsign);
            Assert.NotNull(grid.ContextMenu);
            Assert.DoesNotContain(Sequence(grid.ContextMenu), item => item.StartsWith('↪'));
        }
        finally
        {
            window.Close();
        }
    }

    // A keyboard request (the menu key, Shift+F10) has no pointer and comes from the grid itself: it commands the
    // selected row, with no previous selection to send relative items.
    [AvaloniaFact]
    public void ListKeyboardContextRequest_CommandsTheSelectedRowWithNoRelativeItems()
    {
        (Window window, MainViewModel main, DataGrid grid) = HostList();
        try
        {
            main.SelectedAircraft = Find(main, "AAL602");
            Dispatcher.UIThread.RunJobs();

            grid.RaiseEvent(new ContextRequestedEventArgs());
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(grid.ContextMenu);
            List<string> sequence = Sequence(grid.ContextMenu);
            Assert.Contains(sequence, item => item.Contains("AAL602", StringComparison.Ordinal));
            Assert.DoesNotContain(sequence, item => item.StartsWith('↪'));
        }
        finally
        {
            window.Close();
        }
    }

    // A right-click outside the rows opens no menu, not the one the previous row right-click attached.
    [AvaloniaFact]
    public void ListRightClick_OnColumnHeader_LeavesNoStaleMenu()
    {
        (Window window, MainViewModel main, DataGrid grid) = HostList();
        try
        {
            main.SelectedAircraft = Find(main, "AAL602");
            Dispatcher.UIThread.RunJobs();
            ClickAt(window, Row(grid, Find(main, "AAL601")), MouseButton.Right);
            Assert.NotNull(grid.ContextMenu);
            grid.ContextMenu.Close();

            DataGridColumnHeader header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => h.Bounds.Width > 0);
            ClickAt(window, header, MouseButton.Right);

            Assert.Null(grid.ContextMenu);
        }
        finally
        {
            window.Close();
        }
    }
}
