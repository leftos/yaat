using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A measure anchor belongs to the surface that set it, on the Ground view too: the aircraft row offers to complete only an
/// anchor the Ground view picked, and Esc and a right press on the ground canvas cancel only that anchor (or the armed tool
/// with no anchor), leaving an anchor a Scope picked.
/// </summary>
public class GroundMeasureOwnershipTests
{
    private const double FieldLat = 37.62;
    private const double FieldLon = -122.39;
    private const string Callsign = "UAL100";

    private static readonly RblEndpoint Anchor = RblEndpoint.AtPoint(new LatLon(37.70, -122.20), "");

    // --- Aircraft menu's measure row ------------------------------------------------------------------------------------

    // A Scope's anchor pending: the ground aircraft row starts a measurement from the aircraft rather than completing it.
    [AvaloniaFact]
    public void GroundAircraftRow_WithAScopeAnchor_ReadsMeasureFrom()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = GroundAircraft();
        main.Aircraft.Add(ac);
        main.Measure.Pick(Anchor, new MeasureTarget(RblView.Scope, main.Ground.MeasureTrackLookup, RblUnits.NauticalMiles, new object()));

        List<string> display = DisplayHeaders(MenuHostHarness.BuildGroundMenu(main, ac, null));

        Assert.Contains($"Measure from {Callsign}", display);
        Assert.DoesNotContain($"Measure to {Callsign}", display);
    }

    // The Ground view's own anchor pending: the ground aircraft row completes it.
    [AvaloniaFact]
    public void GroundAircraftRow_WithAGroundAnchor_ReadsMeasureTo()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = GroundAircraft();
        main.Aircraft.Add(ac);
        main.Measure.Pick(Anchor, main.MeasureSurface(RblView.Ground, null));

        List<string> display = DisplayHeaders(MenuHostHarness.BuildGroundMenu(main, ac, null));

        Assert.Contains($"Measure to {Callsign}", display);
        Assert.DoesNotContain($"Measure from {Callsign}", display);
    }

    // --- Esc ------------------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundEsc_WithAScopeAnchor_LeavesIt_AndPassesTheKeyOn()
    {
        (GroundView view, MainViewModel main, Window window) = ShowGround();
        try
        {
            object scope = new();
            main.Measure.Pick(Anchor, new MeasureTarget(RblView.Scope, main.Ground.MeasureTrackLookup, RblUnits.NauticalMiles, scope));

            KeyEventArgs esc = PressEscape(view);

            Assert.False(esc.Handled);
            Assert.True(main.Measure.IsMeasuring);
            Assert.Equal(new RblPendingAnchor(Anchor, RblView.Scope, scope), main.Measure.Anchor);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public async Task GroundEsc_WithAGroundAnchor_CancelsIt()
    {
        (GroundView view, MainViewModel main, Window window) = ShowGround();
        try
        {
            // The startup nav-data load writes StatusText when it finishes; let it land before the Esc this test reads.
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            main.Measure.Pick(Anchor, main.MeasureSurface(RblView.Ground, null));

            KeyEventArgs esc = PressEscape(view);

            Assert.True(esc.Handled);
            Assert.False(main.Measure.IsMeasuring);
            Assert.Null(main.Measure.Anchor);
            Assert.Equal("Measure cancelled", main.StatusText);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void GroundEsc_WithTheToolArmedAndNoAnchor_CancelsIt()
    {
        (GroundView view, MainViewModel main, Window window) = ShowGround();
        try
        {
            main.Measure.Arm();

            KeyEventArgs esc = PressEscape(view);

            Assert.True(esc.Handled);
            Assert.False(main.Measure.IsMeasuring);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // --- Right press on the ground canvas -------------------------------------------------------------------------------

    // A Scope's anchor pending: the right press is not the tool's; it cancels nothing and opens the ground map menu.
    [AvaloniaFact]
    public void GroundRightClick_WithAScopeAnchor_CancelsNothing_AndOpensTheMapMenu()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            canvas.IsMeasuring = true;
            canvas.MeasureAnchor = new RblPendingAnchor(Anchor, RblView.Scope, new object());
            int cancels = 0;
            var mapMenus = new List<Point>();
            canvas.MeasureCancelled += () => cancels++;
            canvas.MapRightClicked += mapMenus.Add;
            var at = new Point(400, 300);

            RightClick(window, canvas, at);

            Assert.Equal(0, cancels);
            Assert.Equal([at], mapMenus);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // The Ground view's own anchor pending: the right press cancels the tool and opens no menu.
    [AvaloniaFact]
    public void GroundRightClick_WithAGroundAnchor_CancelsIt_NoMenu()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            canvas.IsMeasuring = true;
            canvas.MeasureAnchor = new RblPendingAnchor(Anchor, RblView.Ground, null);
            int cancels = 0;
            var mapMenus = new List<Point>();
            canvas.MeasureCancelled += () => cancels++;
            canvas.MapRightClicked += mapMenus.Add;

            RightClick(window, canvas, new Point(400, 300));

            Assert.Equal(1, cancels);
            Assert.Empty(mapMenus);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // The tool armed with no anchor: the right press cancels it and opens no menu.
    [AvaloniaFact]
    public void GroundRightClick_ArmedWithNoAnchor_CancelsIt_NoMenu()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            canvas.IsMeasuring = true;
            int cancels = 0;
            var mapMenus = new List<Point>();
            canvas.MeasureCancelled += () => cancels++;
            canvas.MapRightClicked += mapMenus.Add;

            RightClick(window, canvas, new Point(400, 300));

            Assert.Equal(1, cancels);
            Assert.Empty(mapMenus);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // --- Fixtures -------------------------------------------------------------------------------------------------------

    private static AircraftModel GroundAircraft() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            IsOnGround = true,
            FlightRules = "IFR",
            CurrentPhase = "Taxiing",
            Position = new LatLon(FieldLat, FieldLon),
        };

    // The headers of the Display submenu in the aircraft menu's All Commands, where the measure row lives.
    private static List<string> DisplayHeaders(ContextMenu menu)
    {
        MenuItem all = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        MenuItem display = Assert.Single(all.Items.OfType<MenuItem>(), m => (m.Header as string) == "Display");
        return [.. display.Items.OfType<MenuItem>().Select(m => m.Header as string ?? "")];
    }

    // A shown ground view over the main view model's primary ground view model, parented to a host carrying the main view model.
    private static (GroundView View, MainViewModel Main, Window Window) ShowGround()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new GroundView { DataContext = main.Ground };
        var host = new Grid { DataContext = main };
        host.Children.Add(view);
        var window = new Window { Content = host };
        window.ShowAndRunLayout();
        return (view, main, window);
    }

    private static void CloseWindow(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static KeyEventArgs PressEscape(GroundView view)
    {
        var esc = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        view.RaiseEvent(esc);
        Dispatcher.UIThread.RunJobs();
        return esc;
    }

    // A shown ground canvas over a runways-only layout with no runways: a right press hits nothing and asks for the map menu.
    private static (GroundCanvas Canvas, Window Window) ShowCanvas()
    {
        var canvas = new GroundCanvas();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        canvas.AirportCenterLat = FieldLat;
        canvas.AirportCenterLon = FieldLon;
        canvas.AirportElevation = 0;
        canvas.Layout = new GroundLayoutDto("OAK", [], [], null, [], null, IsRunwaysOnly: true);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (canvas, window);
    }

    private static void RightClick(Window window, GroundCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Right);
        window.MouseUp(inWindow.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }
}
