using System.Globalization;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;
using Yaat.Sim.Commands;

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

/// <summary>The response to one Scope entry: its kind, its message, any lines under it and the entry it echoes.</summary>
/// <param name="Kind">Accept, text or error.</param>
/// <param name="Message">The message: <c>ACCEPT</c>, the echo text or the error.</param>
/// <param name="Echo">The entry as it was typed.</param>
/// <param name="Detail">The lines under the message, such as an ERAM acceptance's descriptor and ACID; empty for none.</param>
public sealed record ScopeEntryResponse(ScopeResponseKind Kind, string Message, string Echo, IReadOnlyList<string> Detail);

/// <summary>One line of a Scope response: its text, and whether it is the message (else the entry's echo).</summary>
internal readonly record struct ScopeResponseLine(string Text, bool IsMessage);

/// <summary>
/// Typing on the Scope: the entry being typed, the Scope's own entry history and the response to the last entry. Enter runs
/// the entry through <see cref="MainViewModel.SendTextAsync(string, ICommandTextSink, CommandScheme)"/> with a sink of the
/// Scope's own, so the main command line's text and status are never touched. A STARS entry sends the YAAT command it
/// translates to, resolved here from the slewed track's state where it needs it (a clicked track, else the selected aircraft:
/// bare <c>HND</c>, <c>*J</c>/<c>*P</c>, <c>**</c>); a STARS entry with no YAAT command
/// is refused with <c>NOT SUPPORTED</c> and nothing is sent. An ERAM entry goes to the server's ERAM dispatch
/// (<see cref="EramEntrySender"/>), which answers as CRC's ERAM does. A <c>$</c> YAAT command sends what follows the <c>$</c>,
/// as it reads (a clicked track's callsign is in it already; with none, it is for the selected aircraft); an entry that reads
/// as none of these sends nothing and is answered <see cref="FormatMessage"/>. A slow answer never overwrites a later entry's
/// or a measure prompt. On a STARS profile the response goes at the next entry's first key, and an answer still in flight
/// then is dropped: it never shows on the Scope (the terminal history still logs the entry). On ERAM and Mixed the response
/// stays until the next entry is sent, and a late answer landing while that entry is typed still shows.
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

    internal const string NotSupportedMessage = StarsErrorText.NotSupported;

    /// <summary>The answer to an entry that reads as neither a STARS or ERAM entry nor a <c>$</c> YAAT command.</summary>
    internal const string FormatMessage = "FORMAT - $ FOR A YAAT COMMAND";

    internal const string AcceptMessage = "ACCEPT";

    /// <summary>The Scope's answer to a CA inhibit that left the track inhibited, on the student's display too.</summary>
    internal const string CaInhibitedMessage = "CA INHIBITED (student display)";

    /// <summary>The Scope's answer to a CA inhibit that re-enabled the track's conflict alerts, on the student's display too.</summary>
    internal const string CaEnabledMessage = "CA ENABLED (student display)";

    /// <summary>The caret drawn after the entry being typed.</summary>
    internal const string Caret = "_";

    /// <summary>How many entries the Scope's own history keeps, as the main command box does.</summary>
    private const int EntryHistoryCap = 50;

    private readonly List<string> _entryHistory = [];

    // The history line Up/Down shows, or -1 when the entry is not a recalled one.
    private int _entryHistoryIndex = -1;

    // The half-typed entry the first Up left, which Down past the newest line puts back.
    private string _savedEntry = "";

    // The callsign of the track a click last filled into the entry (or a Field A middle click's QF named), sent as a pick of
    // that aircraft while its token is still in the entry; null when the entry holds no clicked track.
    private string? _pickedCallsign;

    // The pick of the half-typed entry the first Up left (_savedEntry), which Down past the newest line puts back with it.
    private string? _savedPickedCallsign;

    // The callsigns the clicks on tracks filled into the entry, in order: CRC's slews. A MIN runs only on these, and a STARS
    // entry ending in the last one runs on that track (ClickedSlew). In CRC a click is never text, so any change to EntryText
    // that a click fill did not make drops the whole record (OnEntryTextChanged), typing the same text back included; that
    // covers typing, Backspace, history, staging, Esc and the run's own clear.
    private readonly List<string> _clickSlews = [];

    // True while a click fill writes the entry, so its own write keeps the record.
    private bool _fillingFromClick;

    partial void OnEntryTextChanged(string value)
    {
        if (!_fillingFromClick)
        {
            _clickSlews.Clear();
        }
    }

    // Records a click on callsign whose fill (fill) wrote the entry.
    private void FillFromClick(string callsign, Action fill)
    {
        _fillingFromClick = true;
        try
        {
            fill();
        }
        finally
        {
            _fillingFromClick = false;
        }

        _clickSlews.Add(callsign);
    }

    /// <summary>A STARS entry's slew: the track it acts on, the entry text without a clicked callsign, and whether a click gave it.</summary>
    private readonly record struct ScopeSlew(AircraftModel? Aircraft, string Text, bool Clicked);

    // The number of the latest entry run, bumped as each starts, as a measure prompt shows (ScopeViewModel.Measure.cs) and at a
    // STARS entry's first key; a run answers only while its number is still the latest, so a slow answer never overwrites a
    // later one. Esc does not bump it.
    private int _entrySeq;

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

    /// <summary>Whether the Scope draws ERAM's boxes (an ERAM, Mixed or EuroScope profile) rather than STARS's one area.</summary>
    public bool IsEramEntryLayout => ProfileKind != ScopeProfileKind.Stars;

    /// <summary>How the entry being typed reads.</summary>
    internal ScopeEntryReading EntryReading =>
        ScopeEntryClassifier.Classify(EntryText, ProfileKind, SelectedOwnerTemplateId, Main.Preferences.CommandScheme);

    // How a click on the track callsign reads the entry it fills: in its owner's dialect, with the user's command scheme.
    private ScopeEntryContext FillContext(string callsign) => new(ProfileKind, OwnerTemplateIdOf(callsign), Main.Preferences.CommandScheme);

    /// <summary>
    /// The owner kind of the selected track (<see cref="ScopeOwnerKind.TemplateIdFor"/>), which picks an entry's dialect on a
    /// Mixed or EuroScope profile; null with no track selected.
    /// </summary>
    private string? SelectedOwnerTemplateId => (SelectedAircraft is { } ac) ? EntryOwnerTemplateId(ac) : null;

    /// <summary>
    /// The owner kind an entry reads <paramref name="ac"/>'s dialect from (<see cref="ScopeOwnerKind.TemplateIdFor"/>): an
    /// unowned track takes the zoom band's <see cref="ScopeZoomBand.Unowned"/> on the Mixed and EuroScope profiles, which read
    /// entries alike, and ERAM on any other.
    /// </summary>
    private string EntryOwnerTemplateId(AircraftModel ac) =>
        ScopeOwnerKind.TemplateIdFor(
            ac,
            (ProfileKind is ScopeProfileKind.Mixed or ScopeProfileKind.EuroScope) ? EffectiveBand.Unowned : ScopeTemplates.EramId
        );

    /// <summary>
    /// The entry and its caret, wrapped, the last <see cref="PreviewLineCap"/> lines; empty while nothing is typed. An entry
    /// that reads as ERAM shows ERAM's own characters as its marks.
    /// </summary>
    internal IReadOnlyList<string> EntryPreviewLines => EntryText.Length == 0 ? [] : [.. Wrap(PreviewText).TakeLast(PreviewLineCap)];

    // The entry as the preview shows it: an ERAM entry through ERAM's keyboard and marks, any other entry as typed.
    private string PreviewText =>
        (EntryReading.Kind == ScopeEntryKind.Eram) ? ScopeEramKeys.ToDisplay(ScopeEramKeys.MapTyped(EntryText + Caret)) : (EntryText + Caret);

    /// <summary>The response's message, its detail lines, then its echo, wrapped, the first <see cref="FeedbackLineCap"/> lines.</summary>
    internal IReadOnlyList<ScopeResponseLine> EntryResponseLines =>
        EntryResponse is not { } response
            ? []
            :
            [
                .. new[] { response.Message }
                    .Concat(response.Detail)
                    .Select(ScopeEramKeys.ToDisplay)
                    .SelectMany(Wrap)
                    .Select(line => new ScopeResponseLine(line, true))
                    .Concat(Wrap(ScopeEramKeys.ToDisplay(response.Echo)).Select(line => new ScopeResponseLine(line, false)))
                    .Take(FeedbackLineCap),
            ];

    /// <summary>
    /// Sends an ERAM entry to the server and returns its answer: <see cref="MainViewModel.SendScopeEramEntryAsync"/> unless set.
    /// Test seam.
    /// </summary>
    internal Func<ScopeEramEntryDto, Task<ScopeEramEntryResultDto>>? EramEntrySender { get; set; }

    /// <summary>
    /// Sends a non-ERAM entry's YAAT command through the pilot path with the Scope's sink, read in the command scheme it is
    /// written in: <see cref="MainViewModel.SendTextAsync(string, ICommandTextSink, CommandScheme)"/> unless set. Test seam.
    /// </summary>
    internal Func<string, ICommandTextSink, CommandScheme, Task>? YaatEntrySender { get; set; }

    /// <summary>
    /// Adds typed <paramref name="text"/> to the entry. A STARS entry takes printable ASCII only (0x20-0x7E), upper-cased as
    /// typed, as CRC's STARS preview does; a <c>$</c> entry (a YAAT command) and ERAM and Mixed drop control characters only.
    /// </summary>
    /// <returns>False when nothing printable was typed.</returns>
    public bool TypeEntryText(string? text)
    {
        bool starsEntry = (ProfileKind == ScopeProfileKind.Stars) && !(EntryText + text).TrimStart().StartsWith(ScopeEntryClassifier.YaatPrefix);
        string printable = starsEntry
            ? new([.. (text ?? "").Where(c => c is >= ' ' and <= '~').Select(char.ToUpperInvariant)])
            : new([.. (text ?? "").Where(c => !char.IsControl(c))]);
        if (printable.Length == 0)
        {
            return false;
        }

        SetEntry(EntryText + printable);
        _entryHistoryIndex = -1;
        return true;
    }

    /// <summary>
    /// F3, F4, F5, F7 and F11 on STARS: the key starts its function's entry in place of what the preview held, and drops a
    /// pending <c>*T</c> anchor, as CRC's ResetInput does.
    /// </summary>
    /// <returns>False on ERAM, Mixed or EuroScope, and for any other key.</returns>
    internal bool StartStarsFunctionEntry(Key key)
    {
        if ((ProfileKind != ScopeProfileKind.Stars) || (StarsFunctionWord(key) is not { } word))
        {
            return false;
        }

        ResetRangeBearingEntry();
        ClearEntry();
        SetEntry(word);
        _entryHistoryIndex = -1;
        return true;
    }

    // The key table's entry word for a STARS function key.
    private static string? StarsFunctionWord(Key key) =>
        key switch
        {
            Key.F3 => "INIT ",
            Key.F4 => "TERM ",
            Key.F5 => "HND ",
            Key.F7 => "MF ",
            Key.F11 => "CA ",
            _ => null,
        };

    /// <summary>
    /// Stages <paramref name="command"/> for <paramref name="callsign"/> in the entry (<see cref="ScopeEntryStaging.Text"/>): appended
    /// to a <c>$</c> entry for the same aircraft, else in place of the entry.
    /// </summary>
    internal void StageEntry(string callsign, string command)
    {
        SetEntry(ScopeEntryStaging.Text(EntryText, callsign, command, Main.Preferences.CommandScheme));
        _entryHistoryIndex = -1;
    }

    /// <summary>
    /// Stages the bare ERAM <paramref name="entry"/> a Shift+click on <paramref name="callsign"/>'s block produced, in place of
    /// whatever the preview holds (never appended), with <paramref name="callsign"/> as the clicked track, so Enter sends it as a
    /// pick; the shown response clears.
    /// </summary>
    internal void StageEramText(string callsign, string entry)
    {
        EntryResponse = null;
        SetEntry(entry);
        _pickedCallsign = callsign;
        _entryHistoryIndex = -1;
    }

    /// <summary>
    /// Fills the track <paramref name="callsign"/> into the entry (<see cref="ScopeEntryFill.Track"/>), as a click on it with text
    /// in the preview does: first in a <c>$</c> entry, else appended, the entry read in the clicked track's dialect on Mixed.
    /// </summary>
    internal void FillTrack(string callsign)
    {
        // A *T entry takes the click as its slew and runs, as CRC's does.
        if (TryRunRangeBearingSlew(RblEndpoint.OnAircraft(callsign)))
        {
            return;
        }

        FillFromClick(callsign, () => SetEntry(ScopeEntryFill.Track(EntryText, callsign, FillContext(callsign), IsKnownCallsign)));
        _pickedCallsign = callsign;
        _entryHistoryIndex = -1;
    }

    /// <summary>
    /// A middle click on a track with text in the preview: fills the track (<see cref="FillTrack"/>) and runs the entry, both
    /// read in the clicked track's dialect on Mixed, whatever is selected.
    /// </summary>
    internal void FillTrackAndRun(string callsign)
    {
        // The second track of a MIN entry runs it already, the first track again included.
        if (CompletesMinSep(callsign))
        {
            FillTrackFromClick(callsign);
            return;
        }

        FillTrack(callsign);
        RunEntry(OwnerTemplateIdOf(callsign));
    }

    /// <summary>
    /// The owner kind of the track <paramref name="callsign"/> names (<see cref="ScopeOwnerKind.TemplateIdFor"/>), which a click on
    /// it reads the entry's dialect from on Mixed; null, the profile's own dialect, when no known aircraft has that callsign.
    /// </summary>
    private string? OwnerTemplateIdOf(string callsign) => (FindAircraft(callsign) is { } ac) ? EntryOwnerTemplateId(ac) : null;

    /// <summary>Appends the clicked point to the entry: <paramref name="frd"/> when known, else its ERAM lat/long.</summary>
    internal void FillPoint(LatLon point, string? frd)
    {
        if (TryRunRangeBearingSlew(RblEndpoint.AtPoint(point, frd ?? "")))
        {
            return;
        }

        SetEntry(ScopeEntryFill.Point(EntryText, point, frd));
        _entryHistoryIndex = -1;
    }

    /// <summary>
    /// A middle click on an ERAM Field A with an empty preview: runs <c>QF {callsign}</c> through the entry, read in the clicked
    /// track's dialect on Mixed and sent to the server as any ERAM entry is.
    /// </summary>
    internal void RunQuickFlight(string callsign)
    {
        SetEntry($"QF {callsign}");
        _pickedCallsign = callsign;
        _entryHistoryIndex = -1;
        RunEntry(OwnerTemplateIdOf(callsign));
    }

    /// <summary>
    /// Whether a left click fills the entry: text in the preview, the Rings tool not armed, and, on a track
    /// (<paramref name="callsign"/> not null), the second track of a <c>MIN</c> entry (<see cref="CompletesMinSep"/>, even the
    /// first track again), the slew of a <c>*T</c> entry or of text typed while this Scope's <c>*T</c> anchor is pending
    /// (<see cref="RangeBearingEntryTakesPress"/>) or a fill that would change the entry (<see cref="ScopeEntryFill.ChangesTrack"/>).
    /// </summary>
    internal bool FillsOnLeftClick(string? callsign) =>
        (EntryText.Length > 0)
        && !IsRingToolArmed
        && (
            (callsign is null)
            || CompletesMinSep(callsign)
            || RangeBearingEntryTakesPress
            || ScopeEntryFill.ChangesTrack(EntryText, callsign, FillContext(callsign), IsKnownCallsign)
        );

    private bool IsKnownCallsign(string word) => Main.Aircraft.Any(a => string.Equals(a.Callsign, word, StringComparison.OrdinalIgnoreCase));

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
                _savedPickedCallsign = _pickedCallsign;
            }

            _pickedCallsign = null;

            _entryHistoryIndex = (_entryHistoryIndex < 0) ? (_entryHistory.Count - 1) : Math.Max(0, _entryHistoryIndex - 1);
            SetEntry(_entryHistory[_entryHistoryIndex]);
            return true;
        }

        if (_entryHistoryIndex < 0)
        {
            return false;
        }

        _pickedCallsign = null;
        _entryHistoryIndex++;
        if (_entryHistoryIndex < _entryHistory.Count)
        {
            SetEntry(_entryHistory[_entryHistoryIndex]);
            return true;
        }

        _entryHistoryIndex = -1;
        SetEntry(_savedEntry);
        _pickedCallsign = _savedPickedCallsign;
        _savedEntry = "";
        _savedPickedCallsign = null;
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
        _pickedCallsign = null;
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
        _pickedCallsign = null;
        _entryHistoryIndex = -1;
        return true;
    }

    /// <summary>
    /// Enter: runs the entry, read in the selected track's dialect on Mixed, which leaves the preview for the response.
    /// </summary>
    /// <returns>
    /// False when nothing is typed (unless a <c>*T</c> anchor is pending, which answers FORMAT), or the entry is only the
    /// <c>$</c>, which has nothing to send.
    /// </returns>
    public bool ExecuteEntry() => RunEntry(SelectedOwnerTemplateId);

    // Runs the entry, read in the dialect the owner kind ownerTemplateId picks on Mixed or EuroScope.
    private bool RunEntry(string? ownerTemplateId)
    {
        string entry = EntryText.Trim();
        if (entry.Length == 0)
        {
            return AnswerEmptyRangeBearingEntry();
        }

        // An unsupported STARS entry and an entry that reads as nothing send nothing but still run, to be answered; so does any
        // entry while a *T anchor waits for its completion.
        ScopeEntryReading reading = ScopeEntryClassifier.Classify(entry, ProfileKind, ownerTemplateId, Main.Preferences.CommandScheme);
        if ((reading.Kind is not (ScopeEntryKind.Stars or ScopeEntryKind.None)) && (reading.SendText.Length == 0) && !HasRangeBearingEntryPending)
        {
            return false;
        }

        LastEntryRun = RunEntryAsync(entry, ownerTemplateId, ++_entrySeq);
        return true;
    }

    // Shows response as the answer to the run numbered seq, unless a later run has started since.
    private void Answer(int seq, ScopeEntryResponse response)
    {
        if (seq == _entrySeq)
        {
            EntryResponse = response;
        }
    }

    private async Task RunEntryAsync(string entry, string? ownerTemplateId, int seq)
    {
        ScopeEntryReading reading = ScopeEntryClassifier.Classify(entry, ProfileKind, ownerTemplateId, Main.Preferences.CommandScheme);
        IReadOnlyList<string> clicks = [.. _clickSlews];
        ScopeSlew? clicked = ClickedSlew(entry, clicks);
        string? pickedCallsign = TakeEntry(entry);

        // A *T entry, and any entry completing a *T anchor, runs on the Scope (ScopeViewModel.Measure.cs) and sends nothing.
        if (TryRunRangeBearingEntry(entry, reading))
        {
            return;
        }

        if (reading.Kind == ScopeEntryKind.Eram)
        {
            await RunEramEntryAsync(entry, pickedCallsign, seq);
            return;
        }

        // MIN runs on the Scope (ScopeViewModel.MinSep.cs) and sends nothing.
        if ((reading.Kind == ScopeEntryKind.Stars) && reading.Supported && TryRunMinSep(entry, clicks))
        {
            return;
        }

        (string? sendText, string? refusal) = ResolveEntry(entry, reading, clicked, ownerTemplateId);
        if (sendText is null)
        {
            Main.AddHistory("", entry);
            Answer(seq, new ScopeEntryResponse(ScopeResponseKind.Error, refusal ?? "", entry, []));
            return;
        }

        await SendEntryAsync(entry, sendText, SendSchemeFor(reading.Kind), seq);
    }

    // A STARS entry ending in the callsign a click filled runs without it, on that track (CRC's slew); any other on the
    // selected aircraft.
    private (string? Text, string? Refusal) ResolveEntry(string entry, ScopeEntryReading reading, ScopeSlew? clicked, string? ownerTemplateId) =>
        (clicked is { } slew)
            ? ResolveSendText(slew.Text, ScopeEntryClassifier.Classify(slew.Text, ProfileKind, ownerTemplateId, Main.Preferences.CommandScheme), slew)
            : ResolveSendText(entry, reading, new ScopeSlew(Main.SelectedAircraft, entry, false));

    /// <summary>
    /// The slew a click gave <paramref name="entry"/>: when its last word is the callsign the last click filled, and the entry
    /// without that word reads, in the clicked track's dialect, as a STARS entry whose CRC form ends in a slew (one with an
    /// answer for no slew, bare <c>HND</c>, bare <c>*J</c>/<c>*P</c>), that track and the entry without its callsign; null
    /// otherwise, so a typed trailing callsign reads as typed.
    /// </summary>
    private ScopeSlew? ClickedSlew(string entry, IReadOnlyList<string> clicks)
    {
        int space = entry.LastIndexOf(' ');
        if ((space < 0) || (clicks.Count == 0) || !string.Equals(entry[(space + 1)..], clicks[^1], StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = entry[..space].TrimEnd();
        string callsign = clicks[^1];
        bool stars =
            ScopeEntryClassifier.Classify(rest, ProfileKind, OwnerTemplateIdOf(callsign), Main.Preferences.CommandScheme).Kind
            == ScopeEntryKind.Stars;
        return (stars && EndsInSlew(StarsEntryParser.TryRead(rest)) && (FindAircraft(callsign) is { } ac)) ? new ScopeSlew(ac, rest, true) : null;
    }

    // Whether a STARS entry's CRC form ends in a slew.
    private static bool EndsInSlew(StarsEntryRead? read) =>
        read is { WithoutSlew: not null } or { Need: StarsEntryNeed.RingRemoval } or { Need: StarsEntryNeed.HandoffAccept, Callsign: null };

    // The command scheme an entry's YAAT command is written in: a STARS entry's translation in the default verbs, any other
    // entry in the user's own.
    private CommandScheme SendSchemeFor(ScopeEntryKind kind) =>
        (kind == ScopeEntryKind.Stars) ? CommandScheme.Default() : Main.Preferences.CommandScheme;

    // Puts the entry Enter ran in the Scope's history and empties the preview; returns the clicked track the entry held.
    private string? TakeEntry(string entry)
    {
        _entryHistory.Add(entry);
        if (_entryHistory.Count > EntryHistoryCap)
        {
            _entryHistory.RemoveAt(0);
        }

        _entryHistoryIndex = -1;
        _savedEntry = "";
        _savedPickedCallsign = null;
        string? pickedCallsign = _pickedCallsign;
        _pickedCallsign = null;
        EntryText = "";
        return pickedCallsign;
    }

    // The text a non-ERAM entry sends, or null and its refusal: an entry that reads as nothing answers FORMAT, a STARS entry
    // with no YAAT command CRC's category where CRC refuses it (StarsRefusal), else NOT SUPPORTED. A $ command sends as it reads:
    // a clicked track is in its text already (the click fill wrote it), and one naming no callsign is for the selected aircraft,
    // which SendTextAsync supplies.
    private (string? Text, string? Refusal) ResolveSendText(string entry, ScopeEntryReading reading, ScopeSlew slew) =>
        reading.Kind switch
        {
            ScopeEntryKind.None => (null, FormatMessage),
            _ when !reading.Supported => (null, StarsRefusal(entry, slew.Aircraft)),
            ScopeEntryKind.Stars => ResolveStarsEntry(entry, reading.SendText, slew),
            _ => (reading.SendText, null),
        };

    // The answer to a STARS entry with no YAAT command: CRC's category where CRC refuses it, else, with no slewed aircraft, its
    // answer for no slew, else NOT SUPPORTED.
    private static string StarsRefusal(string entry, AircraftModel? slewed) =>
        StarsEntryParser.TryRead(entry) switch
        {
            { Error: { } error } => error,
            { WithoutSlew: { } withoutSlew } when slewed is null => withoutSlew,
            _ => NotSupportedMessage,
        };

    // Sends sendText, the entry's YAAT command written in scheme, through the pilot path with the Scope's own sink; its answer
    // the response.
    private async Task SendEntryAsync(string entry, string sendText, CommandScheme scheme, int seq)
    {
        var sink = new ScopeEntrySink(Main);
        try
        {
            LastSentEntryText = sendText;
            await ((YaatEntrySender is { } send) ? send(sendText, sink, scheme) : Main.SendTextAsync(sendText, sink, scheme));
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

        Answer(seq, CaInhibitAnswer(sendText, sink.ResponseTo(entry)));
    }

    /// <summary>
    /// <paramref name="response"/>, unless <paramref name="sendText"/> is a CA inhibit (<c>CAINH</c>, which STARS <c>CA K</c>
    /// and F11's <c>CA </c> send) and the Sim answered it: that inhibit acts on the student's display as well, so the Scope
    /// names it (7110.65 §5-14-6.c.4), its direction the Sim's own "inhibited" or "enabled".
    /// </summary>
    internal static ScopeEntryResponse CaInhibitAnswer(string sendText, ScopeEntryResponse response)
    {
        string[] words = sendText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool isInhibit = (words.Length > 0) && words[^1].TrimStart('$').Equals("CAINH", StringComparison.OrdinalIgnoreCase);
        if ((response.Kind != ScopeResponseKind.Text) || !isInhibit)
        {
            return response;
        }

        string? message =
            response.Message.Contains("inhibited", StringComparison.OrdinalIgnoreCase) ? CaInhibitedMessage
            : response.Message.Contains("enabled", StringComparison.OrdinalIgnoreCase) ? CaEnabledMessage
            : null;
        return (message is null) ? response : response with { Message = message };
    }

    /// <summary>
    /// Runs the ERAM <paramref name="entry"/> a click on <paramref name="callsign"/>'s block produced: to the server's ERAM
    /// dispatch with <paramref name="callsign"/> as the clicked track (sent as a pick where it is a token of its own), its
    /// answer the response. As CRC's automatic entries, it joins no Scope entry recall (Up, Down, Ctrl+R), though the terminal
    /// log still records it, and the preview text is left as it is.
    /// </summary>
    internal Task RunEramText(string callsign, string entry)
    {
        Task run = RunEramEntryAsync(entry, callsign, ++_entrySeq);
        LastEntryRun = run;
        return run;
    }

    // An ERAM entry, the run numbered seq: in the history as typed, then to the server's ERAM dispatch with ERAM's own
    // characters where its keys typed them, its answer the response. Only this path maps: a $ command typed on an ERAM Scope
    // sends as typed. The clicked track's callsign, when still in the entry, goes as a pick of that aircraft.
    private async Task RunEramEntryAsync(string entry, string? pickedCallsign, int seq)
    {
        Main.AddHistory("", entry);
        Func<ScopeEramEntryDto, Task<ScopeEramEntryResultDto>> send = EramEntrySender ?? Main.SendScopeEramEntryAsync;
        try
        {
            ScopeEramEntryResultDto result = await send(ScopeEramEntryBuilder.Build(ScopeEramKeys.MapTyped(entry), pickedCallsign));
            Answer(seq, ScopeEramEntryBuilder.ResponseTo(result, entry));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Scope ERAM entry failed");
            Answer(seq, new ScopeEntryResponse(ScopeResponseKind.Error, ex.Message, entry, []));
        }
    }

    /// <summary>
    /// The text a supported STARS entry sends, once the rows that need the Scope's state have it. <paramref name="slew"/> is
    /// CRC's slew (a clicked track, else the selected aircraft): an entry that acts on one (it has a
    /// <see cref="StarsEntryRead.WithoutSlew"/>) answers that when there is none, else runs on it (<see cref="ResolveOnSlew"/>);
    /// any other runs by what it needs (<see cref="ResolveStarsNeed"/>).
    /// </summary>
    /// <returns>The text to send, or null and the refusal to answer with.</returns>
    private (string? Text, string? Refusal) ResolveStarsEntry(string entry, string translation, ScopeSlew slew)
    {
        if (StarsEntryParser.TryRead(entry) is not { } read)
        {
            return (translation, null);
        }

        if (read.WithoutSlew is not { } withoutSlew)
        {
            return ResolveStarsNeed(read, translation, slew);
        }

        return (slew.Aircraft is { } slewed) ? ResolveOnSlew(read, translation, slewed) : (null, withoutSlew);
    }

    /// <summary>
    /// An entry that acts on the slewed track <paramref name="slewed"/>: <c>*J</c>/<c>*P</c> with a size and bare <c>MF R</c>
    /// name it after the translation, bare <c>**</c> converts its point-out or force-quicklooks it
    /// (<see cref="ResolvePointoutOrQuicklook"/>), and a leader direction names it first.
    /// </summary>
    private (string? Text, string? Refusal) ResolveOnSlew(StarsEntryRead read, string translation, AircraftModel slewed) =>
        read.Need switch
        {
            StarsEntryNeed.RingPlacement or StarsEntryNeed.SelectedTrack => ($"{translation} {slewed.Callsign}", null),
            StarsEntryNeed.ConvertPointoutOrQuicklook => ResolvePointoutOrQuicklook(translation, slewed),
            _ => ($"{slewed.Callsign} {translation}", null),
        };

    /// <summary>
    /// An entry that needs no slew by what it needs: bare <c>HND</c> accepts a handoff (<see cref="ResolveHandoff"/>), bare
    /// <c>*J</c>/<c>*P</c> remove the slewed track's Scope ring, and <c>MF L&lt;d&gt; &lt;id&gt;</c> finds the id's track
    /// (<see cref="ResolveLeaderForTrack"/>).
    /// </summary>
    private (string? Text, string? Refusal) ResolveStarsNeed(StarsEntryRead read, string translation, ScopeSlew slew) =>
        read.Need switch
        {
            StarsEntryNeed.HandoffAccept => ResolveHandoff(read, translation, slew),
            StarsEntryNeed.RingRemoval => ResolveRingRemoval(read.Ring, slew.Aircraft),
            StarsEntryNeed.LeaderForTrack when read.TrackId is { } id => ResolveLeaderForTrack(id, translation),
            // The parser always gives MF L<d> <id> its id; an entry without one cannot be read.
            StarsEntryNeed.LeaderForTrack => (null, StarsErrorText.Format),
            _ => (translation, null),
        };

    /// <summary>
    /// <c>HND</c> with no handoff id accepts, never cancels: a typed (partial) callsign is matched as the pilot path matches one,
    /// and the command names the matched aircraft's full callsign; with none typed it is for the slewed track (a click, else the
    /// selected aircraft); with no slew either, for the handoff to the acting position nearest the home centre, else
    /// <see cref="StarsErrorText.NoFlight"/>, as CRC's bare HND (InputManager.ProcessHandoffCommand).
    /// </summary>
    private (string? Text, string? Refusal) ResolveHandoff(StarsEntryRead read, string translation, ScopeSlew slew)
    {
        if (read.Callsign is { } callsign)
        {
            return (CallsignMatcher.Match(callsign, Main.Aircraft).Match is { } typed) ? ($"{typed.Callsign} ACCEPT", null) : (translation, null);
        }

        if (slew.Aircraft is { } slewed)
        {
            return (slew.Clicked ? $"{slewed.Callsign} {translation}" : translation, null);
        }

        return (NearestIncomingHandoff() is { } nearest) ? ($"{nearest.Callsign} {translation}", null) : (null, StarsErrorText.NoFlight);
    }

    // The aircraft in handoff to the acting position nearest the Scope's home centre (its map source centre: the primary
    // airport when its position is known, else the first area's centre; CRC orders the area's visibility centre first, a
    // difference booked on its own), or the display centre while there is no home centre; null with none, or while the acting
    // position is unknown.
    private AircraftModel? NearestIncomingHandoff()
    {
        if (ActingAs() is not { } acting)
        {
            return null;
        }

        LatLon centre = HomeCenter(Main.ScopeMaps) ?? new LatLon(CenterLat, CenterLon);
        return Main
            .Aircraft.Where(ac => TrackCoordinationView.IsHandoffToActing(HandoffPeerOf(ac), RedirectedByOf(ac), acting))
            .MinBy(ac => GeoMath.DistanceNm(ac.Position, centre));
    }

    /// <summary>
    /// Bare <c>**</c> on the slewed track: <c>POHO</c> converts a point-out pending to the acting position, else <c>FQL</c>
    /// force-quicklooks the track to the acting position's own TCP, each named for the track;
    /// <see cref="StarsErrorText.IllegalPosition"/> while the acting position is unknown.
    /// </summary>
    private (string? Text, string? Refusal) ResolvePointoutOrQuicklook(string translation, AircraftModel ac)
    {
        if (ActingAs() is not { } acting)
        {
            return (null, StarsErrorText.IllegalPosition);
        }

        bool pointoutToUs =
            AircraftCommandApplicability.HasPendingPointout(ac) && TrackCoordinationView.IsPointoutToActing(ac.PointoutRecipientCode, acting);
        return ($"{ac.Callsign} {(pointoutToUs ? translation : $"FQL {Main.ActiveTcp}")}", null);
    }

    /// <summary>
    /// <c>MF L&lt;d&gt; &lt;id&gt;</c>: <paramref name="direction"/> (the translation, <c>LDR d</c>) for the track <paramref name="id"/>
    /// names, as CRC's LookupAssociatedTrack finds it: a four-digit beacon code (<see cref="MainViewModel.TracksWithBeacon"/>),
    /// else a callsign matched whole, ignoring case. None is <see cref="StarsErrorText.NoTrack"/>, several
    /// <see cref="StarsErrorText.DuplicateTrack"/>; a one- or two-digit line number has no YAAT meaning
    /// (<see cref="StarsErrorText.NotSupported"/>).
    /// </summary>
    private (string? Text, string? Refusal) ResolveLeaderForTrack(string id, string direction)
    {
        if ((id.Length <= 2) && id.All(char.IsAsciiDigit))
        {
            return (null, StarsErrorText.NotSupported);
        }

        AircraftModel[] tracks =
            (id.Length == 4) && id.All(char.IsAsciiDigit)
                ? Main.TracksWithBeacon(id)
                : [.. Main.Aircraft.Where(ac => string.Equals(ac.Callsign, id, StringComparison.OrdinalIgnoreCase))];
        return tracks switch
        {
            [] => (null, StarsErrorText.NoTrack),
            [AircraftModel track] => ($"{track.Callsign} {direction}", null),
            _ => (null, StarsErrorText.DuplicateTrack),
        };
    }

    // The position the client acts as; null while it is unknown.
    private ActingPosition? ActingAs() =>
        string.IsNullOrEmpty(Main.ActiveTcp) ? null : new ActingPosition(Main.ActingCallsign, Main.ActingTrackFacilityId, Main.ActiveTcp);

    private static TrackPositionIdentity HandoffPeerOf(AircraftModel ac) => new(ac.HandoffPeer, ac.HandoffPeerFacilityId, ac.HandoffPeerSectorCode);

    private static TrackPositionIdentity RedirectedByOf(AircraftModel ac) =>
        new(ac.HandoffRedirectedByCallsign, ac.HandoffRedirectedByFacilityId, ac.HandoffRedirectedBySectorCode);

    // Bare *J / *P: the slewed track's ring of that kind, at its size, which the ring command toggles off.
    private (string? Text, string? Refusal) ResolveRingRemoval(ScopeRingTool tool, AircraftModel? slewed)
    {
        if (slewed is not { } ac)
        {
            return (null, MainViewModel.NoAircraftMatchedMessage);
        }

        ScopeTrackRings rings = RingsOf(ac.Callsign);
        double? size = (tool == ScopeRingTool.JRing) ? rings.JRingNm : rings.ConeNm;
        string kind = ScopeRingTools.NameOf(tool);
        return (size is { } nm)
            ? ($".scope rings {kind} {nm.ToString("0.#", CultureInfo.InvariantCulture)} {ac.Callsign}", null)
            : (null, $"No {((tool == ScopeRingTool.JRing) ? "J-ring" : "cone")} on {ac.Callsign}");
    }

    // A new entry on STARS takes the place of the response shown in its one area, and drops the answer of a run still in flight.
    private void SetEntry(string text)
    {
        if (!IsEramEntryLayout && (EntryText.Length == 0) && (text.Length > 0))
        {
            EntryResponse = null;
            _entrySeq++;
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
            _error is { } error ? new ScopeEntryResponse(ScopeResponseKind.Error, error, entry, [])
            : string.IsNullOrWhiteSpace(_status) ? new ScopeEntryResponse(ScopeResponseKind.Accept, AcceptMessage, entry, [])
            : new ScopeEntryResponse(ScopeResponseKind.Text, _status, entry, []);
    }
}
