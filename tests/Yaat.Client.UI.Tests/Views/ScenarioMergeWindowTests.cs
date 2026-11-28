using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MsBox.Avalonia.ViewModels;
using MsBox.Avalonia.Windows;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scenario merge window over a three-way merge with two General clashes (name, auto-delete) and one auto-merged field
/// (primary airport): the gutter's → resolves a clash and Apply waits for every clash; Apply returns the result and writes;
/// the close box returns null and writes nothing; a two-way merge disables Show base and shows its note; ✎ opens the field's
/// editor and Keep this commits it; Apply waits while an editor is open; Previous/Next walk the clashes without wrapping; the
/// checks' findings show in one "Merged with problems" box before the write; Fresh copy beside closes with nothing applied,
/// or keeps the window open with its failure on the footer; the Aircraft tab shows the selected aircraft's differing rows,
/// Next reaches it from the last General clash, and ✎ on an aircraft opens the MERGED AIRCRAFT strip (the aircraft view in
/// detail-only mode) whose Keep this marks the line edited.
/// </summary>
public sealed class ScenarioMergeWindowTests
{
    private const string Id = "01H06NVK7VN8BS7MCDXHKJZ7MQ";

    private const string TwoWayNote = "There is no base version: choose a side for every difference.";

    private readonly List<string> _events = [];

    private readonly List<string> _written = [];

    private IReadOnlyList<ScenarioEditIssue> _findings = [];

