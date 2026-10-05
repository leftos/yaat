using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using MsBox.Avalonia.Windows;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Every message box opens through <see cref="MessageBoxPresenter"/>, which hosts MessageBox.Avalonia's
/// view in its window and opens that window through <see cref="DialogPresenter"/>. In automation mode the
/// box shows never-activated and non-modal with its owner disabled; outside it the box is a modal dialog.
/// The assembly runs its tests serially, so flipping the process-wide flag cannot leak; each test restores it.
/// </summary>
public class MessageBoxPresenterTests : IDisposable
{
    public void Dispose() => AutomationMode.IsEnabled = false;

    private static Window ShowOwner()
    {
        var owner = new Window
        {
            Width = 300,
            Height = 200,
            ShowActivated = false,
        };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }

    [AvaloniaFact]
    public async Task AutomationMode_StandardBox_OpensWithoutActivating_AndReturnsTheChosenButton()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        int ownerActivations = 0;
        owner.Activated += (_, _) => ownerActivations++;
        try
        {
            Task<ButtonResult> pending = MessageBoxPresenter.ShowStandardAsync(owner, "Delete profile?", "Delete layout \"Two\"?", ButtonEnum.YesNo);
            MsBoxWindow box = OpenBox(owner);

            Assert.False(box.ShowActivated);
            Assert.False(box.IsActive);
            Assert.False(owner.IsEnabled);
            Assert.True(box.IsEffectivelyEnabled);
            Assert.Equal("Delete profile?", box.Title);
            Assert.False(pending.IsCompleted);

            Click(box, "Yes");

            Assert.Equal(ButtonResult.Yes, await pending);
            Assert.True(owner.IsEnabled);
            Assert.Equal(0, ownerActivations);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_CustomBox_OpensWithoutActivating_AndReturnsTheChosenButtonName()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        try
        {
            Task<string> pending = MessageBoxPresenter.ShowCustomAsync(owner, ImportChoiceParams());
            MsBoxWindow box = OpenBox(owner);

            Assert.False(box.ShowActivated);
            Assert.False(box.IsActive);
            Assert.False(owner.IsEnabled);

            Click(box, "Add to existing");

            Assert.Equal("Add to existing", await pending);
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AutomationMode_BoxClosedWithoutAButton_ReturnsNone_AndOwnerReenabled()
    {
        AutomationMode.IsEnabled = true;
        Window owner = ShowOwner();
        try
        {
            Task<ButtonResult> pending = MessageBoxPresenter.ShowStandardAsync(owner, "YAAT", "Open the release notes?", ButtonEnum.YesNo);
            MsBoxWindow box = OpenBox(owner);

            box.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(pending.IsCompleted);
            Assert.Equal(ButtonResult.None, await pending);
            Assert.True(owner.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task NormalMode_StandardBox_ShowsAsDialog_AndReturnsTheChosenButton()
    {
        AutomationMode.IsEnabled = false;
        Window owner = ShowOwner();
        try
        {
            Task<ButtonResult> pending = MessageBoxPresenter.ShowStandardAsync(owner, "YAAT", "Saved.", ButtonEnum.Ok);
            MsBoxWindow box = OpenBox(owner);

            // ShowDialog leaves the owner's IsEnabled alone; the platform blocks its input instead.
            Assert.True(box.ShowActivated);
            Assert.True(owner.IsEnabled);

            Click(box, "OK");

            Assert.Equal(ButtonResult.Ok, await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task NormalMode_BoxClosedWithoutAButton_ReturnsNone()
    {
        AutomationMode.IsEnabled = false;
        Window owner = ShowOwner();
        try
        {
            Task<ButtonResult> pending = MessageBoxPresenter.ShowStandardAsync(owner, "YAAT", "Open the release notes?", ButtonEnum.YesNo);
            MsBoxWindow box = OpenBox(owner);

            box.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(pending.IsCompleted);
            Assert.Equal(ButtonResult.None, await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    private static MessageBoxCustomParams ImportChoiceParams() =>
        new()
        {
            ButtonDefinitions = [new ButtonDefinition { Name = "Add to existing" }, new ButtonDefinition { Name = "Cancel", IsCancel = true }],
            ContentTitle = "Import Favorites",
            ContentMessage = "Add or replace?",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

    private static MsBoxWindow OpenBox(Window owner)
    {
        Dispatcher.UIThread.RunJobs();
        MsBoxWindow box = Assert.IsType<MsBoxWindow>(Assert.Single(owner.OwnedWindows));
        Assert.True(box.IsVisible);
        box.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return box;
    }

    // The standard view keeps every button in the tree and shows only the ones its ButtonEnum names.
    private static void Click(MsBoxWindow box, string content)
    {
        Button button = Assert.Single(
            box.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && string.Equals(b.Content as string, content, StringComparison.Ordinal)
        );
        Point? center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), box);
        Assert.NotNull(center);

        box.MouseDown(center.Value, MouseButton.Left);
        box.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
