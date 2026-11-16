using System.ComponentModel;
using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's side of the distance measuring tool. The lines live in the shared <see cref="RangeBearingViewState"/>
/// (<see cref="MainViewModel.Measure"/>): session state in the one 15-slot pool, never saved, kept while the Scope is closed and
/// not cleared with the Scope's per-track state. A line taken here belongs to the Scope view and renders only here. Feedback
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

    /// <summary>The response <see cref="ArmMeasure"/> last wrote, so Esc can tell the tool's own prompt from another response.</summary>
    private ScopeEntryResponse? _measureResponse;

    /// <summary>Whether this Scope is the surface the measuring tool acts on now.</summary>
    public bool IsActiveMeasureSurface => Main.ActiveMeasureView == MeasureView;

    /// <summary>Makes this Scope the active measure surface, as the canvas taking the focus does.</summary>
    public void ActivateMeasureSurface() => Main.SetActiveMeasureView(MeasureView);

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
        _measureResponse = new ScopeEntryResponse(armed ? ScopeResponseKind.Text : ScopeResponseKind.Error, status, "");
        EntryResponse = _measureResponse;
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
        bool ownsAnchor = Measure.Anchor is { View: MeasureView };
        if ((!Measure.IsMeasuring && (Measure.Anchor is null)) || (!IsActiveMeasureSurface && !ownsAnchor))
        {
            return false;
        }

        ActivateMeasureSurface();
        Measure.Cancel();
        return true;
    }
}
