using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public sealed partial class SavedServer : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    public SavedServer() { }

    public SavedServer(string name, string url)
    {
        _name = name;
        _url = url;
    }
}

public sealed class UserPreferences
{
    private static readonly ILogger Log = AppLog.CreateLogger<UserPreferences>();

    private static readonly string ConfigDir = YaatPaths.AppDataRoot;

    private static readonly string ConfigPath = YaatPaths.Combine("preferences.json");

    // Process-wide lock to serialize file IO on ConfigPath. All
    // UserPreferences instances share ConfigPath, so concurrent reads (Load
    // in the ctor) and writes (Save) would otherwise race on File.Move and
    // fail with UnauthorizedAccessException (Windows) or trample each
    // other's intermediate .tmp files (Linux).
    private static readonly object FileLock = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SavedPrefs _data;
    private CommandScheme _commandScheme;
    private List<MacroDefinition> _macros;

    public UserPreferences()
    {
        _data = Load();
        _commandScheme = _data.CommandScheme is not null ? FromSaved(_data.CommandScheme) ?? CommandScheme.Default() : CommandScheme.Default();
        _macros = _data.Macros.Select(m => new MacroDefinition { Name = m.Name, Expansion = m.Expansion }).ToList();
        HiddenTerminalKinds = _data
            .HiddenTerminalKinds.Where(s => Enum.TryParse<TerminalEntryKind>(s, out _))
            .Select(s => Enum.Parse<TerminalEntryKind>(s))
            .ToHashSet();
    }

    public CommandScheme CommandScheme => _commandScheme;
    public IReadOnlyList<SavedServer> SavedServers => _data.SavedServers;
    public string LastUsedServerUrl => _data.LastUsedServerUrl;
    public string VatsimCid => _data.VatsimCid;
    public string UserInitials => _data.UserInitials;
    public string ArtccId => _data.ArtccId;

    public string? LastActiveRoomId
    {
        get => _data.LastActiveRoomId;
        set
        {
            _data.LastActiveRoomId = value;
            Save();
        }
    }
    public bool IsAdminMode => _data.IsAdminMode;
    public string AdminPassword => _data.AdminPassword;
    public string TrainingKey => _data.TrainingKey;
    public SavedWindowGeometry? MainWindowGeometry => _data.MainWindowGeometry;
    public SavedWindowGeometry? SettingsWindowGeometry => _data.SettingsWindowGeometry;
    public SavedWindowGeometry? TerminalWindowGeometry => _data.TerminalWindowGeometry;
    public SavedWindowGeometry? GroundViewWindowGeometry => _data.GroundViewWindowGeometry;
    public SavedWindowGeometry? RadarViewWindowGeometry => _data.RadarViewWindowGeometry;
    public SavedWindowGeometry? DataGridWindowGeometry => _data.DataGridWindowGeometry;

    /// <summary>
    /// Raised when <see cref="SetWindowTopmost"/> is called (e.g. from the Settings Save button).
    /// WindowGeometryHelper subscribes so that already-open windows update their Topmost state
    /// immediately, instead of waiting until the next time the window is opened.
    /// </summary>
    public event Action<string, bool>? WindowTopmostChanged;

    public SavedWindowGeometry? GetWindowGeometry(string windowName)
    {
        return windowName switch
        {
            "Main" => _data.MainWindowGeometry,
            "Settings" => _data.SettingsWindowGeometry,
            "Terminal" => _data.TerminalWindowGeometry,
            "GroundView" => _data.GroundViewWindowGeometry,
            "RadarView" => _data.RadarViewWindowGeometry,
            "DataGrid" => _data.DataGridWindowGeometry,
            _ => _data.WindowGeometries.GetValueOrDefault(windowName),
        };
    }

