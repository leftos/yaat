using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>A whole benchmark run over fake rows, a fake child and a fake suspender; then the real suspender over a real engine holder.</summary>
public sealed class SttBenchServiceTests : IDisposable
{
    private const string SkipReason = "needs the GPU runtime (not installed)";

    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(5);

    private static readonly SttBenchPlanRow First = new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu, null);
    private static readonly SttBenchPlanRow Skipped = new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu, SkipReason);
    private static readonly SttBenchPlanRow Second = new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu, null);

    private static readonly SttBenchState[] FullCycle =
    [
        SttBenchState.Idle,
        SttBenchState.Unloading,
        SttBenchState.Running,
        SttBenchState.Reloading,
        SttBenchState.Idle,
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("stt-bench-service-").FullName;
    private readonly List<string> _events = [];
    private readonly FakeRunner _runner;
    private readonly FakeSuspender _suspender;

    public SttBenchServiceTests()
    {
        _runner = new FakeRunner(_events);
        _suspender = new FakeSuspender(_events);
    }

    private SttBenchFolders Folders =>
        new(Path.Combine(_root, "models"), Path.Combine(_root, "clips"), Path.Combine(_root, "runs"), Path.Combine(_root, "stt-benchmark.json"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Rows_Run_In_Order_One_At_A_Time_Inside_The_Suspension()
    {
        SttBenchService service = Service(First, Skipped, Second);

        await service.RunAsync(CancellationToken.None);

        Assert.Equal(["suspend", "row RasrV2Fp32 Accurate Cpu", "row RasrV2Int8 Fast Cpu", "resume"], _events);
        Assert.Equal(1, _runner.MostAtOnce);
        Assert.Equal(SttBenchState.Idle, service.State);
    }

    [Fact]
    public async Task A_Job_Points_At_The_Model_Folder_The_Clips_And_Its_Own_Run_Folder()
    {
        await Service(First, Second).RunAsync(CancellationToken.None);

        SttBenchJob job = _runner.Jobs[0];
        Assert.Equal(SttModelInstaller.ModelDir(Folders.ModelRoot, "rasr-parakeet-v2-fp32"), job.ModelDirectory);
        Assert.Equal(Folders.ClipsDirectory, job.ClipsDirectory);
        Assert.Equal(Folders.RunRoot, Path.GetDirectoryName(Path.GetDirectoryName(job.ResultPath)));
        Assert.NotEqual(Path.GetDirectoryName(job.ResultPath), Path.GetDirectoryName(_runner.Jobs[1].ResultPath));
    }

    [Fact]
    public async Task A_Finished_Run_Keeps_Every_Row_And_Survives_A_Restart()
    {
        SttBenchService service = Service(First, Skipped);

        await service.RunAsync(CancellationToken.None);

        Assert.Equal([Ran(First), SttBenchRowOutcome.Skip(SkipReason)], service.Rows.Select(row => row.Outcome));
        SttBenchLastRow[] expected =
        [
            new(First.Engine, First.Mode, First.Device, Ran(First).Result, null),
            new(Skipped.Engine, Skipped.Mode, Skipped.Device, null, SkipReason),
        ];
        Assert.Equal(expected, service.LastRun?.Rows);
        Assert.Equal(expected, Service(First).LastRun?.Rows);
    }

    [Fact]
    public async Task Progress_Goes_From_Zero_To_The_Rows_That_Run()
    {
        SttBenchService service = Service(First, Skipped, Second);
        var done = new List<int>();
        service.Changed += () =>
        {
            int current = service.Progress.Done;
            if ((done.Count == 0) || (done[^1] != current))
            {
                done.Add(current);
            }
        };

        await service.RunAsync(CancellationToken.None);

        Assert.Equal([0, 1, 2], done);
        Assert.Equal(2, service.Progress.Total);
    }

    [Fact]
    public async Task A_Row_That_Throws_Still_Resumes_And_Saves_No_Last_Run()
    {
        _runner.Throw = new InvalidOperationException("no process");
        SttBenchService service = Service(First, Second);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(CancellationToken.None));

        Assert.Equal(["suspend", "row RasrV2Fp32 Accurate Cpu", "resume"], _events);
        Assert.Null(service.LastRun);
        Assert.Equal(SttBenchState.Idle, service.State);
    }

    [Fact]
    public async Task A_Failed_Suspend_Still_Resumes_And_Runs_No_Row()
    {
        _suspender.Throw = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(First).RunAsync(CancellationToken.None));

        Assert.Equal(["suspend", "resume"], _events);
    }

    [Fact]
    public async Task A_Cancelled_Run_Stops_The_Remaining_Rows_Resumes_And_Keeps_The_Previous_Last_Run()
    {
        SttBenchService service = Service(First, Second);
        await service.RunAsync(CancellationToken.None);
        SttBenchLastRun? previous = service.LastRun;
        _events.Clear();
        using var cts = new CancellationTokenSource();
        _runner.Hold = new TaskCompletionSource();

        Task run = service.RunAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.Equal(["suspend", "row RasrV2Fp32 Accurate Cpu", "resume"], _events);
        Assert.Equal(previous, service.LastRun);
        Assert.Equal(previous?.Rows, Service(First).LastRun?.Rows);
    }

    [Fact]
    public async Task A_Second_Run_While_Running_Is_Refused_And_Leaves_The_First_Running()
    {
        SttBenchService service = Service(First);
        _runner.Hold = new TaskCompletionSource();

        Task run = service.RunAsync(CancellationToken.None);
        Assert.Equal(SttBenchState.Running, service.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(CancellationToken.None));
        _runner.Hold.SetResult();
        await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(["suspend", "row RasrV2Fp32 Accurate Cpu", "resume"], _events);
    }

    [Fact]
    public async Task An_Empty_Plan_Runs_Nothing_Suspends_Nothing_And_Saves_Nothing()
    {
        SttBenchService service = Service();

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(_events);
        Assert.Null(service.LastRun);
    }

    [Fact]
    public async Task Cancel_Kills_The_Running_Childs_Tree_Before_It_Returns()
    {
        var launcher = new HangingLauncher();
        var process = new SttBenchProcess(launcher, new FakeTimeProvider());
        var service = new SttBenchService(
            () => Task.FromResult<IReadOnlyList<SttBenchPlanRow>>([First]),
            process,
            _suspender,
            Folders,
            new FakeTimeProvider()
        );

        Task run = service.RunAsync(CancellationToken.None);
        service.Cancel();

        Assert.True(launcher.Child.KilledTree);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Cancel_With_No_Run_Does_Nothing() => Service(First).Cancel();

    [Fact]
    public async Task The_Stand_In_Refuses_A_Transcription_With_The_Users_Message()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        SttEngineSuspender suspender = Suspender(holder, () => Task.CompletedTask);

        await suspender.SuspendAsync(CancellationToken.None);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            holder.TranscribeAsync([0f], null, CancellationToken.None)
        );
        Assert.Equal("Push-to-talk is off while the speech benchmark runs", refused.Message);
    }

    [Fact]
    public async Task Suspending_Disposes_The_Live_Engine_So_Its_Memory_Is_Free_For_The_Run()
    {
        var live = new FakeEngine();
        using var holder = new SwappableSttEngine(live);

        await Suspender(holder, () => Task.CompletedTask).SuspendAsync(CancellationToken.None);

        Assert.True(live.Disposed);
    }

    [Fact]
    public async Task A_Swap_Requested_During_A_Run_Leaves_A_Stand_In_And_Builds_Nothing()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        SttEngineSuspender suspender = Suspender(holder, () => Task.CompletedTask);
        await suspender.SuspendAsync(CancellationToken.None);
        bool built = false;

        await holder.SwapToLatestAsync(
            () =>
                suspender.StandInWhileSuspended(() =>
                {
                    built = true;
                    return Task.FromResult<ISttEngine>(new FakeEngine());
                }),
            CancellationToken.None
        );

        Assert.False(built);
        Assert.IsType<SttBenchStandInEngine>(holder.Current);
    }

    [Fact]
    public async Task Resuming_Rebuilds_The_Engine_Through_The_Normal_Swap()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var rebuilt = new FakeEngine();
        SttEngineSuspender? suspender = null;
        suspender = Suspender(
            holder,
            () => holder.SwapToLatestAsync(() => suspender!.StandInWhileSuspended(() => Task.FromResult<ISttEngine>(rebuilt)), CancellationToken.None)
        );
        await suspender.SuspendAsync(CancellationToken.None);

        await suspender.ResumeAsync(CancellationToken.None);

        Assert.Same(rebuilt, holder.Current);
        Assert.False(suspender.IsSuspended);
    }

    [Fact]
    public async Task A_Run_Goes_Through_Every_State_In_Order_And_Each_Phase_Says_So()
    {
        SttBenchService service = Service(First);
        List<SttBenchState> states = RecordStates(service);
        var messages = new List<string?>();
        service.Changed += () => messages.Add(service.StatusMessage);

        await service.RunAsync(CancellationToken.None);

        Assert.Equal(FullCycle, states);
        Assert.Contains("Unloading speech models…", messages);
        Assert.Contains("Reloading speech models…", messages);
        Assert.Null(service.StatusMessage);
    }

    [Fact]
    public async Task A_Cancelled_Run_Goes_Through_Reloading_Too()
    {
        SttBenchService service = Service(First);
        List<SttBenchState> states = RecordStates(service);
        using var cts = new CancellationTokenSource();
        _runner.Hold = new TaskCompletionSource();

        Task run = service.RunAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.Equal(FullCycle, states);
        Assert.Null(service.Progress.Current);
    }

    [Fact]
    public async Task A_Run_Whose_Row_Throws_Goes_Through_Reloading_Too()
    {
        SttBenchService service = Service(First);
        List<SttBenchState> states = RecordStates(service);
        _runner.Throw = new InvalidOperationException("no process");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(CancellationToken.None));

        Assert.Equal(FullCycle, states);
        Assert.Null(service.Progress.Current);
    }

    [Fact]
    public async Task No_Row_Starts_Before_The_Unload_Has_Finished()
    {
        SttBenchService service = Service(First);
        _suspender.HoldSuspend = new TaskCompletionSource();

        Task run = service.RunAsync(CancellationToken.None);
        Assert.Equal(SttBenchState.Unloading, service.State);
        Assert.Equal(["suspend"], _events);

        _suspender.HoldSuspend.SetResult();
        await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(["suspend", "row RasrV2Fp32 Accurate Cpu", "resume"], _events);
    }

    [Fact]
    public async Task The_Run_Does_Not_End_Before_The_Reload_Has_Finished()
    {
        SttBenchService service = Service(First);
        _suspender.HoldResume = new TaskCompletionSource();
        var reloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (service.State == SttBenchState.Reloading)
            {
                reloading.TrySetResult();
            }
        };

        Task run = service.RunAsync(CancellationToken.None);
        await reloading.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        Assert.Equal(SttBenchState.Reloading, service.State);

        _suspender.HoldResume.SetResult();
        await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(SttBenchState.Idle, service.State);
    }

    [Fact]
    public async Task Cancelling_During_The_Unload_Still_Reloads()
    {
        SttBenchService service = Service(First);
        _suspender.HoldSuspend = new TaskCompletionSource();

        Task run = service.RunAsync(CancellationToken.None);
        service.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.Equal(["suspend", "resume"], _events);
    }

    [Fact]
    public async Task Cancelling_During_The_Reload_Does_Not_Skip_It()
    {
        SttBenchService service = Service(First);
        _suspender.HoldResume = new TaskCompletionSource();
        var reloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (service.State == SttBenchState.Reloading)
            {
                reloading.TrySetResult();
            }
        };

        Task run = service.RunAsync(CancellationToken.None);
        await reloading.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        service.Cancel();
        Assert.False(run.IsCompleted);

        _suspender.HoldResume.SetResult();
        await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(SttBenchState.Idle, service.State);
    }

    [Fact]
    public async Task A_Rebuild_That_Throws_Falls_Back_To_An_Unchecked_Engine_And_Does_Not_Throw()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var fallback = new FakeEngine();
        SttEngineSuspender suspender = Suspender(holder, () => throw new InvalidOperationException("no native"), fallback);
        await suspender.SuspendAsync(CancellationToken.None);

        await suspender.ResumeAsync(CancellationToken.None);

        Assert.Same(fallback, holder.Current);
    }

    [Fact]
    public async Task A_Rebuild_That_Leaves_The_Stand_In_Falls_Back_To_An_Unchecked_Engine()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var fallback = new FakeEngine();
        SttEngineSuspender suspender = Suspender(holder, () => Task.CompletedTask, fallback);
        await suspender.SuspendAsync(CancellationToken.None);

        await suspender.ResumeAsync(CancellationToken.None);

        Assert.Same(fallback, holder.Current);
    }

    [Fact]
    public async Task A_Failed_Fallback_Is_Logged_And_Does_Not_Throw()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var suspender = new SttEngineSuspender(
            holder,
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("no engine"),
            new ModelHooks().Models,
            () => false
        );
        await suspender.SuspendAsync(CancellationToken.None);

        await suspender.ResumeAsync(CancellationToken.None);

        Assert.IsType<SttBenchStandInEngine>(holder.Current);
    }

    [Fact]
    public async Task The_Models_Unload_Once_On_Suspend_And_Reload_Once_On_Resume()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var hooks = new ModelHooks();
        var suspender = new SttEngineSuspender(holder, () => Task.CompletedTask, () => new FakeEngine(), hooks.Models, () => false);

        await suspender.SuspendAsync(CancellationToken.None);
        Assert.Equal(["unload whisper", "unload llm"], hooks.Calls);
        await suspender.ResumeAsync(CancellationToken.None);

        Assert.Equal(["unload whisper", "unload llm", "reload whisper", "reload llm"], hooks.Calls);
    }

    [Fact]
    public async Task A_Rebuild_That_Throws_Still_Reloads_The_Models()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var hooks = new ModelHooks();
        var suspender = new SttEngineSuspender(
            holder,
            () => throw new InvalidOperationException("no native"),
            () => new FakeEngine(),
            hooks.Models,
            () => false
        );
        await suspender.SuspendAsync(CancellationToken.None);

        await suspender.ResumeAsync(CancellationToken.None);

        Assert.Equal(["unload whisper", "unload llm", "reload whisper", "reload llm"], hooks.Calls);
    }

    [Fact]
    public async Task Resume_AfterACancelBeforeTheSwap_RebuildsNothing()
    {
        var live = new FakeEngine();
        using var holder = new SwappableSttEngine(live);
        var hooks = new ModelHooks();
        bool rebuilt = false;
        var suspender = new SttEngineSuspender(
            holder,
            () =>
            {
                rebuilt = true;
                return Task.CompletedTask;
            },
            () => new FakeEngine(),
            hooks.Models,
            () => false
        );
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspender.SuspendAsync(cancelled.Token));
        await suspender.ResumeAsync(CancellationToken.None);

        Assert.False(rebuilt);
        Assert.Empty(hooks.Calls);
        Assert.Same(live, holder.Current);
        Assert.False(live.Disposed);
        Assert.False(suspender.IsSuspended);
    }

    [Fact]
    public async Task Resume_AfterACancelDuringTheWhisperUnload_ReloadsWhisperAndNotTheLlm()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var hooks = new ModelHooks { HoldWhisperUnload = true };
        bool rebuilt = false;
        var suspender = new SttEngineSuspender(
            holder,
            () =>
            {
                rebuilt = true;
                return Task.CompletedTask;
            },
            () => new FakeEngine(),
            hooks.Models,
            () => false
        );
        using var cts = new CancellationTokenSource();

        Task suspend = suspender.SuspendAsync(cts.Token);
        await hooks.WhisperUnloadStarted.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspend.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        await suspender.ResumeAsync(CancellationToken.None);

        Assert.True(rebuilt);
        Assert.Equal(["unload whisper", "reload whisper"], hooks.Calls);
        Assert.False(suspender.IsSuspended);
    }

    [Fact]
    public async Task While_The_App_Shuts_Down_Resume_Loads_Nothing()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var hooks = new ModelHooks();
        bool rebuilt = false;
        var suspender = new SttEngineSuspender(
            holder,
            () =>
            {
                rebuilt = true;
                return Task.CompletedTask;
            },
            () => new FakeEngine(),
            hooks.Models,
            () => true
        );
        await suspender.SuspendAsync(CancellationToken.None);

        await suspender.ResumeAsync(CancellationToken.None);

        Assert.False(rebuilt);
        Assert.Equal(["unload whisper", "unload llm"], hooks.Calls);
        Assert.IsType<SttBenchStandInEngine>(holder.Current);
    }

    [Fact]
    public async Task A_Real_Build_In_Flight_When_Suspend_Runs_Loses_And_Cannot_Replace_The_Stand_In()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var build = new TaskCompletionSource<ISttEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var real = new FakeEngine();
        bool? realDisposedAtUnload = null;
        var models = new SttBenchSuspendedModels(
            _ =>
            {
                realDisposedAtUnload = real.Disposed;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask
        );
        var suspender = new SttEngineSuspender(holder, () => Task.CompletedTask, () => new FakeEngine(), models, () => false);

        Task<bool> inFlight = holder.SwapToLatestAsync(() => suspender.StandInWhileSuspended(() => build.Task), CancellationToken.None);
        Task suspend = suspender.SuspendAsync(CancellationToken.None);
        Assert.False(suspend.IsCompleted);
        build.SetResult(real);
        await suspend.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        bool swapped = await inFlight.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.False(swapped);
        Assert.IsType<SttBenchStandInEngine>(holder.Current);
        Assert.True(real.Disposed);
        Assert.True(realDisposedAtUnload);
    }

    [Fact]
    public async Task Resume_AfterACancelledSuspend_WhenALateBuildInstalledAStandIn_RebuildsTheEngine()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var rebuiltEngine = new FakeEngine();
        bool rebuilt = false;
        SttEngineSuspender? suspender = null;
        suspender = new SttEngineSuspender(
            holder,
            () =>
            {
                rebuilt = true;
                return holder.SwapToLatestAsync(
                    () => suspender!.StandInWhileSuspended(() => Task.FromResult<ISttEngine>(rebuiltEngine)),
                    CancellationToken.None
                );
            },
            () => new FakeEngine(),
            new ModelHooks().Models,
            () => false
        );
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspender.SuspendAsync(cancelled.Token));
        Task<bool> late = holder.SwapToLatestAsync(
            async () =>
            {
                await hold.Task;
                return await suspender.StandInWhileSuspended(() => Task.FromResult<ISttEngine>(new FakeEngine()));
            },
            CancellationToken.None
        );
        hold.SetResult();
        Assert.True(await late.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.IsType<SttBenchStandInEngine>(holder.Current);

        await suspender.ResumeAsync(CancellationToken.None).WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.True(rebuilt);
        Assert.Same(rebuiltEngine, holder.Current);
    }

    [Fact]
    public async Task AThrowingListener_DoesNotSkipTheReload()
    {
        SttBenchService service = Service(First);
        service.Changed += () => throw new InvalidOperationException("a broken view");

        await service.RunAsync(CancellationToken.None).WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(["suspend", "row RasrV2Fp32 Accurate Cpu", "resume"], _events);
        Assert.Equal(SttBenchState.Idle, service.State);
    }

    [Fact]
    public async Task Resume_WhenANewerSwapArrivesDuringTheRebuild_TheFallbackDoesNotReplaceIt()
    {
        using var holder = new SwappableSttEngine(new FakeEngine());
        var rebuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuildBuild = new TaskCompletionSource<ISttEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuildDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fallbackBuilt = false;
        var suspender = new SttEngineSuspender(
            holder,
            async () =>
            {
                await holder.SwapToLatestAsync(
                    () =>
                    {
                        rebuildStarted.TrySetResult();
                        return rebuildBuild.Task;
                    },
                    CancellationToken.None
                );
                rebuildDone.TrySetResult();
            },
            () =>
            {
                fallbackBuilt = true;
                return new FakeEngine();
            },
            new ModelHooks().Models,
            () => false
        );
        await suspender.SuspendAsync(CancellationToken.None);
        var real = new FakeEngine();
        var realBuild = new TaskCompletionSource<ISttEngine>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task resume = suspender.ResumeAsync(CancellationToken.None);
        await rebuildStarted.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Task<bool> newer = holder.SwapToLatestAsync(() => realBuild.Task, CancellationToken.None);
        rebuildBuild.SetResult(new FakeEngine());
        await rebuildDone.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        realBuild.SetResult(real);
        await resume.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.True(await newer.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.Same(real, holder.Current);
        Assert.False(fallbackBuilt);
    }

    private static List<SttBenchState> RecordStates(SttBenchService service)
    {
        var states = new List<SttBenchState> { service.State };
        service.Changed += () =>
        {
            if (states[^1] != service.State)
            {
                states.Add(service.State);
            }
        };
        return states;
    }

    private static SttEngineSuspender Suspender(SwappableSttEngine holder, Func<Task> rebuild) => Suspender(holder, rebuild, new FakeEngine());

    private static SttEngineSuspender Suspender(SwappableSttEngine holder, Func<Task> rebuild, ISttEngine fallback) =>
        new(holder, rebuild, () => fallback, new ModelHooks().Models, () => false);

    private static SttBenchRowOutcome Ran(SttBenchPlanRow row) => SttBenchRowOutcome.Ran(SttBenchResult.Failed($"ran {row.Engine}", 0, null));

    private SttBenchService Service(params SttBenchPlanRow[] rows) =>
        new(() => Task.FromResult<IReadOnlyList<SttBenchPlanRow>>(rows), _runner, _suspender, Folders, new FakeTimeProvider());

    private sealed class FakeRunner(List<string> events) : ISttBenchRowRunner
    {
        private int _running;

        public List<SttBenchJob> Jobs { get; } = [];

        public int MostAtOnce { get; private set; }

        public Exception? Throw { get; set; }

        public TaskCompletionSource? Hold { get; set; }

        public async Task<SttBenchRowOutcome> RunRowAsync(SttBenchJob job, TimeSpan deadline, CancellationToken ct)
        {
            MostAtOnce = Math.Max(MostAtOnce, Interlocked.Increment(ref _running));
            try
            {
                events.Add($"row {job.Engine} {job.Mode} {job.Device}");
                Jobs.Add(job);
                if (Throw is not null)
                {
                    throw Throw;
                }
                if (Hold is not null)
                {
                    await Hold.Task.WaitAsync(ct);
                }
                await Task.Yield();
                return SttBenchRowOutcome.Ran(SttBenchResult.Failed($"ran {job.Engine}", 0, null));
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private sealed class FakeSuspender(List<string> events) : ISttEngineSuspender
    {
        public Exception? Throw { get; set; }

        public TaskCompletionSource? HoldSuspend { get; set; }

        public TaskCompletionSource? HoldResume { get; set; }

        public async Task SuspendAsync(CancellationToken ct)
        {
            events.Add("suspend");
            if (HoldSuspend is not null)
            {
                await HoldSuspend.Task.WaitAsync(ct);
            }
            if (Throw is not null)
            {
                throw Throw;
            }
        }

        public async Task ResumeAsync(CancellationToken ct)
        {
            events.Add("resume");
            if (HoldResume is not null)
            {
                await HoldResume.Task.WaitAsync(ct);
            }
        }
    }

    private sealed class ModelHooks
    {
        public List<string> Calls { get; } = [];

        /// <summary>Whether the Whisper unload waits for its token's cancel instead of returning.</summary>
        public bool HoldWhisperUnload { get; init; }

        public TaskCompletionSource WhisperUnloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SttBenchSuspendedModels Models => new(WhisperUnload, Hook("reload whisper"), Hook("unload llm"), Hook("reload llm"));

        private Task WhisperUnload(CancellationToken ct)
        {
            Task done = Hook("unload whisper")(ct);
            if (!HoldWhisperUnload)
            {
                return done;
            }
            WhisperUnloadStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        private Func<CancellationToken, Task> Hook(string name) =>
            _ =>
            {
                lock (Calls)
                {
                    Calls.Add(name);
                }
                return Task.CompletedTask;
            };
    }

    private sealed class HangingLauncher : ISttBenchLauncher
    {
        public HangingChild Child { get; } = new();

        public ISttBenchChildProcess Start(string exePath, string jobPath) => Child;
    }

    private sealed class HangingChild : ISttBenchChildProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExitCode => -1;

        public IReadOnlyList<string> StderrLines => [];

        public bool KilledTree { get; private set; }

        public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

        public void Kill(bool entireProcessTree)
        {
            KilledTree = entireProcessTree;
            _exit.TrySetResult();
        }

        public void Dispose() { }
    }

    private sealed class FakeEngine : ISttEngine
    {
        public bool Disposed { get; private set; }

        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            throw new InvalidOperationException("The fake engine transcribes nothing");

        public void Dispose() => Disposed = true;
    }
}
