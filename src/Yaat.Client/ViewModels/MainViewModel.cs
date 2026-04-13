using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velopack;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Speech;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ILogger _log = AppLog.CreateLogger<MainViewModel>();

    private readonly ServerConnection _connection = new();
    public ServerConnection Connection => _connection;
    private readonly UserPreferences _preferences = new();
    private readonly CommandInputController _commandInput = new();
    private readonly VideoMapService _videoMapService = new();
    private readonly VnasConfigService _vnasConfigService = new();
    private readonly TowerCabImageService _towerCabImageService = new();

    // Speech recognition pipeline. All services are lazy/opt-in: they only touch real resources
    // (PortAudio, Whisper weights, LLM weights) when SpeechEnabled is true AND the user holds the
    // PTT key. When disabled, these sit dormant with zero cost.
    private readonly ModelManager _modelManager = new();
    private readonly AudioCaptureService _audioCapture;
    private readonly WhisperSttEngine _whisperStt;
    private readonly LocalLlmService _llmService;
    private readonly LocalLlmCommandMapper _llmMapper;
    private readonly PhraseologyCommandMapper _ruleMapper = new();
    private readonly SpeechRecognitionService _speechService;

    public UserPreferences Preferences => _preferences;
    public CommandInputController CommandInput => _commandInput;
    public SpeechRecognitionService SpeechService => _speechService;
    public AudioCaptureService AudioCapture => _audioCapture;

    private string _connectedServerUrl = "";
    private bool _isSyncingSelection;
    private string? _studentPositionType;
    private bool _isAutoClearedToLand;

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

    // Session-level settings (room state, not user preferences).
    // Displayed in the session settings flyout. Synced via server broadcasts.
    public static string[] SessionAutoDeleteOptions { get; } = ["Scenario Default", "Never", "On Landing", "On Parking"];

    [ObservableProperty]
    private int _sessionAutoDeleteIndex;

    [ObservableProperty]
    private string? _activeAutoDeleteMode;

    [ObservableProperty]
    private int _sessionAutoAcceptDelaySeconds = -1;

    [ObservableProperty]
    private bool _sessionAutoClearedToLand;

    [ObservableProperty]
    private bool _sessionAutoCrossRunway;

    [ObservableProperty]
    private bool _sessionValidateDctFixes = true;

    [ObservableProperty]
    private bool _isSessionSettingsOpen;

    [ObservableProperty]
    private double _scenarioElapsedSeconds;

    [ObservableProperty]
    private bool _isPlaybackMode;

    [ObservableProperty]
    private bool _isExportingRecording;

    [ObservableProperty]
    private string _exportingStatusText = "";

    [ObservableProperty]
    private double _exportProgress;

    [ObservableProperty]
    private bool _isExportIndeterminate = true;

    [ObservableProperty]
    private double _playbackTapeEnd;

    public double TimelineMaximum => IsPlaybackMode ? PlaybackTapeEnd : ScenarioElapsedSeconds;

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
    private string? _pendingDifficultyApiId;

    public ObservableCollection<DifficultyOption> DifficultyOptions { get; } = [];

    [ObservableProperty]
    private bool _showRoomList;

    [ObservableProperty]
    private bool _showCrcPanel;

    [ObservableProperty]
    private bool _showRoomMembersPanel;

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
    private double _dataGridScale = 1.0;

    [ObservableProperty]
    private string _distanceReferenceFix = "";

    // Auto-update state
    private readonly UpdateService _updateService = new();
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateVersion = "";

    [ObservableProperty]
    private int _updateProgress;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

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
        RefreshAircraftView();
    }

    [ObservableProperty]
    private bool _showOnlyActiveAircraft;

    partial void OnShowOnlyActiveAircraftChanged(bool value)
    {
        _preferences.SetShowOnlyActiveAircraft(value);
        RefreshAircraftView();
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
            || Contains(ac.OwnerDisplay, filter)
            || Contains(ac.SmartStatus, filter);

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

    public ObservableCollection<CrcLobbyClientDto> CrcLobbyClients { get; } = [];

    public ObservableCollection<CrcRoomMemberDto> CrcRoomMembers { get; } = [];

    public ObservableCollection<RoomMemberDto> RoomMembers { get; } = [];

    [ObservableProperty]
    private SpeechStatus _speechStatus = SpeechStatus.Idle;

    public MainViewModel()
    {
        // Speech pipeline wiring. The order here matters: LlmService must exist before
        // LocalLlmCommandMapper, and SpeechRecognitionService needs all of them.
        _audioCapture = new AudioCaptureService(_preferences);
        _whisperStt = new WhisperSttEngine(_preferences, _modelManager);
        _llmService = new LocalLlmService(new PreferencesLlmRuntimeConfig(_preferences));
        _llmMapper = new LocalLlmCommandMapper(_llmService);
        _speechService = new SpeechRecognitionService(_preferences, _audioCapture, _whisperStt, _ruleMapper, _llmMapper, BuildSpeechContext);
        _speechService.StatusChanged += HandleSpeechServiceStatusChange;
        _speechService.CommandReady += HandleSpeechServiceCommandReady;

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
        Ground.SetAircraftLookup(cs => Aircraft.FirstOrDefault(a => a.Callsign == cs));
        Ground.SetTowerCabServices(_vnasConfigService, _towerCabImageService, _airportResolver);
        Radar = new RadarViewModel(_connection, _videoMapService, SendCommandForViewAsync, OnChildSelectionChanged);
        Radar.SetPreferences(_preferences);
        Radar.SetAircraftLookup(cs => Aircraft.FirstOrDefault(a => a.Callsign == cs));

        _dataGridScale = _preferences.DataGridFontSize / 12.0;
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
        _connection.SessionSettingsChanged += OnSessionSettingsChanged;
        _connection.KickedFromRoom += OnKickedFromRoom;

        RefreshCommandScheme();
        _commandInput.Macros = _preferences.Macros;
        RefreshDisplayFavorites();

        _ = InitializeNavDataAsync();
        _ = _vnasConfigService.InitializeAsync();
        _ = CheckForUpdateAsync();
    }

    private async Task InitializeNavDataAsync()
    {
        try
        {
            using var vnasData = new VnasDataService();
            await vnasData.InitializeAsync();

            using var cifpService = new CifpDataService();
            await cifpService.InitializeAsync();

            if (vnasData.NavData is null || cifpService.CifpFilePath is null)
            {
                _log.LogError("NavData or CIFP data unavailable — navigation database not initialized");
                return;
            }

            NavigationDatabase.Initialize(vnasData.NavData, cifpService.CifpFilePath);
            var navDb = NavigationDatabase.Instance;

            _commandInput.NavDbReady = true;
            Radar.SetElevationLookup(navDb.GetAirportElevation);
            Ground.SetElevationLookup(navDb.GetAirportElevation);
            Radar.SetNavDbReady();
            _log.LogInformation("Navdata loaded: {Count} fixes, CIFP initialized", navDb.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Navdata initialization failed");
        }
    }

    private async Task CheckForUpdateAsync()
    {
        // Delay to avoid slowing initial startup
        await Task.Delay(TimeSpan.FromSeconds(5));

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                return;
            }

            _pendingUpdate = update;
            UpdateVersion = update.TargetFullRelease.Version.ToString();
            IsUpdateAvailable = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
        }
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        try
        {
            IsDownloadingUpdate = true;
            await _updateService.DownloadUpdateAsync(_pendingUpdate, progress => UpdateProgress = progress);
            _updateService.ApplyUpdateAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to download/apply update");
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        IsUpdateAvailable = false;
        _pendingUpdate = null;
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

    /// <summary>
    /// Builds the snapshot of scenario state passed to <see cref="SpeechRecognitionService"/> at
    /// PTT press: active callsigns (for <see cref="PhraseologyMapper"/> disambiguation), programmed
    /// fixes for the selected aircraft (for the <see cref="PhoneticFixMatcher"/> post-pass), and a
    /// free-text Whisper <c>initial_prompt</c> that biases recognition toward the ICAO + spoken
    /// forms of every active callsign plus the selected aircraft's fix names.
    /// </summary>
    private SpeechContext BuildSpeechContext()
    {
        var snapshot = Aircraft.ToArray();
        var callsigns = snapshot.Select(a => a.Callsign).Where(cs => !string.IsNullOrEmpty(cs)).ToList();
        var selected = SelectedAircraft;
        IReadOnlyList<string> programmedFixes = [];
        if (selected is not null)
        {
            var fixSet = ProgrammedFixResolver.Resolve(
                selected.Route,
                selected.ExpectedApproach,
                selected.Destination,
                selected.Departure,
                null,
                selected.ActiveStarId,
                selected.DestinationRunway
            );
            programmedFixes = fixSet.ToList();
        }

        // Build the Whisper initial prompt: every ICAO callsign + its spoken variants + the fix
        // names for the selected aircraft. Keeping this terse so Whisper's decoder bias stays
        // focused — too much prompt text dilutes the signal.
        var promptParts = new List<string>(capacity: callsigns.Count * 2 + programmedFixes.Count);
        foreach (var cs in callsigns)
        {
            promptParts.Add(cs);
            var ac = snapshot.FirstOrDefault(a => a.Callsign == cs);
            var variants = CallsignParser.GetSpokenVariants(cs, ac?.AircraftType, callsigns);
            if (variants.Count > 0)
            {
                // Only the first variant keeps the prompt compact; variants past the first are
                // usually derivative forms that Whisper already generalizes from the first one.
                promptParts.Add(variants[0]);
            }
        }

        foreach (var f in programmedFixes)
        {
            promptParts.Add(f);
        }

        var whisperInitialPrompt = string.Join(' ', promptParts);
        return new SpeechContext(callsigns, programmedFixes, whisperInitialPrompt);
    }

    private void HandleSpeechServiceStatusChange(SpeechStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SpeechStatus = status);
    }

    private void HandleSpeechServiceCommandReady(SpeechResult result)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(result.CanonicalCommand))
            {
                CommandText = result.CanonicalCommand;
            }
            else if (!string.IsNullOrWhiteSpace(result.Transcript))
            {
                // Neither mapper produced a canonical command — surface the raw transcript so the
                // user sees what Whisper heard and can correct manually. Better than silently
                // dropping the input.
                CommandText = result.Transcript;
            }
        });
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

        // Check for global commands first (no callsign needed).
        // SetActivePosition ("AS {tcp}") is global only when it's a standalone
        // command. The prefix form "AS {tcp} {track_command}" is per-aircraft
        // (server's ExtractAsPrefix strips the prefix and resolves RPO identity).
        var globalParsed = CommandSchemeParser.Parse(text, scheme);
        if (globalParsed is not null && IsGlobalCommand(globalParsed.Type))
        {
            bool isAsPrefix = (globalParsed.Type == CanonicalCommandType.SetActivePosition) && (globalParsed.Argument?.Contains(' ') == true);
            if (!isAsPrefix)
            {
                await HandleGlobalCommand(globalParsed);
                return;
            }
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

        // Expand macros first so callsign prefix resolution sees real commands
        var commandText = text;
        var originalInput = text;
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

        // Try to resolve callsign prefix from the (possibly expanded) input
        AircraftModel? target = SelectedAircraft;
        _log.LogDebug(
            "SendCommand target resolution: SelectedAircraft={Selected}, target={Target}",
            SelectedAircraft?.Callsign ?? "(none)",
            target?.Callsign ?? "(none)"
        );

        var resolved = TryResolveCallsignPrefix(commandText, scheme);
        if (resolved is not null)
        {
            target = resolved.Value.Aircraft;
            commandText = resolved.Value.Remainder;
        }

        // Rewrite partial callsign arguments (FOLLOW, RTIS, CVA FOLLOW, ...) into canonical
        // callsigns before parsing. Matches the first-word partial-match behavior.
        var rewrite = CallsignArgumentResolver.TryRewrite(commandText, scheme, Aircraft);
        if (rewrite.Error is not null)
        {
            StatusText = rewrite.Error;
            return;
        }

        if (rewrite.Text is not null)
        {
            commandText = rewrite.Text;
        }

        // RPO control commands (client-local, bypass command pipeline)
        var rpoResult = await TryHandleRpoCommand(commandText, target, text);
        if (rpoResult)
        {
            return;
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
                    StatusText = $"Unrecognized command \"{cmdVerb}\" — type a command like FH 270, CM 240, CLAND";
                }
                else
                {
                    var verb = commandText.Split([' ', ',', ';'], 2)[0];
                    _log.LogWarning("Unrecognized command '{Verb}' in input '{Input}'", verb, commandText);
                    StatusText = $"Unrecognized command \"{verb}\" — type a command like FH 270, CM 240, CLAND";
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
            _log.LogDebug("SendCommand: {Callsign} '{Canonical}' (input: '{Input}')", target.Callsign, canonical, originalInput);
            var result = await _connection.SendCommandAsync(target.Callsign, canonical, _preferences.UserInitials);

            if (!result.Success)
            {
                StatusText = result.Message ?? "Command rejected";
                return;
            }

            AddHistory(originalInput);

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
            await _connection.SendCommandAsync("", "PAUSE", _preferences.UserInitials);
            AddHistory("PAUSE");
            CommandText = "";
            return;
        }
        if (parsed.Type == CanonicalCommandType.Unpause)
        {
            await _connection.SendCommandAsync("", "UNPAUSE", _preferences.UserInitials);
            AddHistory("UNPAUSE");
            CommandText = "";
            return;
        }
        if (parsed.Type == CanonicalCommandType.SimRate)
        {
            if (int.TryParse(parsed.Argument, out var rate))
            {
                await _connection.SendCommandAsync("", $"SIMRATE {rate}", _preferences.UserInitials);
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
            var canonical = $"ADD {parsed.Argument}";
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
                _log.LogError(ex, "ADD failed");
                StatusText = $"ADD error: {ex.Message}";
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
        if (parsed.Type == CanonicalCommandType.TaxiAll)
        {
            var canonical = string.IsNullOrEmpty(parsed.Argument) ? "TAXIALL" : $"TAXIALL {parsed.Argument}";
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
                _log.LogError(ex, "TAXIALL failed");
                StatusText = $"TAXIALL error: {ex.Message}";
            }
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
        }
    }

    private async Task<bool> TryHandleRpoCommand(string commandText, AircraftModel? target, string originalInput)
    {
        var upper = commandText.Trim().ToUpperInvariant();

        if (upper == "TAKE")
        {
            if (target is null)
            {
                StatusText = "Select an aircraft first";
                return true;
            }
            await TakeControlAsync(target.Callsign);
            AddHistory(originalInput);
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
            return true;
        }

        if (upper.StartsWith("GIVE ", StringComparison.Ordinal))
        {
            if (target is null)
            {
                StatusText = "Select an aircraft first";
                return true;
            }
            var initials = upper[5..].Trim();
            if (initials.Length == 0)
            {
                StatusText = "Usage: GIVE <initials>";
                return true;
            }
            await GiveControlAsync(target.Callsign, initials);
            AddHistory(originalInput);
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
            return true;
        }

        if (upper == "GIVEUP")
        {
            if (target is null)
            {
                StatusText = "Select an aircraft first";
                return true;
            }
            await ReleaseControlAsync(target.Callsign);
            AddHistory(originalInput);
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";
            return true;
        }

        return false;
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
                or CanonicalCommandType.CoordinationAutoAck
                or CanonicalCommandType.TaxiAll
                or CanonicalCommandType.GhostTrack;
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
        var (match, outcome, candidates) = CallsignMatcher.Match(token, Aircraft);
        if (outcome == CallsignMatcher.Outcome.Ambiguous)
        {
            StatusText = CallsignMatcher.FormatAmbiguityMessage(token, candidates);
        }

        return match;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteInRoom))]
    private async Task TogglePauseAsync()
    {
        try
        {
            var cmd = IsPaused ? "UNPAUSE" : "PAUSE";
            await _connection.SendCommandAsync("", cmd, _preferences.UserInitials);
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
            await _connection.SendCommandAsync("", $"SIMRATE {rate}", _preferences.UserInitials);
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

        var pos = _commandInput.NavDbReady ? NavigationDatabase.Instance.GetFixPosition(airportId) : null;
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

        if (!_commandInput.NavDbReady)
        {
            _log.LogWarning("Cannot set distance reference — navdata not loaded");
            return;
        }

        var resolved = FrdResolver.Resolve(fixOrFrd, NavigationDatabase.Instance);
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

    // --- Session settings (flyout) ---

    private bool _isApplyingSessionSettings;

    private void OnSessionSettingsChanged(SessionSettingsDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplySessionSettings(dto));
    }

    private void ApplySessionSettings(SessionSettingsDto dto)
    {
        _isApplyingSessionSettings = true;
        ActiveAutoDeleteMode = dto.AutoDeleteMode;
        SessionAutoDeleteIndex = AutoDeleteModeToIndex(dto.AutoDeleteMode);
        SessionAutoAcceptDelaySeconds = dto.AutoAcceptDelaySeconds;
        SessionAutoClearedToLand = dto.AutoClearedToLand;
        SessionAutoCrossRunway = dto.AutoCrossRunway;
        SessionValidateDctFixes = dto.ValidateDctFixes;
        _isApplyingSessionSettings = false;
    }

    private void ApplySessionSettingsFromRoom(RoomStateDto state)
    {
        ApplySessionSettings(
            new SessionSettingsDto(
                state.AutoDeleteMode,
                state.AutoAcceptDelaySeconds,
                state.AutoClearedToLand,
                state.AutoCrossRunway,
                state.ValidateDctFixes
            )
        );
    }

    private void ApplySessionSettingsFromScenarioLoaded(ScenarioLoadedDto dto)
    {
        ApplySessionSettings(
            new SessionSettingsDto(dto.AutoDeleteMode, dto.AutoAcceptDelaySeconds, dto.AutoClearedToLand, dto.AutoCrossRunway, dto.ValidateDctFixes)
        );
    }

    partial void OnSessionAutoDeleteIndexChanged(int value)
    {
        if (_isApplyingSessionSettings)
        {
            return;
        }

        var mode = IndexToActiveAutoDeleteMode(value);
        ActiveAutoDeleteMode = mode;
        _ = _connection.SetAutoDeleteModeAsync(mode);
    }

    partial void OnSessionAutoAcceptDelaySecondsChanged(int value)
    {
        if (!_isApplyingSessionSettings)
        {
            _ = _connection.SetAutoAcceptDelayAsync(value);
        }
    }

    partial void OnSessionAutoClearedToLandChanged(bool value)
    {
        if (!_isApplyingSessionSettings)
        {
            _ = _connection.SetAutoClearedToLandAsync(value);
        }
    }

    partial void OnSessionAutoCrossRunwayChanged(bool value)
    {
        if (!_isApplyingSessionSettings)
        {
            _ = _connection.SetAutoCrossRunwayAsync(value);
        }
    }

    partial void OnSessionValidateDctFixesChanged(bool value)
    {
        if (!_isApplyingSessionSettings)
        {
            _ = _connection.SetValidateDctFixesAsync(value);
        }
    }

    private static int AutoDeleteModeToIndex(string? mode) =>
        mode switch
        {
            "Never" => 1,
            "OnLanding" => 2,
            "Parked" => 3,
            _ => 0,
        };

    private static string? IndexToActiveAutoDeleteMode(int index) =>
        index switch
        {
            1 => "Never",
            2 => "OnLanding",
            3 => "Parked",
            _ => null,
        };

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
            _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);
            await _connection.SetAutoClearedToLandAsync(_isAutoClearedToLand);
            foreach (var ac in Aircraft)
            {
                ac.IsAutoClearedToLand = _isAutoClearedToLand;
                ac.ComputeSmartStatus();
            }
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
        OnPropertyChanged(nameof(TimelineMaximum));
        OnPropertyChanged(nameof(IsTimelineAvailable));
        OnPropertyChanged(nameof(PlayPauseIcon));
    }

    private void RefreshAircraftView()
    {
        var saved = SelectedAircraft;
        AircraftView.Refresh();
        if ((saved is not null) && (SelectedAircraft is null) && Aircraft.Contains(saved))
        {
            SelectedAircraft = saved;
        }
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
