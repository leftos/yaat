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
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Ctrl-hover MVA readout: while Control is held over the scope, the MVA floor under the pointer shows as a label beside
/// it; outside every MVA sector, or once Control is let go, no label shows.
/// </summary>
public class ScopeMvaHoverTests
{
    private static readonly Point Pointer = new(400, 300);

    [AvaloniaFact]
    public void ControlHeld_OverAnMvaSector_ShowsItsAltitude()
    {
        (ScopeView view, Window window) = ShowScope(37.7213, -122.2208);
        try
        {
            MvaSector sector = SectorUnder(view.Canvas, Pointer)!;
            Assert.NotNull(sector);

            window.MouseMove(Pointer, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            ScopeMvaHover? hover = view.Canvas.RenderSnapshotForTesting().Overlays?.MvaHover;
            Assert.NotNull(hover);
            Assert.Equal($"MVA {sector.FloorFtMsl} ({sector.Sector})", hover.Value.Text);
            Assert.Equal(((float)Pointer.X, (float)Pointer.Y), (hover.Value.At.X, hover.Value.At.Y));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ControlHeld_OutsideEveryMvaSector_ShowsNoLabel()
    {
        (ScopeView view, Window window) = ShowScope(30.0, -140.0);
        try
        {
            Assert.Null(SectorUnder(view.Canvas, Pointer));

            window.MouseMove(Pointer, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(view.Canvas.RenderSnapshotForTesting().Overlays?.MvaHover);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ControlReleased_TakesTheLabelAway()
    {
        (ScopeView view, Window window) = ShowScope(37.7213, -122.2208);
        try
        {
            view.Canvas.Focus();
            window.MouseMove(Pointer, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(view.Canvas.RenderSnapshotForTesting().Overlays?.MvaHover);

            window.KeyReleaseQwerty(PhysicalKey.ControlLeft, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(view.Canvas.RenderSnapshotForTesting().Overlays?.MvaHover);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TheWindowDeactivating_LetsGoOfControl()
    {
        (ScopeView view, Window window) = ShowScope(37.7213, -122.2208);
        try
        {
            window.MouseMove(Pointer, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.Canvas.IsMvaKeyHeld);

            // Headless raises Deactivated only on Hide.
            window.Hide();
            Dispatcher.UIThread.RunJobs();

            Assert.False(view.Canvas.IsMvaKeyHeld);
            Assert.Null(view.Canvas.RenderSnapshotForTesting().Overlays?.MvaHover);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void NoControl_ShowsNoLabel()
    {
        (ScopeView view, Window window) = ShowScope(37.7213, -122.2208);
        try
        {
            window.MouseMove(Pointer);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(view.Canvas.RenderSnapshotForTesting().Overlays?.MvaHover);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static MvaSector? SectorUnder(ScopeCanvas canvas, Point point)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)point.X, (float)point.Y);
        return MvaDatabase.Default.FindSector(new LatLon(lat, lon), canvas.MvaFacilityId);
    }

    private static (ScopeView View, Window Window) ShowScope(double lat, double lon)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot) { CenterLat = lat, CenterLon = lon };
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
        return (view, window);
    }
}
