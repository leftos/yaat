using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Ground side of the distance measuring tool. Slots live in the store shared with the Scope windows, but
/// ground measurements only render here, labelled in feet at taxiway scale. A measure anchor belongs to the surface that
/// set it: the Ground view offers, completes, draws and cancels only an anchor picked on Ground.
/// </summary>
public partial class GroundViewModel
{
    /// <summary>Shared measuring-tool state, or null before <see cref="SetMeasureState" /> runs.</summary>
    [ObservableProperty]
    private RangeBearingViewState? _measure;

    /// <summary>
    /// The ground view reads distances in feet below a mile: a 1,200 ft taxiway gap is unreadable as
    /// 0.20 NM.
    /// </summary>
    public const RblUnits MeasureUnits = RblUnits.FeetThenNauticalMiles;

    /// <summary>Measurements taken here belong to the ground view and only render there.</summary>
    public const RblView MeasureView = RblView.Ground;

    /// <summary>Injects the measuring-tool state whose slot pool is shared with the Scope windows.</summary>
    public void SetMeasureState(RangeBearingViewState state) => Measure = state;

    /// <summary>Whether a half-placed measurement picked on Ground waits for its second point.</summary>
    public bool HasGroundMeasureAnchor => Measure?.HasAnchorFor(MeasureView, null) ?? false;

    /// <summary>
    /// Esc's and the right press's measure step on Ground: cancels the anchor the Ground view picked, or the armed tool with
    /// no anchor. An anchor a Scope window holds stays, and the gesture is not the tool's.
    /// </summary>
    /// <returns>False when there was nothing of the Ground view's to cancel.</returns>
    public bool CancelMeasure()
    {
        if ((Measure is not { IsMeasuring: true } measure) || ((measure.Anchor is not null) && !HasGroundMeasureAnchor))
        {
            return false;
        }

        measure.Cancel();
        return true;
    }

    /// <summary>Resolves a latched measurement endpoint against the ground view's aircraft list.</summary>
    public RblTrackLookup MeasureTrackLookup => RangeBearingViewState.TrackLookup(cs => _findAircraft?.Invoke(cs));

    /// <summary>Arms the tool from the toolbar button, hotkey, or context menu.</summary>
    [RelayCommand]
    private void StartMeasure() => Measure?.Arm();

    /// <summary>Clears every measurement.</summary>
    [RelayCommand]
    private void ClearMeasurements() => Measure?.Clear();
}
