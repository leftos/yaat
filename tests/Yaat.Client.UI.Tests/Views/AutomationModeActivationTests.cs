using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Automation mode (<c>YAAT_AUTOMATION=1</c>) must never bring a window forward: the client's own
/// <c>Activate()</c> calls and <c>Topmost</c> pulses are suppressed, and windows show
/// never-activated. The assembly runs its tests serially (<c>xunit.runner.json</c>), so flipping
/// the process-wide flag here cannot leak into a concurrently running test; each test restores it.
/// </summary>
public class AutomationModeActivationTests : IDisposable
{
    public AutomationModeActivationTests()
    {
        WindowGroupRaiser.ResetForTest();
        AutomationMode.IsEnabled = true;
    }

    public void Dispose()
    {
        AutomationMode.IsEnabled = false;
        WindowGroupRaiser.ResetForTest();
    }

    private static SavedWindowGeometry Geometry(bool isTopmost) => Geometry(isTopmost, isMaximized: false);

    private static SavedWindowGeometry Geometry(bool isTopmost, bool isMaximized) =>
        new()
        {
            X = 200,
            Y = 150,
            Width = 800,
            Height = 500,
            IsMaximized = isMaximized,
            ScreenIndex = 0,
            IsTopmost = isTopmost,
        };

    [AvaloniaFact]
    public void AutomationMode_SavedMaximizedGeometry_OpensNormal()
    {
        const string windowName = "AutomationSavedMaximizedTest";
        var prefs = new UserPreferences();
        prefs.SetWindowGeometry(windowName, Geometry(isTopmost: false, isMaximized: true));
        var window = new Window();
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        try
        {
            // A window shown Maximized goes up with SW_SHOWMAXIMIZED, which activates it.
            Assert.Equal(WindowState.Normal, window.WindowState);

            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(800.0, window.Width);
            Assert.Equal(500.0, window.Height);

            // A profile apply of a maximized geometry keeps the visible window Normal too.
            helper.ApplyGeometry(Geometry(isTopmost: false, isMaximized: true));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Normal, window.WindowState);

            // The user's saved maximized state survives an automation session that never applied it.
            helper.FlushSavedGeometry();
            Assert.True(prefs.GetWindowGeometry(windowName)?.IsMaximized);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NormalMode_SavedMaximizedGeometry_OpensMaximized()
    {
        AutomationMode.IsEnabled = false;
        const string windowName = "NormalSavedMaximizedTest";
        var prefs = new UserPreferences();
        prefs.SetWindowGeometry(windowName, Geometry(isTopmost: false, isMaximized: true));
        var window = new Window();
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        try
        {
            Assert.Equal(WindowState.Maximized, window.WindowState);

            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Maximized, window.WindowState);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AutomationMode_ApplyGeometry_LeavesMinimizedOrMaximizedWindowStateAlone()
    {
        WindowState[] states = [WindowState.Minimized, WindowState.Maximized];
        foreach (WindowState state in states)
        {
            var prefs = new UserPreferences();
            var window = new Window { ShowActivated = false };
            var helper = new WindowGeometryHelper(window, prefs, $"AutomationApplyOnto{state}Test", defaultWidth: 300, defaultHeight: 200);
            helper.Restore();
            window.Show();
            try
            {
                window.WindowState = state;
                Dispatcher.UIThread.RunJobs();

                // Outside automation mode a profile apply drops the window to Normal before placing it.
                helper.ApplyGeometry(Geometry(isTopmost: false));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(state, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void ApplyGeometry_InAutomationMode_DoesNotActivate()
    {
        const string windowName = "AutomationApplyGeometryTest";
        var prefs = new UserPreferences();
        var window = new Window();
        int activations = 0;
        window.Activated += (_, _) => activations++;
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        try
        {
            bool showActivatedFromHelper = window.ShowActivated;

            // Keep the show itself inactive so only ApplyGeometry's own activation can be counted.
            window.ShowActivated = false;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            int activationsAfterShow = activations;

            helper.ApplyGeometry(Geometry(isTopmost: false));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(activationsAfterShow, activations);
            Assert.False(window.IsActive);
            Assert.False(showActivatedFromHelper);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AutomationMode_RestoreAndActivate_LeavesWindowStateAlone()
    {
        var window = new Window { ShowActivated = false };
        int activations = 0;
        window.Activated += (_, _) => activations++;
        window.Show();
        try
        {
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            int activationsBefore = activations;

            // Un-minimizing a visible window sends SW_RESTORE and then SetForegroundWindow, so
            // automation mode leaves a minimized window minimized.
            window.RestoreAndActivate();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Minimized, window.WindowState);
            Assert.Equal(activationsBefore, activations);
            Assert.False(window.IsActive);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WindowGroupRaiser_InAutomationMode_NeverSetsTopmost()
    {
        var prefs = new UserPreferences();
        prefs.SetRaiseWindowsTogether(true);
        var a = new Window { ShowActivated = false };
        var b = new Window { ShowActivated = false };
        WindowGroupRaiser.Attach(a, prefs);
        WindowGroupRaiser.Attach(b, prefs);
        int topmostSets = 0;
        void CountTopmost(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if ((e.Property == Window.TopmostProperty) && (e.NewValue is true))
            {
                topmostSets++;
            }
        }
        a.PropertyChanged += CountTopmost;
        b.PropertyChanged += CountTopmost;
        a.Show();
        b.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Focus arriving from outside the app is what triggers a group raise.
            a.Activate();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, topmostSets);
        }
        finally
        {
            a.Close();
            b.Close();
        }
    }

    [AvaloniaFact]
    public void GeometryTopmostPreference_InAutomationMode_IsNotApplied()
    {
        const string windowName = "AutomationTopmostTest";
        var prefs = new UserPreferences();
        prefs.SetWindowGeometry(windowName, Geometry(isTopmost: true));
        var window = new Window();
        var helper = new WindowGeometryHelper(window, prefs, windowName, defaultWidth: 300, defaultHeight: 200);
        helper.Restore();
        try
        {
            Assert.False(window.Topmost);

            window.Show();
            Dispatcher.UIThread.RunJobs();
            helper.ApplyGeometry(Geometry(isTopmost: true));
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Topmost);

            prefs.SetWindowTopmost(windowName, true);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Topmost);

            // The user's saved pin survives an automation session that never applied it.
            helper.FlushSavedGeometry();
            Assert.True(prefs.GetWindowGeometry(windowName)?.IsTopmost);
        }
        finally
        {
            window.Close();
        }
    }
}
