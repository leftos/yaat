using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.ViewModels;

/// <summary>What the speech benchmark window does when it is asked to close.</summary>
public enum CloseDecision
{
    /// <summary>Close now: no run is going on.</summary>
    Close,

    /// <summary>Stay open and ask nothing: the speech models are unloading or reloading.</summary>
    Refuse,

    /// <summary>Stay open and ask whether to cancel the run.</summary>
    Ask,
}

/// <summary>
/// The "Speech benchmark" window: runs the app-lifetime <see cref="SttBenchService"/>, shows its rows as they run and the last
/// finished run afterwards, and applies a picked row's model, mode and device. The service outlives the window; this view
/// model only mirrors it, marshalling its <see cref="SttBenchService.Changed"/> onto the UI thread.
/// </summary>
public sealed partial class SpeechBenchmarkViewModel : ObservableObject, IDisposable
{
    /// <summary>What the window says before the first run.</summary>
    public const string IntroMessage =
        "Runs each downloaded model in each mode on each device, on 12 short clips, and measures latency and memory on this machine. "
        + "WER and callsign recall come from YAAT's reference run, not from this machine.";

    /// <summary>What the window says while no run goes on.</summary>
    public const string PushToTalkMessage = "Push-to-talk is off during a run: YAAT unloads its speech models first and reloads them after.";

    /// <summary>What the window says when the GPU rows were skipped for want of the GPU runtime.</summary>
    public const string GpuRuntimeMessage = "The GPU rows need the GPU runtime: install it in Settings > Speech > Device, then run again.";

    private static readonly ILogger Log = AppLog.CreateLogger<SpeechBenchmarkViewModel>();

    private readonly SttBenchService _service;
    private readonly Func<Task<IReadOnlyList<SttBenchPlanRow>>> _plan;
    private readonly Func<SttEngine, bool> _isInstalled;
    private readonly SttReference? _reference;
    private readonly Func<(SttEngine Engine, SttMode Mode, SttDevice Device)> _current;
    private readonly Func<SttEngine, SttMode, SttDevice, Task> _apply;
    private readonly Func<SttDevice> _loadedDevice;
    private readonly TimeZoneInfo _zone;
    private bool _disposed;

    /// <summary>Mirrors <paramref name="service"/> as it stands now and follows its changes.</summary>
    /// <param name="service">The app-lifetime benchmark.</param>
    /// <param name="plan">The rows a run would hold now, for the run count.</param>
    /// <param name="isInstalled">Whether a model is installed now; a row of a deleted model cannot be picked.</param>
    /// <param name="reference">The reference WER and callsign recall; null when the file could not be read.</param>
    /// <param name="current">The model, mode and device the preferences save.</param>
    /// <param name="apply">Saves a picked row's model, mode and device.</param>
    /// <param name="loadedDevice">The device speech recognition loaded on at startup.</param>
    /// <param name="zone">The zone the last run's time is shown in.</param>
    public SpeechBenchmarkViewModel(
        SttBenchService service,
        Func<Task<IReadOnlyList<SttBenchPlanRow>>> plan,
        Func<SttEngine, bool> isInstalled,
        SttReference? reference,
        Func<(SttEngine Engine, SttMode Mode, SttDevice Device)> current,
        Func<SttEngine, SttMode, SttDevice, Task> apply,
        Func<SttDevice> loadedDevice,
        TimeZoneInfo zone
    )
    {
        _service = service;
        _plan = plan;
        _isInstalled = isInstalled;
        _reference = reference;
        _current = current;
        _apply = apply;
        _loadedDevice = loadedDevice;
        _zone = zone;
        _service.Changed += OnServiceChanged;
        Refresh();
    }

    /// <summary>Raised once the run cancelled by <see cref="CancelAndClose"/> has ended.</summary>
    public event Action? CloseRequested;

    /// <summary>The table's rows.</summary>
    public ObservableCollection<SpeechBenchmarkRowViewModel> Rows { get; } = [];

    public string IntroText => IntroMessage;

    public string PushToTalkNote => PushToTalkMessage;

    public string GpuRuntimeNote => GpuRuntimeMessage;

    [ObservableProperty]
    private bool _showsEmpty;

    [ObservableProperty]
    private bool _showsIntro;

    [ObservableProperty]
    private bool _showsRunsText;

    [ObservableProperty]
    private bool _showsGpuRuntimeNote;

    [ObservableProperty]
    private string _runsText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isTransitioning;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _failedText;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    private string _runButtonText = "Run benchmark";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyPropertyChangedFor(nameof(RestartNote))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private SpeechBenchmarkRowViewModel? _selectedRow;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isAskingToCancel;

