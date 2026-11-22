using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Authoring;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Airport Editor's canvas pans on a middle drag and not on a right or a left one, which it keeps for its menus and
/// its selection; the Scope's canvas still pans on a right drag.
/// </summary>
public class AirportEditorCanvasPanTests
{
    private static readonly Point Start = new(400, 300);

    private static readonly Vector Step = new(60, 40);

    [AvaloniaFact]
    public void AMiddleDrag_Pans_ARightOrALeftDragDoesNot()
    {
        var canvas = new AirportEditorCanvas();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        Settle(window);
        try
        {
            canvas.Viewport.CenterLat = 37.72;
            canvas.Viewport.CenterLon = -122.22;
            double centerLon = canvas.Viewport.CenterLon;
            double centerLat = canvas.Viewport.CenterLat;

            Drag(window, MouseButton.Right, RawInputModifiers.RightMouseButton);
            Drag(window, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Assert.Equal(centerLon, canvas.Viewport.CenterLon);
            Assert.Equal(centerLat, canvas.Viewport.CenterLat);

            Drag(window, MouseButton.Middle, RawInputModifiers.MiddleMouseButton);
            Assert.NotEqual(centerLon, canvas.Viewport.CenterLon);
            Assert.NotEqual(centerLat, canvas.Viewport.CenterLat);
        }
        finally
        {
            window.Close();
            canvas.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void AScopeCanvas_StillPansOnARightDrag()
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
        Settle(window);
        try
        {
            double centerLon = view.Canvas.Viewport.CenterLon;

            Drag(window, MouseButton.Right, RawInputModifiers.RightMouseButton);

            Assert.NotEqual(centerLon, view.Canvas.Viewport.CenterLon);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Drag(Window window, MouseButton button, RawInputModifiers held)
    {
        window.MouseDown(Start, button);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(Start + Step, held);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(Start + Step, button);
        Dispatcher.UIThread.RunJobs();
    }
}
