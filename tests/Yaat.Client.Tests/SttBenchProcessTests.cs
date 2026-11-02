using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>One benchmark row's child process, driven through a stub launcher: each way it can end, and the folder it leaves behind.</summary>
public sealed class SttBenchProcessTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(5);

    private readonly string _root = Directory.CreateTempSubdirectory("stt-bench-process-").FullName;
    private readonly FakeTimeProvider _time = new();

    private string RunFolder => Path.Combine(_root, "row");

    private string ResultPath => Path.Combine(RunFolder, "result.json");

    private SttBenchJob Job => new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, "model", "clips", ResultPath);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_Child_That_Writes_Its_Result_Gives_That_Result()
    {
        var written = SttBenchResult.Failed("bench failed: no clips", 0, null);
        var child = new StubChild();
        var launcher = new StubLauncher(
            child,
            _ =>
            {
                File.WriteAllText(ResultPath, JsonSerializer.Serialize(written, SttBenchJsonContext.Default.SttBenchResult));
                child.Exit(0);
            }
        );

        SttBenchRowOutcome outcome = await Run(launcher);

        Assert.Equal(written, outcome.Result);
        Assert.Equal(Job, launcher.JobRead);
        Assert.False(child.Killed);
        Assert.False(Directory.Exists(RunFolder));
    }

    [Fact]
    public async Task A_Child_That_Exits_With_Stderr_And_No_Result_Is_A_Failed_Row_Quoting_The_Code_And_First_Line()
    {
        var child = new StubChild { StderrLines = ["", "cannot load the model", "at frame 2"] };
        var launcher = new StubLauncher(child, _ => child.Exit(3));

        SttBenchRowOutcome outcome = await Run(launcher);

        Assert.Equal("failed: exit 3: cannot load the model", outcome.Result?.Failure);
        Assert.False(outcome.Result?.Succeeded);
        Assert.False(Directory.Exists(RunFolder));
    }

    [Fact]
    public async Task A_Child_That_Exits_With_No_Stderr_And_No_Result_Names_Only_The_Code()
    {
        var child = new StubChild();
        var launcher = new StubLauncher(child, _ => child.Exit(-1073741819));

        SttBenchRowOutcome outcome = await Run(launcher);

        Assert.Equal("failed: exit -1073741819", outcome.Result?.Failure);
    }

    [Fact]
    public async Task An_Unreadable_Result_File_Is_A_Failed_Row()
    {
        var child = new StubChild();
        var launcher = new StubLauncher(
            child,
            _ =>
            {
                File.WriteAllText(ResultPath, "{ not json");
                child.Exit(0);
            }
        );

        SttBenchRowOutcome outcome = await Run(launcher);

        Assert.Equal("failed: exit 0", outcome.Result?.Failure);
        Assert.False(Directory.Exists(RunFolder));
    }

    [Fact]
    public async Task A_Child_That_Never_Exits_Has_Its_Tree_Killed_At_The_Deadline()
    {
        var child = new StubChild();
        var process = new SttBenchProcess(new StubLauncher(child, _ => { }), _time);

        Task<SttBenchRowOutcome> run = process.RunRowAsync(Job, Deadline, CancellationToken.None);
        _time.Advance(Deadline - TimeSpan.FromSeconds(1));
        Assert.False(child.Killed);
        _time.Advance(TimeSpan.FromSeconds(1));
        SttBenchRowOutcome outcome = await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.True(child.KilledTree);
        Assert.Equal("timed out after 120 s", outcome.Result?.Failure);
        Assert.False(Directory.Exists(RunFolder));
    }

    [Fact]
    public async Task Cancelling_Mid_Row_Kills_The_Tree_And_Throws()
    {
        var child = new StubChild();
        var process = new SttBenchProcess(new StubLauncher(child, _ => { }), _time);
        using var cts = new CancellationTokenSource();

        Task<SttBenchRowOutcome> run = process.RunRowAsync(Job, Deadline, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.True(child.KilledTree);
        Assert.False(Directory.Exists(RunFolder));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_Child_That_Survives_The_Deadline_Kill_Fails_The_Row_And_Never_Hangs(bool killThrows, bool killIgnored)
    {
        var child = new StubChild { KillThrows = killThrows, KillIgnored = killIgnored };
        var process = new SttBenchProcess(new StubLauncher(child, _ => { }), _time);

        Task<SttBenchRowOutcome> run = process.RunRowAsync(Job, Deadline, CancellationToken.None);
        _time.Advance(Deadline);
        Assert.False(run.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(10));
        SttBenchRowOutcome outcome = await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal("could not stop the benchmark process", outcome.Result?.Failure);
        Assert.False(Directory.Exists(RunFolder));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_Child_That_Survives_The_Cancel_Kill_Still_Ends_The_Row_As_Cancelled(bool killThrows, bool killIgnored)
    {
        var child = new StubChild { KillThrows = killThrows, KillIgnored = killIgnored };
        var process = new SttBenchProcess(new StubLauncher(child, _ => { }), _time);
        using var cts = new CancellationTokenSource();

        Task<SttBenchRowOutcome> run = process.RunRowAsync(Job, Deadline, cts.Token);
        await cts.CancelAsync();
        _time.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(RunFolder));
    }

    [Fact]
    public async Task Cancelling_While_The_Kill_Blocks_Ends_The_Row_Once_The_Kill_Wait_Has_Passed()
    {
        var child = new StubChild { KillHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var process = new SttBenchProcess(new StubLauncher(child, _ => { }), _time);
        using var cts = new CancellationTokenSource();

        Task<SttBenchRowOutcome> run = process.RunRowAsync(Job, Deadline, cts.Token);
        var cancel = Task.Run(cts.Cancel, TestContext.Current.CancellationToken);
        await child.KillStarted.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        child.KillHold.SetResult();
        await cancel.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
    }

    private Task<SttBenchRowOutcome> Run(StubLauncher launcher) =>
        new SttBenchProcess(launcher, _time)
            .RunRowAsync(Job, Deadline, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

    private sealed class StubLauncher(StubChild child, Action<string> started) : ISttBenchLauncher
    {
        public SttBenchJob? JobRead { get; private set; }

        public ISttBenchChildProcess Start(string exePath, string jobPath)
        {
            JobRead = JsonSerializer.Deserialize(File.ReadAllText(jobPath), SttBenchJsonContext.Default.SttBenchJob);
            started(jobPath);
            return child;
        }
    }

    private sealed class StubChild : ISttBenchChildProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExitCode { get; private set; }

        public IReadOnlyList<string> StderrLines { get; init; } = [];

        public bool KillThrows { get; init; }

        public bool KillIgnored { get; init; }

        /// <summary>When set, <see cref="Kill"/> blocks its caller until this completes.</summary>
        public TaskCompletionSource? KillHold { get; init; }

        public TaskCompletionSource KillStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Killed { get; private set; }

        public bool KilledTree { get; private set; }

        public void Exit(int code)
        {
            ExitCode = code;
            _exit.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

        public void Kill(bool entireProcessTree)
        {
            Killed = true;
            KilledTree = entireProcessTree;
            KillStarted.TrySetResult();
            KillHold?.Task.Wait();
            if (KillThrows)
            {
                throw new AggregateException(new InvalidOperationException("access denied"));
            }
            if (!KillIgnored)
            {
                Exit(-1);
            }
        }

        public void Dispose() { }
    }
}
