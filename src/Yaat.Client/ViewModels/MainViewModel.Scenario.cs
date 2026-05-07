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
            await SendScenarioToServer(scenarioJson, apiId);
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

            var difficulties = ScenarioDifficultyHelper.GetAvailableDifficulties(json);

            if (difficulties.Count >= 2)
            {
                var counts = ScenarioDifficultyHelper.GetCountsPerCeiling(json, difficulties);

                DifficultyOptions.Clear();
                foreach (var level in difficulties)
                {
                    DifficultyOptions.Add(new DifficultyOption(level, counts[level]));
                }

                SelectedDifficultyIndex = difficulties.Count - 1;
                _pendingScenarioJson = json;
                _pendingDifficultyApiId = apiId;
                ShowDifficultySelection = true;
                return;
            }

            await SendScenarioToServer(json, apiId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario load error");
            StatusText = $"Load error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConfirmDifficultySelectionAsync()
    {
        ShowDifficultySelection = false;
        var json = _pendingScenarioJson;
        var apiId = _pendingDifficultyApiId;
        _pendingScenarioJson = null;
        _pendingDifficultyApiId = null;

        if (json is null || SelectedDifficultyIndex < 0 || SelectedDifficultyIndex >= DifficultyOptions.Count)
        {
            return;
        }

        var selected = DifficultyOptions[SelectedDifficultyIndex];
        var (filtered, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(json, selected.Level);

        foreach (var w in warnings)
        {
            AddWarningEntry($"[WARN] {w}");
        }

        try
        {
            await SendScenarioToServer(filtered, apiId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario load error");
            StatusText = $"Load error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelDifficultySelection()
    {
        ShowDifficultySelection = false;
        _pendingScenarioJson = null;
        _pendingDifficultyApiId = null;
        DifficultyOptions.Clear();
    }

    private async Task SendScenarioToServer(string json, string? apiId)
    {
        StashLoadedScenarioJson(json);
        var result = await _connection.LoadScenarioAsync(json);

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
        foreach (var dto in bootstrap.Aircraft)
        {
            var model = AircraftModel.FromDto(dto, ComputeDistance);
            ApplyAutoClearedToLand(model);
            Aircraft.Add(model);
        }

        var artccId = _preferences.ArtccId;
        Radar.ApplyScenarioBootstrap(bootstrap, artccId);
        Ground.ApplyScenarioBootstrap(bootstrap, artccId);
        VStrips.ApplyBayConfig(bootstrap.FlightStripsConfig);
        // Populate the student VM's accessible-facility list so the View →
        // Strips → New Strips Tab… picker has entries. The ScenarioLoaded
        // broadcast path (other clients) refreshes via VStripsViewModel's
        // own subscription; the loader path goes through here instead.
        _ = VStrips.RefreshAccessibleFacilitiesAsync();
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
        Ground.ClearLayout();
        Radar.ClearVideoMaps();
        ApplySessionSettings(new SessionSettingsDto(null, -1, false, false, true, false, false));
    }
}

public record DifficultyOption(string Level, int AircraftCount)
{
    public string Display => $"{Level} — {AircraftCount} aircraft";
}
