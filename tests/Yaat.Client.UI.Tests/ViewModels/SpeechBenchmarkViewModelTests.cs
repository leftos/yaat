using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The speech benchmark window's view model over a real <see cref="SttBenchService"/> whose rows and suspender are fakes: the
/// mock's five states (never run, running, done, one failed row, no GPU runtime), the transitions, the close path and Apply.
/// </summary>
public sealed class SpeechBenchmarkViewModelTests : IDisposable
{
    private const int HangGuardMs = 300_000;
    private const string NoGpuRuntime = "needs the GPU runtime (not installed)";

    private static readonly SttBenchPlanRow Fp32AccurateCpu = new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu, null);
    private static readonly SttBenchPlanRow Fp32AccurateGpu = new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu, null);
    private static readonly SttBenchPlanRow Fp32AccurateGpuSkipped = Fp32AccurateGpu with { SkipReason = NoGpuRuntime };
    private static readonly SttBenchPlanRow Int8FastCpu = new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu, null);
    private static readonly SttBenchPlanRow Int8FastGpuSkipped = new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu, NoGpuRuntime);

    private readonly string _root = Directory.CreateTempSubdirectory("speech-benchmark-vm-").FullName;
    private readonly BenchFakes _fakes = new();
    private readonly List<(SttEngine Engine, SttMode Mode, SttDevice Device)> _applied = [];
    private readonly HashSet<SttEngine> _deleted = [];
    private Exception? _planFault;
    private Exception? _applyFault;
    private (SttEngine Engine, SttMode Mode, SttDevice Device) _current = (SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [AvaloniaFact(Timeout = HangGuardMs)]
    public void NeverRun_ShowsTheIntroAndNoResults()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu));

        Assert.True(vm.ShowsIntro);
        Assert.Equal(
            "Runs each downloaded model in each mode on each device, on 12 short clips, and measures latency and memory on this machine. "
                + "WER and callsign recall come from YAAT's reference run, not from this machine.",
            vm.IntroText
        );
        Assert.Equal("Push-to-talk is off during a run: YAAT unloads its speech models first and reloads them after.", vm.PushToTalkNote);
        Assert.True(vm.ShowsEmpty);
        Assert.Empty(vm.Rows);
        Assert.Equal("Run benchmark", vm.RunButtonText);
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task NeverRun_RunsTextCountsOnlyTheRunnableRows()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Fp32AccurateGpuSkipped, Int8FastCpu, Int8FastGpuSkipped));

        await vm.LoadPlanAsync();

        Assert.Equal("2 runs", vm.RunsText);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task NeverRun_OneRunnableRow_SaysOneRun()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Fp32AccurateGpuSkipped));

        await vm.LoadPlanAsync();

        Assert.Equal("1 run", vm.RunsText);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task ALoadPlanFault_ClearsRunsTextAndLogs()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await vm.LoadPlanAsync();
        Assert.Equal("2 runs", vm.RunsText);
        _planFault = new InvalidOperationException("the plan broke");
        long before = AppLog.RecentErrors.LastSequence;

        await vm.LoadPlanAsync();

        Assert.Equal("", vm.RunsText);
        Assert.Contains(
            AppLog.RecentErrors.Since(before, 50),
            entry => entry.Message.Contains("Listing the speech benchmark rows failed", StringComparison.Ordinal)
        );
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public void AMalformedLastRunRow_ShowsFailedNoResultAndCannotBePicked()
    {
        SttBenchLastRunStore.Save(
            Path.Combine(_root, "last-run.json"),
            new SttBenchLastRun(DateTimeOffset.UnixEpoch, [new SttBenchLastRow(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu, null, null)])
        );
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu));

        SpeechBenchmarkRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal("failed: no result", row.MessageText);
        Assert.True(row.IsFailed);
        Assert.False(row.IsPickable);
        row.IsSelected = true;
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task AViewModelBuiltOverARunningService_ShowsTheMeasuringRowAndDisablesRun()
    {
        SttBenchService service = Service(Fp32AccurateCpu, Int8FastCpu);
        using SpeechBenchmarkViewModel first = Vm(service);
        Task run = await StartAndHoldTheFirstRowAsync(first);

        using SpeechBenchmarkViewModel vm = Vm(service);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsBusy);
        Assert.True(vm.IsRunning);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.True(vm.Rows[0].IsMeasuring);
        Assert.Equal("waiting", vm.Rows[1].MessageText);
        Assert.Equal("Running 1 of 2: rasr-parakeet v2 · fp32 · Accurate · CPU", vm.StatusText);

        await FinishAsync(run);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Running_MarksTheCurrentRowMeasuringAndTheRestWaiting()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Fp32AccurateGpuSkipped, Int8FastCpu));
        Task run = await StartAndHoldTheFirstRowAsync(vm);

        Assert.True(vm.Rows[0].IsMeasuring);
        Assert.Equal("measuring…", vm.Rows[0].MessageText);
        Assert.True(vm.Rows[1].IsGreyed);
        Assert.Equal(NoGpuRuntime, vm.Rows[1].MessageText);
        Assert.Equal("waiting", vm.Rows[2].MessageText);
        Assert.False(vm.Rows[2].IsMeasuring);
        Assert.True(vm.IsRunning);

        await FinishAsync(run);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Running_StatusNamesTheRowAndTheCount()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        Task run = await StartAndHoldTheFirstRowAsync(vm);

        Assert.Equal("Running 1 of 2: rasr-parakeet v2 · fp32 · Accurate · CPU", vm.StatusText);
        Assert.Equal(0, vm.ProgressValue);
        Assert.Equal(2, vm.ProgressMaximum);

        await FinishAsync(run);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Done_ShowsFiguresAndTheLastRunLine()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Fp32AccurateGpu));

        await RunToTheEndAsync(vm);

        Assert.Equal("Last run 1 Jan 2000, 00:00 · 2 of 2 ran", vm.StatusText);
        Assert.Null(vm.FailedText);
        Assert.Equal("Run again", vm.RunButtonText);
        Assert.False(vm.ShowsIntro);
        Assert.False(vm.ShowsEmpty);
        SpeechBenchmarkRowViewModel cpu = vm.Rows[0];
        Assert.Null(cpu.MessageText);
        string[] cpuShown =
        [
            cpu.ModelText,
            cpu.ModeText,
            cpu.DeviceText,
            cpu.LoadText,
            cpu.P50Text,
            cpu.P90Text,
            cpu.RamText,
            cpu.VramText,
            cpu.WerText,
            cpu.RecallText,
        ];
        Assert.Equal(["rasr-parakeet v2 · fp32", "Accurate", "CPU", "6.2 s", "410", "640", "1.1 GB", "—", "7.8%", "96%"], cpuShown);
        string[] gpuShown = [vm.Rows[1].DeviceText, vm.Rows[1].VramText, vm.Rows[1].WerText, vm.Rows[1].RecallText];
        Assert.Equal(["GPU", "3.6 GB", "7.8%", "96%"], gpuShown);
        Assert.True(cpu.IsCurrent);
        Assert.False(vm.Rows[1].IsCurrent);
        Assert.True(cpu.IsPickable);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task OneFailedRow_ShowsTheFailureAndTheOtherRowsStillShow()
    {
        _fakes.Runner.Outcome = job => (job.Engine == SttEngine.RasrV2Int8) ? Failed() : Measured(job);
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));

        await RunToTheEndAsync(vm);

        Assert.Equal("Last run 1 Jan 2000, 00:00 · 1 of 2 ran", vm.StatusText);
        Assert.Equal("1 failed", vm.FailedText);
        Assert.True(vm.Rows[1].IsFailed);
        Assert.Equal("failed: native crashed (exit -1073741819)", vm.Rows[1].MessageText);
        Assert.Null(vm.Rows[0].MessageText);
        Assert.Equal("410", vm.Rows[0].P50Text);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task OneFailedRow_IsNotPickable()
    {
        _fakes.Runner.Outcome = job => (job.Engine == SttEngine.RasrV2Int8) ? Failed() : Measured(job);
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await RunToTheEndAsync(vm);

        vm.Rows[1].IsSelected = true;

        Assert.False(vm.Rows[1].IsPickable);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task NoGpuRuntime_GreysGpuRowsWithTheReasonAndShowsTheNote()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Fp32AccurateGpuSkipped));

        await RunToTheEndAsync(vm);

        Assert.True(vm.ShowsGpuRuntimeNote);
        Assert.Equal("The GPU rows need the GPU runtime: install it in Settings > Speech > Device, then run again.", vm.GpuRuntimeNote);
        SpeechBenchmarkRowViewModel gpu = vm.Rows[1];
        Assert.True(gpu.IsGreyed);
        Assert.Equal(NoGpuRuntime, gpu.MessageText);
        Assert.False(gpu.IsPickable);
        Assert.Equal("7.8%", gpu.WerText);
        Assert.Equal("Last run 1 Jan 2000, 00:00 · 1 of 2 ran", vm.StatusText);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Unloading_DisablesAndShowsTheMessage()
    {
        _fakes.Suspender.HoldSuspend = Gate();
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu));
        Task run = vm.RunCommand.ExecuteAsync(null);
        await _fakes.Suspender.SuspendStarted.Task;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsTransitioning);
        Assert.True(vm.IsBusy);
        Assert.Equal("Unloading speech models…", vm.StatusText);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.False(vm.ApplyCommand.CanExecute(null));

        _fakes.Suspender.HoldSuspend.SetResult();
        await FinishAsync(run);
        Assert.False(vm.IsTransitioning);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Reloading_DisablesAndShowsTheMessage()
    {
        _fakes.Suspender.HoldResume = Gate();
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu));
        Task run = vm.RunCommand.ExecuteAsync(null);
        await _fakes.Suspender.ResumeStarted.Task;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsTransitioning);
        Assert.True(vm.IsBusy);
        Assert.Equal("Reloading speech models…", vm.StatusText);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.False(vm.ApplyCommand.CanExecute(null));

        _fakes.Suspender.HoldResume.SetResult();
        await FinishAsync(run);
        Assert.False(vm.IsTransitioning);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task ClosePath_IdleClosesTransitionRefusesRunningAsks()
    {
        _fakes.Suspender.HoldSuspend = Gate();
        _fakes.Runner.Hold = Gate();
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu));
        Assert.Equal(CloseDecision.Close, vm.RequestClose());

        Task run = vm.RunCommand.ExecuteAsync(null);
        await _fakes.Suspender.SuspendStarted.Task;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(CloseDecision.Refuse, vm.RequestClose());
        Assert.False(vm.IsAskingToCancel);

        _fakes.Suspender.HoldSuspend.SetResult();
        await _fakes.Runner.RowStarted.Task;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(CloseDecision.Ask, vm.RequestClose());
        Assert.True(vm.IsAskingToCancel);

        await FinishAsync(run);
        Assert.False(vm.IsAskingToCancel);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task KeepRunning_ClearsTheAsk()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu));
        Task run = await StartAndHoldTheFirstRowAsync(vm);
        Assert.Equal(CloseDecision.Ask, vm.RequestClose());

        vm.KeepRunning();

        Assert.False(vm.IsAskingToCancel);
        Assert.True(vm.IsRunning);
        await FinishAsync(run);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task CancelTheRun_ClosesOnceIdle()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        int closeRequests = 0;
        vm.CloseRequested += () => closeRequests++;
        Task run = await StartAndHoldTheFirstRowAsync(vm);
        vm.RequestClose();

        vm.CancelAndClose();
        Assert.Equal(0, closeRequests);

        await run;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, closeRequests);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
        Assert.Equal(["row RasrV2Fp32 Accurate Cpu"], _fakes.Rows);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task ACancelledRun_ShowsTheLastFinishedRun()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await CancelInTheFirstRowAsync(vm);
        Assert.True(vm.ShowsEmpty);
        Assert.Equal("Run benchmark", vm.RunButtonText);

        await RunToTheEndAsync(vm);
        await CancelInTheFirstRowAsync(vm);

        Assert.Equal("Last run 1 Jan 2000, 00:00 · 2 of 2 ran", vm.StatusText);
        Assert.All(vm.Rows, row => Assert.Null(row.MessageText));
        Assert.Equal(["410", "410"], vm.Rows.Select(r => r.P50Text));
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task ADeletedModel_KeepsItsFiguresAndCannotBePicked()
    {
        SttBenchService service = Service(Fp32AccurateCpu, Int8FastCpu);
        using (SpeechBenchmarkViewModel first = Vm(service))
        {
            await RunToTheEndAsync(first);
        }
        _deleted.Add(SttEngine.RasrV2Int8);

        using SpeechBenchmarkViewModel vm = Vm(service);

        SpeechBenchmarkRowViewModel deleted = vm.Rows[1];
        Assert.Null(deleted.MessageText);
        Assert.Equal("6.2 s", deleted.LoadText);
        Assert.False(deleted.IsPickable);
        Assert.True(vm.Rows[0].IsPickable);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Apply_CallsApplyWithThePickedRowAndRemarksCurrent()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await RunToTheEndAsync(vm);
        vm.Rows[0].IsSelected = true;

        vm.Rows[1].IsSelected = true;

        Assert.False(vm.Rows[0].IsSelected);
        Assert.Same(vm.Rows[1], vm.SelectedRow);
        Assert.Equal("Selected: rasr-parakeet v2 · int8 · Fast · CPU", vm.SelectionText);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal([(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu)], _applied);
        Assert.Equal([false, true], vm.Rows.Select(r => r.IsCurrent));
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Apply_IsRefusedForAModelDeletedWhileTheWindowIsOpen()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await RunToTheEndAsync(vm);
        vm.Rows[1].IsSelected = true;
        Assert.True(vm.ApplyCommand.CanExecute(null));

        _deleted.Add(SttEngine.RasrV2Int8);

        Assert.False(vm.ApplyCommand.CanExecute(null));
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(_applied);
        Assert.Equal("rasr-parakeet v2 · int8 is no longer downloaded; download it again to apply this row.", vm.ErrorText);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task AnApplyFailure_ShowsTheError()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await RunToTheEndAsync(vm);
        vm.Rows[1].IsSelected = true;
        _applyFault = new IOException("the preferences file is locked");
        long before = AppLog.RecentErrors.LastSequence;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Apply failed: the preferences file is locked", vm.ErrorText);
        Assert.Equal([true, false], vm.Rows.Select(r => r.IsCurrent));
        Assert.Contains(
            AppLog.RecentErrors.Since(before, 50),
            entry => entry.Message.Contains("Applying the speech benchmark row", StringComparison.Ordinal)
        );
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Apply_IsDisabledUntilARowIsPicked()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await RunToTheEndAsync(vm);

        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("Pick a row to apply it", vm.SelectionText);

        vm.Rows[1].IsSelected = true;

        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task Apply_IsDisabledWhileBusy()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Int8FastCpu));
        await RunToTheEndAsync(vm);
        _fakes.HoldPlan = Gate();

        // Until the next run's plan is in, the table still shows the last run's rows, figures and all. The plan is asked
        // for before ExecuteAsync returns, so the run is held there by now.
        Task run = vm.RunCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.Rows[1].IsSelected = true;

        Assert.True(vm.IsBusy);
        Assert.True(vm.Rows[1].IsPickable);
        Assert.False(vm.ApplyCommand.CanExecute(null));

        _fakes.HoldPlan.SetResult();
        await FinishAsync(run);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task RestartNote_ShowsOnlyWhenTheDeviceDiffers()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu, Fp32AccurateGpu));
        await RunToTheEndAsync(vm);

        vm.Rows[0].IsSelected = true;
        Assert.Null(vm.RestartNote);

        vm.Rows[1].IsSelected = true;
        Assert.Equal("Restart YAAT to apply: Device changes from CPU to GPU.", vm.RestartNote);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task MissingReference_ShowsDashes()
    {
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu), reference: null);

        await RunToTheEndAsync(vm);

        string[] reference = [vm.Rows[0].WerText, vm.Rows[0].RecallText];
        Assert.Equal(["—", "—"], reference);
        Assert.Equal("410", vm.Rows[0].P50Text);
    }

    [AvaloniaFact(Timeout = HangGuardMs)]
    public async Task ARunFault_IsLoggedAndShown()
    {
        _fakes.Runner.Throw = new InvalidOperationException("the row runner broke");
        using SpeechBenchmarkViewModel vm = Vm(Service(Fp32AccurateCpu));
        long before = AppLog.RecentErrors.LastSequence;

        await RunToTheEndAsync(vm);

        Assert.Equal("The benchmark stopped: the row runner broke", vm.ErrorText);
        Assert.Contains(
            AppLog.RecentErrors.Since(before, 50),
            entry => entry.Message.Contains("speech benchmark run failed", StringComparison.Ordinal)
        );
        Assert.False(vm.IsBusy);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SttBenchRowOutcome Measured(SttBenchJob job) =>
        SttBenchRowOutcome.Ran(SttBenchResult.Measured(BenchFakes.Timing(job.Device), 12, job.Mode));

    private static SttBenchRowOutcome Failed() => SttBenchRowOutcome.Ran(SttBenchResult.Failed("native crashed (exit -1073741819)", 0, null));

    private async Task FinishAsync(Task run)
    {
        _fakes.Runner.Hold?.TrySetResult();
        await run;
        _fakes.Runner.Hold = null;
        Dispatcher.UIThread.RunJobs();
    }

    private async Task RunToTheEndAsync(SpeechBenchmarkViewModel vm) => await FinishAsync(vm.RunCommand.ExecuteAsync(null));

    private async Task<Task> StartAndHoldTheFirstRowAsync(SpeechBenchmarkViewModel vm)
    {
        _fakes.Runner.Hold = Gate();
        _fakes.Runner.RowStarted = Gate();
        Task run = vm.RunCommand.ExecuteAsync(null);
        await _fakes.Runner.RowStarted.Task;
        Dispatcher.UIThread.RunJobs();
        return run;
    }

    private async Task CancelInTheFirstRowAsync(SpeechBenchmarkViewModel vm)
    {
        Task run = await StartAndHoldTheFirstRowAsync(vm);
        vm.CancelCommand.Execute(null);
        await FinishAsync(run);
    }

    private SttBenchService Service(params SttBenchPlanRow[] rows)
    {
        _fakes.Plan = rows;
        var folders = new SttBenchFolders(
            Path.Combine(_root, "models"),
            Path.Combine(_root, "clips"),
            Path.Combine(_root, "runs"),
            Path.Combine(_root, "last-run.json")
        );
        return new SttBenchService(_fakes.PlanAsync, _fakes.Runner, _fakes.Suspender, folders, new FakeTimeProvider());
    }

    private SpeechBenchmarkViewModel Vm(SttBenchService service) => Vm(service, Reference(_root));

    private SpeechBenchmarkViewModel Vm(SttBenchService service, SttReference? reference) =>
        new(
            service,
            () =>
                (_planFault is null)
                    ? Task.FromResult<IReadOnlyList<SttBenchPlanRow>>(_fakes.Plan)
                    : Task.FromException<IReadOnlyList<SttBenchPlanRow>>(_planFault),
            engine => !_deleted.Contains(engine),
            reference,
            () => _current,
            (engine, mode, device) =>
            {
                if (_applyFault is not null)
                {
                    return Task.FromException(_applyFault);
                }
                _applied.Add((engine, mode, device));
                _current = (engine, mode, device);
                return Task.CompletedTask;
            },
            () => SttDevice.Cpu,
            TimeZoneInfo.Utc
        );

    /// <summary>A reference file with figures for fp32 Accurate only.</summary>
    internal static SttReference Reference(string root)
    {
        string path = Path.Combine(root, "stt-reference.json");
        File.WriteAllText(
            path,
            """{"schema":1,"corpus":"test","sttNative":"test","entries":["""
                + """{"engine":"RasrV2Fp32","mode":"Accurate","ownerWer":0.078,"callsignRecall":0.961},"""
                + """{"engine":"RasrV2Int8","mode":"Fast","ownerWer":0.103,"callsignRecall":0.89}]}"""
        );
        return SttReference.Load(path);
    }
}

