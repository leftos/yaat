using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Ground view makes its surface the measuring tool's active one when its canvas takes the focus or a press,
/// so a typed <c>.rbl A B</c> places there.
/// </summary>
public class MeasureSurfaceWiringTests
{
    private static Window Show(MainViewModel main, Control view)
    {
        var window = new Window
        {
            Width = 800,
            Height = 600,
            DataContext = main,
            Content = view,
        };
        window.ShowAndRunLayout();
        return window;
    }

    private static void Press(Window window, Control canvas)
    {
        Point? inWindow = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // The canvas holds the focus already, so only the press can hand the surface back from the Scope.
    private static async Task AssertAPressTakesTheSurface(Func<MainViewModel, (Control View, Control Canvas)> create, RblView expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        (Control view, Control canvas) = create(main);
        Window window = Show(main, view);
        try
        {
            canvas.Focus();
            Dispatcher.UIThread.RunJobs();
            scope.ActivateMeasureSurface();
            Assert.Equal(RblView.Scope, main.ActiveMeasureView);

            Press(window, canvas);

            Assert.Equal(expected, main.ActiveMeasureView);
        }
        finally
        {
            window.Close();
            scope.Dispose();
        }
    }

    private static async Task AssertAFocusTakesTheSurface(Func<MainViewModel, (Control View, Control Canvas)> create, RblView expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        (Control view, Control canvas) = create(main);
        Window window = Show(main, view);
        try
        {
            scope.ActivateMeasureSurface();

            canvas.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(expected, main.ActiveMeasureView);
        }
        finally
        {
            window.Close();
            scope.Dispose();
        }
    }

    private static (Control View, Control Canvas) Ground(MainViewModel main)
    {
        var view = new GroundView { DataContext = main.Ground };
        return (view, view.FindControl<GroundCanvas>("Canvas")!);
    }

    [AvaloniaFact]
    public Task APressOnTheGroundCanvas_MakesTheGroundActive() => AssertAPressTakesTheSurface(Ground, RblView.Ground);

    [AvaloniaFact]
    public Task TheGroundCanvasTakingTheFocus_MakesTheGroundActive() => AssertAFocusTakesTheSurface(Ground, RblView.Ground);
}
