using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The part every General-tab generator row shares: a wrapper over one generator list's item at <see cref="Index"/>, so
/// every commit edits that item in place (one undo step, "Change generator") and its unmodelled fields survive; a row the
/// document no longer holds at its index writes nothing. Holds the window and cadence fields every kind has: start and
/// interval times (blank is 0), the optional max time (blank is none), randomize interval, and enabled: the three-state
/// override, mirrored as the document holds it (none follows the start and max time window, true is always on, false
/// always off), a change written as itself, none removing the member. A field whose text does not parse shows its error
/// under the field, keeps the text and writes nothing, so the document keeps its last good value.
/// </summary>
/// <typeparam name="T">The generator config type.</typeparam>
public abstract partial class ScenarioGeneratorRowViewModel<T> : ObservableObject
    where T : class, IGeneratorConfig
{
    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioGeneratorRowViewModel<T>>();

    private readonly ScenarioEditSession _session;
    private readonly ScenarioEditApply _apply;
    private readonly Func<Scenario, List<T>> _list;

    [ObservableProperty]
    private string _startTimeOffsetText;

    [ObservableProperty]
    private string? _startTimeOffsetError;

    [ObservableProperty]
    private string _maxTimeText;

    [ObservableProperty]
    private string? _maxTimeError;

    [ObservableProperty]
    private string _intervalTimeText;

    [ObservableProperty]
    private string? _intervalTimeError;

    [ObservableProperty]
    private bool _randomizeInterval;

    [ObservableProperty]
    private bool? _enabled;

    /// <summary>Initializes a new instance of the <see cref="ScenarioGeneratorRowViewModel{T}"/> class from the item at the index.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="index">The item's index into the list.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    /// <param name="list">The scenario's list of this kind.</param>
    private protected ScenarioGeneratorRowViewModel(ScenarioEditSession session, int index, ScenarioEditApply apply, Func<Scenario, List<T>> list)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _list = list ?? throw new ArgumentNullException(nameof(list));
        Index = index;
        T item = list(session.Document.Scenario)[index];
        Item = item;
        _startTimeOffsetText = ScenarioFieldText.Time.Format(item.StartTimeOffset);
        _maxTimeText = ScenarioFieldText.OptionalTime.Format(item.MaxTime);
        _intervalTimeText = ScenarioFieldText.Time.Format(item.IntervalTime);
        _randomizeInterval = item.RandomizeInterval;
        _enabled = item.Enabled;
    }

    /// <summary>Gets the item's index into its list.</summary>
    public int Index { get; }

    /// <summary>Gets whether the document still holds this row's item at its index; a stale row writes nothing.</summary>
    public bool IsCurrent => ScenarioListRow.IsCurrent(_list(_session.Document.Scenario), Index, Item);

    /// <summary>Gets whether this kind is a YAAT extension the vNAS scenario format does not have.</summary>
    public abstract bool IsYaatOnly { get; }

    /// <summary>Gets the item the row was built over, as the document held it then.</summary>
    protected T Item { get; }

    /// <summary>Writes the start time.</summary>
    /// <param name="generator">The document's item.</param>
    /// <param name="seconds">The time in seconds.</param>
    protected abstract void SetStartTimeOffset(T generator, int seconds);

    /// <summary>Writes the max time.</summary>
    /// <param name="generator">The document's item.</param>
    /// <param name="seconds">The time in seconds; null for none.</param>
    protected abstract void SetMaxTime(T generator, int? seconds);

    /// <summary>Writes the interval time.</summary>
    /// <param name="generator">The document's item.</param>
    /// <param name="seconds">The time in seconds.</param>
    protected abstract void SetIntervalTime(T generator, int seconds);

    /// <summary>Writes randomize interval.</summary>
    /// <param name="generator">The document's item.</param>
    /// <param name="value">The value.</param>
    protected abstract void SetRandomizeInterval(T generator, bool value);

    /// <summary>Writes enabled.</summary>
    /// <param name="generator">The document's item.</param>
    /// <param name="value">The value.</param>
    protected abstract void SetEnabled(T generator, bool? value);

    /// <summary>
    /// Writes the field's value when its text parses, showing it formatted with no error; otherwise shows the error and
    /// keeps the text. A stale row does neither.
    /// </summary>
    /// <typeparam name="TValue">The field's value type.</typeparam>
    /// <param name="kind">How the field reads and shows its text.</param>
    /// <param name="text">The field's text.</param>
    /// <param name="show">Sets the field's text and error.</param>
    /// <param name="write">Writes the value to the document's item.</param>
    private protected void CommitField<TValue>(ScenarioFieldKind<TValue> kind, string text, Action<string, string?> show, Action<T, TValue> write)
    {
        if (IsStale())
        {
            return;
        }

        if (!kind.TryParse(text, out TValue value))
        {
            show(text, kind.ErrorText);
            return;
        }

        show(kind.Format(value), null);
        Write(g => write(g, value));
    }

    /// <summary>Writes a text or picker field, trimmed; blank is written as empty.</summary>
    /// <param name="text">The field's text.</param>
    /// <param name="show">Sets the field's text.</param>
    /// <param name="write">Writes the value to the document's item.</param>
    private protected void CommitText(string text, Action<string> show, Action<T, string> write)
    {
        if (IsStale())
        {
            return;
        }

        string value = text.Trim();
        show(value);
        Write(g => write(g, value));
    }

    /// <summary>
    /// Writes the auto-track position, trimmed: a position creates the conditions when there are none and keeps their
    /// other members; blank removes the conditions.
    /// </summary>
    /// <param name="positionId">The picked position id.</param>
    /// <param name="show">Sets the field's text.</param>
    /// <param name="get">Reads the document's item's conditions.</param>
    /// <param name="set">Writes the document's item's conditions.</param>
    private protected void CommitAutoTrack(
        string positionId,
        Action<string> show,
        Func<T, AutoTrackConditions?> get,
        Action<T, AutoTrackConditions?> set
    )
    {
        if (IsStale())
        {
            return;
        }

        string id = positionId.Trim();
        show(id);
        Write(g => set(g, (id.Length == 0) ? null : WithPosition(get(g), id)));
    }

    /// <summary>
    /// Takes a picker's selection: null (a picker clearing as its options are swapped) and the value already held write
    /// nothing; any other value is the user's pick, set and committed.
    /// </summary>
    /// <param name="picked">The selection the picker wrote.</param>
    /// <param name="current">The field's value.</param>
    /// <param name="set">Sets the field.</param>
    /// <param name="commit">Commits the field.</param>
    private protected static void Pick(string? picked, string current, Action<string> set, Action commit)
    {
        if (ScenarioFieldText.IsUserPick(picked, current))
        {
            set(picked);
            commit();
        }
    }

    /// <summary>Writes a change to the document's item when the row is current.</summary>
    /// <param name="change">Changes the item in place.</param>
    private protected void WriteIfCurrent(Action<T> change)
    {
        if (!IsStale())
        {
            Write(change);
        }
    }

    private static AutoTrackConditions WithPosition(AutoTrackConditions? conditions, string positionId)
    {
        AutoTrackConditions result = conditions ?? new AutoTrackConditions();
        result.PositionId = positionId;
        return result;
    }

    private bool IsStale() => ScenarioListRow.IsStale(_list(_session.Document.Scenario), Index, Item, Log);

    private void Write(Action<T> change)
    {
        int index = Index;
        Func<Scenario, List<T>> list = _list;
        _apply(ScenarioFieldText.ChangeGeneratorText, s => change(list(s)[index]));
    }

    [RelayCommand]
    private void CommitStartTimeOffset() =>
        CommitField(ScenarioFieldText.Time, StartTimeOffsetText, (t, e) => (StartTimeOffsetText, StartTimeOffsetError) = (t, e), SetStartTimeOffset);

    [RelayCommand]
    private void CommitMaxTime() =>
        CommitField(ScenarioFieldText.OptionalTime, MaxTimeText, (t, e) => (MaxTimeText, MaxTimeError) = (t, e), SetMaxTime);

    [RelayCommand]
    private void CommitIntervalTime() =>
        CommitField(ScenarioFieldText.Time, IntervalTimeText, (t, e) => (IntervalTimeText, IntervalTimeError) = (t, e), SetIntervalTime);

    partial void OnRandomizeIntervalChanged(bool value) => WriteIfCurrent(g => SetRandomizeInterval(g, value));

    partial void OnEnabledChanged(bool? value) => WriteIfCurrent(g => SetEnabled(g, value));
}
