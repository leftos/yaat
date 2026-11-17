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
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The EuroScope elastic heading vector on the Scope: an AHDG press starts it from the press point and opens no rose, a release
/// without dragging leaves it armed, a drag past the threshold confirms at the release, the heading snaps and wraps through 000,
/// Esc and a right click cancel it, any left press while armed confirms toward its press point whatever is under it (another
/// aircraft, an Alt+left press, the armed measuring tool), the AHDG field starts nothing for an aircraft it may not command, and
/// starting either mode leaves the other.
/// </summary>
public class ScopeHeadingVectorGestureTests
{
    private const string Callsign = "UAL238";

    // An empty map point, far from the track at (300, 300).
    private static readonly Point MapPoint = new(520, 180);

    private sealed record Shown(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)
    {
        public ScopeCanvas Canvas => View.Canvas;

        public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];
    }

    private static async Task<Shown> ShowScopeAsync(Action<AircraftModel>? setUp)
    {
        MainViewModel main = await MainAsync();
        await main.CrcAliasLoad;
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null) { TemplateId = ScopeTemplates.EuroScopeId };
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
        (double lat, double lon) = view.Canvas.Viewport.ScreenToLatLon(300f, 300f);
        AircraftModel ac = AddAircraft(main, Callsign, 5000);
        ac.Position = new LatLon(lat, lon);
        ac.Destination = "KSFO";
        if (setUp is not null)
        {
            setUp(ac);
        }

        main.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();

        var shown = new Shown(main, scope, view, window, ac);
        scope.SendPilotCommand = (callsign, command, initials) =>
        {
            shown.Sent.Add((callsign, command, initials));
            return Task.CompletedTask;
        };
        return shown;
    }

    private static void CloseWindow(Shown s)
    {
        s.View.ActiveContextMenu?.Close();
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

    private static void Click(Shown s, Point canvasPoint, MouseButton button, RawInputModifiers modifiers)
    {
        Point at = InWindow(s, canvasPoint);
        s.Window.MouseMove(at, modifiers);
        s.Window.MouseDown(at, button, modifiers);
        s.Window.MouseUp(at, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    // Presses and releases at the point with no pointer move first, so the vector's cursor stays where it last was.
    private static void ClickInPlace(Shown s, Point canvasPoint, MouseButton button)
    {
        Point at = InWindow(s, canvasPoint);
        s.Window.MouseDown(at, button);
        s.Window.MouseUp(at, button);
        Dispatcher.UIThread.RunJobs();
    }

    // Holds the button at from, drags to to and releases there.
    private static void Drag(Shown s, Point from, Point to)
    {
        s.Window.MouseMove(InWindow(s, from));
        s.Window.MouseDown(InWindow(s, from), MouseButton.Left);
        s.Window.MouseMove(InWindow(s, to));
        s.Window.MouseUp(InWindow(s, to), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Shown s, Point canvasPoint, MouseButton button)
    {
        s.Window.MouseMove(InWindow(s, canvasPoint));
        s.Window.MouseDown(InWindow(s, canvasPoint), button);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Release(Shown s, Point canvasPoint, MouseButton button)
    {
        s.Window.MouseUp(InWindow(s, canvasPoint), button);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point AhdgField(Shown s)
    {
        ScopeTrack track = Assert.Single(s.Canvas.CurrentFrame.Tracks, t => ReferenceEquals(t.Aircraft, s.Aircraft));
        FieldRect field = track.Block.Fields.First(f => f.Slot == ScopeFieldSlot.AssignedHeading);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }

    private static Point SymbolOf(Shown s, AircraftModel ac)
    {
        ScopeTrack track = Assert.Single(s.Canvas.CurrentFrame.Tracks, t => ReferenceEquals(t.Aircraft, ac));
        return new Point(track.Symbol.X, track.Symbol.Y);
    }

    private static Point ScreenPoint(Shown s, LatLon at)
    {
        (float x, float y) = s.Canvas.Viewport.LatLonToScreen(at.Lat, at.Lon);
        return new Point(x, y);
    }

    private static LatLon MapLatLon(Shown s, Point canvasPoint)
    {
        (double lat, double lon) = s.Canvas.Viewport.ScreenToLatLon((float)canvasPoint.X, (float)canvasPoint.Y);
        return new LatLon(lat, lon);
    }

    // The command the aircraft's position and the map point produce, computed through the state's own maths.
    private static string ExpectedCommand(Shown s, Point canvasPoint) =>
        $"FH {ScopeHeadingVectorState.MagneticHeading(s.Aircraft.Position, MapLatLon(s, canvasPoint))}";

    private static void PickDrawRoute(Shown s)
    {
        ContextMenu menu = s.View.BuildAircraftMenu(s.Scope, s.Aircraft, null, Callsign);
        MenuItem item = Assert.Single(MenuItems(menu), m => m.Header is "Draw route");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static List<MenuItem> MenuItems(ItemsControl menu) =>
        [.. menu.Items.OfType<MenuItem>().SelectMany(m => (List<MenuItem>)[m, .. MenuItems(m)])];

    [AvaloniaFact]
    public async Task AhdgPress_StartsTheVector_AndOpensNoRose()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Press(s, AhdgField(s), MouseButton.Left);

            Assert.True(s.Canvas.IsHeadingVectorActive);
            Assert.Same(s.Aircraft, s.Main.SelectedAircraft);
            Assert.Null(s.View.ActiveContextMenu);
            Assert.Same(ScopeCanvas.HeadingVectorCursor, s.Canvas.Cursor);

            Release(s, AhdgField(s), MouseButton.Left);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task ReleaseWithoutMoving_StaysArmed_AndAMapClickConfirms()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);

            Assert.True(s.Canvas.IsHeadingVectorActive);
            Assert.Empty(s.Sent);

            Click(s, MapPoint, MouseButton.Left, RawInputModifiers.None);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            (string callsign, string command, string initials) = Assert.Single(s.Sent);
            Assert.Equal(Callsign, callsign);
            Assert.Equal(ExpectedCommand(s, MapPoint), command);
            Assert.Equal(s.Main.Preferences.UserInitials, initials);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task ArmedPress_ConfirmsTowardThePressPoint_NotTheLastMove()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);
            Point lastMove = new(700, 500);
            s.Window.MouseMove(InWindow(s, lastMove));
            Dispatcher.UIThread.RunJobs();
            Point pressPoint = new(420, 120);

            ClickInPlace(s, pressPoint, MouseButton.Left);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            string command = Assert.Single(s.Sent).Command;
            Assert.Equal(ExpectedCommand(s, pressPoint), command);
            Assert.NotEqual(ExpectedCommand(s, lastMove), command);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task DragPastTheThreshold_AndRelease_ConfirmsAtTheRelease()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Drag(s, AhdgField(s), MapPoint);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            (string callsign, string command, _) = Assert.Single(s.Sent);
            Assert.Equal(Callsign, callsign);
            Assert.Equal(ExpectedCommand(s, MapPoint), command);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task DragToAHeadingJustWestOfNorth_ConfirmsAs360()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            // Aim along the true bearing whose magnetic heading is 359: it snaps to 360, never to 000.
            TrueHeading along = new(MagneticDeclination.MagneticToTrue(359, s.Aircraft.Position));
            LatLon target = GeoMath.ProjectPoint(s.Aircraft.Position, along, 5);

            Drag(s, AhdgField(s), ScreenPoint(s, target));

            Assert.Equal("FH 360", Assert.Single(s.Sent).Command);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task Escape_WhileArmed_Cancels_AndSendsNothing()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);
            Assert.True(s.Canvas.IsHeadingVectorActive);

            s.Window.DispatchKey(Key.Escape);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Empty(s.Sent);
            Assert.Null(s.Canvas.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task RightClick_WhileArmed_Cancels_AndSendsNothing()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);

            Click(s, MapPoint, MouseButton.Right, RawInputModifiers.None);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Empty(s.Sent);
            Assert.Null(s.View.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task PressOnAnotherAircraft_WhileArmed_ConfirmsTowardIt_AndSelectsNothing()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            AircraftModel other = AddAircraft(s.Main, "DAL2", 6000);
            other.Position = MapLatLon(s, new Point(620, 440));
            Dispatcher.UIThread.RunJobs();

            Point field = AhdgField(s);
            Drag(s, field, field);
            Point at = SymbolOf(s, other);

            Click(s, at, MouseButton.Left, RawInputModifiers.None);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            string command = Assert.Single(s.Sent).Command;
            Assert.Equal(ExpectedCommand(s, at), command);
            Assert.Same(s.Aircraft, s.Main.SelectedAircraft);
            Assert.Null(s.Canvas.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task AltLeftClick_WhileArmed_Confirms_AndPlacesNoMeasurePoint()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);

            Click(s, MapPoint, MouseButton.Left, RawInputModifiers.Alt);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Null(s.Main.Measure.Anchor);
            Assert.Empty(s.Main.Measure.Lines);
            Assert.Equal(ExpectedCommand(s, MapPoint), Assert.Single(s.Sent).Command);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task ArmedMeasuringTool_LeftPress_ConfirmsAndPicksNoPoint()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);
            s.Main.Measure.Arm();
            Dispatcher.UIThread.RunJobs();
            Assert.True(s.Canvas.IsMeasuring);

            Click(s, MapPoint, MouseButton.Left, RawInputModifiers.None);

            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Null(s.Main.Measure.Anchor);
            Assert.Empty(s.Main.Measure.Lines);
            Assert.Equal(ExpectedCommand(s, MapPoint), Assert.Single(s.Sent).Command);
            Assert.True(s.Canvas.IsMeasuring);
            Assert.Null(s.Canvas.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task DetachingTheView_TakesAnArmedVectorDown()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);
            Assert.True(s.Canvas.IsHeadingVectorActive);

            s.View.DataContext = null;
            Dispatcher.UIThread.RunJobs();

            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Null(s.Canvas.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task AhdgPress_OnALiveTrafficShadow_StartsNoVector()
    {
        Shown s = await ShowScopeAsync(ac =>
        {
            // Airborne phase on the ground: the flight-commands guard lets it through, so only the shadow guard applies.
            ac.CurrentPhase = "Approach";
            ac.IsLiveTraffic = true;
            ac.IsOnGround = true;
        });
        try
        {
            s.View.OnFieldClicked(Callsign, ScopeFieldAction.StartHeadingVector, new Point(300, 300), KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Null(s.Canvas.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task AhdgPress_WithFlightCommandsHidden_StartsNoVector()
    {
        Shown s = await ShowScopeAsync(ac => ac.CurrentPhase = "Landing");
        try
        {
            s.View.OnFieldClicked(Callsign, ScopeFieldAction.StartHeadingVector, new Point(300, 300), KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Null(s.Canvas.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task FlyHeadingAsync_SendsTheHeadingThroughThePilotCommandSeam()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            await s.Scope.FlyHeadingAsync(Callsign, 270);

            (string callsign, string command, string initials) = Assert.Single(s.Sent);
            Assert.Equal(Callsign, callsign);
            Assert.Equal("FH 270", command);
            Assert.Equal(s.Main.Preferences.UserInitials, initials);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task Frame_CarriesTheVector_ToThePointer()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);
            s.Window.MouseMove(InWindow(s, MapPoint));
            Dispatcher.UIThread.RunJobs();

            ScopeHeadingVectorOverlay overlay = Assert.IsType<ScopeHeadingVectorOverlay>(s.Canvas.RenderSnapshotForTesting().Overlays?.HeadingVector);

            Assert.Equal(s.Aircraft.Position, overlay.Aircraft);
            Assert.Equal(s.Aircraft.Heading.Degrees, overlay.HeadingTrue);
            Assert.Equal(s.Aircraft.GroundSpeed, overlay.GroundSpeed);
            Assert.Equal(MapLatLon(s, MapPoint), overlay.Cursor);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task StartingTheHeadingVector_CancelsTheRouteBeingDrawn()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            s.Scope.EnterDrawRoute(Callsign);
            Assert.True(s.Scope.DrawRoute.IsDrawing);

            s.View.OnFieldClicked(Callsign, ScopeFieldAction.StartHeadingVector, new Point(300, 300), KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(s.Scope.DrawRoute.IsDrawing);
            Assert.True(s.Canvas.IsHeadingVectorActive);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task StartingTheDrawRoute_CancelsTheArmedHeadingVector()
    {
        Shown s = await ShowScopeAsync(null);
        try
        {
            Point field = AhdgField(s);
            Drag(s, field, field);
            Assert.True(s.Canvas.IsHeadingVectorActive);

            PickDrawRoute(s);

            Assert.True(s.Scope.DrawRoute.IsDrawing);
            Assert.False(s.Canvas.IsHeadingVectorActive);
            Assert.Empty(s.Sent);
        }
        finally
        {
            CloseWindow(s);
        }
    }
}
