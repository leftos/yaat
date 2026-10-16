using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Weather;

namespace Yaat.Client.ViewModels;

/// <summary>A GFS wind grid the active weather names: its raw GRIB2 bytes and the hash the server stored them under.</summary>
public sealed record ActiveWindGrid(byte[] Grib2, string Hash);

/// <summary>Weather JSON ready for <c>LoadWeather</c>, with the grid it names, if any.</summary>
public sealed record PreparedWeather(string Json, ActiveWindGrid? WindGrid);

/// <summary>One airport's raw METAR string, as broadcast for the currently active weather.</summary>
public record MetarEntry(string? StationId, string Raw, bool IsFavorite, bool CanFavorite);

public record WeatherDisplayInfo(
    string? StationId,
    int? WindDirectionDeg,
    int? WindSpeedKts,
    int? WindGustKts,
    double? AltimeterInHg,
    // Cloud ceiling (lowest BKN/OVC base) in feet AGL, or null when clear / scattered-only. The ground
    // view keeps an aircraft visible until it climbs through the ceiling (or 6,000 ft AGL if clear).
    int? CeilingFeetAgl = null,
    // The METAR dddVddd variable-direction extremes (clockwise), when the report carries them.
    int? WindVarFromDeg = null,
    int? WindVarToDeg = null
)
{
    public string ToDisplayString()
    {
        var parts = new List<string>(4);

        if (StationId is not null)
        {
            parts.Add(StationId);
        }

        if (AltimeterInHg is not null)
        {
            parts.Add($"{AltimeterInHg:F2}");
        }

        if (WindDirectionDeg is not null || WindSpeedKts is not null)
        {
            // A parsed VRB (variable-direction) wind has no numeric direction; render the METAR "VRB" token.
            string direction = WindDirectionDeg is { } dir ? $"{dir:D3}" : "VRB";
            string wind = $"{direction}{WindSpeedKts:D2}";
            if (WindGustKts is not null)
            {
                wind += $"G{WindGustKts:D2}";
            }

            wind += "KT";
            parts.Add(wind);

            if (WindVarFromDeg is { } varFrom && WindVarToDeg is { } varTo)
            {
                parts.Add($"{varFrom:D3}V{varTo:D3}");
            }
        }

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Weather loading and clearing commands, and the WeatherChanged event handler.
/// </summary>
public partial class MainViewModel
{
    private readonly LiveWeatherService _liveWeather = new();
    private readonly GfsGridFetcher _gfsGrid = new(new HttpClientHandler(), GfsGridFetcher.DefaultRequestTimeout);
    private readonly ArtccAirportResolver _airportResolver = new();

    private string? _activeWeatherJson;

    // The GFS grid the active weather JSON names, kept so Save Weather can embed its bytes.
    private ActiveWindGrid? _activeWindGrid;
    private IReadOnlyList<WeatherDisplayInfo>? _allWeatherInfo;
    private IReadOnlyList<string>? _lastPopulatedMetars;

    /// <summary>Raw METAR strings for the active weather, one per airport, shown in the METAR window.</summary>
    public ObservableCollection<MetarEntry> Metars { get; } = [];

    public string? ActiveWeatherJson => _activeWeatherJson;

    public bool HasActiveWeather => _activeWeatherJson is not null;

    private void SetActiveWeatherJson(string? json, ActiveWindGrid? windGrid)
    {
        _activeWeatherJson = json;
        _activeWindGrid = windGrid;
        OnPropertyChanged(nameof(ActiveWeatherJson));
        OnPropertyChanged(nameof(HasActiveWeather));
        SaveWeatherCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteInRoom))]
    private async Task LoadWeatherAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            _log.LogInformation("Loading weather from {Path}", filePath);
            string fileJson = await File.ReadAllTextAsync(filePath);
            PreparedWeather prepared = await PrepareSavedWeatherAsync(fileJson);
            CommandResultDto result = await _connection.LoadWeatherAsync(prepared.Json, reconstructMetars: true);

            if (result.Success)
            {
                StatusText = result.Message ?? "Weather loaded";
                string name = Path.GetFileNameWithoutExtension(filePath);
                _preferences.AddRecentWeather(filePath, name);
                SetActiveWeatherJson(prepared.Json, prepared.WindGrid);
            }
            else
            {
                StatusText = result.Message ?? "Weather load failed";
                _log.LogWarning("Weather load failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Weather load error");
            StatusText = $"Weather error: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads weather from pre-fetched JSON (e.g. from the vNAS data API or editor).
    /// </summary>
    public async Task LoadWeatherFromJsonAsync(string json, string displayName, string? apiId = null)
    {
        try
        {
            _log.LogInformation("Loading weather from API: {Name}", displayName);
            PreparedWeather prepared = await PrepareSavedWeatherAsync(json);
            CommandResultDto result = await _connection.LoadWeatherAsync(prepared.Json, reconstructMetars: true);

            if (result.Success)
            {
                StatusText = result.Message ?? $"Weather loaded: {displayName}";
                if (apiId is not null)
                {
                    _preferences.AddRecentWeather("", displayName, apiId);
                }
                SetActiveWeatherJson(prepared.Json, prepared.WindGrid);
            }
            else
            {
                StatusText = result.Message ?? "Weather load failed";
                _log.LogWarning("Weather load failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Weather load error");
            StatusText = $"Weather error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadLiveWeather))]
    private async Task LoadLiveWeatherAsync()
    {
        try
        {
            StatusText = "Fetching live weather...";
            string artccId = _preferences.ArtccId;
            IReadOnlyList<string> airportIds = await _airportResolver.GetAirportIdsAsync(artccId);
            if (airportIds.Count == 0)
            {
                StatusText = "No airports found for ARTCC";
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            using var gridCancel = new CancellationTokenSource();
            Task<GfsGridFetchResult> gridTask = _gfsGrid.FetchAsync(artccId, nowUtc, gridCancel.Token);
            WeatherProfile? profile = await BuildLiveWeatherBesideGridAsync(artccId, airportIds, gridTask, gridCancel);
            if (profile is null)
            {
                StatusText = "Failed to fetch live weather data";
                return;
            }

            if (profile.Metars.Count == 0)
            {
                AddWarningEntry("Live weather: METARs unavailable — winds aloft only. Altimeter and surface wind will use defaults.");
            }

            ActiveWindGrid? windGrid = await AwaitLiveWindGridAsync(profile, gridTask, nowUtc);

            string json = JsonSerializer.Serialize(profile);
            // Live-fetched real METARs are left untouched (no dynamic reconstruction).
            CommandResultDto result = await _connection.LoadWeatherAsync(json, reconstructMetars: false);

            if (result.Success)
            {
                StatusText = result.Message ?? $"Loaded: {profile.Name}";
                SetActiveWeatherJson(json, windGrid);
            }
            else
            {
                StatusText = result.Message ?? "Live weather load failed";
                _log.LogWarning("Live weather load failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Live weather error");
            StatusText = $"Live weather error: {ex.Message}";
        }
    }

    /// <summary>
    /// The AWC weather, built while the grid fetch runs. When there is no weather (null or a throw), the grid fetch is cancelled
    /// and awaited, so its GETs stop and its outcome is logged.
    /// </summary>
    private async Task<WeatherProfile?> BuildLiveWeatherBesideGridAsync(
        string artccId,
        IReadOnlyList<string> airportIds,
        Task<GfsGridFetchResult> gridTask,
        CancellationTokenSource gridCancel
    )
    {
        WeatherProfile? profile;
        try
        {
            profile = await _liveWeather.BuildLiveWeatherAsync(artccId, airportIds);
        }
        catch
        {
            await StopGridFetchAsync(gridTask, gridCancel);
            throw;
        }

        if (profile is null)
        {
            await StopGridFetchAsync(gridTask, gridCancel);
        }

        return profile;
    }

    private async Task StopGridFetchAsync(Task<GfsGridFetchResult> gridTask, CancellationTokenSource gridCancel)
    {
        await gridCancel.CancelAsync();
        try
        {
            GfsGridFetchResult dropped = await gridTask;
            _log.LogInformation("GFS grid fetch stopped without live weather to go with it ({Outcome})", dropped.FailureReason ?? "fetched");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GFS grid fetch faulted");
        }
    }

    /// <summary>
    /// Awaits the grid fetch (showing the GFS status line while it is still running) and, for live-shaped weather, uploads it and
    /// names it on <paramref name="profile"/>. Other weather drops the bytes without a warning.
    /// </summary>
    private async Task<ActiveWindGrid?> AwaitLiveWindGridAsync(WeatherProfile profile, Task<GfsGridFetchResult> gridTask, DateTime nowUtc)
    {
        if (!gridTask.IsCompleted)
        {
            DateTime cycle = GfsGridFetcher.NewestCycle(nowUtc);
            StatusText = GfsGridFetcher.FetchingStatus(cycle, GfsGridFetcher.ForecastHour(cycle, nowUtc));
        }

        GfsGridFetchResult grid = await gridTask;
        return WindGridRegistry.IsLiveWeather(profile) ? await AttachLiveWindGridAsync(profile, grid) : null;
    }

    /// <summary>
    /// Uploads the fetched grid and names it on <paramref name="profile"/>; null (after the GFS warning) when there is none to name.
    /// </summary>
    private async Task<ActiveWindGrid?> AttachLiveWindGridAsync(WeatherProfile profile, GfsGridFetchResult grid)
    {
        if (grid.Grib2 is not { } grib2)
        {
            WarnGfsUnavailable(grid.FailureReason ?? throw new InvalidOperationException("GFS grid fetch returned neither a grid nor a reason"));
            return null;
        }

        string? hash = await UploadWindGridAsync(grib2);
        if (hash is null)
        {
            return null;
        }

        profile.WindGridHash = hash;
        return new ActiveWindGrid(grib2, hash);
    }

    /// <summary>
    /// Readies a saved or API weather JSON for <c>LoadWeather</c>: an embedded grid is uploaded first and its server hash named;
    /// a grid that is missing, corrupt or refused leaves the JSON naming none, with the GFS warning. The JSON returned never
    /// carries the embedded bytes.
    /// </summary>
    private async Task<PreparedWeather> PrepareSavedWeatherAsync(string json)
    {
        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(json);
        if (extract.GridMissing)
        {
            WarnGfsUnavailable(SavedWeatherGrid.NotInSavedFile);
            return new PreparedWeather(extract.Json, null);
        }

        if (extract.Grib2 is not { } grib2)
        {
            return new PreparedWeather(extract.Json, null);
        }

        string? hash = await UploadWindGridAsync(grib2);
        if (hash is null)
        {
            return new PreparedWeather(SavedWeatherGrid.WithHash(extract.Json, null), null);
        }

        string? savedHash = SavedWeatherGrid.NamedHash(extract.Json);
        if (!string.Equals(savedHash, hash, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "Saved weather names GFS wind grid {SavedHash}, but the server stored its bytes as {Hash}; using the server's",
                savedHash,
                hash
            );
        }

        return new PreparedWeather(SavedWeatherGrid.WithHash(extract.Json, hash), new ActiveWindGrid(grib2, hash));
    }

    /// <summary>Uploads a grid within the client cap; returns the server's hash, or null after the GFS warning.</summary>
    private async Task<string?> UploadWindGridAsync(byte[] grib2)
    {
        if (grib2.Length > GfsGridFetcher.MaxWindGridUploadBytes)
        {
            WarnGfsUnavailable(GfsGridFetcher.OverUploadLimit);
            return null;
        }

        string hash;
        try
        {
            hash = await _connection.UploadWindGridAsync(grib2);
        }
        catch (HubException ex)
        {
            WarnGfsUnavailable(GfsGridFetcher.RefusalReason(ex.Message));
            return null;
        }

        string localHash = WindGridRegistry.HashOf(grib2);
        if (!string.Equals(localHash, hash, StringComparison.Ordinal))
        {
            _log.LogWarning("GFS wind grid hashed {LocalHash} here but the server returned {Hash}; using the server's", localHash, hash);
        }

        return hash;
    }

    private void WarnGfsUnavailable(string reason)
    {
        string warning = GfsGridFetcher.WarningText(reason);
        _log.LogWarning("{Warning}", warning);
        AddWarningEntry(warning);
    }

    private bool CanLoadLiveWeather() =>
        IsConnected && ActiveRoomId is not null && !string.IsNullOrWhiteSpace(_preferences.ArtccId) && _commandInput.NavDbReady;

    [RelayCommand(CanExecute = nameof(CanClearWeather))]
    private async Task ClearWeatherAsync()
    {
        try
        {
            await _connection.ClearWeatherAsync();
            StatusText = "Weather cleared";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clear weather error");
            StatusText = $"Weather error: {ex.Message}";
        }
    }

    private bool CanClearWeather() => CanExecuteInRoom && ActiveWeatherName is not null;

    [RelayCommand(CanExecute = nameof(HasActiveWeather))]
    private async Task SaveWeatherAsync()
    {
        if (_activeWeatherJson is null)
        {
            return;
        }

        string sanitized = SanitizeFileName(ActiveWeatherName ?? "weather");
        string? path = await _filePicker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Save Weather As…",
                SuggestedFileName: sanitized,
                Filters: [new FilePickerFilter("JSON", ["*.json"])],
                DefaultExtension: "json"
            )
        );

        if (path is null)
        {
            return;
        }

        try
        {
            string contents = _activeWindGrid is { } windGrid ? SavedWeatherGrid.Embed(_activeWeatherJson, windGrid.Grib2) : _activeWeatherJson;
            await File.WriteAllTextAsync(path, contents);
            string name = Path.GetFileNameWithoutExtension(path);
            _preferences.AddRecentWeather(path, name);
            StatusText = $"Weather saved: {name}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save weather error");
            StatusText = $"Save error: {ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sanitized = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            sanitized.Append(invalid.Contains(c) ? '_' : c);
        }
        return sanitized.ToString();
    }

    private void OnWeatherChanged(WeatherChangedDto dto) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyWeatherChanged(dto));

    /// <summary>Applies a room weather change to the METAR list and every radar and ground view. Runs on the UI thread.</summary>
    internal void ApplyWeatherChanged(WeatherChangedDto dto)
    {
        ActiveWeatherName = dto.Name;

        if (dto.Name is null)
        {
            SetActiveWeatherJson(null, null);
            ApplyDefaultWeatherIfNoWeather();
        }
        else
        {
            // The room's weather keeps the grid bytes only while it names the same grid.
            bool sameGrid = (_activeWindGrid is { } held) && SavedWeatherGrid.NamesGrid(dto.SourceJson, held.Hash);
            SetActiveWeatherJson(dto.SourceJson, sameGrid ? _activeWindGrid : null);

            IReadOnlyList<WeatherDisplayInfo>? allInfo = ExtractAllWeatherDisplay(dto.Metars);
            _allWeatherInfo = allInfo;
            ApplyWeatherToAllViews(allInfo);
            PopulateMetars(dto.Metars);
        }
    }

    public void PopulateMetars(IReadOnlyList<string>? metars)
    {
        _lastPopulatedMetars = metars;
        Metars.Clear();
        if (metars is null)
        {
            return;
        }

        string? scenarioId = ActiveScenarioId;
        var entries = new List<MetarEntry>(metars.Count);
        foreach (string raw in metars)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Strip K prefix for US ICAO stations (KOAK → OAK) for the display label.
            string? stationId = MetarParser.Parse(raw)?.StationId;
            if (stationId is { Length: 4 } && stationId.StartsWith('K'))
            {
                stationId = stationId[1..];
            }

            bool canFavorite = (stationId is not null) && !string.IsNullOrEmpty(scenarioId);
            bool isFavorite = canFavorite && _preferences.IsFavoriteMetarStation(scenarioId!, stationId!);
            entries.Add(new MetarEntry(stationId, raw.Trim(), isFavorite, canFavorite));
        }

        // Favorited stations first, then alphabetical within each group; entries whose
        // METAR failed to parse (no station id) sort last, keeping broadcast order.
        foreach (
            MetarEntry? entry in entries
                .OrderByDescending(e => e.IsFavorite)
                .ThenBy(e => e.StationId is null)
                .ThenBy(e => e.StationId, StringComparer.OrdinalIgnoreCase)
        )
        {
            Metars.Add(entry);
        }
    }

    /// <summary>
    /// Re-sorts the METAR list from the last broadcast, picking up the active scenario's
    /// favorites. Called after a favorite toggle and on every scenario transition.
    /// </summary>
    private void RefreshMetarOrdering()
    {
        if (_lastPopulatedMetars is not null)
        {
            PopulateMetars(_lastPopulatedMetars);
        }
    }

    [RelayCommand]
    private void ToggleMetarFavorite(MetarEntry entry)
    {
        string? scenarioId = ActiveScenarioId;
        if ((entry.StationId is null) || string.IsNullOrEmpty(scenarioId))
        {
            return;
        }

        _preferences.SetFavoriteMetarStation(scenarioId, entry.StationId, !entry.IsFavorite);
        RefreshMetarOrdering();
    }

    private static IReadOnlyList<WeatherDisplayInfo>? ExtractAllWeatherDisplay(IReadOnlyList<string>? metars)
    {
        if (metars is null || metars.Count == 0)
        {
            return null;
        }

        var list = new List<WeatherDisplayInfo>(metars.Count);
        foreach (string raw in metars)
        {
            MetarParser.ParsedMetar? parsed = MetarParser.Parse(raw);
            if (parsed is null)
            {
                continue;
            }

            // Key the station by the FAA id the layouts, airport pickers and position configs carry: a
            // 4-letter ICAO loses its K or P prefix (KOAK → OAK, PHNL → HNL).
            if (AirportAirlines.NormalizeAirportId(parsed.StationId) is not { } displayId)
            {
                continue;
            }

            list.Add(
                new WeatherDisplayInfo(
                    displayId,
                    parsed.WindDirectionDeg,
                    parsed.WindSpeedKts,
                    parsed.WindGustKts,
                    parsed.AltimeterInHg,
                    parsed.CeilingFeetAgl,
                    parsed.WindVarFromDeg,
                    parsed.WindVarToDeg
                )
            );
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// The report for the airport a Ground View depicts, or null when the loaded weather carries no station
    /// for it (or the view's airport is not known yet). Never another airport's report: the view then shows
    /// <see cref="GroundWeatherNote"/> in place of its weather readout. Both ids are normalized, so a
    /// P-prefixed METAR (PHNL) matches the layout's FAA id (HNL).
    /// </summary>
    private static WeatherDisplayInfo? PickGroundWeather(IReadOnlyList<WeatherDisplayInfo>? allInfo, string? airportId)
    {
        string? normalized = AirportAirlines.NormalizeAirportId(airportId);
        if (allInfo is null || normalized is null)
        {
            return null;
        }

        foreach (WeatherDisplayInfo info in allInfo)
        {
            if (string.Equals(AirportAirlines.NormalizeAirportId(info.StationId), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>
    /// The note a Ground View shows instead of a weather readout when the loaded weather carries no station
    /// for the airport it depicts. Null when there is no weather to be missing, when the view's airport is not
    /// known, or when its station is present.
    /// </summary>
    private static string? GroundWeatherNote(IReadOnlyList<WeatherDisplayInfo>? allInfo, string? airportId)
    {
        if (allInfo is not { Count: > 0 } || string.IsNullOrWhiteSpace(airportId))
        {
            return null;
        }

        return PickGroundWeather(allInfo, airportId) is null ? $"No METAR for {airportId}" : null;
    }

    /// <summary>
    /// The note a Radar View shows instead of a weather readout when the loaded weather carries no station
    /// for any of the position's underlying airports. Null when there is no weather to be missing, when no
    /// position filter is set, or when a station matched.
    /// </summary>
    private static string? RadarWeatherNote(IReadOnlyList<WeatherDisplayInfo>? allInfo, List<string> weatherAirports)
    {
        if (allInfo is not { Count: > 0 } || weatherAirports.Count == 0)
        {
            return null;
        }

        return FilterWeatherForPosition(allInfo, weatherAirports) is { Count: > 0 } ? null : $"No METAR for {string.Join(" ", weatherAirports)}";
    }

    /// <summary>
    /// Filters weather display info to only stations matching the position's underlying airports. Returns all
    /// stations if no position filter is set, and none when the filter matches nothing (the view then shows
    /// <see cref="RadarWeatherNote"/> in place of its weather readout). Both sides are normalized, so a
    /// P-prefixed METAR (PHNL) matches the position's FAA id (HNL).
    /// </summary>
    private static IReadOnlyList<WeatherDisplayInfo>? FilterWeatherForPosition(
        IReadOnlyList<WeatherDisplayInfo>? allInfo,
        List<string> weatherAirports
    )
    {
        if (allInfo is null)
        {
            return null;
        }

        if (weatherAirports.Count == 0)
        {
            return allInfo;
        }

        var filtered = new List<WeatherDisplayInfo>();
        foreach (WeatherDisplayInfo info in allInfo)
        {
            string? stationId = AirportAirlines.NormalizeAirportId(info.StationId);
            if (
                stationId is not null
                && weatherAirports.Any(a => string.Equals(AirportAirlines.NormalizeAirportId(a), stationId, StringComparison.OrdinalIgnoreCase))
            )
            {
                filtered.Add(info);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Re-applies weather filtering when the active position changes.
    /// Called from <see cref="OnPositionDisplayChanged"/>, and after a scenario unload drops the filter.
    /// </summary>
    internal void UpdateRadarWeatherDisplay()
    {
        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.WeatherInfo = FilterWeatherForPosition(_allWeatherInfo, radar.WeatherAirports);
            radar.WeatherNote = RadarWeatherNote(_allWeatherInfo, radar.WeatherAirports);
        }
    }

    /// <summary>
    /// Pushes a freshly-extracted weather set to every Radar and Ground View instance. Each radar filters
    /// by its own position's underlying airports and each ground view picks the report for the airport it
    /// is showing, so the instances can legitimately end up displaying different stations.
    /// </summary>
    private void ApplyWeatherToAllViews(IReadOnlyList<WeatherDisplayInfo>? allInfo)
    {
        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.WeatherInfo = FilterWeatherForPosition(allInfo, radar.WeatherAirports);
            radar.WeatherNote = RadarWeatherNote(allInfo, radar.WeatherAirports);
        }

        foreach (GroundViewModel ground in AllGroundViews)
        {
            ground.WeatherInfo = PickGroundWeather(allInfo, ground.Layout?.AirportId);
            ground.WeatherNote = GroundWeatherNote(allInfo, ground.Layout?.AirportId);
        }
    }

    /// <summary>
    /// When no weather profile is loaded, fills the METAR view AND the radar/ground per-airport
    /// wind/altimeter overlays with a standard default report (calm wind, 10SM, clear, 29.92) for
    /// each scenario airport, so every weather surface reflects the calm/standard conditions the sim
    /// already applies rather than showing nothing. No-op when real weather is loaded.
    /// </summary>
    private void ApplyDefaultWeatherIfNoWeather()
    {
        if (HasActiveWeather)
        {
            return;
        }

        DateTime now = WallClock.GetUtcNow().UtcDateTime;
        var defaults = new List<string>();
        foreach (string icao in CollectScenarioAirportIcaos())
        {
            defaults.Add(DefaultMetar.Build(icao, now));
        }

        PopulateMetars(defaults);

        IReadOnlyList<WeatherDisplayInfo>? allInfo = ExtractAllWeatherDisplay(defaults);
        _allWeatherInfo = allInfo;
        ApplyWeatherToAllViews(allInfo);
    }

    private List<string> CollectScenarioAirportIcaos()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            string upper = id.Trim().ToUpperInvariant();
            string icao = upper.Length == 3 ? "K" + upper : upper;
            // The same airport arrives in either form (a position's "HNL", a scenario's "PHNL"): key the
            // dedupe on the normalized id so one airport gets one default report, not two.
            if (seen.Add(AirportAirlines.NormalizeAirportId(icao) ?? icao))
            {
                result.Add(icao);
            }
        }

        foreach (string airport in Radar.WeatherAirports)
        {
            Add(airport);
        }

        Add(ActiveScenarioPrimaryAirportId);

        // Every Ground View's own airport as well: with no profile loaded each view's overlay shows the
        // synthetic fair-weather report for the airport it depicts, which exists only if it is listed here.
        foreach (GroundViewModel ground in AllGroundViews)
        {
            Add(ground.Layout?.AirportId);
        }

        return result;
    }
}