    [ObservableProperty]
    private bool _closesWhenIdle;

    /// <summary>The picked row, or the hint to pick one.</summary>
    public string SelectionText =>
        SelectedRow is { } row ? $"Selected: {row.ModelText} · {row.ModeText} · {row.DeviceText}" : "Pick a row to apply it";

    /// <summary>Why the picked row needs a restart; null when it does not.</summary>
    public string? RestartNote
    {
        get
        {
            SttDevice loaded = _loadedDevice();
            return ((SelectedRow is { } row) && (row.Key.Device != loaded))
                ? $"Restart YAAT to apply: Device changes from {SttBenchText.Device(loaded)} to {SttBenchText.Device(row.Key.Device)}."
                : null;
        }
    }

    /// <summary>Counts the rows a run would run now; called when the window opens.</summary>
    public async Task LoadPlanAsync()
    {
        try
        {
            IReadOnlyList<SttBenchPlanRow> rows = await _plan();
            int runs = rows.Count(row => row.SkipReason is null);
            RunsText = (runs == 1) ? "1 run" : $"{runs} runs";
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Listing the speech benchmark rows failed");
            RunsText = "";
        }
    }

    /// <summary>What to do when the window is asked to close; <see cref="CloseDecision.Ask"/> shows the ask bar.</summary>
    public CloseDecision RequestClose()
    {
        switch (_service.State)
        {
            case SttBenchState.Idle:
                return CloseDecision.Close;
            case SttBenchState.Running:
                IsAskingToCancel = true;
                return CloseDecision.Ask;
            default:
                return CloseDecision.Refuse;
        }
    }

    /// <summary>Closes the ask; the window closes and the run goes on.</summary>
    public void KeepRunning() => IsAskingToCancel = false;

    /// <summary>Cancels the run and closes the window once the models are back.</summary>
    public void CancelAndClose()
    {
        IsAskingToCancel = false;
        ClosesWhenIdle = true;
        _service.Cancel();
    }

    /// <summary>Moves the current badge after the saved choice changed elsewhere (Settings' OK or Apply).</summary>
    public void RefreshCurrent() => MarkCurrent();

    public void Dispose()
    {
        _disposed = true;
        _service.Changed -= OnServiceChanged;
        DetachRows();
    }

