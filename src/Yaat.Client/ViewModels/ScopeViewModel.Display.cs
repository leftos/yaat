using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's display state: its BRITE groups, its video-map toggles, its DB settings and top-down mode. Changed only by <c>.scope brite</c>,
/// <c>.scope map</c>, <c>.scope db</c> and <c>.scope tdm</c> (the toolbar and Ctrl+Shift+T run them) and by a pref-set load; never written to the
/// preferences, never sent to the server.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>Set while <see cref="ClearMaps"/> turns every toggle off, so the maps are recomputed once.</summary>
    private bool _isClearingMaps;

    /// <summary>The scope's BRITE groups, opening at <see cref="ScopeBrightness.Default"/>.</summary>
    [ObservableProperty]
    private ScopeBrightness _brightness = ScopeBrightness.Default;

    /// <summary>The scope's DB settings, seeded once from the preferences when the scope opens.</summary>
    [ObservableProperty]
    private ScopeDbSettings _dbSettings;

    /// <summary>
    /// Whether top-down mode is on: the scope then draws aircraft on the ground (with the ground-target block on the STARS
    /// and ERAM templates), airborne ones below the display floor (with the ordinary block) and the TDM-only maps that are
    /// on. A display setting, kept in pref sets and the saved state.
    /// </summary>
    [ObservableProperty]
    private bool _isTdm;

    /// <summary>
    /// The scope's own video-map toggles, in STARS id order, rebuilt from the map source's maps at every seed. A change
    /// to the list or to a toggle's state raises <see cref="ObservableObject.PropertyChanged"/> for this property.
    /// </summary>
    public ObservableCollection<ScopeMapToggle> MapToggles { get; } = [];

    /// <summary>
    /// The map ids of <see cref="MapToggles"/> that are TDM-only, derived when the toggles are seeded; the canvas leaves those
    /// maps out of its frame while top-down mode is off.
    /// </summary>
    [ObservableProperty]
    private IReadOnlySet<string> _tdmOnlyMapIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="item"/> names a DB on/off item (<c>4thline</c>, <c>dwell</c>, <c>atpa</c>, <c>alertcones</c>,
    /// <c>monitorcones</c>, <c>caring</c>; lowercase).
    /// </summary>
    public static bool IsDbToggleItem(string item) =>
        item
            is ScopeToolbarButtons.DbLine4Item
                or ScopeToolbarButtons.DbDwellItem
                or ScopeToolbarButtons.DbAtpaItem
                or ScopeToolbarButtons.DbAtpaAlertConesItem
                or ScopeToolbarButtons.DbAtpaMonitorConesItem
                or ScopeToolbarButtons.DbCaRingItem;

    /// <summary>
    /// Sets the BRITE group named <paramref name="group"/> (case-insensitive) to <paramref name="value"/>, as
    /// <c>.scope brite</c> does once it has checked both.
    /// </summary>
    /// <exception cref="ArgumentException">The group is unknown, or the value is outside its range.</exception>
    internal void SetBrightness(string group, int value)
    {
        ScopeBrightnessGroup found = ScopeBrightness.FindGroup(group) ?? throw new ArgumentException($"Unknown BRITE group: {group}", nameof(group));
        if (!found.Contains(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Outside {found.Name}'s {found.Min} to {found.Max}");
        }

        Brightness = found.With(Brightness, value);
    }

    /// <summary>Turns the map with STARS id <paramref name="starsId"/> on or off, as <c>.scope map</c> does.</summary>
    /// <returns>False when the scope has no toggle for that id.</returns>
    internal bool SetMapOn(int starsId, bool on)
    {
        if (MapToggles.FirstOrDefault(t => t.StarsId == starsId) is not { } toggle)
        {
            return false;
        }

        toggle.IsOn = on;
        return true;
    }

    /// <summary>Turns every map toggle off, as <c>.scope map clear</c> (the MAPS tray's CLR ALL) does.</summary>
    internal void ClearMaps()
    {
        _isClearingMaps = true;
        try
        {
            foreach (ScopeMapToggle toggle in MapToggles)
            {
                toggle.IsOn = false;
            }
        }
        finally
        {
            _isClearingMaps = false;
        }

        OnMapsChanged();
    }

    /// <summary>Sets the DB on/off item named <paramref name="item"/> (<see cref="IsDbToggleItem"/>), as <c>.scope db</c> does.</summary>
    /// <exception cref="ArgumentException">The item is no DB on/off item.</exception>
    internal void SetDbToggle(string item, bool on) =>
        DbSettings = item switch
        {
            ScopeToolbarButtons.DbLine4Item => DbSettings with { Line4 = on },
            ScopeToolbarButtons.DbDwellItem => DbSettings with { HoverDwell = on },
            ScopeToolbarButtons.DbAtpaItem => DbSettings with { Atpa = on },
            ScopeToolbarButtons.DbAtpaAlertConesItem => DbSettings with { AtpaAlertCones = on },
            ScopeToolbarButtons.DbAtpaMonitorConesItem => DbSettings with { AtpaMonitorCones = on },
            ScopeToolbarButtons.DbCaRingItem => DbSettings with { EramCaRing = on },
            _ => throw new ArgumentException($"Unknown scope DB item: {item}", nameof(item)),
        };

    /// <summary>Sets the datablock point size, as <c>.scope db char</c> does.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size is outside <see cref="ScopeDbSettings.MinCharSize"/> to <see cref="ScopeDbSettings.MaxCharSize"/>.
    /// </exception>
    internal void SetDbCharSize(int size) =>
        DbSettings = ScopeDbSettings.IsCharSize(size)
            ? DbSettings with
            {
                CharSize = size,
            }
            : throw new ArgumentOutOfRangeException(nameof(size), size, "Outside the CHAR size range");

    /// <summary>
    /// Rebuilds <see cref="MapToggles"/> from <paramref name="maps"/> in STARS id order, each on only when a toggle with its
    /// map id was on before, and redraws.
    /// </summary>
    private void SeedMaps(IReadOnlyList<ScopeMapInfo> maps)
    {
        var wasOn = MapToggles.Where(t => t.IsOn).Select(t => t.MapId).ToHashSet(StringComparer.Ordinal);
        RemoveMapToggles();
        foreach (ScopeMapInfo map in maps.OrderBy(m => m.StarsId))
        {
            var toggle = new ScopeMapToggle
            {
                MapId = map.MapId,
                StarsId = map.StarsId,
                ShortName = map.ShortName,
                Name = map.Name,
                BrightnessCategory = map.BrightnessCategory,
                TdmOnly = map.TdmOnly,
                IsOn = wasOn.Contains(map.MapId),
            };
            toggle.PropertyChanged += OnMapTogglePropertyChanged;
            MapToggles.Add(toggle);
        }

        TdmOnlyMapIds = MapToggles.Where(t => t.TdmOnly).Select(t => t.MapId).ToHashSet(StringComparer.Ordinal);
        OnMapsChanged();
    }

    /// <summary>Drops every map toggle and the maps they draw, as a cleared map source leaves the scope.</summary>
    private void ClearSeededMaps()
    {
        RemoveMapToggles();
        TdmOnlyMapIds = new HashSet<string>(StringComparer.Ordinal);
        VideoMaps = [];
        OnPropertyChanged(nameof(MapToggles));
    }

    private void RemoveMapToggles()
    {
        foreach (ScopeMapToggle toggle in MapToggles)
        {
            toggle.PropertyChanged -= OnMapTogglePropertyChanged;
        }

        MapToggles.Clear();
    }

    private void OnMapTogglePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(ScopeMapToggle.IsOn)) && !_isClearingMaps)
        {
            OnMapsChanged();
        }
    }

    /// <summary>
    /// Redraws the on toggles' maps in toggle order, each from the video-map cache; a map the cache lacks is skipped. Then
    /// tells the toolbar.
    /// </summary>
    private void OnMapsChanged()
    {
        var maps = new List<VideoMapData>();
        foreach (ScopeMapToggle toggle in MapToggles.Where(t => t.IsOn))
        {
            if (Main.GetCachedVideoMap(toggle.MapId) is { } map)
            {
                maps.Add(map);
            }
        }

        VideoMaps = maps;
        OnPropertyChanged(nameof(MapToggles));
    }
}