    /// <summary>
    /// Returns all dictionary-stored window-geometry keys whose names start
    /// with the given prefix. Used to apply a single Settings toggle to
    /// every multi-instance window of one kind (e.g. all popped-out Flight
    /// Strips windows, which use keys "VStripsView" and "VStripsView:{facility}").
    /// </summary>
    public IEnumerable<string> GetWindowGeometryKeysStartingWith(string prefix)
    {
        foreach (var key in _data.WindowGeometries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return key;
            }
        }
    }

    public SavedGridLayout? GridLayout => _data.GridLayout;
    public bool AutoAcceptEnabled => _data.AutoAcceptEnabled;
    public int AutoAcceptDelaySeconds => _data.AutoAcceptDelaySeconds;
    public string AutoDeleteOverride => _data.AutoDeleteOverride;
    public bool IsDataGridPoppedOut => _data.IsDataGridPoppedOut;
    public bool IsGroundViewPoppedOut => _data.IsGroundViewPoppedOut;
    public bool IsRadarViewPoppedOut => _data.IsRadarViewPoppedOut;
    public bool IsControllersPoppedOut => _data.IsControllersPoppedOut;
    public bool IsMetarPoppedOut => _data.IsMetarPoppedOut;
    public bool IsVStripsPoppedOut => _data.IsVStripsPoppedOut;
    public bool IsVTdlsPoppedOut => _data.IsVTdlsPoppedOut;
    public bool IsVTdlsDarkMode => _data.IsVTdlsDarkMode;

    public void SetVTdlsDarkMode(bool enabled)
    {
        if (_data.IsVTdlsDarkMode == enabled)
        {
            return;
        }
        _data.IsVTdlsDarkMode = enabled;
        Save();
    }

    public bool IsTerminalDocked => _data.IsTerminalDocked;
    public bool ShowOnlyActiveAircraft => _data.ShowOnlyActiveAircraft;
    public bool ShowTimelineBar => _data.ShowTimelineBar;
    public bool DataGridAlternatingRowColor => _data.DataGridAlternatingRowColor;
    public string? LastScenarioFolder => _data.LastScenarioFolder;
    public string? LastWeatherFolder => _data.LastWeatherFolder;
    public IReadOnlyList<MacroDefinition> Macros => _macros;
    public bool ValidateDctFixes => _data.ValidateDctFixes;
    public bool EuroScopeMode => _data.EuroScopeMode;
    public bool FlashNoLandingClearance => _data.FlashNoLandingClearance;
    public bool ShowSpeechBubbles => _data.ShowSpeechBubbles;
    public double SpeechBubbleDurationMultiplier => Math.Clamp(_data.SpeechBubbleDurationMultiplier, 0.25, 4.0);
    public bool ShowWarningSpeechBubbles => _data.ShowWarningSpeechBubbles;
    public bool AlwaysShowGroundBubblesOnRadar => _data.AlwaysShowGroundBubblesOnRadar;
    public bool AutoClearedToLandGnd => _data.AutoClearedToLandGnd;
    public bool AutoClearedToLandTwr => _data.AutoClearedToLandTwr;
    public bool AutoClearedToLandApp => _data.AutoClearedToLandApp;
    public bool AutoClearedToLandCtr => _data.AutoClearedToLandCtr;
    public bool AutoCrossRunway => _data.AutoCrossRunway;
    public bool SoloTrainingMode => _data.SoloTrainingMode;
    public int SoloParkingInitialCallupRatePercent => Math.Clamp(_data.SoloParkingInitialCallupRatePercent, 0, 200);
    public int SoloArrivalGeneratorRatePercent => Math.Clamp(_data.SoloArrivalGeneratorRatePercent, 0, 100);
    public int SoloGoAroundProbabilityPercent => Math.Clamp(_data.SoloGoAroundProbabilityPercent, 0, 100);
    public bool RpoShowPilotSpeech => _data.RpoShowPilotSpeech;
    public bool RpoPilotSpeechAudibleAlert => _data.RpoPilotSpeechAudibleAlert;
    public bool PilotVoiceEnabled => _data.PilotVoiceEnabled;
    public int PilotVoiceVolume => Math.Clamp(_data.PilotVoiceVolume, 0, 100);
    public bool PilotVoiceRadioFxEnabled => _data.PilotVoiceRadioFxEnabled;

    public bool GetAutoClearedToLand(string? positionType)
    {
        return positionType?.ToUpperInvariant() switch
        {
            "GND" => AutoClearedToLandGnd,
            "TWR" => AutoClearedToLandTwr,
            "APP" => AutoClearedToLandApp,
            "CTR" => AutoClearedToLandCtr,
            _ => true,
        };
    }

    /// <summary>
    /// User-managed window-layout profiles. Restored on demand via the
    /// View → Window Profiles menu; the list is not auto-applied at startup.
    /// Ordered as the user last sorted them (by-name for now; insertion order otherwise).
    /// </summary>
    public IReadOnlyList<SavedWindowProfile> WindowProfiles => _data.WindowProfiles;

    /// <summary>
    /// Adds a new profile, or replaces an existing one with the same name (case-insensitive).
    /// Sorts by name afterwards so the menu order is stable.
    /// </summary>
    public void SaveWindowProfile(SavedWindowProfile profile)
    {
        var name = profile.Name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        profile.Name = name;
        var existingIndex = _data.WindowProfiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            // Preserve CreatedUtc across overwrites; only bump ModifiedUtc.
            profile.CreatedUtc = _data.WindowProfiles[existingIndex].CreatedUtc;
            profile.ModifiedUtc = DateTime.UtcNow;
            _data.WindowProfiles[existingIndex] = profile;
        }
        else
        {
            if (profile.CreatedUtc == default)
            {
                profile.CreatedUtc = DateTime.UtcNow;
            }
            profile.ModifiedUtc = DateTime.UtcNow;
            _data.WindowProfiles.Add(profile);
        }

        _data.WindowProfiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Save();
        RaiseWindowProfilesChanged();
    }

    public void DeleteWindowProfile(string name)
    {
        var removed = _data.WindowProfiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Save();
            RaiseWindowProfilesChanged();
        }
    }

    /// <summary>Returns true on rename, false when oldName not found or newName collides with another profile.</summary>
    public bool RenameWindowProfile(string oldName, string newName)
    {
        var trimmed = newName.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var existing = _data.WindowProfiles.FirstOrDefault(p => string.Equals(p.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }

        if (!string.Equals(oldName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            // Collision: a different profile already uses the target name.
            var collision = _data.WindowProfiles.Any(p => p != existing && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (collision)
            {
                return false;
            }
        }

        existing.Name = trimmed;
        existing.ModifiedUtc = DateTime.UtcNow;
        _data.WindowProfiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Save();
        RaiseWindowProfilesChanged();
        return true;
    }

    public SavedWindowProfile? GetWindowProfile(string name) =>
        _data.WindowProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Raised after the WindowProfiles collection changes (add/delete/rename) so the View menu can re-populate.</summary>
    public event Action? WindowProfilesChanged;

    /// <summary>Called by the public mutators above after Save(). Lets MainWindow refresh its menu.</summary>
    private void RaiseWindowProfilesChanged() => WindowProfilesChanged?.Invoke();

    public IReadOnlyList<FavoriteCommand> FavoriteCommands => _data.FavoriteCommands;
    public int FavoritePanelColumns => Math.Clamp(_data.FavoritePanelColumns, 1, 20);
    public IReadOnlyList<RecentScenario> RecentScenarios => _data.RecentScenarios;
    public IReadOnlyList<RecentWeather> RecentWeatherFiles => _data.RecentWeatherFiles;
    public string AircraftSelectKey => _data.AircraftSelectKey;
    public string FocusInputKey => _data.FocusInputKey;
    public string TakeControlKey => _data.TakeControlKey;
    public string AlwaysOnTopKey => _data.AlwaysOnTopKey;
    public bool SpeechEnabled => _data.SpeechEnabled;
    public string WhisperModelSize => _data.WhisperModelSize;
    public string LlmModelPath => _data.LlmModelPath;
    public int LlmGpuLayers => _data.LlmGpuLayers;
    public string PttKey => _data.PttKey;
    public string AudioInputDevice => _data.AudioInputDevice;
    public string AudioOutputDevice => _data.AudioOutputDevice;
    public bool AutoFocusInputAfterSpeech => _data.AutoFocusInputAfterSpeech;
    public HashSet<TerminalEntryKind> HiddenTerminalKinds { get; private set; } = [];
    public bool GroundShowRunwayLabels => _data.GroundShowRunwayLabels;
    public bool GroundShowTaxiwayLabels => _data.GroundShowTaxiwayLabels;
    public GroundFilterMode GroundShowHoldShort => (GroundFilterMode)_data.GroundShowHoldShort;
    public GroundFilterMode GroundShowParking => (GroundFilterMode)_data.GroundShowParking;
    public GroundFilterMode GroundShowSpot => (GroundFilterMode)_data.GroundShowSpot;
    public bool GroundPanZoomLocked => _data.GroundPanZoomLocked;
    public bool GroundHideDataBlocksByDefault => _data.GroundHideDataBlocksByDefault;
    public bool GroundShowSatelliteImage => _data.GroundShowSatelliteImage;
    public int GroundSatelliteImageBrightness => _data.GroundSatelliteImageBrightness;
    public bool GroundShowVideoMapOverlay => _data.GroundShowVideoMapOverlay;
    public int GroundVideoMapOverlayBrightness => _data.GroundVideoMapOverlayBrightness;
    public bool GroundShowYaatLayout => _data.GroundShowYaatLayout;
    public int GroundYaatLayoutBrightness => _data.GroundYaatLayoutBrightness;
    public bool AssignmentTintEnabled => _data.AssignmentTintEnabled;
    public string AssignmentTintColor => _data.AssignmentTintColor;
    public bool UnassignedTintEnabled => _data.UnassignedTintEnabled;
    public string UnassignedTintColor => _data.UnassignedTintColor;
    public string SelectedColor => _data.SelectedColor;

    public GroundColorScheme GroundColors =>
        new(
            _data.GroundBackgroundColor,
            _data.GroundTaxiwayColor,
            _data.GroundTaxiLabelColor,
            _data.GroundRampEdgeColor,
            _data.GroundHoldShortColor,
            _data.GroundRunwayFillColor,
            _data.GroundRunwayOutlineColor,
            _data.GroundAircraftColor,
            _data.GroundDatablockTextColor,
            _data.GroundBrightness
        );

    public TerminalColorScheme TerminalColors =>
        new(
            _data.TerminalCommandColor,
            _data.TerminalResponseColor,
            _data.TerminalSystemColor,
            _data.TerminalSayColor,
            _data.TerminalPilotSpeechColor,
            _data.TerminalWarningColor,
            _data.TerminalErrorColor,
            _data.TerminalChatColor,
            _data.TerminalTdlsColor
        );

    /// <summary>Raised after <see cref="SetTerminalColors"/> persists; subscribers refresh terminal styling.</summary>
    public event Action? TerminalColorsChanged;

    public string SignatureHelpPlacement => _data.SignatureHelpPlacement;
    public bool AutoExpandSuggestionOnEnter => _data.AutoExpandSuggestionOnEnter;
    public int DataGridFontSize => _data.DataGridFontSize;
    public int RadarDatablockFontSize => _data.RadarDatablockFontSize;
    public int RadarFlyoutFontSize => _data.RadarFlyoutFontSize;
    public int GroundDatablockFontSize => _data.GroundDatablockFontSize;
    public int GroundLabelFontSize => _data.GroundLabelFontSize;

    /// <summary>Raised when any font-size preference changes (debounced via the single Save call).</summary>
    public event Action? FontSizesChanged;

    public void SetSavedServers(IEnumerable<SavedServer> servers, string lastUsedUrl)
    {
        _data.SavedServers = servers.ToList();
        _data.LastUsedServerUrl = lastUsedUrl.Trim();
        Save();
    }

    public void SetAutoAcceptSettings(bool enabled, int delaySeconds)
    {
        _data.AutoAcceptEnabled = enabled;
        _data.AutoAcceptDelaySeconds = Math.Clamp(delaySeconds, 0, 60);
        Save();
    }

    public void SetAutoDeleteOverride(string value)
    {
        _data.AutoDeleteOverride = value;
        Save();
    }

    public void SetCommandScheme(CommandScheme scheme)
    {
        _commandScheme = scheme;
        _data.CommandScheme = ToSaved(scheme);
        Save();
    }

    public void SetVatsimCid(string cid)
    {
        _data.VatsimCid = cid.Trim();
        Save();
    }

    public void SetUserInitials(string initials)
    {
        _data.UserInitials = initials.ToUpperInvariant();
        Save();
    }

    public void SetArtccId(string artccId)
    {
        _data.ArtccId = artccId.ToUpperInvariant().Trim();
        Save();
    }

    public void SetAdminSettings(bool isAdmin, string password)
    {
        _data.IsAdminMode = isAdmin;
        _data.AdminPassword = password;
        Save();
    }

    public void SetTrainingKey(string key)
    {
        _data.TrainingKey = key.Trim();
        Save();
    }

    public void SetWindowGeometry(string windowName, SavedWindowGeometry geometry)
    {
        switch (windowName)
        {
            case "Main":
                _data.MainWindowGeometry = geometry;
                break;
            case "Settings":
                _data.SettingsWindowGeometry = geometry;
                break;
            case "Terminal":
                _data.TerminalWindowGeometry = geometry;
                break;
            case "GroundView":
                _data.GroundViewWindowGeometry = geometry;
                break;
            case "RadarView":
                _data.RadarViewWindowGeometry = geometry;
                break;
            case "DataGrid":
                _data.DataGridWindowGeometry = geometry;
                break;
            default:
                _data.WindowGeometries[windowName] = geometry;
                break;
        }
        Save();
    }

    public void SetPoppedOut(string tabName, bool poppedOut)
    {
        // "Terminal" has inverted semantics: the stored field is IsTerminalDocked (default
        // true). poppedOut == true means !IsTerminalDocked.
        bool current = tabName switch
        {
            "DataGrid" => _data.IsDataGridPoppedOut,
            "GroundView" => _data.IsGroundViewPoppedOut,
            "RadarView" => _data.IsRadarViewPoppedOut,
            "Controllers" => _data.IsControllersPoppedOut,
            "Metar" => _data.IsMetarPoppedOut,
            "VStrips" => _data.IsVStripsPoppedOut,
            "VTdls" => _data.IsVTdlsPoppedOut,
            "Terminal" => !_data.IsTerminalDocked,
            _ => poppedOut,
        };
        if (current == poppedOut)
        {
            return;
        }

        switch (tabName)
        {
            case "DataGrid":
                _data.IsDataGridPoppedOut = poppedOut;
                break;
            case "GroundView":
                _data.IsGroundViewPoppedOut = poppedOut;
                break;
            case "RadarView":
                _data.IsRadarViewPoppedOut = poppedOut;
                break;
            case "Controllers":
                _data.IsControllersPoppedOut = poppedOut;
                break;
            case "Metar":
                _data.IsMetarPoppedOut = poppedOut;
                break;
            case "VStrips":
                _data.IsVStripsPoppedOut = poppedOut;
                break;
            case "VTdls":
                _data.IsVTdlsPoppedOut = poppedOut;
                break;
            case "Terminal":
                _data.IsTerminalDocked = !poppedOut;
                break;
        }
        Save();
    }

    public void SetGridLayout(SavedGridLayout layout)
    {
        _data.GridLayout = layout;
        Save();
    }

    public void SetShowOnlyActiveAircraft(bool value)
    {
        _data.ShowOnlyActiveAircraft = value;
        Save();
    }

    public void SetDataGridAlternatingRowColor(bool value)
    {
        _data.DataGridAlternatingRowColor = value;
        Save();
    }

    public void SetShowTimelineBar(bool value)
    {
        _data.ShowTimelineBar = value;
        Save();
    }

    public void SetLastScenarioFolder(string? folder)
    {
        _data.LastScenarioFolder = folder;
        Save();
    }

    public void SetLastWeatherFolder(string? folder)
    {
        _data.LastWeatherFolder = folder;
        Save();
    }

    public void SetValidateDctFixes(bool validate)
    {
        _data.ValidateDctFixes = validate;
        Save();
    }

    public void SetRpoShowPilotSpeech(bool enabled)
    {
        _data.RpoShowPilotSpeech = enabled;
        Save();
    }

    public void SetSoloTrainingMode(bool enabled)
    {
        _data.SoloTrainingMode = enabled;
        Save();
    }

    public void SetSoloPacingRates(int parkingInitialCallupRatePercent, int arrivalGeneratorRatePercent)
    {
        _data.SoloParkingInitialCallupRatePercent = Math.Clamp(parkingInitialCallupRatePercent, 0, 200);
        _data.SoloArrivalGeneratorRatePercent = Math.Clamp(arrivalGeneratorRatePercent, 0, 100);
        Save();
    }

    /// <summary>
    /// Returns the global default per-approach pilot-go-around probability (0–100).
    /// Used as the seed for the Scenario Setup dialog when no per-scenario override
    /// has been saved for the loading scenario id. Same value also pushed mid-session
    /// via <c>ServerConnection.SetSoloPacingRatesAsync</c> when the operator drags
    /// the runtime slider.
    /// </summary>
    public int GetSoloGoAroundProbability(string? scenarioId)
    {
        if (!string.IsNullOrEmpty(scenarioId) && _data.SoloGoAroundProbabilityByScenario.TryGetValue(scenarioId, out var perScenario))
        {
            return Math.Clamp(perScenario, 0, 100);
        }
        return SoloGoAroundProbabilityPercent;
    }

    public void SetSoloGoAroundProbabilityGlobal(int percent)
    {
        _data.SoloGoAroundProbabilityPercent = Math.Clamp(percent, 0, 100);
        Save();
    }

    public void SetSoloGoAroundProbabilityForScenario(string scenarioId, int percent)
    {
        if (string.IsNullOrEmpty(scenarioId))
        {
            return;
        }
        _data.SoloGoAroundProbabilityByScenario[scenarioId] = Math.Clamp(percent, 0, 100);
        Save();
    }

    public void SetRpoPilotSpeechAudibleAlert(bool enabled)
    {
        _data.RpoPilotSpeechAudibleAlert = enabled;
        Save();
    }

    public void SetPilotVoiceSettings(bool enabled, int volume, bool radioFxEnabled)
    {
        _data.PilotVoiceEnabled = enabled;
        _data.PilotVoiceVolume = Math.Clamp(volume, 0, 100);
        _data.PilotVoiceRadioFxEnabled = radioFxEnabled;
        Save();
    }

    public void SetEuroScopeMode(bool enabled)
    {
        _data.EuroScopeMode = enabled;
        Save();
    }

    public void SetFlashNoLandingClearance(bool enabled)
    {
        _data.FlashNoLandingClearance = enabled;
        Save();
    }

    public void SetShowSpeechBubbles(bool enabled)
    {
        _data.ShowSpeechBubbles = enabled;
        Save();
    }

    public void SetSpeechBubbleDurationMultiplier(double multiplier)
    {
        _data.SpeechBubbleDurationMultiplier = Math.Clamp(multiplier, 0.25, 4.0);
        Save();
    }

    public void SetShowWarningSpeechBubbles(bool enabled)
    {
        _data.ShowWarningSpeechBubbles = enabled;
        Save();
    }

    public void SetAlwaysShowGroundBubblesOnRadar(bool enabled)
    {
        _data.AlwaysShowGroundBubblesOnRadar = enabled;
        Save();
    }

    public void SetSimulationShortcuts(
        bool autoClearedToLandGnd,
        bool autoClearedToLandTwr,
        bool autoClearedToLandApp,
        bool autoClearedToLandCtr,
        bool autoCrossRunway
    )
    {
        _data.AutoClearedToLandGnd = autoClearedToLandGnd;
        _data.AutoClearedToLandTwr = autoClearedToLandTwr;
        _data.AutoClearedToLandApp = autoClearedToLandApp;
        _data.AutoClearedToLandCtr = autoClearedToLandCtr;
        _data.AutoCrossRunway = autoCrossRunway;
        Save();
    }

    public void SetMacros(List<MacroDefinition> macros)
    {
        _macros = macros;
        _data.Macros = macros.Select(m => new SavedMacro { Name = m.Name, Expansion = m.Expansion }).ToList();
        Save();
    }

    public void SetAircraftSelectKey(string key)
    {
        _data.AircraftSelectKey = key;
        Save();
    }

    public void SetFocusInputKey(string key)
    {
        _data.FocusInputKey = key;
        Save();
    }

    public void SetTakeControlKey(string key)
    {
        _data.TakeControlKey = key;
        Save();
    }

    public void SetAlwaysOnTopKey(string key)
    {
        _data.AlwaysOnTopKey = key;
        Save();
    }

    public void SetSpeechSettings(
        bool enabled,
        string whisperModelSize,
        string llmModelPath,
        int llmGpuLayers,
        string pttKey,
        bool autoFocusInputAfterSpeech
    )
    {
        _data.SpeechEnabled = enabled;
        _data.WhisperModelSize = whisperModelSize;
        _data.LlmModelPath = llmModelPath;
        _data.LlmGpuLayers = llmGpuLayers;
        _data.PttKey = pttKey;
        _data.AutoFocusInputAfterSpeech = autoFocusInputAfterSpeech;
        Save();
    }

    public bool SpeechSampleCaptureEnabled => _data.SpeechSampleCaptureEnabled;
    public int SpeechSampleCacheMaxMb => _data.SpeechSampleCacheMaxMb;

    /// <summary>
    /// Persists the opt-in speech-sample capture toggle and its on-disk size cap (in MB). When
    /// capture is on, the speech pipeline writes every push-to-talk recording + pipeline trace
    /// under <c>%LOCALAPPDATA%/yaat/speech-samples/</c>; <see cref="SpeechSampleCacheMaxMb"/>
    /// bounds total disk use via FIFO eviction. Nothing is uploaded automatically — users export
    /// individual samples from the Speech Debug window and attach them to GitHub issues by hand.
    /// </summary>
    public void SetSpeechSampleSettings(bool enabled, int maxMb)
    {
        _data.SpeechSampleCaptureEnabled = enabled;
        _data.SpeechSampleCacheMaxMb = Math.Max(1, maxMb);
        Save();
    }

    /// <summary>
    /// Persists the audio device selection used by both microphone capture (input) and pilot
    /// TTS / notification chime playback (output). Empty string means "use the OS default
    /// device". Both arguments are required so the compiler enforces wiring at every call site.
    /// </summary>
    public void SetAudioSettings(string audioInputDevice, string audioOutputDevice)
    {
        _data.AudioInputDevice = audioInputDevice;
        _data.AudioOutputDevice = audioOutputDevice;
        Save();
    }

    /// <summary>
    /// Targeted setter for the speech-enable toggle. Used by the mic status-bar context menu so
    /// the user can enable/disable without opening the full Settings dialog.
    /// </summary>
    public void SetSpeechEnabled(bool enabled)
    {
        if (_data.SpeechEnabled == enabled)
        {
            return;
        }

        _data.SpeechEnabled = enabled;
        Save();
    }

    public void SetWindowTopmost(string windowName, bool isTopmost)
    {
        var geo = GetWindowGeometry(windowName) ?? new SavedWindowGeometry();
        geo.IsTopmost = isTopmost;
        SetWindowGeometry(windowName, geo);
        WindowTopmostChanged?.Invoke(windowName, isTopmost);
    }

    public void SetAssignmentTint(bool enabled, string color)
    {
        _data.AssignmentTintEnabled = enabled;
        _data.AssignmentTintColor = color;
        Save();
    }

    public void SetUnassignedTint(bool enabled, string color)
    {
        _data.UnassignedTintEnabled = enabled;
        _data.UnassignedTintColor = color;
        Save();
    }

    public void SetSelectedColor(string color)
    {
        _data.SelectedColor = color;
        Save();
    }

    public void SetGroundColors(GroundColorScheme scheme)
    {
        _data.GroundBackgroundColor = scheme.Background;
        _data.GroundTaxiwayColor = scheme.Taxiway;
        _data.GroundTaxiLabelColor = scheme.TaxiLabel;
        _data.GroundRampEdgeColor = scheme.RampEdge;
        _data.GroundHoldShortColor = scheme.HoldShort;
        _data.GroundRunwayFillColor = scheme.RunwayFill;
        _data.GroundRunwayOutlineColor = scheme.RunwayOutline;
        _data.GroundAircraftColor = scheme.Aircraft;
        _data.GroundDatablockTextColor = scheme.DatablockText;
        _data.GroundBrightness = Math.Clamp(scheme.Brightness, 10, 100);
        Save();
    }

    public void SetTerminalColors(TerminalColorScheme scheme)
    {
        _data.TerminalCommandColor = scheme.Command;
        _data.TerminalResponseColor = scheme.Response;
        _data.TerminalSystemColor = scheme.System;
        _data.TerminalSayColor = scheme.Say;
        _data.TerminalPilotSpeechColor = scheme.PilotSpeech;
        _data.TerminalWarningColor = scheme.Warning;
        _data.TerminalErrorColor = scheme.Error;
        _data.TerminalChatColor = scheme.Chat;
        _data.TerminalTdlsColor = scheme.Tdls;
        Save();
        TerminalColorsChanged?.Invoke();
    }

    public void SetSignatureHelpPlacement(string placement)
    {
        _data.SignatureHelpPlacement = placement;
        Save();
    }

    public void SetAutoExpandSuggestionOnEnter(bool value)
    {
        _data.AutoExpandSuggestionOnEnter = value;
        Save();
    }

    public void SetDataGridFontSize(int size)
    {
        _data.DataGridFontSize = Math.Clamp(size, 8, 24);
        Save();
        FontSizesChanged?.Invoke();
    }

    public void SetFontSizes(int radarDatablock, int radarFlyout, int groundDatablock, int groundLabel, int dataGrid)
    {
        _data.RadarDatablockFontSize = Math.Clamp(radarDatablock, 8, 24);
        _data.RadarFlyoutFontSize = Math.Clamp(radarFlyout, 8, 24);
        _data.GroundDatablockFontSize = Math.Clamp(groundDatablock, 8, 24);
        _data.GroundLabelFontSize = Math.Clamp(groundLabel, 8, 24);
        _data.DataGridFontSize = Math.Clamp(dataGrid, 8, 24);
        Save();
        FontSizesChanged?.Invoke();
    }

    public void SetGroundLabelFilters(bool runways, bool taxiways, GroundFilterMode holdShort, GroundFilterMode parking, GroundFilterMode spot)
    {
        _data.GroundShowRunwayLabels = runways;
        _data.GroundShowTaxiwayLabels = taxiways;
        _data.GroundShowHoldShort = (int)holdShort;
        _data.GroundShowParking = (int)parking;
        _data.GroundShowSpot = (int)spot;
        Save();
    }

    public void SetGroundPanZoomLocked(bool locked)
    {
        _data.GroundPanZoomLocked = locked;
        Save();
    }

    public void SetGroundHideDataBlocksByDefault(bool hide)
    {
        _data.GroundHideDataBlocksByDefault = hide;
        Save();
    }

    public void SetGroundLayerSettings(
        bool showSatellite,
        int satelliteBrightness,
        bool showVideoMap,
        int videoMapBrightness,
        bool showYaatLayout,
        int yaatLayoutBrightness
    )
    {
        _data.GroundShowSatelliteImage = showSatellite;
        _data.GroundSatelliteImageBrightness = Math.Clamp(satelliteBrightness, 10, 100);
        _data.GroundShowVideoMapOverlay = showVideoMap;
        _data.GroundVideoMapOverlayBrightness = Math.Clamp(videoMapBrightness, 10, 100);
        _data.GroundShowYaatLayout = showYaatLayout;
        _data.GroundYaatLayoutBrightness = Math.Clamp(yaatLayoutBrightness, 10, 100);
        Save();
    }

    public void SetHiddenTerminalKinds(HashSet<TerminalEntryKind> hidden)
    {
        HiddenTerminalKinds = hidden;
        _data.HiddenTerminalKinds = hidden.Select(k => k.ToString()).ToList();
        Save();
    }

    public void SetFavoriteCommands(List<FavoriteCommand> favorites)
    {
        _data.FavoriteCommands = favorites;
        Save();
    }

    public void SetFavoritePanelColumns(int columns)
    {
        _data.FavoritePanelColumns = Math.Clamp(columns, 1, 20);
        Save();
    }

    public void AddRecentScenario(string filePath, string name, string? apiId = null)
    {
        var key = apiId ?? filePath;
        _data.RecentScenarios.RemoveAll(r => r.Key == key);
        _data.RecentScenarios.Insert(
            0,
            new RecentScenario
            {
                FilePath = filePath,
                Name = name,
                ApiId = apiId,
            }
        );
        if (_data.RecentScenarios.Count > 10)
        {
            _data.RecentScenarios.RemoveRange(10, _data.RecentScenarios.Count - 10);
        }
        Save();
    }

    public void AddRecentWeather(string filePath, string name, string? apiId = null)
    {
        var key = apiId ?? filePath;
        _data.RecentWeatherFiles.RemoveAll(r => r.Key == key);
        _data.RecentWeatherFiles.Insert(
            0,
            new RecentWeather
            {
                FilePath = filePath,
                Name = name,
                ApiId = apiId,
            }
        );
        if (_data.RecentWeatherFiles.Count > 10)
        {
            _data.RecentWeatherFiles.RemoveRange(10, _data.RecentWeatherFiles.Count - 10);
        }
        Save();
    }

    public void ResetGridLayout()
    {
        _data.GridLayout = null;
        Save();
    }

    public SavedRadarSettings? GetRadarSettings(string scenarioId)
    {
        _data.RadarSettings.TryGetValue(scenarioId, out var settings);
        return settings;
    }

    public void SetRadarSettings(string scenarioId, SavedRadarSettings settings)
    {
        _data.RadarSettings[scenarioId] = settings;
        Save();
    }

    public SavedGroundSettings? GetGroundSettings(string scenarioId)
    {
        _data.GroundSettings.TryGetValue(scenarioId, out var settings);
        return settings;
    }

    public void SetGroundSettings(string scenarioId, SavedGroundSettings settings)
    {
        _data.GroundSettings[scenarioId] = settings;
        Save();
    }

    public double? GetGroundRotation(string airportId)
    {
        return _data.GroundRotationByAirport.TryGetValue(airportId, out var r) ? r : null;
    }

    public void SetGroundRotation(string airportId, double rotation)
    {
        _data.GroundRotationByAirport[airportId] = rotation;
        Save();
    }

    /// <summary>
    /// Returns the persisted up-arrow command history for a scenario, newest first.
    /// Empty list when no history has been saved for the given scenario.
    /// </summary>
    public IReadOnlyList<string> GetCommandHistory(string scenarioId)
    {
        return _data.ScenarioCommandHistory.TryGetValue(scenarioId, out var history) ? NormalizeCommandHistoryEntries(history) : [];
    }

    /// <summary>
    /// Replaces the persisted up-arrow command history for a scenario with the given
    /// snapshot. Caller is responsible for ordering (newest first) and trimming
    /// (MainViewModel caps at 50 entries before calling).
    /// </summary>
    public void SetCommandHistory(string scenarioId, IEnumerable<string> entries)
    {
        _data.ScenarioCommandHistory[scenarioId] = NormalizeCommandHistoryEntries(entries);
        Save();
    }

    private static List<string> NormalizeCommandHistoryEntries(IEnumerable<string> entries)
    {
        var normalized = new List<string>();
        foreach (var entry in entries)
        {
            var value = entry.ToUpperInvariant();
            if (normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            normalized.Add(value);
        }

        return normalized;
    }

    public List<(string ScenarioId, string DisplayName)> GetSavedViewScenarioIds()
    {
        var ids = new HashSet<string>();
        foreach (var key in _data.RadarSettings.Keys)
        {
            ids.Add(key);
        }

        foreach (var key in _data.GroundSettings.Keys)
        {
            ids.Add(key);
        }

        var nameMap = new Dictionary<string, string>(_data.ScenarioNames);
        foreach (var recent in _data.RecentScenarios)
        {
            nameMap[recent.Key] = recent.Name;
        }

        var result = new List<(string, string)>();
        foreach (var id in ids)
        {
            var display = nameMap.TryGetValue(id, out var name) ? name : id;
            result.Add((id, display));
        }

        result.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public void SetScenarioName(string scenarioId, string name)
    {
        _data.ScenarioNames[scenarioId] = name;
        Save();
    }

    public string? GetScenarioAirport(string scenarioId)
    {
        return _data.ScenarioAirports.TryGetValue(scenarioId, out var airport) ? airport : null;
    }

    public void SetScenarioAirport(string scenarioId, string airportId)
    {
        if (string.IsNullOrEmpty(scenarioId) || string.IsNullOrEmpty(airportId))
        {
            return;
        }

        if (_data.ScenarioAirports.TryGetValue(scenarioId, out var existing) && existing == airportId)
        {
            return;
        }

        _data.ScenarioAirports[scenarioId] = airportId;
        Save();
    }

    private static SavedPrefs Load()
    {
        string json;
        lock (FileLock)
        {
            if (!File.Exists(ConfigPath))
            {
                return ApplyDefaultServers(new SavedPrefs());
            }

            try
            {
                json = File.ReadAllText(ConfigPath);
            }
            catch (IOException ex)
            {
                Log.LogWarning(ex, "Could not read preferences from {Path}", ConfigPath);
                return ApplyDefaultServers(new SavedPrefs());
            }
        }

        // Fast path: full deserialization
        try
        {
            var saved = JsonSerializer.Deserialize<SavedPrefs>(json, JsonOptions);
            if (saved is not null)
            {
                return ApplyDefaultServers(saved);
            }
        }
        catch (JsonException)
        {
            // Fall through to field-by-field recovery
        }

        // Slow path: recover individual fields so one bad field doesn't wipe everything
        Log.LogWarning("Full preferences deserialization failed — recovering individual fields from {Path}", ConfigPath);
        BackupFile();
        return RecoverFields(json);
    }

    private static SavedPrefs RecoverFields(string json)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json)?.AsObject();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Preferences JSON is completely unparseable");
            return new SavedPrefs();
        }

        if (obj is null)
        {
            return new SavedPrefs();
        }

        var result = new SavedPrefs
        {
            CommandScheme = GetFieldOr<SavedCommandScheme?>(obj, "commandScheme", null),
            SavedServers = GetFieldOr<List<SavedServer>>(obj, "savedServers", []),
            LastUsedServerUrl = GetFieldOr(obj, "lastUsedServerUrl", "http://localhost:5000"),
            VatsimCid = GetFieldOr(obj, "vatsimCid", ""),
            UserInitials = GetFieldOr(obj, "userInitials", ""),
            ArtccId = GetFieldOr(obj, "artccId", ""),
            IsAdminMode = GetFieldOr(obj, "isAdminMode", false),
            AdminPassword = GetFieldOr(obj, "adminPassword", ""),
            MainWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "mainWindowGeometry", null),
            SettingsWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "settingsWindowGeometry", null),
            TerminalWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "terminalWindowGeometry", null),
            GroundViewWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "groundViewWindowGeometry", null),
            RadarViewWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "radarViewWindowGeometry", null),
            DataGridWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "dataGridWindowGeometry", null),
            GridLayout = GetFieldOr<SavedGridLayout?>(obj, "gridLayout", null),
            AutoAcceptEnabled = GetFieldOr(obj, "autoAcceptEnabled", true),
            AutoAcceptDelaySeconds = GetFieldOr(obj, "autoAcceptDelaySeconds", 5),
            AutoDeleteOverride = GetFieldOr(obj, "autoDeleteOverride", ""),
            IsDataGridPoppedOut = GetFieldOr(obj, "isDataGridPoppedOut", false),
            IsGroundViewPoppedOut = GetFieldOr(obj, "isGroundViewPoppedOut", false),
            IsRadarViewPoppedOut = GetFieldOr(obj, "isRadarViewPoppedOut", false),
            IsControllersPoppedOut = GetFieldOr(obj, "isControllersPoppedOut", false),
            IsMetarPoppedOut = GetFieldOr(obj, "isMetarPoppedOut", false),
            IsVStripsPoppedOut = GetFieldOr(obj, "isVStripsPoppedOut", false),
            IsVTdlsPoppedOut = GetFieldOr(obj, "isVTdlsPoppedOut", false),
            IsVTdlsDarkMode = GetFieldOr(obj, "isVTdlsDarkMode", false),
            IsTerminalDocked = GetFieldOr(obj, "isTerminalDocked", true),
            RadarSettings = GetFieldOr<Dictionary<string, SavedRadarSettings>>(obj, "radarSettings", []),
            GroundSettings = GetFieldOr<Dictionary<string, SavedGroundSettings>>(obj, "groundSettings", []),
            WindowGeometries = GetFieldOr<Dictionary<string, SavedWindowGeometry>>(obj, "windowGeometries", []),
            WindowProfiles = GetFieldOr<List<SavedWindowProfile>>(obj, "windowProfiles", []),
            ShowOnlyActiveAircraft = GetFieldOr(obj, "showOnlyActiveAircraft", false),
            ShowTimelineBar = GetFieldOr(obj, "showTimelineBar", false),
            DataGridAlternatingRowColor = GetFieldOr(obj, "dataGridAlternatingRowColor", true),
            LastScenarioFolder = GetFieldOr<string?>(obj, "lastScenarioFolder", null),
            LastWeatherFolder = GetFieldOr<string?>(obj, "lastWeatherFolder", null),
            Macros = GetFieldOr<List<SavedMacro>>(obj, "macros", []),
            ValidateDctFixes = GetFieldOr(obj, "validateDctFixes", false),
            EuroScopeMode = GetFieldOr(obj, "euroScopeMode", false),
            FlashNoLandingClearance = GetFieldOr(obj, "flashNoLandingClearance", true),
            AutoClearedToLandGnd = GetFieldOr(obj, "autoClearedToLandGnd", true),
            AutoClearedToLandTwr = GetFieldOr(obj, "autoClearedToLandTwr", false),
            AutoClearedToLandApp = GetFieldOr(obj, "autoClearedToLandApp", true),
            AutoClearedToLandCtr = GetFieldOr(obj, "autoClearedToLandCtr", true),
            AutoCrossRunway = GetFieldOr(obj, "autoCrossRunway", false),
            SoloTrainingMode = GetFieldOr(obj, "soloTrainingMode", false),
            SoloParkingInitialCallupRatePercent = GetFieldOr(obj, "soloParkingInitialCallupRatePercent", 100),
            SoloArrivalGeneratorRatePercent = GetFieldOr(obj, "soloArrivalGeneratorRatePercent", 100),
            RpoShowPilotSpeech = GetFieldOr(obj, "rpoShowPilotSpeech", false),
            RpoPilotSpeechAudibleAlert = GetFieldOr(obj, "rpoPilotSpeechAudibleAlert", false),
            PilotVoiceEnabled = GetFieldOr(obj, "pilotVoiceEnabled", false),
            PilotVoiceVolume = GetFieldOr(obj, "pilotVoiceVolume", 80),
            PilotVoiceRadioFxEnabled = GetFieldOr(obj, "pilotVoiceRadioFxEnabled", true),
            FavoriteCommands = GetFieldOr<List<FavoriteCommand>>(obj, "favoriteCommands", []),
            FavoritePanelColumns = GetFieldOr(obj, "favoritePanelColumns", 6),
            RecentScenarios = GetFieldOr<List<RecentScenario>>(obj, "recentScenarios", []),
            RecentWeatherFiles = GetFieldOr<List<RecentWeather>>(obj, "recentWeatherFiles", []),
            AircraftSelectKey = GetFieldOr(obj, "aircraftSelectKey", "Add"),
            FocusInputKey = GetFieldOr(obj, "focusInputKey", "OemTilde"),
            TakeControlKey = GetFieldOr(obj, "takeControlKey", "Ctrl+T"),
            AlwaysOnTopKey = GetFieldOr(obj, "alwaysOnTopKey", "Ctrl+Shift+T"),
            HiddenTerminalKinds = GetFieldOr<List<string>>(obj, "hiddenTerminalKinds", []),
            GroundShowRunwayLabels = GetFieldOr(obj, "groundShowRunwayLabels", true),
            GroundShowTaxiwayLabels = GetFieldOr(obj, "groundShowTaxiwayLabels", true),
            GroundShowHoldShort = GetFieldOr(obj, "groundShowHoldShort", 0),
            GroundShowParking = GetFieldOr(obj, "groundShowParking", 0),
            GroundShowSpot = GetFieldOr(obj, "groundShowSpot", 0),
            GroundPanZoomLocked = GetFieldOr(obj, "groundPanZoomLocked", false),
            GroundHideDataBlocksByDefault = GetFieldOr(obj, "groundHideDataBlocksByDefault", false),
            GroundShowSatelliteImage = GetFieldOr(obj, "groundShowSatelliteImage", false),
            GroundSatelliteImageBrightness = GetFieldOr(obj, "groundSatelliteImageBrightness", 50),
            GroundShowVideoMapOverlay = GetFieldOr(obj, "groundShowVideoMapOverlay", false),
            GroundVideoMapOverlayBrightness = GetFieldOr(obj, "groundVideoMapOverlayBrightness", 70),
            GroundShowYaatLayout = GetFieldOr(obj, "groundShowYaatLayout", true),
            GroundYaatLayoutBrightness = GetFieldOr(obj, "groundYaatLayoutBrightness", 100),
            AssignmentTintEnabled = GetFieldOr(obj, "assignmentTintEnabled", false),
            AssignmentTintColor = GetFieldOr(obj, "assignmentTintColor", "#00FF00"),
            UnassignedTintEnabled = GetFieldOr(obj, "unassignedTintEnabled", false),
            UnassignedTintColor = GetFieldOr(obj, "unassignedTintColor", "#888888"),
            SelectedColor = GetFieldOr(obj, "selectedColor", "#FFFFFF"),
            GroundBackgroundColor = GetFieldOr(obj, "groundBackgroundColor", GroundColorScheme.DefaultBackground),
            GroundTaxiwayColor = GetFieldOr(obj, "groundTaxiwayColor", GroundColorScheme.DefaultTaxiway),
            GroundTaxiLabelColor = GetFieldOr(obj, "groundTaxiLabelColor", GroundColorScheme.DefaultTaxiLabel),
            GroundRampEdgeColor = GetFieldOr(obj, "groundRampEdgeColor", GroundColorScheme.DefaultRampEdge),
            GroundHoldShortColor = GetFieldOr(obj, "groundHoldShortColor", GroundColorScheme.DefaultHoldShort),
            GroundRunwayFillColor = GetFieldOr(obj, "groundRunwayFillColor", GroundColorScheme.DefaultRunwayFill),
            GroundRunwayOutlineColor = GetFieldOr(obj, "groundRunwayOutlineColor", GroundColorScheme.DefaultRunwayOutline),
            GroundAircraftColor = GetFieldOr(obj, "groundAircraftColor", GroundColorScheme.DefaultAircraft),
            GroundDatablockTextColor = GetFieldOr(obj, "groundDatablockTextColor", GroundColorScheme.DefaultDatablockText),
            GroundBrightness = GetFieldOr(obj, "groundBrightness", GroundColorScheme.DefaultBrightness),
            TerminalCommandColor = GetFieldOr(obj, "terminalCommandColor", TerminalColorScheme.DefaultCommand),
            TerminalResponseColor = GetFieldOr(obj, "terminalResponseColor", TerminalColorScheme.DefaultResponse),
            TerminalSystemColor = GetFieldOr(obj, "terminalSystemColor", TerminalColorScheme.DefaultSystem),
            TerminalSayColor = GetFieldOr(obj, "terminalSayColor", TerminalColorScheme.DefaultSay),
            TerminalPilotSpeechColor = GetFieldOr(obj, "terminalPilotSpeechColor", TerminalColorScheme.DefaultPilotSpeech),
            TerminalWarningColor = GetFieldOr(obj, "terminalWarningColor", TerminalColorScheme.DefaultWarning),
            TerminalErrorColor = GetFieldOr(obj, "terminalErrorColor", TerminalColorScheme.DefaultError),
            TerminalChatColor = GetFieldOr(obj, "terminalChatColor", TerminalColorScheme.DefaultChat),
            TerminalTdlsColor = GetFieldOr(obj, "terminalTdlsColor", TerminalColorScheme.DefaultTdls),
            SignatureHelpPlacement = GetFieldOr(obj, "signatureHelpPlacement", "Above"),
            AutoExpandSuggestionOnEnter = GetFieldOr(obj, "autoExpandSuggestionOnEnter", true),
            DataGridFontSize = GetFieldOr(obj, "dataGridFontSize", 12),
            RadarDatablockFontSize = GetFieldOr(obj, "radarDatablockFontSize", 12),
            RadarFlyoutFontSize = GetFieldOr(obj, "radarFlyoutFontSize", 12),
            GroundDatablockFontSize = GetFieldOr(obj, "groundDatablockFontSize", 12),
            GroundLabelFontSize = GetFieldOr(obj, "groundLabelFontSize", 13),
            ScenarioNames = GetFieldOr<Dictionary<string, string>>(obj, "scenarioNames", []),
            ScenarioCommandHistory = GetFieldOr<Dictionary<string, List<string>>>(obj, "scenarioCommandHistory", []),
            SoloGoAroundProbabilityPercent = GetFieldOr(obj, "soloGoAroundProbabilityPercent", 0),
            SoloGoAroundProbabilityByScenario = GetFieldOr<Dictionary<string, int>>(obj, "soloGoAroundProbabilityByScenario", []),
        };

        return ApplyDefaultServers(result);
    }

    /// <summary>
    /// Canonical default server list. Used (a) to populate the saved-servers list on
    /// first launch when no preferences file exists, and (b) by the Connect dialog's
    /// "Restore defaults" button so a user who accidentally edits or deletes one of
    /// these can put them back without resetting unrelated preferences.
    /// </summary>
    public static IReadOnlyList<SavedServer> DefaultServers { get; } =
    [new SavedServer("YAAT1", "https://yaat1.leftos.dev"), new SavedServer("Local", "http://localhost:5000")];

    private static SavedPrefs ApplyDefaultServers(SavedPrefs prefs)
    {
        if (prefs.SavedServers is null or { Count: 0 })
        {
            prefs.SavedServers = [.. DefaultServers.Select(s => new SavedServer(s.Name, s.Url))];
        }

        return prefs;
    }

    private static T GetFieldOr<T>(JsonObject obj, string name, T fallback)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            return node.Deserialize<T>(JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            Log.LogWarning("Skipped unreadable preference field '{Field}'", name);
            return fallback;
        }
    }

    private static void BackupFile()
    {
        try
        {
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Could not back up preferences file");
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        // Sync cached conversions back to _data before serializing
        _data.CommandScheme = ToSaved(_commandScheme);
        _data.Macros = _macros.Select(m => new SavedMacro { Name = m.Name, Expansion = m.Expansion }).ToList();

        var json = JsonSerializer.Serialize(_data, JsonOptions);

        lock (FileLock)
        {
            // Atomic write: write to .tmp then move, so a crash mid-write
            // can't corrupt the real file. The lock serializes against
            // concurrent Save and Load calls from other instances.
            var tmpPath = ConfigPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, ConfigPath, overwrite: true);
        }
    }

    private static CommandScheme? FromSaved(SavedCommandScheme s)
    {
        var patterns = new Dictionary<CanonicalCommandType, CommandPattern>();

        var defaults = CommandScheme.Default();
        foreach (var (type, pattern) in defaults.Patterns)
        {
            patterns[type] = new CommandPattern { Aliases = [.. pattern.Aliases] };
        }

        foreach (var (key, sp) in s.Patterns)
        {
            if (!Enum.TryParse<CanonicalCommandType>(key, out var type))
            {
                continue;
            }

            // Read aliases: prefer Aliases list, fall back to legacy Verb field
            var aliases = sp.Aliases is { Count: > 0 } ? sp.Aliases : (!string.IsNullOrWhiteSpace(sp.Verb) ? [sp.Verb] : null);

            if (aliases is null)
            {
                continue;
            }

            if (patterns.TryGetValue(type, out var existing))
            {
                existing.Aliases = aliases;
            }
        }

        return new CommandScheme { Patterns = patterns };
    }

    private static SavedCommandScheme ToSaved(CommandScheme scheme)
    {
        var defaults = CommandScheme.Default();
        var patterns = new Dictionary<string, SavedPattern>();
        foreach (var (type, pattern) in scheme.Patterns)
        {
            // Only persist aliases that differ from defaults — unmodified commands
            // always pick up the latest default aliases on next load.
            if (defaults.Patterns.TryGetValue(type, out var defaultPattern) && pattern.Aliases.SequenceEqual(defaultPattern.Aliases))
            {
                continue;
            }

            patterns[type.ToString()] = new SavedPattern { Aliases = pattern.Aliases };
        }

        return new SavedCommandScheme { Patterns = patterns };
    }

    private sealed class SavedPrefs
    {
        public SavedCommandScheme? CommandScheme { get; set; }
        public List<SavedServer> SavedServers { get; set; } = [];
        public string LastUsedServerUrl { get; set; } = "https://yaat1.leftos.dev";
        public string VatsimCid { get; set; } = "";
        public string UserInitials { get; set; } = "";
        public string ArtccId { get; set; } = "";
        public bool IsAdminMode { get; set; }
        public string AdminPassword { get; set; } = "";
        public string TrainingKey { get; set; } = "";
        public SavedWindowGeometry? MainWindowGeometry { get; set; }
        public SavedWindowGeometry? SettingsWindowGeometry { get; set; }
        public SavedWindowGeometry? TerminalWindowGeometry { get; set; }
        public SavedWindowGeometry? GroundViewWindowGeometry { get; set; }
        public SavedWindowGeometry? RadarViewWindowGeometry { get; set; }
        public SavedWindowGeometry? DataGridWindowGeometry { get; set; }
        public SavedGridLayout? GridLayout { get; set; }
        public bool AutoAcceptEnabled { get; set; } = true;
        public int AutoAcceptDelaySeconds { get; set; } = 5;
        public string AutoDeleteOverride { get; set; } = "";
        public bool IsDataGridPoppedOut { get; set; }
        public bool IsGroundViewPoppedOut { get; set; }
        public bool IsRadarViewPoppedOut { get; set; }
        public bool IsControllersPoppedOut { get; set; }
        public bool IsMetarPoppedOut { get; set; }
        public bool IsVStripsPoppedOut { get; set; }
        public bool IsVTdlsPoppedOut { get; set; }
        public bool IsVTdlsDarkMode { get; set; }
        public bool IsTerminalDocked { get; set; } = true;
        public Dictionary<string, SavedRadarSettings> RadarSettings { get; set; } = [];
        public Dictionary<string, SavedGroundSettings> GroundSettings { get; set; } = [];
        public Dictionary<string, double> GroundRotationByAirport { get; set; } = [];
        public Dictionary<string, SavedWindowGeometry> WindowGeometries { get; set; } = [];
        public List<SavedWindowProfile> WindowProfiles { get; set; } = [];
        public bool ShowOnlyActiveAircraft { get; set; }
        public bool ShowTimelineBar { get; set; }
        public bool DataGridAlternatingRowColor { get; set; } = true;
        public string? LastActiveRoomId { get; set; }
        public string? LastScenarioFolder { get; set; }
        public string? LastWeatherFolder { get; set; }
        public List<SavedMacro> Macros { get; set; } = [];
        public bool ValidateDctFixes { get; set; }
        public bool EuroScopeMode { get; set; }
        public bool FlashNoLandingClearance { get; set; } = true;
        public bool ShowSpeechBubbles { get; set; }
        public double SpeechBubbleDurationMultiplier { get; set; } = 1.0;
        public bool ShowWarningSpeechBubbles { get; set; }
        public bool AlwaysShowGroundBubblesOnRadar { get; set; }
        public bool AutoClearedToLandGnd { get; set; } = true;
        public bool AutoClearedToLandTwr { get; set; }
        public bool AutoClearedToLandApp { get; set; } = true;
        public bool AutoClearedToLandCtr { get; set; } = true;
        public bool AutoCrossRunway { get; set; }
        public bool SoloTrainingMode { get; set; }
        public int SoloParkingInitialCallupRatePercent { get; set; } = 100;
        public int SoloArrivalGeneratorRatePercent { get; set; } = 100;

        // Global default per-approach pilot-go-around probability (0–100). Seeds the
        // Scenario Setup dialog when no per-scenario override is present. Default 0 =
        // no random GAs (existing behavior preserved).
        public int SoloGoAroundProbabilityPercent { get; set; }

        // Per-scenario override of the pilot-go-around probability, keyed by ScenarioId
        // (same key used by ScenarioCommandHistory). Missing key falls back to the
        // global default above.
        public Dictionary<string, int> SoloGoAroundProbabilityByScenario { get; set; } = [];
        public bool RpoShowPilotSpeech { get; set; }
        public bool RpoPilotSpeechAudibleAlert { get; set; }
        public bool PilotVoiceEnabled { get; set; }
        public int PilotVoiceVolume { get; set; } = 80;
        public bool PilotVoiceRadioFxEnabled { get; set; } = true;
        public List<FavoriteCommand> FavoriteCommands { get; set; } = [];
        public int FavoritePanelColumns { get; set; } = 6;
        public List<RecentScenario> RecentScenarios { get; set; } = [];
        public List<RecentWeather> RecentWeatherFiles { get; set; } = [];
        public string AircraftSelectKey { get; set; } = "Add";
        public string FocusInputKey { get; set; } = "OemTilde";
        public string TakeControlKey { get; set; } = "Ctrl+T";
        public string AlwaysOnTopKey { get; set; } = "Ctrl+Shift+T";
        public List<string> HiddenTerminalKinds { get; set; } = [];
        public bool GroundShowRunwayLabels { get; set; } = true;
        public bool GroundShowTaxiwayLabels { get; set; } = true;
        public int GroundShowHoldShort { get; set; }
        public int GroundShowParking { get; set; }
        public int GroundShowSpot { get; set; }
        public bool GroundPanZoomLocked { get; set; }
        public bool GroundHideDataBlocksByDefault { get; set; }
        public bool GroundShowSatelliteImage { get; set; }
        public int GroundSatelliteImageBrightness { get; set; } = 50;
        public bool GroundShowVideoMapOverlay { get; set; }
        public int GroundVideoMapOverlayBrightness { get; set; } = 70;
        public bool GroundShowYaatLayout { get; set; } = true;
        public int GroundYaatLayoutBrightness { get; set; } = 100;
        public bool AssignmentTintEnabled { get; set; }
        public string AssignmentTintColor { get; set; } = "#00FF00";
        public bool UnassignedTintEnabled { get; set; }
        public string UnassignedTintColor { get; set; } = "#888888";
        public string SelectedColor { get; set; } = "#FFFFFF";
        public string GroundBackgroundColor { get; set; } = GroundColorScheme.DefaultBackground;
        public string GroundTaxiwayColor { get; set; } = GroundColorScheme.DefaultTaxiway;
        public string GroundTaxiLabelColor { get; set; } = GroundColorScheme.DefaultTaxiLabel;
        public string GroundRampEdgeColor { get; set; } = GroundColorScheme.DefaultRampEdge;
        public string GroundHoldShortColor { get; set; } = GroundColorScheme.DefaultHoldShort;
        public string GroundRunwayFillColor { get; set; } = GroundColorScheme.DefaultRunwayFill;
        public string GroundRunwayOutlineColor { get; set; } = GroundColorScheme.DefaultRunwayOutline;
        public string GroundAircraftColor { get; set; } = GroundColorScheme.DefaultAircraft;
        public string GroundDatablockTextColor { get; set; } = GroundColorScheme.DefaultDatablockText;
        public int GroundBrightness { get; set; } = GroundColorScheme.DefaultBrightness;
        public string TerminalCommandColor { get; set; } = TerminalColorScheme.DefaultCommand;
        public string TerminalResponseColor { get; set; } = TerminalColorScheme.DefaultResponse;
        public string TerminalSystemColor { get; set; } = TerminalColorScheme.DefaultSystem;
        public string TerminalSayColor { get; set; } = TerminalColorScheme.DefaultSay;
        public string TerminalPilotSpeechColor { get; set; } = TerminalColorScheme.DefaultPilotSpeech;
        public string TerminalWarningColor { get; set; } = TerminalColorScheme.DefaultWarning;
        public string TerminalErrorColor { get; set; } = TerminalColorScheme.DefaultError;
        public string TerminalChatColor { get; set; } = TerminalColorScheme.DefaultChat;
        public string TerminalTdlsColor { get; set; } = TerminalColorScheme.DefaultTdls;
        public string SignatureHelpPlacement { get; set; } = "Above";
        public bool AutoExpandSuggestionOnEnter { get; set; } = true;
        public int DataGridFontSize { get; set; } = 12;
        public int RadarDatablockFontSize { get; set; } = 12;
        public int RadarFlyoutFontSize { get; set; } = 12;
        public int GroundDatablockFontSize { get; set; } = 12;
        public int GroundLabelFontSize { get; set; } = 13;
        public Dictionary<string, string> ScenarioNames { get; set; } = [];

        // Per-scenario primary airport id (e.g. "KOAK"), keyed by ScenarioId. Recorded
        // whenever a scenario is active so the Copy View Settings dialog can label map
        // positions by airport and warn when copying view position across airports.
        public Dictionary<string, string> ScenarioAirports { get; set; } = [];

        // Per-scenario up-arrow recall history. Keyed by ActiveScenarioId; values are
        // ordered newest-first, capped at 50 entries by MainViewModel before save.
        // Discarded for commands typed while no scenario is active.
        public Dictionary<string, List<string>> ScenarioCommandHistory { get; set; } = [];

        // Speech recognition. With the LM-Kit engine swap, WhisperModelSize and LlmModelPath
        // hold LM-Kit model sources — curated IDs (e.g. "whisper-large-turbo3", "qwen3.5:4b"),
        // absolute file paths, or http(s) URIs. The previous values like "base.en" (Whisper.net
        // ggml suffix) and explicit GGUF paths still resolve correctly through the same
        // dispatch logic in WhisperSttEngine.EnsureLoaded / LocalLlmService.EnsureLoaded.
        // LlmGpuLayers: -1 = auto, 0 = CPU only, N = offload N layers.
        public bool SpeechEnabled { get; set; }
        public string WhisperModelSize { get; set; } = "whisper-large-turbo3";
        public string LlmModelPath { get; set; } = "qwen3.5:4b";
        public int LlmGpuLayers { get; set; } = -1;

        // Opt-in speech-sample capture. When true, every push-to-talk session is persisted as
        // {audio.wav + session.json} under %LOCALAPPDATA%/yaat/speech-samples/, FIFO-evicted to
        // stay within SpeechSampleCacheMaxMb. Users review and export samples from the Speech
        // Debug window; nothing is uploaded automatically.
        public bool SpeechSampleCaptureEnabled { get; set; }
        public int SpeechSampleCacheMaxMb { get; set; } = 50;
        public string PttKey { get; set; } = "RightCtrl";
        public string AudioInputDevice { get; set; } = "";
        public string AudioOutputDevice { get; set; } = "";

        // Auto-focus the command input box after a successful PTT/STT transcription so the user
        // can press Enter to send the recognized command without having to mouse to the input.
        // Defaults to true because that's what the Settings checkbox starts at — the default is
        // also the recommended workflow per the user's request.
        public bool AutoFocusInputAfterSpeech { get; set; } = true;
    }

    private sealed class SavedCommandScheme
    {
        public Dictionary<string, SavedPattern> Patterns { get; set; } = [];
    }

    private sealed class SavedPattern
    {
        public List<string>? Aliases { get; set; }
        public string? Verb { get; set; }
    }
}

