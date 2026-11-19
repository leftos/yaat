using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The YAAT Scope window's view model. It keeps its own centre, range, map toggles, brightness and datablock state; nothing
/// is shared with another view. At every facility load of the Scope's map source (<see cref="MainViewModel.ScopeMaps"/>) it
/// takes the facility's map toggles, brightness categories, centre and MVA-tint default, and opens at
/// <see cref="DefaultRangeNm"/>, keeping on the maps that were on. The selection is the app-wide one: a pick here sets
/// <see cref="MainViewModel.SelectedAircraft"/>, and a pick anywhere else shows here.
/// </summary>
public sealed partial class ScopeViewModel : ObservableObject, IDisposable
{
    /// <summary>The range the scope opens at, and returns to at every facility load.</summary>
    public const double DefaultRangeNm = 40;

    private bool _isSyncingSelection;

    /// <summary>Whether <see cref="Dispose"/> has run; a second call changes nothing and saves nothing.</summary>
    private bool _disposed;

    /// <summary>True from a seed by the map source's catalogue until the source is cleared.</summary>
    private bool _isSeeded;

    /// <summary>The centre the latest seed put the scope at, or where a late recentre moved it since; null before the first seed.</summary>
    private (double Lat, double Lon)? _seededCenter;

    [ObservableProperty]
    private double _centerLat;

    [ObservableProperty]
    private double _centerLon;

    [ObservableProperty]
    private double _rangeNm = DefaultRangeNm;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    /// <summary>
    /// The video maps the scope draws: the maps of the scope's own on toggles (<see cref="MapToggles"/>), in toggle order,
    /// each from the video-map cache.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<VideoMapData> _videoMaps = [];

    /// <summary>Brightness category ("A" or "B") by video map id, copied with the maps.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, string> _brightnessLookup = new Dictionary<string, string>();

    /// <summary>Whether the scope tints altitudes against the MVA, set to the map source's default at every seed.</summary>
    [ObservableProperty]
    private bool _showMvaHints;

    /// <summary>
    /// Id of the datablock template the scope draws (<see cref="ScopeTemplates"/>): seeded once from the EuroScope-mode
    /// preference when the scope opens, then changed by <c>.scope template</c> (the toolbar's TEMPLATE menu runs it), by a
    /// pref-set load or saved-state restore, and by a profile applied while the facility has no saved state; never written
    /// to the preferences.
    /// </summary>
    [ObservableProperty]
    private string _templateId;

    /// <summary>
    /// The position the scope is viewed from: the main view model's active position, rebuilt whenever its TCP, position or
    /// acting callsign/facility changes.
    /// </summary>
    [ObservableProperty]
    private ScopeViewer _viewer;

    /// <summary>
    /// Seeds the scope from the map source now when its latest load is over, otherwise at its next load; it then reseeds at
    /// every load, empties its maps when the source is cleared and follows a late recentre while the user has not moved it.
    /// The scope saves and restores its state in the slot <paramref name="ordinal"/> of its facility.
    /// </summary>
    /// <param name="main">The main view model.</param>
    /// <param name="prefStore">The pref-set store <c>.scope pref</c> reads and writes, or null when prefs are unavailable.</param>
    /// <param name="profileStore">The profile files the scope reads its profiles from, or null to use the shipped profiles.</param>
    /// <param name="ordinal">The Scope window's number: 1 for the primary Scope, n for extra window #n.</param>
    public ScopeViewModel(MainViewModel main, ScopePrefSetStore? prefStore, ScopeProfileStore? profileStore, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, ScopePrefSetStore.PrimarySlot);
        Ordinal = ordinal;
        Main = main;
        PrefStore = prefStore;
        ProfileStore = profileStore;
        _selectedAircraft = main.SelectedAircraft;
        _templateId = main.Preferences.EuroScopeMode ? ScopeTemplates.EuroScopeId : ScopeTemplates.StarsId;
        _dbSettings = ScopeDbSettings.FromPreferences(main.Preferences);
        _deconflictMode = main.Preferences.RadarDeconflictMode;
        _viewer = BuildViewer(main);
        main.PropertyChanged += OnMainPropertyChanged;
        main.Measure.PropertyChanged += OnMeasurePropertyChanged;
        main.ScopeMaps.Loaded += OnMapSourceLoaded;
        main.ScopeMaps.Cleared += OnMapSourceCleared;
        main.ScopeMaps.PropertyChanged += OnMapSourcePropertyChanged;
        if ((main.ScopeMaps.IsReady) && (main.ScopeMaps.HasCatalogue))
        {
            SeedFromMapSource();
        }

