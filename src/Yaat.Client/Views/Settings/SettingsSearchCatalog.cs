using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Settings;

/// <summary>
/// Every searchable control and link in the Settings window, section by section in the order each section shows them.
/// A control is keyed by its binding path and found on screen by its label (after its heading, when it has one); a link
/// carries the aliases of the setting it opens, so a search finds a shared setting both at its home and at each link.
/// </summary>
public static class SettingsSearchCatalog
{
    private static readonly string[] Font = ["font", "size"];
    private static readonly string[] Zoom = ["zoom"];
    private static readonly string[] OnTop = ["topmost", "pin"];
    private static readonly string[] Mic = ["mic"];
    private static readonly string[] Ptt = ["PTT"];
    private static readonly string[] Hotkey = ["hotkey", "keybind"];
    private static readonly string[] Colour = ["colour", "color"];
    private static readonly string[] Tint = ["tint", "colour", "color"];
    private static readonly string[] Delay = ["delay"];
    private static readonly string[] Verb = ["verb", "alias"];
    private static readonly string[] Macro = ["macro"];
    private static readonly string[] None = [];

    private const string AlwaysOnTop = "Always on Top";
    private const string FontSizes = "Font Sizes";
    private const string CommandRunDelay = "Command run delay (seconds):";
    private const string AutoClearToLand = "Auto-clear aircraft to land";
    private const string ArrivalSpacing = "Auto arrival spacing behind traffic on the runway";
    private const string MvaTint = "Tint datablock altitude by MVA (red below, amber at)";
    private const string LayerBrightness = "Layer Brightness";
    private const string TerminalChannels = "Terminal Channels";
    private const string WhisperModel = "Whisper Model (speech-to-text)";
    private const string LlmModel = "LLM Model (command interpretation)";
    private const string Acceleration = "Acceleration";
    private const string PiperVoicePack = "Piper voice pack";
    private const string SoloPilotVoice = "Solo pilot voice";

    public static IReadOnlyList<SettingsSearchEntry> Entries { get; } =
    [
        .. General(),
        .. Appearance(),
        .. ScenarioDefaults(),
        .. Radar(),
        .. Ground(),
        .. ViewLinks(SettingsSectionId.AircraftList, "Font size → Appearance"),
        Link(SettingsSectionId.StripsAndTdls, "Zoom → Appearance", SettingsSectionId.Appearance, Zoom),
        Link(SettingsSectionId.StripsAndTdls, "Always on top → General", SettingsSectionId.General, OnTop),
        .. Terminal(),
        .. Input(),
        .. Keys(),
        .. Speech(),
        .. Voice(),
        .. Advanced(),
    ];

