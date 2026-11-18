using System.ComponentModel;
using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's side of the distance measuring tool. The lines live in the shared <see cref="RangeBearingViewState"/>
/// (<see cref="MainViewModel.Measure"/>): session state in the one 15-slot pool, never saved, kept while the primary Scope's pop-out is closed
/// (an extra Scope window drops its own when it closes) and not cleared with the Scope's per-track state. A line or anchor taken here belongs to this Scope window and renders only here. Feedback
/// for an action the Scope itself starts (its Ctrl+M) also shows in the Scope's response; the status line shows every
/// action's feedback, whichever surface started it.
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

        if (status is not null)
        {
            ShowMeasureResponse(status);
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
        ShowMeasureResponse(status);
    }

    /// <summary>
    /// A right press on the Scope while the tool is armed: makes the Scope the active surface and cancels the tool, as Esc
    /// does; the status line says so and the Scope's response keeps what it shows, less the tool's own prompt.
    /// </summary>
    public void CancelMeasureFromPointer()
    {
        ActivateMeasureSurface();
        CancelMeasure();
    }

    // A Scope-started measure status in the response: an error for a full store, text otherwise. While the tool stays armed it
    // is the tool's own prompt, which Esc takes with the measurement and a disarm elsewhere takes down.
    private void ShowMeasureResponse(string status)
    {
        var response = new ScopeEntryResponse(
            status == RangeBearingViewState.FullStatus ? ScopeResponseKind.Error : ScopeResponseKind.Text,
            status,
            "",
            []
        );
        _measureResponse = Measure.IsMeasuring ? response : null;
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

    /// <summary>
    /// Esc's measure step: cancels the armed tool or a half-placed line when the Scope is the active surface or picked the
    /// anchor, so an Esc here never cancels a measurement the instructor left armed on another surface. The status line says
    /// so; the Scope's response is left as it is, so each Esc peels one layer.
    /// </summary>
    /// <returns>False when there was nothing of the Scope's to cancel.</returns>
    public bool CancelMeasure()
    {
        bool ownsAnchor = (Measure.Anchor is { View: MeasureView } anchor) && ReferenceEquals(anchor.Surface, this);
        if ((!Measure.IsMeasuring && (Measure.Anchor is null)) || (!IsActiveMeasureSurface && !ownsAnchor))
        {
            return false;
        }

        ActivateMeasureSurface();
        Measure.Cancel();
        return true;
    }
}
