using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// DISPLAY's LOCK: while locked the wheel does not zoom and a right drag does not pan, but the RANGE spinner and
/// <c>.scope range</c> still change the range; LOCK and the MVA tint are kept in a pref set.
/// </summary>
public class ScopePanZoomLockTests
{
    [AvaloniaFact]
    public void Locked_TheWheelDoesNotZoom()
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            var centre = new Point(400, 300);
            main.RunScopeCommand(".scope lock on");
            double zoom = view.Canvas.Viewport.Zoom;

            window.MouseWheel(centre, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(zoom, view.Canvas.Viewport.Zoom);

            main.RunScopeCommand(".scope lock off");
            window.MouseWheel(centre, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(zoom, view.Canvas.Viewport.Zoom);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Locked_ARightDragDoesNotPan()
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            main.RunScopeCommand(".scope lock on");
            double centerLon = view.Canvas.Viewport.CenterLon;

            RightDrag(window, new Point(400, 300), new Vector(60, 40));
            Assert.Equal(centerLon, view.Canvas.Viewport.CenterLon);

            main.RunScopeCommand(".scope lock off");
            RightDrag(window, new Point(400, 300), new Vector(60, 40));
            Assert.NotEqual(centerLon, view.Canvas.Viewport.CenterLon);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Locked_TheRangeSpinnerAndScopeRange_StillChangeTheRange()
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            var scope = (ScopeViewModel)view.DataContext!;
            main.RunScopeCommand(".scope lock on");
            double zoom = view.Canvas.Viewport.Zoom;

            scope.StepSpinner(ScopeButtonId.Range, 5);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ScopeViewModel.DefaultRangeNm - 5, scope.RangeNm);
            Assert.NotEqual(zoom, view.Canvas.Viewport.Zoom);

            main.RunScopeCommand(".scope range 20");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(20, scope.RangeNm);
            Assert.Equal(20, view.Canvas.RangeNm);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LockAndTheMvaTint_AreKeptInAPrefSet()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        ScopeMapToolsCommandTests.LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        scope.IsPanZoomLocked = true;
        scope.ShowMvaHints = true;

        ScopePrefSet set = scope.CapturePrefSet("MINE");
        Assert.Equal(new ScopePrefMapTools(Mva: true, Lock: true, RangeRing: null, Weather: null, FixesAndMarkers: null), set.MapTools);

        scope.IsPanZoomLocked = false;
        scope.ShowMvaHints = false;
        scope.ApplyPrefSet(set);

        Assert.True(scope.IsPanZoomLocked);
        Assert.True(scope.ShowMvaHints);
    }

    [AvaloniaFact]
    public void ASetsLockAndMva_AreApplied()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        ScopeMapToolsCommandTests.LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        ScopePrefSet set = scope.CapturePrefSet("MINE") with
        {
            MapTools = new ScopePrefMapTools(Mva: true, Lock: false, RangeRing: null, Weather: null, FixesAndMarkers: null),
        };
        scope.IsPanZoomLocked = true;
        scope.ShowMvaHints = false;

        scope.ApplyPrefSet(set);

        Assert.False(scope.IsPanZoomLocked);
        Assert.True(scope.ShowMvaHints);
    }

    private static void RightDrag(Window window, Point from, Vector by)
    {
        window.MouseDown(from, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(from + by, RawInputModifiers.RightMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(from + by, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainViewModel Main, ScopeView View, Window Window) ShowScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot)
        {
            CenterLat = 37.62,
            CenterLon = -122.38,
        };
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
        return (main, view, window);
    }
}