    private static IEnumerable<SettingsSearchEntry> General()
    {
        const SettingsSectionId s = SettingsSectionId.General;
        return
        [
            Setting(s, "UserInitials", "Initials (2 letters):", None),
            SettingWithin(s, "DiscordRichPresenceEnabled", "Show the scenario I'm running as my Discord status", "Discord", None),
            SettingWithin(s, "RaiseWindowsTogether", "Bring all windows to front together", "Windows", None),
            SettingWithin(s, "MainWindowTopmost", "Main Window", AlwaysOnTop, OnTop),
            SettingWithin(s, "GroundViewTopmost", "Ground View", AlwaysOnTop, OnTop),
            SettingWithin(s, "RadarViewTopmost", "Radar View", AlwaysOnTop, OnTop),
            SettingWithin(s, "DataGridTopmost", "Aircraft List", AlwaysOnTop, OnTop),
            SettingWithin(s, "TerminalTopmost", "Terminal", AlwaysOnTop, OnTop),
            SettingWithin(s, "VStripsTopmost", "Flight Strips", AlwaysOnTop, OnTop),
            SettingWithin(s, "FavoritesPanelTopmost", "Favorites", AlwaysOnTop, OnTop),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> Appearance()
    {
        const SettingsSectionId s = SettingsSectionId.Appearance;
        return
        [
            SettingWithin(s, "DataGridFontSize", "Aircraft List", FontSizes, Font),
            SettingWithin(s, "RadarDatablockFontSize", "Radar Datablock", FontSizes, Font),
            SettingWithin(s, "RadarFlyoutFontSize", "Radar Tag Flyouts", FontSizes, Font),
            SettingWithin(s, "GroundDatablockFontSize", "Ground Datablock", FontSizes, Font),
            SettingWithin(s, "GroundLabelFontSize", "Ground Labels (taxi/runway/node)", FontSizes, Font),
            SettingWithin(s, "TerminalFontSize", "Terminal (output + input)", FontSizes, Font),
            SettingWithin(s, "InterfaceFontSize", "Interface (tabs, buttons, lists)", FontSizes, Font),
            SettingWithin(s, "StripsZoomPercent", "Strips Zoom %", FontSizes, Zoom),
            SettingWithin(s, "TdlsZoomPercent", "vTDLS Zoom %", FontSizes, Zoom),
            SettingWithin(s, "SelectedRendererModeIndex", "Renderer:", "Graphics", None),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> ScenarioDefaults()
    {
        const SettingsSectionId s = SettingsSectionId.ScenarioDefaults;
        return
        [
            Setting(s, "SoloTrainingMode", "Solo training mode", None),
            Setting(s, "SoloGoAroundProbabilityPercent", "Pilot go-around probability (default)", None),
            SettingWithin(s, "SoloParkingInitialCallupIntervalSeconds", "Parking call-up interval:", "Solo training", None),
            SettingWithin(s, "SoloArrivalGeneratorRatePercent", "Arrival generator rate:", "Solo training", None),
            Setting(s, "AutoAcceptEnabled", "Auto-accept handoffs to unattended positions", None),
            Setting(s, "AutoAcceptDelaySeconds", "Delay (seconds):", Delay),
            SettingWithin(s, "CommandRunDelayMinSeconds", "Min", CommandRunDelay, Delay),
            SettingWithin(s, "CommandRunDelayMaxSeconds", "Max", CommandRunDelay, Delay),
            Setting(s, "SelectedAutoDeleteIndex", "Default Auto-Delete:", None),
            Setting(s, "DepartureAutoDeleteDistanceNm", "Auto-delete departures beyond (nm)", None),
            Setting(s, "ValidateDctFixes", "Validate DCT fixes against programmed route", None),
            SettingWithin(s, "AutoClearedToLandGnd", "GND", AutoClearToLand, None),
            SettingWithin(s, "AutoClearedToLandTwr", "TWR", AutoClearToLand, None),
            SettingWithin(s, "AutoClearedToLandApp", "APP", AutoClearToLand, None),
            SettingWithin(s, "AutoClearedToLandCtr", "CTR", AutoClearToLand, None),
            Setting(s, "AutoCrossRunway", "Aircraft cross runways automatically", None),
            Setting(s, "AutoPullUpToParallel", "Auto pull-up to hold short of parallel runway after landing", None),
            Setting(s, "AutoGoAroundOnOccupiedRunway", "Auto go-around when the runway is occupied on short final", None),
            Setting(s, "AutoRejectTakeoffOnOccupiedRunway", "Auto rejected takeoff when the runway is blocked ahead", None),
            SettingWithin(s, "AutoArrivalSpacingOnOccupiedRunwayGnd", "GND", ArrivalSpacing, None),
            SettingWithin(s, "AutoArrivalSpacingOnOccupiedRunwayTwr", "TWR", ArrivalSpacing, None),
            Setting(s, "SelectedVfrCommandsForIfrIndex", "VFR commands for IFR aircraft:", None),
            Setting(s, "RpoShowPilotSpeech", "Show sim-initiated pilot transmissions as pilot speech (RPO mode)", None),
            Setting(s, "RpoPilotSpeechAudibleAlert", "Audible alert on pilot transmissions", None),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> Radar()
    {
        const SettingsSectionId s = SettingsSectionId.Radar;
        return
        [
            .. ViewLinks(s, "Font sizes → Appearance"),
            Setting(s, "EuroScopeMode", "EuroScope-style interactive tags", None),
            Setting(s, "FlashNoLandingClearance", "Flash 'NoLndgClnc' on datablock when approaching final without landing clearance", None),
            Setting(s, "ShowConflictAlerts", "Show conflict alerts (CA) on the radar", None),
            Setting(s, "ShowTypeMismatchHints", "Highlight filed-vs-actual aircraft type mismatches", None),
            Setting(s, "ShowAtpa", "Show ATPA cones and in-trail distance on the radar", None),
            Setting(s, "SyncStudentDatablockColors", "Sync datablock colors to the student's STARS scope", Colour),
            Setting(s, "MarkStudentLimitedDatablocks", "Mark limited datablocks with (LDB) / (PDB)", None),
            Setting(s, "CollapseStudentDatablocks", "Collapse datablocks to match the student's LDB/PDB/FDB", None),
            Setting(s, "SyncStudentLeaderDirection", "Sync leader-line direction to the student's scope", None),
            SettingWithin(s, "MvaHintDefaultGnd", "GND", MvaTint, Tint),
            SettingWithin(s, "MvaHintDefaultTwr", "TWR", MvaTint, Tint),
            SettingWithin(s, "MvaHintDefaultApp", "APP", MvaTint, Tint),
            SettingWithin(s, "MvaHintDefaultCtr", "CTR", MvaTint, Tint),
            Setting(s, "ShowSpeechBubbles", "Show speech bubbles for SAY and pilot transmissions", None),
            Setting(s, "SpeechBubblesStayUntilClicked", "Keep bubbles on screen until clicked to dismiss", None),
            Setting(s, "SpeechBubbleDurationMultiplier", "Duration multiplier:", None),
            Setting(s, "ShowWarningSpeechBubbles", "Also show WARN messages as speech bubbles (amber)", None),
            Setting(s, "AlwaysShowGroundBubblesOnRadar", "Always show ground aircraft bubbles on the Radar view", None),
            Setting(s, "TpaConeHalfAngleDegrees", "Instructor TPA cone half-angle (°):", None),
            Setting(s, "ScrollSensitivityPercent", "Scroll / zoom sensitivity:", None),
            Setting(s, "AssignmentTintEnabled", "Tint my assigned aircraft", Tint),
            SettingWithin(s, "AssignmentTintColor", "Tint Color:", "Tint my assigned aircraft", Tint),
            Setting(s, "UnassignedTintEnabled", "Tint unassigned aircraft", Tint),
            SettingWithin(s, "UnassignedTintColor", "Tint Color:", "Tint unassigned aircraft", Tint),
            Setting(s, "SelectedColor", "Selected Aircraft Color:", Colour),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> Ground()
    {
        const SettingsSectionId s = SettingsSectionId.Ground;
        return
        [
            Link(s, "Font sizes → Appearance", SettingsSectionId.Appearance, Font),
            Link(s, "Speech bubbles → Radar", SettingsSectionId.Radar, None),
            Link(s, "Scroll sensitivity → Radar", SettingsSectionId.Radar, Zoom),
            Link(s, "Always on top → General", SettingsSectionId.General, OnTop),
            Setting(s, "GroundHideDataBlocksByDefault", "Start with all datablocks hidden", None),
            Setting(s, "GroundShowTaxiRouteOnHover", "Show taxi route when hovering an aircraft", None),
            Setting(s, "GroundShowAllTaxiRoutes", "Show all taxiing aircraft's routes", None),
            Setting(s, "GroundBackgroundColor", "Background Color:", Colour),
            Setting(s, "GroundTaxiwayColor", "Taxiway Color:", Colour),
            Setting(s, "GroundTaxiLabelColor", "Taxiway Label Color:", Colour),
            Setting(s, "GroundRampEdgeColor", "RAMP Edge Color:", Colour),
            Setting(s, "GroundHoldShortColor", "Hold Short Color:", Colour),
            Setting(s, "GroundRunwayFillColor", "Runway Fill Color:", Colour),
            Setting(s, "GroundRunwayOutlineColor", "Runway Outline Color:", Colour),
            Setting(s, "GroundAircraftColor", "Aircraft Color:", Colour),
            Setting(s, "GroundDatablockTextColor", "Datablock Text Color:", Colour),
            Setting(s, "GroundBrightness", "Brightness:", None),
            SettingWithin(s, "GroundSatelliteImageBrightness", "Satellite Image:", LayerBrightness, None),
            SettingWithin(s, "GroundVideoMapOverlayBrightness", "Video Map Overlay:", LayerBrightness, None),
            SettingWithin(s, "GroundYaatLayoutBrightness", "YAAT Layout:", LayerBrightness, None),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> Terminal()
    {
        const SettingsSectionId s = SettingsSectionId.Terminal;
        return
        [
            .. ViewLinks(s, "Font size → Appearance"),
            SettingWithin(s, "TerminalCommandColor", "Command:", TerminalChannels, Colour),
            SettingWithin(s, "TerminalResponseColor", "Response:", TerminalChannels, Colour),
            SettingWithin(s, "TerminalSystemColor", "System:", TerminalChannels, Colour),
            SettingWithin(s, "TerminalSayColor", "SAY (controller):", TerminalChannels, Colour),
            SettingWithin(s, "TerminalPilotSpeechColor", "Pilot Speech:", TerminalChannels, Colour),
            SettingWithin(s, "TerminalWarningColor", "Warning:", TerminalChannels, Colour),
            SettingWithin(s, "TerminalErrorColor", "Error:", TerminalChannels, Colour),
            SettingWithin(s, "TerminalChatColor", "Chat:", TerminalChannels, Colour),
            SettingWithin(s, "TerminalTdlsColor", "vTDLS (PDC):", TerminalChannels, Colour),
            SettingWithin(s, "TerminalStripColor", "Flight strips:", TerminalChannels, Colour),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> Input() =>
        [
            Setting(SettingsSectionId.CommandInput, "SelectedSignatureHelpPlacementIndex", "Signature Help Placement:", None),
            Setting(SettingsSectionId.CommandInput, "AutoExpandSuggestionOnEnter", "Auto-expand highlighted suggestion on Enter", None),
            Setting(SettingsSectionId.CommandVerbs, "TestCommandInput", "Try it out:", Verb),
            Setting(SettingsSectionId.CommandVerbs, "ImportVerbsButton", "Import...", Verb),
            Setting(SettingsSectionId.CommandVerbs, "ExportVerbsButton", "Export...", Verb),
            Setting(SettingsSectionId.Macros, "CrcAliasDirectory", "CRC Aliases", ["alias", .. Macro]),
            SettingWithin(SettingsSectionId.Macros, "BrowseCrcAliasDirectoryButton", "Browse...", "CRC Aliases", ["alias", .. Macro]),
            Setting(SettingsSectionId.Macros, "AddMacroCommand", "Add Macro", Macro),
            Setting(SettingsSectionId.Macros, "ImportMacrosButton", "Import...", Macro),
            Setting(SettingsSectionId.Macros, "ExportSelectedMacrosButton", "Export Selected", Macro),
            Setting(SettingsSectionId.Macros, "ExportAllMacrosButton", "Export All", Macro),
        ];

    private static IEnumerable<SettingsSearchEntry> Keys()
    {
        const SettingsSectionId s = SettingsSectionId.Keys;
        return
        [
            Link(s, "Push-to-talk key → Speech", SettingsSectionId.Speech, [.. Ptt, .. Hotkey]),
            Link(s, "Always on top → General", SettingsSectionId.General, OnTop),
            Setting(s, "StartKeyCaptureCommand", "Aircraft Select Key:", Hotkey),
            Setting(s, "StartFocusInputKeyCaptureCommand", "Focus Command Input Key:", Hotkey),
            Setting(s, "StartTakeControlKeyCaptureCommand", "Take Control Key:", Hotkey),
            Setting(s, "StartAlwaysOnTopKeyCaptureCommand", "Always on Top Key:", [.. Hotkey, .. OnTop]),
            Setting(s, "StartQuickBookmarkKeyCaptureCommand", "Quick Bookmark Key:", Hotkey),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> Speech()
    {
        const SettingsSectionId s = SettingsSectionId.Speech;
        const string improve = "Help improve speech recognition";
        return
        [
            Setting(s, "SpeechEnabled", "Speech-to-text (STT)", None),
            Setting(s, "AutoFocusInputAfterSpeech", "Auto-focus command input after PTT", None),
            Setting(s, "SelectedWhisperLmKitModel", WhisperModel, None),
            SettingWithin(s, "DownloadSelectedWhisperModelCommand", "Download now", WhisperModel, None),
            SettingWithin(s, "DeleteSelectedWhisperModelCommand", "Delete cached", WhisperModel, None),
            Setting(s, "SelectedLlmLmKitModel", LlmModel, None),
            SettingWithin(s, "DownloadSelectedLlmModelCommand", "Download now", LlmModel, None),
            SettingWithin(s, "DeleteSelectedLlmModelCommand", "Delete cached", LlmModel, None),
            SettingWithin(s, "BrowseLlmModelButton", "Browse...", LlmModel, None),
            SettingWithin(s, "InstallCudaBackendCommand", "Download CUDA 13 runtime", Acceleration, None),
            SettingWithin(s, "CancelCudaBackendInstallCommand", "Cancel", Acceleration, None),
            SettingWithin(s, "UninstallCudaBackendCommand", "Uninstall CUDA", Acceleration, None),
            SettingWithin(s, "LlmGpuLayers", "LLM GPU layers:", Acceleration, None),
            SettingWithin(s, "StartPttKeyCaptureCommand", "PTT key:", "Push-to-Talk", [.. Ptt, .. Hotkey]),
            Link(s, "Microphone → Audio devices", SettingsSectionId.AudioDevices, Mic),
            SettingWithin(s, "SpeechTelemetryEnabled", "Automatically send my push-to-talk recordings to the YAAT developers", improve, None),
            SettingWithin(s, "SpeechSampleCaptureEnabled", "Save my push-to-talk samples locally for review", improve, None),
            SettingWithin(s, "SpeechSampleCacheMaxMb", "Max retained audio:", improve, None),
            SettingWithin(s, "OpenSpeechSamplesFolderCommand", "Open samples folder", improve, None),
            SettingWithin(s, "DeleteAllSpeechSamplesCommand", "Delete all saved samples", improve, None),
            Setting(s, "PilotVoiceEnabled", "Text-to-speech (TTS)", None),
            SettingWithin(s, "PilotVoiceVolume", "Volume:", SoloPilotVoice, None),
            SettingWithin(s, "PilotVoiceSpeechRate", "Speed:", SoloPilotVoice, None),
            SettingWithin(s, "PilotVoiceRadioFxEnabled", "Radio effect", SoloPilotVoice, None),
            SettingWithin(s, "InstallPiperVoicePackCommand", "Download Piper voice pack", PiperVoicePack, None),
            SettingWithin(s, "CancelPiperVoicePackInstallCommand", "Cancel", PiperVoicePack, None),
            SettingWithin(s, "UninstallPiperVoicePackCommand", "Delete cached", PiperVoicePack, None),
        ];
    }

    private static IEnumerable<SettingsSearchEntry> Voice() =>
        [
            Setting(SettingsSectionId.AudioDevices, "SelectedAudioInputDeviceDisplay", "Input device:", Mic),
            Setting(SettingsSectionId.AudioDevices, "SelectedAudioOutputDeviceDisplay", "Output device:", None),
        ];

    private static IEnumerable<SettingsSearchEntry> Advanced() =>
        [
            Setting(SettingsSectionId.ServerAdmin, "IsAdminMode", "Server Admin Mode", None),
            Setting(SettingsSectionId.ServerAdmin, "AdminPassword", "Admin Password:", None),
        ];

    // The links a view section opens with: its font size(s) in Appearance and always-on-top in General.
    private static IEnumerable<SettingsSearchEntry> ViewLinks(SettingsSectionId section, string fontLink) =>
        [Link(section, fontLink, SettingsSectionId.Appearance, Font), Link(section, "Always on top → General", SettingsSectionId.General, OnTop)];

    // A control is keyed by its binding path, or by its x:Name when the window wires it up in code.
    private static SettingsSearchEntry Setting(SettingsSectionId section, string key, string label, string[] aliases) =>
        SettingWithin(section, key, label, null, aliases);

    private static SettingsSearchEntry SettingWithin(SettingsSectionId section, string key, string label, string? within, string[] aliases) =>
        new()
        {
            Section = section,
            Key = key,
            Label = label,
            Within = within,
            LinkTarget = null,
            Aliases = aliases,
        };

    private static SettingsSearchEntry Link(SettingsSectionId section, string label, SettingsSectionId target, string[] aliases) =>
        new()
        {
            Section = section,
            Key = null,
            Label = label,
            Within = null,
            LinkTarget = target,
            Aliases = aliases,
        };
}
