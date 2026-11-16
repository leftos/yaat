using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.ViewModels;

/// <summary>What the Scope's response shows after an entry ran.</summary>
public enum ScopeResponseKind
{
    /// <summary>A success with nothing to say: <c>ACCEPT</c>.</summary>
    Accept,

    /// <summary>A success with an echo of its own.</summary>
    Text,

    /// <summary>The entry failed or was refused.</summary>
    Error,
}

/// <summary>The response to one Scope entry: its kind, its message and the entry it echoes.</summary>
/// <param name="Kind">Accept, text or error.</param>
/// <param name="Message">The message: <c>ACCEPT</c>, the echo text or the error.</param>
/// <param name="Echo">The entry as it was typed.</param>
public sealed record ScopeEntryResponse(ScopeResponseKind Kind, string Message, string Echo);

/// <summary>One line of a Scope response: its text, and whether it is the message (else the entry's echo).</summary>
internal readonly record struct ScopeResponseLine(string Text, bool IsMessage);

/// <summary>
/// Typing on the Scope: the entry being typed, the Scope's own entry history and the response to the last entry. Enter runs
/// the entry through <see cref="MainViewModel.SendTextAsync"/> with a sink of the Scope's own, so the main command line's
/// text and status are never touched; a CRC dialect entry is refused with <c>NOT SUPPORTED</c> and nothing is sent. On a
/// STARS profile the response goes at the next entry's first key; on ERAM and Mixed it stays until the next entry is sent.
/// Esc clears both.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>How many characters one line of the preview or the response holds.</summary>
    internal const int EntryLineWidth = 30;

    /// <summary>How many lines of the entry the preview shows: the last ones, where the caret is.</summary>
    internal const int PreviewLineCap = 3;

    /// <summary>How many lines of the response the feedback shows.</summary>
    internal const int FeedbackLineCap = 5;

    internal const string NotSupportedMessage = "NOT SUPPORTED";

    internal const string AcceptMessage = "ACCEPT";

    /// <summary>The caret drawn after the entry being typed.</summary>
    internal const string Caret = "_";

    /// <summary>How many entries the Scope's own history keeps, as the main command box does.</summary>
    private const int EntryHistoryCap = 50;

    private readonly List<string> _entryHistory = [];

    // The history line Up/Down shows, or -1 when the entry is not a recalled one.
    private int _entryHistoryIndex = -1;

    // The half-typed entry the first Up left, which Down past the newest line puts back.
    private string _savedEntry = "";

    /// <summary>The entry being typed on the Scope.</summary>
    [ObservableProperty]
    private string _entryText = "";

    /// <summary>The response to the last entry, or null when none is shown.</summary>
    [ObservableProperty]
    private ScopeEntryResponse? _entryResponse;

    /// <summary>The run of the last entry Enter started. Test seam.</summary>
    internal Task LastEntryRun { get; private set; } = Task.CompletedTask;

    /// <summary>The text the last entry sent through the send path; null while no entry was sent. Test seam.</summary>
    internal string? LastSentEntryText { get; private set; }

    /// <summary>Whether the Scope draws ERAM's boxes (an ERAM or Mixed profile) rather than STARS's one area.</summary>
    public bool IsEramEntryLayout => ProfileKind != ScopeProfileKind.Stars;

    /// <summary>How the entry being typed reads.</summary>
    internal ScopeEntryReading EntryReading => ScopeEntryClassifier.Classify(EntryText, ProfileKind);

    /// <summary>The entry and its caret, wrapped, the last <see cref="PreviewLineCap"/> lines; empty while nothing is typed.</summary>
    internal IReadOnlyList<string> EntryPreviewLines => EntryText.Length == 0 ? [] : [.. Wrap(EntryText + Caret).TakeLast(PreviewLineCap)];

    /// <summary>The response's message then its echo, wrapped, the first <see cref="FeedbackLineCap"/> lines.</summary>
    internal IReadOnlyList<ScopeResponseLine> EntryResponseLines =>
        EntryResponse is not { } response
            ? []
            :
            [
                .. Wrap(response.Message)
                    .Select(line => new ScopeResponseLine(line, true))
                    .Concat(Wrap(response.Echo).Select(line => new ScopeResponseLine(line, false)))
                    .Take(FeedbackLineCap),
            ];

    /// <summary>Adds typed <paramref name="text"/> to the entry; control characters are dropped.</summary>
    /// <returns>False when nothing printable was typed.</returns>
    public bool TypeEntryText(string? text)
    {
        string printable = new([.. (text ?? "").Where(c => !char.IsControl(c))]);
        if (printable.Length == 0)
        {
            return false;
        }

        SetEntry(EntryText + printable);
        _entryHistoryIndex = -1;
        return true;
    }

    /// <summary>Takes the last character off the entry.</summary>
    /// <returns>False when the entry is empty.</returns>
    public bool BackspaceEntry()
    {
        if (EntryText.Length == 0)
        {
            return false;
        }

        EntryText = EntryText[..^1];
        _entryHistoryIndex = -1;
        return true;
    }

    /// <summary>
    /// Walks the Scope's entry history: <paramref name="older"/> for Up, else Down. The first Up keeps the half-typed entry, and
    /// Down past the newest line puts it back.
    /// </summary>
    /// <returns>False when there is nothing to walk to.</returns>
    public bool WalkEntryHistory(bool older)
    {
        if (_entryHistory.Count == 0)
        {
            return false;
        }

        if (older)
        {
            if (_entryHistoryIndex < 0)
            {
                _savedEntry = EntryText;
            }

            _entryHistoryIndex = (_entryHistoryIndex < 0) ? (_entryHistory.Count - 1) : Math.Max(0, _entryHistoryIndex - 1);
            SetEntry(_entryHistory[_entryHistoryIndex]);
            return true;
        }

        if (_entryHistoryIndex < 0)
        {
            return false;
        }

        _entryHistoryIndex++;
        if (_entryHistoryIndex < _entryHistory.Count)
        {
            SetEntry(_entryHistory[_entryHistoryIndex]);
            return true;
        }

        _entryHistoryIndex = -1;
        SetEntry(_savedEntry);
        _savedEntry = "";
        return true;
    }

    /// <summary>Ctrl+R on ERAM and Mixed: puts the last entry back in the preview.</summary>
    /// <returns>False on STARS or with no entry yet.</returns>
    public bool RecallLastEntry()
    {
        if (!IsEramEntryLayout || (_entryHistory.Count == 0))
        {
            return false;
        }

        SetEntry(_entryHistory[^1]);
        _entryHistoryIndex = -1;
        return true;
    }

    /// <summary>Esc: clears the entry and the response.</summary>
    /// <returns>False when there was neither.</returns>
    public bool ClearEntry()
    {
        if ((EntryText.Length == 0) && (EntryResponse is null))
        {
            return false;
        }

        EntryText = "";
        EntryResponse = null;
        _entryHistoryIndex = -1;
        return true;
    }

    /// <summary>Enter: runs the entry, which leaves the preview for the response.</summary>
    /// <returns>False when nothing is typed, or the entry is only the <c>PILOT</c> word, which has nothing to send.</returns>
    public bool ExecuteEntry()
    {
        string entry = EntryText.Trim();
        if ((entry.Length == 0) || (ScopeEntryClassifier.Classify(entry, ProfileKind).SendText.Length == 0))
        {
            return false;
        }

        LastEntryRun = RunEntryAsync(entry);
        return true;
    }

    private async Task RunEntryAsync(string entry)
    {
        ScopeEntryReading reading = ScopeEntryClassifier.Classify(entry, ProfileKind);
        _entryHistory.Add(entry);
        if (_entryHistory.Count > EntryHistoryCap)
        {
            _entryHistory.RemoveAt(0);
        }

        _entryHistoryIndex = -1;
        _savedEntry = "";
        EntryText = "";

        if (reading.Kind is ScopeEntryKind.Stars or ScopeEntryKind.Eram)
        {
            Main.AddHistory("", entry);
            EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Error, NotSupportedMessage, entry);
            return;
        }

        var sink = new ScopeEntrySink(Main);
        try
        {
            LastSentEntryText = reading.SendText;
            await Main.SendTextAsync(reading.SendText, sink);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Scope entry failed");
            sink.ReportError(ex.Message);
        }

        if (!sink.AddedHistory)
        {
            Main.AddHistory("", entry);
        }

        EntryResponse = sink.ResponseTo(entry);
    }

    // A new entry on STARS takes the place of the response shown in its one area.
    private void SetEntry(string text)
    {
        if (!IsEramEntryLayout && (EntryText.Length == 0) && (text.Length > 0))
        {
            EntryResponse = null;
        }

        EntryText = text;
    }

    private static IEnumerable<string> Wrap(string text)
    {
        for (int start = 0; start < text.Length; start += EntryLineWidth)
        {
            yield return text.Substring(start, Math.Min(EntryLineWidth, text.Length - start));
        }
    }

    /// <summary>
    /// Where a Scope entry reports: its last error or status becomes the Scope's response, its history and warnings go to the
    /// terminal as the command line's do, and its clear does nothing, as the Scope empties its preview on every Enter.
    /// </summary>
    private sealed class ScopeEntrySink(MainViewModel main) : ICommandTextSink
    {
        private string? _error;
        private string _status = "";

        public bool AddedHistory { get; private set; }

        public void ReportStatus(string status) => _status = status;

        public void ReportError(string message) => _error = message;

        public void ClearInput() { }

        public void AddHistory(string callsign, string command)
        {
            main.AddHistory(callsign, command);
            AddedHistory = true;
        }

        public void AddWarning(string message) => main.AddWarningEntry(message);

        public ScopeEntryResponse ResponseTo(string entry) =>
            _error is { } error ? new ScopeEntryResponse(ScopeResponseKind.Error, error, entry)
            : string.IsNullOrWhiteSpace(_status) ? new ScopeEntryResponse(ScopeResponseKind.Accept, AcceptMessage, entry)
            : new ScopeEntryResponse(ScopeResponseKind.Text, _status, entry);
    }
}
