using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

public class WindowGeometryHelperTests
{
    [AvaloniaFact]
    public void ClosingMinimizedWindow_PreservesRestoredGeometryAfterIconicOriginReport()
    {
        const string windowName = "GeometryTest";
        var prefs = new UserPreferences();
        prefs.SetWindowGeometry(
            windowName,
            new SavedWindowGeometry
            {
                X = 240,
                Y = 180,
                Width = 900,
                Height = 600,
                IsMaximized = false,
                ScreenIndex = 0,
                IsTopmost = false,
            }
        );

        var window = new Window();
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Position = new Avalonia.PixelPoint(0, 0);
        Dispatcher.UIThread.RunJobs();
        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var saved = new UserPreferences().GetWindowGeometry(windowName);
        Assert.NotNull(saved);
        Assert.Equal(240, saved.X);
        Assert.Equal(180, saved.Y);
        Assert.Equal(900, saved.Width);
        Assert.Equal(600, saved.Height);
        Assert.False(saved.IsMaximized);
    }

    [AvaloniaFact]
    public void FlushSavedGeometry_PersistsCurrentGeometry_WithoutClosingWindow()
    {
        const string windowName = "FlushTest";
        var prefs = new UserPreferences();

        var window = new Window();
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Position = new Avalonia.PixelPoint(150, 75);
        window.Width = 720;
        window.Height = 480;
        Dispatcher.UIThread.RunJobs();

        helper.FlushSavedGeometry();

        // Window stays open — simulating Velopack restart that never fires the
        // window-closing pipeline. Reload prefs from disk to verify the flush
        // wrote through.
        var saved = new UserPreferences().GetWindowGeometry(windowName);
        Assert.NotNull(saved);
        Assert.Equal(150, saved.X);
        Assert.Equal(75, saved.Y);
        Assert.Equal(720, saved.Width);
        Assert.Equal(480, saved.Height);

        Assert.True(window.IsVisible);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void FlushAllSavedGeometries_PersistsEveryRegisteredHelper()
    {
        const string firstName = "FlushAllTestA";
        const string secondName = "FlushAllTestB";
        var prefs = new UserPreferences();

        var firstWindow = new Window();
        var firstHelper = new WindowGeometryHelper(firstWindow, prefs, firstName, defaultWidth: 300, defaultHeight: 200);
        firstHelper.Restore();
        firstWindow.Show();

        var secondWindow = new Window();
        var secondHelper = new WindowGeometryHelper(secondWindow, prefs, secondName, defaultWidth: 300, defaultHeight: 200);
        secondHelper.Restore();
        secondWindow.Show();
        Dispatcher.UIThread.RunJobs();

        firstWindow.Position = new Avalonia.PixelPoint(50, 60);
        firstWindow.Width = 800;
        firstWindow.Height = 600;
        secondWindow.Position = new Avalonia.PixelPoint(700, 200);
        secondWindow.Width = 1024;
        secondWindow.Height = 768;
        Dispatcher.UIThread.RunJobs();

        WindowGeometryHelper.FlushAllSavedGeometries();

        var reloaded = new UserPreferences();
        var savedFirst = reloaded.GetWindowGeometry(firstName);
        var savedSecond = reloaded.GetWindowGeometry(secondName);

        Assert.NotNull(savedFirst);
        Assert.Equal(50, savedFirst.X);
        Assert.Equal(800, savedFirst.Width);

        Assert.NotNull(savedSecond);
        Assert.Equal(700, savedSecond.X);
        Assert.Equal(1024, savedSecond.Width);

        firstWindow.Close();
        secondWindow.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
