using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.UI.Tests.ViewModels;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The "Speech benchmark" window in each of the decision mock's lane C states (never run, running, done, one failed row, no
/// GPU runtime): what it shows and hides, and that no binding of it logs an error.
/// </summary>
public sealed class SpeechBenchmarkWindowTests : IDisposable
{
    private const int HangGuardMs = 300_000;
    private const string NoGpuRuntime = "needs the GPU runtime (not installed)";

    private static readonly SttBenchPlanRow Fp32AccurateCpu = new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu, null);
    private static readonly SttBenchPlanRow Fp32AccurateGpu = new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu, null);
    private static readonly SttBenchPlanRow Int8FastCpu = new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu, null);

    private readonly string _root = Directory.CreateTempSubdirectory("speech-benchmark-window-").FullName;
    private readonly BenchFakes _fakes = new();
    private readonly BindingErrors _bindingErrors = new();
    private readonly ILogSink? _previousSink = Logger.Sink;

    public SpeechBenchmarkWindowTests() => Logger.Sink = _bindingErrors;

    public void Dispose()
    {
        Logger.Sink = _previousSink;
        Directory.Delete(_root, recursive: true);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public void NeverRun_ShowsTheIntroTheRunCountAndNoResults()
    {
        (SpeechBenchmarkWindow window, _) = Open(Fp32AccurateCpu, Int8FastCpu);
        try
        {
            Assert.True(Visible(window, "IntroText"));
            Assert.True(Visible(window, "PushToTalkNote"));
            Assert.True(Visible(window, "EmptyText"));
            Assert.Equal("2 runs", Text(window, "RunsText"));
            Assert.True(Visible(window, "RunsText"));
            Assert.Equal("Run benchmark", window.FindControl<Button>("RunButton")!.Content);
            Assert.True(Visible(window, "RunButton"));
            Assert.False(Visible(window, "CancelButton"));
            Assert.False(Visible(window, "RunProgress"));
            Assert.Equal("Pick a row to apply it", Text(window, "SelectionText"));
            Assert.False(window.FindControl<Button>("ApplyButton")!.IsEffectivelyEnabled);
            Assert.Empty(_bindingErrors.Messages);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Running_ShowsCancelAndTheMeasuringRow_AndCloseAsksFirst()
    {
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Open(Fp32AccurateCpu, Int8FastCpu);
        SttBenchService service = _fakes.Service!;
        _fakes.Runner.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = vm.RunCommand.ExecuteAsync(null);
        try
        {
            await _fakes.Runner.RowStarted.Task;
            Layout(window);

            Assert.False(Visible(window, "RunButton"));
            Assert.True(Visible(window, "CancelButton"));
            Assert.True(Visible(window, "RunProgress"));
            Assert.False(Visible(window, "PushToTalkNote"));
            Assert.Equal("Running 1 of 2: rasr-parakeet v2 · fp32 · Accurate · CPU", Text(window, "StatusText"));
            Assert.True(ShowsText(window, "measuring…"));
            Assert.True(ShowsText(window, "waiting"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<RadioButton>(), radio => radio.IsEffectivelyVisible);

            window.Close();
            Layout(window);
            Assert.True(window.IsVisible, "Close while a run goes on asks first");
            Assert.True(Visible(window, "AskBar"));
            Assert.False(Visible(window, "Footer"));

            window.FindControl<Button>("KeepRunningButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
            Assert.Equal(SttBenchState.Running, service.State);
            Assert.Empty(_bindingErrors.Messages);
        }
        finally
        {
            _fakes.Runner.Hold.TrySetResult();
            await run;
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Done_ShowsTheFiguresTheRadiosAndTheCurrentRow()
    {
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Open(Fp32AccurateCpu, Fp32AccurateGpu);
        try
        {
            await vm.RunCommand.ExecuteAsync(null);
            Layout(window);

            Assert.Equal("Last run 1 Jan 2000, 00:00 · 2 of 2 ran", Text(window, "StatusText"));
            Assert.Equal("Run again", window.FindControl<Button>("RunButton")!.Content);
            Assert.False(Visible(window, "IntroText"));
            Assert.False(Visible(window, "EmptyText"));
            Assert.False(Visible(window, "RunsText"));
            Assert.True(ShowsText(window, "6.2 s"));
            Assert.True(ShowsText(window, "3.6 GB"));
            Assert.True(ShowsText(window, "7.8%"));
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text == "current") && t.IsEffectivelyVisible);
            RadioButton[] radios = [.. window.GetVisualDescendants().OfType<RadioButton>()];
            Assert.Equal(2, radios.Length);
            Assert.All(radios, radio => Assert.True(radio.IsEffectivelyVisible && radio.IsEffectivelyEnabled));

            radios[1].IsChecked = true;
            Layout(window);
            Assert.Equal("Selected: rasr-parakeet v2 · fp32 · Accurate · GPU", Text(window, "SelectionText"));
            Assert.True(window.FindControl<Button>("ApplyButton")!.IsEffectivelyEnabled);
            Assert.Equal("Restart YAAT to apply: Device changes from CPU to GPU.", Text(window, "RestartNote"));
            Assert.True(Visible(window, "RestartNote"));
            Assert.Empty(_bindingErrors.Messages);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task OneFailedRow_ShowsTheFailureInTheRowAndTheStatusLine()
    {
        _fakes.Runner.Outcome = job =>
            (job.Engine == SttEngine.RasrV2Int8)
                ? SttBenchRowOutcome.Ran(SttBenchResult.Failed("native crashed (exit -1073741819)", 0, null))
                : SttBenchRowOutcome.Ran(SttBenchResult.Measured(BenchFakes.Timing(job.Device), 12, job.Mode));
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Open(Fp32AccurateCpu, Int8FastCpu);
        try
        {
            await vm.RunCommand.ExecuteAsync(null);
            Layout(window);

            Assert.Equal("Last run 1 Jan 2000, 00:00 · 1 of 2 ran", Text(window, "StatusText"));
            Assert.Equal("1 failed", Text(window, "FailedText"));
            Assert.True(Visible(window, "FailedText"));
            Assert.True(Visible(window, "FailedDetails"));
            Assert.True(ShowsText(window, "failed: native crashed (exit -1073741819)"));
            Assert.True(ShowsText(window, "410"));
            RadioButton[] radios = [.. window.GetVisualDescendants().OfType<RadioButton>()];
            Assert.Equal([true, false], radios.Select(radio => radio.IsEffectivelyEnabled));
            Assert.Empty(_bindingErrors.Messages);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task NoGpuRuntime_ShowsTheNoteAndGreysTheGpuRowWithTheReason()
    {
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Open(Fp32AccurateCpu, Fp32AccurateGpu with { SkipReason = NoGpuRuntime });
        try
        {
            await vm.RunCommand.ExecuteAsync(null);
            Layout(window);

            Assert.True(Visible(window, "GpuRuntimeNote"));
            Assert.Equal("Last run 1 Jan 2000, 00:00 · 1 of 2 ran", Text(window, "StatusText"));
            TextBlock reason = Assert.Single(
                window.GetVisualDescendants().OfType<TextBlock>(),
                t => (t.Text == NoGpuRuntime) && t.IsEffectivelyVisible
            );
            Grid row = reason.FindAncestorOfType<Grid>()!;
            Assert.Equal(0.5, row.Opacity);
            Assert.False(Visible(window, "FailedText"));
            Assert.Empty(_bindingErrors.Messages);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task ARefusedClose_KeepsTheWindowRegisteredWithItsGeometryHelper()
    {
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Open(Fp32AccurateCpu);
        _fakes.Runner.Hold = Gate();
        Task run = vm.RunCommand.ExecuteAsync(null);
        try
        {
            await _fakes.Runner.RowStarted.Task;
            Layout(window);

            window.Close();
            Layout(window);

            Assert.True(window.IsVisible, "Close while a run goes on asks first");
            Assert.Contains(WindowGeometryHelper.GetActiveHelpers(), helper => helper.WindowName == "SpeechBenchmark");
        }
        finally
        {
            _fakes.Runner.Hold.TrySetResult();
            await run;
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task ClosingDuringUnloading_IsRefusedWithNoAskBar()
    {
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Open(Fp32AccurateCpu);
        _fakes.Suspender.HoldSuspend = Gate();
        Task run = vm.RunCommand.ExecuteAsync(null);
        try
        {
            await _fakes.Suspender.SuspendStarted.Task;
            Layout(window);

            window.Close();
            Layout(window);

            Assert.False(window.FindControl<Button>("ApplyButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("CloseButton")!.IsEffectivelyEnabled);
            Assert.True(window.IsVisible, "Close while the speech models unload is refused");
            Assert.False(Visible(window, "AskBar"));
            Assert.True(Visible(window, "Footer"));
            Assert.Contains(WindowGeometryHelper.GetActiveHelpers(), helper => helper.WindowName == "SpeechBenchmark");
        }
        finally
        {
            _fakes.Suspender.HoldSuspend.TrySetResult();
            await run;
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task CancelTheRun_ClosesTheWindowOnceIdle()
    {
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Open(Fp32AccurateCpu, Int8FastCpu);
        _fakes.Runner.Hold = Gate();
        _fakes.Suspender.HoldResume = Gate();
        Task run = vm.RunCommand.ExecuteAsync(null);
        try
        {
            await _fakes.Runner.RowStarted.Task;
            Layout(window);
            window.Close();
            Layout(window);

            window.FindControl<Button>("CancelRunButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _fakes.Suspender.ResumeStarted.Task;
            Layout(window);
            Assert.True(window.IsVisible, "The window stays until the speech models are back");
            Assert.False(Visible(window, "AskBar"));

            _fakes.Suspender.HoldResume.SetResult();
            await run;
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.IsVisible);
            Assert.Equal(SttBenchState.Idle, _fakes.Service!.State);
        }
        finally
        {
            _fakes.Suspender.HoldResume.TrySetResult();
            _fakes.Runner.Hold.TrySetResult();
            await run;
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }

    // Headless Avalonia has no application shutdown to raise, so the owner closing is the shutdown path tested here: closing
    // the app's window closes the windows it owns with WindowCloseReason.OwnerWindowClosing.
    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task AShutdownClose_ClosesMidRun()
    {
        var owner = new Window();
        owner.Show();
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Build(Fp32AccurateCpu);
        window.Show(owner);
        Layout(window);
        _fakes.Runner.Hold = Gate();
        Task run = vm.RunCommand.ExecuteAsync(null);
        try
        {
            await _fakes.Runner.RowStarted.Task;
            Layout(window);

            owner.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.False(owner.IsVisible, "The window must not hold up its owner's close");
            Assert.False(window.IsVisible);
            Assert.Equal(SttBenchState.Running, _fakes.Service!.State);
        }
        finally
        {
            _fakes.Runner.Hold.TrySetResult();
            await run;
            Dispatcher.UIThread.RunJobs();
            window.Close();
            owner.Close();
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool Visible(Window window, string name) => window.FindControl<Control>(name)!.IsEffectivelyVisible;

    private static string? Text(Window window, string name) => window.FindControl<TextBlock>(name)!.Text;

    private static bool ShowsText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text == text) && t.IsEffectivelyVisible);

    private (SpeechBenchmarkWindow Window, SpeechBenchmarkViewModel Vm) Open(params SttBenchPlanRow[] rows)
    {
        (SpeechBenchmarkWindow window, SpeechBenchmarkViewModel vm) = Build(rows);
        window.ShowAndRunLayout();
        return (window, vm);
    }

    private (SpeechBenchmarkWindow Window, SpeechBenchmarkViewModel Vm) Build(params SttBenchPlanRow[] rows)
    {
        _fakes.Plan = rows;
        var folders = new SttBenchFolders(
            Path.Combine(_root, "models"),
            Path.Combine(_root, "clips"),
            Path.Combine(_root, "runs"),
            Path.Combine(_root, "last-run.json")
        );
        _fakes.Service = new SttBenchService(_fakes.PlanAsync, _fakes.Runner, _fakes.Suspender, folders, new FakeTimeProvider());
        var vm = new SpeechBenchmarkViewModel(
            _fakes.Service,
            _fakes.PlanAsync,
            _ => true,
            SpeechBenchmarkViewModelTests.Reference(_root),
            () => (SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu),
            (_, _, _) => Task.CompletedTask,
            () => SttDevice.Cpu,
            TimeZoneInfo.Utc
        );
        return (new SpeechBenchmarkWindow(vm, new UserPreferences()), vm);
    }

    private sealed class BindingErrors : ILogSink
    {
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => (level >= LogEventLevel.Warning) && (area == LogArea.Binding);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Messages.Add(messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Messages.Add($"{messageTemplate} {string.Join(", ", propertyValues)}");
    }
}
