using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// Issue #350: per-callsign datablock view state — manual drag offsets, hidden/shown choices,
// highlights, minified state — must be session-persistent. It used to live in private fields on
// the rendering canvas, which lost it two ways: the Ground canvas cleared it whenever the Layout
// binding re-fired (tab switches detach the view and churn inherited-DataContext bindings), and
// pop-out windows instantiate a brand-new view whose canvas starts empty. The state now lives on
// the view-model (one instance per session, shared by every view bound to it).
public class DataBlockStatePersistenceTests
{
    private const string Callsign = "UAL238";
    private const double FieldLat = 37.62;
    private const double FieldLon = -122.39;

    // DataBlockLayout geometry (see GroundCanvasDataBlockHitTests): default offset (30, -25),
    // on-ground block is two 14 px lines, so a point a few px into the block is a reliable grab.
    private static readonly Point BlockGrabDelta = new(31, -17);

    [AvaloniaFact]
    public void GroundCanvas_LayoutAssignment_PreservesManualDataBlockOffset()
    {
        var ac = MakeAircraft();
        var canvas = MakeGroundCanvas(ac);
        var window = ShowInWindow(canvas);

        DragDataBlock(window, canvas, ac);
        Assert.True(canvas.HasManualDataBlockOffset(Callsign), "drag should have produced a manual offset");

        // A layout (re)load — or the Layout binding re-firing on tab reattach — must not wipe it.
        canvas.Layout = SfoLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.HasManualDataBlockOffset(Callsign), "manual datablock offset must survive a Layout property change");
    }

    [AvaloniaFact]
    public void GroundCanvas_LayoutAssignment_PreservesHiddenDataBlockChoice()
    {
        var canvas = new GroundCanvas();
        canvas.ToggleHiddenDataBlock(Callsign);
        Assert.True(canvas.IsDataBlockHidden(Callsign));

        canvas.Layout = SfoLayout();

        Assert.True(canvas.IsDataBlockHidden(Callsign), "hidden-datablock choice must survive a Layout property change");
    }

    [AvaloniaFact]
    public void GroundViews_BoundToSameViewModel_ShareManualOffsets()
    {
        // The pop-out contract: the embedded tab view and the pop-out window are two GroundView
        // instances over the same GroundViewModel, and must see the same datablock state.
        var vm = NewGroundVm();
        var ac = MakeAircraft();
        var (canvas1, window1) = BindGroundView(vm, ac);
        var (canvas2, _) = BindGroundView(vm, ac);

        DragDataBlock(window1, canvas1, ac);
        Assert.True(canvas1.HasManualDataBlockOffset(Callsign), "drag should have produced a manual offset");

        Assert.True(canvas2.HasManualDataBlockOffset(Callsign), "a second view over the same view-model must see the manual offset");
    }

    [AvaloniaFact]
    public void GroundViews_BoundToSameViewModel_ShareHiddenChoice_AcrossLateLoad()
    {
        // Opening a pop-out AFTER hiding a datablock: the fresh view's OnLoaded runs
        // SetStartWithAllHidden(preference) and must not wipe existing per-callsign choices.
        var vm = NewGroundVm();
        var ac = MakeAircraft();
        var (canvas1, _) = BindGroundView(vm, ac);

        canvas1.ToggleHiddenDataBlock(Callsign);
        Assert.True(canvas1.IsDataBlockHidden(Callsign));

        var (canvas2, _) = BindGroundView(vm, ac);

        Assert.True(canvas2.IsDataBlockHidden(Callsign), "a late-opened view must see the existing hidden choice");
        Assert.True(canvas1.IsDataBlockHidden(Callsign), "opening a second view must not reset the first view's hidden choice");
    }

    [AvaloniaFact]
    public void RadarViews_BoundToSameViewModel_ShareMinifiedState()
    {
        var vm = new RadarViewModel(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);
        var canvas1 = BindRadarView(vm);
        var canvas2 = BindRadarView(vm);

        canvas1.ToggleMinifiedDataBlock(Callsign);

        Assert.True(canvas2.IsMinified(Callsign), "a second radar view over the same view-model must see the minified choice");
    }

    [AvaloniaFact]
    public void MainWindow_GroundTabSwitchAwayAndBack_PreservesManualDataBlockOffset()
    {
        // The literal user repro: drag a Ground datablock, switch to another docked tab, switch
        // back. Tab reselection detaches/reattaches the inline GroundView, which re-fires the
        // Layout binding on the canvas — that churn must not clear the manual offset.
        var window = new MainWindow();
        window.ShowAndRunLayout();
        var vm = (MainViewModel)window.DataContext!;

        // Start from a known all-docked state: pop-out flips persist to the shared per-process
        // preferences.json, so a preceding test can leave the Ground view popped out — its canvas
        // would then live in a pop-out window instead of the docked tab this test drives.
        vm.IsDataGridPoppedOut = false;
        vm.IsGroundViewPoppedOut = false;
        vm.IsRadarViewPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("MainTabControl");
        Assert.NotNull(tabs);

        vm.Ground.SetLayoutForTesting(SfoLayout());
        tabs!.SelectedIndex = 1; // Ground View tab
        PumpLayout(window);

        var canvas = WaitForDockedGroundCanvas(window, vm);
        var ac = MakeAircraft();
        canvas.Aircraft = new[] { ac };
        canvas.Viewport.CenterLat = FieldLat;
        canvas.Viewport.CenterLon = FieldLon;
        canvas.Viewport.Zoom = 1.0;
        Dispatcher.UIThread.RunJobs();

        DragDataBlock(window, canvas, ac);
        Assert.True(canvas.HasManualDataBlockOffset(Callsign), "drag should have produced a manual offset");

        tabs.SelectedIndex = 0;
        PumpLayout(window);
        tabs.SelectedIndex = 1;
        PumpLayout(window);

        Assert.True(canvas.HasManualDataBlockOffset(Callsign), "manual datablock offset must survive switching the docked tab away and back");
    }

    [AvaloniaFact]
    public void GroundViewModel_LayoutLifecycle_ClearsDataBlockState()
    {
        // The clear moved from the canvas's Layout-changed handler to the view-model's layout
        // lifecycle: a real load or unload resets per-callsign state, binding churn does not.
        var vm = NewGroundVm();

        vm.DataBlockState.ManualOffsets[Callsign] = new SKPoint(12, 34);
        vm.SetLayoutForTesting(SfoLayout());
        Assert.Empty(vm.DataBlockState.ManualOffsets);

        vm.DataBlockState.ManualOffsets[Callsign] = new SKPoint(12, 34);
        vm.DataBlockState.HighlightedCallsigns.Add(Callsign);
        vm.DataBlockState.ToggleHiddenDataBlock(Callsign);
        vm.ClearLayout();
        Assert.Empty(vm.DataBlockState.ManualOffsets);
        Assert.Empty(vm.DataBlockState.HighlightedCallsigns);
        Assert.False(vm.DataBlockState.IsDataBlockHidden(Callsign));
    }

    [AvaloniaFact]
    public void ScenarioRestart_ClearsBothViewsDataBlockState()
    {
        // Respawned aircraft are back at their start positions, so stale offsets/choices don't apply.
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Ground.DataBlockState.ManualOffsets[Callsign] = new SKPoint(12, 34);
        vm.Radar.DataBlockState.ManualOffsets[Callsign] = new SKPoint(12, 34);
        vm.Radar.DataBlockState.MinifiedCallsigns.Add(Callsign);

        vm.ApplyScenarioRestart([]);

        Assert.Empty(vm.Ground.DataBlockState.ManualOffsets);
        Assert.Empty(vm.Radar.DataBlockState.ManualOffsets);
        Assert.Empty(vm.Radar.DataBlockState.MinifiedCallsigns);
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// The docked GroundCanvas enters MainWindow's visual tree only once the TabControl
    /// materializes the selected tab's content, which can lag extra dispatcher/layout passes when
    /// the machine is loaded (the suite has flaked here under parallel full-solution runs). Pumps
    /// until it appears; on timeout fails with the view-model state so the cause is visible.
    /// </summary>
    private static GroundCanvas WaitForDockedGroundCanvas(Window window, MainViewModel vm)
    {
        for (var i = 0; i < 40; i++)
        {
            var canvas = window.GetVisualDescendants().OfType<GroundCanvas>().FirstOrDefault();
            if (canvas is not null)
            {
                return canvas;
            }
            Thread.Sleep(25);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        Assert.Fail(
            $"no docked GroundCanvas materialized: SelectedTabIndex={vm.SelectedTabIndex}, GroundPoppedOut={vm.IsGroundViewPoppedOut}, "
                + $"AnyTabVisible={vm.IsAnyTabVisible}, ContentGridVisible={vm.IsContentGridVisible}, WindowSize={window.Bounds.Size}"
        );
        return null!; // unreachable — Assert.Fail throws
    }

    private static AircraftModel MakeAircraft()
    {
        var ac = new AircraftModel
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Destination = "KLAX",
            FlightRules = "IFR",
            TransponderMode = "C", // avoid the SqStby line so the on-ground block stays two lines
        };
        ac.IsOnGround = true;
        ac.Altitude = 0;
        ac.Position = new LatLon(FieldLat, FieldLon);
        return ac;
    }

    private static GroundCanvas MakeGroundCanvas(AircraftModel ac)
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = FieldLat;
        canvas.Viewport.CenterLon = FieldLon;
        canvas.Viewport.Zoom = 1.0;
        canvas.AirportCenterLat = FieldLat;
        canvas.AirportCenterLon = FieldLon;
        canvas.AirportElevation = 0;
        canvas.Aircraft = new[] { ac };
        return canvas;
    }

    private static Window ShowInWindow(Control content)
    {
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = content,
        };
        window.ShowAndRunLayout();
        PumpLayout(window);
        return window;
    }

    private static GroundViewModel NewGroundVm() => new(new ServerConnection(), (_, _, _) => Task.CompletedTask, preferences: new UserPreferences());

    private static (GroundCanvas Canvas, Window Window) BindGroundView(GroundViewModel vm, AircraftModel ac)
    {
        var view = new GroundView { DataContext = vm };
        var window = ShowInWindow(view);
        var canvas = view.FindControl<GroundCanvas>("Canvas");
        Assert.NotNull(canvas);
        canvas!.Viewport.CenterLat = FieldLat;
        canvas.Viewport.CenterLon = FieldLon;
        canvas.Viewport.Zoom = 1.0;
        canvas.Aircraft = new[] { ac };
        Dispatcher.UIThread.RunJobs();
        return (canvas, window);
    }

    private static RadarCanvas BindRadarView(RadarViewModel vm)
    {
        var view = new RadarView { DataContext = vm };
        var window = ShowInWindow(view);
        var canvas = view.FindControl<RadarCanvas>("Canvas");
        Assert.NotNull(canvas);
        return canvas!;
    }

    /// <summary>Drags the aircraft's datablock 40 px down-right with the headless mouse.</summary>
    private static void DragDataBlock(Window window, GroundCanvas canvas, AircraftModel ac)
    {
        var (sx, sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        var origin = canvas.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(origin);
        var from = new Point(origin!.Value.X + sx + BlockGrabDelta.X, origin.Value.Y + sy + BlockGrabDelta.Y);
        var to = new Point(from.X + 40, from.Y + 40);
        window.MouseDrag(from, to);
    }

    private static void PumpLayout(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static GroundLayoutDto SfoLayout()
    {
        var nodes = new List<GroundNodeDto>
        {
            new(1, 37.61, -122.39, "Taxiway", null, null, null),
            new(2, 37.63, -122.39, "Taxiway", null, null, null),
            new(3, 37.61, -122.36, "Taxiway", null, null, null),
            new(4, 37.63, -122.36, "Taxiway", null, null, null),
        };
        return new GroundLayoutDto("SFO", nodes, [], null, null, null);
    }
}
