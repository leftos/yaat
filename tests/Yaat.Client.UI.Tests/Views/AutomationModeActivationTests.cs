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

    private static SavedWindowGeometry Geometry(bool isTopmost) =>
        new()
        {
            X = 200,
            Y = 150,
            Width = 800,
            Height = 500,
            IsMaximized = false,
            ScreenIndex = 0,
            IsTopmost = isTopmost,
        };

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
    public void RestoreAndActivate_InAutomationMode_RestoresButDoesNotActivate()
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

            window.RestoreAndActivate();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Normal, window.WindowState);
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
