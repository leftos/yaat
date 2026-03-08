using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Scenario loading, difficulty selection, and unloading.
/// </summary>
public partial class MainViewModel
{
    private readonly TrainingDataService _trainingData = new();

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
        _pendingScenarioJson = null;

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
            await SendScenarioToServer(filtered);
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
        DifficultyOptions.Clear();
    }

    private async Task SendScenarioToServer(string json, string? apiId = null)
    {
        var result = await _connection.LoadScenarioAsync(json);

        if (result.Success)
        {
            ApplyScenarioResult(result);
            var scenarioName = result.Name ?? ScenarioFilePath;
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

        foreach (var w in result.Warnings)
        {
            _log.LogWarning("Scenario: {Warning}", w);
            AddWarningEntry($"[WARN] {w}");
        }
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
        ActiveScenarioId = result.ScenarioId;
        ActiveScenarioName = result.Name;
        _commandInput.PrimaryAirportId = result.PrimaryAirportId;
        Radar.SetPrimaryAirportId(result.PrimaryAirportId);
        SetRadarAirportPosition(result.PrimaryAirportId);
        ApplySimState(result.IsPaused, result.SimRate);

        if (!string.IsNullOrEmpty(result.PrimaryAirportId))
        {
            SetDistanceReference(result.PrimaryAirportId);
        }

        Aircraft.Clear();
        foreach (var dto in result.AllAircraft)
        {
            Aircraft.Add(AircraftModel.FromDto(dto, ComputeDistance));
        }

        _ = SendAutoAcceptDelay();
        _ = SendAutoDeleteMode();
        _ = SendValidateDctFixes();
        _ = SendAutoClearedToLand();
        _ = SendAutoCrossRunway();

        if (!string.IsNullOrEmpty(result.PrimaryAirportId))
        {
            _ = Ground.LoadLayoutAsync(result.PrimaryAirportId);
        }

        if (!string.IsNullOrEmpty(_preferences.ArtccId))
        {
            _ = Radar.LoadVideoMapsForArtccAsync(_preferences.ArtccId, result.PrimaryAirportId, result.ScenarioId);
        }

        if (result.PositionDisplayConfig is not null)
        {
            Radar.ApplyPositionDisplayConfig(result.PositionDisplayConfig);
        }
    }

    private void OnScenarioLoaded(ScenarioLoadedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _log.LogInformation("Scenario loaded by another client: '{Name}' ({Id})", dto.ScenarioName, dto.ScenarioId);

            ActiveScenarioId = dto.ScenarioId;
            ActiveScenarioName = dto.ScenarioName;
            _commandInput.PrimaryAirportId = dto.PrimaryAirportId;
            Radar.SetPrimaryAirportId(dto.PrimaryAirportId);
            SetRadarAirportPosition(dto.PrimaryAirportId);
            ApplySimState(dto.IsPaused, dto.SimRate);

            if (!string.IsNullOrEmpty(dto.PrimaryAirportId))
            {
                SetDistanceReference(dto.PrimaryAirportId);
                _ = Ground.LoadLayoutAsync(dto.PrimaryAirportId);
            }

            Aircraft.Clear();
            foreach (var ac in dto.AllAircraft)
            {
                Aircraft.Add(AircraftModel.FromDto(ac, ComputeDistance));
            }

            if (!string.IsNullOrEmpty(_preferences.ArtccId))
            {
                _ = Radar.LoadVideoMapsForArtccAsync(_preferences.ArtccId, dto.PrimaryAirportId, dto.ScenarioId);
            }

            if (dto.PositionDisplayConfig is not null)
            {
                Radar.ApplyPositionDisplayConfig(dto.PositionDisplayConfig);
            }

            _ = SendAutoAcceptDelay();
            _ = SendAutoDeleteMode();
            _ = SendValidateDctFixes();
            _ = SendAutoClearedToLand();
            _ = SendAutoCrossRunway();

            StatusText = $"Scenario loaded: {dto.ScenarioName}";
            AddSystemEntry($"Scenario loaded: {dto.ScenarioName} ({dto.AllAircraft.Count} aircraft)");
        });
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
        _commandInput.PrimaryAirportId = null;
        Radar.SetPrimaryAirportId(null);
        Aircraft.Clear();
        Ground.ClearLayout();
        Radar.ClearVideoMaps();
    }
}

public record DifficultyOption(string Level, int AircraftCount)
{
    public string Display => $"{Level} — {AircraftCount} aircraft";
}
