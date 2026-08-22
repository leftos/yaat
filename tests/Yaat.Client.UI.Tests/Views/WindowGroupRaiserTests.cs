using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Headless caveats (issue #392): SetTopmost does not change the headless Z-order, so the
// visual raise itself is not observable — these tests assert on the GroupRaised hook and
// the candidate/order computation instead. Headless posts Activated at Input priority
// (RunJobs required after Show), and Deactivated only fires on Hide().
public class WindowGroupRaiserTests : IDisposable
{
    public WindowGroupRaiserTests()
    {
        WindowGroupRaiser.ResetForTest();
    }

    public void Dispose()
    {
        WindowGroupRaiser.ResetForTest();
    }

    [AvaloniaFact]
    public void ComputeRaiseOrder_ExcludesMinimizedPinnedHiddenOwned_ActivatedLast()
    {
        var activated = new Window();
        var other = new Window();
        var minimized = new Window();
        var pinned = new Window();
        var hidden = new Window();
        var owned = new Window();

        activated.Show();
        other.Show();
        minimized.Show();
        minimized.WindowState = WindowState.Minimized;
        pinned.Show();
        pinned.Topmost = true;
        owned.Show(activated);
        Dispatcher.UIThread.RunJobs();

        var mru = new List<Window> { other, minimized, pinned, hidden, owned, activated };
        var order = WindowGroupRaiser.ComputeRaiseOrder(mru, activated);

        Assert.Equal([other, activated], order);
    }

    [AvaloniaFact]
    public void ComputeRaiseOrder_PinnedActivatedWindow_IsNotPulsed()
    {
        var activated = new Window();
        var other = new Window();
        activated.Show();
        activated.Topmost = true;
        other.Show();
        Dispatcher.UIThread.RunJobs();

        var order = WindowGroupRaiser.ComputeRaiseOrder([other, activated], activated);

        Assert.Equal([other], order);
    }

    [AvaloniaFact]
    public void Raises_WhenFocusReturnsFromOutside_NotOnIntraAppHandoff()
    {
        var prefs = new UserPreferences();
        prefs.SetRaiseWindowsTogether(true);
        var a = new Window();
        var b = new Window();
        WindowGroupRaiser.Attach(a, prefs);
        WindowGroupRaiser.Attach(b, prefs);
        var raises = 0;
        WindowGroupRaiser.GroupRaised += _ => raises++;

        a.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, raises);

        // Intra-app handoff: group already active, no re-raise.
        b.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, raises);

        // Focus leaves the app entirely (headless: Hide is the only Deactivated
        // trigger); the posted check marks the group inactive.
        a.Hide();
        b.Hide();
        Dispatcher.UIThread.RunJobs();

        // Focus returns → raise again.
        a.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, raises);
    }

    [AvaloniaFact]
    public void Suspended_ActivationDoesNotRaise()
    {
        var prefs = new UserPreferences();
        prefs.SetRaiseWindowsTogether(true);
        var window = new Window();
        WindowGroupRaiser.Attach(window, prefs);
        var raises = 0;
        WindowGroupRaiser.GroupRaised += _ => raises++;

        WindowGroupRaiser.IsSuspended = true;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            WindowGroupRaiser.IsSuspended = false;
        }

        Assert.Equal(0, raises);
    }

    [AvaloniaFact]
    public void PreferenceOff_ActivationDoesNotRaise()
    {
        var prefs = new UserPreferences();
        try
        {
            prefs.SetRaiseWindowsTogether(false);
            var window = new Window();
            WindowGroupRaiser.Attach(window, prefs);
            var raises = 0;
            WindowGroupRaiser.GroupRaised += _ => raises++;

            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, raises);
        }
        finally
        {
            // Tests share one per-process preferences.json; restore the default.
            prefs.SetRaiseWindowsTogether(true);
        }
    }

    [AvaloniaFact]
    public void ClosedWindow_IsDetached()
    {
        var prefs = new UserPreferences();
        prefs.SetRaiseWindowsTogether(true);
        var a = new Window();
        var b = new Window();
        WindowGroupRaiser.Attach(a, prefs);
        WindowGroupRaiser.Attach(b, prefs);

        a.Show();
        b.Show();
        Dispatcher.UIThread.RunJobs();
        b.Close();
        a.Hide();
        Dispatcher.UIThread.RunJobs();

        Window? raisedFor = null;
        WindowGroupRaiser.GroupRaised += w => raisedFor = w;
        a.Show();
        Dispatcher.UIThread.RunJobs();

        // The raise still fires for the surviving window and the closed one is
        // no longer part of the computation (ComputeRaiseOrder would throw in
        // SortWindowsByZOrder's PlatformImpl null check otherwise and fall back).
        Assert.Equal(a, raisedFor);
    }
}

public class UserPreferencesRaiseWindowsTogetherTests
{
    [Fact]
    public void RaiseWindowsTogether_DefaultsOn_AndPersists()
    {
        var prefs = new UserPreferences();
        try
        {
            Assert.True(prefs.RaiseWindowsTogether);

            prefs.SetRaiseWindowsTogether(false);
            Assert.False(new UserPreferences().RaiseWindowsTogether);
        }
        finally
        {
            // Tests share one per-process preferences.json; restore the default.
            prefs.SetRaiseWindowsTogether(true);
        }

        Assert.True(new UserPreferences().RaiseWindowsTogether);
    }
}