public sealed class SavedWindowGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
    public int ScreenIndex { get; set; }
    public bool IsTopmost { get; set; }
}

public sealed class SavedGridLayout
{
    public List<string>? ColumnOrder { get; set; }
    public string? SortColumn { get; set; }
    public ListSortDirection? SortDirection { get; set; }
    public Dictionary<string, double>? ColumnWidths { get; set; }
    public List<string>? HiddenColumns { get; set; }
}

/// <summary>
/// A named snapshot of the entire window arrangement (positions, sizes,
/// pop-out / dock state, and DataGrid columns). Restored on demand from the
/// View → Window Profiles menu so the user can switch quickly between layouts
/// tuned for different roles (e.g. GC vs LC).
/// </summary>
public sealed class SavedWindowProfile
{
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }

    /// <summary>
    /// Per-window outer geometry keyed by the same name <see cref="WindowGeometryHelper"/>
    /// uses (e.g. "Main", "GroundView", "VStripsView:KSFO_TWR"). Keys whose windows are
    /// not open at apply time get written into the per-window preferences so the next
    /// time that window opens it picks up the profile's geometry.
    /// </summary>
    public Dictionary<string, SavedWindowGeometry> WindowGeometries { get; set; } = [];

    /// <summary>True when the Terminal is docked inside the main window at capture time.</summary>
    public bool IsTerminalDocked { get; set; } = true;
    public bool IsDataGridPoppedOut { get; set; }
    public bool IsGroundViewPoppedOut { get; set; }
    public bool IsRadarViewPoppedOut { get; set; }
    public bool IsControllersPoppedOut { get; set; }
    public bool IsMetarPoppedOut { get; set; }

    /// <summary>DataGrid column order / widths / sort / hidden columns at capture time.</summary>
    public SavedGridLayout? DataGridLayout { get; set; }
}

