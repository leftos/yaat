using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Every client dialog opens through <see cref="DialogPresenter"/>. In automation mode it shows the
/// dialog never-activated and non-modal, disabling the owner by hand so the owner still cannot be
/// used while the dialog is open; outside it the helper is plain <c>ShowDialog</c>. The assembly
/// runs its tests serially, so flipping the process-wide flag cannot leak; each test restores it.
/// </summary>
public class DialogPresenterTests : IDisposable
{
    public void Dispose() => AutomationMode.IsEnabled = false;

    private static Window ShowOwner()
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }

    [AvaloniaFact]
    public async Task AutomationMode_ResultDialog_ReturnsValueOnClose_AndOwnerReenabled()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        var dialog = new Window();
        try
        {
            Task<string?> pending = DialogPresenter.ShowModalAsync<string?>(dialog, owner);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dialog.IsVisible);
            Assert.False(owner.IsEnabled);
            Assert.True(dialog.IsEffectivelyEnabled);
            Assert.False(pending.IsCompleted);

            DialogPresenter.Close(dialog, "KSFO");
            Dispatcher.UIThread.RunJobs();

            Assert.True(pending.IsCompleted);
            Assert.Equal("KSFO", await pending);
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_Dialog_NeverActivatesDialogOrOwner()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        var dialog = new Window();
        int ownerActivations = 0;
        int dialogActivations = 0;
        owner.Activated += (_, _) => ownerActivations++;
        dialog.Activated += (_, _) => dialogActivations++;
        try
        {
            Task pending = DialogPresenter.ShowModalAsync(dialog, owner);
            Dispatcher.UIThread.RunJobs();

            Assert.False(dialog.IsActive);
            Assert.False(owner.IsActive);

            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            await pending;

            Assert.Equal(0, dialogActivations);
            Assert.Equal(0, ownerActivations);
            Assert.False(owner.IsActive);
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_DialogClosedWithoutResult_ReturnsDefault()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        var dialog = new Window();
        try
        {
            Task<string?> pending = DialogPresenter.ShowModalAsync<string?>(dialog, owner);
            Dispatcher.UIThread.RunJobs();

            dialog.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(pending.IsCompleted);
            Assert.Null(await pending);
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task NormalMode_UsesShowDialog()
    {
        AutomationMode.IsEnabled = false;
        Window owner = ShowOwner();
        var dialog = new Window();
        try
        {
            Task<string?> pending = DialogPresenter.ShowModalAsync<string?>(dialog, owner);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dialog.IsVisible);
            Assert.True(owner.IsEnabled);

            DialogPresenter.Close(dialog, "KOAK");
            Dispatcher.UIThread.RunJobs();

            Assert.True(pending.IsCompleted);
            Assert.Equal("KOAK", await pending);
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_OverlappingDialogs_OwnerReenabledOnlyAfterLastCloses()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        var first = new Window();
        var second = new Window();
        try
        {
            Task firstPending = DialogPresenter.ShowModalAsync(first, owner);
            Task secondPending = DialogPresenter.ShowModalAsync(second, owner);
            Dispatcher.UIThread.RunJobs();

            first.Close();
            Dispatcher.UIThread.RunJobs();
            await firstPending;
            Assert.False(owner.IsEnabled);

            second.Close();
            Dispatcher.UIThread.RunJobs();
            await secondPending;
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_OwnerAlreadyDisabled_StaysDisabledAfterClose()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        owner.IsEnabled = false;
        var dialog = new Window();
        try
        {
            Task pending = DialogPresenter.ShowModalAsync(dialog, owner);
            Dispatcher.UIThread.RunJobs();

            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            await pending;

            Assert.False(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_ShowThrows_OwnerUnlockedAndNextDialogWorks()
    {
        AutomationMode.IsEnabled = true;
        var owner = new Window { ShowActivated = false };
        var failing = new Window();

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = DialogPresenter.ShowModalAsync(failing, owner);
        });
        Assert.True(owner.IsEnabled);

        owner.Show();
        Dispatcher.UIThread.RunJobs();
        var dialog = new Window();
        try
        {
            Task pending = DialogPresenter.ShowModalAsync(dialog, owner);
            Dispatcher.UIThread.RunJobs();
            Assert.False(owner.IsEnabled);

            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            await pending;

            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_ResultOfWrongType_FaultsWithInvalidCast_AndOwnerReenabled()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        var dialog = new Window();
        try
        {
            Task<string?> pending = DialogPresenter.ShowModalAsync<string?>(dialog, owner);
            Dispatcher.UIThread.RunJobs();

            DialogPresenter.Close(dialog, 42);
            Dispatcher.UIThread.RunJobs();

            await Assert.ThrowsAsync<InvalidCastException>(() => pending);
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_ProgrammaticOwnerClose_ClosesOwnerAndDialog_AndReleasesLock()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        var dialog = new Window();
        int ownerClosedCount = 0;
        owner.Closed += (_, _) => ownerClosedCount++;
        try
        {
            Task pending = DialogPresenter.ShowModalAsync(dialog, owner);
            Dispatcher.UIThread.RunJobs();
            Assert.False(owner.IsEnabled);

            owner.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, ownerClosedCount);
            Assert.False(owner.IsVisible);
            Assert.False(dialog.IsVisible);
            Assert.True(pending.IsCompleted);
            await pending;
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    // Headless Avalonia closes a window only programmatically, so the user's title-bar close is
    // covered through the decision the owner's Closing handler makes.
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, false, true)]
    [InlineData(WindowCloseReason.WindowClosing, true, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, false, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false, false)]
    [InlineData(WindowCloseReason.OSShutdown, false, false)]
    [InlineData(WindowCloseReason.Undefined, false, false)]
    public void ShouldCancelOwnerClose_CancelsOnlyTheUsersOwnClose(WindowCloseReason reason, bool isProgrammatic, bool expected) =>
        Assert.Equal(expected, DialogPresenter.ShouldCancelOwnerClose(reason, isProgrammatic));
}
