using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// The extra Radar View windows (#2, #3, …) this session has open. The docked Radar View is the
    /// implicit #1 (<see cref="Radar"/>) and has no entry here. Each instance owns its own
    /// <see cref="RadarViewModel"/> — center, range, filters and datablock offsets are per-window — while
    /// the aircraft collection and the selected aircraft stay app-wide.
    /// </summary>
    public ObservableCollection<RadarViewInstance> ExtraRadarViews { get; } = [];

    /// <summary>
    /// The extra Ground View windows (#2, #3, …) this session has open; same convention as
    /// <see cref="ExtraRadarViews"/>. An extra Ground View mirrors the primary's airport layout
    /// (<see cref="GroundViewModel.MirrorLayoutFrom"/>) instead of fetching its own copy.
    /// </summary>
    public ObservableCollection<GroundViewInstance> ExtraGroundViews { get; } = [];

    /// <summary>Every Radar View driven by this view-model: the docked primary first, then the extra windows.</summary>
    public IEnumerable<RadarViewModel> AllRadarViews
    {
        get
        {
            yield return Radar;
            foreach (var instance in ExtraRadarViews)
            {
                yield return instance.Vm;
            }
        }
    }

    /// <summary>Every Ground View driven by this view-model: the docked primary first, then the extra windows.</summary>
    public IEnumerable<GroundViewModel> AllGroundViews
    {
        get
        {
            yield return Ground;
            foreach (var instance in ExtraGroundViews)
            {
                yield return instance.Vm;
            }
        }
    }

    // State the docked views received before an extra window existed. A window opened mid-session can't
    // replay the events that carried it, so each one-shot push is stashed here and re-applied by the
    // seeding methods below. The scenario-scoped entries are dropped in ClearScenarioState.
    private Func<string, double?>? _airportElevationLookup;
    private string? _lastRadarPrimaryAirportId;
    private (double Lat, double Lon)? _lastRadarAirportPosition;
    private ScenarioBootstrap? _lastScenarioBootstrap;
    private string? _lastScenarioArtccId;
    private string? _lastGroundScenarioId;
    private PositionDisplayConfigDto? _lastPositionDisplayConfig;

    /// <summary>
    /// Builds a <see cref="GroundViewModel"/> wired to this view-model's connection, services and
    /// selection callback. Used for the docked primary view in the constructor and for every extra
    /// Ground View window afterwards; the two differ only in <paramref name="isPrimary"/> (global
    /// preference writes and layout loading are primary-only) and the per-scenario settings key.
    /// </summary>
    private GroundViewModel CreateGroundViewModel(bool isPrimary, string settingsKeySuffix)
    {
        var vm = new GroundViewModel(_connection, SendCommandForViewAsync, OnChildSelectionChanged, _preferences)
        {
            IsPrimary = isPrimary,
            SettingsKeySuffix = settingsKeySuffix,
        };
        vm.ShownAirportChanged += () => OnPropertyChanged(nameof(GroundShownAirportId));
        vm.SetAircraftLookup(cs => Aircraft.FirstOrDefault(a => a.Callsign == cs));
        vm.SetAircraftProvider(() => Aircraft);
        vm.SetTowerCabServices(_vnasConfigService, _towerCabImageService, _airportResolver);
        // Null only while the constructor builds the primary views — Measure is created right after
        // them and handed over there. Every later instance gets it here.
        if (Measure is { } measure)
        {
            vm.SetMeasureState(measure);
        }

        return vm;
    }

    /// <summary>
    /// Builds a <see cref="RadarViewModel"/> wired to this view-model's connection, services and
    /// selection callback; the counterpart of <see cref="CreateGroundViewModel"/>.
    /// </summary>
    private RadarViewModel CreateRadarViewModel(bool isPrimary, string settingsKeySuffix)
    {
        var vm = new RadarViewModel(_connection, _videoMapService, SendCommandForViewAsync, OnChildSelectionChanged)
        {
            IsPrimary = isPrimary,
            SettingsKeySuffix = settingsKeySuffix,
        };
        vm.SetPreferences(_preferences);
        vm.SetAircraftLookup(cs => Aircraft.FirstOrDefault(a => a.Callsign == cs));
        if (Measure is { } measure)
        {
            vm.SetMeasureState(measure);
        }

        return vm;
    }

    /// <summary>Opens another Radar View instance on the lowest free ordinal and persists the new set.</summary>
    [RelayCommand]
    private void OpenExtraRadarView()
    {
        OpenRadarInstance(ViewInstanceOrdinals.NextFree(ExtraRadarViews.Select(i => i.Ordinal)));
        PersistExtraViewOrdinals();
    }

    /// <summary>Opens another Ground View instance on the lowest free ordinal and persists the new set.</summary>
    [RelayCommand]
    private void OpenExtraGroundView()
    {
        OpenGroundInstance(ViewInstanceOrdinals.NextFree(ExtraGroundViews.Select(i => i.Ordinal)));
        PersistExtraViewOrdinals();
    }

    /// <summary>Drops an extra Radar View instance. A no-op when it was already removed.</summary>
    public void CloseExtraRadarView(RadarViewInstance instance)
    {
        if (!ExtraRadarViews.Remove(instance))
        {
            return;
        }

        PersistExtraViewOrdinals();
    }

    /// <summary>
    /// Drops an extra Ground View instance, detaching it from the primary's layout so the closed
    /// window's view-model stops tracking it. A no-op when it was already removed.
    /// </summary>
    public void CloseExtraGroundView(GroundViewInstance instance)
    {
        if (!ExtraGroundViews.Remove(instance))
        {
            return;
        }

        instance.Vm.StopMirroring();
        PersistExtraViewOrdinals();
    }

    /// <summary>
    /// Brings the open extra views in line with the given ordinals: instances not listed are closed,
    /// missing ones are opened and seeded, and survivors are left untouched so applying a window profile
    /// doesn't reset a window the profile also has. Persists the resulting set once, at the end.
    /// </summary>
    public void ReconcileExtraViews(IReadOnlyList<int> radarOrdinals, IReadOnlyList<int> groundOrdinals)
    {
        for (var i = ExtraRadarViews.Count - 1; i >= 0; i--)
        {
            if (!radarOrdinals.Contains(ExtraRadarViews[i].Ordinal))
            {
                ExtraRadarViews.RemoveAt(i);
            }
        }

        for (var i = ExtraGroundViews.Count - 1; i >= 0; i--)
        {
            if (!groundOrdinals.Contains(ExtraGroundViews[i].Ordinal))
            {
                ExtraGroundViews[i].Vm.StopMirroring();
                ExtraGroundViews.RemoveAt(i);
            }
        }

        foreach (var ordinal in radarOrdinals)
        {
            if (!ExtraRadarViews.Any(instance => instance.Ordinal == ordinal))
            {
                OpenRadarInstance(ordinal);
            }
        }

        foreach (var ordinal in groundOrdinals)
        {
            if (!ExtraGroundViews.Any(instance => instance.Ordinal == ordinal))
            {
                OpenGroundInstance(ordinal);
            }
        }

        PersistExtraViewOrdinals();
    }

    private RadarViewInstance OpenRadarInstance(int ordinal)
    {
        var vm = CreateRadarViewModel(isPrimary: false, settingsKeySuffix: OrdinalSuffix(ordinal));
        SeedExtraRadar(vm);
        var instance = new RadarViewInstance { Ordinal = ordinal, Vm = vm };
        ExtraRadarViews.Add(instance);
        return instance;
    }

    private GroundViewInstance OpenGroundInstance(int ordinal)
    {
        var vm = CreateGroundViewModel(isPrimary: false, settingsKeySuffix: OrdinalSuffix(ordinal));
        SeedExtraGround(vm);
        var instance = new GroundViewInstance { Ordinal = ordinal, Vm = vm };
        ExtraGroundViews.Add(instance);
        return instance;
    }

    /// <summary>The per-scenario settings-key suffix for an extra view instance, e.g. <c>"#2"</c>.</summary>
    private static string OrdinalSuffix(int ordinal) => "#" + ordinal.ToString(CultureInfo.InvariantCulture);

    private void PersistExtraViewOrdinals()
    {
        _preferences.SetExtraRadarViewOrdinals([.. ExtraRadarViews.Select(i => i.Ordinal).Order()]);
        _preferences.SetExtraGroundViewOrdinals([.. ExtraGroundViews.Select(i => i.Ordinal).Order()]);
    }

    /// <summary>
    /// Catches a newly-created Radar View up on everything the docked view was told before it existed:
    /// the measuring tool, the navigation database, the MVA tint, the primary airport, the active
    /// scenario (which loads this instance's video maps and restores its own per-scenario settings),
    /// the position display config, weather, and the app-wide selected aircraft.
    /// </summary>
    private void SeedExtraRadar(RadarViewModel vm)
    {
        vm.SetMeasureState(Measure);
        if (_airportElevationLookup is { } elevationLookup)
        {
            vm.SetElevationLookup(elevationLookup);
            vm.SetNavDbReady();
        }

        vm.ShowMvaHints = Radar.ShowMvaHints;
        vm.SetPrimaryAirportId(_lastRadarPrimaryAirportId);
        if (_lastScenarioBootstrap is { } bootstrap)
        {
            vm.ApplyScenarioBootstrap(bootstrap, _lastScenarioArtccId);
        }

        if (_lastPositionDisplayConfig is { } positionConfig)
        {
            vm.ApplyPositionDisplayConfig(positionConfig);
        }

        if (_lastRadarAirportPosition is { } position)
        {
            vm.SetPrimaryAirportPosition(position.Lat, position.Lon);
        }

        vm.WeatherInfo = FilterWeatherForPosition(_allWeatherInfo, vm.WeatherAirports);
        _isSyncingSelection = true;
        vm.SelectedAircraft = SelectedAircraft;
        _isSyncingSelection = false;
    }

    /// <summary>
    /// Catches a newly-created Ground View up on the measuring tool, the navigation database, the active
    /// scenario's settings key, the primary view's airport layout (mirrored, not re-fetched), its weather
    /// and the app-wide selected aircraft.
    /// </summary>
    private void SeedExtraGround(GroundViewModel vm)
    {
        vm.SetMeasureState(Measure);
        if (_airportElevationLookup is { } elevationLookup)
        {
            vm.SetElevationLookup(elevationLookup);
        }

        vm.SetScenarioId(_lastGroundScenarioId);
        vm.MirrorLayoutFrom(Ground);
        vm.WeatherInfo = Ground.WeatherInfo;
        _isSyncingSelection = true;
        vm.SelectedAircraft = SelectedAircraft;
        _isSyncingSelection = false;
    }

    private void OnExtraGroundViewsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // An extra Ground View window is always visible, so opening or closing one changes whether the
        // radar should surface that airport's ground speech bubbles.
        OnPropertyChanged(nameof(GroundShownAirportId));
    }
}
