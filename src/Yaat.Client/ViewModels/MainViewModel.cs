using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ILogger _log = AppLog.CreateLogger<MainViewModel>();

    private readonly ServerConnection _connection = new();
    private readonly UserPreferences _preferences = new();
    private readonly CommandInputController _commandInput = new();

    public UserPreferences Preferences => _preferences;
    public CommandInputController CommandInput => _commandInput;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAllCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000";

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private string _scenarioFilePath = "";

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private int _simRate = 1;

    [ObservableProperty]
    private int _selectedSimRateIndex;

    public static int[] SimRateOptions { get; } = [1, 2, 4, 8, 16];

    [ObservableProperty]
    private string _commandSchemeName = "ATCTrainer";

    [ObservableProperty]
    private string? _activeScenarioId;

    [ObservableProperty]
    private string? _activeScenarioName;

    [ObservableProperty]
    private int _scenarioClientCount;

    [ObservableProperty]
    private bool _showDeleteAllConfirmation;

    [ObservableProperty]
    private string? _pendingDeleteAllWarning;

    [ObservableProperty]
    private bool _showScenarioSwitchConfirmation;

    [ObservableProperty]
    private bool _showActiveScenarios;

    [ObservableProperty]
    private bool _isTerminalDocked = true;

    [ObservableProperty]
    private string _terminalText = "";

    [ObservableProperty]
    private string _distanceReferenceFix = "";

    private double? _distanceRefLat;
    private double? _distanceRefLon;

    public ObservableCollection<AircraftModel> Aircraft { get; } = [];

    public ObservableCollection<string> CommandHistory { get; } = [];

    public ObservableCollection<TerminalEntry> TerminalEntries { get; } = [];

    public ObservableCollection<ScenarioSessionInfoDto> ActiveScenarios { get; } = [];

    public MainViewModel()
    {
        _connection.AircraftUpdated += OnAircraftUpdated;
        _connection.AircraftDeleted += OnAircraftDeleted;
        _connection.AircraftSpawned += OnAircraftSpawned;
        _connection.SimulationStateChanged += OnSimulationStateChanged;
        _connection.Reconnected += OnReconnected;
        _connection.TerminalEntryReceived += OnTerminalEntry;

        RefreshCommandScheme();

        _ = InitializeNavDataAsync();
    }

    private async Task InitializeNavDataAsync()
    {
        try
        {
            var logger = AppLog.CreateLogger<VnasDataService>();
            using var vnasData = new VnasDataService(logger);
            await vnasData.InitializeAsync();

            var fixDb = new FixDatabase(
                vnasData.NavData,
                logger: AppLog.CreateLogger<FixDatabase>());

            _commandInput.FixDb = fixDb;
            _log.LogInformation(
                "Navdata loaded: {Count} fixes available for autocomplete",
                fixDb.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Navdata initialization failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (_preferences.UserInitials.Length != 2)
        {
            StatusText = "Set your 2-letter initials in Settings before connecting";
            return;
        }

        try
        {
            StatusText = "Connecting...";
            await _connection.ConnectAsync(ServerUrl);
            IsConnected = true;
            StatusText = "Connected";

            // Check for resumable scenarios
            var scenarios = await _connection.GetActiveScenariosAsync();
            if (scenarios.Count > 0)
            {
                ActiveScenarios.Clear();
                foreach (var s in scenarios)
                {
                    ActiveScenarios.Add(s);
                }
                ShowActiveScenarios = true;
            }

            var list = await _connection.GetAircraftListAsync();
            Aircraft.Clear();
            foreach (var dto in list)
            {
                Aircraft.Add(AircraftModel.FromDto(dto, ComputeDistance));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Connection failed");
            StatusText = $"Error: {ex.Message}";
            IsConnected = false;
        }
    }

    private static bool CanToggleConnect() => true;

    private async Task DisconnectAsync()
    {
        if (ActiveScenarioId is not null)
        {
            try
            {
                await _connection.LeaveScenarioAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LeaveScenario on disconnect failed");
            }
        }

        await _connection.DisconnectAsync();
        IsConnected = false;
        StatusText = "Disconnected";
        Aircraft.Clear();
        ActiveScenarioId = null;
        ActiveScenarioName = null;
        ScenarioClientCount = 0;
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task LoadScenarioAsync()
    {
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

        try
        {
            await _connection.LeaveScenarioAsync();
            ActiveScenarioId = null;
            ActiveScenarioName = null;
            ScenarioClientCount = 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LeaveScenario on switch failed");
        }

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
            var result = await _connection.LoadScenarioAsync(json);

            if (result.Success)
            {
                ApplyScenarioResult(result);

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
                AddSystemEntry($"Scenario loaded: {result.Name} ({result.AllAircraft.Count} aircraft)");
            }
            else
            {
                _log.LogWarning("Scenario load failed");
                StatusText = "Scenario load failed";
            }

            foreach (var w in result.Warnings)
            {
                _log.LogWarning("Scenario: {Warning}", w);
                AddHistory($"[WARN] {w}");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario load error");
            StatusText = $"Load error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RejoinScenarioAsync(string scenarioId)
    {
        ShowActiveScenarios = false;
        try
        {
            var result = await _connection.RejoinScenarioAsync(scenarioId);
            if (result.Success)
            {
                ApplyScenarioResult(result);
                StatusText = $"Rejoined '{result.Name}': " + $"{result.AllAircraft.Count} aircraft";
            }
            else
            {
                StatusText = "Scenario no longer active";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rejoin failed");
            StatusText = $"Rejoin error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DismissActiveScenarios()
    {
        ShowActiveScenarios = false;
    }

    // --- Delete All ---

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DeleteAllAsync()
    {
        if (ActiveScenarioId is null)
        {
            StatusText = "No active scenario";
            return;
        }

        try
        {
            var result = await _connection.DeleteAllAircraftAsync();

            if (result.RequiresConfirmation)
            {
                PendingDeleteAllWarning = result.Message;
                ShowDeleteAllConfirmation = true;
                return;
            }

            Aircraft.Clear();
            ActiveScenarioId = null;
            ActiveScenarioName = null;
            ScenarioClientCount = 0;
            StatusText = "All aircraft deleted";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DeleteAll failed");
            StatusText = $"Delete error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConfirmDeleteAllAsync()
    {
        ShowDeleteAllConfirmation = false;
        try
        {
            await _connection.ConfirmDeleteAllAsync();
            Aircraft.Clear();
            ActiveScenarioId = null;
            ActiveScenarioName = null;
            ScenarioClientCount = 0;
            StatusText = "All aircraft deleted";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ConfirmDeleteAll failed");
            StatusText = $"Delete error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelDeleteAll()
    {
        ShowDeleteAllConfirmation = false;
        PendingDeleteAllWarning = null;
    }

    partial void OnCommandTextChanged(string value)
    {
        _commandInput.UpdateSuggestions(
            value, Aircraft, _preferences.CommandScheme, SelectedAircraft);
    }

    // --- Commands ---

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task SendCommandAsync()
    {
        var text = CommandText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Chat messages: ' / > prefix → broadcast text, not a command
        if (text.Length > 1 && (text[0] == '\'' || text[0] == '/' || text[0] == '>'))
        {
            var chatMessage = text[1..].TrimStart();
            if (!string.IsNullOrEmpty(chatMessage))
            {
                try
                {
                    await _connection.SendChatAsync(_preferences.UserInitials, chatMessage);
                    AddHistory(text);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Chat send failed");
                    StatusText = $"Chat error: {ex.Message}";
                }
            }
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
            return;
        }

        var scheme = _preferences.CommandScheme;

        // Check for global commands first (no callsign needed)
        var globalParsed = CommandSchemeParser.Parse(text, scheme);
        if (globalParsed is not null && IsGlobalCommand(globalParsed.Type))
        {
            await HandleGlobalCommand(globalParsed);
            return;
        }

        // Try to resolve callsign prefix from the input
        var commandText = text;
        AircraftModel? target = SelectedAircraft;

        var resolved = TryResolveCallsignPrefix(text, scheme);
        if (resolved is not null)
        {
            target = resolved.Value.Aircraft;
            commandText = resolved.Value.Remainder;
        }

        // Parse as compound command (handles single and multi-block)
        var compound = CommandSchemeParser.ParseCompound(commandText, scheme);
        if (compound is null)
        {
            // If the first token is a known callsign, report the command as the problem
            var tokens = commandText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2 && ResolveAircraft(tokens[0]) is not null)
            {
                var cmdVerb = tokens[1].Split([' ', ',', ';'], 2)[0];
                StatusText = $"Unrecognized command \"{cmdVerb}\" — type a command like FH 270, CM 240, CTL";
            }
            else
            {
                var verb = commandText.Split([' ', ',', ';'], 2)[0];
                StatusText = $"Unrecognized command \"{verb}\" — type a command like FH 270, CM 240, CTL";
            }

            return;
        }

        if (target is null)
        {
            StatusText = "No aircraft matched — type a callsign (or partial) before the command";
            return;
        }

        SelectedAircraft = target;

        try
        {
            var result = await _connection.SendCommandAsync(
                target.Callsign, compound.CanonicalString, _preferences.UserInitials);

            AddHistory(commandText);

            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Command failed");
            StatusText = $"Command error: {ex.Message}";
        }
    }

    private async Task HandleGlobalCommand(ParsedInput parsed)
    {
        if (parsed.Type == CanonicalCommandType.Pause)
        {
            await _connection.PauseSimulationAsync();
            AddHistory("PAUSE");
            CommandText = "";
            return;
        }
        if (parsed.Type == CanonicalCommandType.Unpause)
        {
            await _connection.ResumeSimulationAsync();
            AddHistory("UNPAUSE");
            CommandText = "";
            return;
        }
        if (parsed.Type == CanonicalCommandType.SimRate)
        {
            if (int.TryParse(parsed.Argument, out var rate))
            {
                await _connection.SetSimRateAsync(rate);
                AddHistory($"SIMRATE {rate}");
            }
            CommandText = "";
            return;
        }
        if (parsed.Type == CanonicalCommandType.Add)
        {
            if (string.IsNullOrWhiteSpace(parsed.Argument))
            {
                StatusText = "ADD requires arguments: ADD {rules} {weight} {engine} {position...}";
                return;
            }
            try
            {
                var result = await _connection.SpawnAircraftAsync(parsed.Argument);
                if (result.Success)
                {
                    AddHistory($"ADD {parsed.Argument}");
                    StatusText = result.Message ?? "Aircraft spawned";
                }
                else
                {
                    StatusText = result.Message ?? "Spawn failed";
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "SpawnAircraft failed");
                StatusText = $"Spawn error: {ex.Message}";
            }
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
        }
    }

    private static bool IsGlobalCommand(CanonicalCommandType type)
    {
        return type is CanonicalCommandType.Pause or CanonicalCommandType.Unpause
            or CanonicalCommandType.SimRate or CanonicalCommandType.Add;
    }

    /// <summary>
    /// Tries to resolve the first token of the input as a full or partial callsign.
    /// Returns the matched aircraft and the remainder of the input (the command part).
    /// Returns null if no unique match is found.
    /// </summary>
    private (AircraftModel Aircraft, string Remainder)? TryResolveCallsignPrefix(
        string input,
        CommandScheme scheme
    )
    {
        // Split into first token (potential callsign) and remainder (the command)
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var token = parts[0];
        var remainder = parts[1].Trim();

        // Only consider this a callsign if the remainder parses as a valid command
        var remainderParsed = CommandSchemeParser.ParseCompound(remainder, scheme);
        if (remainderParsed is null)
        {
            return null;
        }

        var match = ResolveAircraft(token);
        if (match is null)
        {
            return null;
        }

        return (match, remainder);
    }

    /// <summary>
    /// Resolves a full or partial callsign to a single spawned aircraft.
    /// Returns null and sets StatusText if no match or ambiguous.
    /// </summary>
    private AircraftModel? ResolveAircraft(string token)
    {
        // Exact match first (case-insensitive)
        var exact = Aircraft.FirstOrDefault(
            a => string.Equals(a.Callsign, token, StringComparison.OrdinalIgnoreCase)
        );
        if (exact is not null)
        {
            return exact;
        }

        // Partial match: substring anywhere in callsign
        var matches = Aircraft
            .Where(a => a.Callsign.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            var names = string.Join(", ", matches.Select(a => a.Callsign).Take(5));
            StatusText = $"\"{token}\" matches multiple aircraft: {names}";
            return null;
        }

        return null;
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task TogglePauseAsync()
    {
        try
        {
            if (IsPaused)
            {
                await _connection.ResumeSimulationAsync();
            }
            else
            {
                await _connection.PauseSimulationAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pause/resume failed");
            StatusText = $"Pause error: {ex.Message}";
        }
    }

    partial void OnSelectedSimRateIndexChanged(int value)
    {
        if (value < 0 || value >= SimRateOptions.Length)
        {
            return;
        }

        var rate = SimRateOptions[value];
        if (rate == SimRate)
        {
            return;
        }

        _ = SetSimRateFromDropdownAsync(rate);
    }

    private async Task SetSimRateFromDropdownAsync(int rate)
    {
        try
        {
            await _connection.SetSimRateAsync(rate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SimRate change failed");
            StatusText = $"SimRate error: {ex.Message}";
        }
    }

    public void RefreshCommandScheme()
    {
        CommandSchemeName = CommandScheme.DetectPresetName(_preferences.CommandScheme) ?? "Custom";

        if (ActiveScenarioId is not null)
        {
            _ = SendAutoAcceptDelay();
        }
    }

    public void SetDistanceReference(string fixOrFrd)
    {
        if (string.IsNullOrWhiteSpace(fixOrFrd))
        {
            _distanceRefLat = null;
            _distanceRefLon = null;
            DistanceReferenceFix = "";
            ClearAllDistances();
            return;
        }

        var fixDb = _commandInput.FixDb;
        if (fixDb is null)
        {
            _log.LogWarning(
                "Cannot set distance reference — navdata not loaded");
            return;
        }

        var resolved = FrdResolver.Resolve(fixOrFrd, fixDb);
        if (resolved is null)
        {
            _log.LogWarning(
                "Distance reference '{Fix}' could not be resolved", fixOrFrd);
            StatusText = $"Unknown fix: {fixOrFrd}";
            return;
        }

        _distanceRefLat = resolved.Latitude;
        _distanceRefLon = resolved.Longitude;
        DistanceReferenceFix = FrdResolver.ParseFrd(fixOrFrd)?.Fix
            ?? fixOrFrd.Trim().ToUpperInvariant();
        RecalculateAllDistances();
    }

    private void RecalculateAllDistances()
    {
        foreach (var ac in Aircraft)
        {
            ac.DistanceFromFix = ComputeDistance(ac);
        }
    }

    private void ClearAllDistances()
    {
        foreach (var ac in Aircraft)
        {
            ac.DistanceFromFix = null;
        }
    }

    private double? ComputeDistance(AircraftModel model)
    {
        if (_distanceRefLat is null || _distanceRefLon is null)
        {
            return null;
        }

        if (model.IsDelayedOrDeferred)
        {
            return null;
        }

        return GeoMath.DistanceNm(
            model.Latitude, model.Longitude,
            _distanceRefLat.Value, _distanceRefLon.Value);
    }

    [RelayCommand]
    private void ToggleTerminalDock()
    {
        IsTerminalDocked = !IsTerminalDocked;
    }

    private void AddTerminalEntry(TerminalEntry entry)
    {
        TerminalEntries.Add(entry);
        while (TerminalEntries.Count > 500)
        {
            TerminalEntries.RemoveAt(0);
        }
        RebuildTerminalText();
    }

    private void RebuildTerminalText()
    {
        var sb = new StringBuilder();
        foreach (var entry in TerminalEntries)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.Append(entry.Timestamp.ToString("HH:mm:ss"));
            if (!string.IsNullOrEmpty(entry.Initials))
            {
                sb.Append("  ");
                sb.Append(entry.Initials);
            }
            if (!string.IsNullOrEmpty(entry.Callsign))
            {
                sb.Append("  ");
                sb.Append(entry.Callsign);
            }
            sb.Append("  ");
            sb.Append(entry.Message);
        }
        TerminalText = sb.ToString();
    }

    public void AddSystemEntry(string message)
    {
        AddTerminalEntry(new TerminalEntry
        {
            Timestamp = DateTime.Now,
            Initials = "",
            Kind = TerminalEntryKind.System,
            Callsign = "",
            Message = message,
        });
    }

    private void OnTerminalEntry(TerminalBroadcastDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var kind = Enum.TryParse<TerminalEntryKind>(dto.Kind, out var k) ? k : TerminalEntryKind.System;
            AddTerminalEntry(new TerminalEntry
            {
                Timestamp = dto.Timestamp.ToLocalTime(),
                Initials = dto.Initials,
                Kind = kind,
                Callsign = dto.Callsign,
                Message = dto.Message,
            });
        });
    }

    // --- Event handlers ---

    private void OnAircraftUpdated(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                existing.UpdateFromDto(dto, ComputeDistance);
            }
            else
            {
                Aircraft.Add(AircraftModel.FromDto(dto, ComputeDistance));
            }
        });
    }

    private void OnAircraftDeleted(string callsign)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var ac = FindAircraft(callsign);
            if (ac is not null)
            {
                Aircraft.Remove(ac);
            }
        });
    }

    private void OnAircraftSpawned(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                existing.UpdateFromDto(dto, ComputeDistance);
            }
            else
            {
                Aircraft.Add(AircraftModel.FromDto(dto, ComputeDistance));
            }
        });
    }

    private void OnSimulationStateChanged(bool paused, int rate)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ApplySimState(paused, rate);
        });
    }

    private void OnReconnected(string? connectionId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (ActiveScenarioId is null)
            {
                return;
            }

            try
            {
                var result = await _connection.RejoinScenarioAsync(ActiveScenarioId);
                if (result.Success)
                {
                    ApplyScenarioResult(result);
                    StatusText = "Reconnected to scenario";
                    AddSystemEntry("Reconnected to scenario");
                }
                else
                {
                    StatusText = "Scenario no longer active";
                    ActiveScenarioId = null;
                    ActiveScenarioName = null;
                    ScenarioClientCount = 0;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Rejoin after reconnect failed");
            }
        });
    }

    // --- Helpers ---

    private void ApplyScenarioResult(LoadScenarioResultDto result)
    {
        ActiveScenarioId = result.ScenarioId;
        ActiveScenarioName = result.Name;
        _commandInput.PrimaryAirportId = result.PrimaryAirportId;
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
    }

    private async Task SendAutoAcceptDelay()
    {
        try
        {
            var seconds = _preferences.AutoAcceptEnabled
                ? _preferences.AutoAcceptDelaySeconds
                : -1;
            await _connection.SetAutoAcceptDelayAsync(seconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set auto-accept delay");
        }
    }

    private void ApplySimState(bool paused, int rate)
    {
        IsPaused = paused;
        SimRate = rate;
        SelectedSimRateIndex = Array.IndexOf(SimRateOptions, rate);
    }

    private void AddHistory(string entry)
    {
        CommandHistory.Insert(0, entry);
        while (CommandHistory.Count > 50)
        {
            CommandHistory.RemoveAt(CommandHistory.Count - 1);
        }
    }

    private AircraftModel? FindAircraft(string callsign)
    {
        foreach (var a in Aircraft)
        {
            if (a.Callsign == callsign)
            {
                return a;
            }
        }
        return null;
    }

}
