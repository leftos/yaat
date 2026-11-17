using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's pinned markers (<c>.ff</c>, <c>.marker</c>, <c>.markers</c>, <c>.nomarkers</c> and the map point menu's rows)
/// and DISPLAY's FIX overlay of the chart's fixes. A marker is kept as its fix name or FRD and placed once the navigation data
/// has loaded; neither a track reset nor a map-source seed clears the markers. Both are kept in a pref set.
/// </summary>
public sealed partial class ScopeViewModel
{
    // The smallest radius around a click inside which the point menu offers to remove a marker, in NM.
    private const double MinMarkerPickNm = 0.5;

    // The share of the range the marker pick radius grows to at a long range.
    private const double MarkerPickRangeShare = 0.04;

    private readonly List<string> _markerTokens = [];

    /// <summary>Whether the FIX overlay is drawn. Off by default; kept in a pref set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownFixes))]
    private bool _showFixes;

    /// <summary>
    /// The pinned markers placed on the map, in the order they were pinned; empty before the navigation data loads, and a
    /// token it cannot place is left out.
    /// </summary>
    public IReadOnlyList<ScopeMarker> Markers
    {
        get => _markers;
        private set => SetProperty(ref _markers, value);
    }

    private IReadOnlyList<ScopeMarker> _markers = [];

    /// <summary>The pinned markers' fix names or FRDs, upper-cased, in the order they were pinned.</summary>
    public IReadOnlyList<string> MarkerTokens => _markerTokens;

    /// <summary>Whether any marker is pinned.</summary>
    public bool HasMarkers => _markerTokens.Count > 0;

    /// <summary>The fixes the FIX overlay draws: the chart's fixes while it is on and the navigation data has loaded; else null.</summary>
    public IReadOnlyList<(string Name, double Lat, double Lon)>? ShownFixes => ShowFixes ? Main.ScopeMaps.Fixes : null;

    /// <summary>How near a click a marker must lie for the point menu to offer to remove it: 4% of the range, at least 0.5 NM.</summary>
    public double MarkerPickRadiusNm => Math.Max(MinMarkerPickNm, RangeNm * MarkerPickRangeShare);

    /// <summary>
    /// Pins the fix or FRD <paramref name="token"/>, or removes it when already pinned, as <c>.ff</c> does. Returns false,
    /// changing nothing, when the token is empty or names no point the navigation data knows (or it has not loaded).
    /// </summary>
    public bool ToggleMarker(string token)
    {
        string normalized = NormalizeMarker(token);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (!_markerTokens.Remove(normalized))
        {
            if ((Main.ScopeMaps.Fixes is null) || (FrdResolver.Resolve(normalized, NavigationDatabase.Instance) is null))
            {
                return false;
            }

            _markerTokens.Add(normalized);
        }

        RebuildMarkers();
        return true;
    }

    /// <summary>Pins <paramref name="frd"/>, as the point menu's Pin marker here does; a token already pinned stays pinned once.</summary>
    public void AddMarker(string frd)
    {
        string normalized = NormalizeMarker(frd);
        if ((normalized.Length == 0) || _markerTokens.Contains(normalized))
        {
            return;
        }

        _markerTokens.Add(normalized);
        RebuildMarkers();
    }

    /// <summary>
    /// Removes the marker nearest <paramref name="at"/> within <paramref name="maxNm"/>, as the point menu's Remove marker
    /// does. Returns false, changing nothing, when no marker lies that near.
    /// </summary>
    public bool RemoveNearestMarker(LatLon at, double maxNm)
    {
        if (NearestMarker(at, maxNm) is not { } nearest)
        {
            return false;
        }

        _markerTokens.Remove(nearest.Name);
        RebuildMarkers();
        return true;
    }

    /// <summary>Whether a marker lies within <paramref name="maxNm"/> of <paramref name="at"/>.</summary>
    public bool HasMarkerNear(LatLon at, double maxNm) => NearestMarker(at, maxNm) is not null;

    /// <summary>Removes every marker, as <c>.nomarkers</c> and the point menu's Clear pinned markers do.</summary>
    public void ClearMarkers()
    {
        if (_markerTokens.Count == 0)
        {
            return;
        }

        _markerTokens.Clear();
        RebuildMarkers();
    }

    private static string NormalizeMarker(string token) => token.Trim().ToUpperInvariant();

    private ScopeMarker? NearestMarker(LatLon at, double maxNm)
    {
        ScopeMarker? nearest = null;
        double best = maxNm;
        foreach (ScopeMarker marker in Markers)
        {
            double nm = GeoMath.DistanceNm(at, marker.Position);
            if (nm <= best)
            {
                best = nm;
                nearest = marker;
            }
        }

        return nearest;
    }

    /// <summary>Places the pinned tokens again: none before the navigation data loads, and none a token cannot name.</summary>
    private void RebuildMarkers()
    {
        OnPropertyChanged(nameof(MarkerTokens));
        OnPropertyChanged(nameof(HasMarkers));
        Markers = (Main.ScopeMaps.Fixes is null) ? [] : [.. PlaceMarkers()];
    }

    private IEnumerable<ScopeMarker> PlaceMarkers()
    {
        foreach (string token in _markerTokens)
        {
            if (FrdResolver.Resolve(token, NavigationDatabase.Instance) is { } position)
            {
                yield return new ScopeMarker(token, position);
            }
        }
    }

    /// <summary>The map source's fixes arrived or changed: the markers are placed again and the FIX overlay redraws.</summary>
    private void OnMapSourceFixesChanged()
    {
        RebuildMarkers();
        OnPropertyChanged(nameof(ShownFixes));
    }

    /// <summary>The FIX overlay and the markers as a pref set keeps them; null while FIX is off and no marker is pinned.</summary>
    private ScopePrefFixesAndMarkers? CaptureFixesAndMarkers() =>
        (!ShowFixes && (_markerTokens.Count == 0)) ? null : new ScopePrefFixesAndMarkers(ShowFixes, [.. _markerTokens]);

    /// <summary>Applies a pref set's FIX overlay and markers; a set from an older build turns FIX off and pins no marker.</summary>
    private void ApplyFixesAndMarkers(ScopePrefFixesAndMarkers? saved)
    {
        ShowFixes = saved?.ShowFixes ?? false;
        _markerTokens.Clear();
        foreach (string? token in saved?.Markers ?? [])
        {
            if (token is null)
            {
                Log.LogWarning("A pref set's marker list holds a null entry; skipped");
                continue;
            }

            string normalized = NormalizeMarker(token);
            if ((normalized.Length > 0) && !_markerTokens.Contains(normalized))
            {
                _markerTokens.Add(normalized);
            }
        }

        RebuildMarkers();
    }
}