    // The window may be gone by the time the run ends (Keep running closes it), so this touches only properties.
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        ErrorText = null;
        try
        {
            await _service.RunAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // A cancel is how a cancelled run ends; the service has reloaded the models by then, and nothing failed.
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The speech benchmark run failed");
            ErrorText = $"The benchmark stopped: {ex.Message}";
        }
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand]
    private void Cancel() => _service.Cancel();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }
        if (!_isInstalled(row.Key.Engine))
        {
            ErrorText = $"{row.ModelText} is no longer downloaded; download it again to apply this row.";
            return;
        }
        ErrorText = null;
        try
        {
            await _apply(row.Key.Engine, row.Key.Mode, row.Key.Device);
            MarkCurrent();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Applying the speech benchmark row {Row} failed", row.Key);
            ErrorText = $"Apply failed: {ex.Message}";
        }
    }

    // A model deleted since the row was built cannot be applied, so the install is checked again here.
    private bool CanApply() => (SelectedRow is { IsPickable: true } row) && (!IsBusy) && _isInstalled(row.Key.Engine);

    private void OnServiceChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        SttBenchState state = _service.State;
        SttBenchLastRun? lastRun = _service.LastRun;
        IsBusy = state != SttBenchState.Idle;
        IsTransitioning = state is SttBenchState.Unloading or SttBenchState.Reloading;
        IsRunning = state == SttBenchState.Running;
        if (!IsRunning)
        {
            IsAskingToCancel = false;
        }

        SttBenchProgress progress = _service.Progress;
        ReplaceRows(state, lastRun, progress);
        RefreshTexts(state, progress, lastRun);

        if ((state == SttBenchState.Idle) && ClosesWhenIdle)
        {
            ClosesWhenIdle = false;
            CloseRequested?.Invoke();
        }
    }

    private void RefreshTexts(SttBenchState state, SttBenchProgress progress, SttBenchLastRun? lastRun)
    {
        ShowsEmpty = Rows.Count == 0;
        ShowsGpuRuntimeNote = Rows.Any(row => row.IsGreyed && (row.MessageText == SttBenchPlan.NoGpuRuntimeReason));
        ShowsIntro = (lastRun is null) && (!IsBusy);
        ShowsRunsText = ShowsIntro;
        RunButtonText = (lastRun is null) ? "Run benchmark" : "Run again";
        ProgressMaximum = Math.Max(progress.Total, 1);
        ProgressValue = progress.Done;
        StatusText = StatusFor(state, progress, lastRun);
        int failed = (IsBusy || (lastRun is null)) ? 0 : SttBenchText.RunCounts(lastRun.Rows).Failed;
        FailedText = (failed > 0) ? $"{failed} failed" : null;
    }

    private string StatusFor(SttBenchState state, SttBenchProgress progress, SttBenchLastRun? lastRun) =>
        state switch
        {
            SttBenchState.Unloading or SttBenchState.Reloading => _service.StatusMessage ?? "",
            SttBenchState.Running => SttBenchText.RunningLine(progress),
            _ => (lastRun is null) ? "" : SttBenchText.LastRunLine(lastRun, _zone),
        };

    private void ReplaceRows(SttBenchState runState, SttBenchLastRun? lastRun, SttBenchProgress progress)
    {
        DetachRows();
        Rows.Clear();
        SelectedRow = null;
        IReadOnlyList<SttBenchRowState> running = _service.Rows;
        IEnumerable<SpeechBenchmarkRowViewModel> rows =
            (IsBusy && (running.Count > 0)) ? running.Select(state => RowOf(state, runState, progress))
            : lastRun is not null ? lastRun.Rows.Select(RowOf)
            : [];
        foreach (SpeechBenchmarkRowViewModel row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
        MarkCurrent();
    }

    // A row still current during the reload never finished: the run was stopped under it.
    private SpeechBenchmarkRowViewModel RowOf(SttBenchRowState state, SttBenchState runState, SttBenchProgress progress)
    {
        var key = new SpeechBenchmarkRowKey(state.Row.Engine, state.Row.Mode, state.Row.Device);
        SttReferenceEntry? reference = _reference?.Find(key.Engine, key.Mode);
        return state.Outcome switch
        {
            { Result: { } result } => SpeechBenchmarkRowViewModel.FromResult(key, result, reference, _isInstalled(key.Engine)),
            { SkipReason: { } reason } => SpeechBenchmarkRowViewModel.FromSkip(key, reason, reference),
            _ when (state.Row == progress.Current) && (runState == SttBenchState.Running) => SpeechBenchmarkRowViewModel.Measuring(key, reference),
            _ when (state.Row == progress.Current) && (runState == SttBenchState.Reloading) => SpeechBenchmarkRowViewModel.FromSkip(
                key,
                "cancelled",
                reference
            ),
            _ => SpeechBenchmarkRowViewModel.Waiting(key, reference),
        };
    }

    private SpeechBenchmarkRowViewModel RowOf(SttBenchLastRow last)
    {
        var key = new SpeechBenchmarkRowKey(last.Engine, last.Mode, last.Device);
        SttReferenceEntry? reference = _reference?.Find(key.Engine, key.Mode);
        if (last.Result is { } result)
        {
            return SpeechBenchmarkRowViewModel.FromResult(key, result, reference, _isInstalled(key.Engine));
        }
        if (last.SkipReason is { } reason)
        {
            return SpeechBenchmarkRowViewModel.FromSkip(key, reason, reference);
        }
        Log.LogWarning("The last speech benchmark run has a row {Row} with neither a result nor a skip reason", key);
        return SpeechBenchmarkRowViewModel.FromResult(key, SttBenchResult.Failed("no result", 0, null), reference, false);
    }

    private void MarkCurrent()
    {
        (SttEngine engine, SttMode mode, SttDevice device) = _current();
        var current = new SpeechBenchmarkRowKey(engine, mode, device);
        foreach (SpeechBenchmarkRowViewModel row in Rows)
        {
            row.IsCurrent = row.Key == current;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName != nameof(SpeechBenchmarkRowViewModel.IsSelected)) || (sender is not SpeechBenchmarkRowViewModel row))
        {
            return;
        }
        if (row.IsSelected)
        {
            foreach (SpeechBenchmarkRowViewModel other in Rows.Where(other => (other != row) && other.IsSelected))
            {
                other.IsSelected = false;
            }
            SelectedRow = row;
        }
        else if (SelectedRow == row)
        {
            SelectedRow = null;
        }
    }

    private void DetachRows()
    {
        foreach (SpeechBenchmarkRowViewModel row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
    }
}
