using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Regression tests for GitHub issue #365: applying a window profile to an already-open,
/// currently-minimized pop-out window resized it but left it minimized and buried —
/// <see cref="WindowGeometryHelper.ApplyGeometry"/> set <c>WindowState</c> without ever
/// activating the window. Profiles also could not record minimized state, and capturing a
/// minimized-from-maximized window lost its maximized flag.
/// </summary>
public class Issue365ProfileApplyRestoreTests
{
    private static (Window Window, WindowGeometryHelper Helper) NewShownWindow(string windowName)
    {
        var window = new Window();
        var helper = new WindowGeometryHelper(window, new UserPreferences(), windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, helper);
    }

    private static SavedWindowGeometry NormalGeometry() =>
        new()
        {
            X = 320,
            Y = 240,
            Width = 800,
            Height = 500,
            IsMaximized = false,
            ScreenIndex = 0,
            IsTopmost = false,
        };

    [AvaloniaFact]
    public void ApplyGeometry_OnMinimizedWindow_RestoresAndActivates()
    {
        var (window, helper) = NewShownWindow("Issue365RestoreTest");
        try
        {
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();

            var activated = false;
            window.Activated += (_, _) => activated = true;

            helper.ApplyGeometry(NormalGeometry());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(800, window.Width);
            Assert.Equal(500, window.Height);
            Assert.True(activated);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ApplyGeometry_OnMinimizedWindow_WithMaximizedGeometry_Maximizes()
    {
        var (window, helper) = NewShownWindow("Issue365MaximizeTest");
        try
        {
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();

            var geo = NormalGeometry();
            geo.IsMaximized = true;
            helper.ApplyGeometry(geo);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Maximized, window.WindowState);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ApplyGeometry_WithMinimizedGeometry_MinimizesWithoutActivating()
    {
        var (window, helper) = NewShownWindow("Issue365MinimizeTest");
        try
        {
            var activated = false;
            window.Activated += (_, _) => activated = true;

            var geo = NormalGeometry();
            geo.IsMinimized = true;
            helper.ApplyGeometry(geo);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Minimized, window.WindowState);
            Assert.False(activated);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SaveGeometry_MinimizedFromMaximized_RecordsBothFlags()
    {
        const string windowName = "Issue365CaptureTest";
        var (window, helper) = NewShownWindow(windowName);
        try
        {
            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();

            helper.FlushSavedGeometry();

            var saved = new UserPreferences().GetWindowGeometry(windowName);
            Assert.NotNull(saved);
            Assert.True(saved.IsMinimized);
            Assert.True(saved.IsMaximized);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SaveGeometry_MinimizedFromNormal_RecordsMinimizedOnly()
    {
        const string windowName = "Issue365CaptureNormalTest";
        var (window, helper) = NewShownWindow(windowName);
        try
        {
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();

            helper.FlushSavedGeometry();

            var saved = new UserPreferences().GetWindowGeometry(windowName);
            Assert.NotNull(saved);
            Assert.True(saved.IsMinimized);
            Assert.False(saved.IsMaximized);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