/// <summary>The Scope's DB settings. A data record: nothing here reads the preferences after <see cref="FromPreferences"/>.</summary>
/// <param name="Line4">4TH LINE: the ERAM line 4 falls back to the type and the destination when no HSF is set.</param>
/// <param name="HoverDwell">DWELL: the pointer dwells the block it is over; locked blocks dwell either way.</param>
/// <param name="Atpa">ATPA DIST: blocks show the ATPA in-trail distance. The text only: the cones have their own toggles.</param>
/// <param name="AtpaAlertCones">ALERT CONES: an ATPA pair in the Warning or Alert state draws its cone.</param>
/// <param name="AtpaMonitorCones">MONITOR CONES: an ATPA pair in the Monitor state draws its cone.</param>
/// <param name="EramCaRing">CA RING: an ERAM conflict pair draws its ring on each member but an MCI pair's Mode C intruder.</param>
/// <param name="CharSize">CHAR: the datablock point size, <see cref="MinCharSize"/> to <see cref="MaxCharSize"/>.</param>
public sealed record ScopeDbSettings(
    bool Line4,
    bool HoverDwell,
    bool Atpa,
    bool AtpaAlertCones,
    bool AtpaMonitorCones,
    bool EramCaRing,
    int CharSize
)
{
    /// <summary>The smallest CHAR size.</summary>
    public const int MinCharSize = 8;

    /// <summary>The largest CHAR size.</summary>
    public const int MaxCharSize = 24;

    /// <summary>Whether <paramref name="size"/> is a CHAR size.</summary>
    public static bool IsCharSize(int size) => size is >= MinCharSize and <= MaxCharSize;

    /// <summary>
    /// The settings a scope opens with: 4TH LINE, DWELL, ALERT CONES, MONITOR CONES and CA RING on, ATPA DIST and CHAR as the
    /// preferences have them.
    /// </summary>
    public static ScopeDbSettings FromPreferences(UserPreferences preferences) =>
        new(
            Line4: true,
            HoverDwell: true,
            Atpa: preferences.ShowAtpa,
            AtpaAlertCones: true,
            AtpaMonitorCones: true,
            EramCaRing: true,
            CharSize: Math.Clamp(preferences.RadarDatablockFontSize, MinCharSize, MaxCharSize)
        );
}
