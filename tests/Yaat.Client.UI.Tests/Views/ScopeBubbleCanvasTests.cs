using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The Scope's speech bubbles: which ground aircraft a bubble surfaces, the track order a bubbled aircraft takes, and the
// click that dismisses one without clearing the selection. Modelled on ScopeCanvasClickTests.
public class ScopeBubbleCanvasTests
{
    [AvaloniaFact]
    public void Press_ThenReleaseOnTheBubble_DismissesIt_AndKeepsTheSelection()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            AircraftModel other = AddAircraft(main, view.Canvas, "SWA1", 600, 200);
            ac.SpeechBubble = Bubble("CLEARED FOR TAKEOFF RUNWAY 30");
            main.SelectedAircraft = other;
            Rebuild(view);

            ScopeBubble bubble = Assert.Single(view.Canvas.CurrentBubbles);
            Click(window, view.Canvas, new Point(bubble.Rect.MidX, bubble.Rect.MidY));

            Assert.Null(ac.SpeechBubble);
            Assert.Same(other, main.SelectedAircraft);
            Assert.Same(other, scope.SelectedAircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Press_ThenDragPastFivePixels_KeepsTheBubble()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            ac.SpeechBubble = Bubble("CLEARED FOR TAKEOFF RUNWAY 30");
            Rebuild(view);

            ScopeBubble bubble = Assert.Single(view.Canvas.CurrentBubbles);
            Point? press = view.Canvas.TranslatePoint(new Point(bubble.Rect.MidX, bubble.Rect.MidY), window);
            Assert.NotNull(press);
            var release = new Point(press.Value.X + 12, press.Value.Y);
            window.MouseDown(press.Value, MouseButton.Left);
            window.MouseMove(release, RawInputModifiers.LeftMouseButton);
            window.MouseUp(release, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(ac.SpeechBubble);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Press_OnTheEmptyMapWithNoBubble_ClearsTheSelection()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            main.SelectedAircraft = ac;
            Assert.Empty(view.Canvas.CurrentBubbles);

            Click(window, view.Canvas, new Point(100, 500));

            Assert.Null(main.SelectedAircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void GroundAircraft_WithABubble_IsSurfaced_WhenNoGroundViewShowsItsAirport()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddGroundAircraft(main, view.Canvas, "UAL238", 300, 300);
            ac.SpeechBubble = Bubble("TAXI TO RUNWAY 30");
            view.Canvas.GroundShownAirportIdProvider = () => null;
            Rebuild(view);

            Assert.Same(ac, Assert.Single(view.Canvas.CurrentFrame.Tracks).Aircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void GroundAircraft_WithABubble_IsHidden_WhenTheGroundViewShowsItsAirport()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddGroundAircraft(main, view.Canvas, "UAL238", 300, 300);
            ac.SpeechBubble = Bubble("TAXI TO RUNWAY 30");
            view.Canvas.GroundShownAirportIdProvider = () => "OAK";
            Rebuild(view);

            Assert.Empty(view.Canvas.CurrentFrame.Tracks);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void GroundAircraft_WithABubble_IsSurfaced_WhenAlwaysShowGroundBubblesIsOn()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddGroundAircraft(main, view.Canvas, "UAL238", 300, 300);
            ac.SpeechBubble = Bubble("TAXI TO RUNWAY 30");
            view.Canvas.GroundShownAirportIdProvider = () => "OAK";
            main.Preferences.SetAlwaysShowGroundBubblesOnRadar(true);
            Rebuild(view);

            Assert.Same(ac, Assert.Single(view.Canvas.CurrentFrame.Tracks).Aircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void BubbledTrack_DrawsAfterEveryPlainTrack()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel plain = AddAircraft(main, view.Canvas, "SWA1", 600, 200);
            AircraftModel bubbled = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            bubbled.SpeechBubble = Bubble("CLEARED FOR TAKEOFF RUNWAY 30");
            Rebuild(view);

            IReadOnlyList<ScopeTrack> tracks = view.Canvas.CurrentFrame.Tracks;

            Assert.Equal(2, tracks.Count);
            Assert.Same(plain, tracks[0].Aircraft);
            Assert.Same(bubbled, tracks[1].Aircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void StayUntilClickedBubble_SurvivesTheFrame()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            ac.SpeechBubble = new AircraftSpeechBubble("SAY AGAIN", DateTime.MaxValue, SpeechBubbleSeverity.Speech);
            Rebuild(view);

            Assert.Single(view.Canvas.CurrentBubbles);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static AircraftSpeechBubble Bubble(string text) => new(text, DateTime.UtcNow.AddMinutes(1), SpeechBubbleSeverity.Speech);

    private static (MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window) ShowScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
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

        main.Preferences.SetShowSpeechBubbles(true);
        Rebuild(view);
        return (main, scope, view, window);
    }

    // The next drawn frame rebuilds the kept one, as the canvas's own repaint would.
    private static void Rebuild(ScopeView view)
    {
        view.Canvas.MarkDirty();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static AircraftModel AddAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
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
        return ac;
    }

    private static AircraftModel AddGroundAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 0,
            GroundSpeed = 10,
            IsOnGround = true,
            GroundAirportId = "OAK",
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static void Click(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
