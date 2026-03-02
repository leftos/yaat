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
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task LoadScenarioAsync()
    {
        if (ActiveRoomId is null)
        {
            StatusText = "Join a room before loading a scenario";
            return;
        }

        if (string.IsNullOrWhiteSpace(ScenarioFilePath))
        {
            StatusText = "No scenario file selected";
            return;
        }

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
    }

    private async Task ExecuteLoadScenario()
    {
        try
        {
            _log.LogInformation("Loading scenario from {Path}", ScenarioFilePath);

            var json = await File.ReadAllTextAsync(ScenarioFilePath);

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

            await SendScenarioToServer(json);
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

    private async Task SendScenarioToServer(string json)
    {
        var result = await _connection.LoadScenarioAsync(json);

        if (result.Success)
        {
            ApplyScenarioResult(result);
            _preferences.AddRecentScenario(ScenarioFilePath);

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

    private bool CanUnloadScenario() => IsConnected && HasScenario;

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

        if (!string.IsNullOrEmpty(result.PrimaryAirportId))
        {
            _ = Ground.LoadLayoutAsync(result.PrimaryAirportId);
        }

        if (!string.IsNullOrEmpty(_preferences.ArtccId))
        {
            _ = Radar.LoadVideoMapsForArtccAsync(_preferences.ArtccId, result.PrimaryAirportId, result.ScenarioId);
        }
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
