using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Ground side of the distance measuring tool. Slots live in the store shared with the radar view, but
/// ground measurements only render here, labelled in feet at taxiway scale.
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

    /// <summary>Injects the measuring-tool state whose slot pool is shared with the radar view.</summary>
    public void SetMeasureState(RangeBearingViewState state)
    {
        Measure = state;
    }

    /// <summary>Resolves a latched measurement endpoint against the ground view's aircraft list.</summary>
    public RblTrackLookup MeasureTrackLookup => RangeBearingViewState.TrackLookup(cs => _findAircraft?.Invoke(cs));

    /// <summary>Arms the tool from the toolbar button, hotkey, or context menu.</summary>
    [RelayCommand]
    private void StartMeasure()
    {
        Measure?.Arm();
    }

    /// <summary>Clears every measurement.</summary>
    [RelayCommand]
    private void ClearMeasurements()
    {
        Measure?.Clear();
    }
}
