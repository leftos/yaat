using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
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

    [RelayCommand(CanExecute = nameof(CanExecuteInRoom))]
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
            var scenarioJson = json;
            var difficulties = ScenarioDifficultyHelper.GetAvailableDifficulties(scenarioJson);

            if (difficulties.Count >= 2)
            {
                var hardest = difficulties[^1];
                _log.LogInformation("Auto-selecting difficulty: {Level}", hardest);
                var (filtered, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(scenarioJson, hardest);
                foreach (var w in warnings)
                {
                    AddWarningEntry($"[WARN] {w}");
                }

                scenarioJson = filtered;
            }

            _pendingScenarioSource = null;
            _pendingApiScenarioId = null;
            var scenarioId = ScenarioIdentity.ResolveFromJson(scenarioJson);
            await SendScenarioToServer(scenarioJson, apiId, 100, 100, _preferences.GetSoloGoAroundProbability(scenarioId));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auto-load scenario error");
            StatusText = $"Load error: {ex.Message}";
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

            var seedScenarioId = ScenarioIdentity.ResolveFromJson(json);
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
                foreach (var option in setupPlan.DifficultyOptions)
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
            StatusText = $"Load error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConfirmScenarioSetupAsync()
    {
        ShowScenarioSetup = false;
        var json = _pendingScenarioJson;
        var apiId = _pendingDifficultyApiId;
        _pendingScenarioJson = null;
        _pendingDifficultyApiId = null;

        if (json is null)
        {
            return;
        }

        var scenarioJson = json;
        if (DifficultyOptions.Count > 0)
        {
            if (SelectedDifficultyIndex < 0 || SelectedDifficultyIndex >= DifficultyOptions.Count)
            {
                return;
            }

            var selected = DifficultyOptions[SelectedDifficultyIndex];
            var (filtered, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(json, selected.Level);
            scenarioJson = filtered;
            foreach (var w in warnings)
            {
                AddWarningEntry($"[WARN] {w}");
            }
        }

        var parkingRate = ParkingInitialCallupIntervalSecondsToRate(ScenarioSetupParkingInitialCallupIntervalSeconds);
        var arrivalRate = Math.Clamp(ScenarioSetupArrivalGeneratorRatePercent, 0, 100);
        var goAroundProbability = Math.Clamp(ScenarioSetupSoloGoAroundProbabilityPercent, 0, 100);
        var loadParkingRate = ShowScenarioSetupParkingInitialCallupRate ? parkingRate : 100;
        var loadArrivalRate = ShowScenarioSetupArrivalGeneratorRate ? arrivalRate : 100;
        var loadGoAroundProbability = ShowScenarioSetupGoAroundProbability ? goAroundProbability : 0;
        if (ShowScenarioSetupPacingControls)
        {
            _preferences.SetSoloPacingRates(
                ShowScenarioSetupParkingInitialCallupRate ? parkingRate : _preferences.SoloParkingInitialCallupRatePercent,
                ShowScenarioSetupArrivalGeneratorRate ? arrivalRate : _preferences.SoloArrivalGeneratorRatePercent
            );
            if (ShowScenarioSetupGoAroundProbability)
            {
                var scenarioId = ScenarioIdentity.ResolveFromJson(scenarioJson);
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
            StatusText = $"Load error: {ex.Message}";
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
    /// ARTCC-tab load path. The client only forwards the vNAS scenario id; the server
    /// resolves the canonical JSON from its catalog cache and applies the rating gate
    /// against the canonical MinimumRating. Returns AccessDeniedReason when the user's
    /// training key doesn't unlock the scenario, which is surfaced to the user inline.
    /// </summary>
    public async Task LoadScenarioFromIdAsync(string apiScenarioId, string? displayName = null)
    {
        ScenarioFilePath = displayName ?? apiScenarioId;
        try
        {
            var soloGoAround = _preferences.GetSoloGoAroundProbability(apiScenarioId);
            var result = await _connection.LoadScenarioByIdAsync(apiScenarioId, _preferences.TrainingKey, 100, 100, soloGoAround);

            if (result.AccessDeniedReason is { } reason)
            {
                _log.LogInformation("Scenario load denied: {Reason}", reason);
                StatusText = reason;
                AddSystemEntry($"Access denied: {reason}");
                return;
            }

            if (result.Success)
            {
                ApplyScenarioResult(result);
                _preferences.AddRecentScenario("", result.Name, apiScenarioId);
                _log.LogInformation(
                    "Scenario loaded by id: '{Name}' ({Id}), {Count} aircraft, {Delayed} delayed",
                    result.Name,
                    result.ScenarioId,
                    result.AircraftCount,
                    result.DelayedCount
                );
                StatusText = $"Loaded '{result.Name}': {result.AllAircraft.Count} aircraft";
                AddSystemEntry($"Scenario loaded: {result.Name} ({result.AllAircraft.Count} aircraft)");
            }
            else
            {
                _log.LogWarning("Scenario load failed");
                StatusText = "Scenario load failed";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load scenario by id error");
            StatusText = $"Load error: {ex.Message}";
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
        var result = await _connection.LoadScenarioAsync(
            json,
            soloParkingInitialCallupRatePercent,
            soloArrivalGeneratorRatePercent,
            soloGoAroundProbabilityPercent
        );

        if (result.Success)
        {
            ApplyScenarioResult(result);
            var scenarioName = result.Name;
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
            var result = await _connection.UnloadScenarioAircraftAsync();

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
            StatusText = $"Unload error: {ex.Message}";
        }
    }

    private bool CanUnloadScenario() => CanExecuteInRoom && HasScenario;

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
            StatusText = $"Unload error: {ex.Message}";
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
        _studentPositionType = result.StudentPositionType;
        _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);

        ApplyScenarioBootstrap(
            new ScenarioBootstrap(
                result.ScenarioId,
                result.Name,
                result.PrimaryAirportId,
                result.PositionDisplayConfig,
                result.FlightStripsConfig,
                result.AllAircraft
            )
        );
        StashScenarioGeneratorsAndPositions(result.AircraftGenerators, result.Positions);
        ApplySimState(result.IsPaused, result.SimRate);
        ApplySessionSettingsFromLoadScenarioResult(result);

        _ = SendAutoAcceptDelay();
        _ = SendAutoDeleteMode();
        _ = SendValidateDctFixes();
        _ = SendSoloTrainingMode();
        _ = SendRpoShowPilotSpeech();
        _ = SendAutoClearedToLand();
        _ = SendAutoCrossRunway();
    }

    private void OnScenarioLoaded(ScenarioLoadedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _log.LogInformation("Scenario loaded by another client: '{Name}' ({Id})", dto.ScenarioName, dto.ScenarioId);

            _studentPositionType = dto.StudentPositionType;
            _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);

            ApplyScenarioBootstrap(
                new ScenarioBootstrap(
                    dto.ScenarioId,
                    dto.ScenarioName,
                    dto.PrimaryAirportId,
                    dto.PositionDisplayConfig,
                    dto.FlightStripsConfig,
                    dto.AllAircraft
                )
            );
            StashScenarioGeneratorsAndPositions(dto.AircraftGenerators, dto.Positions);
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
    private void ApplyScenarioBootstrap(ScenarioBootstrap bootstrap)
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
        int delayed = 0;
        foreach (var dto in bootstrap.Aircraft)
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

        var artccId = _preferences.ArtccId;
        Radar.ApplyScenarioBootstrap(bootstrap, artccId);
        Ground.ApplyScenarioBootstrap(bootstrap, artccId);
        VStrips.ApplyBayConfig(bootstrap.FlightStripsConfig);
        // Populate the student VM's accessible-facility list so the View →
        // Strips → New Strips Tab… picker has entries. The ScenarioLoaded
        // broadcast path (other clients) refreshes via VStripsViewModel's
        // own subscription; the loader path goes through here instead.
        _ = VStrips.RefreshAccessibleFacilitiesAsync();

        // Same bootstrap for vTDLS: populate accessible facilities, then
        // auto-switch to the scenario's primary airport (e.g. OAK for an OAK
        // scenario) so the student tab renders the appropriate DCL/PDC lists
        // without the user picking from the menu. Falls back to the first
        // accessible facility if the primary airport isn't a TDLS facility.
        // No-op silently if the position has no TDLS-configured facility.
        _ = BootstrapStudentTdlsAsync(bootstrap.PrimaryAirportId);
    }

    private async Task BootstrapStudentTdlsAsync(string? primaryAirportId)
    {
        await VTdls.RefreshAccessibleFacilitiesAsync();
        var preferred = ResolvePreferredTdlsFacility(primaryAirportId);
        if (preferred is not null)
        {
            await VTdls.SwitchFacilityAsync(preferred);
        }
    }

    /// <summary>
    /// Picks the best vTDLS facility to default to for the scenario. Prefers the
    /// facility whose id matches the scenario's primary airport (vNAS convention:
    /// OAK facility serves KOAK; SFO facility serves KSFO; etc.). Falls back to
    /// the first accessible TDLS facility, then null when none are available.
    /// </summary>
    private string? ResolvePreferredTdlsFacility(string? primaryAirportId)
    {
        if (VTdls.AccessibleFacilities.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(primaryAirportId))
        {
            // vNAS facility ids drop the leading K (KOAK → OAK). Try the bare
            // form first, then the full id, then a case-insensitive match.
            var bare = primaryAirportId.StartsWith('K') && primaryAirportId.Length == 4 ? primaryAirportId[1..] : primaryAirportId;
            var match =
                VTdls.AccessibleFacilities.FirstOrDefault(f => string.Equals(f.FacilityId, bare, StringComparison.OrdinalIgnoreCase))
                ?? VTdls.AccessibleFacilities.FirstOrDefault(f => string.Equals(f.FacilityId, primaryAirportId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.FacilityId;
            }
        }

        return VTdls.AccessibleFacilities[0].FacilityId;
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

    private void ClearScenarioState()
    {
        ActiveScenarioId = null;
        ActiveScenarioName = null;
        ActiveScenarioPrimaryAirportId = null;
        _studentPositionType = null;
        _isAutoClearedToLand = false;
        _commandInput.PrimaryAirportId = null;
        Radar.SetPrimaryAirportId(null);
        Radar.ClearShownPaths();
        Ground.ClearShownTaxiRoutes();
        Aircraft.Clear();
        InitialDelayedSpawnCount = 0;
        PendingDelayedSpawnCount = 0;
        Ground.ClearLayout();
        Radar.ClearVideoMaps();
        ApplySessionSettings(new SessionSettingsDto(null, null, -1, false, false, true, false, 100, 100, 0, false, false, false));
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
        var difficulties = ScenarioDifficultyHelper.GetAvailableDifficulties(scenarioJson);
        var options = new List<DifficultyOption>();
        if (difficulties.Count >= 2)
        {
            var counts = ScenarioDifficultyHelper.GetCountsPerCeiling(scenarioJson, difficulties);
            foreach (var level in difficulties)
            {
                options.Add(new DifficultyOption(level, counts[level]));
            }
        }

        var showParkingInitialCallupRate = soloTrainingMode && ScenarioDifficultyHelper.HasParkingSpawns(scenarioJson);
        var showArrivalGeneratorRate = soloTrainingMode && ScenarioDifficultyHelper.HasArrivalGenerators(scenarioJson);
        // Surface the go-around slider only when the setup dialog is already popping for
        // another solo reason. Avoids forcing a popup on every solo-mode load — operators
        // who only want to tweak this can do so via the mid-session settings flyout, which
        // also persists per-scenario.
        var showGoAroundProbability = soloTrainingMode && (showParkingInitialCallupRate || showArrivalGeneratorRate);

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
