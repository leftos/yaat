using System.Collections.ObjectModel;
using Avalonia.Collections;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views;
using Yaat.Sim.Commands;

namespace Yaat.Client.ViewModels;

public partial class VerbMappingRow : ObservableObject
{
    public required CanonicalCommandType CommandType { get; init; }
    public required string CommandName { get; init; }
    public required string Category { get; init; }
    public required ArgMode ArgMode { get; init; }
    public required string SampleArg { get; init; }

    [ObservableProperty]
    private string _aliases = "";

    [ObservableProperty]
    private string _example = "";

    public List<string> AliasesList => Aliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    partial void OnAliasesChanged(string value)
    {
        Example = BuildExample();
    }

    private string BuildExample()
    {
        var primary = AliasesList.Count > 0 ? AliasesList[0] : "";
        return ArgMode switch
        {
            ArgMode.Required => $"{primary} {SampleArg}",
            ArgMode.Optional => $"{primary} [{SampleArg}]",
            _ => primary,
        };
    }
}

public partial class MacroRow : ObservableObject
{
    public Action<MacroRow>? RemoveAction { get; set; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _expansion = "";

    [ObservableProperty]
    private string _preview = "";

    [RelayCommand]
    private void Remove()
    {
        RemoveAction?.Invoke(this);
    }

    partial void OnNameChanged(string value)
    {
        UpdatePreview();
    }

    partial void OnExpansionChanged(string value)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Expansion))
        {
            Preview = "";
            return;
        }

        var def = new MacroDefinition { Name = Name, Expansion = Expansion };
        var baseName = def.BaseName;
        var validationError = def.Validate();
        var paramNames = def.ParameterNames;
        var paramHint = paramNames.Count > 0 ? " " + string.Join(" ", paramNames.Select(n => $"&{n}")) : "";
        var warning = validationError is not null ? $" ⚠ {validationError}" : "";
        Preview = $"!{baseName}{paramHint} → {Expansion}{warning}";
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserPreferences _preferences;

    /// <summary>
    /// Fired when any visual/display property changes (colors, brightness, tints, font size).
    /// Subscribers can apply the changes live for preview.
    /// </summary>
    public event Action? VisualSettingsChanged;

    private static readonly IReadOnlyList<CommandDefinition> DisplayCommands = CommandRegistry
        .All.Values.Where(c => c.Type != CanonicalCommandType.DirectTo)
        .ToArray();

    [ObservableProperty]
    private string _vatsimCid = "";

    [ObservableProperty]
    private string _userInitials = "";

    [ObservableProperty]
    private string _artccId = "";

    [ObservableProperty]
    private string _testCommandInput = "";

    [ObservableProperty]
    private string _testCommandResult = "";

    [ObservableProperty]
    private bool _testCommandIsError;

    [ObservableProperty]
    private bool _isAdminMode;

    [ObservableProperty]
    private string _adminPassword = "";

    [ObservableProperty]
    private bool _autoAcceptEnabled;

    [ObservableProperty]
    private int _autoAcceptDelaySeconds;

    [ObservableProperty]
    private int _selectedAutoDeleteIndex;

    [ObservableProperty]
    private bool _validateDctFixes;

    [ObservableProperty]
    private bool _autoClearedToLandGnd;

    [ObservableProperty]
    private bool _autoClearedToLandTwr;

    [ObservableProperty]
    private bool _autoClearedToLandApp;

    [ObservableProperty]
    private bool _autoClearedToLandCtr;

    [ObservableProperty]
    private bool _autoCrossRunway;

    [ObservableProperty]
    private string _aircraftSelectKeyDisplay = "Numpad +";

    [ObservableProperty]
    private string _focusInputKeyDisplay = "~";

    [ObservableProperty]
    private string _takeControlKeyDisplay = "Ctrl + T";

    [ObservableProperty]
    private string _alwaysOnTopKeyDisplay = "Ctrl + Shift + T";

    [ObservableProperty]
    private bool _mainWindowTopmost;

    [ObservableProperty]
    private bool _groundViewTopmost;

    [ObservableProperty]
    private bool _radarViewTopmost;

    [ObservableProperty]
    private bool _dataGridTopmost;

    [ObservableProperty]
    private bool _terminalTopmost;

    [ObservableProperty]
    private bool _assignmentTintEnabled;

    [ObservableProperty]
    private string _assignmentTintColor = "#00FF00";

    [ObservableProperty]
    private bool _unassignedTintEnabled;

    [ObservableProperty]
    private string _unassignedTintColor = "#888888";

    [ObservableProperty]
    private string _selectedColor = "#FFFFFF";

    // Ground view colors
    [ObservableProperty]
    private string _groundBackgroundColor = GroundColorScheme.DefaultBackground;

    [ObservableProperty]
    private string _groundTaxiwayColor = GroundColorScheme.DefaultTaxiway;

    [ObservableProperty]
    private string _groundTaxiLabelColor = GroundColorScheme.DefaultTaxiLabel;

    [ObservableProperty]
    private string _groundRampEdgeColor = GroundColorScheme.DefaultRampEdge;

    [ObservableProperty]
    private string _groundHoldShortColor = GroundColorScheme.DefaultHoldShort;

    [ObservableProperty]
    private string _groundRunwayFillColor = GroundColorScheme.DefaultRunwayFill;

    [ObservableProperty]
    private string _groundRunwayOutlineColor = GroundColorScheme.DefaultRunwayOutline;

    [ObservableProperty]
    private string _groundAircraftColor = GroundColorScheme.DefaultAircraft;

    [ObservableProperty]
    private string _groundDatablockTextColor = GroundColorScheme.DefaultDatablockText;

    [ObservableProperty]
    private int _groundBrightness = GroundColorScheme.DefaultBrightness;

    [ObservableProperty]
    private int _groundSatelliteImageBrightness = 50;

    [ObservableProperty]
    private int _groundVideoMapOverlayBrightness = 70;

    [ObservableProperty]
    private int _groundYaatLayoutBrightness = 100;

    [ObservableProperty]
    private int _selectedSignatureHelpPlacementIndex;

    [ObservableProperty]
    private int _dataGridFontSize;

    [ObservableProperty]
    private bool _groundHideDataBlocksByDefault;

    [ObservableProperty]
    private bool _isCapturingKey;

    // Speech recognition settings
    [ObservableProperty]
    private bool _speechEnabled;

    [ObservableProperty]
    private string _whisperModelSize = "whisper-large-turbo3";

    [ObservableProperty]
    private string _whisperModelStatus = "Unknown";

    [ObservableProperty]
    private double _whisperDownloadProgress;

    [ObservableProperty]
    private bool _whisperIsDownloading;

    [ObservableProperty]
    private string _llmModelPath = "";

    [ObservableProperty]
    private LlmCatalogEntry? _selectedLlmModel;

    /// <summary>
    /// Selected LM-Kit Whisper model. Bound to the ItemsSource <see cref="WhisperLmKitModels"/>.
    /// On change, the entry's <see cref="LmKitModelEntry.ModelId"/> is written to
    /// <see cref="WhisperModelSize"/> (which is what <see cref="WhisperSttEngine"/> actually
    /// reads at PTT time). Initialized from the current preference via <see cref="LmKitModelCatalog.FindById"/>;
    /// stays null when the user has configured a custom path / URI not in the catalog.
    /// </summary>
    [ObservableProperty]
    private LmKitModelEntry? _selectedWhisperLmKitModel;

    /// <summary>
    /// Selected LM-Kit LLM model. Bound to the ItemsSource <see cref="LlmLmKitModels"/>. On
    /// change, the entry's <see cref="LmKitModelEntry.ModelId"/> is written to
    /// <see cref="LlmModelPath"/>. Stays null when the user has configured a custom GGUF path
    /// (file or URI) not in the catalog — that's a valid state, not an error.
    /// </summary>
    [ObservableProperty]
    private LmKitModelEntry? _selectedLlmLmKitModel;

    [ObservableProperty]
    private string _llmModelStatus = "Not downloaded";

    [ObservableProperty]
    private double _llmDownloadProgress;

    [ObservableProperty]
    private bool _llmIsDownloading;

    [ObservableProperty]
    private int _llmGpuLayers = -1;

    [ObservableProperty]
    private string _preferredGpuBackend = "Auto";

    [ObservableProperty]
    private string _detectedGpuSummary = "Detecting...";

    [ObservableProperty]
    private string _llamaVulkanRuntimeStatus = "Not installed";

    [ObservableProperty]
    private double _llamaVulkanDownloadProgress;

    [ObservableProperty]
    private bool _llamaVulkanIsDownloading;

    [ObservableProperty]
    private string _llamaCudaRuntimeStatus = "Not installed";

    [ObservableProperty]
    private double _llamaCudaDownloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLlamaCudaDownloadEnabled))]
    private bool _llamaCudaIsDownloading;

    [ObservableProperty]
    private string _whisperVulkanRuntimeStatus = "Not installed";

    [ObservableProperty]
    private double _whisperVulkanDownloadProgress;

    [ObservableProperty]
    private bool _whisperVulkanIsDownloading;

    [ObservableProperty]
    private string _whisperCudaRuntimeStatus = "Not installed";

    [ObservableProperty]
    private double _whisperCudaDownloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWhisperCudaDownloadEnabled))]
    private bool _whisperCudaIsDownloading;

    [ObservableProperty]
    private string _cudaToolkitStatus = "Not detected";

    // CUDA downloads are only enabled when the CUDA Toolkit 12.x has been detected on disk,
    // because without it the backend DLLs fail to load at runtime (missing cudart64_12.dll etc.).
    public bool IsLlamaCudaDownloadEnabled => _cudaToolkitDetected && !LlamaCudaIsDownloading;

    public bool IsWhisperCudaDownloadEnabled => _cudaToolkitDetected && !WhisperCudaIsDownloading;

    private bool _cudaToolkitDetected;

    [ObservableProperty]
    private string _pttKeyDisplay = "Right Ctrl";

    [ObservableProperty]
    private string _audioInputDevice = "";

    /// <summary>
    /// ComboBox-bound property: <see cref="DefaultAudioDeviceLabel"/> when <see cref="AudioInputDevice"/>
    /// is empty (system default), otherwise the device name. Two-way wrapper so we don't need
    /// Avalonia converters in the axaml.
    /// </summary>
    public string SelectedAudioInputDeviceDisplay
    {
        get => string.IsNullOrEmpty(AudioInputDevice) ? DefaultAudioDeviceLabel : AudioInputDevice;
        set => AudioInputDevice = string.Equals(value, DefaultAudioDeviceLabel, StringComparison.Ordinal) ? string.Empty : value;
    }

    partial void OnAudioInputDeviceChanged(string value)
    {
        // Keep the ComboBox in sync when AudioInputDevice changes from other code paths (e.g.
        // the ctor loading the saved value from prefs).
        OnPropertyChanged(nameof(SelectedAudioInputDeviceDisplay));
    }

    private string _aircraftSelectKeyName = "Add";
    private string _focusInputKeyName = "OemTilde";
    private string _takeControlKeyName = "Ctrl+T";
    private string _alwaysOnTopKeyName = "Ctrl+Shift+T";
    private string _pttKeyName = "RightCtrl";
    private string? _captureTarget;
    private readonly ModelManager _modelManager = new();
    private readonly GpuRuntimeDownloader _gpuDownloader = new();
    private CancellationTokenSource? _whisperDownloadCts;
    private CancellationTokenSource? _llmDownloadCts;
    private CancellationTokenSource? _llamaVulkanDownloadCts;
    private CancellationTokenSource? _llamaCudaDownloadCts;
    private CancellationTokenSource? _whisperVulkanDownloadCts;
    private CancellationTokenSource? _whisperCudaDownloadCts;

    public IReadOnlyList<string> WhisperSizes { get; } = ModelManager.AvailableWhisperSizes;
    public IReadOnlyList<LlmCatalogEntry> LlmModels { get; } = ModelManager.AvailableLlmModels;

    /// <summary>LM-Kit STT model catalog exposed as the bound ItemsSource for the Whisper picker.</summary>
    public IReadOnlyList<LmKitModelEntry> WhisperLmKitModels { get; } = LmKitModelCatalog.WhisperModels;

    /// <summary>LM-Kit LLM model catalog exposed as the bound ItemsSource for the command-mapper picker.</summary>
    public IReadOnlyList<LmKitModelEntry> LlmLmKitModels { get; } = LmKitModelCatalog.LlmModels;

    /// <summary>
    /// Snapshot of GPUs LM-Kit can see at the time the Settings window opened. Used by the
    /// Acceleration panel to tell the user whether the heavy <see cref="LmKitModelTier.Best"/>
    /// models will actually accelerate. Computed once at construction (see <see cref="LmKitGpuDetector.Detect"/>);
    /// re-detection on every window open feels right but isn't necessary because GPU presence
    /// rarely changes between application sessions.
    /// </summary>
    public LmKitGpuSnapshot LmKitGpuSnapshot { get; } = LmKitGpuDetector.Detect();

    /// <summary>
    /// Available audio input device names. First entry is always the <see cref="DefaultAudioDeviceLabel"/>
    /// sentinel which maps to an empty <see cref="UserPreferences.AudioInputDevice"/> (meaning "use
    /// system default"). Enumerated once at Settings open via <see cref="AudioCaptureService.ListInputDevices"/>;
    /// when the service is null (designer / standalone open with no MainViewModel) the list contains
    /// only the default entry.
    /// </summary>
    public IReadOnlyList<string> AudioInputDevices { get; }

    public const string DefaultAudioDeviceLabel = "(System default)";

    public static IReadOnlyList<string> AutoDeleteOptions { get; } = ["Use Scenario Setting", "Never", "On Landing", "On Parking"];
    public static IReadOnlyList<string> SignatureHelpPlacementOptions { get; } = ["Above", "Below"];

    public ObservableCollection<VerbMappingRow> VerbMappings { get; } = [];
    public DataGridCollectionView GroupedVerbMappings { get; }
    public ObservableCollection<MacroRow> MacroRows { get; } = [];

    public SettingsViewModel()
        : this(new UserPreferences(), audioCapture: null) { }

    public SettingsViewModel(UserPreferences preferences)
        : this(preferences, audioCapture: null) { }

    public SettingsViewModel(UserPreferences preferences, AudioCaptureService? audioCapture)
    {
        _preferences = preferences;

        // Enumerate available audio input devices if we have an AudioCaptureService instance
        // (passed in by MainWindow when opening Settings). Always include the system-default
        // sentinel as the first entry so the user can go back to "use whatever the OS decides".
        var devices = new List<string> { DefaultAudioDeviceLabel };
        if (audioCapture is not null)
        {
            foreach (var (_, name) in audioCapture.ListInputDevices())
            {
                if (!devices.Contains(name, StringComparer.Ordinal))
                {
                    devices.Add(name);
                }
            }
        }

        AudioInputDevices = devices;
        GroupedVerbMappings = new DataGridCollectionView(VerbMappings);
        GroupedVerbMappings.GroupDescriptions.Add(new DataGridPathGroupDescription("Category"));
        LoadFromScheme(_preferences.CommandScheme);
        _vatsimCid = _preferences.VatsimCid;
        _userInitials = _preferences.UserInitials;
        _artccId = _preferences.ArtccId;
        _isAdminMode = _preferences.IsAdminMode;
        _adminPassword = _preferences.AdminPassword;
        _autoAcceptEnabled = _preferences.AutoAcceptEnabled;
        _autoAcceptDelaySeconds = _preferences.AutoAcceptDelaySeconds;
        _selectedAutoDeleteIndex = AutoDeleteOverrideToIndex(_preferences.AutoDeleteOverride);
        _validateDctFixes = _preferences.ValidateDctFixes;
        _autoClearedToLandGnd = _preferences.AutoClearedToLandGnd;
        _autoClearedToLandTwr = _preferences.AutoClearedToLandTwr;
        _autoClearedToLandApp = _preferences.AutoClearedToLandApp;
        _autoClearedToLandCtr = _preferences.AutoClearedToLandCtr;
        _autoCrossRunway = _preferences.AutoCrossRunway;
        _aircraftSelectKeyName = _preferences.AircraftSelectKey;
        _aircraftSelectKeyDisplay = KeyComboToDisplay(_aircraftSelectKeyName);
        _focusInputKeyName = _preferences.FocusInputKey;
        _focusInputKeyDisplay = KeyComboToDisplay(_focusInputKeyName);
        _takeControlKeyName = _preferences.TakeControlKey;
        _takeControlKeyDisplay = KeyComboToDisplay(_takeControlKeyName);
        _alwaysOnTopKeyName = _preferences.AlwaysOnTopKey;
        _alwaysOnTopKeyDisplay = KeyComboToDisplay(_alwaysOnTopKeyName);
        _speechEnabled = _preferences.SpeechEnabled;
        _whisperModelSize = _preferences.WhisperModelSize;
        _llmModelPath = _preferences.LlmModelPath;
        _llmGpuLayers = _preferences.LlmGpuLayers;
        _preferredGpuBackend = _preferences.PreferredGpuBackend;

        // Resolve LM-Kit catalog selections from the saved preferences. FindById returns null when
        // the user has typed a custom file path or URI not in the catalog — we leave the dropdown
        // unselected in that case rather than silently overriding their choice.
        _selectedWhisperLmKitModel = LmKitModelCatalog.FindById(LmKitModelCatalog.WhisperModels, _whisperModelSize);
        _selectedLlmLmKitModel = LmKitModelCatalog.FindById(LmKitModelCatalog.LlmModels, _llmModelPath);
        _pttKeyName = _preferences.PttKey;
        _pttKeyDisplay = KeyComboToDisplay(_pttKeyName);
        _audioInputDevice = _preferences.AudioInputDevice;
        _whisperModelStatus = FormatWhisperStatus(
            _modelManager.GetWhisperStatus(_whisperModelSize),
            _modelManager.GetWhisperFileSize(_whisperModelSize)
        );

        // Resolve the initial LLM catalog selection from the stored path: if the stored path
        // matches a catalog filename, pick that entry so the dropdown reflects it; otherwise
        // default to the recommended (middle) option. A user who Browsed to a custom GGUF keeps
        // their LlmModelPath intact; the dropdown still points somewhere sensible in case they
        // click Download.
        var byPath = _modelManager.FindLlmEntryByPath(_llmModelPath);
        _selectedLlmModel =
            byPath
            ?? ModelManager.AvailableLlmModels.FirstOrDefault(m => m.Id == "qwen2.5-1.5b-q4km")
            ?? ModelManager.AvailableLlmModels.FirstOrDefault();
        RefreshLlmModelStatus();
        _llamaVulkanRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetLlamaVulkanStatus());
        _llamaCudaRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetLlamaCudaStatus());
        _whisperVulkanRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetWhisperVulkanStatus());
        _whisperCudaRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetWhisperCudaStatus());

        var toolkit = GpuRuntimeDownloader.FindCuda12Toolkit();
        if (toolkit is not null)
        {
            _cudaToolkitDetected = true;
            _cudaToolkitStatus = $"v12.{toolkit.MinorVersion} at {toolkit.InstallPath}";
        }
        else
        {
            _cudaToolkitStatus = "Not detected (install CUDA Toolkit 12.x to enable CUDA downloads)";
        }

        _detectedGpuSummary = GpuCapabilityDetector.Detect().Summary;
        _mainWindowTopmost = _preferences.MainWindowGeometry?.IsTopmost ?? false;
        _groundViewTopmost = _preferences.GroundViewWindowGeometry?.IsTopmost ?? false;
        _radarViewTopmost = _preferences.RadarViewWindowGeometry?.IsTopmost ?? false;
        _dataGridTopmost = _preferences.DataGridWindowGeometry?.IsTopmost ?? false;
        _terminalTopmost = _preferences.TerminalWindowGeometry?.IsTopmost ?? false;
        _assignmentTintEnabled = _preferences.AssignmentTintEnabled;
        _assignmentTintColor = _preferences.AssignmentTintColor;
        _unassignedTintEnabled = _preferences.UnassignedTintEnabled;
        _unassignedTintColor = _preferences.UnassignedTintColor;
        _selectedColor = _preferences.SelectedColor;
        var groundColors = _preferences.GroundColors;
        _groundBackgroundColor = groundColors.Background;
        _groundTaxiwayColor = groundColors.Taxiway;
        _groundTaxiLabelColor = groundColors.TaxiLabel;
        _groundRampEdgeColor = groundColors.RampEdge;
        _groundHoldShortColor = groundColors.HoldShort;
        _groundRunwayFillColor = groundColors.RunwayFill;
        _groundRunwayOutlineColor = groundColors.RunwayOutline;
        _groundAircraftColor = groundColors.Aircraft;
        _groundDatablockTextColor = groundColors.DatablockText;
        _groundBrightness = groundColors.Brightness;
        _groundSatelliteImageBrightness = _preferences.GroundSatelliteImageBrightness;
        _groundVideoMapOverlayBrightness = _preferences.GroundVideoMapOverlayBrightness;
        _groundYaatLayoutBrightness = _preferences.GroundYaatLayoutBrightness;
        _selectedSignatureHelpPlacementIndex = _preferences.SignatureHelpPlacement == "Below" ? 1 : 0;
        _dataGridFontSize = _preferences.DataGridFontSize;
        _groundHideDataBlocksByDefault = _preferences.GroundHideDataBlocksByDefault;
        LoadMacros();
    }

    partial void OnUserInitialsChanged(string value)
    {
        var upper = value.ToUpperInvariant();
        if (upper != value)
        {
            UserInitials = upper;
        }
    }

    partial void OnArtccIdChanged(string value)
    {
        var upper = value.ToUpperInvariant();
        if (upper != value)
        {
            ArtccId = upper;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var scheme = BuildSchemeFromRows();
        _preferences.SetCommandScheme(scheme);
        _preferences.SetVatsimCid(VatsimCid);
        _preferences.SetUserInitials(UserInitials);
        _preferences.SetArtccId(ArtccId);
        _preferences.SetAdminSettings(IsAdminMode, AdminPassword);
        _preferences.SetAutoAcceptSettings(AutoAcceptEnabled, AutoAcceptDelaySeconds);
        _preferences.SetAutoDeleteOverride(IndexToAutoDeleteOverride(SelectedAutoDeleteIndex));
        _preferences.SetValidateDctFixes(ValidateDctFixes);
        _preferences.SetSimulationShortcuts(AutoClearedToLandGnd, AutoClearedToLandTwr, AutoClearedToLandApp, AutoClearedToLandCtr, AutoCrossRunway);
        _preferences.SetAircraftSelectKey(_aircraftSelectKeyName);
        _preferences.SetFocusInputKey(_focusInputKeyName);
        _preferences.SetTakeControlKey(_takeControlKeyName);
        _preferences.SetAlwaysOnTopKey(_alwaysOnTopKeyName);
        _preferences.SetSpeechSettings(
            SpeechEnabled,
            WhisperModelSize,
            LlmModelPath,
            LlmGpuLayers,
            PreferredGpuBackend,
            _pttKeyName,
            AudioInputDevice
        );
        _preferences.SetWindowTopmost("Main", MainWindowTopmost);
        _preferences.SetWindowTopmost("GroundView", GroundViewTopmost);
        _preferences.SetWindowTopmost("RadarView", RadarViewTopmost);
        _preferences.SetWindowTopmost("DataGrid", DataGridTopmost);
        _preferences.SetWindowTopmost("Terminal", TerminalTopmost);
        _preferences.SetAssignmentTint(AssignmentTintEnabled, AssignmentTintColor);
        _preferences.SetUnassignedTint(UnassignedTintEnabled, UnassignedTintColor);
        _preferences.SetSelectedColor(SelectedColor);
        _preferences.SetGroundColors(
            new GroundColorScheme(
                GroundBackgroundColor,
                GroundTaxiwayColor,
                GroundTaxiLabelColor,
                GroundRampEdgeColor,
                GroundHoldShortColor,
                GroundRunwayFillColor,
                GroundRunwayOutlineColor,
                GroundAircraftColor,
                GroundDatablockTextColor,
                GroundBrightness
            )
        );
        _preferences.SetGroundLayerSettings(
            _preferences.GroundShowSatelliteImage,
            GroundSatelliteImageBrightness,
            _preferences.GroundShowVideoMapOverlay,
            GroundVideoMapOverlayBrightness,
            _preferences.GroundShowYaatLayout,
            GroundYaatLayoutBrightness
        );
        _preferences.SetSignatureHelpPlacement(SelectedSignatureHelpPlacementIndex == 1 ? "Below" : "Above");
        _preferences.SetDataGridFontSize(DataGridFontSize);
        _preferences.SetGroundHideDataBlocksByDefault(GroundHideDataBlocksByDefault);
        SaveMacros();
        Saved = true;
    }

    public bool Saved { get; private set; }

    [RelayCommand]
    private void ResetCommandsToDefaults()
    {
        LoadFromScheme(CommandScheme.Default());

        // Re-run the test input against the reset scheme
        OnTestCommandInputChanged(TestCommandInput);
    }

    private void LoadFromScheme(CommandScheme scheme)
    {
        VerbMappings.Clear();

        foreach (var def in DisplayCommands)
        {
            if (!scheme.Patterns.TryGetValue(def.Type, out var pattern))
            {
                continue;
            }

            var row = new VerbMappingRow
            {
                CommandType = def.Type,
                CommandName = def.Label,
                Category = def.Category,
                ArgMode = def.ArgMode,
                SampleArg = def.SampleArg,
                Aliases = string.Join(", ", pattern.Aliases),
                Example = BuildExample(def, pattern),
            };

            VerbMappings.Add(row);
        }
    }

    private CommandScheme BuildSchemeFromRows()
    {
        var baseScheme = CommandScheme.Default();

        var patterns = new Dictionary<CanonicalCommandType, CommandPattern>();

        foreach (var (type, pattern) in baseScheme.Patterns)
        {
            patterns[type] = new CommandPattern { Aliases = [.. pattern.Aliases] };
        }

        // Override aliases from edited rows
        foreach (var row in VerbMappings)
        {
            var aliases = row.AliasesList;
            if (aliases.Count > 0 && patterns.TryGetValue(row.CommandType, out var existing))
            {
                existing.Aliases = aliases;
            }
        }

        return new CommandScheme { Patterns = patterns };
    }

    private static string BuildExample(CommandDefinition def, CommandPattern pattern)
    {
        var primary = pattern.PrimaryVerb;
        return def.ArgMode switch
        {
            ArgMode.Required => $"{primary} {def.SampleArg}",
            ArgMode.Optional => $"{primary} [{def.SampleArg}]",
            _ => primary,
        };
    }

    [RelayCommand]
    private void AddMacro()
    {
        MacroRows.Add(new MacroRow { RemoveAction = r => MacroRows.Remove(r) });
    }

    [RelayCommand]
    private void ClearAllMacros()
    {
        MacroRows.Clear();
    }

    public void ImportMacros(MacroImportResult result)
    {
        var existingBaseNames = new HashSet<string>(MacroRows.Select(r => MacroDefinition.ExtractBaseName(r.Name)), StringComparer.OrdinalIgnoreCase);

        // Add non-conflicting macros
        foreach (var m in result.NewMacros)
        {
            var baseName = MacroDefinition.ExtractBaseName(m.Name);
            if (!existingBaseNames.Contains(baseName))
            {
                MacroRows.Add(
                    new MacroRow
                    {
                        Name = m.Name,
                        Expansion = m.Expansion,
                        RemoveAction = r => MacroRows.Remove(r),
                    }
                );
                existingBaseNames.Add(baseName);
            }
        }

        // Apply conflict resolutions
        foreach (var conflict in result.Conflicts)
        {
            switch (conflict.Resolution)
            {
                case ConflictResolution.Overwrite:
                {
                    var importBaseName = MacroDefinition.ExtractBaseName(conflict.Macro.Name);
                    var existing = MacroRows.First(r =>
                        string.Equals(MacroDefinition.ExtractBaseName(r.Name), importBaseName, StringComparison.OrdinalIgnoreCase)
                    );
                    existing.Name = conflict.Macro.Name;
                    existing.Expansion = conflict.Macro.Expansion;
                    break;
                }

                case ConflictResolution.Skip:
                    break;

                case ConflictResolution.Rename:
                {
                    var renamedName = conflict.RenamedName!;
                    // Preserve parameter declarations from original name if the rename is just a base name
                    var originalTokens = conflict.Macro.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (originalTokens.Length > 1 && !renamedName.Contains(' '))
                    {
                        renamedName = renamedName + " " + string.Join(" ", originalTokens.Skip(1));
                    }

                    MacroRows.Add(
                        new MacroRow
                        {
                            Name = renamedName,
                            Expansion = conflict.Macro.Expansion,
                            RemoveAction = r => MacroRows.Remove(r),
                        }
                    );
                    break;
                }
            }
        }
    }

    public List<SavedMacro> ExportMacros(IEnumerable<MacroRow>? rows = null)
    {
        return (rows ?? MacroRows)
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
            .Select(r => new SavedMacro { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() })
            .ToList();
    }

    private void LoadMacros()
    {
        MacroRows.Clear();
        foreach (var m in _preferences.Macros)
        {
            MacroRows.Add(
                new MacroRow
                {
                    Name = m.Name,
                    Expansion = m.Expansion,
                    RemoveAction = r => MacroRows.Remove(r),
                }
            );
        }
    }

    private void SaveMacros()
    {
        var macros = MacroRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
            .Select(r => new MacroDefinition { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() })
            .ToList();
        _preferences.SetMacros(macros);
    }

    partial void OnTestCommandInputChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            TestCommandResult = "";
            TestCommandIsError = false;
            return;
        }

        var scheme = BuildSchemeFromRows();

        // Expand macros before parsing
        var testInput = value;
        var macros = MacroRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
            .Select(r => new MacroDefinition { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() })
            .ToList();
        var expanded = MacroExpander.TryExpand(testInput, macros, out var macroError);
        if (macroError is not null)
        {
            TestCommandResult = macroError;
            TestCommandIsError = true;
            return;
        }
        if (expanded is not null)
        {
            testInput = expanded;
        }

        var compoundResult = CommandSchemeParser.ParseCompound(testInput, scheme);

        if (compoundResult is null)
        {
            TestCommandResult = "Unrecognized command";
            TestCommandIsError = true;
            return;
        }

        var labels = CollectCommandLabels(testInput, scheme);
        var labelSuffix = labels.Count > 0 ? $"  ({string.Join(", ", labels)})" : "";

        TestCommandResult = $"→ {compoundResult.CanonicalString}{labelSuffix}";
        TestCommandIsError = false;
    }

    private static List<string> CollectCommandLabels(string input, CommandScheme scheme)
    {
        var labels = new List<string>();
        var blocks = input.Split(';');

        foreach (var blockStr in blocks)
        {
            var block = blockStr.Trim();
            if (string.IsNullOrEmpty(block))
            {
                continue;
            }

            // Strip condition prefixes (LV/AT/GIVEWAY/BEHIND + argument)
            var upper = block.ToUpperInvariant();
            var remaining = block;
            if (upper.StartsWith("LV ") || upper.StartsWith("AT ") || upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND "))
            {
                var tokens = block.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                remaining = tokens.Length >= 3 ? tokens[2] : "";
            }

            var commands = remaining.Split(',');
            foreach (var cmdStr in commands)
            {
                var cmd = cmdStr.Trim();
                if (string.IsNullOrEmpty(cmd))
                {
                    continue;
                }

                var parsed = CommandSchemeParser.Parse(cmd, scheme);
                if (parsed is not null)
                {
                    var label = LabelForType(parsed.Type);
                    if (label is not null)
                    {
                        labels.Add(label);
                    }
                }
            }
        }

        return labels;
    }

    private static string? LabelForType(CanonicalCommandType type)
    {
        return CommandRegistry.Get(type)?.Label;
    }

    private static int AutoDeleteOverrideToIndex(string value) =>
        value switch
        {
            "Never" => 1,
            "OnLanding" => 2,
            "Parked" => 3,
            _ => 0,
        };

    private static string IndexToAutoDeleteOverride(int index) =>
        index switch
        {
            1 => "Never",
            2 => "OnLanding",
            3 => "Parked",
            _ => "",
        };

    [RelayCommand]
    private void StartKeyCapture()
    {
        StartKeyCaptureFor("AircraftSelect");
    }

    [RelayCommand]
    private void StartFocusInputKeyCapture()
    {
        StartKeyCaptureFor("FocusInput");
    }

    [RelayCommand]
    private void StartTakeControlKeyCapture()
    {
        StartKeyCaptureFor("TakeControl");
    }

    [RelayCommand]
    private void StartAlwaysOnTopKeyCapture()
    {
        StartKeyCaptureFor("AlwaysOnTop");
    }

    [RelayCommand]
    private void StartPttKeyCapture()
    {
        StartKeyCaptureFor("Ptt");
    }

    private void StartKeyCaptureFor(string target)
    {
        _captureTarget = target;
        IsCapturingKey = true;
        switch (target)
        {
            case "AircraftSelect":
                AircraftSelectKeyDisplay = "Press a key combo...";
                break;
            case "FocusInput":
                FocusInputKeyDisplay = "Press a key combo...";
                break;
            case "TakeControl":
                TakeControlKeyDisplay = "Press a key combo...";
                break;
            case "AlwaysOnTop":
                AlwaysOnTopKeyDisplay = "Press a key combo...";
                break;
            case "Ptt":
                PttKeyDisplay = "Press a key combo...";
                break;
        }
    }

    public void CaptureKey(Key key, KeyModifiers modifiers)
    {
        if (!IsCapturingKey)
        {
            return;
        }

        // Modifier-only keys (RightCtrl, LeftShift, etc.) are normally rejected, but PTT is commonly
        // bound to a bare modifier — so accept it when the capture target is Ptt.
        var isModifierOnly =
            key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
        if (isModifierOnly && _captureTarget != "Ptt")
        {
            return;
        }

        // For PTT modifier-only capture, store just the raw key name with no modifier prefix so
        // the combo round-trips cleanly through Enum.TryParse<Key> in KeyNameToDisplay.
        var combo = isModifierOnly ? key.ToString() : BuildKeyCombo(key, modifiers);
        switch (_captureTarget)
        {
            case "AircraftSelect":
                _aircraftSelectKeyName = combo;
                AircraftSelectKeyDisplay = KeyComboToDisplay(combo);
                break;
            case "FocusInput":
                _focusInputKeyName = combo;
                FocusInputKeyDisplay = KeyComboToDisplay(combo);
                break;
            case "TakeControl":
                _takeControlKeyName = combo;
                TakeControlKeyDisplay = KeyComboToDisplay(combo);
                break;
            case "AlwaysOnTop":
                _alwaysOnTopKeyName = combo;
                AlwaysOnTopKeyDisplay = KeyComboToDisplay(combo);
                break;
            case "Ptt":
                _pttKeyName = combo;
                PttKeyDisplay = KeyComboToDisplay(combo);
                break;
        }

        IsCapturingKey = false;
        _captureTarget = null;
    }

    public void CancelKeyCapture()
    {
        if (IsCapturingKey)
        {
            switch (_captureTarget)
            {
                case "AircraftSelect":
                    AircraftSelectKeyDisplay = KeyComboToDisplay(_aircraftSelectKeyName);
                    break;
                case "FocusInput":
                    FocusInputKeyDisplay = KeyComboToDisplay(_focusInputKeyName);
                    break;
                case "TakeControl":
                    TakeControlKeyDisplay = KeyComboToDisplay(_takeControlKeyName);
                    break;
                case "AlwaysOnTop":
                    AlwaysOnTopKeyDisplay = KeyComboToDisplay(_alwaysOnTopKeyName);
                    break;
                case "Ptt":
                    PttKeyDisplay = KeyComboToDisplay(_pttKeyName);
                    break;
            }

            IsCapturingKey = false;
            _captureTarget = null;
        }
    }

    internal static string KeyNameToDisplay(string keyName)
    {
        if (!Enum.TryParse<Key>(keyName, out var key))
        {
            return keyName;
        }

        return key switch
        {
            Key.Add => "Numpad +",
            Key.Subtract => "Numpad -",
            Key.Multiply => "Numpad *",
            Key.Divide => "Numpad /",
            Key.Decimal => "Numpad .",
            Key.NumPad0 => "Numpad 0",
            Key.NumPad1 => "Numpad 1",
            Key.NumPad2 => "Numpad 2",
            Key.NumPad3 => "Numpad 3",
            Key.NumPad4 => "Numpad 4",
            Key.NumPad5 => "Numpad 5",
            Key.NumPad6 => "Numpad 6",
            Key.NumPad7 => "Numpad 7",
            Key.NumPad8 => "Numpad 8",
            Key.NumPad9 => "Numpad 9",
            Key.OemTilde => "~",
            Key.OemMinus => "-",
            Key.OemPlus => "+",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.RightCtrl => "Right Ctrl",
            Key.LeftCtrl => "Left Ctrl",
            Key.RightShift => "Right Shift",
            Key.LeftShift => "Left Shift",
            Key.RightAlt => "Right Alt",
            Key.LeftAlt => "Left Alt",
            _ => key.ToString(),
        };
    }

    internal static string BuildKeyCombo(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    internal static string KeyComboToDisplay(string combo)
    {
        var parts = combo.Split('+');
        var display = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed is "Ctrl" or "Alt" or "Shift")
            {
                display.Add(trimmed);
            }
            else
            {
                display.Add(KeyNameToDisplay(trimmed));
            }
        }
        return string.Join(" + ", display);
    }

    public static bool ParseKeybind(string combo, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;

        var parts = combo.Split('+');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            switch (trimmed)
            {
                case "Ctrl":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "Alt":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "Shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                default:
                    if (!Enum.TryParse(trimmed, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        return key != Key.None;
    }

    partial void OnWhisperModelSizeChanged(string value)
    {
        WhisperModelStatus = FormatWhisperStatus(_modelManager.GetWhisperStatus(value), _modelManager.GetWhisperFileSize(value));
    }

    [RelayCommand]
    private async Task DownloadWhisperModel()
    {
        if (WhisperIsDownloading)
        {
            return;
        }

        _whisperDownloadCts?.Dispose();
        _whisperDownloadCts = new CancellationTokenSource();
        WhisperIsDownloading = true;
        WhisperDownloadProgress = 0;
        WhisperModelStatus = "Downloading...";

        var progress = new Progress<double>(p =>
        {
            if (!double.IsNaN(p))
            {
                WhisperDownloadProgress = p;
            }
        });

        try
        {
            var ok = await _modelManager.DownloadWhisperModelAsync(WhisperModelSize, progress, _whisperDownloadCts.Token).ConfigureAwait(true);
            var status = _modelManager.GetWhisperStatus(WhisperModelSize);
            WhisperModelStatus = ok ? FormatWhisperStatus(status, _modelManager.GetWhisperFileSize(WhisperModelSize)) : "Download failed";
        }
        finally
        {
            WhisperIsDownloading = false;
            WhisperDownloadProgress = 0;
        }
    }

    [RelayCommand]
    private void DeleteWhisperModel()
    {
        _modelManager.DeleteWhisperModel(WhisperModelSize);
        WhisperModelStatus = FormatWhisperStatus(_modelManager.GetWhisperStatus(WhisperModelSize), 0);
    }

    [RelayCommand]
    private void CancelWhisperDownload()
    {
        _whisperDownloadCts?.Cancel();
    }

    private static string FormatWhisperStatus(WhisperModelStatus status, long bytes)
    {
        return status switch
        {
            Services.WhisperModelStatus.NotDownloaded => "Not downloaded",
            Services.WhisperModelStatus.Downloading => "Downloading...",
            Services.WhisperModelStatus.Ready => $"Ready ({bytes / (1024 * 1024)} MB)",
            Services.WhisperModelStatus.Failed => "Failed (partial or corrupt)",
            _ => "Unknown",
        };
    }

    // ---------- LM-Kit catalog selection ----------

    partial void OnSelectedWhisperLmKitModelChanged(LmKitModelEntry? value)
    {
        // Push the entry's ModelId into WhisperModelSize so WhisperSttEngine.EnsureLoaded picks
        // it up via the existing UserPreferences read path. Setting WhisperModelSize triggers
        // OnWhisperModelSizeChanged below which persists to disk.
        if (value is not null)
        {
            WhisperModelSize = value.ModelId;
        }
    }

    partial void OnSelectedLlmLmKitModelChanged(LmKitModelEntry? value)
    {
        // Same pattern as above — write the LM-Kit ModelId into LlmModelPath. LocalLlmService's
        // EnsureLoaded dispatches the value: rooted path → file constructor; URI → URI
        // constructor; bare model ID → LoadFromModelID.
        if (value is not null)
        {
            LlmModelPath = value.ModelId;
        }
    }

    // ---------- LLM catalog download (legacy ModelManager path) ----------

    partial void OnSelectedLlmModelChanged(LlmCatalogEntry? value)
    {
        RefreshLlmModelStatus();
    }

    private void RefreshLlmModelStatus()
    {
        var entry = SelectedLlmModel;
        if (entry is null)
        {
            LlmModelStatus = "No catalog entry selected";
            return;
        }

        var status = _modelManager.GetLlmStatus(entry.Id);
        var bytes = _modelManager.GetLlmFileSize(entry.Id);
        LlmModelStatus = status switch
        {
            ModelStatus.NotDownloaded => $"Not downloaded (~{entry.ApproxSizeMb} MB)",
            ModelStatus.Downloading => "Downloading...",
            ModelStatus.Ready => $"Ready ({bytes / (1024 * 1024)} MB)",
            ModelStatus.Failed => "Failed (partial or corrupt)",
            _ => "Unknown",
        };
    }

    [RelayCommand]
    private async Task DownloadLlmModel()
    {
        var entry = SelectedLlmModel;
        if (entry is null || LlmIsDownloading)
        {
            return;
        }

        _llmDownloadCts?.Dispose();
        _llmDownloadCts = new CancellationTokenSource();
        LlmIsDownloading = true;
        LlmDownloadProgress = 0;
        LlmModelStatus = "Downloading...";

        var progress = new Progress<double>(p =>
        {
            if (!double.IsNaN(p))
            {
                LlmDownloadProgress = p;
            }
        });

        try
        {
            var ok = await _modelManager.DownloadLlmModelAsync(entry.Id, progress, _llmDownloadCts.Token).ConfigureAwait(true);
            if (ok)
            {
                // Point LlmModelPath at the newly downloaded file so LocalLlmService picks it up
                // on next load. The user doesn't need to do anything else — Settings Save writes
                // the path to prefs and LocalLlmService lazy-loads from it on first use.
                LlmModelPath = _modelManager.GetLlmPath(entry.Id);
            }

            RefreshLlmModelStatus();
            if (!ok)
            {
                LlmModelStatus = "Download failed";
            }
        }
        finally
        {
            LlmIsDownloading = false;
            LlmDownloadProgress = 0;
        }
    }

    [RelayCommand]
    private void DeleteLlmModel()
    {
        var entry = SelectedLlmModel;
        if (entry is null)
        {
            return;
        }

        _modelManager.DeleteLlmModel(entry.Id);

        // If LlmModelPath was pointing at the deleted model, clear it so the runtime doesn't try
        // to load a missing file next time speech is used.
        var deletedPath = _modelManager.GetLlmPath(entry.Id);
        if (string.Equals(LlmModelPath, deletedPath, StringComparison.OrdinalIgnoreCase))
        {
            LlmModelPath = "";
        }

        RefreshLlmModelStatus();
    }

    [RelayCommand]
    private void CancelLlmDownload()
    {
        _llmDownloadCts?.Cancel();
    }

    // ---------- LLamaSharp Vulkan GPU runtime download ----------

    [RelayCommand]
    private async Task DownloadLlamaVulkanRuntime()
    {
        if (LlamaVulkanIsDownloading)
        {
            return;
        }

        _llamaVulkanDownloadCts?.Dispose();
        _llamaVulkanDownloadCts = new CancellationTokenSource();
        LlamaVulkanIsDownloading = true;
        LlamaVulkanDownloadProgress = 0;
        LlamaVulkanRuntimeStatus = "Downloading...";

        var progress = new Progress<double>(p =>
        {
            if (!double.IsNaN(p))
            {
                LlamaVulkanDownloadProgress = p;
            }
        });

        try
        {
            var ok = await _gpuDownloader.DownloadLlamaVulkanRuntimeAsync(progress, _llamaVulkanDownloadCts.Token).ConfigureAwait(true);
            LlamaVulkanRuntimeStatus = ok
                ? FormatGpuRuntimeStatus(_gpuDownloader.GetLlamaVulkanStatus()) + " (restart YAAT to activate)"
                : "Download failed";
        }
        finally
        {
            LlamaVulkanIsDownloading = false;
            LlamaVulkanDownloadProgress = 0;
        }
    }

    [RelayCommand]
    private void DeleteLlamaVulkanRuntime()
    {
        _gpuDownloader.DeleteLlamaVulkanRuntime();
        LlamaVulkanRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetLlamaVulkanStatus()) + " (restart YAAT to deactivate)";
    }

    [RelayCommand]
    private void CancelLlamaVulkanDownload()
    {
        _llamaVulkanDownloadCts?.Cancel();
    }

    // ---------- LLamaSharp CUDA 12 runtime download ----------

    [RelayCommand]
    private async Task DownloadLlamaCudaRuntime()
    {
        if (LlamaCudaIsDownloading)
        {
            return;
        }

        _llamaCudaDownloadCts?.Dispose();
        _llamaCudaDownloadCts = new CancellationTokenSource();
        LlamaCudaIsDownloading = true;
        LlamaCudaDownloadProgress = 0;
        LlamaCudaRuntimeStatus = "Downloading...";

        var progress = new Progress<double>(p =>
        {
            if (!double.IsNaN(p))
            {
                LlamaCudaDownloadProgress = p;
            }
        });

        try
        {
            var ok = await _gpuDownloader.DownloadLlamaCudaRuntimeAsync(progress, _llamaCudaDownloadCts.Token).ConfigureAwait(true);
            LlamaCudaRuntimeStatus = ok
                ? FormatGpuRuntimeStatus(_gpuDownloader.GetLlamaCudaStatus()) + " (restart YAAT to activate)"
                : "Download failed";
        }
        finally
        {
            LlamaCudaIsDownloading = false;
            LlamaCudaDownloadProgress = 0;
        }
    }

    [RelayCommand]
    private void DeleteLlamaCudaRuntime()
    {
        _gpuDownloader.DeleteLlamaCudaRuntime();
        LlamaCudaRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetLlamaCudaStatus()) + " (restart YAAT to deactivate)";
    }

    [RelayCommand]
    private void CancelLlamaCudaDownload()
    {
        _llamaCudaDownloadCts?.Cancel();
    }

    // ---------- Whisper.net Vulkan runtime download ----------

    [RelayCommand]
    private async Task DownloadWhisperVulkanRuntime()
    {
        if (WhisperVulkanIsDownloading)
        {
            return;
        }

        _whisperVulkanDownloadCts?.Dispose();
        _whisperVulkanDownloadCts = new CancellationTokenSource();
        WhisperVulkanIsDownloading = true;
        WhisperVulkanDownloadProgress = 0;
        WhisperVulkanRuntimeStatus = "Downloading...";

        var progress = new Progress<double>(p =>
        {
            if (!double.IsNaN(p))
            {
                WhisperVulkanDownloadProgress = p;
            }
        });

        try
        {
            var ok = await _gpuDownloader.DownloadWhisperVulkanRuntimeAsync(progress, _whisperVulkanDownloadCts.Token).ConfigureAwait(true);
            WhisperVulkanRuntimeStatus = ok
                ? FormatGpuRuntimeStatus(_gpuDownloader.GetWhisperVulkanStatus()) + " (restart YAAT to activate)"
                : "Download failed";
        }
        finally
        {
            WhisperVulkanIsDownloading = false;
            WhisperVulkanDownloadProgress = 0;
        }
    }

    [RelayCommand]
    private void DeleteWhisperVulkanRuntime()
    {
        _gpuDownloader.DeleteWhisperVulkanRuntime();
        WhisperVulkanRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetWhisperVulkanStatus()) + " (restart YAAT to deactivate)";
    }

    [RelayCommand]
    private void CancelWhisperVulkanDownload()
    {
        _whisperVulkanDownloadCts?.Cancel();
    }

    // ---------- Whisper.net CUDA runtime download ----------

    [RelayCommand]
    private async Task DownloadWhisperCudaRuntime()
    {
        if (WhisperCudaIsDownloading)
        {
            return;
        }

        _whisperCudaDownloadCts?.Dispose();
        _whisperCudaDownloadCts = new CancellationTokenSource();
        WhisperCudaIsDownloading = true;
        WhisperCudaDownloadProgress = 0;
        WhisperCudaRuntimeStatus = "Downloading...";

        var progress = new Progress<double>(p =>
        {
            if (!double.IsNaN(p))
            {
                WhisperCudaDownloadProgress = p;
            }
        });

        try
        {
            var ok = await _gpuDownloader.DownloadWhisperCudaRuntimeAsync(progress, _whisperCudaDownloadCts.Token).ConfigureAwait(true);
            WhisperCudaRuntimeStatus = ok
                ? FormatGpuRuntimeStatus(_gpuDownloader.GetWhisperCudaStatus()) + " (restart YAAT to activate)"
                : "Download failed";
        }
        finally
        {
            WhisperCudaIsDownloading = false;
            WhisperCudaDownloadProgress = 0;
        }
    }

    [RelayCommand]
    private void DeleteWhisperCudaRuntime()
    {
        _gpuDownloader.DeleteWhisperCudaRuntime();
        WhisperCudaRuntimeStatus = FormatGpuRuntimeStatus(_gpuDownloader.GetWhisperCudaStatus()) + " (restart YAAT to deactivate)";
    }

    [RelayCommand]
    private void CancelWhisperCudaDownload()
    {
        _whisperCudaDownloadCts?.Cancel();
    }

    private static string FormatGpuRuntimeStatus(GpuRuntimeStatus status)
    {
        return status switch
        {
            GpuRuntimeStatus.NotInstalled => "Not installed",
            GpuRuntimeStatus.Downloading => "Downloading...",
            GpuRuntimeStatus.Installed => "Installed",
            GpuRuntimeStatus.Failed => "Failed",
            _ => "Unknown",
        };
    }

    [RelayCommand]
    private void ResetAllColors()
    {
        var d = GroundColorScheme.Default;
        GroundBackgroundColor = d.Background;
        GroundTaxiwayColor = d.Taxiway;
        GroundTaxiLabelColor = d.TaxiLabel;
        GroundRampEdgeColor = d.RampEdge;
        GroundHoldShortColor = d.HoldShort;
        GroundRunwayFillColor = d.RunwayFill;
        GroundRunwayOutlineColor = d.RunwayOutline;
        GroundAircraftColor = d.Aircraft;
        GroundDatablockTextColor = d.DatablockText;
        GroundBrightness = d.Brightness;
        AssignmentTintEnabled = false;
        AssignmentTintColor = "#00FF00";
        UnassignedTintEnabled = false;
        UnassignedTintColor = "#888888";
        SelectedColor = "#FFFFFF";
    }

    /// <summary>
    /// Returns the current ground color scheme from the editor fields (not yet saved).
    /// </summary>
    public GroundColorScheme GetCurrentGroundColors() =>
        new(
            GroundBackgroundColor,
            GroundTaxiwayColor,
            GroundTaxiLabelColor,
            GroundRampEdgeColor,
            GroundHoldShortColor,
            GroundRunwayFillColor,
            GroundRunwayOutlineColor,
            GroundAircraftColor,
            GroundDatablockTextColor,
            GroundBrightness
        );

    // Visual property change handlers — fire VisualSettingsChanged for live preview
    partial void OnGroundBackgroundColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundTaxiwayColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundTaxiLabelColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundRampEdgeColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundHoldShortColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundRunwayFillColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundRunwayOutlineColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundAircraftColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundDatablockTextColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundBrightnessChanged(int value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundSatelliteImageBrightnessChanged(int value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundVideoMapOverlayBrightnessChanged(int value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundYaatLayoutBrightnessChanged(int value) => VisualSettingsChanged?.Invoke();

    partial void OnAssignmentTintEnabledChanged(bool value) => VisualSettingsChanged?.Invoke();

    partial void OnAssignmentTintColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnUnassignedTintEnabledChanged(bool value) => VisualSettingsChanged?.Invoke();

    partial void OnUnassignedTintColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnSelectedColorChanged(string value) => VisualSettingsChanged?.Invoke();

    partial void OnDataGridFontSizeChanged(int value) => VisualSettingsChanged?.Invoke();

    partial void OnGroundHideDataBlocksByDefaultChanged(bool value) => VisualSettingsChanged?.Invoke();
}
