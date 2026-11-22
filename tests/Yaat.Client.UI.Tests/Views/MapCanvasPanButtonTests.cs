using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Map;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// <see cref="MapCanvasBase"/> pans on the drag of its <c>PanButton</c> only: right by default (the Scope and the Ground
/// view), middle for a canvas that keeps the right button for its context menus; another button's press or release
/// during the drag neither restarts nor ends the pan.
/// </summary>
public class MapCanvasPanButtonTests
{
    private static readonly Point Start = new(400, 300);

    private static readonly Vector Step = new(60, 40);

    [AvaloniaFact]
    public void ACanvasWithAMiddlePanButton_PansOnAMiddleDrag_NotOnARightDrag()
    {
        (TestCanvas canvas, Window window) = Show(MouseButton.Middle);
        try
        {
            double centerLon = canvas.Viewport.CenterLon;
            Drag(window, MouseButton.Right, RawInputModifiers.RightMouseButton);
            Assert.Equal(centerLon, canvas.Viewport.CenterLon);

            Drag(window, MouseButton.Middle, RawInputModifiers.MiddleMouseButton);
            Assert.NotEqual(centerLon, canvas.Viewport.CenterLon);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TheDefaultPanButton_IsRight_AndAMiddleDragDoesNotPan()
    {
        (TestCanvas canvas, Window window) = Show(panButton: null);
        try
        {
            double centerLon = canvas.Viewport.CenterLon;
            Drag(window, MouseButton.Middle, RawInputModifiers.MiddleMouseButton);
            Assert.Equal(centerLon, canvas.Viewport.CenterLon);

            Drag(window, MouseButton.Right, RawInputModifiers.RightMouseButton);
            Assert.NotEqual(centerLon, canvas.Viewport.CenterLon);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ALeftReleaseDuringAMiddleDrag_DoesNotEndThePan()
    {
        (TestCanvas canvas, Window window) = Show(MouseButton.Middle);
        try
        {
            window.MouseDown(Start, MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            window.MouseMove(Start + Step, RawInputModifiers.MiddleMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(Start + Step, MouseButton.Left, RawInputModifiers.MiddleMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(Start + Step, MouseButton.Left, RawInputModifiers.MiddleMouseButton);
            Dispatcher.UIThread.RunJobs();
            double afterLeftClick = canvas.Viewport.CenterLon;

            window.MouseMove(Start + Step + Step, RawInputModifiers.MiddleMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(afterLeftClick, canvas.Viewport.CenterLon);

            window.MouseUp(Start + Step + Step, MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            double afterRelease = canvas.Viewport.CenterLon;
            window.MouseMove(Start, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(afterRelease, canvas.Viewport.CenterLon);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void APanWhoseReleaseNeverArrives_EndsOnTheFirstMoveWithoutThePanButton()
    {
        (TestCanvas canvas, Window window) = Show(MouseButton.Middle);
        try
        {
            window.MouseDown(Start, MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            double centerLon = canvas.Viewport.CenterLon;

            window.MouseMove(Start + Step, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(centerLon, canvas.Viewport.CenterLon);

            window.MouseMove(Start + Step + Step, RawInputModifiers.MiddleMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(centerLon, canvas.Viewport.CenterLon);
        }
        finally
        {
            window.MouseUp(Start, MouseButton.Middle);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void APanWhoseReleaseWasSwallowed_ThenAPanPressWithNoMove_PansFromTheNewPressPoint()
    {
        (TestCanvas canvas, Window window) = Show(MouseButton.Middle);
        try
        {
            // The first pan's release never arrives; the next press of the pan button comes elsewhere with no move between.
            window.MouseDown(Start, MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            var secondPress = new Point(200, 150);
            window.MouseDown(secondPress, MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            double latAtSecondPress = canvas.Viewport.CenterLat;
            double lonAtSecondPress = canvas.Viewport.CenterLon;

            window.MouseMove(secondPress + Step, RawInputModifiers.MiddleMouseButton);
            Dispatcher.UIThread.RunJobs();

            var expected = new MapViewport
            {
                CenterLat = latAtSecondPress,
                CenterLon = lonAtSecondPress,
                Zoom = canvas.Viewport.Zoom,
                PixelWidth = canvas.Viewport.PixelWidth,
                PixelHeight = canvas.Viewport.PixelHeight,
            };
            expected.Pan((float)Step.X, (float)Step.Y);
            Assert.Equal(expected.CenterLon, canvas.Viewport.CenterLon, 9);
            Assert.Equal(expected.CenterLat, canvas.Viewport.CenterLat, 9);
        }
        finally
        {
            window.MouseUp(Start, MouseButton.Middle);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void AnotherButtonPressedDuringAStalePan_EndsIt_SoAPanHeldMoveDoesNotJump()
    {
        (TestCanvas canvas, Window window) = Show(MouseButton.Middle);
        try
        {
            // The middle press starts a pan whose release never arrives; a left press then comes with the middle up.
            window.MouseDown(Start, MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            var leftPress = new Point(200, 150);
            window.MouseDown(leftPress, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            double lat = canvas.Viewport.CenterLat;
            double lon = canvas.Viewport.CenterLon;

            window.MouseMove(leftPress + Step, RawInputModifiers.MiddleMouseButton);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(lat, canvas.Viewport.CenterLat, 9);
            Assert.Equal(lon, canvas.Viewport.CenterLon, 9);
        }
        finally
        {
            window.MouseUp(Start, MouseButton.Middle);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
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

    private static (TestCanvas Canvas, Window Window) Show(MouseButton? panButton)
    {
        var canvas = new TestCanvas(panButton);
        canvas.Viewport.CenterLat = 37.72;
        canvas.Viewport.CenterLon = -122.22;
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (canvas, window);
    }

    private sealed class TestCanvas(MouseButton? panButton) : MapCanvasBase
    {
        protected override MouseButton PanButton => panButton ?? base.PanButton;

        protected override object? CreateRenderSnapshot() => null;

        protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot) { }
    }
}