public sealed class RecentScenario
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ApiId { get; set; }

    public bool IsApi => ApiId is not null;
    public string Key => ApiId ?? FilePath;
}

public sealed class RecentWeather
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ApiId { get; set; }

    public bool IsApi => ApiId is not null;
    public string Key => ApiId ?? FilePath;
}

public sealed class SavedMacro
{
    public string Name { get; set; } = "";
    public string Expansion { get; set; } = "";
}

public sealed class FavoriteCommand
{
    public bool IsSpacer { get; set; }
    public string Label { get; set; } = "";
    public string CommandText { get; set; } = "";
    public string GroundCommandText { get; set; } = "";
    public string? ScenarioId { get; set; }
    public string? AirportId { get; set; }
    public FavoriteCommandCategory Category { get; set; } = FavoriteCommandCategory.Air;
    public string BackgroundColor { get; set; } = FavoriteCommandDefaults.BackgroundColor;
    public string TextColor { get; set; } = FavoriteCommandDefaults.TextColor;
    public double ButtonWidth { get; set; } = FavoriteCommandDefaults.ButtonWidth;
    public double ButtonHeight { get; set; } = FavoriteCommandDefaults.ButtonHeight;
}

public enum FavoriteCommandCategory
{
    Air,
    Ground,
    Vehicle,
    Airport,
}

