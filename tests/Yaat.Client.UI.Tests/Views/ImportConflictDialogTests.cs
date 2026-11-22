using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The import id-conflict dialog: New id, Keep id and Skip each answer their choice; the "Apply to the remaining N files"
/// checkbox shows, ticked, only when files remain; Keep id's wording follows what holds the id; and closing the dialog
/// without a button answers Skip.
/// </summary>
public class ImportConflictDialogTests
{
    private const string Id = "01H06NVK7VN8BS7MCDXHKJZ7MQ";

    [AvaloniaTheory]
    [InlineData("NewIdButton", ImportIdChoice.NewId)]
    [InlineData("KeepIdButton", ImportIdChoice.KeepId)]
    [InlineData("SkipButton", ImportIdChoice.Skip)]
    public async Task EachButton_AnswersItsChoice(string buttonName, ImportIdChoice expected)
    {
        Window owner = ShowOwner();
        try
        {
            var dialog = new ImportConflictDialog(Question(ScenarioIdConflict.WorkingCopy, remaining: 0));
            Task<ImportConflictAnswer> pending = dialog.ShowAsync(owner);
            Dispatcher.UIThread.RunJobs();

            dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new ImportConflictAnswer(expected, ApplyToRemaining: false), await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void NoFilesRemaining_HidesTheCheckbox()
    {
        var dialog = new ImportConflictDialog(Question(ScenarioIdConflict.WorkingCopy, remaining: 0));

        Assert.False(dialog.FindControl<CheckBox>("ApplyToRemainingCheckBox")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task TwoFilesRemaining_ShowsTheCheckboxTicked_AndTheAnswerAppliesToThem()
    {
        Window owner = ShowOwner();
        try
        {
            var dialog = new ImportConflictDialog(Question(ScenarioIdConflict.Mirror, remaining: 2));
            CheckBox box = dialog.FindControl<CheckBox>("ApplyToRemainingCheckBox")!;

            Assert.True(box.IsVisible);
            Assert.True(box.IsChecked);
            Assert.Equal("Apply to the remaining 2 files", box.Content);

            Task<ImportConflictAnswer> pending = dialog.ShowAsync(owner);
            Dispatcher.UIThread.RunJobs();
            dialog.FindControl<Button>("KeepIdButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new ImportConflictAnswer(ImportIdChoice.KeepId, ApplyToRemaining: true), await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(ScenarioIdConflict.WorkingCopy, "replaces your working copy")]
    [InlineData(ScenarioIdConflict.Both, "replaces your working copy")]
    [InlineData(ScenarioIdConflict.Mirror, "becomes the working copy of this server scenario")]
    public void KeepIdWording_FollowsTheClash(ScenarioIdConflict conflict, string wording)
    {
        var dialog = new ImportConflictDialog(Question(conflict, remaining: 0));

        Assert.Contains(wording, dialog.FindControl<TextBlock>("KeepIdText")!.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ClosingWithoutAButton_AnswersSkip()
    {
        Window owner = ShowOwner();
        try
        {
            var dialog = new ImportConflictDialog(Question(ScenarioIdConflict.WorkingCopy, remaining: 2));
            Task<ImportConflictAnswer> pending = dialog.ShowAsync(owner);
            Dispatcher.UIThread.RunJobs();

            dialog.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new ImportConflictAnswer(ImportIdChoice.Skip, ApplyToRemaining: false), await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Escape_AnswersSkip()
    {
        Window owner = ShowOwner();
        try
        {
            var dialog = new ImportConflictDialog(Question(ScenarioIdConflict.WorkingCopy, remaining: 0));
            Task<ImportConflictAnswer> pending = dialog.ShowAsync(owner);
            Dispatcher.UIThread.RunJobs();

            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new ImportConflictAnswer(ImportIdChoice.Skip, ApplyToRemaining: false), await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    private static ImportConflictQuestion Question(ScenarioIdConflict conflict, int remaining) =>
        new("oak_gnd_1.json", Id, "OAK_GND - Oakland Ground 1", conflict, remaining);

    private static Window ShowOwner()
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }
}
