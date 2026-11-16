using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Which map surface the distance measuring tool's typed <c>.rbl A B</c> places on: the last surface (a Radar, Ground or
/// Scope canvas) that took the focus or a press, the Radar before any, and the Radar again once the Scope that took it closes.
/// </summary>
public partial class MainViewModel
{
    private RblView _activeMeasureView = RblView.Radar;

    /// <summary>The Scope that last took the measuring surface, so a closed or reopened Scope does not keep it.</summary>
    private ScopeViewModel? _activeMeasureScope;

    /// <summary>
    /// The surface a typed <c>.rbl A B</c> places on: the last one that took the focus or a press; the Radar before any, and
    /// the Radar when the Scope that took it is no longer the open one.
    /// </summary>
    public RblView ActiveMeasureView =>
        (_activeMeasureView == RblView.Scope) && ((OpenScope is null) || !ReferenceEquals(OpenScope, _activeMeasureScope))
            ? RblView.Radar
            : _activeMeasureView;

    /// <summary>Records that the <paramref name="view"/> surface took the focus or a press; the Scope's is the open Scope.</summary>
    public void SetActiveMeasureView(RblView view)
    {
        _activeMeasureView = view;
        _activeMeasureScope = (view == RblView.Scope) ? OpenScope : null;
    }

    /// <summary>The view tag, track lookup and units a measurement placed on the <paramref name="view"/> surface takes.</summary>
    internal (RblView View, RblTrackLookup Lookup, RblUnits Units) MeasureSurface(RblView view) =>
        view switch
        {
            RblView.Ground => (GroundViewModel.MeasureView, Ground.MeasureTrackLookup, GroundViewModel.MeasureUnits),
            RblView.Scope => (
                ScopeViewModel.MeasureView,
                OpenScope?.MeasureTrackLookup ?? RangeBearingViewState.TrackLookup(FindAircraft),
                ScopeViewModel.MeasureUnits
            ),
            // The primary radar's track lookup is deliberate: Measure is one shared store, so the line renders in every Radar
            // View window without the extra instances needing their own lookup.
            _ => (RadarViewModel.MeasureView, Radar.MeasureTrackLookup, RadarViewModel.MeasureUnits),
        };
}
