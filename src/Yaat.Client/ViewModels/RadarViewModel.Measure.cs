using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Radar side of the distance measuring tool. The measurements themselves live in the shared
/// <see cref="RangeBearingViewState" />; this partial only exposes them to the radar view and reports
/// radar's unit convention (nautical miles).
/// </summary>
public partial class RadarViewModel
{
    /// <summary>Shared measuring-tool state, or null before <see cref="SetMeasureState" /> runs.</summary>
    [ObservableProperty]
    private RangeBearingViewState? _measure;

    /// <summary>The radar reads distances in nautical miles, matching STARS.</summary>
    public const RblUnits MeasureUnits = RblUnits.NauticalMiles;

    /// <summary>Measurements taken here belong to the radar view and only render there.</summary>
    public const RblView MeasureView = RblView.Radar;

    /// <summary>Injects the measuring-tool state whose slot pool is shared with the ground view.</summary>
    public void SetMeasureState(RangeBearingViewState state)
    {
        Measure = state;
    }

    /// <summary>Resolves a latched measurement endpoint against the radar's aircraft list.</summary>
    public RblTrackLookup MeasureTrackLookup => RangeBearingViewState.TrackLookup(cs => _findAircraft?.Invoke(cs));

    /// <summary>Arms the tool from the DCB button, hotkey, or context menu.</summary>
    [RelayCommand]
    private void StartMeasure()
    {
        Measure?.Arm();
    }

    /// <summary>Clears every measurement (DCB long-press equivalent / context menu).</summary>
    [RelayCommand]
    private void ClearMeasurements()
    {
        Measure?.Clear();
    }
}
