using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
/// is a copy the planner cannot change, a drag on a control point's dot moves its row while a tap, a press elsewhere or an
/// Escape leaves it, closing disposes the planner, and the window keeps its geometry under its key.
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

    [AvaloniaFact]
    public void DragDot_MovesTheRowThePointAndTheText()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            WorkloadGraphSnapshot before = graph.Snapshot;
            GraphPlot plot = PlotOf(graph);
            double x = plot.Left + (plot.Width / 4);
            double y = plot.Bottom - (plot.Height / 4);

            DragDot(window, PixelOf(graph, window, row.ToPlan()), ToWindow(graph, window, x, y));

            SpawnPlanPoint expected = WorkloadGraphGeometry.DragPoint(plot, graph.Snapshot.LengthSec, x, y);
            Assert.Equal(15, expected.TimeMinutes, 6);
            Assert.Equal(25, expected.Percent, 6);
            Assert.Equal(expected.TimeMinutes, row.TimeMinutes, 6);
            Assert.Equal(expected.Percent, row.Percent, 6);
            Assert.NotSame(before, graph.Snapshot);
            Assert.Equal(expected, graph.Snapshot.Points[0]);
            Assert.NotEqual([.. before.Curve], [.. graph.Snapshot.Curve]);
            Assert.Equal(expected.TimeMinutes, NumberIn(RowBox(window, row, 0)), 6);
            Assert.Equal(expected.Percent, NumberIn(RowBox(window, row, 1)), 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DragDotPastANeighbour_LeavesTheRowOrderAndTheCurveBuilds()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel first = vm.ControlPoints[0];
            WorkloadPointRowViewModel second = vm.ControlPoints[1];
            GraphPlot plot = PlotOf(graph);

            DragDot(window, PixelOf(graph, window, first.ToPlan()), ToWindow(graph, window, plot.Right - 4, plot.Bottom - 4));

            Assert.Same(first, vm.ControlPoints[0]);
            Assert.Same(second, vm.ControlPoints[1]);
            Assert.True(first.TimeMinutes > second.TimeMinutes);
            Assert.NotNull(vm.BuildCurve());
            Assert.Equal(2, graph.Snapshot.Points.Length);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DragDotOutsideThePlot_ClampsTheRowToItsRange()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            GraphPlot plot = PlotOf(graph);

            DragDot(window, PixelOf(graph, window, row.ToPlan()), ToWindow(graph, window, plot.Left - 17, plot.Bottom + 30));
            Assert.Equal(0, row.TimeMinutes, 6);
            Assert.Equal(0, row.Percent, 6);

            DragDot(window, PixelOf(graph, window, row.ToPlan()), ToWindow(graph, window, plot.Right + 20, plot.Top - 4));
            Assert.Equal(graph.Snapshot.LengthSec / 60.0, row.TimeMinutes, 6);
            Assert.Equal(100, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TapOnADot_ChangesNothing()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            GraphPlot plot = PlotOf(graph);
            int lengthSec = graph.Snapshot.LengthSec;
            (double X, double Y) at = WorkloadGraphGeometry.PointPixel(plot, lengthSec, row.ToPlan())!.Value;
            var nudged = new Point(at.X, at.Y + 1.5);

            // The nudge is past the threshold's reach, so only the tap size keeps it from writing.
            Assert.NotEqual(20, WorkloadGraphGeometry.DragPoint(plot, lengthSec, nudged.X, nudged.Y).Percent, 6);

            MouseDownAt(window, ToWindow(graph, window, at.X, at.Y));
            MouseUpAt(window, ToWindow(graph, window, nudged.X, nudged.Y));

            Assert.Equal(10, row.TimeMinutes, 6);
            Assert.Equal(20, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightPressOnADot_ChangesNothing()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            Point to = QuarterOf(graph, window);

            window.MouseDown(PixelOf(graph, window, row.ToPlan()), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            window.MouseMove(to, RawInputModifiers.RightMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(to, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(10, row.TimeMinutes, 6);
            Assert.Equal(20, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DetachMidDrag_LeavesTheDraggedValues()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            Point to = QuarterOf(graph, window);

            MouseDownAt(window, PixelOf(graph, window, row.ToPlan()));
            MouseMoveTo(window, to);
            Assert.Equal(15, row.TimeMinutes, 6);

            graph.Detach();
            MouseUpAt(window, to);

            Assert.Equal(15, row.TimeMinutes, 6);
            Assert.Equal(25, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RemovingAnotherRowMidDrag_LeavesTheDraggedValues()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        vm.ControlPoints.Clear();
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(10, 20));
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(30, 80));
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(50, 40));
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            WorkloadPointRowViewModel other = vm.ControlPoints[2];
            Point to = QuarterOf(graph, window);

            MouseDownAt(window, PixelOf(graph, window, row.ToPlan()));
            MouseMoveTo(window, to);
            Assert.Equal(15, row.TimeMinutes, 6);

            vm.RemovePoint(other);
            Dispatcher.UIThread.RunJobs();
            MouseUpAt(window, to);

            Assert.Equal(15, row.TimeMinutes, 6);
            Assert.Equal(25, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ASecondPressWhileDragging_DoesNotStartASecondDrag()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            WorkloadPointRowViewModel other = vm.ControlPoints[1];
            Point to = QuarterOf(graph, window);

            MouseDownAt(window, PixelOf(graph, window, row.ToPlan()));
            MouseMoveTo(window, to);
            Assert.Equal(15, row.TimeMinutes, 6);

            MouseDownAt(window, PixelOf(graph, window, other.ToPlan()));
            MouseMoveTo(window, to);
            Assert.Equal(30, other.TimeMinutes, 6);
            Assert.Equal(80, other.Percent, 6);

            MouseUpAt(window, to);
            Assert.Equal(15, row.TimeMinutes, 6);
            Assert.Equal(25, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AnotherControlCapturingMidDrag_PutsTheRowBackAndKeepsItsCapture()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            Button other = window.FindControl<Button>("LoadButton")!;
            Point to = QuarterOf(graph, window);
            IPointer? pointer = null;
            graph.AddHandler(
                InputElement.PointerPressedEvent,
                (_, pressed) => pointer = pressed.Pointer,
                RoutingStrategies.Tunnel,
                handledEventsToo: true
            );

            MouseDownAt(window, PixelOf(graph, window, row.ToPlan()));
            MouseMoveTo(window, to);
            Assert.Equal(15, row.TimeMinutes, 6);
            Assert.NotNull(pointer);

            pointer.Capture(other);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(other, pointer.Captured);
            Assert.Equal(10, row.TimeMinutes, 6);
            Assert.Equal(20, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tab_SkipsTheGraph()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            ComboBox lastBefore = window.FindControl<ComboBox>("RemedyBox")!;
            Assert.False(window.FindControl<Button>("ExtendButton")!.IsEffectivelyEnabled);
            lastBefore.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(lastBefore.IsFocused);

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(lastBefore.IsFocused);
            Assert.False(graph.IsFocused);
            Assert.False(graph.IsTabStop);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PressAwayFromEveryDot_ChangesNothing()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            GraphPlot plot = PlotOf(graph);

            DragDot(window, ToWindow(graph, window, plot.Right - 5, plot.Top + 5), ToWindow(graph, window, plot.Left + 5, plot.Bottom - 5));

            Assert.Equal(10, row.TimeMinutes, 6);
            Assert.Equal(20, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EscapeMidDrag_PutsTheRowBack()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            Point to = QuarterOf(graph, window);

            MouseDownAt(window, PixelOf(graph, window, row.ToPlan()));
            MouseMoveTo(window, to);
            Assert.Equal(15, row.TimeMinutes, 6);

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(10, row.TimeMinutes, 6);
            Assert.Equal(20, row.Percent, 6);

            MouseUpAt(window, to);
            Assert.Equal(10, row.TimeMinutes, 6);
            Assert.Equal(20, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CaptureLostMidDrag_PutsTheRowBack()
    {
        SpawnPlannerViewModel vm = Planner(Session(6));
        TwoPoints(vm);
        SpawnPlannerWindow window = ShowWorkload(vm, new UserPreferences());
        try
        {
            WorkloadGraph graph = GraphOf(window);
            WorkloadPointRowViewModel row = vm.ControlPoints[0];
            Point to = QuarterOf(graph, window);
            IPointer? pointer = null;
            graph.AddHandler(
                InputElement.PointerPressedEvent,
                (_, pressed) => pointer = pressed.Pointer,
                RoutingStrategies.Tunnel,
                handledEventsToo: true
            );

            MouseDownAt(window, PixelOf(graph, window, row.ToPlan()));
            MouseMoveTo(window, to);
            Assert.Equal(15, row.TimeMinutes, 6);
            Assert.NotNull(pointer);

            graph.RaiseEvent(new PointerCaptureLostEventArgs(graph, pointer));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(10, row.TimeMinutes, 6);
            Assert.Equal(20, row.Percent, 6);
        }
        finally
        {
            window.Close();
        }
    }

    private static void TwoPoints(SpawnPlannerViewModel vm)
    {
        vm.ControlPoints.Clear();
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(10, 20));
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(30, 80));
    }

    private static SpawnPlannerWindow ShowWorkload(SpawnPlannerViewModel vm, UserPreferences preferences)
    {
        vm.Mode = SpawnPlannerMode.Workload;
        var window = SpawnPlannerWindow.Create(vm, preferences, "Spawn plan");
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static WorkloadGraph GraphOf(SpawnPlannerWindow window) => window.FindControl<WorkloadGraph>("Graph")!;

    private static GraphPlot PlotOf(WorkloadGraph graph) => WorkloadGraph.PlotOf(graph.Snapshot.Width, graph.Snapshot.Height);

    private static Point ToWindow(WorkloadGraph graph, SpawnPlannerWindow window, double x, double y) =>
        graph.TranslatePoint(new Point(x, y), window)!.Value;

    private static Point PixelOf(WorkloadGraph graph, SpawnPlannerWindow window, SpawnPlanPoint point)
    {
        (double X, double Y) at = WorkloadGraphGeometry.PointPixel(PlotOf(graph), graph.Snapshot.LengthSec, point)!.Value;
        return ToWindow(graph, window, at.X, at.Y);
    }

    // A quarter of the way across and a quarter of the way up the plot: a drag there rounds to a fifteenth minute and 25 %.
    private static Point QuarterOf(WorkloadGraph graph, SpawnPlannerWindow window)
    {
        GraphPlot plot = PlotOf(graph);
        return ToWindow(graph, window, plot.Left + (plot.Width / 4), plot.Bottom - (plot.Height / 4));
    }

    private static double NumberIn(TextBox box)
    {
        Assert.NotNull(box.Text);
        return double.Parse(box.Text, CultureInfo.CurrentCulture);
    }

    private static TextBox RowBox(SpawnPlannerWindow window, WorkloadPointRowViewModel row, int column) =>
        window
            .FindControl<ItemsControl>("PointList")!
            .GetVisualDescendants()
            .OfType<TextBox>()
            .First(box => (box.DataContext == row) && (Grid.GetColumn(box) == column));

    private static void DragDot(Window window, Point from, Point to)
    {
        MouseDownAt(window, from);
        MouseMoveTo(window, to);
        MouseUpAt(window, to);
    }

    private static void MouseDownAt(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void MouseMoveTo(Window window, Point at)
    {
        window.MouseMove(at, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
    }

    private static void MouseUpAt(Window window, Point at)
    {
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
