using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Views.Authoring;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>The parking picker's canvas pans on a right drag, and a left press on a stand selects it without panning.</summary>
public class ParkingPickerCanvasPanTests
{
    private static readonly Vector Step = new(60, 40);

    [AvaloniaFact]
    public void RightDrag_Pans_LeftClickSelectsAndDoesNotPan()
    {
        (Window window, ParkingPickerCanvas canvas, ParkingPickerScene scene) = Show(null);
        try
        {
            (ParkingPickerStand stand, Point at) = OakParkingLayout.IsolatedStand(scene, canvas.Viewport);
            Point start = canvas.TranslatePoint(at, window)!.Value;
            double centerLon = canvas.Viewport.CenterLon;
            double centerLat = canvas.Viewport.CenterLat;

            Drag(window, start, MouseButton.Left, RawInputModifiers.LeftMouseButton);

            Assert.Equal(stand.Name, canvas.Selected);
            Assert.Equal(centerLon, canvas.Viewport.CenterLon);
            Assert.Equal(centerLat, canvas.Viewport.CenterLat);

            Drag(window, start, MouseButton.Right, RawInputModifiers.RightMouseButton);

            Assert.NotEqual(centerLon, canvas.Viewport.CenterLon);
            Assert.NotEqual(centerLat, canvas.Viewport.CenterLat);
            Assert.Equal(stand.Name, canvas.Selected);
        }
        finally
        {
            Close(window, canvas);
        }
    }

    private static void Drag(Window window, Point start, MouseButton button, RawInputModifiers held)
    {
        window.MouseDown(start, button);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(start + Step, held);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(start + Step, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, ParkingPickerCanvas Canvas, ParkingPickerScene Scene) Show(string? current)
    {
        var scene = ParkingPickerScene.Build(OakParkingLayout.Layout, current);
        var canvas = new ParkingPickerCanvas { Scene = scene };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        Settle(window);
        canvas.FitToStands();
        Settle(window);
        return (window, canvas, scene);
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

    private static void Close(Window window, ParkingPickerCanvas canvas)
    {
        window.Close();
        canvas.Dispose();
        Dispatcher.UIThread.RunJobs();
    }
}
