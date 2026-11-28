using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The spawn planner window over a planner on a text session: Preview fills the table and the graph's bars match the
/// geometry's buckets, Apply writes one undo step, a band that does not read is named in the warnings, the graph's frame
/// is a copy the planner cannot change, closing disposes the planner, and the window keeps its geometry under its key.
/// </summary>
public sealed class SpawnPlannerWindowTests
{
    [AvaloniaFact]
    public void Preview_FillsTheTable_AndTheGraphBarsMatchTheGeometryBuckets()
    {
        ScenarioEditSession session = Session(6);
        SpawnPlannerViewModel vm = Planner(session);
        UseBand(vm, "0", "10", "2:00", "0", "0");
        SpawnPlannerWindow window = Show(vm, new UserPreferences());
        try
        {
            Press(window, "PreviewButton");

            ItemsControl table = window.FindControl<ItemsControl>("PreviewTable")!;
            WorkloadGraph graph = window.FindControl<WorkloadGraph>("Graph")!;
            ImmutableArray<int> expected = WorkloadGraphGeometry.Buckets(
                vm.PreviewRows.Select(row => row.NewDelaySec),
                vm.PlanningLengthSec,
                vm.BucketSizeSec
            );
            Assert.Equal(6, table.ItemCount);
            Assert.Equal([.. expected], [.. graph.Snapshot.Bars]);
            Assert.Equal(6, graph.Snapshot.Bars.Sum());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Apply_WritesOneUndoStep()
    {
        ScenarioEditSession session = Session(6);
        SpawnPlannerViewModel vm = Planner(session);
        UseBand(vm, "0", "10", "2:00", "0", "0");
        SpawnPlannerWindow window = Show(vm, new UserPreferences());
        try
        {
            Press(window, "ApplyButton");

            Assert.True(session.CanUndo);
            Assert.Equal("Apply spawn plan", session.UndoDescription);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Preview_BandRowThatDoesNotRead_ShowsBandSkippedInWarnings()
    {
        ScenarioEditSession session = Session(4);
        SpawnPlannerViewModel vm = Planner(session);
        UseBand(vm, "0", "10", "nope", "0", "0");
        SpawnPlannerWindow window = Show(vm, new UserPreferences());
        try
        {
            Press(window, "PreviewButton");

            ItemsControl warnings = window.FindControl<ItemsControl>("WarningsList")!;
            Assert.Contains($"Band 1 is skipped: {SpawnBandRowViewModel.IntervalErrorText}", warnings.Items.OfType<string>());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void GraphSnapshot_IsACopy_ThePlannerChangingLaterLeavesItUnchanged()
    {
        ScenarioEditSession session = Session(6);
        SpawnPlannerViewModel vm = Planner(session);
        UseBand(vm, "0", "10", "2:00", "0", "0");
        SpawnPlannerWindow window = Show(vm, new UserPreferences());
        try
        {
            Press(window, "PreviewButton");
            WorkloadGraph graph = window.FindControl<WorkloadGraph>("Graph")!;
            WorkloadGraphSnapshot taken = graph.Snapshot;
            int[] bars = [.. taken.Bars];

            UseBand(vm, "20", "30", "2:00", "0", "0");
            Press(window, "PreviewButton");
            vm.PlanningLengthMinutes = 45;

            int[] keptBars = [.. taken.Bars];
            int[] newBars = [.. graph.Snapshot.Bars];
            Assert.Equal(bars, keptBars);
            Assert.Equal(3600, taken.LengthSec);
            Assert.NotSame(taken, graph.Snapshot);
            Assert.NotEqual(bars, newBars);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Close_DisposesThePlanner()
    {
        ScenarioEditSession session = Session(4);
        Assert.True(session.Edit("Earlier edit", scenario => scenario.Aircraft[0].SpawnDelay = 5));
        SpawnPlannerViewModel vm = Planner(session);
        UseBand(vm, "0", "10", "2:00", "0", "0");
        SpawnPlannerWindow window = Show(vm, new UserPreferences());
        Press(window, "PreviewButton");
        Assert.True(vm.HasPreview);

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.Undo());

        Assert.True(vm.HasPreview);
    }

    [AvaloniaFact]
    public void Geometry_RestoresUnderTheSpawnPlannerKey()
    {
        var preferences = new UserPreferences();
        preferences.SetWindowGeometry(
            "SpawnPlanner",
            new SavedWindowGeometry
            {
                X = 120,
                Y = 90,
                Width = 1234,
                Height = 777,
                IsMaximized = false,
                ScreenIndex = 0,
                IsTopmost = false,
            }
        );
        SpawnPlannerViewModel vm = Planner(Session(2));

        var window = SpawnPlannerWindow.Create(vm, preferences, "Spawn plan");
        try
        {
            Assert.Equal("SpawnPlanner", SpawnPlannerWindow.GeometryKey);
            Assert.Equal(1234, window.Width);
            Assert.Equal(777, window.Height);
            Assert.Equal("Spawn planner — Spawn plan", window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SaveAndLoadPickers_StartInTheLastFolder_AndRememberThePickedOne()
    {
        string earlier = YaatPaths.Combine("spawn-planner-tests", Guid.NewGuid().ToString("N"), "earlier");
        string picked = YaatPaths.Combine("spawn-planner-tests", Guid.NewGuid().ToString("N"), "picked");
        Directory.CreateDirectory(earlier);
        Directory.CreateDirectory(picked);
        string planPath = Path.Combine(picked, "plan.json");
        var preferences = new UserPreferences();
        preferences.SetSpawnPlannerLastFolder(earlier);
        var picker = new FakeFilePickerService();
        picker.QueueSaveFile(planPath);
        picker.QueueOpenFile(planPath);
        SpawnPlannerViewModel planner = Planner(Session(2));
        var window = SpawnPlannerWindow.Create(planner, preferences, "Spawn plan", _ => picker);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Click(window, "SaveButton");
            Assert.Equal(picked, preferences.SpawnPlannerLastFolder);
            Assert.True(File.Exists(planPath));
            planner.MinGapSec = 999;
            Click(window, "LoadButton");

            Assert.Equal(2, picker.Calls.Count);
            Assert.Equal(earlier, Assert.IsType<SaveFileOptions>(picker.Calls[0].Options).StartFolder);
            Assert.Equal(picked, Assert.IsType<OpenFileOptions>(picker.Calls[1].Options).StartFolder);
            Assert.Null(planner.PlanError);
            Assert.Equal(SpawnPlan.DefaultMinGapSec, planner.MinGapSec);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("LoadButton")]
    [InlineData("SaveButton")]
    public void PickerThatFails_ShowsTheErrorInTheWarnings(string button)
    {
        SpawnPlannerViewModel planner = Planner(Session(2));
        var window = SpawnPlannerWindow.Create(planner, new UserPreferences(), "Spawn plan", _ => new FailingFilePicker());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Click(window, button);

            Assert.Equal(SpawnPlannerWindow.PickerErrorPrefix + FailingFilePicker.Message, planner.PlanError);
            Assert.Equal(planner.PlanError, window.FindControl<TextBlock>("PlanErrorText")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PreferencesWriteThatFails_AfterAGoodPick_StillLoadsThePlan()
    {
        string picked = YaatPaths.Combine("spawn-planner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(picked);
        string planPath = Path.Combine(picked, "plan.json");
        SpawnPlannerViewModel planner = Planner(Session(2));
        SpawnPlanFile.Write(planPath, planner.ToPlan());
        planner.MinGapSec = 999;
        var preferences = new UserPreferences();
        var picker = new FakeFilePickerService();
        picker.QueueOpenFile(planPath);
        var window = SpawnPlannerWindow.Create(planner, preferences, "Spawn plan", _ => picker);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        string preferencesFile = UserPreferences.FilePath;
        File.Delete(preferencesFile);
        Directory.CreateDirectory(preferencesFile);
        try
        {
            Click(window, "LoadButton");

            Assert.Equal(SpawnPlan.DefaultMinGapSec, planner.MinGapSec);
            Assert.StartsWith(SpawnPlannerWindow.FolderErrorPrefix, planner.PlanError);
        }
        finally
        {
            Directory.Delete(preferencesFile);
            window.Close();
        }
    }

    private static void Click(SpawnPlannerWindow window, string name)
    {
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static SpawnPlannerWindow Show(SpawnPlannerViewModel vm, UserPreferences preferences)
    {
        var window = SpawnPlannerWindow.Create(vm, preferences, "Spawn plan");
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Press(SpawnPlannerWindow window, string name)
    {
        Button button = window.FindControl<Button>(name)!;
        Assert.True(button.Command!.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static ScenarioEditSession Session(int departures)
    {
        var scenario = new Scenario
        {
            Id = "spawn-plan",
            Name = "Spawn plan",
            ArtccId = "ZOA",
            PrimaryAirportId = "KSFO",
            Aircraft = [.. Enumerable.Range(1, departures).Select(i => Ground($"AAL{i}", i * 30))],
        };
        return ScenarioEditSession.OverText("ZOA", "spawn-plan", ScenarioDocument.FromScenario(scenario).Write());
    }

    private static SpawnPlannerViewModel Planner(ScenarioEditSession session) =>
        new(session, (description, change) => session.Edit(description, change));

    private static void UseBand(SpawnPlannerViewModel vm, string from, string to, string interval, string plus, string minus)
    {
        vm.Bands.Clear();
        vm.Bands.Add(new SpawnBandRowViewModel(from, to, interval, plus, minus));
    }

    private static ScenarioAircraft Ground(string callsign, int spawnDelay) =>
        new()
        {
            Id = callsign,
            AircraftId = callsign,
            AircraftType = "B738",
            SpawnDelay = spawnDelay,
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "IFR",
                Departure = "KSFO",
                Destination = "KLAX",
            },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = "A1" },
        };

    // A picker whose every dialog fails to open.
    private sealed class FailingFilePicker : IFilePickerService
    {
        public const string Message = "no dialog here";

        public Task<string?> OpenFileAsync(OpenFileOptions options) => throw new InvalidOperationException(Message);

        public Task<IReadOnlyList<string>> OpenFilesAsync(OpenFileOptions options) => throw new InvalidOperationException(Message);

        public Task<string?> SaveFileAsync(SaveFileOptions options) => throw new InvalidOperationException(Message);

        public Task<string?> OpenFolderAsync(OpenFolderOptions options) => throw new InvalidOperationException(Message);
    }
}
