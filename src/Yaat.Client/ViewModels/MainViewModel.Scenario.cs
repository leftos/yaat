using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Discord;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Scenario loading, difficulty selection, and unloading.
/// </summary>
public partial class MainViewModel
{
    // Pending scenario source: either a file path or pre-fetched JSON from the API.
    private string? _pendingScenarioSource;
    private string? _pendingApiScenarioId;

    // When the active scenario started, as Discord counts it: a joiner's is back-dated by the room's
    // elapsed time so the timer on their profile matches everyone else's rather than restarting at zero.
    private long _richPresenceStartUnixSeconds;

    /// <summary>
    /// Where the active scenario is published as the user's Discord status. Null when the app runs
    /// without the service — headless test hosts, and any host that does not construct it.
    /// </summary>
    public IRichPresencePublisher? RichPresence { get; set; }

    [RelayCommand(CanExecute = nameof(CanLoadScenario))]
    private async Task LoadScenarioAsync()
    {
        if (string.IsNullOrWhiteSpace(ScenarioFilePath) && string.IsNullOrWhiteSpace(_pendingScenarioSource))
        {
            StatusText = "No scenario selected";
            return;
        }

        if (ActiveScenarioId is not null)
        {
            ShowScenarioSwitchConfirmation = true;
            return;
        }

        await ExecuteLoadScenario();
    }

    /// <summary>
    /// Loads a scenario from pre-fetched JSON, auto-selecting the hardest difficulty.
    /// Used by --scenario CLI argument to skip interactive dialogs.
    /// </summary>
    public async Task AutoLoadScenarioFromJsonAsync(string json, string displayName, string apiId)
    {
        _pendingScenarioSource = json;
        _pendingApiScenarioId = apiId;
        ScenarioFilePath = displayName;

        try
        {
            string scenarioJson = json;
            List<string> difficulties = ScenarioDifficultyHelper.GetAvailableDifficulties(scenarioJson);

            if (difficulties.Count >= 2)
            {
                string hardest = difficulties[^1];
                _log.LogInformation("Auto-selecting difficulty: {Level}", hardest);
                (string? filtered, List<string>? warnings) = ScenarioDifficultyHelper.FilterByDifficulty(scenarioJson, hardest);
                foreach (string w in warnings)
                {
                    AddWarningEntry($"[WARN] {w}");
                }

                scenarioJson = filtered;
            }

            _pendingScenarioSource = null;
            _pendingApiScenarioId = null;
            string scenarioId = ScenarioIdentity.ResolveFromJson(scenarioJson);
            await SendScenarioToServer(scenarioJson, apiId, 100, 100, _preferences.GetSoloGoAroundProbability(scenarioId));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auto-load scenario error");
            ReportScenarioActionFailure("Load", ex.Message);
        }
    }

    /// <summary>
    /// Loads a scenario from pre-fetched JSON (e.g. from the vNAS data API).
    /// </summary>
    public async Task LoadScenarioFromJsonAsync(string json, string displayName, string? apiId = null)
    {
        _pendingScenarioSource = json;
        _pendingApiScenarioId = apiId;
        ScenarioFilePath = displayName;

        if (ActiveScenarioId is not null)
        {
            ShowScenarioSwitchConfirmation = true;
            return;
        }

        await ExecuteLoadScenario();
    }

    [RelayCommand]
    private async Task ConfirmScenarioSwitchAsync()
    {
        ShowScenarioSwitchConfirmation = false;
        await ExecuteLoadScenario();
    }

    [RelayCommand]
    private void CancelScenarioSwitch()
    {
        ShowScenarioSwitchConfirmation = false;
        _pendingScenarioSource = null;
        _pendingApiScenarioId = null;
    }

