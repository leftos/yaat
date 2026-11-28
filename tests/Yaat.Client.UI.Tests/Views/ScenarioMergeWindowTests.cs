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
using Yaat.Sim.Authoring;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scenario merge window over a three-way merge with two General clashes (name, auto-delete) and one auto-merged field
/// (primary airport): the gutter's → resolves a clash and Apply waits for every clash; Apply returns the result and writes;
/// the close box returns null and writes nothing; a two-way merge disables Show base and shows its note; ✎ opens the field's
/// editor and Keep this commits it; Apply waits while an editor is open; Previous/Next walk the clashes without wrapping; the
/// checks' findings show in one "Merged with problems" box before the write; and Fresh copy beside closes with nothing applied,
/// or keeps the window open with its failure on the footer.
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
