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
    public ServerConnection Connection => _connection;
    private readonly UserPreferences _preferences = new();
    private readonly CommandInputController _commandInput = new();
    private readonly VideoMapService _videoMapService = new();

    public UserPreferences Preferences => _preferences;
    public CommandInputController CommandInput => _commandInput;

    private string _connectedServerUrl = "";
    private bool _isSyncingSelection;
    private string? _studentPositionType;

    public GroundViewModel Ground { get; }
    public RadarViewModel Radar { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnloadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowRoomsCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadWeatherCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearWeatherCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLiveWeatherCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnecting;

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
    private double _scenarioElapsedSeconds;

    [ObservableProperty]
    private bool _isPlaybackMode;

    [ObservableProperty]
    private double _playbackTapeEnd;

    public bool IsTimelineAvailable => ActiveScenarioName is not null && ShowTimelineBar;

    public string PlayPauseIcon => IsPaused ? "▶" : "⏸";

    public string ElapsedTimeDisplay
    {
        get
        {
            var ts = TimeSpan.FromSeconds(ScenarioElapsedSeconds);
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        }
    }

    public string TapeEndDisplay
    {
        get
        {
            var ts = TimeSpan.FromSeconds(PlaybackTapeEnd);
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LeaveRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowRoomsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnloadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadWeatherCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearWeatherCommand))]
    private string? _activeRoomId;

    [ObservableProperty]
    private string? _activeRoomName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnloadScenarioCommand))]
    private string? _activeScenarioId;

    [ObservableProperty]
    private string? _activeScenarioName;

    /// <summary>
    /// Whether the student's position is a tower (LC/TWR) position. When false, AutoClearedToLand is
    /// forced on by the server and the setting is non-editable.
    /// </summary>
    [ObservableProperty]
    private bool _isStudentTowerPosition = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearWeatherCommand))]
    private string? _activeWeatherName;

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
        if (value && SelectedTabIndex == 0)
        {
            SelectedTabIndex = FindNextVisibleTabIndex(0);
        }
    }

    partial void OnIsGroundViewPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("GroundView", value);
        if (value && SelectedTabIndex == 1)
        {
            SelectedTabIndex = FindNextVisibleTabIndex(1);
        }
    }

    partial void OnIsRadarViewPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("RadarView", value);
        if (value && SelectedTabIndex == 2)
        {
            SelectedTabIndex = FindNextVisibleTabIndex(2);
        }
    }

    private int FindNextVisibleTabIndex(int currentIndex)
    {
        // Tab 0: Aircraft List, Tab 1: Ground View, Tab 2: Radar View
        if (currentIndex == 0)
        {
            return IsGroundViewPoppedOut ? 2 : 1;
        }

        if (currentIndex == 1)
        {
            return IsDataGridPoppedOut ? 2 : 0;
        }

        if (currentIndex == 2)
        {
            return IsGroundViewPoppedOut ? 0 : 1;
        }

        return 0;
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

    public bool CanExecuteInRoom => IsConnected && IsInRoom;

    public bool HasScenario => ActiveScenarioId is not null;

    private double? _distanceRefLat;
    private double? _distanceRefLon;

    public ObservableCollection<AircraftModel> Aircraft { get; } = [];

    public DataGridCollectionView AircraftView { get; }

    [ObservableProperty]
    private string _aircraftFilterText = "";

    partial void OnAircraftFilterTextChanged(string value)
    {
        AircraftView.Refresh();
    }

    [ObservableProperty]
    private bool _showOnlyActiveAircraft;

    partial void OnShowOnlyActiveAircraftChanged(bool value)
    {
        _preferences.SetShowOnlyActiveAircraft(value);
        AircraftView.Refresh();
    }

    private static bool MatchesFilter(AircraftModel ac, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        return Contains(ac.Callsign, filter)
            || Contains(ac.AircraftType, filter)
            || ac.BeaconCode.ToString("D4").Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Contains(ac.Departure, filter)
            || Contains(ac.Destination, filter)
            || Contains(ac.Route, filter)
            || Contains(ac.AssignedRunway, filter)
            || Contains(ac.CurrentPhase, filter)
            || Contains(ac.Scratchpad1, filter)
            || Contains(ac.Scratchpad2, filter)
            || Contains(ac.ActiveApproachId, filter)
            || Contains(ac.OwnerDisplay, filter);

        static bool Contains(string? value, string filter) => value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    private bool _showTimelineBar;

    partial void OnShowTimelineBarChanged(bool value)
    {
        _preferences.SetShowTimelineBar(value);
        OnPropertyChanged(nameof(IsTimelineAvailable));
    }

    [RelayCommand]
    private void ResetGridLayout()
    {
        _preferences.ResetGridLayout();
        GridLayoutReset?.Invoke();
    }

    public event Action? GridLayoutReset;

    [ObservableProperty]
    private bool _showCommandEntries = true;

    [ObservableProperty]
    private bool _showResponseEntries = true;

    [ObservableProperty]
    private bool _showSystemEntries = true;

    [ObservableProperty]
    private bool _showSayEntries = true;

    [ObservableProperty]
    private bool _showWarningEntries = true;

    [ObservableProperty]
    private bool _showErrorEntries = true;

    [ObservableProperty]
    private bool _showChatEntries = true;

    public event Action? TerminalFilterChanged;

    partial void OnShowCommandEntriesChanged(bool value) => PersistTerminalFilters();

    partial void OnShowResponseEntriesChanged(bool value) => PersistTerminalFilters();

    partial void OnShowSystemEntriesChanged(bool value) => PersistTerminalFilters();

    partial void OnShowSayEntriesChanged(bool value) => PersistTerminalFilters();

    partial void OnShowWarningEntriesChanged(bool value) => PersistTerminalFilters();

    partial void OnShowErrorEntriesChanged(bool value) => PersistTerminalFilters();

    partial void OnShowChatEntriesChanged(bool value) => PersistTerminalFilters();

    private void PersistTerminalFilters()
    {
        var hidden = new HashSet<TerminalEntryKind>();
        if (!ShowCommandEntries)
        {
            hidden.Add(TerminalEntryKind.Command);
        }

        if (!ShowResponseEntries)
        {
            hidden.Add(TerminalEntryKind.Response);
        }

        if (!ShowSystemEntries)
        {
            hidden.Add(TerminalEntryKind.System);
        }

        if (!ShowSayEntries)
        {
            hidden.Add(TerminalEntryKind.Say);
        }

        if (!ShowWarningEntries)
        {
            hidden.Add(TerminalEntryKind.Warning);
        }

        if (!ShowErrorEntries)
        {
            hidden.Add(TerminalEntryKind.Error);
        }

        if (!ShowChatEntries)
        {
            hidden.Add(TerminalEntryKind.Chat);
        }

        _preferences.SetHiddenTerminalKinds(hidden);
        TerminalFilterChanged?.Invoke();
    }

    public bool IsEntryVisible(TerminalEntryKind kind) =>
        kind switch
        {
            TerminalEntryKind.Command => ShowCommandEntries,
            TerminalEntryKind.Response => ShowResponseEntries,
            TerminalEntryKind.System => ShowSystemEntries,
            TerminalEntryKind.Say => ShowSayEntries,
            TerminalEntryKind.Warning => ShowWarningEntries,
            TerminalEntryKind.Error => ShowErrorEntries,
            TerminalEntryKind.Chat => ShowChatEntries,
            _ => true,
        };

    public IEnumerable<TerminalEntry> GetFilteredTerminalEntries() => TerminalEntries.Where(e => IsEntryVisible(e.Kind));

    public ObservableCollection<string> CommandHistory { get; } = [];

    public ObservableCollection<TerminalEntry> TerminalEntries { get; } = [];

    public ObservableCollection<TrainingRoomInfoDto> ActiveRooms { get; } = [];

    public ObservableCollection<RoomMemberDto> RoomMembers { get; } = [];

    public ObservableCollection<CrcLobbyClientDto> CrcLobbyClients { get; } = [];

    public ObservableCollection<CrcRoomMemberDto> CrcRoomMembers { get; } = [];

    public MainViewModel()
    {
        AircraftView = new DataGridCollectionView(Aircraft);
        AircraftView.Filter = obj =>
            obj is not AircraftModel ac || (!_showOnlyActiveAircraft || !ac.IsDelayed) && MatchesFilter(ac, _aircraftFilterText);
        _showOnlyActiveAircraft = _preferences.ShowOnlyActiveAircraft;
        _showTimelineBar = _preferences.ShowTimelineBar;

        var hidden = _preferences.HiddenTerminalKinds;
        _showCommandEntries = !hidden.Contains(TerminalEntryKind.Command);
        _showResponseEntries = !hidden.Contains(TerminalEntryKind.Response);
        _showSystemEntries = !hidden.Contains(TerminalEntryKind.System);
        _showSayEntries = !hidden.Contains(TerminalEntryKind.Say);
        _showWarningEntries = !hidden.Contains(TerminalEntryKind.Warning);
        _showErrorEntries = !hidden.Contains(TerminalEntryKind.Error);
        _showChatEntries = !hidden.Contains(TerminalEntryKind.Chat);
        Ground = new GroundViewModel(_connection, SendCommandForViewAsync, OnChildSelectionChanged, _preferences);
        Radar = new RadarViewModel(_connection, _videoMapService, SendCommandForViewAsync, OnChildSelectionChanged);
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
        _connection.WeatherChanged += OnWeatherChanged;
        _connection.PositionDisplayChanged += OnPositionDisplayChanged;
        _connection.ScenarioLoaded += OnScenarioLoaded;
        _connection.ScenarioUnloaded += OnScenarioUnloaded;
        _connection.AircraftAssignmentsChanged += OnAircraftAssignmentsChanged;

        RefreshCommandScheme();
        _commandInput.Macros = _preferences.Macros;
        RefreshDisplayFavorites();

        _ = InitializeNavDataAsync();
    }

    private async Task InitializeNavDataAsync()
    {
        try
        {
            using var vnasData = new VnasDataService();
            await vnasData.InitializeAsync();

            var fixDb = new FixDatabase(vnasData.NavData);

            _commandInput.FixDb = fixDb;
            Radar.SetElevationLookup(fixDb.GetAirportElevation);
            Ground.SetElevationLookup(fixDb.GetAirportElevation);
            Radar.SetFixDb(fixDb);
            _log.LogInformation("Navdata loaded: {Count} fixes available for autocomplete", fixDb.Count);

            // CIFP + approach database for FMC fix highlighting
            using var cifpService = new CifpDataService();
            await cifpService.InitializeAsync();
            if (cifpService.CifpFilePath is not null)
            {
                var approachDb = new ApproachDatabase(cifpService.CifpFilePath);
                Radar.SetApproachDb(approachDb);
                _log.LogInformation("Client-side CIFP initialized for FMC fix highlighting");
            }
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
        LoadLiveWeatherCommand.NotifyCanExecuteChanged();
    }

    partial void OnActiveRoomNameChanged(string? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnActiveScenarioIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScenario));
        OnPropertyChanged(nameof(WindowTitle));
        RefreshDisplayFavorites();
    }

    partial void OnActiveScenarioNameChanged(string? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        _isSyncingSelection = true;
        Ground.SelectedAircraft = value;
        Radar.SelectedAircraft = value;
        _isSyncingSelection = false;
    }

    private void OnChildSelectionChanged(AircraftModel? value)
    {
        if (!_isSyncingSelection)
        {
            SelectedAircraft = value;
        }
    }

    partial void OnCommandTextChanged(string value)
    {
        _commandInput.UpdateSuggestions(value, Aircraft, _preferences.CommandScheme, SelectedAircraft);
        _commandInput.UpdateSignatureHelp(value, _preferences.CommandScheme);
    }

    // --- Commands ---

    [RelayCommand(CanExecute = nameof(CanExecuteInRoom))]
    private async Task SendCommandAsync()
    {
        var text = CommandText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Assignment override: "** " prefix bypasses ownership check
        bool forceOverride = false;
        if (text.StartsWith("** ", StringComparison.Ordinal))
        {
            forceOverride = true;
            text = text[3..].TrimStart();
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

        // If the input is a single token that matches a callsign, just select it
        if (!text.Contains(' ') && !text.Contains(',') && !text.Contains(';'))
        {
            var callsignMatch = ResolveAircraft(text);
            if (callsignMatch is not null)
            {
                SelectedAircraft = callsignMatch;
                _commandInput.DismissSuggestions();
                _commandInput.ResetHistoryNavigation();
                CommandText = "";
                return;
            }
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

        // Expand macros before parsing
        var originalCommand = commandText;
        var expandedCommand = MacroExpander.TryExpand(commandText, _preferences.Macros, out var macroError);
        if (macroError is not null)
        {
            StatusText = macroError;
            return;
        }
        if (expandedCommand is not null)
        {
            commandText = expandedCommand;
        }

        // Parse as compound command (handles single and multi-block)
        var compound = CommandSchemeParser.ParseCompound(commandText, scheme, out var parseFailure);
        if (compound is null)
        {
            if (parseFailure is not null)
            {
                _log.LogWarning("Command '{Verb}' {Reason} in input '{Input}'", parseFailure.Verb, parseFailure.Reason, commandText);
                StatusText = $"\"{parseFailure.Verb}\" {parseFailure.Reason}";
            }
            else
            {
                // If the first token is a known callsign, report the command as the problem
                var tokens = commandText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 && ResolveAircraft(tokens[0]) is not null)
                {
                    var cmdVerb = tokens[1].Split([' ', ',', ';'], 2)[0];
                    _log.LogWarning("Unrecognized command '{Verb}' in input '{Input}'", cmdVerb, commandText);
                    StatusText = $"Unrecognized command \"{cmdVerb}\" — type a command like FH 270, CM 240, CTL";
                }
                else
                {
                    var verb = commandText.Split([' ', ',', ';'], 2)[0];
                    _log.LogWarning("Unrecognized command '{Verb}' in input '{Input}'", verb, commandText);
                    StatusText = $"Unrecognized command \"{verb}\" — type a command like FH 270, CM 240, CTL";
                }
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
            var canonical = forceOverride ? $"** {compound.CanonicalString}" : compound.CanonicalString;
            _log.LogDebug("SendCommand: {Callsign} '{Canonical}' (input: '{Input}')", target.Callsign, canonical, originalCommand);
            var result = await _connection.SendCommandAsync(target.Callsign, canonical, _preferences.UserInitials);

            AddHistory(originalCommand);

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
        if (parsed.Type is CanonicalCommandType.Consolidate or CanonicalCommandType.ConsolidateFull or CanonicalCommandType.Deconsolidate)
        {
            var verb = parsed.Type switch
            {
                CanonicalCommandType.Consolidate => "CON",
                CanonicalCommandType.ConsolidateFull => "CON+",
                CanonicalCommandType.Deconsolidate => "DECON",
                _ => "",
            };
            var canonical = string.IsNullOrEmpty(parsed.Argument) ? verb : $"{verb} {parsed.Argument}";
            try
            {
                var result = await _connection.SendCommandAsync("", canonical, _preferences.UserInitials);
                AddHistory(canonical);
                if (!string.IsNullOrEmpty(result.Message))
                {
                    StatusText = result.Message;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Verb} failed", verb);
                StatusText = $"{verb} error: {ex.Message}";
            }
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
        }
        if (
            parsed.Type
            is CanonicalCommandType.SetActivePosition
                or CanonicalCommandType.AcceptAllHandoffs
                or CanonicalCommandType.InitiateHandoffAll
                or CanonicalCommandType.CoordinationAutoAck
        )
        {
            var verb = parsed.Type switch
            {
                CanonicalCommandType.SetActivePosition => "AS",
                CanonicalCommandType.AcceptAllHandoffs => "ACCEPTALL",
                CanonicalCommandType.InitiateHandoffAll => "HOALL",
                CanonicalCommandType.CoordinationAutoAck => "RDAUTO",
                _ => "",
            };
            var canonical = string.IsNullOrEmpty(parsed.Argument) ? verb : $"{verb} {parsed.Argument}";
            try
            {
                var result = await _connection.SendCommandAsync("", canonical, _preferences.UserInitials);
                AddHistory(canonical);
                if (!string.IsNullOrEmpty(result.Message))
                {
                    StatusText = result.Message;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Verb} failed", verb);
                StatusText = $"{verb} error: {ex.Message}";
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
                or CanonicalCommandType.SquawkStandbyAll
                or CanonicalCommandType.Consolidate
                or CanonicalCommandType.ConsolidateFull
                or CanonicalCommandType.Deconsolidate
                or CanonicalCommandType.SetActivePosition
                or CanonicalCommandType.AcceptAllHandoffs
                or CanonicalCommandType.InitiateHandoffAll
                or CanonicalCommandType.CoordinationAutoAck;
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
    /// Selects the aircraft matching the current command input text as a callsign,
    /// then clears the input. Called by the configurable "aircraft select" keybind.
    /// </summary>
    public void SelectAircraftFromInput()
    {
        var text = CommandText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Use the first token as a callsign candidate
        var token = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var match = ResolveAircraft(token);
        if (match is not null)
        {
            SelectedAircraft = match;
            CommandText = "";
        }
        else
        {
            StatusText = $"No aircraft matched \"{token}\"";
        }
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

    [RelayCommand(CanExecute = nameof(CanExecuteInRoom))]
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
        _commandInput.Macros = _preferences.Macros;
        if (ActiveRoomId is not null)
        {
            _ = SendAutoAcceptDelay();
            _ = SendAutoDeleteMode();
            _ = SendValidateDctFixes();
            _ = SendAutoClearedToLand();
            _ = SendAutoCrossRunway();
        }
    }

    private void SetRadarAirportPosition(string? airportId)
    {
        if (string.IsNullOrEmpty(airportId))
        {
            return;
        }

        var pos = _commandInput.FixDb?.GetFixPosition(airportId);
        if (pos.HasValue)
        {
            Radar.SetPrimaryAirportPosition(pos.Value.Lat, pos.Value.Lon);
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

        if (model.IsDelayed)
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

    private async Task SendValidateDctFixes()
    {
        try
        {
            await _connection.SetValidateDctFixesAsync(_preferences.ValidateDctFixes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set DCT validation mode");
        }
    }

    private async Task SendAutoClearedToLand()
    {
        try
        {
            await _connection.SetAutoClearedToLandAsync(_preferences.GetAutoClearedToLand(_studentPositionType));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set auto-cleared-to-land");
        }
    }

    private async Task SendAutoCrossRunway()
    {
        try
        {
            await _connection.SetAutoCrossRunwayAsync(_preferences.AutoCrossRunway);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set auto-cross-runway");
        }
    }

    private void ApplySimState(bool paused, int rate, double elapsed = 0, bool isPlayback = false, double tapeEnd = 0)
    {
        IsPaused = paused;
        SimRate = rate;
        SelectedSimRateIndex = Array.IndexOf(SimRateOptions, rate);
        ScenarioElapsedSeconds = elapsed;
        IsPlaybackMode = isPlayback;
        PlaybackTapeEnd = tapeEnd;
        OnPropertyChanged(nameof(ElapsedTimeDisplay));
        OnPropertyChanged(nameof(TapeEndDisplay));
        OnPropertyChanged(nameof(IsTimelineAvailable));
        OnPropertyChanged(nameof(PlayPauseIcon));
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