    private async Task ExecuteLoadScenario()
    {
        try
        {
            string json;
            string? apiId = null;
            if (_pendingScenarioSource is not null)
            {
                json = _pendingScenarioSource;
                apiId = _pendingApiScenarioId;
                _pendingScenarioSource = null;
                _pendingApiScenarioId = null;
                _log.LogInformation("Loading scenario from API: {Name}", ScenarioFilePath);
            }
            else
            {
                _log.LogInformation("Loading scenario from {Path}", ScenarioFilePath);
                json = await File.ReadAllTextAsync(ScenarioFilePath);
            }

            string seedScenarioId = ScenarioIdentity.ResolveFromJson(json);
            var setupPlan = ScenarioSetupPlan.Create(
                json,
                _preferences.SoloTrainingMode,
                _preferences.SoloParkingInitialCallupRatePercent,
                _preferences.SoloArrivalGeneratorRatePercent,
                _preferences.GetSoloGoAroundProbability(seedScenarioId)
            );

            if (setupPlan.RequiresSetup)
            {
                DifficultyOptions.Clear();
                foreach (DifficultyOption option in setupPlan.DifficultyOptions)
                {
                    DifficultyOptions.Add(option);
                }

                OnPropertyChanged(nameof(ShowScenarioSetupDifficulty));
                SelectedDifficultyIndex = setupPlan.SelectedDifficultyIndex;
                ShowScenarioSetupPacingControls = setupPlan.ShowPacingControls;
                ShowScenarioSetupParkingInitialCallupRate = setupPlan.ShowParkingInitialCallupRate;
                ShowScenarioSetupArrivalGeneratorRate = setupPlan.ShowArrivalGeneratorRate;
                ShowScenarioSetupGoAroundProbability = setupPlan.ShowGoAroundProbability;
                ScenarioSetupParkingInitialCallupRatePercent = setupPlan.ParkingInitialCallupRatePercent;
                ScenarioSetupParkingInitialCallupIntervalSeconds = ParkingInitialCallupRateToIntervalSeconds(
                    setupPlan.ParkingInitialCallupRatePercent
                );
                ScenarioSetupArrivalGeneratorRatePercent = setupPlan.ArrivalGeneratorRatePercent;
                ScenarioSetupSoloGoAroundProbabilityPercent = setupPlan.GoAroundProbabilityPercent;
                _pendingScenarioJson = json;
                _pendingDifficultyApiId = apiId;
                ShowScenarioSetup = true;
                return;
            }

            await SendScenarioToServer(json, apiId, 100, 100, _preferences.GetSoloGoAroundProbability(seedScenarioId));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario load error");
            ReportScenarioActionFailure("Load", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ConfirmScenarioSetupAsync()
    {
        ShowScenarioSetup = false;
        string? json = _pendingScenarioJson;
        string? apiId = _pendingDifficultyApiId;
        _pendingScenarioJson = null;
        _pendingDifficultyApiId = null;

        if (json is null)
        {
            return;
        }

        string scenarioJson = json;
        if (DifficultyOptions.Count > 0)
        {
            if (SelectedDifficultyIndex < 0 || SelectedDifficultyIndex >= DifficultyOptions.Count)
            {
                return;
            }

            DifficultyOption selected = DifficultyOptions[SelectedDifficultyIndex];
            (string? filtered, List<string>? warnings) = ScenarioDifficultyHelper.FilterByDifficulty(json, selected.Level);
            scenarioJson = filtered;
            foreach (string w in warnings)
            {
                AddWarningEntry($"[WARN] {w}");
            }
        }

        int parkingRate = ParkingInitialCallupIntervalSecondsToRate(ScenarioSetupParkingInitialCallupIntervalSeconds);
        int arrivalRate = Math.Clamp(ScenarioSetupArrivalGeneratorRatePercent, 0, 100);
        int goAroundProbability = Math.Clamp(ScenarioSetupSoloGoAroundProbabilityPercent, 0, 100);
        int loadParkingRate = ShowScenarioSetupParkingInitialCallupRate ? parkingRate : 100;
        int loadArrivalRate = ShowScenarioSetupArrivalGeneratorRate ? arrivalRate : 100;
        int loadGoAroundProbability = ShowScenarioSetupGoAroundProbability ? goAroundProbability : 0;
        if (ShowScenarioSetupPacingControls)
        {
            _preferences.SetSoloPacingRates(
                ShowScenarioSetupParkingInitialCallupRate ? parkingRate : _preferences.SoloParkingInitialCallupRatePercent,
                ShowScenarioSetupArrivalGeneratorRate ? arrivalRate : _preferences.SoloArrivalGeneratorRatePercent
            );
            if (ShowScenarioSetupGoAroundProbability)
            {
                string scenarioId = ScenarioIdentity.ResolveFromJson(scenarioJson);
                _preferences.SetSoloGoAroundProbabilityForScenario(scenarioId, goAroundProbability);
            }
        }
        DifficultyOptions.Clear();
        OnPropertyChanged(nameof(ShowScenarioSetupDifficulty));

        try
        {
            await SendScenarioToServer(scenarioJson, apiId, loadParkingRate, loadArrivalRate, loadGoAroundProbability);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario load error");
            ReportScenarioActionFailure("Load", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelScenarioSetup()
    {
        ShowScenarioSetup = false;
        _pendingScenarioJson = null;
        _pendingDifficultyApiId = null;
        DifficultyOptions.Clear();
        OnPropertyChanged(nameof(ShowScenarioSetupDifficulty));
        ShowScenarioSetupPacingControls = false;
        ShowScenarioSetupParkingInitialCallupRate = false;
        ShowScenarioSetupArrivalGeneratorRate = false;
        ShowScenarioSetupGoAroundProbability = false;
    }

    /// <summary>
    /// ARTCC-tab load path. Fetches the canonical scenario JSON from the server (gated by the caller's
    /// permitted ARTCCs and rating against the canonical fields), then runs it through the standard
    /// JSON load pipeline — so catalog loads get the same difficulty and solo-pacing setup as
    /// local-file loads. Surfaces the gate denial inline when the scenario is out of reach.
    /// </summary>
    public async Task LoadScenarioFromIdAsync(string apiScenarioId, string? displayName = null)
    {
        try
        {
            ScenarioJsonResultDto result = await _connection.GetScenarioJsonByIdAsync(apiScenarioId);

            if (result.AccessDeniedReason is { } reason)
            {
                _log.LogInformation("Scenario load denied: {Reason}", reason);
                StatusText = reason;
                AddSystemEntry($"Access denied: {reason}");
                return;
            }

            if (result.Json is null)
            {
                _log.LogWarning("Scenario fetch by id returned no JSON");
                StatusText = "Scenario load failed";
                return;
            }

            await LoadScenarioFromJsonAsync(result.Json, displayName ?? apiScenarioId, apiScenarioId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load scenario by id error");
            ReportScenarioActionFailure("Load", ex.Message);
        }
    }

    private async Task SendScenarioToServer(
        string json,
        string? apiId,
        int soloParkingInitialCallupRatePercent,
        int soloArrivalGeneratorRatePercent,
        int soloGoAroundProbabilityPercent
    )
    {
        StashLoadedScenarioJson(json);
        LoadScenarioResultDto result = await _connection.LoadScenarioAsync(
            json,
            soloParkingInitialCallupRatePercent,
            soloArrivalGeneratorRatePercent,
            soloGoAroundProbabilityPercent
        );

        if (result.Success)
        {
            ApplyScenarioResult(result);
            string scenarioName = result.Name;
            if (apiId is not null)
            {
                _preferences.AddRecentScenario("", scenarioName, apiId);
            }
            else if (File.Exists(ScenarioFilePath))
            {
                _preferences.AddRecentScenario(ScenarioFilePath, scenarioName);
            }

            _log.LogInformation(
                "Scenario loaded: '{Name}' ({Id}), " + "{Count} aircraft, " + "{Delayed} delayed, " + "{All} total, " + "{Warnings} warnings",
                result.Name,
                result.ScenarioId,
                result.AircraftCount,
                result.DelayedCount,
                result.AllAircraft.Count,
                result.Warnings.Count
            );

            StatusText = $"Loaded '{result.Name}': " + $"{result.AllAircraft.Count} aircraft";
            AddSystemEntry($"Scenario loaded: {result.Name}" + $" ({result.AllAircraft.Count} aircraft)");
        }
        else
        {
            _log.LogWarning("Scenario load failed");
            StatusText = "Scenario load failed";
        }

        // Warnings are already displayed and logged via PendingBroadcasts from the server.
    }

    [RelayCommand(CanExecute = nameof(CanUnloadScenario))]
    private async Task UnloadScenarioAsync()
    {
        if (ActiveScenarioId is null)
        {
            StatusText = "No active scenario";
            return;
        }

        try
        {
            UnloadScenarioResultDto result = await _connection.UnloadScenarioAircraftAsync();

            if (result.RequiresConfirmation)
            {
                PendingUnloadScenarioWarning = result.Message;
                ShowUnloadScenarioConfirmation = true;
                return;
            }

            ClearScenarioState();
            StatusText = "Scenario unloaded";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "UnloadScenario failed");
            ReportScenarioActionFailure("Unload", ex.Message);
        }
    }

    private bool CanUnloadScenario() => CanExecuteInRoom && HasScenario && !IsNonMentor;

    /// <summary>
    /// Re-runs the loaded scenario from the top with freshly generated traffic. Open to every room
    /// member, mentor or not: unlike Unload it cannot strand the room or switch scenarios, so it is not
    /// one of the mentor-only footgun actions.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestartScenario))]
    private void RestartScenario()
    {
        if (ActiveScenarioId is null)
        {
            StatusText = "No active scenario";
            return;
        }

        ShowRestartScenarioConfirmation = true;
    }

    private bool CanRestartScenario() => CanExecuteInRoom && HasScenario;

    [RelayCommand]
    private async Task ConfirmRestartScenarioAsync()
    {
        ShowRestartScenarioConfirmation = false;
        try
        {
            CommandResultDto result = await _connection.RestartScenarioAsync();
            if (!result.Success)
            {
                ReportScenarioActionFailure("Restart", result.Message ?? "Restart failed");
                return;
            }

            StatusText = "Scenario restarted";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RestartScenario failed");
            ReportScenarioActionFailure("Restart", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelRestartScenario() => ShowRestartScenarioConfirmation = false;

    /// <summary>
    /// Surfaces a rejected scenario action in the terminal as well as the status bar. The status bar
    /// alone reads as "nothing happened" — it is a line of small gray text at the bottom of the window
    /// that a controller watching the scope never sees.
    /// </summary>
    private void ReportScenarioActionFailure(string action, string message)
    {
        StatusText = $"{action} error: {message}";
        AddWarningEntry($"{action} scenario failed: {message}");
    }

    [RelayCommand]
    private async Task ConfirmUnloadScenarioAsync()
    {
        ShowUnloadScenarioConfirmation = false;
        try
        {
            await _connection.ConfirmUnloadScenarioAsync();
            ClearScenarioState();
            StatusText = "Scenario unloaded";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ConfirmUnloadScenario failed");
            ReportScenarioActionFailure("Unload", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelUnloadScenario()
    {
        ShowUnloadScenarioConfirmation = false;
        PendingUnloadScenarioWarning = null;
    }

    private void ApplyScenarioResult(LoadScenarioResultDto result)
    {
        SetStudentPositionType(result.StudentPositionType);
        _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);
        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.ShowMvaHints = _preferences.GetMvaHintDefault(_studentPositionType);
        }

        ApplyScenarioBootstrap(
            new ScenarioBootstrap(
                result.ScenarioId,
                result.Name,
                result.PrimaryAirportId,
                result.PositionDisplayConfig,
                result.FlightStripsConfig,
                result.AllAircraft,
                ElapsedSeconds: 0
            )
        );
        StashScenarioGeneratorsAndPositions(result.AircraftGenerators, result.VfrArrivalGenerators, result.OverflightGenerators, result.Positions);
        IsLiveSession = result.IsLiveSession;
        ApplySimState(result.IsPaused, result.SimRate);
        ApplySessionSettingsFromLoadScenarioResult(result);

        _ = SendAutoAcceptDelay();
        _ = SendCommandRunDelay();
        _ = SendAutoDeleteMode();
        _ = SendDepartureAutoDeleteDistance(_preferences.DepartureAutoDeleteDistanceNm);
        _ = SendValidateDctFixes();
        _ = SendSoloTrainingMode();
        _ = SendRpoShowPilotSpeech();
        _ = SendAutoClearedToLand();
        _ = SendAutoCrossRunway();
        _ = SendAutoPullUpToParallel();
        _ = SendAutoGoAroundOnOccupiedRunway();
        _ = SendAutoRejectTakeoffOnOccupiedRunway();
        _ = SendAutoArrivalSpacingOnOccupiedRunway();
    }

    private void OnScenarioLoaded(ScenarioLoadedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _log.LogInformation("Scenario loaded by another client: '{Name}' ({Id})", dto.ScenarioName, dto.ScenarioId);

            SetStudentPositionType(dto.StudentPositionType);
            _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);
            foreach (RadarViewModel radar in AllRadarViews)
            {
                radar.ShowMvaHints = _preferences.GetMvaHintDefault(_studentPositionType);
            }

            ApplyScenarioBootstrap(
                new ScenarioBootstrap(
                    dto.ScenarioId,
                    dto.ScenarioName,
                    dto.PrimaryAirportId,
                    dto.PositionDisplayConfig,
                    dto.FlightStripsConfig,
                    dto.AllAircraft,
                    ElapsedSeconds: 0
                )
            );
            StashScenarioGeneratorsAndPositions(dto.AircraftGenerators, dto.VfrArrivalGenerators, dto.OverflightGenerators, dto.Positions);
            IsLiveSession = dto.IsLiveSession;
            ApplySimState(dto.IsPaused, dto.SimRate);

            // Apply session settings from the server (set by the loading RPO).
            // Do NOT send our preferences — only the loading RPO applies theirs.
            // Other RPOs can change settings via the session settings flyout.
            ApplySessionSettingsFromScenarioLoaded(dto);

            StatusText = $"Scenario loaded: {dto.ScenarioName}";
            AddSystemEntry($"Scenario loaded: {dto.ScenarioName} ({dto.AllAircraft.Count} aircraft)");
        });
    }

    /// <summary>
    /// Shared scenario-activation router. Applies the fields common to all
    /// three paths (loader, other-clients broadcast, join-room) and fans out
    /// to the sub-VM bootstrap methods. Per-path extras — ApplySimState
    /// signature, ApplySessionSettings*, _studentPositionType, prefs push,
    /// StatusText/AddSystemEntry — stay at the call site.
    /// </summary>
    internal void ApplyScenarioBootstrap(ScenarioBootstrap bootstrap)
    {
        ActiveScenarioId = bootstrap.ScenarioId;
        ActiveScenarioName = bootstrap.ScenarioName;
        ActiveScenarioPrimaryAirportId = NormalizeFavoriteAirportId(bootstrap.PrimaryAirportId);
        if (bootstrap.ScenarioName is not null)
        {
            _preferences.SetScenarioName(bootstrap.ScenarioId, bootstrap.ScenarioName);
        }

        _commandInput.PrimaryAirportId = bootstrap.PrimaryAirportId;
        SetRadarAirportPosition(bootstrap.PrimaryAirportId);

        if (!string.IsNullOrEmpty(bootstrap.PrimaryAirportId))
        {
            SetDistanceReference(bootstrap.PrimaryAirportId);
        }

        Aircraft.Clear();
        ClearBookmarks();
        int delayed = 0;
        foreach (AircraftDto dto in bootstrap.Aircraft)
        {
            var model = AircraftModel.FromDto(dto, ComputeDistance);
            ApplyAutoClearedToLand(model);
            Aircraft.Add(model);
            if (model.IsDelayed)
            {
                delayed++;
            }
        }
        InitialDelayedSpawnCount = delayed;
        PendingDelayedSpawnCount = delayed;

        string artccId = _preferences.ArtccId;
        // Stashed so a Radar/Ground window opened later in this scenario bootstraps from the same data.
        _lastScenarioArtccId = artccId;
        _lastRadarPrimaryAirportId = bootstrap.PrimaryAirportId;
        _lastScenarioId = bootstrap.ScenarioId;
        Radar.ApplyScenarioBootstrap(bootstrap, artccId);
        foreach (RadarViewInstance instance in ExtraRadarViews)
        {
            // An extra window stays on the airport it was opened with — the scenario's airport is the
            // docked view's. Re-seeding reloads this instance's own video maps and per-scenario settings.
            SeedRadarAirport(instance);
        }

        Ground.ApplyScenarioBootstrap(bootstrap, artccId);
        foreach (GroundViewInstance instance in ExtraGroundViews)
        {
            // The new scenario id keys this window's own per-scenario view settings; re-seeding the
            // airport re-evaluates whether it mirrors the primary, whose airport may have just changed.
            instance.Vm.SetScenarioId(bootstrap.ScenarioId);
            SeedGroundAirport(instance);
        }

        VStrips.ApplyBayConfig(bootstrap.FlightStripsConfig);
        // Populate the student VM's accessible-facility list so the View →
        // Strips → New Strips Tab… picker has entries. The ScenarioLoaded
        // broadcast path (other clients) refreshes via VStripsViewModel's
        // own subscription; the loader path goes through here instead.
        _ = VStrips.RefreshAccessibleFacilitiesAsync();
        // The student entry's secondary split pane is constructed with
        // auto-bootstrap off, so feed it the same student strips config here.
        // It keeps its own bay selection; only the facility scope follows.
        if (StripsEntries[0].SecondaryVm is { } splitVm)
        {
            splitVm.ApplyBayConfig(bootstrap.FlightStripsConfig);
            _ = splitVm.RefreshAccessibleFacilitiesAsync();
        }

        // Same bootstrap for vTDLS: populate accessible facilities, then
        // auto-switch to the scenario's primary airport (e.g. OAK for an OAK
        // scenario) so the student tab renders the appropriate DCL/PDC lists
        // without the user picking from the menu. Falls back to the first
        // accessible facility if the primary airport isn't a TDLS facility.
        // No-op silently if the position has no TDLS-configured facility.
        _ = BootstrapStudentTdlsAsync(bootstrap.PrimaryAirportId);

        // Seed the active-position indicator with the student position (server default) before any AS.
        SetActiveTcpFromServer(bootstrap.PositionDisplayConfig?.TcpCode);

        // Scenario auto-connect ATC positions appear in the controller list.
        _ = RefreshOnlineControllersAsync();

        // With no weather loaded yet, show default standard METARs for the scenario's airports.
        ApplyDefaultWeatherIfNoWeather();

        StartRichPresence(bootstrap.ElapsedSeconds);
    }

    /// <summary>
    /// Stamps when the newly active scenario started — back-dated by however long it has already been
    /// running, so a joiner's Discord timer matches the room's — and publishes it. Shared by both
    /// scenario-identity writers: the bootstrap router and the recording load.
    /// </summary>
    private void StartRichPresence(double elapsedSeconds)
    {
        _richPresenceStartUnixSeconds = DateTimeOffset.UtcNow.AddSeconds(-elapsedSeconds).ToUnixTimeSeconds();
        RefreshRichPresence();
    }

    /// <summary>
    /// Publishes the active scenario as the user's Discord status, or withdraws whatever is showing
    /// when the setting is off or no scenario is loaded. Called on every scenario activation and
    /// again when the Settings window saves, so the toggle takes effect immediately.
    /// </summary>
    public void RefreshRichPresence()
    {
        if (RichPresence is not { } presence)
        {
            return;
        }

        if (!_preferences.DiscordRichPresenceEnabled || (ActiveScenarioId is null))
        {
            presence.Clear();
            return;
        }

        var activity = new DiscordActivity(
            ActiveScenarioName ?? ActiveScenarioId,
            RichPresenceStateLine(_preferences.ArtccId, ActiveScenarioPrimaryAirportId),
            _richPresenceStartUnixSeconds
        );
        presence.Publish(activity);
    }

    /// <summary>
    /// The second Discord line: the ARTCC and the airport, whichever of the two is known, or null
    /// when neither is — Discord omits the line rather than showing an empty one.
    /// </summary>
    private static string? RichPresenceStateLine(string artccId, string? airportId)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(artccId))
        {
            parts.Add(artccId);
        }

        if (!string.IsNullOrWhiteSpace(airportId))
        {
            parts.Add(airportId);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private async Task BootstrapStudentTdlsAsync(string? primaryAirportId)
    {
        // Use the returned list directly — AccessibleFacilities is updated via
        // Dispatcher.InvokeAsync inside RefreshAccessibleFacilitiesAsync, but
        // we don't want to race even that bookkeeping.
        List<AccessibleFacilityDto> facilities = await VTdls.RefreshAccessibleFacilitiesAsync();
        string? preferred = ResolvePreferredTdlsFacility(facilities, primaryAirportId);
        if (preferred is not null)
        {
            await VTdls.SwitchFacilityAsync(preferred);
        }
    }

    /// <summary>
    /// Picks the best vTDLS facility to default to for the scenario. Prefers the
    /// facility whose id matches the scenario's primary airport (vNAS convention:
    /// OAK facility serves KOAK; SFO facility serves KSFO; etc.). Falls back to
    /// the first accessible leaf facility — never a consolidated parent, whose
    /// merged page is an explicit choice rather than a sensible default — then to
    /// the first entry of any kind, then null when none are available.
    /// </summary>
    private static string? ResolvePreferredTdlsFacility(IReadOnlyList<Services.AccessibleFacilityDto> facilities, string? primaryAirportId)
    {
        if (facilities.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(primaryAirportId))
        {
            string bare = primaryAirportId.StartsWith('K') && primaryAirportId.Length == 4 ? primaryAirportId[1..] : primaryAirportId;
            AccessibleFacilityDto? match =
                facilities.FirstOrDefault(f => string.Equals(f.FacilityId, bare, StringComparison.OrdinalIgnoreCase))
                ?? facilities.FirstOrDefault(f => string.Equals(f.FacilityId, primaryAirportId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.FacilityId;
            }
        }

        AccessibleFacilityDto? leaf = facilities.FirstOrDefault(f => !f.IsConsolidated);
        return (leaf ?? facilities[0]).FacilityId;
    }

    private void OnScenarioUnloaded()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _log.LogInformation("Scenario unloaded by another client");
            ClearScenarioState();
            StatusText = "Scenario unloaded";
            AddSystemEntry("Scenario unloaded by another user");
        });
    }

    internal void ClearScenarioState()
    {
        ActiveScenarioId = null;
        ActiveScenarioName = null;
        ActiveScenarioPrimaryAirportId = null;
        IsLiveSession = false;
        // Nothing is running, so nothing is shown. Every way out of a scenario reaches here: unload,
        // leaving the room, disconnecting, being kicked, and a rejoin that failed.
        RichPresence?.Clear();
        _richPresenceStartUnixSeconds = 0;
        SetStudentPositionType(null);
        _isAutoClearedToLand = false;
        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.ShowMvaHints = false;
        }

        _commandInput.PrimaryAirportId = null;
        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.SetPrimaryAirportId(null);
            radar.ClearShownPaths();
            radar.DataBlockState.Clear();
        }

        foreach (GroundViewModel ground in AllGroundViews)
        {
            ground.ClearShownTaxiRoutes();
            ground.DataBlockState.Clear();
        }

        // Nothing a window opened after the unload should inherit from the scenario that just went away.
        _lastScenarioArtccId = null;
        _lastScenarioId = null;
        _lastRadarPrimaryAirportId = null;
        _lastPositionDisplayConfig = null;
        Aircraft.Clear();
        // The server drops its ATPA/conflict caches on unload without broadcasting an empty set, so the
        // client's mirrors have to be dropped here too: an aircraft added incrementally in the next
        // scenario is seeded from them (SeedAtpaResult / SeedConflictPeer) and would otherwise inherit a
        // pairing from the scenario that just went away.
        _atpaResults = [];
        _conflictAlerts = [];
        ClearBookmarks();
        InitialDelayedSpawnCount = 0;
        PendingDelayedSpawnCount = 0;
        // Every Ground View clears itself: a mirroring extra follows the cleared layout through
        // PropertyChanged, but its scenario id, ground aircraft and shown routes are its own.
        foreach (GroundViewModel ground in AllGroundViews)
        {
            ground.ClearLayout();
        }

        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.ClearVideoMaps();
        }

        // Clearing the maps drops each position's airport filter, so the weather readout has to be re-derived
        // from it: without this the last position's "No METAR for …" note outlives the filter that justified it.
        UpdateRadarWeatherDisplay();

        // Strips and PDCs are pushed state that nothing retracts once the session they belong to is gone, so
        // every open view drops its content here — the docked tabs, the split panes and the popped-out windows
        // alike, which hold the same VM instances (MainWindow.axaml.cs pops a window over the entry's Vm rather
        // than a copy). The bay layout and facility scope stay: an unload is not a room exit, and a
        // linked-facility tab is never re-bootstrapped by the next scenario load (see MainViewModel.Strips.cs,
        // which builds it with autoBootstrapFromScenarioLoaded: false), so dropping its facility would leave it
        // blank for the rest of the session. ClearRoomState drops the scope on top of this.
        foreach (VStripsDockEntryViewModel entry in StripsEntries)
        {
            entry.Vm.Clear();
            entry.SecondaryVm?.Clear();
        }
        foreach (VTdlsDockEntryViewModel entry in TdlsEntries)
        {
            entry.Vm.Clear();
        }
        ApplySessionSettings(
            new SessionSettingsDto(null, null, null, -1, false, false, true, true, true, true, true, false, 100, 100, 0, false, false, false)
        );

        // Active position no longer applies without a scenario; hide the indicator.
        SetActiveTcpFromServer(null);

        // Scenario positions are gone; refresh to leave only live CRC controllers (if any).
        _ = RefreshOnlineControllersAsync();

        // No scenario airports → clear the default METARs and weather overlays.
        ApplyDefaultWeatherIfNoWeather();
    }
}

public record DifficultyOption(string Level, int AircraftCount)
{
    public string Display => $"{Level} — {AircraftCount} aircraft";
}

public sealed record ScenarioSetupPlan(
    IReadOnlyList<DifficultyOption> DifficultyOptions,
    int SelectedDifficultyIndex,
    bool ShowPacingControls,
    bool ShowParkingInitialCallupRate,
    bool ShowArrivalGeneratorRate,
    bool ShowGoAroundProbability,
    int ParkingInitialCallupRatePercent,
    int ArrivalGeneratorRatePercent,
    int GoAroundProbabilityPercent
)
{
    public bool RequiresSetup => DifficultyOptions.Count > 0 || ShowPacingControls;

    public static ScenarioSetupPlan Create(
        string scenarioJson,
        bool soloTrainingMode,
        int parkingInitialCallupRatePercent,
        int arrivalGeneratorRatePercent,
        int goAroundProbabilityPercent
    )
    {
        List<string> difficulties = ScenarioDifficultyHelper.GetAvailableDifficulties(scenarioJson);
        var options = new List<DifficultyOption>();
        if (difficulties.Count >= 2)
        {
            Dictionary<string, int> counts = ScenarioDifficultyHelper.GetCountsPerCeiling(scenarioJson, difficulties);
            foreach (string level in difficulties)
            {
                options.Add(new DifficultyOption(level, counts[level]));
            }
        }

        bool showParkingInitialCallupRate = soloTrainingMode && ScenarioDifficultyHelper.HasParkingSpawns(scenarioJson);
        bool showArrivalGeneratorRate = soloTrainingMode && ScenarioDifficultyHelper.HasArrivalGenerators(scenarioJson);
        // Surface the go-around slider only when the setup dialog is already popping for
        // another solo reason. Avoids forcing a popup on every solo-mode load — operators
        // who only want to tweak this can do so via the mid-session settings flyout, which
        // also persists per-scenario.
        bool showGoAroundProbability = soloTrainingMode && (showParkingInitialCallupRate || showArrivalGeneratorRate);

        return new ScenarioSetupPlan(
            options,
            options.Count > 0 ? options.Count - 1 : -1,
            showParkingInitialCallupRate || showArrivalGeneratorRate,
            showParkingInitialCallupRate,
            showArrivalGeneratorRate,
            showGoAroundProbability,
            Math.Clamp(parkingInitialCallupRatePercent, 0, 200),
            Math.Clamp(arrivalGeneratorRatePercent, 0, 100),
            Math.Clamp(goAroundProbabilityPercent, 0, 100)
        );
    }
}