public static class FavoriteCommandDefaults
{
    public const string BackgroundColor = "#F3F3EE";
    public const string TextColor = "#111111";
    public const double ButtonWidth = 118;
    public const double ButtonHeight = 32;
}

public sealed class SavedRadarSettings
{
    public List<int> EnabledStarsIds { get; set; } = [];
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double RangeNm { get; set; } = 40;
    public double RangeRingCenterLat { get; set; }
    public double RangeRingCenterLon { get; set; }
    public double RangeRingSizeNm { get; set; } = 5;
    public bool ShowRangeRings { get; set; } = true;
    public bool ShowFixes { get; set; }
    public bool IsPanZoomLocked { get; set; }
    public bool ShowTopDown { get; set; }
    public double PtlLengthMinutes { get; set; }
    public bool PtlOwn { get; set; }
    public bool PtlAll { get; set; }
    public Dictionary<string, int>? BrightnessValues { get; set; }
    public int HistoryCount { get; set; }

    public SavedRadarSettings Clone() =>
        new()
        {
            EnabledStarsIds = [.. EnabledStarsIds],
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            RangeNm = RangeNm,
            RangeRingCenterLat = RangeRingCenterLat,
            RangeRingCenterLon = RangeRingCenterLon,
            RangeRingSizeNm = RangeRingSizeNm,
            ShowRangeRings = ShowRangeRings,
            ShowFixes = ShowFixes,
            IsPanZoomLocked = IsPanZoomLocked,
            ShowTopDown = ShowTopDown,
            PtlLengthMinutes = PtlLengthMinutes,
            PtlOwn = PtlOwn,
            PtlAll = PtlAll,
            BrightnessValues = BrightnessValues is null ? null : new Dictionary<string, int>(BrightnessValues),
            HistoryCount = HistoryCount,
        };
}

public sealed class SavedGroundSettings
{
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double Rotation { get; set; }
    public bool IsPanZoomLocked { get; set; }
    public bool ShowRunwayLabels { get; set; } = true;
    public bool ShowTaxiwayLabels { get; set; } = true;
    public GroundFilterMode ShowHoldShort { get; set; }
    public GroundFilterMode ShowParking { get; set; }
    public GroundFilterMode ShowSpot { get; set; }

    public SavedGroundSettings Clone() =>
        new()
        {
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            Zoom = Zoom,
            Rotation = Rotation,
            IsPanZoomLocked = IsPanZoomLocked,
            ShowRunwayLabels = ShowRunwayLabels,
            ShowTaxiwayLabels = ShowTaxiwayLabels,
            ShowHoldShort = ShowHoldShort,
            ShowParking = ShowParking,
            ShowSpot = ShowSpot,
        };
}
