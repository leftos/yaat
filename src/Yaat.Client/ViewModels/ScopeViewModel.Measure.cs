using System.ComponentModel;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's side of the distance measuring tool. The lines live in the shared <see cref="RangeBearingViewState"/>
/// (<see cref="MainViewModel.Measure"/>): session state in the one 15-slot pool, never saved, kept while the primary Scope's pop-out is closed
/// (an extra Scope window drops its own when it closes) and not cleared with the Scope's per-track state. A line or anchor taken here belongs to this Scope window and renders only here. Feedback
/// for a pointer or Ctrl+M action the Scope itself starts also shows in the Scope's response, and the status line shows the
/// feedback of those actions and of the dot commands, whichever surface started them. A <c>*T</c> entry answers as CRC's
/// preview does: a typed <c>*T</c> entry, or one slewed by a click, shows nothing on success in either the Scope's response or
/// the status line. A click completing a <c>*T</c> anchor shows no Scope response but reports the placed reading through the
/// status line, as every pick does (<see cref="RangeBearingViewState.Pick"/>). A refusal shows its CRC error category in the
/// Scope's response.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>The Scope reads distances in nautical miles, as STARS does.</summary>
    public const RblUnits MeasureUnits = RblUnits.NauticalMiles;

    /// <summary>Measurements taken here belong to the Scope view and render only here.</summary>
    public const RblView MeasureView = RblView.Scope;

    /// <summary>The shared measuring-tool state.</summary>
    public RangeBearingViewState Measure => Main.Measure;

    /// <summary>Resolves a latched measurement endpoint against the traffic the Scope draws.</summary>
    public RblTrackLookup MeasureTrackLookup => RangeBearingViewState.TrackLookup(FindAircraft);

    /// <summary>Where a measurement taken here goes: the Scope view, this Scope's traffic and units, and this Scope window.</summary>
    public MeasureTarget MeasureTarget => new(MeasureView, MeasureTrackLookup, MeasureUnits, this);

    /// <summary>The response <see cref="ArmMeasure"/> last wrote, so Esc can tell the tool's own prompt from another response.</summary>
    private ScopeEntryResponse? _measureResponse;

    /// <summary>
    /// The id (<see cref="RangeBearingViewState.PendingAnchorId"/>) of the anchor a <c>*T</c> entry on this Scope started; while
    /// it is still the pending anchor, the next entry is read as its completion
    /// (<see cref="StarsEntryParser.ReadRangeBearingCompletion"/>), as CRC's CompletePendingRbl reads it.
    /// </summary>
    private long? _rangeBearingEntryAnchorId;

    /// <summary>Whether this Scope is the surface the measuring tool acts on now.</summary>
    public bool IsActiveMeasureSurface => ReferenceEquals(Main.ActiveMeasureScope, this);

    /// <summary>Makes this Scope the active measure surface, as the canvas taking the focus does.</summary>
    public void ActivateMeasureSurface() => Main.SetActiveMeasureScope(this);

    /// <summary>
    /// Ctrl+M (Cmd+M on macOS): makes the Scope the active surface and arms the tool, which is global, as the main window's
    /// Ctrl+M is. Its prompt shows in the Scope's response and the status line; a full store shows there as an error and
    /// nothing is armed. Only an arm the Scope starts writes the Scope's response; one from another surface or window does not.
    /// </summary>
    public void ArmMeasure()
    {
        ActivateMeasureSurface();
        (bool armed, string status) = Measure.ArmSilently();
        Main.StatusText = status;
        _measureResponse = new ScopeEntryResponse(armed ? ScopeResponseKind.Text : ScopeResponseKind.Error, status, "", []);
        // The prompt is the latest answer: a slow entry's answer arriving now does not replace it.
        _entrySeq++;
        EntryResponse = _measureResponse;
    }

    /// <summary>
    /// A point the Scope's pointer picked (an armed left click, an Alt-click): makes the Scope the active surface and feeds the
    /// tool, the first pick anchoring on the Scope and the second completing the line. Its status shows in the status line and
    /// the Scope's response (an error for a full store); a prompt for the next point counts as the tool's own, for Esc.
    /// </summary>
    public void PickMeasurePoint(RblEndpoint endpoint)
    {
        ActivateMeasureSurface();
        bool completesEntry = HasRangeBearingEntryPending;
        string? status = null;
        void Capture(string reported) => status = reported;
        Measure.StatusReported += Capture;
        try
        {
            Measure.Pick(endpoint, MeasureTarget);
        }
        finally
        {
            Measure.StatusReported -= Capture;
        }

        if (completesEntry)
        {
            AnswerRangeBearingClick();
            return;
        }

        if (status is not null)
        {
            ShowMeasureResponse(status, isPrompt: true);
        }
    }

    /// <summary>
    /// An Alt-drag on the Scope: makes the Scope the active surface and places the whole line on the Scope, its reading (or the
    /// full-store error) in the status line and the Scope's response.
    /// </summary>
    public void PlaceMeasurement(RblEndpoint from, RblEndpoint to)
    {
        ActivateMeasureSurface();
        (_, string status) = Measure.PlaceSilently(from, to, MeasureTarget);
        Main.StatusText = status;
        // A placed reading is never the tool's prompt, even while another surface's anchor keeps the tool armed.
        ShowMeasureResponse(status, isPrompt: false);
    }

    /// <summary>
    /// A right press on the Scope while the tool is armed: makes the Scope the active surface and cancels the anchor this Scope
    /// picked, or the armed tool with no anchor. An anchor another Scope window or the Ground view holds stays (the canvas
    /// opens its menu for that press instead). The status line says so and the Scope's response keeps what it shows, less the
    /// tool's own prompt.
    /// </summary>
    public void CancelMeasureFromPointer()
    {
        ActivateMeasureSurface();
        if ((Measure.Anchor is not { } anchor) || anchor.BelongsTo(MeasureView, this))
        {
            Measure.Cancel();
        }
    }

    // A Scope-started measure status in the response: an error for a full store, text otherwise. A pick's status while the tool
    // stays armed is the tool's own prompt, which Esc takes with the measurement and a disarm elsewhere takes down; a placed
    // reading (isPrompt false) never is.
    private void ShowMeasureResponse(string status, bool isPrompt)
    {
        var response = new ScopeEntryResponse(
            status == RangeBearingViewState.FullStatus ? ScopeResponseKind.Error : ScopeResponseKind.Text,
            status,
            "",
            []
        );
        _measureResponse = (isPrompt && Measure.IsMeasuring) ? response : null;
        _entrySeq++;
        EntryResponse = response;
    }

    /// <summary>
    /// The tool disarming anywhere (a cancel on another surface, <c>.norbl</c>, <c>.rbl N</c>) takes down the prompt
    /// <see cref="ArmMeasure"/> wrote, while it still shows; any other response stays.
    /// </summary>
    private void OnMeasurePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName != nameof(RangeBearingViewState.IsMeasuring)) || Measure.IsMeasuring || (_measureResponse is null))
        {
            return;
        }

        if (ReferenceEquals(EntryResponse, _measureResponse))
        {
            EntryResponse = null;
        }

        _measureResponse = null;
    }

    /// <summary>
    /// Esc's measure step when the entry is empty and the response showing is the one <see cref="ArmMeasure"/> wrote: the prompt
    /// counts as measure state, so one press takes it and cancels the Scope's measurement (<see cref="CancelMeasure"/>).
    /// </summary>
    /// <returns>False when the entry holds text or the response is not the tool's own.</returns>
    public bool TakeMeasurePrompt()
    {
        if ((EntryText.Length > 0) || (EntryResponse is null) || !ReferenceEquals(EntryResponse, _measureResponse))
        {
            return false;
        }

        EntryResponse = null;
        _measureResponse = null;
        CancelMeasure();
        return true;
    }

    /// <summary>The <c>*T</c> entry the preview holds, read by CRC's grammar, while it reads as a STARS entry; null otherwise.</summary>
    private StarsRblEntry? RangeBearingEntryTyped =>
        (EntryReading.Kind == ScopeEntryKind.Stars) ? StarsEntryParser.ReadRangeBearing(EntryText) : null;

    /// <summary>
    /// Whether the anchor a <c>*T</c> entry on this Scope started still waits for the entry that completes it: the store's
    /// pending anchor is still that one (completing, cancelling, replacing or pruning it ends the wait), and the Scope shows
    /// STARS.
    /// </summary>
    private bool HasRangeBearingEntryPending =>
        (ProfileKind == ScopeProfileKind.Stars) && (_rangeBearingEntryAnchorId is { } id) && (Measure.PendingAnchorId == id);

    /// <summary>
    /// Whether a left press goes to the entry ahead of the measuring tool, whatever armed the tool: a <c>*T</c> entry in the
    /// preview takes it as its slew, and text in the preview while this Scope's <c>*T</c> anchor is pending takes it to answer
    /// FORMAT. The Rings tool armed keeps the press its own.
    /// </summary>
    internal bool RangeBearingEntryTakesPress =>
        (EntryText.Length > 0) && !IsRingToolArmed && ((RangeBearingEntryTyped is not null) || HasRangeBearingEntryPending);

    /// <summary>
    /// Drops this Scope's pending <c>*T</c> anchor, as CRC's ResetInput does: Esc, a function key, the profile changing, and a
    /// scenario, rewind or room change (<see cref="MainViewModel"/>). The store's anchor goes too while it is still that one.
    /// </summary>
    internal void ResetRangeBearingEntry()
    {
        if (_rangeBearingEntryAnchorId is { } id)
        {
            Measure.DropAnchor(id);
        }

        _rangeBearingEntryAnchorId = null;
    }

    /// <summary>
    /// Esc's first step while this Scope's <c>*T</c> anchor is pending: one press clears the text, the response and the anchor,
    /// as CRC's Esc does.
    /// </summary>
    /// <returns>False with no pending <c>*T</c> anchor of this Scope's.</returns>
    public bool EscapeRangeBearingEntry()
    {
        if (!HasRangeBearingEntryPending)
        {
            return false;
        }

        ResetRangeBearingEntry();
        ClearEntry();
        return true;
    }

    // A *T anchor belongs to the STARS profile it was taken on: a profile change drops this Scope's, and leaving STARS drops the
    // main line's anchored on this Scope too.
    partial void OnProfileKindChanged(ScopeProfileKind value)
    {
        ResetRangeBearingEntry();
        if (value != ScopeProfileKind.Stars)
        {
            Main.ResetLineRangeBearingOn(this);
        }
    }

    /// <summary>
    /// A click on a track or the map while the preview holds a <c>*T</c> entry, or holds text while this Scope's <c>*T</c>
    /// anchor is pending: the slew, which runs the entry as CRC's does. Bare <c>*T</c> anchors a line at the slewed item on this
    /// Scope (<c>CAPACITY</c> with every line in use); any other <c>*T</c> entry with a slew, and text with a slew while an
    /// anchor is pending, is <c>FORMAT</c>, the anchor kept.
    /// </summary>
    /// <returns>False when neither holds, so the click fills the entry as usual.</returns>
    private bool TryRunRangeBearingSlew(RblEndpoint slewed)
    {
        bool pending = HasRangeBearingEntryPending && (EntryText.Length > 0);
        StarsRblEntry? typed = RangeBearingEntryTyped;
        if (!pending && (typed is null))
        {
            return false;
        }

        string entry = EntryText.Trim();
        TakeEntry(entry);
        _entrySeq++;
        bool anchors = !pending && (typed?.Action == StarsRblAction.Bare);
        AnswerRangeBearing(entry, anchors ? StartRangeBearingAnchor(slewed) : StarsErrorText.Format);
        return true;
    }

    // A click that met this Scope's pending *T anchor: none on success, as CRC shows none; CAPACITY with every line in use, the
    // anchor kept pending.
    private void AnswerRangeBearingClick()
    {
        bool placed = !HasRangeBearingEntryPending;
        if (placed)
        {
            _rangeBearingEntryAnchorId = null;
        }

        _entrySeq++;
        EntryResponse = placed ? null : new ScopeEntryResponse(ScopeResponseKind.Error, StarsErrorText.Capacity, "", []);
    }

    /// <summary>Enter on an empty preview while this Scope's <c>*T</c> anchor is pending: FORMAT, the anchor kept.</summary>
    /// <returns>False with no pending anchor, so an empty Enter does nothing.</returns>
    private bool AnswerEmptyRangeBearingEntry()
    {
        if (!HasRangeBearingEntryPending)
        {
            return false;
        }

        _entrySeq++;
        EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Error, StarsErrorText.Format, "", []);
        return true;
    }

    // A $ entry is a YAAT command, and *T is none: $*T answers FORMAT rather than reach the main line's *T.
    private static bool IsYaatStarT(string entry) =>
        (entry.Length > 0)
        && (entry[0] == ScopeEntryClassifier.YaatPrefix)
        && entry[1..].TrimStart().StartsWith("*T", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs a <c>*T</c> entry with nothing slewed, or the entry completing a <c>*T</c> anchor (CRC's CompletePendingRbl), on the
    /// Scope: it sends nothing, answers nothing on success, and answers CRC's error category otherwise.
    /// </summary>
    /// <returns>False when <paramref name="entry"/> is neither, so it runs as any other entry.</returns>
    private bool TryRunRangeBearingEntry(string entry, ScopeEntryReading reading)
    {
        string? refusal;
        if (HasRangeBearingEntryPending)
        {
            refusal = CompleteRangeBearingEntry(entry);
        }
        else if (IsYaatStarT(entry))
        {
            refusal = StarsErrorText.Format;
        }
        else if ((reading.Kind == ScopeEntryKind.Stars) && (StarsEntryParser.ReadRangeBearing(entry) is { } rbl))
        {
            refusal = RunRangeBearingEntry(rbl);
        }
        else
        {
            return false;
        }

        AnswerRangeBearing(entry, refusal);
        return true;
    }

    // A *T entry's answer: none on success, as CRC's preview shows none, else the error category.
    private void AnswerRangeBearing(string entry, string? refusal)
    {
        Main.AddHistory("", entry);
        EntryResponse = (refusal is null) ? null : new ScopeEntryResponse(ScopeResponseKind.Error, refusal, entry, []);
    }

    // A *T entry with nothing slewed; returns its refusal, or null.
    private string? RunRangeBearingEntry(StarsRblEntry rbl) =>
        rbl.Action switch
        {
            StarsRblAction.Bare => ClearRangeBearingLines(),
            StarsRblAction.Remove => RemoveRangeBearingLine(rbl.Slot),
            StarsRblAction.RblId => StarsErrorText.RblId,
            StarsRblAction.Beacon or StarsRblAction.Name => StartResolvedAnchor(Main.ResolveRangeBearingTarget(rbl)),
            _ => StarsErrorText.Format,
        };

    // *T alone: every line this Scope placed goes, and the anchor it holds, as CRC clears its display's lines.
    private string? ClearRangeBearingLines()
    {
        Measure.RemoveSurface(this);
        return null;
    }

    // *T<n>: removes line n when this Scope placed it; an empty slot, or another surface's line, is RBL ID.
    private string? RemoveRangeBearingLine(int slot)
    {
        bool ours = Measure.Lines.Any(line => (line.Slot == slot) && (line.View == MeasureView) && ReferenceEquals(line.Surface, this));
        return (ours && Measure.RemoveSilently(slot).Removed) ? null : StarsErrorText.RblId;
    }

    private string? StartResolvedAnchor((RblEndpoint? Endpoint, string? Error) resolved) =>
        (resolved.Endpoint is { } endpoint) ? StartRangeBearingAnchor(endpoint) : resolved.Error;

    // Anchors a *T line at endpoint on this Scope, whose next slew or entry completes it; CAPACITY with every line in use.
    private string? StartRangeBearingAnchor(RblEndpoint endpoint)
    {
        ActivateMeasureSurface();
        if (!Measure.SetAnchorSilently(endpoint, MeasureTarget).Anchored)
        {
            return StarsErrorText.Capacity;
        }

        _rangeBearingEntryAnchorId = Measure.PendingAnchorId;
        return null;
    }

    // The entry after a *T anchor: a beacon code or a fix/ACID completes the line; anything else is FORMAT, and it and NO FLIGHT,
    // DUP TRK or CAPACITY keep the anchor pending.
    private string? CompleteRangeBearingEntry(string entry)
    {
        StarsRblEntry rbl = StarsEntryParser.ReadRangeBearingCompletion(entry);
        if (rbl.Action == StarsRblAction.Format)
        {
            return StarsErrorText.Format;
        }

        (RblEndpoint? resolved, string? error) = Main.ResolveRangeBearingTarget(rbl);
        if (resolved is not { } endpoint)
        {
            return error;
        }

        if (Measure.CompleteSilently(endpoint, MeasureTarget).Slot is null)
        {
            return StarsErrorText.Capacity;
        }

        _rangeBearingEntryAnchorId = null;
        return null;
    }

    /// <summary>
    /// Esc's measure step: cancels the half-placed line this Scope anchored, whichever surface is active, or the armed tool
    /// with no anchor while this Scope is the active surface. An anchor another Scope window or the Ground view
    /// holds stays, as does a tool armed on another surface, and the key passes on to the next Esc step. The status line
    /// says so; the Scope's response is left as it is, so each Esc peels one layer.
    /// </summary>
    /// <returns>False when there was nothing of the Scope's to cancel.</returns>
    public bool CancelMeasure()
    {
        RblPendingAnchor? anchor = Measure.Anchor;
        bool ownsAnchor = (anchor is { } pending) && pending.BelongsTo(MeasureView, this);
        bool armedHere = (anchor is null) && Measure.IsMeasuring && IsActiveMeasureSurface;
        if (!ownsAnchor && !armedHere)
        {
            return false;
        }

        ActivateMeasureSurface();
        Measure.Cancel();
        return true;
    }
}
