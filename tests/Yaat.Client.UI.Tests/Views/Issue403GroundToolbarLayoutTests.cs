using Avalonia;
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

/// <summary>
/// Layout coverage for issue #403: the Ground View controls bar was a non-wrapping StackPanel
/// overlaid on the top-right of the canvas, while the renderer paints the weather readout
/// ("OAK 29.92 00000KT") at a fixed (10, 20) on that same canvas. In a narrow window the bar's
/// translucent buttons slid over the readout, and narrower still the bar pinned left and its
/// trailing buttons were clipped off-screen. The bar now docks above the canvas inside a
/// horizontal scroller, so no button can ever share pixels with the canvas and every button
/// stays reachable at any width.
/// </summary>
public class Issue403GroundToolbarLayoutTests
{
    private const double Epsilon = 0.5;

    [AvaloniaFact]
    public void ToolbarButtons_NeverOverlapCanvas_AtWideAndNarrowWidths()
    {
        var (window, view) = ShowGroundView(800);
        try
        {
            AssertNoButtonOverlapsCanvas(view);

            window.Width = 480;
            PumpLayout(window);
            AssertNoButtonOverlapsCanvas(view);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NarrowWidth_ToolbarScrollsInsteadOfClipping()
    {
        var (window, view) = ShowGroundView(480);
        try
        {
            var canvas = view.FindControl<GroundCanvas>("Canvas");
            Assert.NotNull(canvas);
            Assert.True(canvas.Bounds.Height > 100, $"Canvas collapsed to {canvas.Bounds.Height:F1}px tall");

            var scroller = view.FindControl<ScrollViewer>("ToolbarScroller");
            Assert.NotNull(scroller);
            Assert.True(
                scroller.Extent.Width > scroller.Viewport.Width + Epsilon,
                $"Toolbar extent {scroller.Extent.Width:F1} should exceed the {scroller.Viewport.Width:F1} viewport so the tail can be scrolled into view"
            );

            var reset = view.FindControl<Button>("ResetButton");
            Assert.NotNull(reset);
            Assert.True(reset.Bounds.Width > 0, "RESET button was clipped to zero width");
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertNoButtonOverlapsCanvas(GroundView view)
    {
        var canvas = view.FindControl<GroundCanvas>("Canvas");
        Assert.NotNull(canvas);
        var canvasOrigin = canvas.TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(canvasOrigin);
        var canvasRect = new Rect(canvasOrigin.Value, canvas.Bounds.Size);

        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("gnd-filter")).ToList();
        Assert.NotEmpty(buttons);

        foreach (var button in buttons)
        {
            var origin = button.TranslatePoint(new Point(0, 0), view);
            Assert.NotNull(origin);
            var rect = new Rect(origin.Value, button.Bounds.Size).Deflate(Epsilon);
            Assert.False(
                canvasRect.Intersects(rect),
                $"Button '{button.Content}' at {rect} overlaps the ground canvas at {canvasRect} (view width {view.Bounds.Width:F0})"
            );
        }
    }

    private static (Window Window, GroundView View) ShowGroundView(double width)
    {
        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask) { Layout = MinimalLayout() };
        var view = new GroundView { DataContext = vm };
        var window = new Window
        {
            Width = width,
            Height = 600,
            Content = view,
        };
        window.Show();
        PumpLayout(window);
        return (window, view);
    }

    private static void PumpLayout(Window window)
    {
        // Headless Avalonia needs a few measure/arrange + dispatcher cycles before
        // size changes flow to nested controls; pump until stable.
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static GroundLayoutDto MinimalLayout()
    {
        // The toolbar is only visible once a layout is loaded; four corner nodes are enough.
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
