using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Which map surface the distance measuring tool's typed <c>.rbl A B</c> places on: the last surface (a Radar, Ground or
/// Scope canvas) that took the focus or a press, the Radar before any, and the Radar again once the Scope that took it closes.
/// Every Scope window shows the Scope's lines; the surface is held as the Scope that took it, whichever has the focus.
/// </summary>
public partial class MainViewModel
{
    private RblView _activeMeasureView = RblView.Radar;

    /// <summary>The Scope that last took the measuring surface, so a closed or reopened Scope does not keep it.</summary>
    private ScopeViewModel? _activeMeasureScope;

    /// <summary>
    /// The surface a typed <c>.rbl A B</c> places on: the last one that took the focus or a press; the Radar before any, and
    /// the Radar when the Scope that took it is no longer one of <see cref="AllScopeViews"/>.
    /// </summary>
    public RblView ActiveMeasureView => ((_activeMeasureView == RblView.Scope) && (ActiveMeasureScope is null)) ? RblView.Radar : _activeMeasureView;

    /// <summary>The Scope that holds the measuring surface, while it is still open; null when the surface is not a Scope's.</summary>
    public ScopeViewModel? ActiveMeasureScope =>
        ((_activeMeasureView == RblView.Scope) && (_activeMeasureScope is { } scope) && AllScopeViews.Contains(scope)) ? scope : null;

    /// <summary>The Scope recorded as having taken the measuring surface, open or not. Test seam.</summary>
    internal ScopeViewModel? HeldMeasureScope => _activeMeasureScope;

    /// <summary>Records that the Radar or Ground <paramref name="view"/> surface took the focus or a press.</summary>
    /// <exception cref="ArgumentException"><paramref name="view"/> is the Scope's, which <see cref="SetActiveMeasureScope"/> sets.</exception>
    public void SetActiveMeasureView(RblView view)
    {
        if (view == RblView.Scope)
        {
            throw new ArgumentException("Use SetActiveMeasureScope for a Scope surface", nameof(view));
        }

        _activeMeasureView = view;
        _activeMeasureScope = null;
    }

    /// <summary>Records that <paramref name="scope"/>'s canvas took the focus or a press, making it the measuring surface.</summary>
    public void SetActiveMeasureScope(ScopeViewModel scope)
    {
        _activeMeasureView = ScopeViewModel.MeasureView;
        _activeMeasureScope = scope;
    }

    /// <summary>The view tag, track lookup and units a measurement placed on the <paramref name="view"/> surface takes.</summary>
    internal (RblView View, RblTrackLookup Lookup, RblUnits Units) MeasureSurface(RblView view) =>
        view switch
        {
            RblView.Ground => (GroundViewModel.MeasureView, Ground.MeasureTrackLookup, GroundViewModel.MeasureUnits),
            RblView.Scope => (ScopeViewModel.MeasureView, (ActiveMeasureScope ?? OpenScope).MeasureTrackLookup, ScopeViewModel.MeasureUnits),
            // The primary radar's track lookup is deliberate: Measure is one shared store, so the line renders in every Radar
            // View window without the extra instances needing their own lookup.
            _ => (RadarViewModel.MeasureView, Radar.MeasureTrackLookup, RadarViewModel.MeasureUnits),
        };
}
