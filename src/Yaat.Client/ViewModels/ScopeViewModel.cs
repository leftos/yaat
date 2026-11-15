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
/// is shared with <see cref="RadarViewModel"/>. It starts from the radar's centre, range, map toggles, video maps and
/// MVA-tint flag once, the first time the radar has a centre and map toggles or active maps, and is independent of the radar
/// after that. The selection is the app-wide one: a pick here sets <see cref="MainViewModel.SelectedAircraft"/>, and a pick anywhere else shows here.
/// </summary>
public sealed partial class ScopeViewModel : ObservableObject, IDisposable
{
    /// <summary>The range the scope opens at until the radar gives it one.</summary>
    public const double DefaultRangeNm = 40;

    private bool _isSyncingSelection;

    /// <summary>Whether <see cref="Dispose"/> has run; a second call changes nothing and saves nothing.</summary>
    private bool _disposed;

    /// <summary>
    /// The radar the scope takes its first centre, map toggles and maps from, while it still waits for a centre and toggles or
    /// maps; null once seeded.
    /// </summary>
    private RadarViewModel? _seedSource;

    [ObservableProperty]
    private double _centerLat;

    [ObservableProperty]
    private double _centerLon;

    [ObservableProperty]
    private double _rangeNm = DefaultRangeNm;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    /// <summary>
    /// The video maps the scope draws: the radar's active maps at the seed, then the maps of the scope's own on toggles
    /// (<see cref="MapToggles"/>), in toggle order.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<VideoMapData> _videoMaps = [];

    /// <summary>Brightness category ("A" or "B") by video map id, copied with the maps.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, string> _brightnessLookup = new Dictionary<string, string>();

    /// <summary>Whether the scope tints altitudes against the MVA, copied from the radar when the scope is seeded.</summary>
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
    /// Seeds the scope from the radar now when the radar has a centre and map toggles or active maps; otherwise waits for
    /// the radar's first centre and toggles or maps, takes them once and stops listening.
    /// </summary>
    /// <param name="main">The main view model.</param>
    /// <param name="prefStore">The pref-set store <c>.scope pref</c> reads and writes, or null when prefs are unavailable.</param>
    /// <param name="profileStore">The profile files the scope reads its profiles from, or null to use the shipped profiles.</param>
    public ScopeViewModel(MainViewModel main, ScopePrefSetStore? prefStore, ScopeProfileStore? profileStore)
    {
        Main = main;
        PrefStore = prefStore;
        ProfileStore = profileStore;
        _selectedAircraft = main.SelectedAircraft;
        _templateId = main.Preferences.EuroScopeMode ? ScopeTemplates.EuroScopeId : ScopeTemplates.StarsId;
        _dbSettings = ScopeDbSettings.FromPreferences(main.Preferences);
        _viewer = BuildViewer(main);
        main.PropertyChanged += OnMainPropertyChanged;
        if (!TrySeedFrom(main.Radar))
        {
            _seedSource = main.Radar;
            _seedSource.PropertyChanged += OnSeedSourcePropertyChanged;
        }

        InitViews();
        SyncStateFacility();
        InitProfile();
    }

    /// <summary>Whether the scope is still waiting for the radar's first centre and maps. Test seam.</summary>
    internal bool IsWaitingForRadarSeed => _seedSource is not null;

    /// <summary>The main view model: the traffic, the preferences and the app-wide selection.</summary>
    public MainViewModel Main { get; }

    /// <summary>The traffic the scope draws.</summary>
    public ObservableCollection<AircraftModel> Aircraft => Main.Aircraft;

    /// <summary>The user's preferences; the scope seeds <see cref="TemplateId"/> and <see cref="DbSettings"/> from them once.</summary>
    public UserPreferences Preferences => Main.Preferences;

    /// <summary>The scope's own per-callsign datablock state; the scope places each block from it.</summary>
    public RadarDataBlockViewState DataBlockState { get; } = new();

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
        StopWaitingForSeed();
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
    /// Copies the radar's centre, range, map toggles, video maps, their brightness categories and its MVA-tint flag when the
    /// radar has a centre (both coordinates set) and at least one map toggle or active map.
    /// </summary>
    /// <returns>True when the scope was seeded.</returns>
    private bool TrySeedFrom(RadarViewModel radar)
    {
        IReadOnlyList<VideoMapData> maps = radar.ActiveVideoMaps ?? [];
        if ((radar.CenterLat == 0) || (radar.CenterLon == 0) || ((radar.MapToggles.Count == 0) && (maps.Count == 0)))
        {
            return false;
        }

        BrightnessLookup = new Dictionary<string, string>(radar.BrightnessLookup);
        SeedMaps(radar.MapToggles, maps);
        ShowMvaHints = radar.ShowMvaHints;
        RangeNm = radar.RangeNm > 0 ? radar.RangeNm : DefaultRangeNm;
        CenterLat = radar.CenterLat;
        CenterLon = radar.CenterLon;
        ApplyPendingPrefSet();
        return true;
    }

    private void OnSeedSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        bool seedInput =
            e.PropertyName is nameof(RadarViewModel.CenterLat) or nameof(RadarViewModel.CenterLon) or nameof(RadarViewModel.ActiveVideoMaps);
        if (seedInput && (_seedSource is { } radar) && TrySeedFrom(radar))
        {
            StopWaitingForSeed();
        }
    }

    private void StopWaitingForSeed()
    {
        _seedSource?.PropertyChanged -= OnSeedSourcePropertyChanged;
        _seedSource = null;
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