        InitViews();
        SyncStateFacility();
        InitProfile();
    }

    /// <summary>
    /// Whether a pref set or saved state applied now would be held until the next seed: the map source has not seeded the
    /// scope yet, or it has a load in flight whose seed would overwrite the set.
    /// </summary>
    internal bool IsWaitingForMapSource => (!_isSeeded) || Main.ScopeMaps.IsLoading;

    /// <summary>The main view model: the traffic, the preferences and the app-wide selection.</summary>
    public MainViewModel Main { get; }

    /// <summary>
    /// The Scope window's number: 1 for the primary Scope, n for extra window #n. It names the slot of the facility the
    /// Scope's current state saves in, so each window keeps its own.
    /// </summary>
    public int Ordinal { get; }

    /// <summary>Whether the Scope was disposed: its window closed for good, or the app exited.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>The traffic the scope draws.</summary>
    public ObservableCollection<AircraftModel> Aircraft => Main.Aircraft;

    /// <summary>The user's preferences; the scope seeds <see cref="TemplateId"/> and <see cref="DbSettings"/> from them once.</summary>
    public UserPreferences Preferences => Main.Preferences;

    /// <summary>The scope's own per-callsign datablock state; the scope places each block from it.</summary>
    public ScopeDataBlockViewState DataBlockState { get; } = new();

    /// <summary>Selects the aircraft with <paramref name="callsign"/>, as a left click on it does; an unknown callsign changes nothing.</summary>
    public void SelectAircraft(string callsign)
    {
        AircraftModel? ac = FindAircraft(callsign);
        if (ac is not null)
        {
            SelectedAircraft = ac;
        }
    }

    /// <summary>
    /// Toggles the instructor's local dwell lock of the aircraft with <paramref name="callsign"/>, as a click on its ERAM block's
    /// Field A does: the block holds at Dwell on this scope only, and nothing is sent to the server. A block the student's CRC
    /// already locks for the viewer's ERAM sector gains no local lock, since it is at Dwell already; a local lock is always
    /// released.
    /// </summary>
    public void ToggleDwellLock(string callsign)
    {
        if (!DataBlockState.DwellLockedCallsigns.Contains(callsign) && (FindAircraft(callsign) is { } ac) && EramDwellLock.IsDwellLocked(ac, Viewer))
        {
            return;
        }

        DataBlockState.ToggleDwellLock(callsign);
    }

    /// <summary>
    /// Toggles the highlight of the aircraft's block on this scope, as a middle click on a STARS or EuroScope block does; the
    /// scope's own state, never another view's.
    /// </summary>
    public void ToggleHighlight(string callsign) => DataBlockState.ToggleHighlight(callsign);

    /// <summary>The aircraft with <paramref name="callsign"/>, or null.</summary>
    public AircraftModel? FindAircraft(string callsign) => Main.Aircraft.FirstOrDefault(a => a.Callsign == callsign);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SaveStateFor(_stateFacility);
        Main.PropertyChanged -= OnMainPropertyChanged;
        Main.Measure.PropertyChanged -= OnMeasurePropertyChanged;
        ClearMinSep();
        Main.ScopeMaps.Loaded -= OnMapSourceLoaded;
        Main.ScopeMaps.Cleared -= OnMapSourceCleared;
        Main.ScopeMaps.PropertyChanged -= OnMapSourcePropertyChanged;
        StopViews();
    }

    /// <summary>Picks the datablock template (or Mixed) with <paramref name="templateId"/>, as <c>.scope template</c> does.</summary>
    /// <exception cref="ArgumentException">The id is no selectable pick (<see cref="ScopeTemplates.IsSelectable"/>).</exception>
    [RelayCommand]
    private void PickTemplate(string templateId) =>
        TemplateId = ScopeTemplates.IsSelectable(templateId)
            ? templateId
            : throw new ArgumentException($"Unknown scope template id '{templateId}'.", nameof(templateId));

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        if (!_isSyncingSelection)
        {
            Main.SelectedAircraft = value;
        }
    }

    /// <summary>
    /// Takes the map source's maps (keeping on the maps that were on, by map id), brightness categories, MVA-tint default and
    /// centre, opens at <see cref="DefaultRangeNm"/>, then applies a pref set held while the scope waited.
    /// </summary>
    private void SeedFromMapSource()
    {
        ScopeMapSource source = Main.ScopeMaps;
        _isSeeded = true;
        BrightnessLookup = new Dictionary<string, string>(source.BrightnessLookup);
        SeedMaps(source.Maps);
        ShowMvaHints = source.ShowMvaHintsDefault;
        RangeNm = DefaultRangeNm;
        CenterLat = source.CenterLat;
        CenterLon = source.CenterLon;
        _seededCenter = (CenterLat, CenterLon);
        CenterRangeRing();
        ApplyPendingPrefSet();
    }

    private void OnMapSourceLoaded(object? sender, EventArgs e) => SeedFromMapSource();

    /// <summary>
    /// Empties the scope's maps and makes it unseeded again, so a set applied before the next load holds its maps, range
    /// and centre for that load's seed.
    /// </summary>
    private void OnMapSourceCleared(object? sender, EventArgs e)
    {
        _isSeeded = false;
        ClearSeededMaps();
    }

    private void OnMapSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScopeMapSource.CenterLat) or nameof(ScopeMapSource.CenterLon):
                FollowLateRecentre();
                SyncOffCenter();
                break;
            case nameof(ScopeMapSource.IsLoading):
                OnMapSourceLoadEnded();
                break;
            case nameof(ScopeMapSource.Fixes):
                OnMapSourceFixesChanged();
                break;
        }
    }

    /// <summary>
    /// Once a load ends with no new catalogue (no maps, or the fetch failed) while an older one stands: a scope opened
    /// during the load seeds from the standing catalogue, and a seeded one applies the part of a set held during the load
    /// against its current toggles. A load that seeded did both already; a cleared source keeps the held part for the next
    /// seed.
    /// </summary>
    private void OnMapSourceLoadEnded()
    {
        ScopeMapSource source = Main.ScopeMaps;
        if ((source.IsLoading) || (!source.HasCatalogue))
        {
            return;
        }

        if (_isSeeded)
        {
            ApplyPendingPrefSet();
        }
        else if (source.IsReady)
        {
            SeedFromMapSource();
        }
    }

    /// <summary>
    /// Follows the map source's late recentre while the scope is still at its seeded centre and range; a scope the user
    /// panned or zoomed since stays where it is.
    /// </summary>
    private void FollowLateRecentre()
    {
        if ((_seededCenter is not { } seeded) || (CenterLat != seeded.Lat) || (CenterLon != seeded.Lon) || (RangeNm != DefaultRangeNm))
        {
            return;
        }

        CenterLat = Main.ScopeMaps.CenterLat;
        CenterLon = Main.ScopeMaps.CenterLon;
        _seededCenter = (CenterLat, CenterLon);
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedAircraft):
                _isSyncingSelection = true;
                SelectedAircraft = Main.SelectedAircraft;
                _isSyncingSelection = false;
                break;
            case nameof(MainViewModel.ActiveTcp)
            or nameof(MainViewModel.ActivePosition)
            or nameof(MainViewModel.ActingCallsign)
            or nameof(MainViewModel.ActingTrackFacilityId):
                Viewer = BuildViewer(Main);
                break;
            case nameof(MainViewModel.ScopeProfile):
                OnMainScopeProfileChanged();
                break;
        }
    }

    /// <summary>
    /// The viewer <paramref name="main"/>'s active position names: the acting position as <c>MenuSession.Acting</c> builds it,
    /// and the position's track facility (else its listing facility) and ERAM sector id.
    /// </summary>
    private static ScopeViewer BuildViewer(MainViewModel main)
    {
        ActingPosition? acting = string.IsNullOrEmpty(main.ActiveTcp)
            ? null
            : new ActingPosition(main.ActingCallsign, main.ActingTrackFacilityId, main.ActiveTcp);
        ActivePositionDto? position = main.ActivePosition;
        if ((acting is null) && (position is null))
        {
            return ScopeViewer.None;
        }

        return new ScopeViewer(acting, position?.TrackFacilityId ?? position?.FacilityId, position?.EramSectorId);
    }
}