/// <summary>The plan, row runner and suspender a real <see cref="SttBenchService"/> runs over in the benchmark view tests.</summary>
internal sealed class BenchFakes
{
    public BenchFakes()
    {
        Runner = new FakeRunner(Rows);
        Suspender = new FakeSuspender();
    }

    public List<string> Rows { get; } = [];

    public FakeRunner Runner { get; }

    public FakeSuspender Suspender { get; }

    public IReadOnlyList<SttBenchPlanRow> Plan { get; set; } = [];

    public TaskCompletionSource? HoldPlan { get; set; }

    /// <summary>The service a window test built over these fakes.</summary>
    public SttBenchService? Service { get; set; }

    /// <summary>This machine's figures for a row on <paramref name="device"/>, as the mock's fp32 rows show them.</summary>
    public static CorpusWerTiming Timing(SttDevice device) =>
        new(device.ToString(), 6.2, 12, 0, 410, 640, 700, 100_000_000, 1_100_000_000, 0, (device == SttDevice.Gpu) ? 3_600_000_000 : -1, null);

    public async Task<IReadOnlyList<SttBenchPlanRow>> PlanAsync()
    {
        if (HoldPlan is not null)
        {
            await HoldPlan.Task;
            HoldPlan = null;
        }
        return Plan;
    }

