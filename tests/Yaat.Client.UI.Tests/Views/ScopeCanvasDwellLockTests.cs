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
using Yaat.Client.Views.Scope.Profiles;
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
            // The scope forwarded the request; the view model holds the block locally, with no viewer ERAM sector.
            Assert.Contains("UAL238", scope.DataBlockState.DwellLockedCallsigns);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_SecondClickOnAnEramCallsign_ReleasesTheLocalLock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            Point callsign = Centre(view.Canvas, ScopeFieldSlot.Callsign);

            Click(window, view.Canvas, callsign);

            Assert.Null(view.Canvas.HoveredCallsign);
            Assert.Equal(ScopeBlockLevel.Dwell, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);

            Click(window, view.Canvas, callsign);

            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
            Assert.Same(ac, main.SelectedAircraft);
            // The aircraft was already selected, so only the click itself can have dropped the frame that drew the lock.
            Assert.Null(view.Canvas.HoveredCallsign);
            Assert.Equal(ScopeBlockLevel.Full, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_LocallyLockedTrack_IsAtDwellInTheNextFrame()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            Assert.Equal(ScopeBlockLevel.Full, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);

            Click(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.Callsign));
            Move(window, view.Canvas, new Point(600, 500));

            Assert.Null(view.Canvas.HoveredCallsign);
            Assert.Equal(ScopeBlockLevel.Dwell, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);
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

    /// <summary>
    /// A gap of the top block over a lower block's Altitude field: the lower field's menu does not open; the top aircraft is selected.
    /// </summary>
    [AvaloniaFact]
    public void ScopeCanvas_LeftClickOnALowerAltitudeInsideTheTopBlock_SelectsTheTopAircraft_AndOpensNoMenu()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope(ScopeTemplates.StarsId);
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            IReadOnlyList<FieldRect> lower = Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields;
            FieldRect altitude = lower.First(f => f.Slot == ScopeFieldSlot.Altitude);
            float lineHeight = altitude.Rect.Top - lower.First(f => f.Slot == ScopeFieldSlot.Callsign).Rect.Top;
            float cell = altitude.Rect.Width / 3;
            // One line down and seven cells left: the top block's callsign line, shorter than its line 2, ends left of the lower
            // block's altitude, which lies in the top block's bounds right of that callsign.
            AircraftModel top = AddAircraft(main, view.Canvas, "DAL5501", 300 - (7 * cell), 300 + lineHeight);
            ScopeFrame frame = view.Canvas.CurrentFrame;
            ScopeTrack topTrack = frame.Tracks[^1];
            Assert.Same(top, topTrack.Aircraft);
            Point gap = GapOverLowerField(frame, topTrack, "UAL238", ScopeFieldSlot.Altitude);
            Assert.Equal(ScopeFieldAction.OpenAltitudeMenu, frame.FieldAt((float)gap.X, (float)gap.Y)?.Field.Action);

            Click(window, view.Canvas, gap);

            Assert.Null(view.ActiveContextMenu);
            Assert.Same(top, main.SelectedAircraft);
        }
        finally
        {
            view.ActiveContextMenu?.Close();
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // A point on the lower block's slot field that lies inside the top block's bounds but on none of its fields.
    private static Point GapOverLowerField(ScopeFrame frame, ScopeTrack topTrack, string lowerCallsign, ScopeFieldSlot slot)
    {
        FieldRect field = frame.Tracks.First(t => t.Aircraft.Callsign == lowerCallsign).Block.Fields.First(f => f.Slot == slot);
        for (float y = field.Rect.Top + 0.5f; y < field.Rect.Bottom; y += 0.5f)
        {
            for (float x = field.Rect.Left + 0.5f; x < field.Rect.Right; x += 0.5f)
            {
                bool inTop = topTrack.Block.Bounds.Contains(x, y) && !topTrack.Block.Fields.Any(f => f.Rect.Contains(x, y));
                if (inTop && (frame.FieldAt(x, y) is { } hit) && (hit.Aircraft.Callsign == lowerCallsign) && (hit.Field.Slot == slot))
                {
                    return new Point(x, y);
                }
            }
        }
        Assert.Fail($"No gap in the top block lies over the lower block's {slot}.");
        return default;
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

            // A drawn field other than Field A whose click opens no menu, so the click only selects.
            FieldRect inert = Assert
                .Single(view.Canvas.CurrentFrame.Tracks)
                .Block.Fields.First(f => (f.Action == ScopeFieldAction.None) && (f.Slot != ScopeFieldSlot.Callsign));
            Click(window, view.Canvas, new Point(inert.Rect.MidX, inert.Rect.MidY));

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
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
            // The track is not the viewer's, so the ERAM band would draw it as an LDB, which has no VCI cell: pin others Full.
            scope.PinOthersLevel(ScopeProfile.OthersFull);
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

    [AvaloniaFact]
    public void ScopeCanvas_HoverOnAQueueBadge_ShowsItsLines_WithoutDwelling_AndMovingOffReleasesIt()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.StarsId);
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            ac.PendingCommands = "[Active] FH 270\n[1] AT BRIXX DM 040";
            scope.SetLensEnabled(true);
            scope.SetLensOverlay("queue", true);
            Dispatcher.UIThread.RunJobs();
            SKRect badge = Assert.IsType<ScopeQueueBadgeView>(Assert.Single(view.Canvas.CurrentFrame.Tracks).QueueBadge).Rect;

            Move(window, view.Canvas, new Point(badge.MidX, badge.MidY));

            Assert.Equal("UAL238", view.Canvas.HoveredQueueCallsign);
            Assert.Null(view.Canvas.HoveredCallsign);
            ScopeTrack hovered = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.False(hovered.IsDwelled);
            Assert.True(hovered.QueueBadge?.Hovered);

            Move(window, view.Canvas, new Point(600, 500));

            Assert.Null(view.Canvas.HoveredQueueCallsign);
            Assert.False(Assert.Single(view.Canvas.CurrentFrame.Tracks).QueueBadge?.Hovered);
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
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null) { TemplateId = templateId };
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
