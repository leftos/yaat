using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
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
    private readonly VideoMapService _videoMapService = new();

    public UserPreferences Preferences => _preferences;
    public CommandInputController CommandInput => _commandInput;

    public GroundViewModel Ground { get; }
    public RadarViewModel Radar { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnloadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowRoomsCommand))]
    private bool _isConnected;

    public string ServerUrl
    {
        get => _preferences.ServerUrl;
        set
        {
            if (_preferences.ServerUrl != value)
            {
                _preferences.SetServerUrl(value);
                OnPropertyChanged();
            }
        }
    }

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
    [NotifyCanExecuteChangedFor(nameof(LeaveRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowRoomsCommand))]
    private string? _activeRoomId;

    [ObservableProperty]
    private string? _activeRoomName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnloadScenarioCommand))]
    private string? _activeScenarioId;

    [ObservableProperty]
    private string? _activeScenarioName;

    [ObservableProperty]
    private bool _showUnloadScenarioConfirmation;

    [ObservableProperty]
    private string? _pendingUnloadScenarioWarning;

    [ObservableProperty]
    private bool _showScenarioSwitchConfirmation;

    [ObservableProperty]
    private bool _showDifficultySelection;

    [ObservableProperty]
    private int _selectedDifficultyIndex;

    private string? _pendingScenarioJson;

    public ObservableCollection<DifficultyOption> DifficultyOptions { get; } = [];

    [ObservableProperty]
    private bool _showRoomList;

    [ObservableProperty]
    private bool _showCrcPanel;

    [ObservableProperty]
    private bool _isTerminalDocked = true;

    [ObservableProperty]
    private bool _isDataGridPoppedOut;

    [ObservableProperty]
    private bool _isGroundViewPoppedOut;

    [ObservableProperty]
    private bool _isRadarViewPoppedOut;

    partial void OnIsDataGridPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("DataGrid", value);
    }

    partial void OnIsGroundViewPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("GroundView", value);
    }

    partial void OnIsRadarViewPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("RadarView", value);
    }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _distanceReferenceFix = "";

    public string WindowTitle
    {
        get
        {
            if (ActiveRoomName is null)
            {
                return "YAAT";
            }

            return ActiveScenarioName is not null ? $"{ActiveRoomName} ({ActiveScenarioName}) - YAAT" : $"{ActiveRoomName} - YAAT";
        }
    }

    public string ConnectMenuText => IsConnected ? "_Disconnect" : "_Connect";

    public bool IsInRoom => ActiveRoomId is not null;

    public bool HasScenario => ActiveScenarioId is not null;

    private double? _distanceRefLat;
    private double? _distanceRefLon;

    public ObservableCollection<AircraftModel> Aircraft { get; } = [];

    public DataGridCollectionView AircraftView { get; }

    [ObservableProperty]
    private bool _filterActiveOnly;

    partial void OnFilterActiveOnlyChanged(bool value)
    {
        AircraftView.Filter = value ? obj => obj is AircraftModel ac && !ac.IsDelayedOrDeferred : null;
    }

    public ObservableCollection<string> CommandHistory { get; } = [];

    public ObservableCollection<TerminalEntry> TerminalEntries { get; } = [];

    public ObservableCollection<TrainingRoomInfoDto> ActiveRooms { get; } = [];

    public ObservableCollection<RoomMemberDto> RoomMembers { get; } = [];

    public ObservableCollection<CrcLobbyClientDto> CrcLobbyClients { get; } = [];

    public ObservableCollection<CrcRoomMemberDto> CrcRoomMembers { get; } = [];

    public MainViewModel()
    {
        AircraftView = new DataGridCollectionView(Aircraft);
        Ground = new GroundViewModel(_connection, SendCommandForViewAsync);
        Radar = new RadarViewModel(_connection, _videoMapService, SendCommandForViewAsync);
        Radar.SetPreferences(_preferences);

        IsDataGridPoppedOut = _preferences.IsDataGridPoppedOut;
        IsGroundViewPoppedOut = _preferences.IsGroundViewPoppedOut;
        IsRadarViewPoppedOut = _preferences.IsRadarViewPoppedOut;

        _connection.AircraftUpdated += OnAircraftUpdated;
        _connection.AircraftDeleted += OnAircraftDeleted;
        _connection.AircraftSpawned += OnAircraftSpawned;
        _connection.SimulationStateChanged += OnSimulationStateChanged;
        _connection.Reconnecting += OnReconnecting;
        _connection.Reconnected += OnReconnected;
        _connection.Closed += OnConnectionClosed;
        _connection.TerminalEntryReceived += OnTerminalEntry;
        _connection.RoomMemberChanged += OnRoomMemberChanged;
        _connection.CrcLobbyChanged += OnCrcLobbyChanged;
        _connection.CrcRoomMembersChanged += OnCrcRoomMembersChanged;

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

            var fixDb = new FixDatabase(vnasData.NavData, logger: AppLog.CreateLogger<FixDatabase>());

            _commandInput.FixDb = fixDb;
            Radar.SetElevationLookup(fixDb.GetAirportElevation);
            _log.LogInformation("Navdata loaded: {Count} fixes available for autocomplete", fixDb.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Navdata initialization failed");
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectMenuText));
    }

    partial void OnActiveRoomIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsInRoom));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnActiveRoomNameChanged(string? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnActiveScenarioIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScenario));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnActiveScenarioNameChanged(string? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        Ground.SelectedAircraft = value;
        Radar.SelectedAircraft = value;
    }

    partial void OnCommandTextChanged(string value)
    {
        _commandInput.UpdateSuggestions(value, Aircraft, _preferences.CommandScheme, SelectedAircraft);
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
            var result = await _connection.SendCommandAsync(target.Callsign, compound.CanonicalString, _preferences.UserInitials);

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
        if (parsed.Type is CanonicalCommandType.SquawkAll or CanonicalCommandType.SquawkNormalAll or CanonicalCommandType.SquawkStandbyAll)
        {
            var verb = parsed.Type switch
            {
                CanonicalCommandType.SquawkAll => "SQALL",
                CanonicalCommandType.SquawkNormalAll => "SNALL",
                CanonicalCommandType.SquawkStandbyAll => "SSALL",
                _ => "",
            };
            try
            {
                await _connection.SendCommandAsync("", verb, _preferences.UserInitials);
                AddHistory(verb);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Verb} failed", verb);
                StatusText = $"{verb} error: {ex.Message}";
            }
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
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
        return type
            is CanonicalCommandType.Pause
                or CanonicalCommandType.Unpause
                or CanonicalCommandType.SimRate
                or CanonicalCommandType.Add
                or CanonicalCommandType.SquawkAll
                or CanonicalCommandType.SquawkNormalAll
                or CanonicalCommandType.SquawkStandbyAll;
    }

    /// <summary>
    /// Tries to resolve the first token of the input as a full or partial callsign.
    /// Returns the matched aircraft and the remainder of the input (the command part).
    /// Returns null if no unique match is found.
    /// </summary>
    private (AircraftModel Aircraft, string Remainder)? TryResolveCallsignPrefix(string input, CommandScheme scheme)
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
        var exact = Aircraft.FirstOrDefault(a => string.Equals(a.Callsign, token, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // Partial match: substring anywhere in callsign
        var matches = Aircraft.Where(a => a.Callsign.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();

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
        if (ActiveRoomId is not null)
        {
            _ = SendAutoAcceptDelay();
            _ = SendAutoDeleteMode();
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
            _log.LogWarning("Cannot set distance reference — navdata not loaded");
            return;
        }

        var resolved = FrdResolver.Resolve(fixOrFrd, fixDb);
        if (resolved is null)
        {
            _log.LogWarning("Distance reference '{Fix}' could not be resolved", fixOrFrd);
            StatusText = $"Unknown fix: {fixOrFrd}";
            return;
        }

        _distanceRefLat = resolved.Latitude;
        _distanceRefLon = resolved.Longitude;
        DistanceReferenceFix = FrdResolver.ParseFrd(fixOrFrd)?.Fix ?? fixOrFrd.Trim().ToUpperInvariant();
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

        return GeoMath.DistanceNm(model.Latitude, model.Longitude, _distanceRefLat.Value, _distanceRefLon.Value);
    }

    [RelayCommand]
    private void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    [RelayCommand]
    private void ToggleTerminalDock()
    {
        IsTerminalDocked = !IsTerminalDocked;
    }

    private async Task SendCommandForViewAsync(string callsign, string command, string initials)
    {
        try
        {
            await _connection.SendCommandAsync(callsign, command, initials);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "View command failed: {Cmd}", command);
            StatusText = $"Command error: {ex.Message}";
        }
    }

    // --- Helpers ---

    private async Task SendAutoAcceptDelay()
    {
        try
        {
            var seconds = _preferences.AutoAcceptEnabled ? _preferences.AutoAcceptDelaySeconds : -1;
            await _connection.SetAutoAcceptDelayAsync(seconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set auto-accept delay");
        }
    }

    private async Task SendAutoDeleteMode()
    {
        try
        {
            var override_ = _preferences.AutoDeleteOverride;
            string? mode = string.IsNullOrEmpty(override_) ? null : override_;
            await _connection.SetAutoDeleteModeAsync(mode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set auto-delete mode");
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
}