    internal sealed class FakeRunner(List<string> rows) : ISttBenchRowRunner
    {
        public Func<SttBenchJob, SttBenchRowOutcome> Outcome { get; set; } =
            job => SttBenchRowOutcome.Ran(SttBenchResult.Measured(Timing(job.Device), 12, job.Mode));

        public Exception? Throw { get; set; }

        /// <summary>Holds each row open until it completes; the test waits on <see cref="RowStarted"/>, never on a delay.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Completed when a row starts.</summary>
        public TaskCompletionSource RowStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SttBenchRowOutcome> RunRowAsync(SttBenchJob job, TimeSpan deadline, CancellationToken ct)
        {
            lock (rows)
            {
                rows.Add($"row {job.Engine} {job.Mode} {job.Device}");
            }
            RowStarted.TrySetResult();
            if (Throw is not null)
            {
                throw Throw;
            }
            if (Hold is not null)
            {
                await Hold.Task.WaitAsync(ct);
            }
            await Task.Yield();
            return Outcome(job);
        }
    }

    internal sealed class FakeSuspender : ISttEngineSuspender
    {
        public TaskCompletionSource? HoldSuspend { get; set; }

        public TaskCompletionSource? HoldResume { get; set; }

        public TaskCompletionSource SuspendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SuspendAsync(CancellationToken ct)
        {
            SuspendStarted.TrySetResult();
            if (HoldSuspend is not null)
            {
                await HoldSuspend.Task.WaitAsync(ct);
            }
        }

        public async Task ResumeAsync(CancellationToken ct)
        {
            ResumeStarted.TrySetResult();
            if (HoldResume is not null)
            {
                await HoldResume.Task.WaitAsync(ct);
            }
        }
    }
}
