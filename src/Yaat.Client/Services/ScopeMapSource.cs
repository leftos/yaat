using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// One video map of the loaded facility as the Scope lists it: the server's map info plus whether the user favourited it
/// for the ARTCC, airport or scenario the maps were loaded for.
/// </summary>
public sealed record ScopeMapInfo(
    string MapId,
    string ShortName,
    string Name,
    string BrightnessCategory,
    int StarsId,
    bool TdmOnly,
    bool IsFavoriteArtcc,
    bool IsFavoriteAirport,
    bool IsFavoriteScenario
);

/// <summary>
/// The main facility's map and display catalogue for the Scope: its video maps, brightness categories, map group, weather
/// airports, primary airport, centre, fixes and MVA facility, loaded from the server and fed by <see cref="MainViewModel"/>
/// on the scenario, recording and room paths. It holds what the facility offers, never which maps a view turned on.
/// </summary>
public sealed partial class ScopeMapSource(ServerConnection connection, VideoMapService videoMapService, UserPreferences preferences)
    : ObservableObject
{
    private readonly ILogger _log = AppLog.CreateLogger<ScopeMapSource>();

    private readonly ServerConnection _connection = connection;
    private readonly VideoMapService _videoMapService = videoMapService;
    private readonly UserPreferences _preferences = preferences;

    private readonly List<ScopeMapInfo> _maps = [];
    private readonly Dictionary<string, string> _brightnessLookup = [];
    private List<int?>? _positionMapGroupMapIds;
    private List<int?>? _dtoMapGroupMapIds;
    private LatLon _primaryAirportPosition;
    private LatLon? _areaFallbackCenter;
    private Func<string, double?>? _getAirportElevation;

    private string? _mvaFacilityId;
    private string? _primaryAirportId;
    private double _centerLat;
    private double _centerLon;
    private IReadOnlyList<(string Name, double Lat, double Lon)>? _fixes;
    private bool _isReady;
    private bool _isLoading;
    private bool _hasCatalogue;

    /// <summary>Whether a view shows MVA hints by default for the student's position type; false with no scenario.</summary>
    [ObservableProperty]
    private bool _showMvaHintsDefault;

    /// <summary>
    /// The facility whose video maps were loaded, whose MVA chart wins where charts overlap; null before a load and after
    /// <see cref="Clear"/>.
    /// </summary>
    public string? MvaFacilityId
    {
        get => _mvaFacilityId;
        private set => SetProperty(ref _mvaFacilityId, value);
    }

    /// <summary>The scenario's primary airport id, as the scenario names it; null when none.</summary>
    public string? PrimaryAirportId
    {
        get => _primaryAirportId;
        private set => SetProperty(ref _primaryAirportId, value);
    }

    /// <summary>The latitude a load centres on: the primary airport when its position is known, else the first area's centre.</summary>
    public double CenterLat
    {
        get => _centerLat;
        private set => SetProperty(ref _centerLat, value);
    }

    /// <summary>The longitude a load centres on: the primary airport when its position is known, else the first area's centre.</summary>
    public double CenterLon
    {
        get => _centerLon;
        private set => SetProperty(ref _centerLon, value);
    }

    /// <summary>The chartable fixes once the navigation database is ready; null before.</summary>
    public IReadOnlyList<(string Name, double Lat, double Lon)>? Fixes
    {
        get => _fixes;
        private set => SetProperty(ref _fixes, value);
    }

    /// <summary>
    /// True once the latest load is over (maps downloaded, no maps, or failed); false while one runs and after
    /// <see cref="Clear"/>.
    /// </summary>
    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    /// <summary>
    /// True from the start of a load until the latest one ends (applied, no maps or failed) or <see cref="Clear"/> runs.
    /// It turns false after <see cref="Loaded"/> is raised.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>True once a load applied a facility's map list; false before and after <see cref="Clear"/>.</summary>
    public bool HasCatalogue
    {
        get => _hasCatalogue;
        private set => SetProperty(ref _hasCatalogue, value);
    }

    /// <summary>The facility's video maps in the server's order.</summary>
    public IReadOnlyList<ScopeMapInfo> Maps => _maps;

    /// <summary>Brightness category ("A" or "B") by map id.</summary>
    public IReadOnlyDictionary<string, string> BrightnessLookup => _brightnessLookup;

    /// <summary>The map group's STARS ids: the position's group when one was applied, else the facility's first group, else empty.</summary>
    public IReadOnlyList<int?> MapGroupMapIds => _positionMapGroupMapIds ?? _dtoMapGroupMapIds ?? [];

    /// <summary>The airports under the active position's STARS area, whose weather the scope shows.</summary>
    public IReadOnlyList<string> WeatherAirports { get; private set; } = [];

    /// <summary>The ARTCC key the loaded maps' favourites are read under (upper case); null before a load.</summary>
    public string? ActiveArtccId { get; private set; }

    /// <summary>The airport key the loaded maps' favourites are read under; null when the load named none.</summary>
    public string? ActiveAirportId { get; private set; }

    /// <summary>The scenario key the loaded maps' favourites are read under; null when the load named none.</summary>
    public string? ActiveScenarioId { get; private set; }

    /// <summary>Counts load starts and clears; only the latest load applies its maps and marks the source ready.</summary>
    internal int Generation { get; private set; }

    /// <summary>
    /// Raised once per load that applied the server's map list, after its maps were downloaded or their download failed,
    /// unless a newer load or a clear began since.
    /// </summary>
    public event EventHandler? Loaded;

    /// <summary>Raised by <see cref="Clear"/>.</summary>
    public event EventHandler? Cleared;

    /// <summary>Loads the facility's video maps for <paramref name="artccId"/> and downloads them; a failure is logged, never thrown.</summary>
    public Task LoadAsync(string artccId, string? airportId, string? scenarioId) =>
        RunLoadAsync(
            BeginLoad(),
            new LoadKeys(artccId, airportId, scenarioId),
            () => _connection.GetFacilityVideoMapsForArtccAsync(artccId, airportId),
            maps => _videoMapService.LoadMapsAsync(artccId, maps)
        );

    /// <summary>
    /// Applies a scenario activation: its primary airport, the facility's maps when an ARTCC is set, and a STARS position's
    /// display config.
    /// </summary>
    public void ApplyScenarioBootstrap(ScenarioBootstrap bootstrap, string? artccId)
    {
        SetPrimaryAirportId(bootstrap.PrimaryAirportId);

        if (!string.IsNullOrEmpty(artccId))
        {
            _ = LoadAsync(artccId, bootstrap.PrimaryAirportId, bootstrap.ScenarioId);
        }

        if (bootstrap.PositionDisplayConfig is { NoStarsDisplay: false })
        {
            ApplyPositionDisplayConfig(bootstrap.PositionDisplayConfig);
        }
    }

    /// <summary>Takes the position's map group, which wins over the facility's, and its weather airports.</summary>
    public void ApplyPositionDisplayConfig(PositionDisplayConfigDto config)
    {
        _positionMapGroupMapIds = config.MapGroupMapIds;
        WeatherAirports = config.UnderlyingAirports;
        _log.LogInformation(
            "Scope applied position display config for TCP {TcpCode}: {MapCount} maps, {AirportCount} weather airports",
            config.TcpCode,
            config.MapGroupMapIds.Count,
            config.UnderlyingAirports.Count
        );
    }

    /// <summary>Sets the scenario's primary airport id.</summary>
    public void SetPrimaryAirportId(string? id) => PrimaryAirportId = id;

    /// <summary>Records the primary airport's position, which the next load centres on.</summary>
    public void SetPrimaryAirportPosition(double lat, double lon) => _primaryAirportPosition = new LatLon(lat, lon);

    /// <summary>
    /// Records the primary airport's position once it becomes known late, and centres on it while the centre is still the
    /// area fallback a load put there; a centre changed since is left alone.
    /// </summary>
    public void RecenterOnPrimaryAirport(double lat, double lon)
    {
        SetPrimaryAirportPosition(lat, lon);
        if ((_areaFallbackCenter is not { } fallback) || (CenterLat != fallback.Lat) || (CenterLon != fallback.Lon))
        {
            return;
        }

        _areaFallbackCenter = null;
        CenterLat = lat;
        CenterLon = lon;
    }

    /// <summary>Drops the loaded catalogue and the scenario's defaults, ends any load in flight, and raises <see cref="Cleared"/>.</summary>
    public void Clear()
    {
        IsReady = false;
        Generation++;
        _maps.Clear();
        _brightnessLookup.Clear();
        MvaFacilityId = null;
        _positionMapGroupMapIds = null;
        _dtoMapGroupMapIds = null;
        WeatherAirports = [];
        _videoMapService.ClearMemoryCache();
        PrimaryAirportId = null;
        ShowMvaHintsDefault = false;
        HasCatalogue = false;
        IsLoading = false;
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the airport-elevation lookup <see cref="GetFieldElevation"/> reads.</summary>
    public void SetElevationLookup(Func<string, double?> lookup) => _getAirportElevation = lookup;

    /// <summary>Marks the navigation database ready and builds <see cref="Fixes"/> from it.</summary>
    public void SetNavDbReady() => Fixes = BuildChartFixes(true);

    /// <summary>The destination's field elevation, else the primary airport's, else 0.</summary>
    public double GetFieldElevation(string? destination) => ResolveFieldElevation(_getAirportElevation, destination, PrimaryAirportId);

    /// <summary>
    /// The point as an FRD from the nearest fix; null when the fixes or the navigation database are not loaded, or no fix is
    /// in range.
    /// </summary>
    public string? DescribePoint(double lat, double lon) => ToFrd(Fixes, lat, lon);

    /// <summary>
    /// The point named by <see cref="FrdResolver.ToFrd"/> over <paramref name="fixes"/> and the loaded nav database; null when
    /// the fixes or the nav database are not loaded yet, or no fix is in range.
    /// </summary>
    internal static string? ToFrd(IReadOnlyList<(string Name, double Lat, double Lon)>? fixes, double lat, double lon) =>
        (fixes is not null) && (NavigationDatabase.InstanceOrNull is { } navDb) ? FrdResolver.ToFrd(lat, lon, fixes, navDb) : null;

    /// <summary>
    /// The navigation database's chartable fixes: every fix with a position, less the adapted fixes whose identifiers are
    /// themselves FRD strings. Empty when the database is not ready.
    /// </summary>
    internal static IReadOnlyList<(string Name, double Lat, double Lon)> BuildChartFixes(bool navDbReady)
    {
        if (!navDbReady)
        {
            return [];
        }

        string[] names = NavigationDatabase.Instance.AllFixNames;
        var result = new List<(string, double, double)>(names.Length);
        foreach (string name in names)
        {
            // vNAS publishes thousands of adapted fixes whose identifiers are themselves FRD strings
            // (OAK169001). They stay typeable in the command bar, but they aren't chartable waypoints, so
            // they neither paint on the scope nor anchor a deduced FRD.
            if (FrdResolver.IsFrdIdentifier(name))
            {
                continue;
            }

            (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetFixPosition(name);
            if (pos.HasValue)
            {
                result.Add((name, pos.Value.Lat, pos.Value.Lon));
            }
        }

        return result;
    }

    /// <summary>The destination's field elevation from <paramref name="lookup"/>, else the primary airport's, else 0.</summary>
    internal static double ResolveFieldElevation(Func<string, double?>? lookup, string? destination, string? primaryAirportId)
    {
        if (lookup is null)
        {
            return 0;
        }

        if (!string.IsNullOrEmpty(destination) && (lookup(destination) is { } destinationElevation))
        {
            return destinationElevation;
        }

        if (!string.IsNullOrEmpty(primaryAirportId) && (lookup(primaryAirportId) is { } primaryElevation))
        {
            return primaryElevation;
        }

        return 0;
    }

    /// <summary>
    /// Applies a facility's video-map list as a whole load would, with no server round trip or download, and raises
    /// <see cref="Loaded"/>. Test seam.
    /// </summary>
    /// <param name="dto">The facility's maps; its ARTCC id is the favourites' ARTCC key.</param>
    /// <param name="airportId">The airport key a load would have been given.</param>
    /// <param name="scenarioId">The scenario key a load would have been given.</param>
    internal void ApplyVideoMapsDtoForTesting(FacilityVideoMapsDto dto, string? airportId, string? scenarioId) =>
        RunLoadAsync(
                BeginLoad(),
                new LoadKeys(dto.ArtccId, airportId, scenarioId),
                () => Task.FromResult<FacilityVideoMapsDto?>(dto),
                _ => Task.FromResult(new List<VideoMapData>())
            )
            .GetAwaiter()
            .GetResult();

    /// <summary>Starts a load as <see cref="LoadAsync"/> does and returns its token for <see cref="FinishLoadForTesting"/>. Test seam.</summary>
    internal int BeginLoadForTesting() => BeginLoad();

    /// <summary>
    /// Ends the load <paramref name="token"/> started as <see cref="LoadAsync"/> would once the server answered with
    /// <paramref name="dto"/> (null for no maps), its download then failing with <paramref name="downloadFailure"/> when one
    /// is given. Test seam.
    /// </summary>
    internal void FinishLoadForTesting(int token, FacilityVideoMapsDto? dto, Exception? downloadFailure) =>
        RunLoadAsync(
                token,
                new LoadKeys(dto?.ArtccId ?? "", null, null),
                () => Task.FromResult(dto),
                _ => (downloadFailure is null) ? Task.FromResult(new List<VideoMapData>()) : Task.FromException<List<VideoMapData>>(downloadFailure)
            )
            .GetAwaiter()
            .GetResult();

    /// <summary>Sets <see cref="Fixes"/> as the navigation database would. Test seam.</summary>
    internal void SetFixesForTesting(IReadOnlyList<(string Name, double Lat, double Lon)> fixes) => Fixes = fixes;

    private int BeginLoad()
    {
        IsReady = false;
        IsLoading = true;
        return ++Generation;
    }

    /// <summary>
    /// Fetches the facility's map list, applies it when this is still the latest load, then downloads its maps; a failure is
    /// logged. The load ends in <see cref="FinishLoad"/> whatever happened.
    /// </summary>
    private async Task RunLoadAsync(
        int load,
        LoadKeys keys,
        Func<Task<FacilityVideoMapsDto?>> fetch,
        Func<IReadOnlyList<VideoMapInfoDto>, Task<List<VideoMapData>>> download
    )
    {
        bool applied = false;
        try
        {
            FacilityVideoMapsDto? dto = await fetch();

            // A newer load or a clear since this one began owns the catalogue instead.
            if (load != Generation)
            {
                return;
            }

            if (dto is null)
            {
                _log.LogWarning("No video maps for {Artcc}", keys.ArtccId);
                return;
            }

            SetFavoriteKeys(keys);
            ApplyVideoMapsDto(dto);
            applied = true;
            List<VideoMapData> data = await download(dto.VideoMaps);
            _log.LogInformation("Scope video maps loaded: {Count} maps for {Facility}", data.Count, dto.FacilityId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load scope video maps for {Artcc}", keys.ArtccId);
        }
        finally
        {
            FinishLoad(load, applied);
        }
    }

    private void FinishLoad(int load, bool applied)
    {
        // A newer load or a clear since this one began owns the ready and loading flags instead.
        if (load != Generation)
        {
            return;
        }

        IsReady = true;
        if (applied)
        {
            Loaded?.Invoke(this, EventArgs.Empty);
        }

        // Last, so a listener that holds work until the load ends sees the Loaded handlers' result.
        IsLoading = false;
    }

    private void SetFavoriteKeys(LoadKeys keys)
    {
        ActiveArtccId = string.IsNullOrWhiteSpace(keys.ArtccId) ? null : keys.ArtccId.ToUpperInvariant();
        ActiveAirportId = MainViewModel.NormalizeFavoriteAirportId(keys.AirportId);
        ActiveScenarioId = keys.ScenarioId;
    }

    private void ApplyVideoMapsDto(FacilityVideoMapsDto dto)
    {
        MvaFacilityId = string.IsNullOrWhiteSpace(dto.FacilityId) ? null : dto.FacilityId;
        _brightnessLookup.Clear();
        _maps.Clear();
        foreach (VideoMapInfoDto map in dto.VideoMaps)
        {
            _brightnessLookup[map.Id] = map.BrightnessCategory;
            _maps.Add(
                new ScopeMapInfo(
                    map.Id,
                    map.ShortName,
                    map.Name,
                    map.BrightnessCategory,
                    map.StarsId,
                    map.TdmOnly,
                    IsFavorite(FavoriteMapScope.Artcc, ActiveArtccId, map.Id),
                    IsFavorite(FavoriteMapScope.Airport, ActiveAirportId, map.Id),
                    IsFavorite(FavoriteMapScope.Scenario, ActiveScenarioId, map.Id)
                )
            );
        }

        _dtoMapGroupMapIds = dto.MapGroups.Count > 0 ? dto.MapGroups[0].MapIds : null;
        ApplyLoadCenter(dto);
        HasCatalogue = true;
    }

    private bool IsFavorite(FavoriteMapScope scope, string? key, string mapId) =>
        (key is not null) && _preferences.IsFavoriteVideoMap(scope, key, mapId);

    // Prefer the primary airport; fall back to the first STARS area's centre, remembered so a late airport position can replace it.
    private void ApplyLoadCenter(FacilityVideoMapsDto dto)
    {
        _areaFallbackCenter = null;
        if ((_primaryAirportPosition.Lat != 0) || (_primaryAirportPosition.Lon != 0))
        {
            CenterLat = _primaryAirportPosition.Lat;
            CenterLon = _primaryAirportPosition.Lon;
        }
        else if (dto.Areas.Count > 0)
        {
            StarsAreaDto area = dto.Areas[0];
            CenterLat = area.CenterLat;
            CenterLon = area.CenterLon;
            _areaFallbackCenter = new LatLon(area.CenterLat, area.CenterLon);
        }
    }

    /// <summary>The keys a load reads the maps' favourites under.</summary>
    private sealed record LoadKeys(string ArtccId, string? AirportId, string? ScenarioId);
}
