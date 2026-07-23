using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.UI.Tests.Views;

// Covers the fresh-profile-vs-saved-view auto-fit decision in GroundCanvas.
// Two compounding bugs were addressed: (1) the (0,0) sentinel meant a fresh
// profile rendered the airport off-screen because ApplyViewToViewport early-
// returned and FitToLayout's rescue path was timing-dependent; (2) on every
// scenario reload the auto-fit clobbered any restored saved view because
// OnPropertyChanged(LayoutProperty) unconditionally re-ran FitToLayout.
//
// The new flow uses an explicit `_initialFitDone` one-shot plus a bindable
// `HasSavedView` flag from GroundViewModel: TryInitialView() picks
// ApplyViewToViewport (saved) or FitToLayout (auto-fit) once both layout and
// viewport size are present.
public class GroundCanvasFitTests
{
    // SFO-ish bounding box; centroid ≈ (37.62, -122.375).
    private const double MinLat = 37.61;
    private const double MaxLat = 37.63;
    private const double MinLon = -122.39;
    private const double MaxLon = -122.36;

    [AvaloniaFact]
    public void FreshProfile_LayoutThenSize_AutoFitsToCentroid()
    {
        // Mimics the user's repro: empty preferences, scenario loads, layout
        // arrives. Once the canvas is sized the auto-fit must run with no RESET click.
        var (canvas, _) = MakeCanvas();
        canvas.HasSavedView = false;
        canvas.Layout = SfoLayout();

        AttachAndSize(canvas, 800, 600);

        AssertCentroidFit(canvas);
    }

    [AvaloniaFact]
    public void FreshProfile_SizeThenLayout_AutoFitsToCentroid()
    {
        // Reverse ordering: canvas is sized while idle, then layout arrives.
        // OnPropertyChanged(LayoutProperty) → TryInitialView must fit immediately.
        var (canvas, _) = MakeCanvas();
        canvas.HasSavedView = false;

        AttachAndSize(canvas, 800, 600);
        canvas.Layout = SfoLayout();
        Dispatcher.UIThread.RunJobs();

        AssertCentroidFit(canvas);
    }

    [AvaloniaFact]
    public void SavedView_RestoresExactValues_NoAutoFit()
    {
        // Saved view exists for this scenario. The canvas must apply the saved
        // CenterLat/Lon/Zoom/Rotation and not run FitToLayout.
        var (canvas, _) = MakeCanvas();
        canvas.ViewCenterLat = 40.7;
        canvas.ViewCenterLon = -74.0;
        canvas.ViewZoom = 12.5;
        canvas.ViewRotation = 0;
        canvas.HasSavedView = true;
        canvas.Layout = SfoLayout();

        AttachAndSize(canvas, 800, 600);

        Assert.Equal(40.7, canvas.Viewport.CenterLat, 6);
        Assert.Equal(-74.0, canvas.Viewport.CenterLon, 6);
        Assert.Equal(12.5, canvas.Viewport.Zoom, 6);
    }

    [AvaloniaFact]
    public void LayoutBeforeSized_NoOpUntilSize_ThenFits()
    {
        // Canvas not in a window yet — Viewport.PixelWidth=0. Layout is set,
        // TryInitialView returns early, viewport stays at default. Once attached
        // and sized, OnSizeChanged → TryInitialView completes the fit.
        var (canvas, _) = MakeCanvas();
        canvas.Layout = SfoLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, canvas.Viewport.CenterLat);
        Assert.Equal(0, canvas.Viewport.CenterLon);

        AttachAndSize(canvas, 800, 600);

