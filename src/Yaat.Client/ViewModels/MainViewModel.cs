using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velopack;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views;
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
    private readonly IFilePickerService _filePicker;
    private readonly VideoMapService _videoMapService = new();
    private readonly VnasConfigService _vnasConfigService = new();
    private readonly TowerCabImageService _towerCabImageService = new();
    private readonly PilotSpeechAlertService _pilotSpeechAlerts;
    private readonly PilotVoiceService _pilotVoice;

    // Speech recognition pipeline. All services are lazy/opt-in: they only touch real resources
    // (PortAudio, Whisper weights, LLM weights) when SpeechEnabled is true AND the user holds the
    // PTT key. When disabled, these sit dormant with zero cost.
    private readonly AudioCaptureService _audioCapture;
    private readonly WhisperSttEngine _whisperStt;
    private readonly LocalLlmService _llmService;
    private readonly LocalLlmCommandMapper _llmMapper;
    private readonly LocalLlmCallsignResolver _llmCallsignResolver;
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

    /// <summary>
    /// Short-hand for the student-facility strips VM (the first entry in
    /// <see cref="StripsEntries"/>). Kept as a property so scenario-bootstrap
    /// code calling <c>VStrips.ApplyBayConfig</c> keeps working — the
    /// student entry is always element 0.
    /// </summary>
    public VStripsViewModel VStrips => StripsEntries[0].Vm;

    /// <summary>
    /// All flight-strips instances in this client — the student-facility one
    /// at index 0 (auto-created in the constructor) plus any additional
    /// per-facility entries the user opened via the 'Open strips window…'
    /// affordance. Each entry carries its own dock state (see
    /// <see cref="VStripsDockEntryViewModel.IsPoppedOut"/>).
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<VStripsDockEntryViewModel> StripsEntries { get; } = [];

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
    [NotifyCanExecuteChangedFor(nameof(OpenStripsInBrowserCommand))]
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
    private int _commandCaretIndex;

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
    private bool _sessionSoloTrainingMode;

    [ObservableProperty]
    private int _sessionSoloParkingInitialCallupRatePercent = 100;

    [ObservableProperty]
    private int _sessionSoloParkingInitialCallupIntervalSeconds = 20;

    public string SessionSoloParkingInitialCallupIntervalLabel => FormatParkingInitialCallupInterval(SessionSoloParkingInitialCallupIntervalSeconds);

    [ObservableProperty]
    private int _sessionSoloArrivalGeneratorRatePercent = 100;

    [ObservableProperty]
    private int _sessionSoloGoAroundProbabilityPercent;

    [ObservableProperty]
    private bool _sessionHasSoloParkingInitialCallupSource;

    [ObservableProperty]
    private bool _sessionHasSoloArrivalGeneratorSource;

    public bool ShowSessionSoloParkingInitialCallupRate => SessionSoloTrainingMode && SessionHasSoloParkingInitialCallupSource;

    public bool ShowSessionSoloArrivalGeneratorRate => SessionSoloTrainingMode && SessionHasSoloArrivalGeneratorSource;

    public bool ShowSessionSoloGoAroundProbability => SessionSoloTrainingMode;

    [ObservableProperty]
    private bool _sessionRpoShowPilotSpeech;

    [ObservableProperty]
    private bool _isSessionSettingsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedTimeDisplay))]
    [NotifyPropertyChangedFor(nameof(TimelineMaximum))]
    private double _scenarioElapsedSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineMaximum))]
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
    [NotifyPropertyChangedFor(nameof(TapeEndDisplay))]
    [NotifyPropertyChangedFor(nameof(TimelineMaximum))]
    private double _playbackTapeEnd;

    public double TimelineMaximum => IsPlaybackMode ? PlaybackTapeEnd : ScenarioElapsedSeconds;

    public bool IsTimelineAvailable => ActiveScenarioName is not null && ShowTimelineBar;

    /// <summary>
    /// Markers rendered above the rewind slider (M12.5). Populated from
    /// <see cref="ServerConnection.GetSessionReportAsync"/> findings on a short polling
    /// cadence whenever <see cref="IsTimelineAvailable"/> is true. Optionally filtered to
    /// a single callsign by <see cref="TimelineFilterCallsign"/> via the Aircraft tab's
    /// "Show on Timeline" button.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<TimelineMarkerVm> TimelineMarkers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineFilterActive))]
    [NotifyPropertyChangedFor(nameof(TimelineFilterText))]
    private string? _timelineFilterCallsign;

    public bool TimelineFilterActive => !string.IsNullOrEmpty(TimelineFilterCallsign);

    public string TimelineFilterText => TimelineFilterActive ? $"Filter: {TimelineFilterCallsign}" : "";

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

    [ObservableProperty]
    private string? _activeScenarioPrimaryAirportId;

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
    private bool _showScenarioSetup;

    public bool ShowScenarioSetupDifficulty => DifficultyOptions.Count > 0;

    [ObservableProperty]
    private bool _showScenarioSetupPacingControls;

    [ObservableProperty]
    private bool _showScenarioSetupParkingInitialCallupRate;

    [ObservableProperty]
    private bool _showScenarioSetupArrivalGeneratorRate;

    [ObservableProperty]
    private bool _showScenarioSetupGoAroundProbability;

    [ObservableProperty]
    private int _scenarioSetupParkingInitialCallupRatePercent = 100;

    [ObservableProperty]
    private int _scenarioSetupParkingInitialCallupIntervalSeconds = 20;

    public string ScenarioSetupParkingInitialCallupIntervalLabel =>
        FormatParkingInitialCallupInterval(ScenarioSetupParkingInitialCallupIntervalSeconds);

    [ObservableProperty]
    private int _scenarioSetupArrivalGeneratorRatePercent = 100;

    [ObservableProperty]
    private int _scenarioSetupSoloGoAroundProbabilityPercent;

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

    partial void OnIsTerminalDockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsContentGridVisible));
        OnPropertyChanged(nameof(IsTabSplitterVisible));
    }

    partial void OnIsDataGridPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("DataGrid", value);
        OnTabPoppedOutChanged();
    }

    partial void OnIsGroundViewPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("GroundView", value);
        OnTabPoppedOutChanged();
    }

    partial void OnIsRadarViewPoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut("RadarView", value);
        OnTabPoppedOutChanged();
    }

    /// <summary>
    /// Common bookkeeping after any tab's pop-out state changes (the three
    /// fixed tabs <i>or</i> any strip entry). Re-publishes the derived
    /// visibility properties that drive the main-window layout and shifts
    /// <see cref="SelectedTabIndex"/> off a tab that just became invisible
    /// so the TabControl never falls through to rendering popped-out
    /// content in the docked area.
    /// </summary>
    private void OnTabPoppedOutChanged()
    {
        OnPropertyChanged(nameof(IsAnyTabVisible));
        OnPropertyChanged(nameof(IsContentGridVisible));
        OnPropertyChanged(nameof(IsTabSplitterVisible));
        EnsureSelectedTabVisible();
    }

    private void EnsureSelectedTabVisible()
    {
        if (IsTabVisible(SelectedTabIndex))
        {
            return;
        }
        var next = FindNextVisibleTabIndex(SelectedTabIndex);
        if (next >= 0)
        {
            SelectedTabIndex = next;
        }
    }

    /// <summary>
    /// True when at least one TabItem is still docked (any of the three
    /// fixed tabs or any strip entry). Drives the TabControl's IsVisible
    /// binding so the entire tab area collapses when every view has been
    /// popped out into its own window.
    /// </summary>
    public bool IsAnyTabVisible
    {
        get
        {
            if (!IsDataGridPoppedOut || !IsGroundViewPoppedOut || !IsRadarViewPoppedOut)
            {
                return true;
            }
            foreach (var entry in StripsEntries)
            {
                if (!entry.IsPoppedOut)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// True when the splitter between the tab area and the terminal has
    /// something to split — both regions are docked and visible. False
    /// when either region is popped out so the splitter doesn't render a
    /// 6 px stripe with nothing above or below it.
    /// </summary>
    public bool IsTabSplitterVisible => IsAnyTabVisible && IsTerminalDocked;

    /// <summary>
    /// True when the central content grid (tabs + terminal) has anything
    /// to show. When false the main window collapses down to just the
    /// menu bar since every view has been popped out into its own window.
    /// </summary>
    public bool IsContentGridVisible => IsAnyTabVisible || IsTerminalDocked;

    private int FindNextVisibleTabIndex(int currentIndex)
    {
        // Tab 0: Aircraft List, Tab 1: Ground View, Tab 2: Radar View.
        // Strips tabs start at index 3, ordered to match StripsEntries.
        // Walk all tabs (wrapping) and pick the next still-docked one.
        // Returns -1 when every tab is popped out — caller then leaves
        // SelectedTabIndex alone since the TabControl is hidden anyway.
        var total = 3 + StripsEntries.Count;
        if (total <= 0)
        {
            return -1;
        }
        for (var offset = 1; offset <= total; offset++)
        {
            var candidate = (currentIndex + offset) % total;
            if (IsTabVisible(candidate))
            {
                return candidate;
            }
        }
        return -1;
    }

    private bool IsTabVisible(int index) =>
        index switch
        {
            0 => !IsDataGridPoppedOut,
            1 => !IsGroundViewPoppedOut,
            2 => !IsRadarViewPoppedOut,
            _ when index >= 3 && index - 3 < StripsEntries.Count => !StripsEntries[index - 3].IsPoppedOut,
            _ => false,
        };

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private double _dataGridScale = 1.0;

    [ObservableProperty]
    private string _distanceReferenceFix = "";

    // Auto-update state
    private readonly UpdateService _updateService = new(channel: null);
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
            var appLabel = $"YAAT {BuildInfo.TitleSuffix}";
            var soloMode = HasScenario ? SessionSoloTrainingMode : _preferences.SoloTrainingMode;
            var modeLabel = soloMode ? "Solo mode" : "RPO mode";
            appLabel = $"{appLabel} [{modeLabel}]";
            if (ActiveRoomName is null)
            {
                return appLabel;
            }

            return ActiveScenarioName is not null ? $"{ActiveRoomName} ({ActiveScenarioName}) - {appLabel}" : $"{ActiveRoomName} - {appLabel}";
        }
    }

    public string ConnectMenuText => IsConnected ? "_Disconnect" : "_Connect";

    public bool IsInRoom => ActiveRoomId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteInRoom))]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand))]
    private bool _isServerRestarting;

    public bool CanExecuteInRoom => IsConnected && IsInRoom && !IsServerRestarting;

    /// <summary>Visible state of the top-of-window restart banner.</summary>
    public enum RestartBanner
    {
        Hidden,
        Draining,
        Disconnected,
        Restored,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRestartBannerVisible))]
    [NotifyPropertyChangedFor(nameof(RestartBannerBackground))]
    private RestartBanner _restartBannerKind = RestartBanner.Hidden;

    [ObservableProperty]
    private string _restartBannerText = "";

    public bool IsRestartBannerVisible => RestartBannerKind != RestartBanner.Hidden;

    public string RestartBannerBackground =>
        RestartBannerKind switch
        {
            RestartBanner.Restored => "#1F4D2E", // muted green
            _ => "#7A4A0A", // amber for Draining / Disconnected
        };

    // Drain countdown + auto-dismiss timer (one per banner episode).
    private DispatcherTimer? _restartBannerTimer;
    private DateTime _restartTargetUtc;
    private DateTime _restartBannerHideAtUtc;

    public bool HasScenario => ActiveScenarioId is not null;

    private LatLon? _distanceRef;

    public ObservableCollection<AircraftModel> Aircraft { get; } = [];

    public DataGridCollectionView AircraftView { get; }

    /// <summary>
    /// Count of delayed-spawn aircraft present at scenario load. Drives the
    /// "to begin with" gate on <see cref="AircraftListTitle"/>: scenarios that
    /// start with zero delayed spawns never surface a count.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AircraftListTitle))]
    private int _initialDelayedSpawnCount;

    /// <summary>
    /// Live count of aircraft still waiting to spawn. Decremented when an
    /// aircraft's <see cref="AircraftModel.IsDelayed"/> flips to false (the
    /// server's spawn event arrives) or when a delayed entry is deleted.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AircraftListTitle))]
    private int _pendingDelayedSpawnCount;

    public string AircraftListTitle
    {
        get
        {
            if (InitialDelayedSpawnCount == 0)
            {
                return "Aircraft List";
            }
            if (PendingDelayedSpawnCount <= 0)
            {
                return "Aircraft List (No pending spawns)";
            }
            return PendingDelayedSpawnCount == 1 ? "Aircraft List (1 pending spawn)" : $"Aircraft List ({PendingDelayedSpawnCount} pending spawns)";
        }
    }

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

    /// <summary>
    /// Predicate backing <see cref="AircraftView"/>'s filter. STARS unsupported data
    /// blocks created by CRC <c>DA</c>/<c>VP</c> typing (<see cref="AircraftModel.IsUnsupported"/>)
    /// are always hidden from the operator-facing list — they have no real aircraft
    /// body and would otherwise display "No altitude asgn". Delayed-spawn aircraft are
    /// hidden only when the "Show only active" toggle is on.
    /// </summary>
    public static bool IsAircraftVisible(AircraftModel ac, bool showOnlyActive, string filter)
    {
        if (ac.IsUnsupported)
        {
            return false;
        }

        if (showOnlyActive && ac.IsDelayed)
        {
            return false;
        }

        return MatchesFilter(ac, filter);
    }

    private static bool MatchesFilter(AircraftModel ac, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        return Contains(ac.Callsign, filter)
            || Contains(ac.AircraftType, filter)
            || Contains(ac.FiledAircraftType, filter)
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

    /// <summary>
    /// Raised after a successful speech transcription has populated <see cref="CommandText"/>
    /// and the user has opted in (<see cref="UserPreferences.AutoFocusInputAfterSpeech"/>) to
    /// having the command input automatically focused. The view subscribes and forwards to
    /// <c>CommandInputView.FocusCommandInput()</c>; the viewmodel can't reach the control
    /// directly without breaking MVVM. Mirrors the existing <see cref="GridLayoutReset"/> pattern.
    /// </summary>
    public event Action? RequestCommandInputFocus;

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

    /// <summary>
    /// Ephemeral case-insensitive substring filter applied on top of the kind toggles.
    /// Empty string disables the filter. Not persisted.
    /// </summary>
    [ObservableProperty]
    private string _terminalSearchText = "";

    /// <summary>
    /// When non-null, exactly one category is currently solo'd (Shift+Click).
    /// </summary>
    private TerminalEntryKind? _terminalSoloKind;

    /// <summary>
    /// The set of kinds that were visible immediately before the current solo started.
    /// Used to restore on Shift+Click of the solo'd category.
    /// </summary>
    private HashSet<TerminalEntryKind> _terminalSoloSnapshot = [];

    /// <summary>
    /// Set true while <see cref="ApplyVisibilityProgrammatic"/> is mutating Show*Entries
    /// during enter/switch/restore solo. The partial change handlers consult this to
    /// skip persistence and the cancel-solo side effect.
    /// </summary>
    private bool _isProgrammaticTerminalToggle;

    partial void OnShowCommandEntriesChanged(bool value) => OnTerminalToggleChanged();

    partial void OnShowResponseEntriesChanged(bool value) => OnTerminalToggleChanged();

    partial void OnShowSystemEntriesChanged(bool value) => OnTerminalToggleChanged();

    partial void OnShowSayEntriesChanged(bool value) => OnTerminalToggleChanged();

    partial void OnShowWarningEntriesChanged(bool value) => OnTerminalToggleChanged();

    partial void OnShowErrorEntriesChanged(bool value) => OnTerminalToggleChanged();

    partial void OnShowChatEntriesChanged(bool value) => OnTerminalToggleChanged();

    partial void OnTerminalSearchTextChanged(string value) => TerminalFilterChanged?.Invoke();

    private void OnTerminalToggleChanged()
    {
        if (_isProgrammaticTerminalToggle)
        {
            return;
        }

        if (_terminalSoloKind is not null)
        {
            _terminalSoloKind = null;
            _terminalSoloSnapshot = [];
        }

        PersistTerminalFilters();
    }

    /// <summary>
    /// Shift+Click on a category toggle. Enters, switches, or exits solo mode.
    /// Snapshot is captured on entry and preserved across switches; restored on exit.
    /// </summary>
    public void OnTerminalCategoryShiftClicked(TerminalEntryKind kind)
    {
        if (_terminalSoloKind == kind)
        {
            // Exit solo: restore the snapshot.
            var snapshot = _terminalSoloSnapshot;
            _terminalSoloKind = null;
            _terminalSoloSnapshot = [];
            ApplyVisibilityProgrammatic(snapshot);
            return;
        }

        if (_terminalSoloKind is null)
        {
            // Enter solo: capture currently-visible kinds.
            _terminalSoloSnapshot = CurrentVisibleKinds();
        }

        // Either entering or switching — same end state.
        _terminalSoloKind = kind;
        ApplyVisibilityProgrammatic([kind]);
    }

    private HashSet<TerminalEntryKind> CurrentVisibleKinds()
    {
        var visible = new HashSet<TerminalEntryKind>();
        if (ShowCommandEntries)
        {
            visible.Add(TerminalEntryKind.Command);
        }

        if (ShowResponseEntries)
        {
            visible.Add(TerminalEntryKind.Response);
        }

        if (ShowSystemEntries)
        {
            visible.Add(TerminalEntryKind.System);
        }

        if (ShowSayEntries)
        {
            visible.Add(TerminalEntryKind.Say);
        }

        if (ShowWarningEntries)
        {
            visible.Add(TerminalEntryKind.Warning);
        }

        if (ShowErrorEntries)
        {
            visible.Add(TerminalEntryKind.Error);
        }

        if (ShowChatEntries)
        {
            visible.Add(TerminalEntryKind.Chat);
        }

        return visible;
    }

    private void ApplyVisibilityProgrammatic(HashSet<TerminalEntryKind> visible)
    {
        _isProgrammaticTerminalToggle = true;
        try
        {
            ShowCommandEntries = visible.Contains(TerminalEntryKind.Command);
            ShowResponseEntries = visible.Contains(TerminalEntryKind.Response);
            ShowSystemEntries = visible.Contains(TerminalEntryKind.System);
            ShowSayEntries = visible.Contains(TerminalEntryKind.Say);
            ShowWarningEntries = visible.Contains(TerminalEntryKind.Warning);
            ShowErrorEntries = visible.Contains(TerminalEntryKind.Error);
            ShowChatEntries = visible.Contains(TerminalEntryKind.Chat);
        }
        finally
        {
            _isProgrammaticTerminalToggle = false;
        }

        TerminalFilterChanged?.Invoke();
    }

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

    public bool IsEntryVisible(TerminalEntry entry)
    {
        if (!IsEntryVisible(entry.Kind))
        {
            return false;
        }

        var query = TerminalSearchText;
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return Contains(entry.Callsign, query) || Contains(entry.Initials, query) || Contains(entry.Message, query);

        static bool Contains(string? value, string query) => value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<TerminalEntry> GetFilteredTerminalEntries() => TerminalEntries.Where(IsEntryVisible);

    public ObservableCollection<string> CommandHistory { get; } = [];

    public ObservableCollection<TerminalEntry> TerminalEntries { get; } = [];

    public ObservableCollection<TrainingRoomInfoDto> ActiveRooms { get; } = [];

    public ObservableCollection<CrcLobbyClientDto> CrcLobbyClients { get; } = [];

    public ObservableCollection<CrcRoomMemberDto> CrcRoomMembers { get; } = [];

    public ObservableCollection<RoomMemberDto> RoomMembers { get; } = [];

    [ObservableProperty]
    private SpeechStatus _speechStatus = SpeechStatus.Idle;

    /// <summary>
    /// Live mirror of <see cref="UserPreferences.SpeechEnabled"/> so the mic status-bar visibility
    /// and right-click context menu check state both react immediately when the user toggles it
    /// — either via Settings save or via the context menu itself. A partial change handler
    /// persists to prefs on write.
    /// </summary>
    [ObservableProperty]
    private bool _isSpeechEnabled;

    partial void OnIsSpeechEnabledChanged(bool value)
    {
        _preferences.SetSpeechEnabled(value);

        // Kick off prewarm when the user toggles speech on at runtime. Null-guarded because the
        // observable-property change handler fires during field initialization before
        // _speechService is assigned in the constructor.
        if (value && _speechService is not null)
        {
            _ = Task.Run(() => _speechService.PrewarmAsync(CancellationToken.None));
        }
    }

    /// <summary>Re-reads the speech-enabled flag from prefs. Called from the Settings save path
    /// so the mirror stays in sync with bulk saves. Setting the property triggers the OnChanged
    /// handler which calls <see cref="UserPreferences.SetSpeechEnabled"/> — that setter is
    /// idempotent (early-returns if unchanged), so this is safe to call even when the value
    /// hasn't actually moved.</summary>
    public void RefreshIsSpeechEnabledFromPrefs()
    {
        IsSpeechEnabled = _preferences.SpeechEnabled;
    }

    public void RefreshWindowTitleFromPrefs()
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    public MainViewModel(IFilePickerService filePicker)
    {
        _filePicker = filePicker;
        _isSpeechEnabled = _preferences.SpeechEnabled;
        _sessionSoloTrainingMode = _preferences.SoloTrainingMode;
        _pilotSpeechAlerts = new PilotSpeechAlertService(_preferences);
        _pilotVoice = new PilotVoiceService(_preferences);

        // Speech pipeline wiring. The order here matters: LlmService must exist before
        // LocalLlmCommandMapper, and SpeechRecognitionService needs all of them.
        _audioCapture = new AudioCaptureService(_preferences);
        _whisperStt = new WhisperSttEngine(_preferences);
        _llmService = new LocalLlmService(new PreferencesLlmRuntimeConfig(_preferences));
        _llmMapper = new LocalLlmCommandMapper(_llmService);
        _llmCallsignResolver = new LocalLlmCallsignResolver(_llmService);
        _speechService = new SpeechRecognitionService(
            _preferences,
            _audioCapture,
            _whisperStt,
            _llmService,
            _ruleMapper,
            _llmMapper,
            _llmCallsignResolver,
            () => BuildSpeechContext()
        );
        _speechService.StatusChanged += HandleSpeechServiceStatusChange;
        _speechService.CommandReady += HandleSpeechServiceCommandReady;

        // Fire-and-forget prewarm so the first PTT press after startup doesn't stall on
        // multi-second Whisper/LLM model load. Guard on SpeechEnabled — disabled users pay
        // no startup cost. OnIsSpeechEnabledChanged also kicks prewarm when toggled on.
        if (_preferences.SpeechEnabled)
        {
            _ = Task.Run(() => _speechService.PrewarmAsync(CancellationToken.None));
        }

        AircraftView = new DataGridCollectionView(Aircraft);
        AircraftView.Filter = obj => obj is not AircraftModel ac || IsAircraftVisible(ac, _showOnlyActiveAircraft, _aircraftFilterText);
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
        // Student entry is always the first strips entry. Additional
        // per-facility entries are appended via OpenStripsEntryForFacilityAsync.
        var studentVm = new VStripsViewModel(_connection, SendCommandForViewAsync, () => _preferences.UserInitials);
        StripsEntries.Add(new VStripsDockEntryViewModel(studentVm, isStudentEntry: true));
        // Subscribe so a strip tab being popped out / docked feeds the same
        // tab-visibility bookkeeping as the three fixed tabs (collapses the
        // TabControl row when the last docked tab disappears, advances
        // SelectedTabIndex off an invisible tab, etc.).
        SubscribeStripsEntry(StripsEntries[0]);
        StripsEntries.CollectionChanged += OnStripsEntriesCollectionChanged;

        _dataGridScale = _preferences.DataGridFontSize / 12.0;
        IsDataGridPoppedOut = _preferences.IsDataGridPoppedOut;
        IsGroundViewPoppedOut = _preferences.IsGroundViewPoppedOut;
        IsRadarViewPoppedOut = _preferences.IsRadarViewPoppedOut;
        // Student Strips entry pop-out state persists across restarts. Non-student
        // per-facility entries are session-scoped and always start docked.
        StripsEntries[0].IsPoppedOut = _preferences.IsVStripsPoppedOut;

        _connection.AircraftUpdated += OnAircraftUpdated;
        _connection.AircraftDeleted += OnAircraftDeleted;
        _connection.AircraftSpawned += OnAircraftSpawned;
        _connection.SimulationStateChanged += OnSimulationStateChanged;
        _connection.Reconnecting += OnReconnecting;
        _connection.Reconnected += OnReconnected;
        _connection.Closed += OnConnectionClosed;
        _connection.ServerRestarting += OnServerRestarting;
        _connection.ServerRestartReady += OnServerRestartReady;
        _connection.ServerRestartComplete += OnServerRestartComplete;
        _connection.RoomAvailableForCid += OnRoomAvailableForCid;
        _connection.TerminalEntryReceived += OnTerminalEntry;
        _connection.PilotTransmissionReceived += OnPilotTransmissionReceived;
        _connection.RoomMemberChanged += OnRoomMemberChanged;
        _connection.CrcLobbyChanged += OnCrcLobbyChanged;
        _connection.CrcRoomMembersChanged += OnCrcRoomMembersChanged;
        _connection.WeatherChanged += OnWeatherChanged;
        _connection.ArrivalGeneratorsChanged += OnArrivalGeneratorsChanged;
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
            await _updateService.DownloadUpdateAsync(
                _pendingUpdate,
                progress => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateProgress = progress)
            );

            // Velopack's restart bypasses Avalonia's window-closing pipeline,
            // so the per-window geometry save (hooked via Window.Closing in
            // WindowGeometryHelper) never runs. Flush all tracked windows
            // before handing off to Velopack.
            WindowGeometryHelper.FlushAllSavedGeometries();

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
        ReloadCommandHistoryForScenario(value);
    }

    /// <summary>
    /// Replaces the in-memory up-arrow recall list with whatever was last persisted for
    /// this scenario. Called on every ActiveScenarioId transition (load, switch, unload).
    /// When the new id is null, the in-memory list is cleared but the saved-to-disk
    /// history for the previously-active scenario stays intact.
    /// </summary>
    private void ReloadCommandHistoryForScenario(string? scenarioId)
    {
        CommandHistory.Clear();
        if (string.IsNullOrEmpty(scenarioId))
        {
            return;
        }

        foreach (var entry in _preferences.GetCommandHistory(scenarioId))
        {
            CommandHistory.Add(entry);
        }
    }

    partial void OnActiveScenarioNameChanged(string? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnActiveScenarioPrimaryAirportIdChanged(string? value)
    {
        RefreshDisplayFavorites();
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
    private SpeechContext BuildSpeechContext(AircraftModel? contextAircraft = null)
    {
        var snapshot = Aircraft.ToArray();
        var callsigns = snapshot.Select(a => a.Callsign).Where(cs => !string.IsNullOrEmpty(cs)).ToList();
        var selected = contextAircraft ?? SelectedAircraft;
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

        // Whisper biasing prompt is the static ATC vocabulary set (NATO alphabet + phonetic
        // numbers + every literal token from PhraseologyRules.All). Built once per process by
        // WhisperBiasingPrompt and reused across PTT presses — no per-PTT construction overhead,
        // no 224-token budget concern, no prompt truncation risk. The probe data showed
        // whisper-large-turbo3 recognizing arbitrary tail numbers cleanly when the NATO alphabet
        // is in the prompt, so we no longer need to inject scenario-specific callsigns or
        // programmed fix names — the static vocabulary covers them.
        var whisperInitialPrompt = WhisperBiasingPrompt.Default;

        // Pull custom-fix speech patterns from the NavigationDatabase. These let the rule engine
        // collapse multi-word natural-language references (e.g. "the runway 30 numbers") into
        // their canonical alias ("OAK30NUM") so downstream {fix} captures work unchanged. Only
        // available after the NavDb has finished loading — returns an empty list until then.
        var navDb = NavigationDatabase.Instance;
        var customFixPatterns = navDb?.CustomFixSpeechPatterns ?? [];

        // Build the callsign → destination map from active aircraft flight plans. Used by the LLM
        // fallback to correlate an in-transcript callsign with the right airport's runway list
        // (e.g. "N9225L" → "KOAK" → KOAK runway list → recover misheard "288" as "28R").
        var aircraftDestinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ac in snapshot)
        {
            if (!string.IsNullOrEmpty(ac.Callsign) && !string.IsNullOrEmpty(ac.Destination))
            {
                aircraftDestinations[ac.Callsign] = ac.Destination;
            }
        }

        // Build the airport → runway-IDs map for every airport relevant to the active scenario.
        // Scope (per the design plan): destinations of all active aircraft, plus the selected
        // aircraft's departure airport (so a controller talking to a still-on-the-ground departure
        // gets runway validation for that airport too). Falls back to empty when the NavDb hasn't
        // finished loading — both the rule-engine validator and the LLM fallback skip their checks
        // in that case.
        var availableRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (navDb is not null)
        {
            var airports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dest in aircraftDestinations.Values)
            {
                airports.Add(dest);
            }
            foreach (var ac in snapshot)
            {
                if (!string.IsNullOrEmpty(ac.Departure))
                {
                    airports.Add(ac.Departure);
                }
            }

            foreach (var airport in airports)
            {
                // RunwayInfo is bidirectional — each entry represents one physical runway with
                // two end designators (e.g. End1="28R", End2="10L"). Enumerate both ends so the
                // validator/LLM see every designator the controller might issue. Distinct in case
                // an airport's data has duplicate entries from both approach directions.
                var rwys = new List<string>();
                foreach (var rwy in navDb.GetRunways(airport))
                {
                    if (!string.IsNullOrEmpty(rwy.Id.End1))
                    {
                        rwys.Add(rwy.Id.End1);
                    }
                    if (!string.IsNullOrEmpty(rwy.Id.End2))
                    {
                        rwys.Add(rwy.Id.End2);
                    }
                }
                var distinct = rwys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinct.Count > 0)
                {
                    availableRunways[airport] = distinct;
                }
            }
        }

        // Taxiway-name set for the currently-loaded ground layout — used by NatoLetterNormalizer
        // to disambiguate multi-letter taxiway names during NATO collapse. The GroundViewModel
        // owns the domain layout because it's the view that reconstructs it from the server DTO;
        // MainViewModel borrows a reference here so the speech pipeline sees the same airport
        // the user is currently looking at. Falls back to empty when no ground layout is loaded —
        // single-letter splits still work in that case.
        var taxiwayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layout = Ground.DomainLayout;
        if (layout is not null)
        {
            foreach (var edge in layout.Edges)
            {
                if (edge.IsRunwayCenterline || edge.IsRamp)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(edge.TaxiwayName))
                {
                    taxiwayNames.Add(edge.TaxiwayName.ToUpperInvariant());
                }
            }
        }

        return new SpeechContext(callsigns, programmedFixes, whisperInitialPrompt)
        {
            CustomFixPatterns = customFixPatterns,
            AvailableRunways = availableRunways,
            AircraftDestinations = aircraftDestinations,
            TaxiwayNames = taxiwayNames,
        };
    }

    private void HandleSpeechServiceStatusChange(SpeechStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SpeechStatus = status);
    }

    private void HandleSpeechServiceCommandReady(SpeechResult result)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var populatedCommandText = false;
            string? source = null;
            if (!string.IsNullOrEmpty(result.CanonicalCommand))
            {
                // Prepend the extracted callsign when present so SendCommandAsync's existing
                // CallsignPrefixResolver path auto-dispatches to the right aircraft on Enter.
                // Format: "SWA123 FH 270" — single space, leading token, matches CallsignPrefixResolver.
                CommandText = string.IsNullOrEmpty(result.Callsign) ? result.CanonicalCommand : $"{result.Callsign} {result.CanonicalCommand}";
                populatedCommandText = true;
                source = "canonical";
            }
            else if (!string.IsNullOrWhiteSpace(result.Transcript))
            {
                // Neither mapper produced a canonical command — surface the raw transcript so the
                // user sees what Whisper heard and can correct manually. Better than silently
                // dropping the input.
                CommandText = result.Transcript;
                populatedCommandText = true;
                source = "raw-transcript";
            }

            if (populatedCommandText)
            {
                // Reset caret to end of populated text so cursor-aware suggestions work on the
                // newly populated string, not against a stale caret from an earlier entry.
                CommandCaretIndex = CommandText.Length;
                _log.LogInformation("Speech populated command box ({Source}): {CommandText}", source, CommandText);
            }

            // Auto-focus the command input box so the user can press Enter to send (or arrow-keys
            // to edit) without mousing for the input. Only fire when we actually wrote something
            // and the user has the preference enabled — empty transcripts shouldn't pull focus
            // away from whatever the user was doing.
            if (populatedCommandText && _preferences.AutoFocusInputAfterSpeech)
            {
                RequestCommandInputFocus?.Invoke();
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
        // Clamp caret to new text length so the controller never sees a stale out-of-range index.
        if (CommandCaretIndex > value.Length)
        {
            CommandCaretIndex = value.Length;
        }
        _commandInput.UpdateSuggestions(value, CommandCaretIndex, Aircraft, _preferences.CommandScheme, SelectedAircraft);
        _commandInput.UpdateSignatureHelp(value, CommandCaretIndex, _preferences.CommandScheme);
    }

    partial void OnCommandCaretIndexChanged(int value)
    {
        // Skip caret-only updates while navigating history — the controller dismissed
        // suggestions and we don't want a follow-up MoveCaret to revive them.
        if (_commandInput.IsNavigatingHistory)
        {
            return;
        }
        _commandInput.UpdateSuggestions(CommandText, value, Aircraft, _preferences.CommandScheme, SelectedAircraft);
        _commandInput.UpdateSignatureHelp(CommandText, value, _preferences.CommandScheme);
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

        var prefixResult = CallsignPrefixResolver.Resolve(commandText, scheme, Aircraft);
        if (prefixResult is CallsignPrefixResolver.Ambiguous ambiguousPrefix)
        {
            StatusText = ambiguousPrefix.Message;
            return;
        }

        string? resolvedCallsign = null;
        if (prefixResult is CallsignPrefixResolver.Resolved resolvedPrefix)
        {
            target = resolvedPrefix.Aircraft;
            commandText = resolvedPrefix.Remainder;
            resolvedCallsign = target.Callsign;
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
            if (
                SessionSoloTrainingMode
                && await TryDispatchSoloNaturalCommandAsync(originalInput, commandText, target, resolvedCallsign, forceOverride)
            )
            {
                return;
            }

            if (parseFailure is not null)
            {
                _log.LogWarning(
                    "Command '{Verb}' {Reason} (expected: {Expected}) in input '{Input}'",
                    parseFailure.Verb,
                    parseFailure.Reason,
                    parseFailure.Expected ?? "(unknown)",
                    commandText
                );
                StatusText = parseFailure.Expected is { } expected
                    ? $"\"{parseFailure.Verb}\" {parseFailure.Reason}. Expected: {expected}"
                    : $"\"{parseFailure.Verb}\" {parseFailure.Reason}";
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
            // Half-strip commands (HSC/HSA/HSD) are dual-mode: when no aircraft is targeted,
            // they run globally with an empty callsign and the server treats them as freeform.
            if (TryGetCanonicalVerb(compound.CanonicalString, out var verb) && IsHalfStripVerb(verb))
            {
                try
                {
                    var canonical = compound.CanonicalString;
                    _log.LogDebug("SendCommand (global half-strip): '{Canonical}' (input: '{Input}')", canonical, originalInput);
                    var result = await _connection.SendCommandAsync("", canonical, _preferences.UserInitials);

                    // Always drop the typed text: even when the server rejects, the RPO has
                    // seen the result and should not retype the whole command. The error
                    // message surfaces through StatusText and the terminal history.
                    AddHistory(originalInput);
                    _commandInput.DismissSuggestions();
                    _commandInput.ResetHistoryNavigation();
                    CommandText = "";

                    StatusText = CommandStatusResolver.Resolve(result, "(half-strip)");
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Half-strip command failed");
                    StatusText = $"Command error: {ex.Message}";
                }

                return;
            }

            StatusText = "No aircraft matched — type a callsign (or partial) before the command";
            return;
        }

        SelectedAircraft = target;

        try
        {
            var canonical = forceOverride ? $"** {compound.CanonicalString}" : compound.CanonicalString;
            _log.LogDebug("SendCommand: {Callsign} '{Canonical}' (input: '{Input}')", target.Callsign, canonical, originalInput);
            var result = await _connection.SendCommandAsync(target.Callsign, canonical, _preferences.UserInitials);

            if (result.Success)
            {
                RecordCommandMarker(target.Callsign, compound.CanonicalString, result.ServerElapsedSeconds);
            }

            // Always drop the typed text: even when the server rejects (or the pilot
            // soft-fails e.g. RTIS "looking"), the RPO has seen the result and should
            // not retype the whole command. The error surfaces through StatusText and
            // the terminal history. The leading callsign is stripped so up-arrow recall
            // produces the canonical command alone — easy to rerun on a new aircraft.
            AddHistory(CommandHistoryFormatter.Format(originalInput, resolvedCallsign, compound.CanonicalString));
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";

            StatusText = CommandStatusResolver.Resolve(result, target.Callsign);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Command failed");
            StatusText = $"Command error: {ex.Message}";
        }
    }

    private async Task<bool> TryDispatchSoloNaturalCommandAsync(
        string originalInput,
        string commandText,
        AircraftModel? currentTarget,
        string? resolvedCallsign,
        bool forceOverride
    )
    {
        var allowLlmFallback = _preferences.SpeechEnabled && _llmService.IsConfigured;
        var normalization = await NaturalCommandNormalizer.TryNormalizeAsync(
            originalInput,
            BuildSpeechContext(currentTarget),
            _ruleMapper,
            allowLlmFallback ? _llmMapper : null,
            allowLlmFallback ? _llmCallsignResolver : null,
            CancellationToken.None
        );

        if (normalization is null)
        {
            return false;
        }

        var scheme = _preferences.CommandScheme;
        var mappedCompound = CommandSchemeParser.ParseCompound(normalization.CanonicalCommand, scheme, out var mappedFailure);
        if (mappedCompound is null)
        {
            _log.LogDebug(
                "Solo natural mapping did not produce a dispatchable command. Input='{Input}', Mapped='{Mapped}', Reason='{Reason}'",
                commandText,
                normalization.CanonicalCommand,
                mappedFailure?.Reason ?? "parse failed"
            );
            return false;
        }

        var target = currentTarget;
        var historyCallsign = resolvedCallsign;
        if (!string.IsNullOrWhiteSpace(normalization.Callsign))
        {
            target = ResolveAircraft(normalization.Callsign);
            if (target is null)
            {
                return false;
            }
            historyCallsign = normalization.Callsign;
        }

        if (target is null)
        {
            return false;
        }

        SelectedAircraft = target;

        try
        {
            var canonical = forceOverride ? $"** {mappedCompound.CanonicalString}" : mappedCompound.CanonicalString;
            _log.LogInformation(
                "Solo natural command mapped: {Input} -> {Callsign} {Canonical} (LLM fallback: {UsedLlmFallback})",
                originalInput,
                target.Callsign,
                canonical,
                normalization.UsedLlmFallback
            );
            var result = await _connection.SendCommandAsync(target.Callsign, canonical, _preferences.UserInitials);

            if (result.Success)
            {
                RecordCommandMarker(target.Callsign, mappedCompound.CanonicalString, result.ServerElapsedSeconds);
            }

            AddHistory(CommandHistoryFormatter.Format(originalInput, historyCallsign, mappedCompound.CanonicalString));
            _commandInput.DismissSuggestions();
            _commandInput.ResetHistoryNavigation();
            CommandText = "";

            StatusText = CommandStatusResolver.Resolve(result, target.Callsign);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Solo natural command failed");
            StatusText = $"Command error: {ex.Message}";
        }

        return true;
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
                StatusText = CommandStatusResolver.Resolve(result, "ADD");
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
                StatusText = CommandStatusResolver.Resolve(result, verb);
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
                StatusText = CommandStatusResolver.Resolve(result, verb);
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
                StatusText = CommandStatusResolver.Resolve(result, "TAXIALL");
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

    private static bool TryGetCanonicalVerb(string canonical, out string verb)
    {
        var trimmed = canonical.TrimStart();
        var spaceIdx = trimmed.IndexOf(' ');
        verb = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
        return verb.Length > 0;
    }

    private static bool IsHalfStripVerb(string verb)
    {
        return string.Equals(verb, "HSC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "HSA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "HSD", StringComparison.OrdinalIgnoreCase);
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
        var token = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        if (!Callsign.IsValid(token))
        {
            StatusText = $"\"{token}\" is not a valid callsign";
            return;
        }
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
            _ = SendSoloTrainingMode();
            _ = SendRpoShowPilotSpeech();
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
            _distanceRef = null;
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

        _distanceRef = resolved.Value;
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
        if (_distanceRef is null)
        {
            return null;
        }

        if (model.IsDelayed)
        {
            return null;
        }

        return GeoMath.DistanceNm(model.Position, _distanceRef.Value);
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

    internal void ApplySessionSettings(SessionSettingsDto dto)
    {
        _isApplyingSessionSettings = true;
        ActiveAutoDeleteMode = dto.EffectiveAutoDeleteMode;
        SessionAutoDeleteIndex = AutoDeleteModeToIndex(dto.AutoDeleteOverride);
        SessionAutoAcceptDelaySeconds = dto.AutoAcceptDelaySeconds;
        SessionAutoClearedToLand = dto.AutoClearedToLand;
        SessionAutoCrossRunway = dto.AutoCrossRunway;
        SessionValidateDctFixes = dto.ValidateDctFixes;
        SessionSoloTrainingMode = dto.SoloTrainingMode;
        SessionSoloParkingInitialCallupRatePercent = dto.SoloParkingInitialCallupRatePercent;
        SessionSoloParkingInitialCallupIntervalSeconds = ParkingInitialCallupRateToIntervalSeconds(dto.SoloParkingInitialCallupRatePercent);
        SessionSoloArrivalGeneratorRatePercent = dto.SoloArrivalGeneratorRatePercent;
        SessionSoloGoAroundProbabilityPercent = dto.SoloGoAroundProbabilityPercent;
        SessionHasSoloParkingInitialCallupSource = dto.HasSoloParkingInitialCallupSource;
        SessionHasSoloArrivalGeneratorSource = dto.HasSoloArrivalGeneratorSource;
        SessionRpoShowPilotSpeech = dto.RpoShowPilotSpeech;
        _isApplyingSessionSettings = false;

        ApplyAutoClearedToLandLocally(dto.AutoClearedToLand);
    }

    private void ApplySessionSettingsFromRoom(RoomStateDto state)
    {
        ApplySessionSettings(
            new SessionSettingsDto(
                state.AutoDeleteOverride,
                state.EffectiveAutoDeleteMode,
                state.AutoAcceptDelaySeconds,
                state.AutoClearedToLand,
                state.AutoCrossRunway,
                state.ValidateDctFixes,
                state.SoloTrainingMode,
                state.SoloParkingInitialCallupRatePercent,
                state.SoloArrivalGeneratorRatePercent,
                state.SoloGoAroundProbabilityPercent,
                state.HasSoloParkingInitialCallupSource,
                state.HasSoloArrivalGeneratorSource,
                state.RpoShowPilotSpeech
            )
        );
    }

    private void ApplySessionSettingsFromScenarioLoaded(ScenarioLoadedDto dto)
    {
        ApplySessionSettings(
            new SessionSettingsDto(
                dto.AutoDeleteOverride,
                dto.EffectiveAutoDeleteMode,
                dto.AutoAcceptDelaySeconds,
                dto.AutoClearedToLand,
                dto.AutoCrossRunway,
                dto.ValidateDctFixes,
                dto.SoloTrainingMode,
                dto.SoloParkingInitialCallupRatePercent,
                dto.SoloArrivalGeneratorRatePercent,
                dto.SoloGoAroundProbabilityPercent,
                dto.HasSoloParkingInitialCallupSource,
                dto.HasSoloArrivalGeneratorSource,
                dto.RpoShowPilotSpeech
            )
        );
    }

    internal void ApplySessionSettingsFromLoadScenarioResult(LoadScenarioResultDto result)
    {
        ApplySessionSettings(
            new SessionSettingsDto(
                result.AutoDeleteOverride,
                result.EffectiveAutoDeleteMode,
                result.AutoAcceptDelaySeconds,
                result.AutoClearedToLand,
                result.AutoCrossRunway,
                result.ValidateDctFixes,
                result.SoloTrainingMode,
                result.SoloParkingInitialCallupRatePercent,
                result.SoloArrivalGeneratorRatePercent,
                result.SoloGoAroundProbabilityPercent,
                result.HasSoloParkingInitialCallupSource,
                result.HasSoloArrivalGeneratorSource,
                result.RpoShowPilotSpeech
            )
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
            ApplyAutoClearedToLandLocally(value);
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

    partial void OnSessionSoloTrainingModeChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ShowSessionSoloParkingInitialCallupRate));
        OnPropertyChanged(nameof(ShowSessionSoloArrivalGeneratorRate));
        OnPropertyChanged(nameof(ShowSessionSoloGoAroundProbability));
        if (!_isApplyingSessionSettings)
        {
            _ = _connection.SetSoloTrainingModeAsync(value);
        }
    }

    partial void OnSessionSoloParkingInitialCallupRatePercentChanged(int value)
    {
        OnSessionSoloPacingRateChanged(value, 0, 200, isParkingRate: true);
    }

    partial void OnScenarioSetupParkingInitialCallupIntervalSecondsChanged(int value)
    {
        var clamped = NormalizeParkingInitialCallupIntervalSeconds(value);
        if (clamped != value)
        {
            ScenarioSetupParkingInitialCallupIntervalSeconds = clamped;
            return;
        }

        ScenarioSetupParkingInitialCallupRatePercent = ParkingInitialCallupIntervalSecondsToRate(clamped);
        OnPropertyChanged(nameof(ScenarioSetupParkingInitialCallupIntervalLabel));
    }

    partial void OnSessionSoloParkingInitialCallupIntervalSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(SessionSoloParkingInitialCallupIntervalLabel));
        if (_isApplyingSessionSettings)
        {
            return;
        }

        var clamped = NormalizeParkingInitialCallupIntervalSeconds(value);
        if (clamped != value)
        {
            SessionSoloParkingInitialCallupIntervalSeconds = clamped;
            return;
        }

        _sessionSoloParkingInitialCallupRatePercent = ParkingInitialCallupIntervalSecondsToRate(clamped);
        OnPropertyChanged(nameof(SessionSoloParkingInitialCallupRatePercent));
        _ = _connection.SetSoloPacingRatesAsync(
            SessionSoloParkingInitialCallupRatePercent,
            SessionSoloArrivalGeneratorRatePercent,
            SessionSoloGoAroundProbabilityPercent
        );
    }

    partial void OnSessionSoloArrivalGeneratorRatePercentChanged(int value)
    {
        OnSessionSoloPacingRateChanged(value, 0, 100, isParkingRate: false);
    }

    partial void OnSessionSoloGoAroundProbabilityPercentChanged(int value)
    {
        if (_isApplyingSessionSettings)
        {
            return;
        }

        var clamped = Math.Clamp(value, 0, 100);
        if (clamped != value)
        {
            SessionSoloGoAroundProbabilityPercent = clamped;
            return;
        }

        _ = _connection.SetSoloPacingRatesAsync(
            SessionSoloParkingInitialCallupRatePercent,
            SessionSoloArrivalGeneratorRatePercent,
            SessionSoloGoAroundProbabilityPercent
        );

        // Mid-session drag persists per-scenario so the next load of this scenario
        // seeds with the operator's last value (matches the Scenario Setup confirm path).
        if (!string.IsNullOrEmpty(ActiveScenarioId))
        {
            _preferences.SetSoloGoAroundProbabilityForScenario(ActiveScenarioId, SessionSoloGoAroundProbabilityPercent);
        }
    }

    partial void OnSessionHasSoloParkingInitialCallupSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSessionSoloParkingInitialCallupRate));
    }

    partial void OnSessionHasSoloArrivalGeneratorSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSessionSoloArrivalGeneratorRate));
    }

    private void OnSessionSoloPacingRateChanged(int value, int minimum, int maximum, bool isParkingRate)
    {
        if (_isApplyingSessionSettings)
        {
            return;
        }

        var clamped = Math.Clamp(value, minimum, maximum);
        if (clamped != value)
        {
            if (isParkingRate)
            {
                SessionSoloParkingInitialCallupRatePercent = clamped;
            }
            else
            {
                SessionSoloArrivalGeneratorRatePercent = clamped;
            }
            return;
        }

        _ = _connection.SetSoloPacingRatesAsync(
            SessionSoloParkingInitialCallupRatePercent,
            SessionSoloArrivalGeneratorRatePercent,
            SessionSoloGoAroundProbabilityPercent
        );
    }

    private static int NormalizeParkingInitialCallupIntervalSeconds(int seconds) => seconds <= 0 ? 0 : Math.Clamp(seconds, 10, 120);

    private static int ParkingInitialCallupRateToIntervalSeconds(int ratePercent)
    {
        var rate = Math.Clamp(ratePercent, 0, 200);
        if (rate <= 0)
        {
            return 0;
        }

        var seconds = (int)(Math.Round((2000.0 / rate) / 10.0) * 10);
        return NormalizeParkingInitialCallupIntervalSeconds(seconds);
    }

    private static int ParkingInitialCallupIntervalSecondsToRate(int seconds)
    {
        var interval = NormalizeParkingInitialCallupIntervalSeconds(seconds);
        if (interval <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(2000.0 / interval), 0, 200);
    }

    private static string FormatParkingInitialCallupInterval(int seconds)
    {
        var interval = NormalizeParkingInitialCallupIntervalSeconds(seconds);
        return interval <= 0 ? "Paused" : $"Once per {interval} sec";
    }

    partial void OnSessionRpoShowPilotSpeechChanged(bool value)
    {
        if (!_isApplyingSessionSettings)
        {
            _ = _connection.SetRpoShowPilotSpeechAsync(value);
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

    private async Task SendRpoShowPilotSpeech()
    {
        try
        {
            await _connection.SetRpoShowPilotSpeechAsync(_preferences.RpoShowPilotSpeech);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set RPO pilot-speech rendering");
        }
    }

    private async Task SendSoloTrainingMode()
    {
        try
        {
            await _connection.SetSoloTrainingModeAsync(_preferences.SoloTrainingMode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set solo training mode");
        }
    }

    private async Task SendAutoClearedToLand()
    {
        try
        {
            var value = _preferences.GetAutoClearedToLand(_studentPositionType);
            ApplyAutoClearedToLandLocally(value);
            await _connection.SetAutoClearedToLandAsync(value);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set auto-cleared-to-land");
        }
    }

    /// <summary>
    /// Push a new "Auto Cleared-to-Land" value into local client state: stores it
    /// in <see cref="_isAutoClearedToLand"/> (read by
    /// <c>ApplyAutoClearedToLand(AircraftModel)</c> when fresh aircraft DTOs arrive)
    /// and updates every existing <see cref="AircraftModel"/> so the in-list red
    /// "No landing clnc" alert tracks the live setting. Called from the in-session
    /// flyout toggle, the cross-RPO settings broadcast, and scenario-load wiring.
    /// </summary>
    internal void ApplyAutoClearedToLandLocally(bool value)
    {
        _isAutoClearedToLand = value;
        AutoClearedToLandSync.ApplyToAircraft(Aircraft, value);
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

    internal void AddHistory(string entry)
    {
        var normalizedEntry = entry.ToUpperInvariant();
        for (var i = CommandHistory.Count - 1; i >= 0; i--)
        {
            if (string.Equals(CommandHistory[i], normalizedEntry, StringComparison.OrdinalIgnoreCase))
            {
                CommandHistory.RemoveAt(i);
            }
        }

        CommandHistory.Insert(0, normalizedEntry);
        while (CommandHistory.Count > 50)
        {
            CommandHistory.RemoveAt(CommandHistory.Count - 1);
        }

        // Persist per-scenario; commands typed with no active scenario are kept
        // in memory only (lost on next scenario load, per the chosen design).
        var scenarioId = ActiveScenarioId;
        if (!string.IsNullOrEmpty(scenarioId))
        {
            _preferences.SetCommandHistory(scenarioId, CommandHistory);
        }
    }
}
