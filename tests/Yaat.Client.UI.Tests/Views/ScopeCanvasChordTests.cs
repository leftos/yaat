using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// What a chord of two mouse buttons is on the Scope: a move, never a click. Avalonia's MouseDevice (12.1.0,
// ProcessRawEvent) sends a button-down whose PointerPointProperties report more than one pressed button to the move path
// instead of the press path, so a second button pressed while one is held reaches no press rung at all. The press ladder
// therefore reads PointerUpdateKind, the button the press went down with, as the measuring tool and the menu rungs do.
public class ScopeCanvasChordTests
{
    private const string Callsign = "UAL238";

    // An empty map point, away from the track at (300, 300).
    private static readonly Point MapPoint = new(120, 480);

    private static readonly Point TrackPoint = new(300, 300);

    [AvaloniaFact]
    public async Task ASecondButtonPressedWhileOneIsHeld_IsAMove_NotAClick()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, TrackPoint);
            List<string> clicks = [];
            view.Canvas.AircraftLeftClicked += callsign => clicks.Add("left:" + callsign);
            view.Canvas.AircraftRightClicked += (callsign, _) => clicks.Add("right:" + callsign);
            double centerLon = view.Canvas.Viewport.CenterLon;

            // The right button goes down on the empty map and stays down; the left press on the track carries the right
            // button's flag, as the pointer reports a held button.
            Press(window, view.Canvas, MapPoint, MouseButton.Right, RawInputModifiers.None);
            Press(window, view.Canvas, TrackPoint, MouseButton.Left, RawInputModifiers.RightMouseButton);

            Assert.Empty(clicks);
            Assert.Null(view.ActiveContextMenu);
            // The held right button's pan followed the press point, which is how a press on the move path shows.
            Assert.NotEqual(centerLon, view.Canvas.Viewport.CenterLon);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // A pen can deliver a left press while the right bit is set; the ladder reads the button the press went down with.
    [AvaloniaFact]
    public async Task LeftPress_WhileTheRightBitIsSet_SelectsTheTrack_NotTheMenu()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, TrackPoint);
            List<string> clicks = [];
            view.Canvas.AircraftLeftClicked += callsign => clicks.Add("left:" + callsign);
            view.Canvas.AircraftRightClicked += (callsign, _) => clicks.Add("right:" + callsign);
            var props = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.LeftButtonPressed);
            Point inWindow = view.Canvas.TranslatePoint(TrackPoint, window)!.Value;
            var e = new PointerPressedEventArgs(
                view.Canvas,
                new Pointer(Pointer.GetNextFreeId(), PointerType.Pen, true),
                window,
                inWindow,
                0,
                props,
                KeyModifiers.None
            );

            Assert.True(view.Canvas.RunPressLadderForTesting(new ScopePress(e, props, TrackPoint)));

            Assert.Equal(["left:" + Callsign], clicks);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeAsync()
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
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
        return (main, scope, view, window);
    }

    private static void AddAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, Point point)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)point.X, (float)point.Y);
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A press at <paramref name="canvasPoint"/> that goes down and stays down.</summary>
    private static void Press(Window window, ScopeCanvas canvas, Point canvasPoint, MouseButton button, RawInputModifiers held)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, button, held);
        Dispatcher.UIThread.RunJobs();
    }
}
