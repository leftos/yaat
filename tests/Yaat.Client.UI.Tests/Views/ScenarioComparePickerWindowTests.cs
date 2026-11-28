using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Compare picker window: the preselected sides show as the lists' selections, the same scenario on both sides
/// disables Compare and says "Pick two different scenarios.", Compare returns the picked pair, Cancel returns null.
/// </summary>
public class ScenarioComparePickerWindowTests
{
    [AvaloniaFact]
    public void ThePreselection_IsShown_AndCompareIsEnabled()
    {
        ScenarioComparePickerViewModel picker = Picker(Server("A1", "Alpha"));
        var window = new ScenarioComparePickerWindow(picker, new UserPreferences());

        Assert.Same(picker.SelectedMine, window.FindControl<ListBox>("MineList")!.SelectedItem);
        Assert.Equal("L1", picker.SelectedMine?.Id);
        Assert.Same(picker.SelectedTheirs, window.FindControl<ListBox>("TheirsList")!.SelectedItem);
        Assert.Equal("A1", picker.SelectedTheirs?.Id);
        Assert.True(window.FindControl<Button>("CompareButton")!.IsEnabled);
        Assert.False(window.FindControl<TextBlock>("SameText")!.IsVisible);
    }

    [AvaloniaFact]
    public void TheSameScenarioOnBothSides_DisablesCompare_AndSaysSo()
    {
        ScenarioComparePickerViewModel picker = Picker(Copy("L1", "Lima"));
        var window = new ScenarioComparePickerWindow(picker, new UserPreferences());
        ListBox theirs = window.FindControl<ListBox>("TheirsList")!;

        theirs.SelectedItem = picker.TheirsOptions.Single(s => (s.Kind == ScenarioCompareSideKind.WorkingCopy) && (s.Id == "L1"));
        Dispatcher.UIThread.RunJobs();

        TextBlock same = window.FindControl<TextBlock>("SameText")!;
        Assert.False(window.FindControl<Button>("CompareButton")!.IsEnabled);
        Assert.True(same.IsVisible);
        Assert.Equal("Pick two different scenarios.", same.Text);
    }

    [AvaloniaFact]
    public async Task Compare_ReturnsThePair()
    {
        Window owner = ShowOwner();
        try
        {
            ScenarioComparePickerViewModel picker = Picker(Server("A1", "Alpha"));
            var window = new ScenarioComparePickerWindow(picker, new UserPreferences());
            Task<ScenarioComparePick?> pending = window.ShowAsync(owner);
            Dispatcher.UIThread.RunJobs();

            window.FindControl<Button>("CompareButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ScenarioComparePick? pick = await pending;
            Assert.Equal("L1", pick?.Mine.Id);
            Assert.Equal(ScenarioCompareSideKind.Server, pick?.Theirs.Kind);
            Assert.Equal("A1", pick?.Theirs.Id);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancel_ReturnsNull()
    {
        Window owner = ShowOwner();
        try
        {
            var window = new ScenarioComparePickerWindow(Picker(Server("A1", "Alpha")), new UserPreferences());
            Task<ScenarioComparePick?> pending = window.ShowAsync(owner);
            Dispatcher.UIThread.RunJobs();

            window.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Null(await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    // The local-only copy L1 and the server-only scenario A1, with the given row preselected.
    private static ScenarioComparePickerViewModel Picker(ScenarioBrowserRowViewModel preselect) =>
        new([Copy("L1", "Lima")], [Server("A1", "Alpha")], preselect);

    private static ScenarioBrowserRowViewModel Copy(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Kind = ScenarioRowKind.WorkingCopy,
        };

    private static ScenarioBrowserRowViewModel Server(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Kind = ScenarioRowKind.Server,
        };

    private static Window ShowOwner()
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }
}
