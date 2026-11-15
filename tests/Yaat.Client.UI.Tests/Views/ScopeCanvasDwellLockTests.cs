using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The scope's ERAM dwell lock and VCI preview, as CRC's: a left click on an ERAM block's Field A asks to toggle the lock and
// still selects; any other click only selects, and a STARS block asks nothing. Hovering an ERAM FDB's VCI cell lights the VCI
// without dwelling the block, and moving off releases it. Modelled on ScopeCanvasClickTests and ScopeCanvasHoverTests.
public class ScopeCanvasDwellLockTests
{
    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    [AvaloniaFact]
    public void ScopeCanvas_LeftClickOnAnEramCallsign_AsksForTheToggle_AndSelects()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            var toggled = new List<string>();
            view.Canvas.AircraftDwellLockToggleRequested += toggled.Add;

            Click(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.Callsign));

            Assert.Equal(["UAL238"], toggled);
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Same(ac, scope.SelectedAircraft);
            // The scope forwarded the request; with no viewer ERAM sector the view model refuses it.
            Assert.Equal(ScopeViewModel.NoEramSectorStatus, main.StatusText);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_LeftClickOnALowerCallsignInsideTheTopBlock_SelectsTheTopAircraft_AndAsksNoToggle()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            IReadOnlyList<FieldRect> lower = Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields;
            float lineHeight =
                lower.First(f => f.Slot == ScopeFieldSlot.EramAltitude).Rect.Top - lower.First(f => f.Slot == ScopeFieldSlot.Callsign).Rect.Top;
            // One line up: the top block's line 2 lies over the lower block's callsign, and is shorter than it.
            AircraftModel top = AddAircraft(main, view.Canvas, "DAL5501", 300, 300 - lineHeight);
            ScopeFrame frame = view.Canvas.CurrentFrame;
            ScopeTrack topTrack = frame.Tracks[^1];
            Assert.Same(top, topTrack.Aircraft);
            Point gap = GapOverLowerCallsign(frame, topTrack, "UAL238");
            var toggled = new List<string>();
            view.Canvas.AircraftDwellLockToggleRequested += toggled.Add;

            Click(window, view.Canvas, gap);

            Assert.Empty(toggled);
            Assert.Same(top, main.SelectedAircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // A point on the lower block's callsign that lies inside the top block's bounds but on none of its fields.
    private static Point GapOverLowerCallsign(ScopeFrame frame, ScopeTrack topTrack, string lowerCallsign)
    {
        FieldRect callsign = frame.Tracks.First(t => t.Aircraft.Callsign == lowerCallsign).Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign);
        float y = callsign.Rect.MidY;
        for (float x = callsign.Rect.Right - 0.5f; x > callsign.Rect.Left; x -= 0.5f)
        {
            bool inTop = topTrack.Block.Bounds.Contains(x, y) && !topTrack.Block.Fields.Any(f => f.Rect.Contains(x, y));
            if (inTop && (frame.FieldAt(x, y) is { Field.Slot: ScopeFieldSlot.Callsign } hit) && (hit.Aircraft.Callsign == lowerCallsign))
            {
                Assert.Same(topTrack.Aircraft, frame.BlockAt(x, y));
                return new Point(x, y);
            }
        }
        Assert.Fail("No gap in the top block lies over the lower block's callsign.");
        return default;
    }

    [AvaloniaFact]
    public void ScopeCanvas_LeftClickElsewhereOnAnEramBlock_OnlySelects()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            var toggled = new List<string>();
            view.Canvas.AircraftDwellLockToggleRequested += toggled.Add;

            Click(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.EramAltitude));

            Assert.Empty(toggled);
            Assert.Same(ac, main.SelectedAircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_LeftClickOnAStarsCallsign_AsksNoToggle()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope(ScopeTemplates.StarsId);
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            var toggled = new List<string>();
            view.Canvas.AircraftDwellLockToggleRequested += toggled.Add;

            Click(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.Callsign));

            Assert.Empty(toggled);
            Assert.Same(ac, main.SelectedAircraft);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_HoverOnTheVciCell_LightsTheVci_AndMovingOffReleasesIt()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
            Dispatcher.UIThread.RunJobs();
            AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            ScopeTrack track = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            SKRect cell = Assert.NotNull(track.Block.VciCell);
            var onCell = new Point(cell.Left - 4f, cell.Top - 2f);
            Assert.True(SKPoint.Distance(track.Symbol, new SKPoint((float)onCell.X, (float)onCell.Y)) > ScopeFrame.DwellSymbolRadiusPx);
            Assert.DoesNotContain(track.Block.Fields, f => f.Slot == ScopeFieldSlot.Vci);

            Move(window, view.Canvas, onCell);

            Assert.Equal("UAL238", view.Canvas.HoveredVciCallsign);
            Assert.Null(view.Canvas.HoveredCallsign);
            ScopeTrack previewed = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.Equal(ScopeBlockLevel.Full, previewed.Level);
            Assert.Contains(previewed.Block.Fields, f => f.Slot == ScopeFieldSlot.Vci);

            Move(window, view.Canvas, new Point(600, 500));

            Assert.Null(view.Canvas.HoveredVciCallsign);
            Assert.DoesNotContain(Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields, f => f.Slot == ScopeFieldSlot.Vci);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window) ShowScope(string templateId)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main) { TemplateId = templateId };
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
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

    // An airborne aircraft whose symbol sits at the given canvas point, added to the traffic the scope draws.
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

    private static Point Centre(ScopeCanvas canvas, ScopeFieldSlot slot)
    {
        FieldRect field = Assert.Single(canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == slot);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }

    private static void Click(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // Moves the pointer to a canvas point and lets any repaint it asked for run, as the canvas's own would.
    private static void Move(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseMove(inWindow.Value);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
