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
    public void ClosingMinimizedWindow_PreservesRestoredGeometryAfterWindowsIconicSentinel()
    {
        // On Windows a minimized window reports Position (-32000,-32000); that sentinel
        // must never be captured as the window's normal geometry (issue #361).
        const string windowName = "GeometryIconicSentinelTest";
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

        window.Position = new Avalonia.PixelPoint(-32000, -32000);
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

    // A closed window has no platform surface, and Avalonia's Window.Position getter falls back to
    // PixelPoint.Origin while Width/Height keep their values — so a save taken after close persists
    // (0,0) with the size intact. That is byte-for-byte the corruption reported in issue #408:
    // (-7,0,610,500) became (0,0,610,500) in preferences.json, and every later launch restored the
    // window offset from where the user put it.
    [AvaloniaFact]
    public void SaveAfterClose_DoesNotPersistPlatformOriginFallback()
    {
        const string windowName = "SaveAfterCloseTest";
        var prefs = new UserPreferences();
        prefs.SetWindowGeometry(windowName, EdgeSnappedGeometry());

        var window = new Window();
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        helper.FlushSavedGeometry();

        var saved = new UserPreferences().GetWindowGeometry(windowName);
        Assert.NotNull(saved);
        Assert.Equal(-7, saved.X);
        Assert.Equal(0, saved.Y);
        Assert.Equal(610, saved.Width);
        Assert.Equal(500, saved.Height);
    }

    // The debounced auto-save is armed before the window is shown (applying Topmost during the
    // restore schedules it) and runs at the dispatcher's Normal priority, while the post-open
    // drift verify is posted at Background. On a slow cold start the save wins and persists
    // whatever the platform currently reports. Until the verify has run, the geometry the restore
    // applied is the authoritative one (#408).
    [AvaloniaFact]
    public void SaveBeforeStartupVerify_PersistsRestoredGeometry()
    {
        const string windowName = "SaveBeforeVerifyTest";
        var prefs = new UserPreferences();
        prefs.SetWindowGeometry(windowName, EdgeSnappedGeometry());

        var window = new Window();
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        window.Show();

        // Something shoves the window before the queued verify gets a dispatcher slot.
        window.Position = new Avalonia.PixelPoint(0, 0);

        helper.FlushSavedGeometry();

        var saved = new UserPreferences().GetWindowGeometry(windowName);
        Assert.NotNull(saved);
        Assert.Equal(-7, saved.X);
        Assert.Equal(610, saved.Width);

        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // Avalonia closes owned windows through Window.CloseInternal(), which disposes the child's
    // platform surface directly. With ClosingBehavior.OwnerWindowOnly the child never sees
    // Closing, so the helper neither saves, unregisters, nor stops its auto-save timer — it stays
    // in the process-wide registry pointing at a dead window, and the next flush writes the
    // Position fallback over good geometry.
    [AvaloniaFact]
    public void OwnedWindowClosedWithoutClosingEvent_DoesNotCorruptSavedGeometry()
    {
        const string windowName = "OwnedChildCloseTest";
        var prefs = new UserPreferences();
        prefs.SetWindowGeometry(windowName, EdgeSnappedGeometry());

        var owner = new Window();
        owner.Show();

        var child = new Window();
        var helper = new WindowGeometryHelper(child, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        child.Show(owner);
        Dispatcher.UIThread.RunJobs();

        helper.FlushSavedGeometry();

        owner.ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        owner.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(helper, WindowGeometryHelper.GetActiveHelpers());

        // Flush this helper only. FlushAllSavedGeometries would drag in helpers left registered by
        // other tests, and each writes its own UserPreferences snapshot over the shared file.
        helper.FlushSavedGeometry();

        var saved = new UserPreferences().GetWindowGeometry(windowName);
        Assert.NotNull(saved);
        Assert.Equal(-7, saved.X);
        Assert.Equal(610, saved.Width);
    }

    // An edge-snapped window: Windows' invisible resize border puts the frame origin just outside
    // the work area, so the saved X is negative even though the window looks flush against the
    // screen edge. The only shape in which the (0,0) fallback is distinguishable from a real read.
    private static SavedWindowGeometry EdgeSnappedGeometry() =>
        new()
        {
            X = -7,
            Y = 0,
            Width = 610,
            Height = 500,
            IsMaximized = false,
            ScreenIndex = 0,
            IsTopmost = false,
        };
}