        AssertCentroidFit(canvas);
    }

    [AvaloniaFact]
    public void ResetView_RefitsAfterRestore()
    {
        // Saved view restored, then user clicks RESET. Centroid fit must
        // overwrite the saved view (RESET is the user's explicit choice).
        var (canvas, _) = MakeCanvas();
        canvas.ViewCenterLat = 40.7;
        canvas.ViewCenterLon = -74.0;
        canvas.ViewZoom = 12.5;
        canvas.HasSavedView = true;
        canvas.Layout = SfoLayout();
        AttachAndSize(canvas, 800, 600);

        canvas.ResetView();
        Dispatcher.UIThread.RunJobs();

        AssertCentroidFit(canvas);
    }

    [AvaloniaFact]
    public void ViewModel_FreshProfile_AutoFitsOnLayoutLoad()
    {
        // End-to-end: GroundViewModel with empty UserPreferences, set scenario
        // id, then push layout. The bound canvas must auto-fit and the
        // resulting viewport push-back must persist saved settings.
        //
        // Uses a scenario id no other test writes: UserPreferences persists to a single
        // preferences.json shared by the whole assembly, so reusing an id that a sibling test
        // saves settings for makes this assertion depend on test execution order.
        const string scenarioId = "scenario-fresh-autofit";

        var prefs = new UserPreferences();
        var vm = new GroundViewModel(new ServerConnection(), (_, _, _) => Task.CompletedTask, preferences: prefs);

        var (canvas, window) = BindCanvasToViewModel(vm);

        vm.SetScenarioId(scenarioId);
        Assert.False(vm.HasSavedView);

        vm.Layout = SfoLayout();
        PumpLayout(window);

        AssertCentroidFit(canvas);
        Assert.True(vm.HasSavedView, "auto-fit push-back should flip HasSavedView true via SaveSettings");

        var saved = prefs.GetGroundSettings(scenarioId);
        Assert.NotNull(saved);
        Assert.InRange(saved!.CenterLat, MinLat, MaxLat);
    }

    [AvaloniaFact]
    public void ViewModel_SavedView_PersistsAcrossClearAndRebootstrap()
    {
        // The companion bug: reload a scenario whose saved view exists. The
        // restored view must survive the Layout-arrived auto-fit pass; today,
        // before the fix, FitToLayout silently overwrote it on every reload.
        var prefs = new UserPreferences();
        prefs.SetGroundSettings(
            "scenario-1",
            new SavedGroundSettings
            {
                CenterLat = 40.7,
                CenterLon = -74.0,
                Zoom = 12.5,
                Rotation = 0,
                IsPanZoomLocked = false,
                ShowRunwayLabels = true,
                ShowTaxiwayLabels = true,
                ShowHoldShort = GroundFilterMode.LabelsAndIcons,
                ShowParking = GroundFilterMode.LabelsAndIcons,
                ShowSpot = GroundFilterMode.LabelsAndIcons,
            }
        );

        var vm = new GroundViewModel(new ServerConnection(), (_, _, _) => Task.CompletedTask, preferences: prefs);
        var (canvas, window) = BindCanvasToViewModel(vm);

        vm.SetScenarioId("scenario-1");
        Assert.True(vm.HasSavedView);

        vm.Layout = SfoLayout();
        PumpLayout(window);

        // Saved view kept; centroid auto-fit must NOT have run.
        Assert.Equal(40.7, canvas.Viewport.CenterLat, 6);
        Assert.Equal(-74.0, canvas.Viewport.CenterLon, 6);
        Assert.Equal(12.5, canvas.Viewport.Zoom, 6);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static (GroundCanvas Canvas, Window Window) MakeCanvas()
    {
        // Detached canvas with no visual root yet; tests opt into a window via AttachAndSize.
        return (new GroundCanvas(), null!);
    }

    private static (GroundCanvas Canvas, Window Window) BindCanvasToViewModel(GroundViewModel vm)
    {
        var view = new GroundView { DataContext = vm };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Show();
        PumpLayout(window);
        var canvas = view.FindControl<GroundCanvas>("Canvas");
        Assert.NotNull(canvas);
        return (canvas!, window);
    }

    private static void AttachAndSize(GroundCanvas canvas, double width, double height)
    {
        if (canvas.GetPresentationSource() is null)
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Content = canvas,
            };
            window.Show();
            PumpLayout(window);
            return;
        }

        if (canvas.GetPresentationSource()?.RootVisual is Window w)
        {
            w.Width = width;
            w.Height = height;
            PumpLayout(w);
        }
    }

    private static void PumpLayout(Window window)
    {
        // Headless Avalonia needs a few measure/arrange + dispatcher cycles before
        // OnSizeChanged actually flows to nested controls; pump until stable.
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void AssertCentroidFit(GroundCanvas canvas)
    {
        Assert.InRange(canvas.Viewport.CenterLat, MinLat, MaxLat);
        Assert.InRange(canvas.Viewport.CenterLon, MinLon, MaxLon);
        Assert.True(canvas.Viewport.Zoom > 1.0, $"expected fit zoom > 1.0, got {canvas.Viewport.Zoom}");
    }

    private static GroundLayoutDto SfoLayout()
    {
        // Four corners of the SFO-ish bounding box. Enough to drive FitBounds.
        var nodes = new List<GroundNodeDto>
        {
            new(1, MinLat, MinLon, "Taxiway", null, null, null),
            new(2, MaxLat, MinLon, "Taxiway", null, null, null),
            new(3, MinLat, MaxLon, "Taxiway", null, null, null),
            new(4, MaxLat, MaxLon, "Taxiway", null, null, null),
        };
        return new GroundLayoutDto("SFO", nodes, [], null, null);
    }
}
