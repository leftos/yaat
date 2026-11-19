using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Which map surface the distance measuring tool's typed <c>.rbl A B</c> places on: the last surface (a Ground or
/// Scope canvas) that took the focus or a press, the primary Scope before any, and the primary Scope again once the Scope that
/// took it closes.
/// A Scope line draws only on the Scope window that placed it, a typed one on the Scope holding the surface; the surface is
/// held as the Scope that took it, whichever has the focus.
/// </summary>
public partial class MainViewModel
{
    private RblView _activeMeasureView = ScopeViewModel.MeasureView;

    /// <summary>The Scope that last took the measuring surface, so a closed or reopened Scope does not keep it.</summary>
    private ScopeViewModel? _activeMeasureScope;

    /// <summary>
    /// The surface a typed <c>.rbl A B</c> places on: the last one that took the focus or a press; the Scope before any, which
    /// is then <see cref="ActiveMeasureScope"/>'s.
    /// </summary>
    public RblView ActiveMeasureView => _activeMeasureView;

    /// <summary>
    /// The Scope that holds the measuring surface: the one that took it while it is still one of <see cref="AllScopeViews"/>,
    /// else the primary. Null when the surface is not a Scope's, and while the constructor builds the primary.
    /// </summary>
    public ScopeViewModel? ActiveMeasureScope
    {
        get
        {
            if (_activeMeasureView != RblView.Scope)
            {
                return null;
            }

            return ((_activeMeasureScope is { } scope) && AllScopeViews.Contains(scope)) ? scope : PrimaryScope;
        }
    }

    /// <summary>The Scope recorded as having taken the measuring surface, open or not. Test seam.</summary>
    internal ScopeViewModel? HeldMeasureScope => _activeMeasureScope;

    /// <summary>Records that the Ground <paramref name="view"/> surface took the focus or a press.</summary>
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

    /// <summary>
    /// The view tag, track lookup, units and owning surface a measurement placed on the <paramref name="view"/> surface takes:
    /// for the Scope, <paramref name="scope"/>, else <see cref="ActiveMeasureScope"/>, else the primary owns it; no other view
    /// has an owning surface.
    /// </summary>
    internal MeasureTarget MeasureSurface(RblView view, ScopeViewModel? scope) =>
        view switch
        {
            RblView.Ground => new MeasureTarget(GroundViewModel.MeasureView, Ground.MeasureTrackLookup, GroundViewModel.MeasureUnits, null),
            _ => (scope ?? ActiveMeasureScope ?? PrimaryScope).MeasureTarget,
        };
}
