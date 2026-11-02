using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Sim;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>Whether a benchmark is running.</summary>
public enum SttBenchState
{
    /// <summary>No run is going on.</summary>
    Idle,

    /// <summary>The speech models are being freed; no row has started.</summary>
    Unloading,

    /// <summary>The rows are running; the speech models are out of memory and push-to-talk is off.</summary>
    Running,

    /// <summary>The rows are over and the speech models are loading again.</summary>
    Reloading,
}

/// <summary>How far a run has come.</summary>
/// <param name="Done">Rows finished.</param>
/// <param name="Total">Rows the run will run; skipped rows do not count.</param>
/// <param name="Current">The row running now; null between rows.</param>
public sealed record SttBenchProgress(int Done, int Total, SttBenchPlanRow? Current);

/// <summary>A row of the run and how it ended so far.</summary>
/// <param name="Row">The row.</param>
/// <param name="Outcome">How it ended; null until it has.</param>
public sealed record SttBenchRowState(SttBenchPlanRow Row, SttBenchRowOutcome? Outcome);

/// <summary>Where a benchmark run reads and writes.</summary>
/// <param name="ModelRoot">The folder the models install under.</param>
/// <param name="ClipsDirectory">The bench clips.</param>
/// <param name="RunRoot">The folder holding each row's run folder.</param>
/// <param name="LastRunPath">The last run's file.</param>
public sealed record SttBenchFolders(string ModelRoot, string ClipsDirectory, string RunRoot, string LastRunPath)
{
    /// <summary>The app's own folders.</summary>
    public static SttBenchFolders Default =>
        new(
            SttModelInstaller.InstallRootDefault,
            Path.Combine(AppContext.BaseDirectory, "Speech", "BenchClips"),
            YaatPaths.Combine("stt-bench"),
            SttBenchLastRunStore.DefaultPath
        );
}

/// <summary>Takes the app's own push-to-talk engine out of memory for a benchmark run.</summary>
public interface ISttEngineSuspender
{
    /// <summary>Unloads the engine; push-to-talk refuses until <see cref="ResumeAsync"/>.</summary>
    /// <param name="ct">Stops waiting for the engine.</param>
    Task SuspendAsync(CancellationToken ct);

    /// <summary>Loads the engine the preferences name.</summary>
    /// <param name="ct">Stops waiting for the load, which goes on.</param>
    Task ResumeAsync(CancellationToken ct);
}

/// <summary>What frees and reloads the speech models that are not the push-to-talk engine itself.</summary>
/// <param name="UnloadWhisper">Frees the Whisper model.</param>
/// <param name="ReloadWhisper">Loads the Whisper model again, when it was loaded; completes when it has.</param>
/// <param name="UnloadLlm">Frees the LLM fallback model and keeps it out until it is reloaded.</param>
/// <param name="ReloadLlm">Loads the LLM fallback model again, when it was loaded; completes when it has.</param>
public sealed record SttBenchSuspendedModels(
    Func<CancellationToken, Task> UnloadWhisper,
    Func<CancellationToken, Task> ReloadWhisper,
    Func<CancellationToken, Task> UnloadLlm,
    Func<CancellationToken, Task> ReloadLlm
);

/// <summary>Stands in for the speech-recognition engine during a benchmark run: it holds no model and refuses every transcription.</summary>
public sealed class SttBenchStandInEngine : ISttEngine
{
    /// <summary>What a push-to-talk during a run is told.</summary>
    public const string RefusalMessage = "Push-to-talk is off while the speech benchmark runs";

    public TranscriptionMode Mode => TranscriptionMode.Fast;

    public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
        throw new InvalidOperationException(RefusalMessage);

    public void Dispose() { }
}

