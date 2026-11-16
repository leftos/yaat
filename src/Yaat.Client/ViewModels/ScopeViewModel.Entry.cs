using System.Globalization;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

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
/// the entry through <see cref="MainViewModel.SendTextAsync"/> with a sink of the Scope's own, so the main command line's
/// text and status are never touched. A STARS entry sends the YAAT command it translates to, resolved here from the
/// selected aircraft's state where it needs it (bare <c>HND</c>, <c>*J</c>/<c>*P</c>); a STARS entry with no YAAT command
/// is refused with <c>NOT SUPPORTED</c> and nothing is sent. An ERAM entry goes to the server's ERAM dispatch
/// (<see cref="EramEntrySender"/>), which answers as CRC's ERAM does. On a
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
    internal ScopeEntryReading EntryReading => ScopeEntryClassifier.Classify(EntryText, ProfileKind, SelectedOwnerTemplateId);

    /// <summary>
    /// The owner kind of the selected track (<see cref="ScopeOwnerKind.TemplateIdFor"/>), which picks an entry's dialect on a
    /// Mixed profile; null with no track selected.
    /// </summary>
    private string? SelectedOwnerTemplateId => (SelectedAircraft is { } ac) ? ScopeOwnerKind.TemplateIdFor(ac) : null;

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

    /// <summary>F3, F4, F5, F7 and F11 on STARS: the key starts its function's entry in place of what the preview held.</summary>
    /// <returns>False on ERAM or Mixed, and for any other key.</returns>
    internal bool StartStarsFunctionEntry(Key key)
    {
        if ((ProfileKind != ScopeProfileKind.Stars) || (StarsFunctionWord(key) is not { } word))
        {
            return false;
        }

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
    /// to a pilot entry for the same aircraft, else in place of the entry.
    /// </summary>
    internal void StageEntry(string callsign, string command)
    {
        SetEntry(ScopeEntryStaging.Text(EntryText, callsign, command));
        _entryHistoryIndex = -1;
    }

    /// <summary>
    /// Fills the track <paramref name="callsign"/> into the entry (<see cref="ScopeEntryFill.Track"/>), as a click on it with text
    /// in the preview does: first in a pilot entry, else appended, the entry read in the clicked track's dialect on Mixed.
    /// </summary>
    internal void FillTrack(string callsign)
    {
        SetEntry(ScopeEntryFill.Track(EntryText, callsign, ProfileKind, OwnerTemplateIdOf(callsign), IsKnownCallsign));
        _entryHistoryIndex = -1;
    }

    /// <summary>
    /// A middle click on a track with text in the preview: fills the track (<see cref="FillTrack"/>) and runs the entry, both
    /// read in the clicked track's dialect on Mixed, whatever is selected.
    /// </summary>
    internal void FillTrackAndRun(string callsign)
    {
        FillTrack(callsign);
        RunEntry(OwnerTemplateIdOf(callsign));
    }

    /// <summary>
    /// The owner kind of the track <paramref name="callsign"/> names (<see cref="ScopeOwnerKind.TemplateIdFor"/>), which a click on
    /// it reads the entry's dialect from on Mixed; null, the profile's own dialect, when no known aircraft has that callsign.
    /// </summary>
    private string? OwnerTemplateIdOf(string callsign) => (FindAircraft(callsign) is { } ac) ? ScopeOwnerKind.TemplateIdFor(ac) : null;

    /// <summary>Appends the clicked point to the entry: <paramref name="frd"/> when known, else its ERAM lat/long.</summary>
    internal void FillPoint(LatLon point, string? frd)
    {
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
        _entryHistoryIndex = -1;
        RunEntry(OwnerTemplateIdOf(callsign));
    }

    /// <summary>
    /// Whether a left click fills the entry: text in the preview, the Rings tool not armed, and, on a track
    /// (<paramref name="callsign"/> not null), a fill that would change the entry (<see cref="ScopeEntryFill.ChangesTrack"/>).
    /// </summary>
    internal bool FillsOnLeftClick(string? callsign) =>
        (EntryText.Length > 0)
        && !IsRingToolArmed
        && ((callsign is null) || ScopeEntryFill.ChangesTrack(EntryText, callsign, ProfileKind, OwnerTemplateIdOf(callsign), IsKnownCallsign));

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

    /// <summary>
    /// Enter: runs the entry, read in the selected track's dialect on Mixed, which leaves the preview for the response.
    /// </summary>
    /// <returns>False when nothing is typed, or the entry is only the <c>PILOT</c> word, which has nothing to send.</returns>
    public bool ExecuteEntry() => RunEntry(SelectedOwnerTemplateId);

    // Runs the entry, read in the dialect the owner kind ownerTemplateId picks on Mixed.
    private bool RunEntry(string? ownerTemplateId)
    {
        string entry = EntryText.Trim();
        if (entry.Length == 0)
        {
            return false;
        }

        // An unsupported STARS entry sends nothing but still runs, to be answered NOT SUPPORTED.
        ScopeEntryReading reading = ScopeEntryClassifier.Classify(entry, ProfileKind, ownerTemplateId);
        if ((reading.Kind != ScopeEntryKind.Stars) && (reading.SendText.Length == 0))
        {
            return false;
        }

        LastEntryRun = RunEntryAsync(entry, ownerTemplateId);
        return true;
    }

    private async Task RunEntryAsync(string entry, string? ownerTemplateId)
    {
        ScopeEntryReading reading = ScopeEntryClassifier.Classify(entry, ProfileKind, ownerTemplateId);
        _entryHistory.Add(entry);
        if (_entryHistory.Count > EntryHistoryCap)
        {
            _entryHistory.RemoveAt(0);
        }

        _entryHistoryIndex = -1;
        _savedEntry = "";
        EntryText = "";

        if (reading.Kind == ScopeEntryKind.Eram)
        {
            await RunEramEntryAsync(entry);
            return;
        }

        if (!reading.Supported)
        {
            Main.AddHistory("", entry);
            EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Error, NotSupportedMessage, entry, []);
            return;
        }

        (string? sendText, string? refusal) =
            (reading.Kind == ScopeEntryKind.Stars) ? ResolveStarsEntry(entry, reading.SendText) : (reading.SendText, null);
        if (sendText is null)
        {
            Main.AddHistory("", entry);
            EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Error, refusal ?? "", entry, []);
            return;
        }

        var sink = new ScopeEntrySink(Main);
        try
        {
            LastSentEntryText = sendText;
            await Main.SendTextAsync(sendText, sink);
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

    // An ERAM entry: in the history as typed, then to the server's ERAM dispatch with ERAM's own characters where its keys
    // typed them, its answer the response. Only this path maps: a pilot command typed on an ERAM Scope sends as typed.
    private async Task RunEramEntryAsync(string entry)
    {
        Main.AddHistory("", entry);
        Func<ScopeEramEntryDto, Task<ScopeEramEntryResultDto>> send = EramEntrySender ?? Main.SendScopeEramEntryAsync;
        try
        {
            ScopeEramEntryResultDto result = await send(ScopeEramEntryBuilder.Build(ScopeEramKeys.MapTyped(entry)));
            EntryResponse = ScopeEramEntryBuilder.ResponseTo(result, entry);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Scope ERAM entry failed");
            EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Error, ex.Message, entry, []);
        }
    }

    /// <summary>
    /// The text a supported STARS entry sends, once the rows that need the Scope's state have it: bare <c>HND</c> becomes
    /// <c>CANCEL</c> for a handoff outbound from the acting position (else it stays <c>ACCEPT</c>, which the pilot path refuses
    /// when there is no handoff to take), <c>*J</c>/<c>*P</c> act on the selected aircraft's Scope rings, and bare <c>MF R</c>
    /// toggles the selected track's PTL.
    /// </summary>
    /// <returns>The text to send, or null and the refusal to answer with.</returns>
    private (string? Text, string? Refusal) ResolveStarsEntry(string entry, string translation)
    {
        if (StarsEntryParser.TryRead(entry) is not { } read)
        {
            return (translation, null);
        }

        return read.Need switch
        {
            StarsEntryNeed.HandoffAcceptOrCancel => (ResolveHandoff(read, translation), null),
            StarsEntryNeed.RingPlacement or StarsEntryNeed.SelectedTrack => (Main.SelectedAircraft is { } ac)
                ? ($"{translation} {ac.Callsign}", null)
                : (null, MainViewModel.NoAircraftMatchedMessage),
            StarsEntryNeed.RingRemoval => ResolveRingRemoval(read.Ring),
            _ => (translation, null),
        };
    }

    // Bare HND: CANCEL for a handoff from the acting position to another, else ACCEPT. A typed (partial) callsign is matched as
    // the pilot path matches one, and the command then names the matched aircraft's full callsign.
    private string ResolveHandoff(StarsEntryRead read, string translation)
    {
        AircraftModel? ac = (read.Callsign is { } callsign) ? CallsignMatcher.Match(callsign, Main.Aircraft).Match : Main.SelectedAircraft;
        if (ac is null)
        {
            return translation;
        }

        string prefix = (read.Callsign is null) ? "" : $"{ac.Callsign} ";
        return $"{prefix}{(IsOutboundHandoff(ac) ? "CANCEL" : "ACCEPT")}";
    }

    // A handoff from the acting position to another, not one to it; false while the acting position is unknown.
    private bool IsOutboundHandoff(AircraftModel ac)
    {
        if (string.IsNullOrEmpty(Main.ActiveTcp))
        {
            return false;
        }

        var acting = new ActingPosition(Main.ActingCallsign, Main.ActingTrackFacilityId, Main.ActiveTcp);
        var owner = new TrackPositionIdentity(ac.Owner, ac.OwnerFacilityId, ac.OwnerSectorCode);
        var peer = new TrackPositionIdentity(ac.HandoffPeer, ac.HandoffPeerFacilityId, ac.HandoffPeerSectorCode);
        var redirectedBy = new TrackPositionIdentity(
            ac.HandoffRedirectedByCallsign,
            ac.HandoffRedirectedByFacilityId,
            ac.HandoffRedirectedBySectorCode
        );
        return !TrackCoordinationView.IsHandoffToActing(peer, redirectedBy, acting) && TrackCoordinationView.IsHandoffFromActing(owner, peer, acting);
    }

    // Bare *J / *P: the selected aircraft's ring of that kind, at its size, which the ring command toggles off.
    private (string? Text, string? Refusal) ResolveRingRemoval(ScopeRingTool tool)
    {
        if (Main.SelectedAircraft is not { } ac)
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
            _error is { } error ? new ScopeEntryResponse(ScopeResponseKind.Error, error, entry, [])
            : string.IsNullOrWhiteSpace(_status) ? new ScopeEntryResponse(ScopeResponseKind.Accept, AcceptMessage, entry, [])
            : new ScopeEntryResponse(ScopeResponseKind.Text, _status, entry, []);
    }
}
