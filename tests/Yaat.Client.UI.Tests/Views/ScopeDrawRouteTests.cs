using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Draw route on the Scope: the aircraft menu's Draw route and the EuroScope Destination field start it for an aircraft that may
/// be commanded; while drawing, a left click places a waypoint (Alt+left still measures), a middle click on a waypoint opens its
/// condition popup, a right click on one its menu, Enter sends the route and Backspace undoes, both ahead of the entry, and Esc
/// cancels the route before it clears the entry.
/// </summary>
public class ScopeDrawRouteTests
{
    private const string Callsign = "UAL238";

    // Empty map points, far from the track the tests draw at (300, 300).
    private static readonly Point MapA = new(100, 500);
    private static readonly Point MapB = new(600, 450);

    private sealed record Shown(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)
    {
        public ScopeCanvas Canvas => View.Canvas;

        public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];
    }

    private static async Task<Shown> ShowScopeAsync(string templateId = ScopeTemplates.MixedId, Action<AircraftModel>? setUp = null)
    {
        MainViewModel main = await MainAsync();
        await main.CrcAliasLoad;
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null) { TemplateId = templateId };
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AircraftModel ac = AddAt300(main, view.Canvas, setUp ?? (_ => { }));
        var shown = new Shown(main, scope, view, window, ac);
        scope.SendPilotCommand = (callsign, command, initials) =>
        {
            shown.Sent.Add((callsign, command, initials));
            return Task.CompletedTask;
        };
        return shown;
    }

    // A B738 at 5000 ft drawn at (300, 300), with nothing selected.
    private static AircraftModel AddAt300(MainViewModel main, ScopeCanvas canvas, Action<AircraftModel> setUp)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon(300f, 300f);
        AircraftModel ac = AddAircraft(main, Callsign, 5000);
        ac.Position = new LatLon(lat, lon);
        ac.Destination = "KSFO";
        setUp(ac);
        main.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static void CloseWindow(Shown s)
    {
        s.View.ActiveContextMenu?.Close();
        s.View.ActiveRouteConditionPopup?.Close();
        Dispatcher.UIThread.RunJobs();
        s.Window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static Point InWindow(Shown s, Point canvasPoint)
    {
        Point? inWindow = s.Canvas.TranslatePoint(canvasPoint, s.Window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    private static void Click(Shown s, Point canvasPoint, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Point at = InWindow(s, canvasPoint);
        s.Window.MouseMove(at, modifiers);
        s.Window.MouseDown(at, button, modifiers);
        s.Window.MouseUp(at, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point SymbolOf(Shown s)
    {
        ScopeTrack track = Assert.Single(s.Canvas.CurrentFrame.Tracks, t => ReferenceEquals(t.Aircraft, s.Aircraft));
        return new Point(track.Symbol.X, track.Symbol.Y);
    }

    // Starts the mode from the aircraft menu's Draw route and places a waypoint at each point.
    private static void DrawRoute(Shown s, params Point[] points)
    {
        PickDrawRoute(s);
        foreach (Point point in points)
        {
            Click(s, point);
        }
    }

    private static void PickDrawRoute(Shown s)
    {
        MenuItem item = Assert.Single(MenuItems(AircraftMenu(s)), m => m.Header is "Draw route");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static ContextMenu AircraftMenu(Shown s) => s.View.BuildAircraftMenu(s.Scope, s.Aircraft, null, Callsign);

    private static List<MenuItem> MenuItems(ItemsControl menu) =>
        [.. menu.Items.OfType<MenuItem>().SelectMany(m => (List<MenuItem>)[m, .. MenuItems(m)])];

    private static void ArmRingsTool(ScopeViewModel scope)
    {
        scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsJRing };
        scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
        Assert.True(scope.IsRingToolArmed);
    }

    [AvaloniaFact]
    public async Task AircraftMenu_DrawRoute_StartsTheModeForTheAircraft()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            List<MenuItem> items = MenuItems(AircraftMenu(s));
            int display = items.FindIndex(m => m.Header is "Display");
            Assert.True(items.FindIndex(m => m.Header is "Draw route") > display);

            PickDrawRoute(s);

            Assert.True(s.Scope.DrawRoute.IsDrawing);
            Assert.Equal(Callsign, s.Scope.DrawRoute.Callsign);
            Assert.Same(s.Aircraft, s.Main.SelectedAircraft);
            Assert.Same(ScopeCanvas.DrawRouteCursor, s.Canvas.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task AircraftMenu_HasNoDrawRoute_ForAnAircraftOnTheGround()
    {
        Shown s = await ShowScopeAsync(setUp: ac => ac.IsOnGround = true);
        try
        {
            Assert.DoesNotContain(MenuItems(AircraftMenu(s)), m => m.Header is "Draw route");
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task AircraftMenu_HasNoDrawRoute_ForALiveTrafficShadow()
    {
        Shown s = await ShowScopeAsync(setUp: ac =>
        {
            // Airborne phase on the ground: HidesFlightCommands lets it through, so only the shadow guard hides the row.
            ac.CurrentPhase = "Approach";
            ac.IsLiveTraffic = true;
            ac.IsOnGround = true;
        });
        try
        {
            Assert.DoesNotContain(MenuItems(AircraftMenu(s)), m => m.Header is "Draw route");
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task StartingTheMode_DisarmsTheRingsTool()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            ArmRingsTool(s.Scope);

            PickDrawRoute(s);

            Assert.False(s.Scope.IsRingToolArmed);
            Assert.True(s.Scope.DrawRoute.IsDrawing);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task LeftClick_OnTheMap_PlacesAWaypoint_AndFillsNothing()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s);
            s.Scope.TypeEntryText("QQ");

            Click(s, MapA);

            DrawnWaypoint wp = Assert.Single(s.Scope.DrawRoute.Waypoints!);
            (double lat, double lon) = s.Canvas.Viewport.ScreenToLatLon((float)MapA.X, (float)MapA.Y);
            Assert.Equal(lat, wp.Lat, 6);
            Assert.Equal(lon, wp.Lon, 6);
            Assert.Equal("QQ", s.Scope.EntryText);
            Assert.Same(s.Aircraft, s.Main.SelectedAircraft);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task LeftClick_OnTheAircraft_PlacesAWaypoint()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s);
            s.Scope.TypeEntryText("QQ");

            Click(s, SymbolOf(s));

            Assert.Single(s.Scope.DrawRoute.Waypoints!);
            Assert.Equal("QQ", s.Scope.EntryText);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task LeftClick_NearAWaypoint_PlacesNone()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA);

            Click(s, new Point(MapA.X + 10, MapA.Y));

            Assert.Single(s.Scope.DrawRoute.Waypoints!);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task AltLeftClick_IsLeftToTheMeasureTool()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s);

            Click(s, MapA, MouseButton.Left, RawInputModifiers.Alt);

            Assert.Null(s.Scope.DrawRoute.Waypoints);
            Assert.True(s.Scope.DrawRoute.IsDrawing);
            Assert.NotNull(s.Main.Measure.Anchor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task MiddleClick_OnAWaypoint_OpensItsConditionPopup_WhoseOkSetsTheCondition()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA);
            string name = s.Scope.DrawRoute.Waypoints![0].ResolvedName;

            Click(s, MapA, MouseButton.Middle);

            ScopeWaypointConditionPopup popup = Assert.IsType<ScopeWaypointConditionPopup>(s.View.ActiveRouteConditionPopup);
            Assert.Equal($"Conditions at {name}", popup.Header.Text);
            popup.AltitudeBox.Text = "080";
            popup.CommandsBox.Text = "SPD 250";
            popup.Submit();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new WaypointCondition("080", "SPD 250"), s.Scope.DrawRoute.GetCondition(0));
            Assert.Null(s.View.ActiveRouteConditionPopup);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task Esc_WithTheConditionPopupOpen_ClosesOnlyThePopup()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA);
            Click(s, MapA, MouseButton.Middle);
            Assert.NotNull(s.View.ActiveRouteConditionPopup);

            s.Window.DispatchKey(Key.Escape);

            Assert.Null(s.View.ActiveRouteConditionPopup);
            Assert.True(s.Scope.DrawRoute.IsDrawing);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task MiddleClick_OffEveryWaypoint_IsSwallowed_AndTogglesNoHighlight()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA);

            Click(s, SymbolOf(s), MouseButton.Middle);

            Assert.Empty(s.Scope.DataBlockState.HighlightedCallsigns);
            Assert.Null(s.View.ActiveRouteConditionPopup);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task RightClick_OnAWaypoint_OpensItsMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA, MapB);
            string name = s.Scope.DrawRoute.Waypoints![0].ResolvedName;

            Click(s, MapA, MouseButton.Right);

            ContextMenu menu = Assert.IsType<ContextMenu>(s.View.ActiveContextMenu);
            Assert.Equal(
                [name, "Confirm route", "Delete waypoint", "Delete waypoints after", "Cancel route"],
                menu.Items.OfType<MenuItem>().Select(m => m.Header as string)
            );
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task RightClick_OnTheLastWaypoint_OffersNoDeleteAfter()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA, MapB);

            Click(s, MapB, MouseButton.Right);

            ContextMenu menu = Assert.IsType<ContextMenu>(s.View.ActiveContextMenu);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header is "Delete waypoints after");
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task RightClick_OnTheAircraft_OffEveryWaypoint_GoesDownTheLadder_ToTheAircraftMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA);

            Click(s, SymbolOf(s), MouseButton.Right);

            ContextMenu menu = Assert.IsType<ContextMenu>(s.View.ActiveContextMenu);
            Assert.Contains(MenuItems(menu), m => m.Header is "Draw route");
            Assert.True(s.Scope.DrawRoute.IsDrawing);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task Enter_SendsTheRoute_AndLeavesTheEntry()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA, MapB);
            IReadOnlyList<DrawnWaypoint> waypoints = s.Scope.DrawRoute.Waypoints!;
            s.Scope.TypeEntryText("QQ");

            s.Window.DispatchKey(Key.Enter);

            (string callsign, string command, string initials) = Assert.Single(s.Sent);
            Assert.Equal(Callsign, callsign);
            Assert.Equal($"DCTF {waypoints[0].ResolvedName} {waypoints[1].ResolvedName}", command);
            Assert.Equal(s.Main.Preferences.UserInitials, initials);
            Assert.False(s.Scope.DrawRoute.IsDrawing);
            Assert.Equal("QQ", s.Scope.EntryText);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task Backspace_UndoesTheLastWaypoint_AndLeavesTheEntry()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA, MapB);
            s.Scope.TypeEntryText("QQ");

            s.Window.DispatchKey(Key.Back);

            Assert.Single(s.Scope.DrawRoute.Waypoints!);
            Assert.Equal("QQ", s.Scope.EntryText);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task Esc_CancelsTheRoute_BeforeItClearsTheEntry()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA);
            s.Scope.TypeEntryText("QQ");

            s.Window.DispatchKey(Key.Escape);

            Assert.False(s.Scope.DrawRoute.IsDrawing);
            Assert.Equal("QQ", s.Scope.EntryText);
            Assert.Empty(s.Sent);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task SelectingAnotherAircraft_CancelsTheRoute()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            AddAircraft(s.Main, "DAL2", 6000);
            DrawRoute(s, MapA);

            s.Scope.SelectAircraft("DAL2");

            Assert.False(s.Scope.DrawRoute.IsDrawing);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task Frame_CarriesTheRoute_AndTheRubberBandToThePointer()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            DrawRoute(s, MapA);
            s.Window.MouseMove(InWindow(s, MapB));
            Dispatcher.UIThread.RunJobs();

            ScopeDrawRouteOverlay overlay = Assert.IsType<ScopeDrawRouteOverlay>(s.Canvas.RenderSnapshotForTesting().Overlays?.DrawRoute);

            Assert.Single(overlay.Waypoints);
            Assert.Equal(s.Aircraft.Position, overlay.Origin);
            (double lat, double lon) = s.Canvas.Viewport.ScreenToLatLon((float)MapB.X, (float)MapB.Y);
            Assert.Equal(lat, overlay.Cursor!.Value.Lat, 6);
            Assert.Equal(lon, overlay.Cursor.Value.Lon, 6);
            Assert.Equal(s.Main.ScopeMaps.DescribePoint(lat, lon), overlay.CursorLabel);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task EuroScopeDestinationClick_StartsTheMode()
    {
        Shown s = await ShowScopeAsync(ScopeTemplates.EuroScopeId);
        try
        {
            Click(s, DestinationField(s));

            Assert.True(s.Scope.DrawRoute.IsDrawing);
            Assert.Equal(Callsign, s.Scope.DrawRoute.Callsign);
            Assert.Null(s.View.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task DestinationFieldAction_IsInert_ForAnAircraftOnTheGround()
    {
        Shown s = await ShowScopeAsync(ScopeTemplates.EuroScopeId, ac => ac.IsOnGround = true);
        try
        {
            s.View.OnFieldClicked(Callsign, ScopeFieldAction.StartDrawRoute, new Point(300, 300), KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(s.Scope.DrawRoute.IsDrawing);
            Assert.Null(s.View.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    private static Point DestinationField(Shown s)
    {
        FieldRect field = Assert.Single(s.Canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == ScopeFieldSlot.Destination);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }

    // Opens the menu of the first of two waypoints.
    private static ContextMenu OpenWaypointMenu(Shown s)
    {
        DrawRoute(s, MapA, MapB);
        Click(s, MapA, MouseButton.Right);
        return Assert.IsType<ContextMenu>(s.View.ActiveContextMenu);
    }

    [AvaloniaFact]
    public async Task WaypointMenuOpen_Esc_ClosesOnlyTheMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            OpenWaypointMenu(s);

            s.Window.DispatchKey(Key.Escape);

            Assert.Null(s.View.ActiveContextMenu);
            Assert.True(s.Scope.DrawRoute.IsDrawing);
            Assert.Equal(2, s.Scope.DrawRoute.Waypoints!.Count);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task WaypointMenuOpen_Enter_SendsTheRoute_AndClosesTheMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            OpenWaypointMenu(s);

            s.Window.DispatchKey(Key.Enter);

            Assert.Single(s.Sent);
            Assert.Null(s.View.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task WaypointMenuOpen_Backspace_UndoesAWaypoint_AndClosesTheMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            OpenWaypointMenu(s);

            s.Window.DispatchKey(Key.Back);

            Assert.Single(s.Scope.DrawRoute.Waypoints!);
            Assert.Null(s.View.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task WaypointMenuOpen_SelectionChange_CancelsTheRoute_AndClosesTheMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            AddAircraft(s.Main, "DAL2", 6000);
            OpenWaypointMenu(s);

            s.Scope.SelectAircraft("DAL2");

            Assert.False(s.Scope.DrawRoute.IsDrawing);
            Assert.Null(s.View.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task WaypointMenuOpen_ClearTrackState_CancelsTheRoute_AndClosesTheMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            OpenWaypointMenu(s);

            s.Scope.ClearTrackState();

            Assert.False(s.Scope.DrawRoute.IsDrawing);
            Assert.Null(s.View.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task WaypointMenuOpen_ANewRoute_LeavesNoStaleMenu()
    {
        Shown s = await ShowScopeAsync();
        try
        {
            OpenWaypointMenu(s);

            s.Scope.EnterDrawRoute(Callsign);

            Assert.Null(s.View.ActiveContextMenu);
            Assert.True(s.Scope.DrawRoute.IsDrawing);
        }
        finally
        {
            CloseWindow(s);
        }
    }
}
