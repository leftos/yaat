using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's map tools: the MVA tile, DISPLAY's WX weather overlay, its LOCK of wheel zoom and right-drag pan and OFF CNTR's
/// return to the facility's home centre, lit while the scope is off it, its DCNF datablock deconfliction, and the RR range
/// ring.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>How far the scope's centre may sit from the facility's home centre before OFF CNTR lights, in NM.</summary>
    internal const double OffCenterThresholdNm = 0.5;

    /// <summary>The range ring's spacing a scope opens with, in NM.</summary>
    public const int DefaultRangeRingSizeNm = 5;

    private static readonly int[] RangeRingSizeGrid = [2, 5, 10, 15, 20];

    /// <summary>
    /// Whether wheel zoom and right-drag pan are off; the RANGE spinner and <c>.scope range</c> still change the range. Kept
    /// in a pref set.
    /// </summary>
    [ObservableProperty]
    private bool _isPanZoomLocked;

    // The value of IsOffCenter last announced; IsOffCenter itself is derived.
    private bool _offCenterShown;

    // The home centre last announced through RangeRingCenterShown.
    private LatLon? _homeShown;

    /// <summary>Whether the range ring is drawn. On by default; kept in a pref set.</summary>
    [ObservableProperty]
    private bool _showRangeRing = true;

    /// <summary>The spacing of the range ring's circles, in NM, one of <see cref="RangeRingSizes"/>. Kept in a pref set.</summary>
    [ObservableProperty]
    private int _rangeRingSizeNm = DefaultRangeRingSizeNm;

    /// <summary>
    /// The centre PLACE RR placed, or null for the facility's home centre (RR CNTR, and every map-source seed). Kept in a pref
    /// set.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeRingCenterShown))]
    private LatLon? _rangeRingCenter;

    /// <summary>Whether PLACE RR is armed, so the next left click on the map places the ring's centre. Never saved.</summary>
    [ObservableProperty]
    private bool _isPlacingRangeRing;

    /// <summary>Whether the weather overlay is drawn. On by default; kept in a pref set.</summary>
    [ObservableProperty]
    private bool _showWeather = true;

    /// <summary>
    /// How overlapping datablocks spread apart (DISPLAY's DCNF): a new scope takes the Radar's mode
    /// (<c>UserPreferences.RadarDeconflictMode</c>, Off unless the user changed it there) and never writes it back. Kept in a
    /// pref set.
    /// </summary>
    [ObservableProperty]
    private DatablockDeconflictMode _deconflictMode;

    /// <summary>The range ring's spacings SIZE steps along, in NM.</summary>
    public static IReadOnlyList<int> RangeRingSizes => RangeRingSizeGrid;

    /// <summary>
    /// The range ring's centre as drawn: the placed one, else the facility's home centre; null while neither exists.
    /// </summary>
    public LatLon? RangeRingCenterShown => RangeRingCenter ?? HomeCenter(Main.ScopeMaps);

    /// <summary>
    /// The weather overlay's lines: one per METAR of the position's airports (every station when no position is set), or the
    /// "No METAR for" note when none matches; empty while no weather is loaded.
    /// </summary>
    public IReadOnlyList<string> WeatherLines => Main.ScopeWeatherLines();

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

    /// <summary>Tells the canvas the weather or the position's airports changed, so it redraws <see cref="WeatherLines"/>.</summary>
    public void RefreshWeather() => OnPropertyChanged(nameof(WeatherLines));

    /// <summary>Arms PLACE RR, disarming the Rings tool, so the next left click on the map places the ring's centre.</summary>
    public void ArmRangeRingPlacement()
    {
        ArmedRingTool = ScopeRingTool.None;
        IsPlacingRangeRing = true;
    }

    /// <summary>Centres the range ring on <paramref name="at"/>, turning it on, and disarms PLACE RR.</summary>
    public void PlaceRangeRing(LatLon at)
    {
        RangeRingCenter = at;
        ShowRangeRing = true;
        IsPlacingRangeRing = false;
    }

    /// <summary>Returns the range ring to the facility's home centre, as RR CNTR does.</summary>
    public void CenterRangeRing() => RangeRingCenter = null;

    private static bool HasHomeCenter(ScopeMapSource source) => (source.CenterLat != 0) || (source.CenterLon != 0);

    private static LatLon? HomeCenter(ScopeMapSource source) => HasHomeCenter(source) ? new LatLon(source.CenterLat, source.CenterLon) : null;

    /// <summary>
    /// Announces <see cref="IsOffCenter"/> when it differs from the value last announced, so a pan inside the half-mile band
    /// redraws no tile, and a move of the source's centre while the scope stays put still lights OFF CNTR; announces
    /// <see cref="RangeRingCenterShown"/> when that move carries a ring on the home centre with it.
    /// </summary>
    private void SyncOffCenter()
    {
        SyncRangeRingHome();
        bool offCenter = IsOffCenter;
        if (offCenter == _offCenterShown)
        {
            return;
        }

        _offCenterShown = offCenter;
        OnPropertyChanged(nameof(IsOffCenter));
    }

    private void SyncRangeRingHome()
    {
        LatLon? home = HomeCenter(Main.ScopeMaps);
        if (home == _homeShown)
        {
            return;
        }

        _homeShown = home;
        if (RangeRingCenter is null)
        {
            OnPropertyChanged(nameof(RangeRingCenterShown));
        }
    }

    partial void OnCenterLatChanged(double value) => SyncOffCenter();

    partial void OnCenterLonChanged(double value) => SyncOffCenter();

    // Arming the Rings tool disarms PLACE RR: one left-click tool at a time.
    partial void OnArmedRingToolChanged(ScopeRingTool value)
    {
        if (value != ScopeRingTool.None)
        {
            IsPlacingRangeRing = false;
        }
    }

    // Esc cancels an armed PLACE RR, ahead of the Rings tool.
    partial void EscapeMapTools(ref bool handled)
    {
        if (handled || !IsPlacingRangeRing)
        {
            return;
        }

        IsPlacingRangeRing = false;
        handled = true;
    }

    /// <summary>Whether the map-tool toggle with <paramref name="id"/> is on: MVA, FIX, WX, LOCK or the range ring's SHOW.</summary>
    private bool IsMapToolToggleOn(ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.Mva => ShowMvaHints,
            ScopeButtonId.DisplayFix => ShowFixes,
            ScopeButtonId.DisplayWx => ShowWeather,
            ScopeButtonId.RrShow => ShowRangeRing,
            _ => IsPanZoomLocked,
        };

    /// <summary>
    /// Whether a map-tool command button draws active: PLACE RR while armed, DCNF while deconfliction is on, OFF CNTR while
    /// off the home centre.
    /// </summary>
    private bool IsMapToolActive(ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.RrPlace => IsPlacingRangeRing,
            ScopeButtonId.DisplayDcnf => DeconflictMode != DatablockDeconflictMode.Off,
            _ => IsOffCenter,
        };

    /// <summary>
    /// The value RR (its size, or OFF), SIZE and DCNF (S for Snap, F for Free-form, nothing while off) show after their
    /// labels; null for any other id.
    /// </summary>
    private string? MapToolValue(ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.Rr when !ShowRangeRing => "OFF",
            ScopeButtonId.Rr or ScopeButtonId.RrSize => RangeRingSizeNm.ToString(CultureInfo.InvariantCulture),
            ScopeButtonId.DisplayDcnf => DeconflictValue(DeconflictMode),
            _ => null,
        };

    private static string? DeconflictValue(DatablockDeconflictMode mode) =>
        mode switch
        {
            DatablockDeconflictMode.CompassSnap => "S",
            DatablockDeconflictMode.FreeForm => "F",
            _ => null,
        };

    /// <summary>
    /// Steps SIZE by <paramref name="steps"/> along <see cref="RangeRingSizes"/> through <c>.scope rr size</c>; a step past an
    /// end stops there, and a step at the end runs no command.
    /// </summary>
    private void StepRangeRingSize(int steps)
    {
        int index = Math.Max(0, Array.IndexOf(RangeRingSizeGrid, RangeRingSizeNm));
        int next = RangeRingSizeGrid[Math.Clamp(index + steps, 0, RangeRingSizeGrid.Length - 1)];
        if (next != RangeRingSizeNm)
        {
            RunCommand(ScopeToolbarButtons.RrSizeCommand(next));
        }
    }

    /// <summary>
    /// The map tools' settings a pref set keeps; the range ring, WX, the fixes and markers and the deconfliction mode are left
    /// null while at their defaults, the mode's being the one a new scope takes.
    /// </summary>
    private ScopePrefMapTools CaptureMapTools() =>
        new(ShowMvaHints, IsPanZoomLocked, CaptureRangeRing(), ShowWeather ? null : false, CaptureFixesAndMarkers())
        {
            Deconflict = (DeconflictMode == Preferences.RadarDeconflictMode) ? null : DeconflictMode,
        };

    private ScopePrefRangeRing? CaptureRangeRing() =>
        (ShowRangeRing && (RangeRingSizeNm == DefaultRangeRingSizeNm) && (RangeRingCenter is null))
            ? null
            : new ScopePrefRangeRing(ShowRangeRing, RangeRingSizeNm, RangeRingCenter?.Lat, RangeRingCenter?.Lon);

    /// <summary>
    /// Applies the map-tool settings a seed leaves alone; a set from an older build unlocks pan and zoom, turns WX on, turns
    /// FIX off with no marker, takes the range ring's defaults and the deconfliction mode a new scope takes.
    /// </summary>
    private void ApplyMapTools(ScopePrefMapTools? tools)
    {
        ApplyDeconflictMode(tools?.Deconflict);
        IsPanZoomLocked = tools?.Lock ?? false;
        ShowWeather = tools?.Weather ?? true;
        ApplyFixesAndMarkers(tools?.FixesAndMarkers);
        ScopePrefRangeRing? ring = tools?.RangeRing;
        ShowRangeRing = ring?.Show ?? true;
        ApplyRangeRingSize(ring?.SizeNm ?? DefaultRangeRingSizeNm);
    }

    /// <summary>Takes <paramref name="mode"/> as the deconfliction mode, or the mode a new scope takes for null.</summary>
    private void ApplyDeconflictMode(DatablockDeconflictMode? mode) => DeconflictMode = mode ?? Preferences.RadarDeconflictMode;

    /// <summary>Takes <paramref name="sizeNm"/> as the ring's spacing; a size off the grid keeps the current one, with a warning.</summary>
    private void ApplyRangeRingSize(int sizeNm)
    {
        if (Array.IndexOf(RangeRingSizeGrid, sizeNm) < 0)
        {
            Log.LogWarning("A pref set names range ring size {SizeNm}; keeping {Current}", sizeNm, RangeRingSizeNm);
            return;
        }

        RangeRingSizeNm = sizeNm;
    }

    /// <summary>
    /// Applies the map-tool settings a seed overwrites (the MVA tint and the range ring's centre), with the set's range and
    /// centre; a set from an older build keeps the facility's MVA default and centres the ring on the home centre.
    /// </summary>
    private void ApplySeededMapTools(ScopePrefMapTools? tools)
    {
        RangeRingCenter = (tools?.RangeRing is { CenterLat: { } lat, CenterLon: { } lon }) ? new LatLon(lat, lon) : null;
        if (tools is not null)
        {
            ShowMvaHints = tools.Mva;
        }
    }
}
