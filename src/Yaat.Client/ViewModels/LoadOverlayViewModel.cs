using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The scenario-load overlay: the step table of the load this client started, from the click through the server's
/// <c>ScenarioLoadProgress</c> events to the RPC result. It closes itself when every step finished <c>done</c> or
/// <c>notNeeded</c>; a <c>warning</c> or <c>failed</c> step keeps it open until the user closes it. Every method runs on
/// the UI thread.
/// </summary>
public partial class LoadOverlayViewModel : ObservableObject
{
    /// <summary>The title shown while the scenario's name is not known.</summary>
    public const string DefaultTitle = "Loading scenario…";

    private string? _loadId;
    private int? _lastSequence;
    private bool _hasServerTable;
    private bool _isFinished;

    /// <summary>Whether the overlay is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadInFlight))]
    private bool _isOpen;

    /// <summary>The scenario's name, or <see cref="DefaultTitle"/> until it is known.</summary>
    [ObservableProperty]
    private string _title = DefaultTitle;

    /// <summary>Whether the table is final: the load finished, failed, or was refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadInFlight))]
    private bool _isComplete;

    /// <summary>Whether the user may dismiss the overlay: a complete table that kept it open.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _canClose;

    /// <summary>The step rows, in the server's order.</summary>
    public ObservableCollection<LoadStepViewModel> Steps { get; } = [];

    /// <summary>True while this client's own load is running: the overlay is open and its table is not final.</summary>
    public bool IsLoadInFlight => IsOpen && !IsComplete;

    /// <summary>
    /// Opens the overlay at the click, before the client parses the scenario, with one local <c>Read scenario</c> row
    /// running. Forgets the previous load's table and id.
    /// </summary>
    public void BeginLocal(string displayName)
    {
        _loadId = null;
        _lastSequence = null;
        _hasServerTable = false;
        _isFinished = false;
        Title = string.IsNullOrWhiteSpace(displayName) ? DefaultTitle : displayName;
        Steps.Clear();
        Steps.Add(new LoadStepViewModel(new LoadStepDto("read", "Read scenario", LoadStepViewModel.Running, null, [])));
        IsComplete = false;
        CanClose = false;
        IsOpen = true;
    }

    /// <summary>
    /// Applies one progress event. The first event adopts its load id and later events of another load are ignored; an
    /// event no newer than the last applied is dropped, and nothing changes once a complete table is shown.
    /// </summary>
    public void ApplyProgress(ScenarioLoadProgressDto progress)
    {
        if (_isFinished)
        {
            return;
        }

        _loadId ??= progress.LoadId;
        if (progress.LoadId != _loadId)
        {
            return;
        }

        if ((_lastSequence is { } last) && (progress.Sequence <= last))
        {
            return;
        }

        _lastSequence = progress.Sequence;
        if (!string.IsNullOrEmpty(progress.ScenarioName))
        {
            Title = progress.ScenarioName;
        }

        ReplaceSteps(progress.Steps);
        if (progress.IsComplete)
        {
            Finish();
        }
    }

    /// <summary>
    /// Applies the load's RPC result: its step table becomes the final one unless a complete event already showed it,
    /// then the table is complete.
    /// </summary>
    public void ApplyResult(LoadScenarioResultDto result)
    {
        if (_isFinished)
        {
            return;
        }

        if (result.Steps.Count > 0)
        {
            if (!string.IsNullOrEmpty(result.Name))
            {
                Title = result.Name;
            }

            ReplaceSteps(result.Steps);
        }

        Finish();
    }

    /// <summary>Closes the overlay of a load that never started (a refusal, an exception, or a detour to the setup dialog).</summary>
    public void ApplyRefusal()
    {
        if (_isFinished)
        {
            return;
        }

        _isFinished = true;
        IsComplete = true;
        CanClose = false;
        IsOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        CanClose = false;
        IsOpen = false;
    }

    private void ReplaceSteps(List<LoadStepDto> steps)
    {
        _hasServerTable = true;
        Steps.Clear();
        foreach (LoadStepDto step in steps)
        {
            Steps.Add(new LoadStepViewModel(step));
        }
    }

    private void Finish()
    {
        _isFinished = true;
        IsComplete = true;
        bool needsAttention = _hasServerTable && Steps.Any(step => step.NeedsAttention);
        CanClose = needsAttention;
        if (!needsAttention)
        {
            IsOpen = false;
        }
    }
}

/// <summary>One row of the load overlay: a step's label, state, detail and problems, with the glyph its state shows.</summary>
public sealed class LoadStepViewModel(LoadStepDto step)
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Warning = "warning";
    public const string Failed = "failed";
    public const string NotNeeded = "notNeeded";

    private static readonly IBrush DoneBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x5C, 0xD6, 0x5C));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
    private static readonly IBrush FailedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C));
    private static readonly IBrush RunningBrush = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    public string Label { get; } = step.Label;

    public string State { get; } = step.State;

    public string? Detail { get; } = step.Detail;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public IReadOnlyList<string> Problems { get; } = step.Problems;

    /// <summary>A step that keeps the overlay open once the table is complete.</summary>
    public bool NeedsAttention => State is Warning or Failed;

    public bool IsNotNeeded => State == NotNeeded;

    public string Glyph =>
        State switch
        {
            Done => "✓",
            Warning => "⚠",
            Failed => "✗",
            Running => "•",
            Pending => "○",
            NotNeeded => "–",
            _ => "?",
        };

    public IBrush GlyphBrush =>
        State switch
        {
            Done => DoneBrush,
            Warning => WarningBrush,
            Failed => FailedBrush,
            Running => RunningBrush,
            _ => MutedBrush,
        };

    /// <summary>The label and detail colour: grey for a step that had nothing to do.</summary>
    public IBrush TextBrush => IsNotNeeded ? MutedBrush : RunningBrush;
}
