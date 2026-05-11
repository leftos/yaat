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
}
