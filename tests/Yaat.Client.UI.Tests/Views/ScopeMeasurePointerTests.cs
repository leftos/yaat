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
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The measuring tool's pointer gestures on the Scope canvas, first match wins: Alt+left-drag measures whether or not the tool
/// is armed (a release within the drag threshold anchors and leaves the tool armed); a left press while the tool is armed
/// picks an endpoint before the Rings tool, the fill, the field menus and the dwell lock; a right press while it is armed
/// cancels it and opens no menu; with the tool not armed the presses act as before. A move records the cursor the half-placed
/// line runs to.
/// </summary>
public class ScopeMeasurePointerTests
{
    private const string AnchoredStatus = "Measure: click the second point or aircraft (Esc to cancel)";

    // Empty map points, far from any track the tests draw at (300, 300).
    private static readonly Point MapA = new(100, 500);
    private static readonly Point MapB = new(600, 450);

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        await main.CrcAliasLoad;
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

    private static void CloseWindow(Window window, ScopeView view)
    {
        view.ActiveContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // A B738 at 5000 ft drawn at (300, 300) on the canvas.
    private static AircraftModel AddAt300(MainViewModel main, ScopeCanvas canvas, string callsign)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon(300f, 300f);
        AircraftModel ac = AddAircraft(main, callsign, 5000);
        ac.Position = new LatLon(lat, lon);
        main.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static Point SymbolOf(ScopeCanvas canvas, AircraftModel ac)
    {
        ScopeTrack track = Assert.Single(canvas.CurrentFrame.Tracks, t => ReferenceEquals(t.Aircraft, ac));
        return new Point(track.Symbol.X, track.Symbol.Y);
    }

    private static Point InWindow(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    private static void Click(Window window, ScopeCanvas canvas, Point canvasPoint, MouseButton button)
    {
        Point at = InWindow(window, canvas, canvasPoint);
        window.MouseDown(at, button);
        window.MouseUp(at, button);
        Dispatcher.UIThread.RunJobs();
    }

    // Alt held from the press, through the moves, to the release.
    private static void AltDrag(Window window, ScopeCanvas canvas, Point from, Point to)
    {
        Point start = InWindow(window, canvas, from);
        Point end = InWindow(window, canvas, to);
        window.MouseMove(start, RawInputModifiers.Alt);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
    }

    private static LatLon LatLonAt(ScopeCanvas canvas, Point canvasPoint)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)canvasPoint.X, (float)canvasPoint.Y);
        return new LatLon(lat, lon);
    }

    private static void ArmTools(ScopeViewModel scope)
    {
        scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsJRing };
        scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
        Assert.True(scope.IsRingToolArmed);
    }

    // Ladder row 1, tool not armed: Alt+left-drag from an aircraft to the map places one Scope line, latched to the aircraft.
    [AvaloniaFact]
    public async Task AltDrag_PlacesAScopeLine_NotARadarLine()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");
            main.SetActiveMeasureView(RblView.Radar);

            AltDrag(window, view.Canvas, SymbolOf(view.Canvas, ac), MapB);

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal(RblView.Scope, line.View);
            Assert.Equal(1, line.Slot);
            Assert.Equal(RblEndpoint.OnAircraft("UAL1"), line.A);
            Assert.Equal(RblEndpoint.AtPoint(LatLonAt(view.Canvas, MapB), ""), line.B);
            Assert.False(main.Measure.IsMeasuring);
            Assert.Null(main.Measure.Anchor);
            Assert.Equal(RblView.Scope, main.ActiveMeasureView);
            Assert.Null(main.SelectedAircraft);
            Assert.Equal(1, Assert.Single(view.Canvas.RenderSnapshotForTesting().RangeBearingLines).Slot);
            Assert.Empty(RangeBearingLineResolver.Resolve(main.Measure.Lines, _ => null, RblUnits.NauticalMiles, RblView.Radar, null));
            // The reading the line itself is labelled with, resolved against the Scope's traffic.
            ResolvedRbl resolved = Assert.Single(
                RangeBearingLineResolver.Resolve(main.Measure.Lines, scope.MeasureTrackLookup, RblUnits.NauticalMiles, RblView.Scope, scope)
            );
            string expected = $"Measurement {resolved.Label}";
            Assert.StartsWith("1", resolved.Label, StringComparison.Ordinal);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Text, expected, "", []), scope.EntryResponse);
            Assert.Equal(expected, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 1, tool armed: the Alt-drag still places the whole line at once, and placing it disarms the tool, as the
    // shared store's Add does for every whole-line placement (the Radar's Alt-drag too).
    [AvaloniaFact]
    public async Task AltDrag_WhileTheToolIsArmed_PlacesTheLine()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.Measure.Arm();

            AltDrag(window, view.Canvas, MapA, MapB);

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal(RblView.Scope, line.View);
            Assert.Equal(RblEndpoint.AtPoint(LatLonAt(view.Canvas, MapA), ""), line.A);
            Assert.Equal(RblEndpoint.AtPoint(LatLonAt(view.Canvas, MapB), ""), line.B);
            Assert.False(main.Measure.IsMeasuring);
            Assert.Null(main.Measure.Anchor);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Release row: an Alt-click released within the drag threshold anchors on the Scope and leaves the tool armed; a plain
    // click completes the line.
    [AvaloniaFact]
    public async Task AltClick_AnchorsAndLeavesToolArmed_ThenPlainClickCompletes()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            Point at = InWindow(window, view.Canvas, MapA);
            window.MouseDown(at, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseUp(at, MouseButton.Left, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();

            Assert.True(main.Measure.IsMeasuring);
            Assert.Equal(new RblPendingAnchor(RblEndpoint.AtPoint(LatLonAt(view.Canvas, MapA), ""), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Empty(main.Measure.Lines);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Text, AnchoredStatus, "", []), scope.EntryResponse);

            Click(window, view.Canvas, MapB, MouseButton.Left);

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal(RblView.Scope, line.View);
            Assert.False(main.Measure.IsMeasuring);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 2, with the Rings tool armed too: the armed measure tool wins the left press, latching the aircraft; nothing
    // is selected and no ring is placed.
    [AvaloniaFact]
    public async Task ArmedLeftClick_PicksEndpoints_DoesNotSelectOrPlaceRing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");
            ArmTools(scope);
            main.Measure.Arm();

            Click(window, view.Canvas, SymbolOf(view.Canvas, ac), MouseButton.Left);

            Assert.Equal(new RblPendingAnchor(RblEndpoint.OnAircraft("UAL1"), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Null(main.SelectedAircraft);
            Assert.False(scope.LocalRings.ContainsKey("UAL1"));
            Assert.True(scope.IsRingToolArmed);

            Click(window, view.Canvas, MapB, MouseButton.Left);

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal(RblEndpoint.OnAircraft("UAL1"), line.A);
            Assert.Equal(RblView.Scope, line.View);
            Assert.Null(main.SelectedAircraft);
            Assert.Empty(scope.LocalRings);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 2, with text in the preview: the armed tool picks; the track is not filled into the entry.
    [AvaloniaFact]
    public async Task ArmedLeftClick_WithTextInThePreview_PicksAndFillsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");
            scope.TypeEntryText("QQ");
            main.Measure.Arm();

            Click(window, view.Canvas, SymbolOf(view.Canvas, ac), MouseButton.Left);

            Assert.Equal(new RblPendingAnchor(RblEndpoint.OnAircraft("UAL1"), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Equal("QQ", scope.EntryText);
            Assert.Null(main.SelectedAircraft);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 3: a right press on an aircraft while the tool is armed cancels it and opens no menu.
    [AvaloniaFact]
    public async Task ArmedRightClick_Cancels_NoMenu()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");
            main.Measure.Arm();
            Click(window, view.Canvas, MapA, MouseButton.Left);
            Assert.NotNull(main.Measure.Anchor);

            Click(window, view.Canvas, SymbolOf(view.Canvas, ac), MouseButton.Right);

            Assert.False(main.Measure.IsMeasuring);
            Assert.Null(main.Measure.Anchor);
            Assert.Empty(main.Measure.Lines);
            Assert.Null(view.ActiveContextMenu);
            Assert.Equal("Measure cancelled", main.StatusText);
            Assert.Null(scope.EntryResponse);
            Assert.Equal(RblView.Scope, main.ActiveMeasureView);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The armed right press is an explicit click on the global tool: it cancels it even when another Scope window holds the
    // anchor, unlike Esc, which leaves an anchor this Scope does not show.
    [AvaloniaFact]
    public async Task ArmedRightClick_WithAnotherScopesAnchor_StillCancelsTheTool()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            other.Vm.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.7, -122.2), ""));
            Assert.Same(other.Vm, Assert.NotNull(main.Measure.Anchor).Surface);

            Click(window, view.Canvas, MapA, MouseButton.Right);

            Assert.False(main.Measure.IsMeasuring);
            Assert.Null(main.Measure.Anchor);
            Assert.Null(view.ActiveContextMenu);
            Assert.Equal("Measure cancelled", main.StatusText);
            Assert.True(scope.IsActiveMeasureSurface);
        }
        finally
        {
            main.CloseExtraScopeView(other);
            CloseWindow(window, view);
        }
    }

    // Ladder row 4: with the tool not armed, a left press selects and a right press opens the aircraft menu, as before.
    [AvaloniaFact]
    public async Task NotArmed_LeftClickSelects_AndRightClickOpensTheMenu()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");

            Click(window, view.Canvas, SymbolOf(view.Canvas, ac), MouseButton.Left);
            Assert.Same(ac, main.SelectedAircraft);

            Click(window, view.Canvas, SymbolOf(view.Canvas, ac), MouseButton.Right);
            Assert.NotNull(view.ActiveContextMenu);
            Assert.Empty(main.Measure.Lines);
            Assert.Null(main.Measure.Anchor);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Move row: with a Scope anchor pending, a move records the cursor and the half-placed line draws to it.
    [AvaloniaFact]
    public async Task Move_WithAScopeAnchor_SetsTheCursor_AndThePendingLineDraws()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.Measure.Arm();
            Click(window, view.Canvas, MapA, MouseButton.Left);

            window.MouseMove(InWindow(window, view.Canvas, MapB));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(MapB, view.Canvas.MeasureCursor);
            ResolvedRbl pending = Assert.IsType<ResolvedRbl>(view.Canvas.RenderSnapshotForTesting().PendingRangeBearingLine);
            Assert.Equal(LatLonAt(view.Canvas, MapA), pending.A);
            Assert.Equal(LatLonAt(view.Canvas, MapB).Lat, pending.B.Lat, 6);
            Assert.Equal(LatLonAt(view.Canvas, MapB).Lon, pending.B.Lon, 6);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Move row, during an Alt-drag: the line being dragged draws from its anchor to the cursor before the release.
    [AvaloniaFact]
    public async Task Move_DuringAnAltDrag_DrawsTheLineBeingDragged()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            Point start = InWindow(window, view.Canvas, MapA);
            window.MouseMove(start, RawInputModifiers.Alt);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseMove(InWindow(window, view.Canvas, MapB), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            ResolvedRbl pending = Assert.IsType<ResolvedRbl>(view.Canvas.RenderSnapshotForTesting().PendingRangeBearingLine);
            Assert.Equal(LatLonAt(view.Canvas, MapA), pending.A);
            Assert.Null(main.Measure.Anchor);

            window.MouseUp(InWindow(window, view.Canvas, MapB), MouseButton.Left, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(main.Measure.Lines);
            Assert.Null(view.Canvas.RenderSnapshotForTesting().PendingRangeBearingLine);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The centre of a field of the aircraft's own block.
    private static Point FieldCentre(ScopeCanvas canvas, AircraftModel ac, ScopeFieldSlot slot)
    {
        ScopeTrack track = Assert.Single(canvas.CurrentFrame.Tracks, t => ReferenceEquals(t.Aircraft, ac));
        FieldRect field = track.Block.Fields.First(f => f.Slot == slot);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }

    // Release row, interrupted: an Alt-drag whose capture is lost (Alt-Tab mid-drag) stops drawing, and neither its release
    // nor the next plain click places a line.
    [AvaloniaFact]
    public async Task AltDrag_WhoseCaptureIsLost_StopsDrawing_AndPlacesNothing()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            IPointer? pointer = null;
            view.Canvas.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel, true);
            Point start = InWindow(window, view.Canvas, MapA);
            window.MouseMove(start, RawInputModifiers.Alt);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseMove(InWindow(window, view.Canvas, MapB), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(view.Canvas.RenderSnapshotForTesting().PendingRangeBearingLine);

            Assert.NotNull(pointer);
            pointer.Capture(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.Canvas.RenderSnapshotForTesting().PendingRangeBearingLine);

            window.MouseUp(InWindow(window, view.Canvas, MapB), MouseButton.Left);
            Click(window, view.Canvas, MapB, MouseButton.Left);

            Assert.Empty(main.Measure.Lines);
            Assert.Null(main.Measure.Anchor);
            Assert.Null(view.Canvas.RenderSnapshotForTesting().PendingRangeBearingLine);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Esc row: Esc drops an Alt-drag in flight before the Rings step; its release past the threshold places nothing, the Rings
    // tool stays armed and the status line is left as it was.
    [AvaloniaFact]
    public async Task Esc_DuringAnAltDrag_DropsIt_AndTheReleasePlacesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            ArmTools(scope);
            string? statusBefore = main.StatusText;
            Point start = InWindow(window, view.Canvas, MapA);
            window.MouseMove(start, RawInputModifiers.Alt);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();

            window.DispatchKey(Key.Escape);
            window.MouseUp(InWindow(window, view.Canvas, MapB), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(main.Measure.Lines);
            Assert.Null(main.Measure.Anchor);
            Assert.False(main.Measure.IsMeasuring);
            Assert.True(scope.IsRingToolArmed);
            Assert.Equal(statusBefore, main.StatusText);
            Assert.Null(view.Canvas.RenderSnapshotForTesting().PendingRangeBearingLine);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 2, on an ERAM block's callsign (Field A): the armed tool picks; the dwell lock is not toggled.
    [AvaloniaFact]
    public async Task ArmedLeftClick_OnAnEramFieldA_PicksAndTogglesNoDwellLock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            scope.TemplateId = ScopeTemplates.EramId;
            Dispatcher.UIThread.RunJobs();
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");
            main.Measure.Arm();

            Click(window, view.Canvas, FieldCentre(view.Canvas, ac, ScopeFieldSlot.Callsign), MouseButton.Left);

            Assert.Equal(new RblPendingAnchor(RblEndpoint.OnAircraft("UAL1"), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
            Assert.Null(main.SelectedAircraft);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 2, on a field with an action (a STARS altitude): the armed tool picks; no field menu opens.
    [AvaloniaFact]
    public async Task ArmedLeftClick_OnAnActionableField_PicksAndOpensNoMenu()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            scope.TemplateId = ScopeTemplates.StarsId;
            Dispatcher.UIThread.RunJobs();
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");
            main.Measure.Arm();

            Click(window, view.Canvas, FieldCentre(view.Canvas, ac, ScopeFieldSlot.Altitude), MouseButton.Left);

            Assert.Equal(new RblPendingAnchor(RblEndpoint.OnAircraft("UAL1"), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Null(view.ActiveContextMenu);
            Assert.Null(main.SelectedAircraft);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 2, on the map with text in the preview: the armed tool picks the point; the point is not filled.
    [AvaloniaFact]
    public async Task ArmedLeftClick_OnTheMap_WithTextInThePreview_PicksAndFillsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            scope.TypeEntryText("QQ");
            main.Measure.Arm();

            Click(window, view.Canvas, MapA, MouseButton.Left);

            Assert.Equal(new RblPendingAnchor(RblEndpoint.AtPoint(LatLonAt(view.Canvas, MapA), ""), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Equal("QQ", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Ladder row 3, held and moved: the right press that cancels the tool starts no pan.
    [AvaloniaFact]
    public async Task ArmedRightPress_ThenARightDrag_CancelsAndDoesNotPan()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.Measure.Arm();
            double lat = view.Canvas.Viewport.CenterLat;
            double lon = view.Canvas.Viewport.CenterLon;

            window.MouseDown(InWindow(window, view.Canvas, MapA), MouseButton.Right);
            window.MouseMove(InWindow(window, view.Canvas, MapB), RawInputModifiers.RightMouseButton);
            window.MouseUp(InWindow(window, view.Canvas, MapB), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Assert.False(main.Measure.IsMeasuring);
            Assert.Equal(lat, view.Canvas.Viewport.CenterLat);
            Assert.Equal(lon, view.Canvas.Viewport.CenterLon);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Middle button, armed: as unarmed (YAAT-2375's rows): a STARS block's highlight toggles; the tool stays armed, no anchor.
    [AvaloniaFact]
    public async Task ArmedMiddleClick_OnAStarsBlock_TogglesTheHighlight_AsUnarmed()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            scope.TemplateId = ScopeTemplates.StarsId;
            Dispatcher.UIThread.RunJobs();
            AircraftModel ac = AddAt300(main, view.Canvas, "UAL1");
            main.Measure.Arm();

            Click(window, view.Canvas, FieldCentre(view.Canvas, ac, ScopeFieldSlot.Callsign), MouseButton.Middle);

            Assert.Contains("UAL1", scope.DataBlockState.HighlightedCallsigns);
            Assert.True(main.Measure.IsMeasuring);
            Assert.Null(main.Measure.Anchor);
            Assert.Empty(main.Measure.Lines);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // A pick that finds every slot taken: the Scope's response shows the full-store error. The store cannot be armed while
    // full and any whole-line placement disarms it, so the pick that meets a full store is an Alt-click's anchor.
    [AvaloniaFact]
    public async Task Pick_WithEveryMeasurementInUse_ShowsTheFullError()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            var a = RblEndpoint.AtPoint(new LatLon(37.6, -122.4), "");
            var b = RblEndpoint.AtPoint(new LatLon(37.8, -122.0), "");
            RblTrackLookup lookup = main.MeasureSurface(RblView.Radar, null).Lookup;
            for (int i = 0; i < RangeBearingLineStore.MaxLines; i++)
            {
                main.Measure.Place(a, b, new MeasureTarget(RblView.Radar, lookup, RblUnits.NauticalMiles, null));
            }

            Point at = InWindow(window, view.Canvas, MapA);
            window.MouseDown(at, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseUp(at, MouseButton.Left, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(main.Measure.Anchor);
            Assert.False(main.Measure.IsMeasuring);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, RangeBearingViewState.FullStatus, "", []), scope.EntryResponse);
            Assert.Equal(RangeBearingViewState.FullStatus, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }
}
