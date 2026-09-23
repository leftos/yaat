using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Data;

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
    /// <see cref="ExtraRadarViews"/>. An instance on the primary's airport mirrors its layout
    /// (<see cref="GroundViewModel.MirrorLayoutFrom"/>) instead of fetching its own copy; an instance on
    /// any other airport loads that airport's layout itself.
    /// </summary>
    public ObservableCollection<GroundViewInstance> ExtraGroundViews { get; } = [];

    /// <summary>Every Radar View driven by this view-model: the docked primary first, then the extra windows.</summary>
    public IEnumerable<RadarViewModel> AllRadarViews
    {
        get
        {
            yield return Radar;
            foreach (RadarViewInstance instance in ExtraRadarViews)
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
            foreach (GroundViewInstance instance in ExtraGroundViews)
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
    private string? _lastScenarioArtccId;
    private string? _lastScenarioId;
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
        vm.ShownAirportChanged += () =>
        {
            OnPropertyChanged(nameof(GroundShownAirportId));
            // The layout usually lands after the weather (it is fetched async), so re-pick this view's
            // station whenever the airport it shows changes.
            vm.WeatherInfo = PickGroundWeather(_allWeatherInfo, vm.Layout?.AirportId);
        };
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

    /// <summary>
    /// Opens another Radar View instance on the lowest free ordinal, based on
    /// <paramref name="airportId"/>, and persists the new set. The airport decides the instance's
    /// centre and video maps — a second Radar View has no scenario-inferred target, so the caller
    /// (the View menu) asks the user for one first.
    /// </summary>
    public RadarViewInstance OpenExtraRadarView(string airportId)
    {
        RadarViewInstance instance = OpenRadarInstance(
            ViewInstanceOrdinals.NextFree(ExtraRadarViews.Select(i => i.Ordinal)),
            NormalizeAirportId(airportId)
        );
        PersistExtraViews();
        return instance;
    }

    /// <summary>
    /// Opens another Ground View instance on the lowest free ordinal, based on
    /// <paramref name="airportId"/>, and persists the new set. Same convention as
    /// <see cref="OpenExtraRadarView"/>; the airport decides whether the instance mirrors the primary
    /// view's layout or loads its own.
    /// </summary>
    public GroundViewInstance OpenExtraGroundView(string airportId)
    {
        GroundViewInstance instance = OpenGroundInstance(
            ViewInstanceOrdinals.NextFree(ExtraGroundViews.Select(i => i.Ordinal)),
            NormalizeAirportId(airportId)
        );
        PersistExtraViews();
        return instance;
    }

    /// <summary>The ARTCC's airports, the list the extra-view airport picker offers; empty when the lookup fails.</summary>
    public async Task<IReadOnlyList<string>> GetArtccAirportIdsAsync()
    {
        try
        {
            return await _airportResolver.GetAirportIdsAsync(_preferences.ArtccId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to list airports for ARTCC '{Artcc}'", _preferences.ArtccId);
            return [];
        }
    }

    /// <summary>
    /// The airport an extra-view picker starts on: the active scenario's primary airport, or the airport
    /// the docked Ground View is showing when no scenario is loaded, in the FAA form the picker lists.
    /// Null when neither is known.
    /// </summary>
    public string? DefaultExtraViewAirportId
    {
        get
        {
            string? airportId = _lastRadarPrimaryAirportId ?? Ground.Layout?.AirportId;
            return airportId is null ? null : NormalizeAirportId(airportId);
        }
    }

    /// <summary>
    /// Whether the navigation database knows <paramref name="airportId"/> in either the FAA ("OAK") or
    /// the ICAO ("KOAK") form, so the picker can refuse a typo. Everything is accepted while the nav db
    /// is still loading — it is not yet in a position to say.
    /// </summary>
    public bool IsKnownAirport(string airportId) =>
        !_commandInput.NavDbReady
        || NavigationDatabase.Instance.TryResolveAirport(airportId, out _)
        || (ResolveAirportPosition(airportId) is not null);

    /// <summary>
    /// Canonicalizes an airport id to the published FAA form ("KOAK" and "oak" both become "OAK") — the
    /// form the ARTCC config lists, the video-map facilities are keyed by, and the tower-cab layers use,
    /// so either form the user types opens the same window. Falls back to the trimmed upper-case input
    /// while the navigation database is still loading or for an airport it does not know.
    /// </summary>
    private string NormalizeAirportId(string airportId)
    {
        string trimmed = airportId.Trim().ToUpperInvariant();
        if (_commandInput.NavDbReady && NavigationDatabase.Instance.TryResolveFaaId(trimmed, out string? faaId))
        {
            return faaId;
        }

        return trimmed;
    }

    /// <summary>
    /// The airport's position from the navigation database, looked up by the id as given and, failing
    /// that, by its canonical ICAO form — the FAA id an instance carries is not always a fix name.
    /// </summary>
    private static (double Lat, double Lon)? ResolveAirportPosition(string airportId)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (navDb.GetFixPosition(airportId) is { } direct)
        {
            return direct;
        }

        return navDb.TryResolveAirport(airportId, out string? canonicalId) ? navDb.GetFixPosition(canonicalId) : null;
    }

    /// <summary>Drops an extra Radar View instance. A no-op when it was already removed.</summary>
    public void CloseExtraRadarView(RadarViewInstance instance)
    {
        if (!ExtraRadarViews.Remove(instance))
        {
            return;
        }

        PersistExtraViews();
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
        PersistExtraViews();
    }

    /// <summary>
    /// Brings the open extra views in line with the given entries: instances not listed are closed,
    /// missing ones are opened and seeded, and survivors are left untouched so applying a window profile
    /// doesn't reset a window the profile also has. An instance survives only when an entry has both its
    /// ordinal and its airport — a window whose airport changed is a different window, so it is closed and
    /// reopened on the new one. Persists the resulting set once, at the end.
    /// </summary>
    public void ReconcileExtraViews(IReadOnlyList<SavedExtraView> radar, IReadOnlyList<SavedExtraView> ground)
    {
        for (int i = ExtraRadarViews.Count - 1; i >= 0; i--)
        {
            if (!radar.Any(entry => Matches(entry, ExtraRadarViews[i].Ordinal, ExtraRadarViews[i].AirportId)))
            {
                ExtraRadarViews.RemoveAt(i);
            }
        }

        for (int i = ExtraGroundViews.Count - 1; i >= 0; i--)
        {
            if (!ground.Any(entry => Matches(entry, ExtraGroundViews[i].Ordinal, ExtraGroundViews[i].AirportId)))
            {
                ExtraGroundViews[i].Vm.StopMirroring();
                ExtraGroundViews.RemoveAt(i);
            }
        }

        foreach (SavedExtraView entry in radar)
        {
            if (!ExtraRadarViews.Any(instance => Matches(entry, instance.Ordinal, instance.AirportId)))
            {
                OpenRadarInstance(entry.Ordinal, NormalizeAirportId(entry.AirportId));
            }
        }

        foreach (SavedExtraView entry in ground)
        {
            if (!ExtraGroundViews.Any(instance => Matches(entry, instance.Ordinal, instance.AirportId)))
            {
                OpenGroundInstance(entry.Ordinal, NormalizeAirportId(entry.AirportId));
            }
        }

        PersistExtraViews();
    }

    private static bool Matches(SavedExtraView entry, int ordinal, string airportId) =>
        (entry.Ordinal == ordinal) && string.Equals(entry.AirportId, airportId, StringComparison.OrdinalIgnoreCase);

    private RadarViewInstance OpenRadarInstance(int ordinal, string airportId)
    {
        RadarViewModel vm = CreateRadarViewModel(isPrimary: false, settingsKeySuffix: OrdinalSuffix(ordinal));
        var instance = new RadarViewInstance
        {
            Ordinal = ordinal,
            AirportId = airportId,
            Vm = vm,
        };
        SeedExtraRadar(instance);
        ExtraRadarViews.Add(instance);
        return instance;
    }

    private GroundViewInstance OpenGroundInstance(int ordinal, string airportId)
    {
        GroundViewModel vm = CreateGroundViewModel(isPrimary: false, settingsKeySuffix: OrdinalSuffix(ordinal));
        var instance = new GroundViewInstance
        {
            Ordinal = ordinal,
            AirportId = airportId,
            Vm = vm,
        };
        SeedExtraGround(instance);
        ExtraGroundViews.Add(instance);
        return instance;
    }

    /// <summary>The per-scenario settings-key suffix for an extra view instance, e.g. <c>"#2"</c>.</summary>
    private static string OrdinalSuffix(int ordinal) => "#" + ordinal.ToString(CultureInfo.InvariantCulture);

    private void PersistExtraViews()
    {
        _preferences.SetExtraRadarViews([.. ExtraRadarViews.OrderBy(i => i.Ordinal).Select(i => new SavedExtraView(i.Ordinal, i.AirportId))]);
        _preferences.SetExtraGroundViews([.. ExtraGroundViews.OrderBy(i => i.Ordinal).Select(i => new SavedExtraView(i.Ordinal, i.AirportId))]);
    }

    /// <summary>
    /// Catches a newly-created Radar View up on everything the docked view was told before it existed:
    /// the measuring tool, the navigation database, the MVA tint, its own base airport (id, position and
    /// video maps, which also restores its per-scenario settings), the position display config, weather,
    /// and the app-wide selected aircraft.
    /// </summary>
    private void SeedExtraRadar(RadarViewInstance instance)
    {
        RadarViewModel vm = instance.Vm;
        vm.SetMeasureState(Measure);
        if (_airportElevationLookup is { } elevationLookup)
        {
            vm.SetElevationLookup(elevationLookup);
            vm.SetNavDbReady();
        }

        vm.ShowMvaHints = Radar.ShowMvaHints;
        SeedRadarAirport(instance);
        if (_lastPositionDisplayConfig is { } positionConfig)
        {
            vm.ApplyPositionDisplayConfig(positionConfig);
        }

        vm.WeatherInfo = FilterWeatherForPosition(_allWeatherInfo, vm.WeatherAirports);
        _isSyncingSelection = true;
        vm.SelectedAircraft = SelectedAircraft;
        _isSyncingSelection = false;
    }

    /// <summary>
    /// Points an extra Radar View at its own base airport: the airport id, its position from the
    /// navigation database, and the video maps for it (the load also restores this instance's
    /// per-scenario settings and, on its first run, centres the view on the airport). Re-run on every
    /// scenario bootstrap and recording load, because those replace the maps and the settings key —
    /// never with the primary's airport, which belongs to the docked view alone.
    /// </summary>
    private void SeedRadarAirport(RadarViewInstance instance)
    {
        RadarViewModel vm = instance.Vm;
        vm.SetPrimaryAirportId(instance.AirportId);
        if (_commandInput.NavDbReady && (ResolveAirportPosition(instance.AirportId) is { } position))
        {
            vm.SetPrimaryAirportPosition(position.Lat, position.Lon);
        }

        // The ARTCC and scenario id the docked view was given — a recording load can carry an ARTCC other
        // than the preference, and the scenario id keys this instance's own saved view.
        string artccId = _lastScenarioArtccId ?? _preferences.ArtccId;
        if (!string.IsNullOrEmpty(artccId))
        {
            _ = vm.LoadVideoMapsForArtccAsync(artccId, instance.AirportId, _lastScenarioId);
        }
    }

    /// <summary>
    /// Catches a newly-created Ground View up on the measuring tool, the navigation database, the active
    /// scenario's settings key, its own base airport's layout, that layout's weather and the app-wide
    /// selected aircraft.
    /// </summary>
    private void SeedExtraGround(GroundViewInstance instance)
    {
        GroundViewModel vm = instance.Vm;
        vm.SetMeasureState(Measure);
        if (_airportElevationLookup is { } elevationLookup)
        {
            vm.SetElevationLookup(elevationLookup);
        }

        vm.SetScenarioId(_lastScenarioId);
        SeedGroundAirport(instance);
        vm.WeatherInfo = Ground.WeatherInfo;
        _isSyncingSelection = true;
        vm.SelectedAircraft = SelectedAircraft;
        _isSyncingSelection = false;
    }

    /// <summary>
    /// Shows an extra Ground View its own base airport. On the primary view's airport it mirrors that
    /// view — no second fetch, no second copy of the tower-cab image — and on any other airport it loads
    /// that airport's layout and tower-cab layers itself. Re-run whenever the primary's airport changes,
    /// since that can flip an instance between the two.
    /// </summary>
    private void SeedGroundAirport(GroundViewInstance instance)
    {
        GroundViewModel vm = instance.Vm;
        // The scenario's airport comes first: during a bootstrap the docked view still holds the previous
        // scenario's layout until the server answers, so its airport is stale exactly when this runs. Both
        // sides are canonicalized — the scenario files the ICAO form ("KOAK"), an instance carries the FAA
        // form ("OAK"), and they are the same airport.
        string? primaryAirport = _lastRadarPrimaryAirportId ?? Ground.Layout?.AirportId;
        if ((primaryAirport is not null) && string.Equals(instance.AirportId, NormalizeAirportId(primaryAirport), StringComparison.OrdinalIgnoreCase))
        {
            vm.MirrorLayoutFrom(Ground);
            return;
        }

        vm.StopMirroring();
        _ = vm.LoadLayoutAsync(instance.AirportId);
        string artccId = _preferences.ArtccId;
        if (!string.IsNullOrEmpty(artccId))
        {
            _ = vm.LoadTowerCabLayersAsync(artccId, instance.AirportId);
        }
    }

    private void OnExtraGroundViewsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        // An extra Ground View window is always visible, so opening or closing one changes whether the
        // radar should surface that airport's ground speech bubbles.
        OnPropertyChanged(nameof(GroundShownAirportId));
}