    [AvaloniaFact]
    public void PressingTheirsOnEachClash_EnablesApply_OnceEveryClashIsChosen()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(Request(threeWay: true));
        try
        {
            Button apply = window.FindControl<Button>("ApplyButton")!;
            List<ScenarioMergeRowViewModel> clashes = [.. window.ViewModel.Rows.Where(r => r.IsClash)];
            Assert.Equal(2, clashes.Count);
            Assert.False(apply.IsEnabled);

            Click(window, Gutter(window, clashes[0], "keepTheirs"));
            Assert.False(apply.IsEnabled);
            Assert.True(clashes[0].IsTheirsPressed);

            Click(window, Gutter(window, clashes[1], "keepTheirs"));
            Assert.True(apply.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Apply_ReturnsTheResult_AndCallsWrite()
    {
        (Window owner, ScenarioMergeWindow window, Task<ScenarioMergeResult?> pending) = Open(Request(threeWay: true));
        try
        {
            ResolveTheirs(window);

            Click(window, window.FindControl<Button>("ApplyButton")!);

            Assert.True(pending.IsCompleted);
            ScenarioMergeResult? result = await pending;
            Assert.NotNull(result);
            Assert.Equal(result.MergedJson, Assert.Single(_written));
            Assert.Equal("Theirs", JsonNode.Parse(result.MergedJson)!["name"]!.GetValue<string>());
            Assert.Equal(["checks", "write"], _events);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloseBox_ReturnsNull_AndWriteIsNeverCalled()
    {
        (Window owner, ScenarioMergeWindow window, Task<ScenarioMergeResult?> pending) = Open(Request(threeWay: true));
        try
        {
            ResolveTheirs(window);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(pending.IsCompleted);
            Assert.Null(await pending);
            Assert.Null(await window.ViewModel.Completion);
            Assert.Empty(_written);
            Assert.Empty(_events);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void TwoWay_ShowBaseDisabledWithItsLabel_TheNoteShown_AndNoBaseStrip()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(Request(threeWay: false));
        try
        {
            CheckBox showBase = window.FindControl<CheckBox>("ShowBaseCheckBox")!;
            Assert.False(showBase.IsEnabled);
            Assert.Equal("Show base (no base version)", showBase.Content);
            Assert.True(window.FindControl<Control>("TwoWayNote")!.IsVisible);
            Assert.Equal(TwoWayNote, window.FindControl<TextBlock>("TwoWayNoteText")!.Text);
            Assert.All(BaseStrips(window), strip => Assert.False(strip.IsVisible));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void BaseStrip_ShowsOnAClash_AndOnAnAutoRowOnlyWithShowBase()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(Request(threeWay: true));
        try
        {
            ScenarioMergeRowViewModel clash = window.ViewModel.Rows.First(r => r.IsClash);
            ScenarioMergeRowViewModel auto = Assert.Single(window.ViewModel.Rows, r => !r.IsClash);
            Assert.False(window.FindControl<Control>("TwoWayNote")!.IsVisible);
            Assert.True(BaseStrip(window, clash).IsVisible);
            Assert.False(BaseStrip(window, auto).IsVisible);

            window.FindControl<CheckBox>("ShowBaseCheckBox")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(BaseStrip(window, auto).IsVisible);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Edit_OpensTheFieldsTextBox_AndKeepThisCommitsIt()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(Request(threeWay: true));
        try
        {
            ScenarioMergeRowViewModel name = Assert.Single(window.ViewModel.Rows, r => r.Path == "/name");

            Click(window, Gutter(window, name, "edit"));

            TextBox box = Assert.Single(EditorBoxes(window));
            Assert.Equal("Mine", box.Text);
            box.Text = "Merged";
            Click(window, EditorButton(window, "keepThis"));

            MergeChoice choice = window.ViewModel.Choices["/name"];
            Assert.Equal(MergeChoiceKind.Edit, choice.Kind);
            Assert.Equal("Merged", choice.Value!.GetValue<string>());
            Assert.True(name.IsEditPressed);
            Assert.Null(name.Editor);
            Assert.Empty(EditorBoxes(window));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Apply_IsDisabledWhileAnEditorIsOpen()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(Request(threeWay: true));
        try
        {
            Button apply = window.FindControl<Button>("ApplyButton")!;
            ResolveTheirs(window);
            Assert.True(apply.IsEnabled);

            Click(window, Gutter(window, Assert.Single(window.ViewModel.Rows, r => r.Path == "/name"), "edit"));
            Assert.False(apply.IsEnabled);

            Click(window, EditorButton(window, "cancelEdit"));
            Assert.True(apply.IsEnabled);
            Assert.Empty(_written);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void PreviousNext_ShowClashNOfN_AndDoNotWrap()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(Request(threeWay: true));
        try
        {
            Button previous = window.FindControl<Button>("PreviousButton")!;
            Button next = window.FindControl<Button>("NextButton")!;
            TextBlock position = window.FindControl<TextBlock>("PositionText")!;
            Assert.False(previous.IsEffectivelyEnabled);

            Click(window, next);
            Assert.Equal("clash 1 of 2", position.Text);
            Click(window, next);
            Assert.Equal("clash 2 of 2", position.Text);
            Assert.False(next.IsEffectivelyEnabled);

            Click(window, previous);
            Assert.Equal("clash 1 of 2", position.Text);
            Assert.False(previous.IsEffectivelyEnabled);
            Assert.True(next.IsEffectivelyEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChecksReturningWarnings_ShowOneMergedWithProblemsBox_ThenWrite()
    {
        _findings =
        [
            new(ScenarioEditIssueSeverity.Warning, null, "SKW3979", "parking", null, "G12 is not a parking spot at OAK"),
            new(ScenarioEditIssueSeverity.Error, null, null, ScenarioEditFields.Scenario, null, "8:00 SNALL is not a known command"),
        ];
        (Window owner, ScenarioMergeWindow window, Task<ScenarioMergeResult?> pending) = Open(Request(threeWay: true));
        try
        {
            ResolveTheirs(window);
            Click(window, window.FindControl<Button>("ApplyButton")!);

            MsBoxWindow box = Assert.IsType<MsBoxWindow>(Assert.Single(window.OwnedWindows));
            box.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Merged with problems", box.Title);
            List<Button> buttons = [.. box.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];
            Assert.Equal(["OK"], buttons.Select(b => b.Content as string));
            string text = Assert.IsType<MsBoxStandardViewModel>(box.DataContext).ContentMessage;
            Assert.Contains("G12 is not a parking spot at OAK", text, StringComparison.Ordinal);
            Assert.Contains("8:00 SNALL is not a known command", text, StringComparison.Ordinal);
            Assert.Empty(_written);

            Click(box, buttons[0]);

            Assert.True(pending.IsCompleted);
            ScenarioMergeResult? result = await pending;
            Assert.NotNull(result);
            Assert.Equal(_findings, result.Warnings);
            Assert.Equal(["checks", "write"], _events);
            Assert.Single(_written);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void FreshCopyBeside_IsHidden_WhenTheRequestHasNone()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(Request(threeWay: true));
        try
        {
            Assert.False(FreshCopyItem(window).IsVisible);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task FreshCopyBeside_ThatSucceeds_ClosesTheWindowReturningNull()
    {
        int copies = 0;
        ScenarioMergeRequest request = Request(threeWay: true) with
        {
            FreshCopy = () =>
            {
                copies++;
                return Task.CompletedTask;
            },
        };
        (Window owner, ScenarioMergeWindow window, Task<ScenarioMergeResult?> pending) = Open(request);
        try
        {
            MenuItem item = FreshCopyItem(window);
            Assert.True(item.IsVisible);

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(pending.IsCompleted);
            Assert.Null(await pending);
            Assert.Equal(1, copies);
            Assert.False(window.IsVisible);
            Assert.Empty(_written);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void FreshCopyBeside_ThatThrows_ShowsTheFailure_AndTheWindowStaysOpen()
    {
        ScenarioMergeRequest request = Request(threeWay: true) with { FreshCopy = () => Task.FromException(new IOException("The disk is full.")) };
        (Window owner, ScenarioMergeWindow window, Task<ScenarioMergeResult?> pending) = Open(request);
        try
        {
            FreshCopyItem(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            TextBlock failure = window.FindControl<TextBlock>("FreshCopyErrorText")!;
            Assert.True(failure.IsVisible);
            Assert.Equal("Couldn't make a fresh copy: The disk is full.", failure.Text);
            Assert.True(window.IsVisible);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void PreviousFromAnAircraftClash_LandsOnTheLastGeneralClash_MarkedCurrent()
    {
        ScenarioMergeRequest request = Request(threeWay: true) with
        {
            Texts = new ScenarioMergeTexts(
                WithAircraft(Scenario("Base", "Never", "OAK"), 0),
                WithAircraft(Scenario("Mine", "OnLanding", "OAK"), 60),
                WithAircraft(Scenario("Theirs", "Parked", "SFO"), 120)
            ),
        };
        (Window owner, ScenarioMergeWindow window, _) = Open(request);
        try
        {
            Button next = window.FindControl<Button>("NextButton")!;
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            Click(window, next);
            Click(window, next);
            Click(window, next);
            Assert.Equal("clash 3 of 3", window.FindControl<TextBlock>("PositionText")!.Text);
            Assert.Equal(1, tabs.SelectedIndex);

            Click(window, window.FindControl<Button>("PreviousButton")!);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            ScenarioMergeRowViewModel last = window.ViewModel.Rows.Last(r => r.IsClash && (r.Section != "Aircraft"));
            Assert.Same(last, window.ViewModel.CurrentClash);
            Assert.Equal(0, tabs.SelectedIndex);
            Border line = Assert.Single(
                window.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("lineBox") && ReferenceEquals(b.DataContext, last)
            );
            Assert.Contains("current", line.Classes);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task FreshCopyBeside_DuringAnApply_IsDisabled_AndMakesNoCopy()
    {
        var write = new TaskCompletionSource();
        int copies = 0;
        ScenarioMergeRequest request = Request(threeWay: true) with
        {
            Write = json =>
            {
                _written.Add(json);
                return write.Task;
            },
            FreshCopy = () =>
            {
                copies++;
                return Task.CompletedTask;
            },
        };
        (Window owner, ScenarioMergeWindow window, Task<ScenarioMergeResult?> pending) = Open(request);
        try
        {
            ResolveTheirs(window);
            MenuItem item = FreshCopyItem(window);
            Assert.True(item.IsEnabled);
            Click(window, window.FindControl<Button>("ApplyButton")!);
            Assert.Single(_written);

            Assert.False(item.IsEnabled);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, copies);
            Assert.True(window.IsVisible);

            write.SetResult();
            Dispatcher.UIThread.RunJobs();
            Assert.True(pending.IsCompleted);
            Assert.NotNull(await pending);
            Assert.Equal(0, copies);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void OrderClash_ThreeWay_ShowsTheBaseStripWithTheBaseOrder()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(TriggerOrderRequest(threeWay: true));
        try
        {
            ScenarioMergeRowViewModel order = Assert.Single(window.ViewModel.Rows, r => r.IsOrderRow);
            Assert.True(order.IsClash);
            Border strip = Assert.Single(OrderBaseStrips(window));
            Assert.True(strip.IsEffectivelyVisible);
            Assert.Contains(strip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1. SQALL · 2. SNALL · 3. SSALL");
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void OrderClash_TwoWay_ShowsNoBaseStrip()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(TriggerOrderRequest(threeWay: false));
        try
        {
            Assert.Single(window.ViewModel.Rows, r => r.IsOrderRow);
            Assert.All(OrderBaseStrips(window), strip => Assert.False(strip.IsVisible));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void SelectingNks2904_ShowsItsDetail_WithTheBaseStripOnThePresetClash()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(NksRequest());
        try
        {
            ScenarioMergeAircraftLineViewModel line = ShowAircraft(window, NksId);

            Assert.Same(line, window.ViewModel.SelectedAircraftLine);
            ScenarioMergeRowViewModel preset = Assert.Single(window.ViewModel.Rows, r => r.Path == NksPath("/presetCommands/1/command"));
            ScenarioMergeRowViewModel parking = Assert.Single(window.ViewModel.Rows, r => r.Path == NksPath("/startingConditions/parking"));
            Assert.True(preset.IsClash);
            Assert.True(BaseStrip(window, preset).IsEffectivelyVisible);
            Assert.False(BaseStrip(window, parking).IsVisible);
            ItemsControl detail = window.FindControl<ItemsControl>("AircraftRowList")!;
            Assert.Contains(
                detail.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("lineBox") && ReferenceEquals(b.DataContext, preset)
            );
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Next_FromTheLastGeneralClash_JumpsToTheAircraftTab_AndMarksTheAircraftsClash()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(AircraftClashRequest());
        try
        {
            Button next = window.FindControl<Button>("NextButton")!;
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            Click(window, next);
            Click(window, next);
            Assert.Equal(0, tabs.SelectedIndex);

            Click(window, next);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, tabs.SelectedIndex);
            ScenarioMergeAircraftLineViewModel line = Assert.Single(window.ViewModel.AircraftLines);
            Assert.Same(line, window.FindControl<ListBox>("AircraftList")!.SelectedItem);
            ScenarioMergeRowViewModel clash = Assert.Single(window.ViewModel.Rows, r => r.IsClash && (r.Section == "Aircraft"));
            Border box = Assert.Single(
                window.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("lineBox") && ReferenceEquals(b.DataContext, clash)
            );
            Assert.Contains("current", box.Classes);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void EditOnAnAircraft_OpensTheMergedDetail_AndKeepThisUpdatesTheLine()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(NksRequest());
        try
        {
            ScenarioMergeAircraftLineViewModel line = ShowAircraft(window, NksId);
            Button edit = AircraftEdit(window, line);
            Assert.Equal("changed", StatusTexts(window, line));

            Click(window, edit);

            ScenarioMergedAircraftViewModel editor = Assert.IsType<ScenarioMergedAircraftViewModel>(window.ViewModel.MergedAircraft);
            Assert.True(window.FindControl<Control>("MergedAircraftStrip")!.IsEffectivelyVisible);
            ScenarioAircraftView view = window.FindControl<ScenarioAircraftView>("MergedAircraftView")!;
            Assert.Same(editor.Aircraft, view.DataContext);
            TextBox callsign = view.FindControl<TextBox>("CallsignBox")!;
            Assert.Equal("NKS2904", callsign.Text);
            TextBox spawnDelay = view.FindControl<TextBox>("SpawnDelayBox")!;
            spawnDelay.Focus();
            spawnDelay.Text = "5:00";
            callsign.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(300, editor.Session.Document.Scenario.Aircraft[editor.Index].SpawnDelay);

            Click(window, window.FindControl<Button>("MergedAircraftKeepButton")!);

            Assert.Null(window.ViewModel.MergedAircraft);
            Assert.False(window.FindControl<Control>("MergedAircraftStrip")!.IsVisible);
            Assert.True(line.IsReplaced);
            Assert.Contains("pressed", AircraftEdit(window, line).Classes);
            Assert.Equal("edited in the merge", StatusTexts(window, line));
            Assert.Equal(300, window.ViewModel.AircraftEdits[NksId]["spawnDelay"]!.GetValue<int>());
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void DetailOnlyMode_HidesTheToolbarAndTheGrid()
    {
        Assert.False(new ScenarioAircraftView().IsDetailOnly);
        (Window owner, ScenarioMergeWindow window, _) = Open(NksRequest());
        try
        {
            ScenarioMergeAircraftLineViewModel line = ShowAircraft(window, NksId);
            window.AircraftServices!.Browser.Preferences.SetScenarioEditorAircraftGrid(new SavedGridLayout { ColumnOrder = ["Type", "Callsign"] });
            Click(window, AircraftEdit(window, line));

            ScenarioAircraftView view = window.FindControl<ScenarioAircraftView>("MergedAircraftView")!;
            DataGrid grid = view.FindControl<DataGrid>("AircraftGrid")!;
            Assert.True(view.IsDetailOnly);
            Assert.Equal("Callsign", FirstColumn(grid));
            Assert.True(view.FindControl<Control>("DetailPanel")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Control>("AircraftGrid")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Control>("AircraftToolbar")!.IsEffectivelyVisible);
            string[] buttons = ["AddAircraftButton", "DuplicateAircraftButton", "RemoveAircraftButton"];
            foreach (string name in buttons)
            {
                Assert.False(view.FindControl<Button>(name)!.IsEffectivelyVisible);
            }

            view.IsDetailOnly = false;
            window.UpdateLayout();

            Assert.True(view.FindControl<Control>("AircraftGrid")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Control>("AircraftToolbar")!.IsEffectivelyVisible);
            Assert.All(buttons, name => Assert.True(view.FindControl<Button>(name)!.IsEffectivelyVisible));
            Assert.Equal("Type", FirstColumn(grid));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void EditEveryClash_ThenKeepThisInTheAircraftStrip_EnablesApply()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(AircraftClashRequest());
        try
        {
            Button apply = window.FindControl<Button>("ApplyButton")!;
            window.ViewModel.EditEveryClashCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            List<ScenarioMergeRowViewModel> clashes = [.. window.ViewModel.Rows.Where(r => r.IsClash)];
            Assert.Equal(3, clashes.Count);
            Assert.All(clashes, r => Assert.True(r.IsEditing));

            foreach (ScenarioMergeRowViewModel general in clashes.Where(r => r.Section != "Aircraft"))
            {
                Click(window, EditorButton(window, general, "keepThis"));
            }

            Assert.False(apply.IsEnabled);
            ShowAircraft(window, AircraftId);
            ScenarioMergeRowViewModel aircraft = Assert.Single(clashes, r => r.Section == "Aircraft");

            Click(window, EditorButton(window, aircraft, "keepThis"));

            Assert.True(aircraft.IsEditPressed);
            Assert.True(apply.IsEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void WhileTheMergedAircraftEditorIsOpen_AnotherLineAndNext_LeaveItsAircraftSelected()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(NksRequest());
        try
        {
            ScenarioMergeAircraftLineViewModel nks = ShowAircraft(window, NksId);
            ListBox list = window.FindControl<ListBox>("AircraftList")!;
            ScenarioMergeAircraftLineViewModel other = window.ViewModel.AircraftLines[0];
            Assert.NotSame(nks, other);
            Click(window, AircraftEdit(window, nks));
            Assert.NotNull(window.ViewModel.MergedAircraft);

            ClickAt(window, list.ContainerFromItem(other)!);
            window.ViewModel.SelectedAircraftLine = other;
            Assert.False(window.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
            window.ViewModel.NextClashCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(nks, window.ViewModel.SelectedAircraftLine);
            Assert.Same(nks, list.SelectedItem);
            Assert.Equal("", window.FindControl<TextBlock>("PositionText")!.Text);

            Click(window, window.FindControl<Button>("MergedAircraftCancelButton")!);
            Assert.True(window.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void AnAircraftAddedInTheirs_EditedAndKept_ReadsEditedInTheMerge()
    {
        ScenarioMergeRequest request = NksRequest() with
        {
            Texts = new ScenarioMergeTexts(Oak().ToJsonString(), Oak().ToJsonString(), WithTheirsOnly(Oak()).ToJsonString()),
        };
        (Window owner, ScenarioMergeWindow window, _) = Open(request);
        try
        {
            ScenarioMergeAircraftLineViewModel line = ShowAircraft(window, TheirsOnlyId);
            Click(window, AircraftEdit(window, line));
            ScenarioMergedAircraftViewModel editor = window.ViewModel.MergedAircraft!;
            editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 240);

            Click(window, window.FindControl<Button>("MergedAircraftKeepButton")!);

            Assert.Empty(window.ViewModel.AircraftEdits);
            Assert.True(line.IsReplaced);
            Assert.Equal("edited in the merge", StatusTexts(window, line));
            Assert.Contains("pressed", AircraftEdit(window, line).Classes);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Next_OntoTheAircraftOrderClash_MarksItsLineCurrent()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(AircraftOrderRequest());
        try
        {
            ScenarioMergeRowViewModel order = Assert.Single(window.ViewModel.Rows, r => r.IsClash);
            Assert.True(order.IsOrderRow);

            Click(window, window.FindControl<Button>("NextButton")!);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, window.FindControl<TabControl>("Tabs")!.SelectedIndex);
            Border box = Assert.Single(
                window.FindControl<ItemsControl>("AircraftOrderList")!.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("lineBox") && ReferenceEquals(b.DataContext, window.ViewModel.CurrentLine)
            );
            Assert.Contains("current", box.Classes);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task MergedAircraftEditor_AfterLoadPickers_OffersTheMergesAirportsAndPositions()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(NksRequest());
        try
        {
            ScenarioMergedAircraftServices services = AircraftServices();
            await window
                .ViewModel.LoadPickersAsync(services.Browser, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            ScenarioMergePickers pickers = window.ViewModel.Pickers;
            Assert.NotEmpty(pickers.Airports!);
            Assert.NotEmpty(pickers.Positions!);

            ScenarioMergeAircraftLineViewModel line = ShowAircraft(window, NksId);
            Click(window, AircraftEdit(window, line));

            ScenarioAircraftViewModel aircraft = window.ViewModel.MergedAircraft!.Aircraft;
            Assert.Equal(pickers.Airports, aircraft.AirportOptions);
            Assert.Equal(pickers.Positions, aircraft.PositionCatalog);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task MergedAircraftEditor_OpenBeforeLoadPickers_GetsThemWhenTheyApply()
    {
        (Window owner, ScenarioMergeWindow window, _) = Open(NksRequest());
        try
        {
            ScenarioMergeAircraftLineViewModel line = ShowAircraft(window, NksId);
            Click(window, AircraftEdit(window, line));
            ScenarioAircraftViewModel aircraft = window.ViewModel.MergedAircraft!.Aircraft;
            Assert.Empty(aircraft.AirportOptions);

            await window
                .ViewModel.LoadPickersAsync(window.AircraftServices!.Browser, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            ScenarioMergePickers pickers = window.ViewModel.Pickers;
            Assert.NotEmpty(pickers.Airports!);
            Assert.NotEmpty(pickers.Positions!);
            Assert.Equal(pickers.Airports, aircraft.AirportOptions);
            Assert.Equal(pickers.Positions, aircraft.PositionCatalog);
        }
        finally
        {
            owner.Close();
        }
    }

    private const string AircraftId = "01HAIRCRAFT000000000000001";

    // The header of the grid's first column as the grid shows them.
    private static string? FirstColumn(DataGrid grid) => grid.Columns.OrderBy(c => c.DisplayIndex).First().Header as string;

    private const string OakId = "01H08FSWF5NCDXTQMQ3BWB0BND";

    private const string NksId = "01H08FSWF5NCDXTQMQ3BWB0BNP";

    private const string TheirsOnlyId = "01HYYYYYYYYYYYYYYYYYYYYYYY";

    // Three aircraft each side holds; mine and theirs each reorder the base's differently, so the aircraft order clashes.
    private ScenarioMergeRequest AircraftOrderRequest()
    {
        static string Side(int[] order)
        {
            JsonObject root = JsonNode.Parse(Scenario("Mine", "OnLanding", "OAK"))!.AsObject();
            root["aircraft"] = new JsonArray([
                .. order.Select(i => (JsonNode)new JsonObject { ["id"] = $"01HAIRCRAFT00000000000000{i}0", ["aircraftId"] = $"N{i}00AB" }),
            ]);
            return root.ToJsonString();
        }

        return Request(threeWay: true) with
        {
            Texts = new ScenarioMergeTexts(Side([0, 1, 2]), Side([1, 0, 2]), Side([0, 2, 1])),
        };
    }

    // N3796G copied under a new id and callsign N4471K, appended to the scenario.
    private static JsonObject WithTheirsOnly(JsonObject scenario)
    {
        JsonArray aircraft = scenario["aircraft"]!.AsArray();
        JsonObject added = aircraft.Single(a => a!["aircraftId"]!.GetValue<string>() == "N3796G")!.DeepClone().AsObject();
        added["id"] = TheirsOnlyId;
        added["aircraftId"] = "N4471K";
        aircraft.Add(added);
        return scenario;
    }

    // A file of the checkout, found above the test output.
    private static string RepoFile(params string[] parts)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"{string.Join("/", parts)} is not above {AppContext.BaseDirectory}");
    }

    private static void ClickAt(Window window, Control control)
    {
        window.UpdateLayout();
        Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(center);
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class StubAccess(string[] artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artccs);
    }

    // The data-api: ZOA's config from the committed snapshot; every other request fails as if the network were down.
    private sealed class ZoaOnlyHandler(string zoa) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (request.RequestUri?.AbsolutePath.EndsWith("/ZOA", StringComparison.Ordinal) == true)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zoa, Encoding.UTF8, "application/json") })
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("the network is down"));
    }

    // The browser's dialogs, none of which the merge window asks.
    private sealed class UnaskedPrompts : IScenarioBrowserPrompts
    {
        public Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row) => Unasked<bool>();

        public Task<IReadOnlyList<string>> PickImportFilesAsync() => Unasked<IReadOnlyList<string>>();

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Unasked<string?>();

        public Task<string?> PickExportPathAsync(string suggestedFileName) => Unasked<string?>();

        public Task<string?> PickExportFolderAsync() => Unasked<string?>();

        public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question) => Unasked<ImportConflictAnswer>();

        public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName, UnsavedChangesContext context) =>
            Unasked<UnsavedChangesChoice>();

        public Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName) => Unasked<bool>();

        public Task<bool> ConfirmReplaceRunningScenarioAsync(string scenarioName) => Unasked<bool>();

        public Task<UnexportedChangesChoice> AskUnexportedChangesAsync(IReadOnlyList<string> names, UnexportedChangesContext context) =>
            Unasked<UnexportedChangesChoice>();

        public Task<UnsavedAndUnexportedChoice> AskUnsavedAndUnexportedAsync(
            string openName,
            IReadOnlyList<string> names,
            UnexportedChangesContext context
        ) => Unasked<UnsavedAndUnexportedChoice>();

        public Task<ScenarioMergeResult?> ShowMergeAsync(ScenarioMergeRequest request) => Unasked<ScenarioMergeResult?>();

        public Task<ScenarioComparePick?> PickCompareAsync(ScenarioComparePickerViewModel picker) => Unasked<ScenarioComparePick?>();

        private static Task<T> Unasked<T>() => Task.FromException<T>(new NotSupportedException("the merge window asks no browser question"));
    }

    private static string NksPath(string member) => "/aircraft/8" + member;

    // The General clashes of the base request, plus one aircraft whose spawn delay differs on each side.
    private ScenarioMergeRequest AircraftClashRequest() =>
        Request(threeWay: true) with
        {
            Texts = new ScenarioMergeTexts(
                WithAircraft(Scenario("Base", "Never", "OAK"), 0),
                WithAircraft(Scenario("Mine", "OnLanding", "OAK"), 60),
                WithAircraft(Scenario("Theirs", "Parked", "SFO"), 120)
            ),
        };

    // The OAK example with NKS2904's parking changed in mine only (11 to 12) and its second preset changed on both sides.
    private ScenarioMergeRequest NksRequest()
    {
        JsonObject baseSide = WithNksPresets(Oak(), "FH 010");
        JsonObject mine = WithNksPresets(Oak(), "FH 020");
        Nks(mine)["startingConditions"]!["parking"] = "12";
        JsonObject theirs = WithNksPresets(Oak(), "FH 030");
        return Request(threeWay: true) with
        {
            ScenarioId = OakId,
            Texts = new ScenarioMergeTexts(baseSide.ToJsonString(), mine.ToJsonString(), theirs.ToJsonString()),
        };
    }

    private static JsonObject WithNksPresets(JsonObject scenario, string second)
    {
        Nks(scenario)["presetCommands"] = new JsonArray(
            new JsonObject { ["id"] = "01HPRESETNKS00000000000001", ["command"] = "CM 50" },
            new JsonObject { ["id"] = "01HPRESETNKS00000000000002", ["command"] = second }
        );
        return scenario;
    }

    private static JsonObject Nks(JsonObject scenario) =>
        scenario["aircraft"]!.AsArray().Single(a => a!["aircraftId"]!.GetValue<string>() == "NKS2904")!.AsObject();

    // The committed OAK scenario example: 30 aircraft, NKS2904 the ninth.
    private static JsonObject Oak() =>
        JsonNode.Parse(File.ReadAllText(RepoFile("docs", "atctrainer-scenario-examples", OakId + ".json")))!.AsObject();

    // The merged-aircraft editor's services: the app's, with ZOA's config from the committed snapshot and no ground layout
    // for any airport.
    private static ScenarioMergedAircraftServices AircraftServices()
    {
        var preferences = new UserPreferences();
        string snapshot = File.ReadAllText(RepoFile("tests", "Yaat.Sim.Tests", "TestData", "artcc-zoa-snapshot.json"));
        var configs = new ArtccConfigSource(
            new HttpClient(new ZoaOnlyHandler(snapshot)),
            YaatPaths.Combine("scenario-merge-tests", Guid.NewGuid().ToString("N"), "artcc"),
            () => DateTime.UtcNow
        );
        ScenarioBrowserServices browser = ScenarioBrowserServices.ForApp(preferences, new UnaskedPrompts(), new StubAccess(["ZOA"])) with
        {
            Configs = configs,
        };
        return new(browser, (_, _) => Task.FromResult<AirportGroundLayout?>(null), Task.CompletedTask);
    }

    // Shows the Aircraft tab and selects the aircraft's line, with the window's merged-aircraft services given.
    private static ScenarioMergeAircraftLineViewModel ShowAircraft(ScenarioMergeWindow window, string itemKey)
    {
        window.AircraftServices = AircraftServices();
        window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        ScenarioMergeAircraftLineViewModel line = Assert.Single(window.ViewModel.AircraftLines, l => l.ItemKey == itemKey);
        window.FindControl<ListBox>("AircraftList")!.SelectedItem = line;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return line;
    }

    private static Button AircraftEdit(Window window, ScenarioMergeAircraftLineViewModel line) =>
        Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Classes.Contains("aircraftEdit") && ReferenceEquals(b.DataContext, line)
        );

    // The aircraft line's visible status text.
    private static string StatusTexts(Window window, ScenarioMergeAircraftLineViewModel line) =>
        string.Join(
            " ",
            window
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t.Classes.Contains("aircraftStatus") && ReferenceEquals(t.DataContext, line) && t.IsEffectivelyVisible)
                .Select(t => t.Text)
        );

    private static Button EditorButton(Window window, ScenarioMergeRowViewModel row, string kind) =>
        Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Classes.Contains(kind) && (b.DataContext is ScenarioMergedValueViewModel) && ReferenceEquals(b.DataContext, row.Editor)
        );

    private static string Scenario(string name, string autoDelete, string airport) =>
        new JsonObject
        {
            ["id"] = Id,
            ["name"] = name,
            ["artccId"] = "ZOA",
            ["primaryAirportId"] = airport,
            ["autoDeleteMode"] = autoDelete,
            ["aircraft"] = new JsonArray(),
            ["atc"] = new JsonArray(),
            ["initializationTriggers"] = new JsonArray(),
            ["aircraftGenerators"] = new JsonArray(),
            ["flightStripConfigurations"] = new JsonArray(),
        }.ToJsonString();

    // The scenario with one aircraft whose spawn delay differs per side, so the Aircraft tab holds one clash.
    private static string WithAircraft(string scenario, int spawnDelay)
    {
        JsonObject root = JsonNode.Parse(scenario)!.AsObject();
        root["aircraft"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "01HAIRCRAFT000000000000001",
                ["aircraftId"] = "N123AB",
                ["spawnDelay"] = spawnDelay,
            }
        );
        return root.ToJsonString();
    }

    // Three triggers each side holds; mine and theirs each reorder the base's SQALL, SNALL, SSALL differently.
    private ScenarioMergeRequest TriggerOrderRequest(bool threeWay) =>
        Request(threeWay) with
        {
            Texts = new ScenarioMergeTexts(
                threeWay ? WithTriggers(Scenario("Mine", "OnLanding", "OAK"), [0, 1, 2]) : null,
                WithTriggers(Scenario("Mine", "OnLanding", "OAK"), [1, 0, 2]),
                WithTriggers(Scenario("Mine", "OnLanding", "OAK"), [0, 2, 1])
            ),
        };

    private static string WithTriggers(string scenario, int[] order)
    {
        string[] commands = ["SQALL", "SNALL", "SSALL"];
        JsonObject root = JsonNode.Parse(scenario)!.AsObject();
        root["initializationTriggers"] = new JsonArray([
            .. order.Select(i =>
                (JsonNode)
                    new JsonObject
                    {
                        ["id"] = $"01HTRIGGER00000000000000{i}0",
                        ["timeOffset"] = 0,
                        ["command"] = commands[i],
                    }
            ),
        ]);
        return root.ToJsonString();
    }

    private static List<Border> OrderBaseStrips(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("orderBaseStrip"))];

    // Three-way: name and auto-delete clash, the primary airport merges by itself from theirs. Two-way: the same two sides.
    private ScenarioMergeRequest Request(bool threeWay) =>
        new(
            "ZOA",
            Id,
            new ScenarioMergeTexts(
                threeWay ? Scenario("Base", "Never", "OAK") : null,
                Scenario("Mine", "OnLanding", "OAK"),
                Scenario("Theirs", "Parked", "SFO")
            ),
            new ScenarioMergeCaptions("Merge server changes", "working copy", "server version", "server version of 2 Oct 2026", TwoWayNote),
            json =>
            {
                _events.Add("write");
                _written.Add(json);
                return Task.CompletedTask;
            }
        );

    private IReadOnlyList<ScenarioEditIssue> RunChecks(string merged)
    {
        _events.Add("checks");
        return _findings;
    }

    private (Window Owner, ScenarioMergeWindow Window, Task<ScenarioMergeResult?> Pending) Open(ScenarioMergeRequest request)
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        var window = new ScenarioMergeWindow(request, new UserPreferences(), RunChecks);
        Task<ScenarioMergeResult?> pending = window.ShowAsync(owner);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (owner, window, pending);
    }

    private static void ResolveTheirs(ScenarioMergeWindow window)
    {
        foreach (ScenarioMergeRowViewModel clash in window.ViewModel.Rows.Where(r => r.IsClash).ToList())
        {
            Click(window, Gutter(window, clash, "keepTheirs"));
        }
    }

    private static Button Gutter(Window window, ScenarioMergeRowViewModel row, string kind) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains(kind) && ReferenceEquals(b.DataContext, row));

    private static List<Border> BaseStrips(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("baseStrip"))];

    private static Border BaseStrip(Window window, ScenarioMergeRowViewModel row) =>
        Assert.Single(BaseStrips(window), b => ReferenceEquals(b.DataContext, row));

    private static List<TextBox> EditorBoxes(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBox>().Where(b => (b.DataContext is ScenarioMergedValueViewModel) && b.IsEffectivelyVisible)];

    private static Button EditorButton(Window window, string kind) =>
        Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Classes.Contains(kind) && (b.DataContext is ScenarioMergedValueViewModel) && b.IsEffectivelyVisible
        );

    private static MenuItem FreshCopyItem(ScenarioMergeWindow window)
    {
        MenuFlyout menu = Assert.IsType<MenuFlyout>(window.FindControl<Button>("WholeScenarioButton")!.Flyout);
        return Assert.Single(menu.Items.OfType<MenuItem>(), i => i.Name == "FreshCopyItem");
    }

    private static void Click(Window window, Button button)
    {
        button.BringIntoView();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsEffectivelyEnabled);
        Point? center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(center);
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