/// <summary>
/// Swaps the live engine for a <see cref="SttBenchStandInEngine"/> for the length of a run. Every engine build goes through
/// <see cref="StandInWhileSuspended"/>, so a swap asked for during a run keeps a stand-in in place; the preferences it
/// was asked for are read when <paramref name="rebuild"/> runs at the end.
/// </summary>
/// <param name="engine">The holder of the live engine.</param>
/// <param name="rebuild">Swaps in the engine the preferences name, the way a settings change does.</param>
/// <param name="fallback">
/// Builds an unchecked engine, for when <paramref name="rebuild"/> fails or leaves a stand-in in place, so push-to-talk does
/// not stay refused after the run.
/// </param>
/// <param name="models">Frees and reloads the other speech models.</param>
/// <param name="isShuttingDown">Whether the app is closing, when nothing is worth loading again.</param>
public sealed class SttEngineSuspender(
    SwappableSttEngine engine,
    Func<Task> rebuild,
    Func<ISttEngine> fallback,
    SttBenchSuspendedModels models,
    Func<bool> isShuttingDown
) : ISttEngineSuspender
{
    private static readonly ILogger Log = AppLog.CreateLogger<SttEngineSuspender>();

    private volatile bool _suspended;

    /// <summary>Whether engine builds give a stand-in.</summary>
    public bool IsSuspended => _suspended;

    // The flag goes up before the swap's request number is taken: a build that started earlier is superseded by this swap,
    // and one that starts later sees the flag.
    public async Task SuspendAsync(CancellationToken ct)
    {
        _suspended = true;
        await engine.SwapToLatestAsync(() => Task.FromResult<ISttEngine>(new SttBenchStandInEngine()), ct).ConfigureAwait(false);
        await Task.Run(() => models.UnloadWhisper(ct), ct).ConfigureAwait(false);
        await Task.Run(() => models.UnloadLlm(ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads everything back and returns when it is all loaded. A failure is logged, never thrown: this runs on the run's
    /// way out, where it must not replace a row's exception or a cancel.
    /// </summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        _suspended = false;
        if (isShuttingDown())
        {
            return;
        }
        await RestoreEngineAsync().ConfigureAwait(false);
        await ReloadAsync("Whisper", models.ReloadWhisper, ct).ConfigureAwait(false);
        await ReloadAsync("LLM", models.ReloadLlm, ct).ConfigureAwait(false);
    }

    private static async Task ReloadAsync(string model, Func<CancellationToken, Task> reload, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => reload(ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Reloading the {Model} model after the speech benchmark failed", model);
        }
    }

    private async Task RestoreEngineAsync()
    {
        try
        {
            await Task.Run(rebuild, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Rebuilding the speech-recognition engine after the speech benchmark failed");
        }
        if (engine.Current is not SttBenchStandInEngine)
        {
            return;
        }

        Log.LogError("The speech-recognition engine did not come back after the speech benchmark; building it unchecked");
        try
        {
            await engine.SwapToLatestAsync(() => Task.FromResult(fallback()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The unchecked speech-recognition engine failed too; push-to-talk stays off until the next engine swap");
        }
    }

    /// <summary>A fresh stand-in during a run; otherwise what <paramref name="build"/> builds.</summary>
    /// <param name="build">Builds the engine the preferences name.</param>
    public Task<ISttEngine> StandInWhileSuspended(Func<Task<ISttEngine>> build) =>
        _suspended ? Task.FromResult<ISttEngine>(new SttBenchStandInEngine()) : build();
}

/// <summary>
/// The app-lifetime owner of the speech benchmark: one child process per row, in order, one at a time, with the app's own
/// push-to-talk engine unloaded for the run. Its state outlives any window that shows it. <see cref="Changed"/> is raised on
/// whichever thread the run is on; a view marshals it. Construction reads the last run from disk.
/// </summary>
/// <param name="plan">Gives the rows of a run when it starts.</param>
/// <param name="runner">Runs one row.</param>
/// <param name="suspender">Unloads the push-to-talk engine for the run.</param>
/// <param name="folders">Where a run reads and writes.</param>
/// <param name="timeProvider">Stamps a finished run.</param>
public sealed class SttBenchService(
    Func<Task<IReadOnlyList<SttBenchPlanRow>>> plan,
    ISttBenchRowRunner runner,
    ISttEngineSuspender suspender,
    SttBenchFolders folders,
    TimeProvider timeProvider
)
{
    /// <summary>How long one row may take.</summary>
    public static readonly TimeSpan RowDeadline = TimeSpan.FromSeconds(120);

    private static readonly ILogger Log = AppLog.CreateLogger<SttBenchService>();

    private readonly Lock _runGate = new();
    private CancellationTokenSource? _run;
    private volatile SttBenchState _state = SttBenchState.Idle;
    private volatile SttBenchProgress _progress = new(0, 0, null);
    private volatile IReadOnlyList<SttBenchRowState> _rows = [];
    private volatile SttBenchLastRun? _lastRun = SttBenchLastRunStore.Load(folders.LastRunPath, Log);

    /// <summary>Raised when any property here changes, on the thread that changed it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Which phase the run is in; a view disables itself during <see cref="SttBenchState.Unloading"/> and
    /// <see cref="SttBenchState.Reloading"/>.
    /// </summary>
    public SttBenchState State => _state;

    /// <summary>What to show during those two phases; null otherwise.</summary>
    public string? StatusMessage =>
        _state switch
        {
            SttBenchState.Unloading => "Unloading speech models…",
            SttBenchState.Reloading => "Reloading speech models…",
            _ => null,
        };

    /// <summary>How far the current run has come, or how far the latest one got.</summary>
    public SttBenchProgress Progress => _progress;

    /// <summary>Each row of the current or latest run with its outcome so far.</summary>
    public IReadOnlyList<SttBenchRowState> Rows => _rows;

    /// <summary>The last run that finished; null when none has. A cancelled run leaves it alone.</summary>
    public SttBenchLastRun? LastRun => _lastRun;

    /// <summary>
    /// Runs the benchmark: unloads the speech models and waits until they are gone, runs the rows that can run, saves the
    /// run as the last one, and on every way out loads the models again and waits until they are back. A cancel never
    /// skips the reload.
    /// </summary>
    /// <param name="ct">Stops the run, as <see cref="Cancel"/> does.</param>
    /// <exception cref="InvalidOperationException">A run is already going on.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled; the child is dead by then.</exception>
    public async Task RunAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_runGate)
        {
            if (_run is not null)
            {
                throw new InvalidOperationException("A speech benchmark is already running");
            }
            _run = run;
        }
        try
        {
            SetState(SttBenchState.Unloading);
            IReadOnlyList<SttBenchPlanRow> planned = await plan().ConfigureAwait(false);
            SttBenchRowState[] rows = [.. planned.Select(row => new SttBenchRowState(row, SkippedOutcome(row)))];
            int total = planned.Count(row => row.SkipReason is null);
            _rows = [.. rows];
            _progress = new SttBenchProgress(0, total, null);
            Changed?.Invoke();
            if (total > 0)
            {
                await RunRowsAsync(rows, total, run.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _progress = _progress with { Current = null };
            lock (_runGate)
            {
                _run = null;
            }
            SetState(SttBenchState.Idle);
        }
    }

    /// <summary>
    /// Stops the run, if any: the kill of the running child's tree has been issued when this returns, and no further row
    /// starts. The reload still runs, and <see cref="RunAsync"/> ends after it.
    /// </summary>
    public void Cancel()
    {
        lock (_runGate)
        {
            _run?.Cancel();
        }
    }

    private static SttBenchRowOutcome? SkippedOutcome(SttBenchPlanRow row) =>
        (row.SkipReason is null) ? null : SttBenchRowOutcome.Skip(row.SkipReason);

    private async Task RunRowsAsync(SttBenchRowState[] rows, int total, CancellationToken ct)
    {
        try
        {
            await suspender.SuspendAsync(ct).ConfigureAwait(false);
            SetState(SttBenchState.Running);
            int done = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                SttBenchPlanRow row = rows[i].Row;
                if (row.SkipReason is not null)
                {
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                _progress = new SttBenchProgress(done, total, row);
                Changed?.Invoke();

                SttBenchRowOutcome outcome = await runner.RunRowAsync(JobFor(row), RowDeadline, ct).ConfigureAwait(false);

                rows[i] = rows[i] with { Outcome = outcome };
                done++;
                _rows = [.. rows];
                _progress = new SttBenchProgress(done, total, null);
                Changed?.Invoke();
            }
            SaveLastRun(rows);
        }
        finally
        {
            SetState(SttBenchState.Reloading);
            await suspender.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void SetState(SttBenchState state)
    {
        _state = state;
        Changed?.Invoke();
    }

    private SttBenchJob JobFor(SttBenchPlanRow row)
    {
        string modelId = row.Engine.ManifestModelId() ?? throw new InvalidOperationException($"{row.Engine} cannot be benchmarked");
        string resultPath = Path.Combine(folders.RunRoot, Guid.NewGuid().ToString("N"), "result.json");
        return new SttBenchJob(
            row.Engine,
            row.Mode,
            row.Device,
            SttModelInstaller.ModelDir(folders.ModelRoot, modelId),
            folders.ClipsDirectory,
            resultPath
        );
    }

    private void SaveLastRun(SttBenchRowState[] rows)
    {
        SttBenchLastRow[] lastRows =
        [
            .. rows.Select(state => new SttBenchLastRow(
                state.Row.Engine,
                state.Row.Mode,
                state.Row.Device,
                state.Outcome?.Result,
                state.Outcome?.SkipReason
            )),
        ];
        var lastRun = new SttBenchLastRun(timeProvider.GetUtcNow(), lastRows);
        SttBenchLastRunStore.Save(folders.LastRunPath, lastRun);
        _lastRun = lastRun;
        Changed?.Invoke();
    }
}
