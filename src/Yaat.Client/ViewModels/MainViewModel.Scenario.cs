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
    /// Export Room as Scenario is offered to the users who may load one (mentor/instructor in a room), once the room has
    /// an aircraft to export.
    /// </summary>
    public bool CanExportRoomAsScenario => CanLoadScenario && (Aircraft.Count > 0);

    private void WireScenarioExportAvailability()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanLoadScenario))
            {
                OnPropertyChanged(nameof(CanExportRoomAsScenario));
            }
        };
        Aircraft.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanExportRoomAsScenario));
    }

    /// <summary>
    /// Asks the server for the room's aircraft as scenario JSON, lets the user choose where to save it, and writes it.
    /// Returns the aircraft the author still has to give presets or delete — empty when there are none, or when the export
    /// was refused, cancelled or failed (each of which is reported in the terminal).
    /// </summary>
    public async Task<IReadOnlyList<ScenarioExportFlagDto>> ExportRoomAsScenarioAsync()
    {
        ScenarioExportResultDto result;
        try
        {
            result = await _connection.ExportRoomAsScenarioAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export room as scenario failed");
            AddWarningEntry($"Export failed: {ex.Message}");
            return [];
        }

        if ((result.DeniedReason is not null) || (result.Json is null))
        {
            AddWarningEntry(result.DeniedReason ?? "Export failed: the server returned no scenario");
            return [];
        }

        string name = string.IsNullOrWhiteSpace(result.Name) ? "room snapshot" : result.Name;
        string? path = await _filePicker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Export Room as Scenario",
                SuggestedFileName: $"{SanitizeFileName(name)}.json",
                Filters: [new FilePickerFilter("JSON", ["*.json"])],
                DefaultExtension: "json"
            )
        );
        if (path is null)
        {
            return [];
        }

        try
        {
            await File.WriteAllTextAsync(path, result.Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Writing exported scenario to {Path} failed", path);
            AddWarningEntry($"Export failed: could not write {path}: {ex.Message}");
            return [];
        }

        AddSystemEntry($"Exported {result.AircraftCount} aircraft to {path} ({result.Flags.Count} need review). Weather is not included.");
        return result.Flags;
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
        LoadOverlay.BeginLocal(displayName);

        try
        {
            (string scenarioJson, List<string> warnings, string scenarioId) = await Task.Run(() => SelectHardestDifficulty(json));
            foreach (string w in warnings)
            {
                AddWarningEntry($"[WARN] {w}");
            }

            _pendingScenarioSource = null;
            _pendingApiScenarioId = null;
            (int defaultParkingRate, int defaultArrivalRate) = NoDialogLoadRates();
            await SendScenarioToServer(
                scenarioJson,
                apiId,
                defaultParkingRate,
                defaultArrivalRate,
                _preferences.GetSoloGoAroundProbability(scenarioId)
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auto-load scenario error");
            LoadOverlay.ApplyRefusal();
            ReportScenarioActionFailure("Load", ex.Message);
        }
    }

    /// <summary>
    /// Filters the scenario to its hardest difficulty when it offers two or more, and resolves its identity. Pure, so it
    /// runs off the UI thread.
    /// </summary>
    private (string Json, List<string> Warnings, string ScenarioId) SelectHardestDifficulty(string json)
    {
        List<string> difficulties = ScenarioDifficultyHelper.GetAvailableDifficulties(json);
        if (difficulties.Count < 2)
        {
            return (json, [], ScenarioIdentity.ResolveFromJson(json));
        }

        string hardest = difficulties[^1];
        _log.LogInformation("Auto-selecting difficulty: {Level}", hardest);
        return FilterAndResolve(json, hardest);
    }

    /// <summary>Filters the scenario to one difficulty level (none: unfiltered) and resolves its identity, off the UI thread.</summary>
    private static (string Json, List<string> Warnings, string ScenarioId) FilterAndResolve(string json, string? level)
    {
        if (level is null)
        {
            return (json, [], ScenarioIdentity.ResolveFromJson(json));
        }

        (string filtered, List<string> warnings) = ScenarioDifficultyHelper.FilterByDifficulty(json, level);
        return (filtered, warnings, ScenarioIdentity.ResolveFromJson(filtered));
    }

    /// <summary>The overlay title at the click: the catalog name, or the local file's name.</summary>
    private string ScenarioLoadTitle(bool fromCatalog) => fromCatalog ? ScenarioFilePath : Path.GetFileName(ScenarioFilePath);

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
        LoadOverlay.BeginLocal(ScenarioLoadTitle(fromCatalog: _pendingScenarioSource is not null));
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

            string seedScenarioId = await Task.Run(() => ScenarioIdentity.ResolveFromJson(json));
            bool soloTrainingMode = _preferences.SoloTrainingMode;
            int parkingRate = _preferences.SoloParkingInitialCallupRatePercent;
            int arrivalRate = _preferences.SoloArrivalGeneratorRatePercent;
            int goAroundProbability = _preferences.GetSoloGoAroundProbability(seedScenarioId);
            ScenarioSetupPlan setupPlan = await Task.Run(() =>
                ScenarioSetupPlan.Create(json, soloTrainingMode, parkingRate, arrivalRate, goAroundProbability)
            );

            if (setupPlan.RequiresSetup)
            {
                LoadOverlay.ApplyRefusal();
                ShowScenarioSetupDialog(setupPlan, json, apiId);
                return;
            }

            (int defaultParkingRate, int defaultArrivalRate) = NoDialogLoadRates();
            await SendScenarioToServer(json, apiId, defaultParkingRate, defaultArrivalRate, goAroundProbability);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario load error");
            LoadOverlay.ApplyRefusal();
            ReportScenarioActionFailure("Load", ex.Message);
        }
    }

    private void ShowScenarioSetupDialog(ScenarioSetupPlan setupPlan, string json, string? apiId)
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
        ScenarioSetupParkingInitialCallupIntervalSeconds = SoloPacing.ParkingInitialCallupRateToIntervalSeconds(
            setupPlan.ParkingInitialCallupRatePercent
        );
        ScenarioSetupArrivalGeneratorRatePercent = setupPlan.ArrivalGeneratorRatePercent;
        ScenarioSetupSoloGoAroundProbabilityPercent = setupPlan.GoAroundProbabilityPercent;
        _pendingScenarioJson = json;
        _pendingDifficultyApiId = apiId;
        ShowScenarioSetup = true;
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

        string? level = null;
        if (DifficultyOptions.Count > 0)
        {
            if (SelectedDifficultyIndex < 0 || SelectedDifficultyIndex >= DifficultyOptions.Count)
            {
                return;
            }

            level = DifficultyOptions[SelectedDifficultyIndex].Level;
        }

        (bool SaveGoAround, (int ParkingRate, int ArrivalRate, int GoAroundProbability) Rates) setup = ResolveSetupRates();
        DifficultyOptions.Clear();
        OnPropertyChanged(nameof(ShowScenarioSetupDifficulty));
        await SendConfirmedScenarioAsync(json, level, apiId, setup.SaveGoAround, setup.Rates);
    }

    /// <summary>
    /// The rates the dialog's load sends — the chosen values, or the stored Settings default for a pacing control the
    /// dialog did not offer (<see cref="SoloPacing.SelectLoadRates"/>) — and whether the go-around choice is stored for
    /// the scenario. The pacing pair is for this load only: the dialog seeds it from the Settings defaults and never
    /// writes it back.
    /// </summary>
    private (bool SaveGoAround, (int ParkingRate, int ArrivalRate, int GoAroundProbability) Rates) ResolveSetupRates()
    {
        int parkingRate = SoloPacing.ParkingInitialCallupIntervalSecondsToRate(ScenarioSetupParkingInitialCallupIntervalSeconds);
        int arrivalRate = Math.Clamp(ScenarioSetupArrivalGeneratorRatePercent, 0, 100);
        int goAroundProbability = Math.Clamp(ScenarioSetupSoloGoAroundProbabilityPercent, 0, 100);
        (int loadParkingRate, int loadArrivalRate) = SoloPacing.SelectLoadRates(
            ShowScenarioSetupParkingInitialCallupRate,
            ShowScenarioSetupArrivalGeneratorRate,
            (parkingRate, arrivalRate),
            SoloPacing.LoadDefaults(_preferences)
        );
        return (
            ShowScenarioSetupPacingControls && ShowScenarioSetupGoAroundProbability,
            (loadParkingRate, loadArrivalRate, ShowScenarioSetupGoAroundProbability ? goAroundProbability : 0)
        );
    }

    /// <summary>The pacing pair a load the setup dialog did not interrupt sends: the stored Settings defaults.</summary>
    private (int ParkingRate, int ArrivalRate) NoDialogLoadRates()
    {
        (int ParkingInitialCallupRatePercent, int ArrivalGeneratorRatePercent) defaults = SoloPacing.LoadDefaults(_preferences);
        return SoloPacing.SelectLoadRates(showParking: false, showArrival: false, chosen: defaults, defaults: defaults);
    }

    /// <summary>
    /// The setup dialog's load: opens the overlay again, filters to the chosen difficulty off the UI thread, stores the
    /// go-around probability for the scenario when the dialog offered it, and sends the scenario.
    /// </summary>
    private async Task SendConfirmedScenarioAsync(
        string json,
        string? level,
        string? apiId,
        bool saveGoAround,
        (int ParkingRate, int ArrivalRate, int GoAroundProbability) rates
    )
    {
        LoadOverlay.BeginLocal(ScenarioLoadTitle(fromCatalog: apiId is not null));
        try
        {
            (string scenarioJson, List<string> warnings, string scenarioId) = await Task.Run(() => FilterAndResolve(json, level));
            foreach (string w in warnings)
            {
                AddWarningEntry($"[WARN] {w}");
            }

            if (saveGoAround)
            {
                _preferences.SetSoloGoAroundProbabilityForScenario(scenarioId, rates.GoAroundProbability);
            }

            await SendScenarioToServer(scenarioJson, apiId, rates.ParkingRate, rates.ArrivalRate, rates.GoAroundProbability);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario load error");
            LoadOverlay.ApplyRefusal();
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

        if (!result.Success)
        {
            ReportLoadFailure(result);
            return;
        }

        LoadOverlay.ApplyResult(result);
        ApplyScenarioResult(result);
        RememberLoadedScenario(result, apiId);
        StatusText = $"Loaded '{result.Name}': " + $"{result.AllAircraft.Count} aircraft";
        AddSystemEntry($"Scenario loaded: {result.Name}" + $" ({result.AllAircraft.Count} aircraft)");

        // Warnings are already displayed and logged via PendingBroadcasts from the server.
    }

    /// <summary>
    /// A load the server did not complete. With a step table the overlay stays open on the failed step; a refusal before
    /// the load started has none and closes it. Either way the server's reason reaches the terminal and the status bar.
    /// </summary>
    private void ReportLoadFailure(LoadScenarioResultDto result)
    {
        if (result.Steps.Count > 0)
        {
            LoadOverlay.ApplyResult(result);
        }
        else
        {
            LoadOverlay.ApplyRefusal();
        }

        string? reason = result.Warnings.FirstOrDefault();
        _log.LogWarning("Scenario load failed: {Reason}", reason ?? "(no reason given)");
        if (reason is null)
        {
            StatusText = "Scenario load failed";
            return;
        }

        StatusText = reason;
        AddSystemEntry(reason);
    }

    private void RememberLoadedScenario(LoadScenarioResultDto result, string? apiId)
    {
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
    }

    /// <summary>
    /// A scenario load progress event for this client's load, raised on a SignalR thread: applied to the overlay on the UI
    /// thread. It may arrive before or after the load's RPC returns.
    /// </summary>
    internal void OnScenarioLoadProgress(ScenarioLoadProgressDto progress) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LoadOverlay.ApplyProgress(progress));

    /// <summary>
    /// The room's load flag changed (<c>RoomLoadingChanged</c>, raised on a SignalR thread): a member's load started
    /// (<paramref name="loadingBy"/>, their initials) or ended (null).
    /// </summary>
    internal void OnRoomLoadingChanged(string? loadingBy) => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetRoomLoadingBy(loadingBy));

    /// <summary>
    /// Records who is loading a scenario in the room and, while someone is, says so in the status bar. The load
    /// ending replaces that status line with a note of its own, unless something else has spoken since.
    /// </summary>
    private void SetRoomLoadingBy(string? loadingBy)
    {
        string? previous = RoomLoadingBy;
        RoomLoadingBy = loadingBy;
        if (loadingBy is not null)
        {
            StatusText = RoomLoadingStatusText(loadingBy);
            return;
        }

        if ((previous is not null) && (StatusText == RoomLoadingStatusText(previous)))
        {
            StatusText = $"Load by {previous} ended";
        }
    }

    partial void OnRoomLoadingByChanged(string? value) => NotifyRoomLoadingChanged();

    /// <summary>Re-evaluates every command a running scenario load disables.</summary>
    private void NotifyRoomLoadingChanged()
    {
        OnPropertyChanged(nameof(IsRoomLoading));
        OnPropertyChanged(nameof(CanLoadScenario));
        OnPropertyChanged(nameof(CanStartLiveSession));
        LoadScenarioCommand.NotifyCanExecuteChanged();
        UnloadScenarioCommand.NotifyCanExecuteChanged();
        RestartScenarioCommand.NotifyCanExecuteChanged();
        NotifyRewindCommandsCanExecuteChanged();
    }

    /// <summary>
    /// The status line while a member's load runs. It never names the scenario: mid-load the room state still carries the
    /// previous scenario's name.
    /// </summary>
    private static string RoomLoadingStatusText(string initials) => $"Loading a scenario (by {initials})…";

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

    private bool CanUnloadScenario() => CanExecuteInRoom && HasScenario && !IsNonMentor && !IsRoomLoading;

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

    private bool CanRestartScenario() => CanExecuteInRoom && HasScenario && !IsRoomLoading;

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
        ResetPilotVoiceWarningSession();
        SetStudentPositionType(result.StudentPositionType);
        _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);
        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.ShowMvaHints = _preferences.GetMvaHintDefault(_studentPositionType);
        }

        ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = result.ScenarioId,
                ScenarioName = result.Name,
                PrimaryAirportId = result.PrimaryAirportId,
                PositionDisplayConfig = result.PositionDisplayConfig,
                FlightStripsConfig = result.FlightStripsConfig,
                Aircraft = result.AllAircraft,
                ElapsedSeconds = 0,
            }
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

    internal void OnScenarioLoaded(ScenarioLoadedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _log.LogInformation("Scenario loaded by another client: '{Name}' ({Id})", dto.ScenarioName, dto.ScenarioId);
            RoomLoadingBy = null;
            ResetPilotVoiceWarningSession();

            SetStudentPositionType(dto.StudentPositionType);
            _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);
            foreach (RadarViewModel radar in AllRadarViews)
            {
                radar.ShowMvaHints = _preferences.GetMvaHintDefault(_studentPositionType);
            }

            ApplyScenarioBootstrap(
                new ScenarioBootstrap
                {
                    ScenarioId = dto.ScenarioId,
                    ScenarioName = dto.ScenarioName,
                    PrimaryAirportId = dto.PrimaryAirportId,
                    PositionDisplayConfig = dto.PositionDisplayConfig,
                    FlightStripsConfig = dto.FlightStripsConfig,
                    Aircraft = dto.AllAircraft,
                    ElapsedSeconds = 0,
                }
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
