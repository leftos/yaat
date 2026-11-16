using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Notes window's text: loaded from its <see cref="NotesStore"/>, saved one second after the last change (at once on a
/// flush or a clear), and stamped with the sim time and the selected aircraft's callsign so a note lines up with the
/// scenario timeline.
/// </summary>
public partial class NotesViewModel : ObservableObject
{
    /// <summary>How long the text rests after a change before it is saved.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly NotesStore _store;
    private readonly TimeProvider _clock;
    private readonly Func<string?> _simTime;
    private readonly Func<string?> _selectedCallsign;

    // _saveLock guards _pending alone and is held only to read or swap it, so a keystroke never waits on the file;
    // _writeLock keeps the writes in order, so the newest text taken is always the last one written.
    private readonly Lock _saveLock = new();
    private readonly Lock _writeLock = new();
    private readonly bool _loaded;

    private ITimer? _debounce;

    // The text a save has still to write; null once written. Read on the timer's thread, so guarded by _saveLock.
    private string? _pending;

    /// <summary>Creates the view model over a store, loading the text kept in it.</summary>
    /// <param name="store">Where the text is kept.</param>
    /// <param name="clock">Times the save after a change.</param>
    /// <param name="simTime">The displayed sim time, or null when no scenario is loaded.</param>
    /// <param name="selectedCallsign">The selected aircraft's callsign, or null when none is selected.</param>
    public NotesViewModel(NotesStore store, TimeProvider clock, Func<string?> simTime, Func<string?> selectedCallsign)
    {
        _store = store;
        _clock = clock;
        _simTime = simTime;
        _selectedCallsign = selectedCallsign;
        Text = store.Load();
        _loaded = true;
    }

    /// <summary>Asks the window to insert a stamp at its caret, through <see cref="InsertStamp"/>.</summary>
    public event EventHandler? StampRequested;

    /// <summary>The notes.</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>
    /// The stamp for a sim time and a callsign: <c>"[75:05 N123AB] "</c>, <c>"[75:05] "</c> with no callsign, and null (no
    /// stamp) with no sim time.
    /// </summary>
    /// <param name="simTime">The displayed sim time, or null when no scenario is loaded.</param>
    /// <param name="callsign">The selected aircraft's callsign, or null.</param>
    /// <returns>The stamp text, or null.</returns>
    public static string? FormatStamp(string? simTime, string? callsign)
    {
        if (simTime is null)
        {
            return null;
        }
        return string.IsNullOrEmpty(callsign) ? $"[{simTime}] " : $"[{simTime} {callsign}] ";
    }

    /// <summary>
    /// Puts the current stamp in place of the text between two positions (the caret when they are equal), keeping the order
    /// of the two either way.
    /// </summary>
    /// <param name="selectionStart">One end of the selection.</param>
    /// <param name="selectionEnd">The other end.</param>
    /// <returns>The position just after the stamp, or null when there is no stamp to insert.</returns>
    public int? InsertStamp(int selectionStart, int selectionEnd)
    {
        string? stamp = FormatStamp(_simTime(), _selectedCallsign());
        if (stamp is null)
        {
            return null;
        }

        string text = Text;
        int from = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        int to = Math.Clamp(Math.Max(selectionStart, selectionEnd), from, text.Length);
        Text = string.Concat(text.AsSpan(0, from), stamp, text.AsSpan(to));
        return from + stamp.Length;
    }

    /// <summary>Re-reads whether a stamp can be made, after the scenario was loaded or unloaded.</summary>
    public void RefreshCanStamp() => StampCommand.NotifyCanExecuteChanged();

    /// <summary>Writes the text now if a save is still waiting.</summary>
    public void Flush()
    {
        _debounce?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SavePending();
    }

    private bool CanStamp() => FormatStamp(_simTime(), _selectedCallsign()) is not null;

    [RelayCommand(CanExecute = nameof(CanStamp))]
    private void Stamp() => StampRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Clear()
    {
        Text = "";
        Flush();
    }

    partial void OnTextChanged(string value)
    {
        if (!_loaded)
        {
            return;
        }

        lock (_saveLock)
        {
            _pending = value;
        }

        if (_debounce is null)
        {
            _debounce = _clock.CreateTimer(_ => SavePending(), null, SaveDelay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _debounce.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // Runs on the timer's thread or the UI thread (Flush); it touches only the captured text and the store, never a bound
    // property.
    private void SavePending()
    {
        lock (_writeLock)
        {
            string? text;
            lock (_saveLock)
            {
                text = _pending;
                _pending = null;
            }
            if (text is not null)
            {
                _store.Save(text);
            }
        }
    }
}
