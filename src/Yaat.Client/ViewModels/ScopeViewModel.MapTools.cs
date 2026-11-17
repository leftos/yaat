using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's map tools: the MVA tile, DISPLAY's LOCK of wheel zoom and right-drag pan, and OFF CNTR's return to the
/// facility's home centre, lit while the scope is off it.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>How far the scope's centre may sit from the facility's home centre before OFF CNTR lights, in NM.</summary>
    internal const double OffCenterThresholdNm = 0.5;

    /// <summary>
    /// Whether wheel zoom and right-drag pan are off; the RANGE spinner and <c>.scope range</c> still change the range. Kept
    /// in a pref set.
    /// </summary>
    [ObservableProperty]
    private bool _isPanZoomLocked;

    // The value of IsOffCenter last announced; IsOffCenter itself is derived.
    private bool _offCenterShown;

    /// <summary>
    /// Whether the scope's centre is more than <see cref="OffCenterThresholdNm"/> from the facility's home centre (the map
    /// source's centre); false while the source has no centre.
    /// </summary>
    public bool IsOffCenter =>
        HasHomeCenter(Main.ScopeMaps)
        && (GeoMath.DistanceNm(CenterLat, CenterLon, Main.ScopeMaps.CenterLat, Main.ScopeMaps.CenterLon) > OffCenterThresholdNm);

    /// <summary>
    /// Pans the scope back to the facility's home centre, keeping the range, as OFF CNTR does; at the default range, a later
    /// recentre of the source still moves it. Returns false, changing nothing, while the source has no centre.
    /// </summary>
    public bool Recenter()
    {
        ScopeMapSource source = Main.ScopeMaps;
        if (!HasHomeCenter(source))
        {
            return false;
        }

        CenterLat = source.CenterLat;
        CenterLon = source.CenterLon;
        _seededCenter = (CenterLat, CenterLon);
        return true;
    }

    private static bool HasHomeCenter(ScopeMapSource source) => (source.CenterLat != 0) || (source.CenterLon != 0);

    /// <summary>
    /// Announces <see cref="IsOffCenter"/> when it differs from the value last announced, so a pan inside the half-mile band
    /// redraws no tile, and a move of the source's centre while the scope stays put still lights OFF CNTR.
    /// </summary>
    private void SyncOffCenter()
    {
        bool offCenter = IsOffCenter;
        if (offCenter == _offCenterShown)
        {
            return;
        }

        _offCenterShown = offCenter;
        OnPropertyChanged(nameof(IsOffCenter));
    }

    partial void OnCenterLatChanged(double value) => SyncOffCenter();

    partial void OnCenterLonChanged(double value) => SyncOffCenter();

    /// <summary>Whether the map-tool toggle with <paramref name="id"/> is on: MVA while the tint is on, LOCK while locked.</summary>
    private bool IsMapToolToggleOn(ScopeButtonId id) => (id == ScopeButtonId.Mva) ? ShowMvaHints : IsPanZoomLocked;

    /// <summary>The map tools' settings a pref set keeps.</summary>
    private ScopePrefMapTools CaptureMapTools() => new(ShowMvaHints, IsPanZoomLocked);

    /// <summary>Applies the map-tool settings a seed leaves alone; a set from an older build unlocks pan and zoom.</summary>
    private void ApplyMapTools(ScopePrefMapTools? tools) => IsPanZoomLocked = tools?.Lock ?? false;

    /// <summary>
    /// Applies the map-tool settings a seed overwrites (the MVA tint), with the set's range and centre; a set from an older
    /// build keeps the facility's default.
    /// </summary>
    private void ApplySeededMapTools(ScopePrefMapTools? tools)
    {
        if (tools is not null)
        {
            ShowMvaHints = tools.Mva;
        }
    }
}
