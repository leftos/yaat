using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using static Yaat.Client.Services.BundledPreferenceRule;

namespace Yaat.Client.Services;

/// <summary>
/// The preferences side of the settings bundle: which preference keys travel in a bundle's <c>preferences.json</c>, the
/// rule each imported value must meet, and the export, import and layout replacement the bundle needs from the private
/// preferences data.
/// </summary>
public sealed partial class UserPreferences
{
    private const string RunDelayMinKey = "commandRunDelayMinSeconds";
    private const string RunDelayMaxKey = "commandRunDelayMaxSeconds";

    // The fields a Settings section edits, in the order they are written to a bundle, each with the rule an imported
    // value must meet: the range its Settings control and setter allow, a colour, an enum name, a key name. A key
    // belongs here only when another instructor's value is a sensible value for this user: no secrets, no paths, no
    // devices, no machine state.
    private static readonly (string Key, BundledPreferenceRule Rule)[] BundledRules =
    [
        // Simulation and scenario defaults
        ("autoAcceptEnabled", Flag),
        ("autoAcceptDelaySeconds", IntRange(0, 60)),
        (RunDelayMinKey, IntRange(0, 60)),
        (RunDelayMaxKey, IntRange(0, 60)),
        ("autoDeleteOverride", OneOf("", "Never", "OnLanding", "Parked")),
        ("departureAutoDeleteDistanceNm", OptionalDoubleRange(1, 500)),
        ("validateDctFixes", Flag),
        ("euroScopeMode", Flag),
        ("vfrCommandsForIfr", EnumName<VfrCommandsForIfr>()),
        ("soloTrainingMode", Flag),
        ("soloParkingInitialCallupRatePercent", IntRange(0, 200)),
        ("soloArrivalGeneratorRatePercent", IntRange(0, 100)),
        ("soloGoAroundProbabilityPercent", IntRange(0, 100)),
        ("rpoShowPilotSpeech", Flag),
        ("rpoPilotSpeechAudibleAlert", Flag),
        ("pilotVoiceEnabled", Flag),
        ("pilotVoiceVolume", IntRange(0, 100)),
        ("pilotVoiceRadioFxEnabled", Flag),
        ("pilotVoiceSpeechRate", DoubleRange(PilotVoiceSpeechRateMin, PilotVoiceSpeechRateMax)),
        ("autoClearedToLandGnd", Flag),
        ("autoClearedToLandTwr", Flag),
        ("autoClearedToLandApp", Flag),
        ("autoClearedToLandCtr", Flag),
        ("autoCrossRunway", Flag),
        ("autoPullUpToParallel", Flag),
        ("autoGoAroundOnOccupiedRunway", Flag),
        ("autoRejectTakeoffOnOccupiedRunway", Flag),
        ("autoArrivalSpacingOnOccupiedRunwayGnd", Flag),
        ("autoArrivalSpacingOnOccupiedRunwayTwr", Flag),
        ("raiseWindowsTogether", Flag),
        // Radar display
        ("flashNoLandingClearance", Flag),
        ("showConflictAlerts", Flag),
        ("showTypeMismatchHints", Flag),
        ("showAtpa", Flag),
        ("tpaConeHalfAngleDegrees", DoubleRange(TpaConeHalfAngleMin, TpaConeHalfAngleMax)),
        ("scrollSensitivity", DoubleRange(ScrollSensitivityMin, ScrollSensitivityMax)),
        ("mvaHintDefaultGnd", Flag),
        ("mvaHintDefaultTwr", Flag),
        ("mvaHintDefaultApp", Flag),
        ("mvaHintDefaultCtr", Flag),
        ("showSpeechBubbles", Flag),
        ("speechBubbleDurationMultiplier", DoubleRange(0.25, 4.0)),
        ("showWarningSpeechBubbles", Flag),
        ("speechBubblesStayUntilClicked", Flag),
        ("alwaysShowGroundBubblesOnRadar", Flag),
        ("syncStudentDatablockColors", Flag),
        ("markStudentLimitedDatablocks", Flag),
        ("collapseStudentDatablocks", Flag),
        ("syncStudentLeaderDirection", Flag),
        // Colors and fonts
        ("assignmentTintEnabled", Flag),
        ("assignmentTintColor", Color),
        ("unassignedTintEnabled", Flag),
        ("unassignedTintColor", Color),
        ("selectedColor", Color),
        ("radarDatablockFontSize", IntRange(8, 24)),
        ("radarFlyoutFontSize", IntRange(8, 24)),
        ("groundDatablockFontSize", IntRange(8, 24)),
        ("groundLabelFontSize", IntRange(8, 24)),
        ("dataGridFontSize", IntRange(8, 24)),
        ("terminalFontSize", IntRange(8, 24)),
        ("interfaceFontSize", IntRange(8, 24)),
        // Ground view
        ("groundBackgroundColor", Color),
        ("groundTaxiwayColor", Color),
        ("groundTaxiLabelColor", Color),
        ("groundRampEdgeColor", Color),
        ("groundHoldShortColor", Color),
        ("groundRunwayFillColor", Color),
        ("groundRunwayOutlineColor", Color),
        ("groundAircraftColor", Color),
        ("groundDatablockTextColor", Color),
        ("groundBrightness", IntRange(10, 100)),
        ("groundSatelliteImageBrightness", IntRange(10, 100)),
        ("groundVideoMapOverlayBrightness", IntRange(10, 100)),
        ("groundYaatLayoutBrightness", IntRange(10, 100)),
        ("groundHideDataBlocksByDefault", Flag),
        ("groundShowTaxiRouteOnHover", Flag),
        ("groundShowAllTaxiRoutes", Flag),
        // Terminal
        ("terminalCommandColor", Color),
        ("terminalResponseColor", Color),
        ("terminalSystemColor", Color),
        ("terminalSayColor", Color),
        ("terminalPilotSpeechColor", Color),
        ("terminalWarningColor", Color),
        ("terminalErrorColor", Color),
        ("terminalChatColor", Color),
        ("terminalTdlsColor", Color),
        ("terminalStripColor", Color),
        // Command input, strips and vTDLS
        ("signatureHelpPlacement", OneOf("Above", "Below")),
        ("autoExpandSuggestionOnEnter", Flag),
        ("stripsZoomPercent", IntRange(50, 200)),
        ("tdlsZoomPercent", IntRange(50, 200)),
        // Keys
        ("aircraftSelectKey", KeyName),
        ("focusInputKey", KeyName),
        ("takeControlKey", KeyName),
        ("alwaysOnTopKey", KeyName),
        ("popOutAircraftListKey", KeyName),
        ("popOutGroundViewKey", KeyName),
        ("popOutRadarViewKey", KeyName),
        ("popOutTerminalKey", KeyName),
        ("popOutControllersKey", KeyName),
        ("popOutMetarKey", KeyName),
        ("favoritesBarKey", KeyName),
        ("quickBookmarkKey", KeyName),
        ("pttKey", KeyName),
        // Speech; a model source is exported only without a local path, user part or query, and imported only as a
        // curated id or a huggingface.co address (BundledPreferenceRule.ModelSource)
        ("speechEnabled", Flag),
        ("whisperModelSize", ModelSource),
        ("llmModelPath", ModelSource),
        ("autoFocusInputAfterSpeech", Flag),
        ("speechSampleCacheMaxMb", IntRange(10, 500)),
    ];

    private static readonly string[] BundledKeys = [.. BundledRules.Select(r => r.Key)];

    private static readonly Dictionary<string, BundledPreferenceRule> RulesByKey = BundledRules.ToDictionary(
        r => r.Key,
        r => r.Rule,
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> ModelSourceKeys = new(["whisperModelSize", "llmModelPath"], StringComparer.Ordinal);

    // Every preference key that does not travel in a bundle, with the reason. A key added to the preferences file
    // must be placed here or in BundledRules: SettingsBundlePreferencesTests fails until it is.
    private static readonly Dictionary<string, string> UnbundledKeys = new(StringComparer.Ordinal)
    {
        ["commandScheme"] = "has its own bundle entry (verbs)",
        ["macros"] = "has its own bundle entry (macros)",
        ["gridLayout"] = "has its own bundle entry (grid layout)",
        ["layouts"] = "has its own bundle entry (layouts)",
        ["windowProfiles"] = "legacy key, migrated into layouts on load",
        ["loadedFavoriteSetIds"] = "favorites state; the favorites entry carries the loaded-set ids",
        ["favoriteCommands"] = "legacy favorites, migrated into the favorites store",
        ["favoriteCommandSets"] = "legacy favorites, migrated into the favorites store",
        ["loadedFavoriteSetNames"] = "legacy favorites, migrated into the favorites store",
        ["showFavoritesBar"] = "favorites bar state, carried by layouts",
        ["isFavoritesPanelOpen"] = "favorites panel state, carried by layouts",
        ["favoritePanelColumns"] = "favorites panel state, not a Settings section field",
        ["savedServers"] = "servers belong to this machine's user",
        ["lastUsedServerUrl"] = "servers belong to this machine's user",
        ["userInitials"] = "identity",
        ["artccId"] = "identity",
        ["isAdminMode"] = "admin access",
        ["adminPassword"] = "secret",
        ["discordRichPresenceEnabled"] = "publishes activity to Discord; each user opts in",
        ["lastActiveRoomId"] = "session state",
        ["lastLiveSession"] = "session state",
        ["lastScenarioFolder"] = "a path on this machine",
        ["lastWeatherFolder"] = "a path on this machine",
        ["crcAliasDirectory"] = "a path on this machine",
        ["recentScenarios"] = "recent files on this machine",
        ["recentWeatherFiles"] = "recent files on this machine",
        ["audioInputDevice"] = "an audio device on this machine",
        ["audioOutputDevice"] = "an audio device on this machine",
        ["rendererMode"] = "a graphics backend choice for this machine",
        ["llmGpuLayers"] = "depends on this machine's GPU",
        ["speechSampleCaptureEnabled"] = "records the user's voice; each user consents on their own machine",
        ["speechTelemetryEnabled"] = "telemetry consent",
        ["speechTelemetryPromptShown"] = "telemetry consent prompt shown",
        ["mainWindowGeometry"] = "window geometry",
        ["settingsWindowGeometry"] = "window geometry",
        ["terminalWindowGeometry"] = "window geometry",
        ["groundViewWindowGeometry"] = "window geometry",
        ["radarViewWindowGeometry"] = "window geometry",
        ["dataGridWindowGeometry"] = "window geometry",
        ["windowGeometries"] = "window geometry, including each window's always-on-top flag",
        ["isDataGridPoppedOut"] = "pop-out state, carried by layouts",
        ["isGroundViewPoppedOut"] = "pop-out state, carried by layouts",
        ["isRadarViewPoppedOut"] = "pop-out state, carried by layouts",
        ["isControllersPoppedOut"] = "pop-out state, carried by layouts",
        ["isMetarPoppedOut"] = "pop-out state, carried by layouts",
        ["isVStripsPoppedOut"] = "pop-out state",
        ["isVTdlsPoppedOut"] = "pop-out state",
        ["isTerminalPoppedOut"] = "pop-out state, carried by layouts",
        ["extraRadarViews"] = "open windows, carried by layouts",
        ["extraGroundViews"] = "open windows, carried by layouts",
        ["isVTdlsDarkMode"] = "a vTDLS view toggle, not a Settings section field",
        ["radarSettings"] = "per-scenario view state",
        ["groundSettings"] = "per-scenario view state",
        ["favoriteVideoMapsByArtcc"] = "per-ARTCC view state",
        ["favoriteVideoMapsByAirport"] = "per-airport view state",
        ["favoriteVideoMapsByScenario"] = "per-scenario view state",
        ["favoriteMetarStationsByScenario"] = "per-scenario view state",
        ["groundRotationByAirport"] = "per-airport view state",
        ["soloGoAroundProbabilityByScenario"] = "per-scenario state",
        ["scenarioNames"] = "per-scenario state",
        ["scenarioAirports"] = "per-scenario state",
        ["scenarioCommandHistory"] = "command history",
        ["showOnlyActiveAircraft"] = "an Aircraft List toggle, not a Settings section field",
        ["showTimelineBar"] = "a view toggle, not a Settings section field",
        ["dataGridAlternatingRowColor"] = "an Aircraft List toggle, not a Settings section field",
        ["liveTrafficListFilter"] = "an Aircraft List filter, not a Settings section field",
        ["terminalTimestampMode"] = "a Terminal toggle, not a Settings section field",
        ["hiddenTerminalKinds"] = "a Terminal filter, not a Settings section field",
        ["groundShowRunwayLabels"] = "a Ground View toggle, not a Settings section field",
        ["groundShowTaxiwayLabels"] = "a Ground View toggle, not a Settings section field",
        ["groundShowHoldShort"] = "a Ground View toggle, not a Settings section field",
        ["groundShowParking"] = "a Ground View toggle, not a Settings section field",
        ["groundShowSpot"] = "a Ground View toggle, not a Settings section field",
        ["groundShowAdwMarkings"] = "a Ground View toggle, not a Settings section field",
        ["groundShowSatelliteImage"] = "a Ground View toggle, not a Settings section field",
        ["groundShowVideoMapOverlay"] = "a Ground View toggle, not a Settings section field",
        ["groundShowYaatLayout"] = "a Ground View toggle, not a Settings section field",
        ["groundPanZoomLocked"] = "a Ground View toggle, not a Settings section field",
        ["groundDeconflictMode"] = "a Ground View toggle, not a Settings section field",
        ["radarDeconflictMode"] = "a Radar View toggle, not a Settings section field",
        ["radarDcbVisible"] = "a Radar View toggle, not a Settings section field",
        ["vStripsSplitMode"] = "a Strips view split, not a Settings section field",
        ["vStripsSplitRatio"] = "a Strips view split, not a Settings section field",
        ["preferencesVersion"] = "file bookkeeping",
    };

    // The serializer's own contract for the preferences file: its property names are the file's keys, and its setters
    // write an imported value without a hand-kept table per key. Built once, on first use: the options resolve metadata
    // only once locked with their reflection resolver, which the first (de)serialization would otherwise do.
    private static readonly Lazy<Dictionary<string, JsonPropertyInfo>> PreferenceProperties = new(BuildPreferenceProperties);

    /// <summary>Every key of the preferences file, as the file names it.</summary>
    public static IReadOnlyList<string> PreferenceKeys => [.. PreferenceProperties.Value.Keys];

    /// <summary>
    /// The keys a bundle's <c>preferences.json</c> carries: the fields a Settings section edits that are not personal to
    /// this machine.
    /// </summary>
    public static IReadOnlyList<string> BundledPreferenceKeys => BundledKeys;

    /// <summary>Every other preference key, each with the reason it stays out of a bundle.</summary>
    public static IReadOnlyDictionary<string, string> UnbundledPreferenceKeys => UnbundledKeys;

    /// <summary>The rule each bundled key's imported value must meet.</summary>
    internal static IReadOnlyDictionary<string, BundledPreferenceRule> BundledPreferenceRules => RulesByKey;

    /// <summary>The CLR type of a preference, by its key in the file.</summary>
    internal static Type PreferenceType(string key) => PreferenceProperties.Value[key].PropertyType;

    /// <summary>The bundled preferences with their current values, in <see cref="BundledPreferenceKeys"/> order.</summary>
    public JsonObject ExportBundlePreferences()
    {
        JsonObject all = JsonSerializer.SerializeToNode(_data, JsonOptions)!.AsObject();
        var bundled = new JsonObject();
        foreach (string key in BundledKeys)
        {
            if (all.TryGetPropertyValue(key, out JsonNode? value) && IsExportable(key, value))
            {
                bundled[key] = value?.DeepClone();
            }
        }

        return bundled;
    }

    /// <summary>
    /// Writes each bundled preference in the object whose value meets its rule, and ignores every other key with a logged
    /// warning: one not bundled, a value of the wrong type or outside its rule, and a run-delay minimum above its maximum
    /// (both run-delay keys are then ignored). Saves once; listeners for terminal colors and font sizes are told.
    /// </summary>
    public PreferencesImportResult ImportBundlePreferences(JsonObject incoming)
    {
        var accepted = new Dictionary<string, object?>(StringComparer.Ordinal);
        var ignored = new List<string>();
        foreach ((string key, JsonNode? value) in incoming)
        {
            if (TryReadBundledPreference(key, value, out object? parsed))
            {
                accepted[key] = parsed;
            }
            else
            {
                ignored.Add(key);
            }
        }

        RejectInvertedRunDelay(accepted, ignored);
        foreach ((string key, object? parsed) in accepted)
        {
            PreferenceProperties.Value[key].Set!(_data, parsed);
        }

        if (accepted.Count > 0)
        {
            Save();
            TerminalColorsChanged?.Invoke();
            FontSizesChanged?.Invoke();
        }

        return new PreferencesImportResult([.. incoming.Select(p => p.Key).Where(accepted.ContainsKey)], ignored);
    }

    /// <summary>
    /// Makes the saved layouts exactly this list, each kept as given (geometry and timestamps included), sorted by name.
    /// A blank name is dropped, and of two layouts with the same name (case-insensitive) the first is kept.
    /// </summary>
    public void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<SavedLayout>();
        foreach (SavedLayout layout in layouts)
        {
            layout.Name = layout.Name.Trim();
            if ((layout.Name.Length > 0) && names.Add(layout.Name))
            {
                kept.Add(layout);
            }
        }

        kept.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _data.Layouts = kept;
        Save();
        RaiseLayoutsChanged();
    }

    /// <summary>Reads an imported value as its preference's type and checks it against the key's rule; logs why when it does not fit.</summary>
    internal static bool TryReadBundledPreference(string key, JsonNode? value, out object? parsed)
    {
        parsed = null;
        if (
            !RulesByKey.TryGetValue(key, out BundledPreferenceRule? rule)
            || !PreferenceProperties.Value.TryGetValue(key, out JsonPropertyInfo? property)
        )
        {
            Log.LogWarning("Ignored imported preference '{Key}': it is not a preference a settings bundle carries", key);
            return false;
        }

        try
        {
            parsed = value?.Deserialize(property.PropertyType, JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Ignored imported preference '{Key}': its value is not a {Type}", key, property.PropertyType.Name);
            return false;
        }

        if (!rule.Accepts(parsed))
        {
            Log.LogWarning("Ignored imported preference '{Key}' = {Value}: it must be {Rule}", key, value?.ToJsonString(), rule.Description);
            parsed = null;
            return false;
        }

        return true;
    }

    private static Dictionary<string, JsonPropertyInfo> BuildPreferenceProperties()
    {
        JsonOptions.MakeReadOnly(populateMissingResolver: true);
        return JsonOptions.GetTypeInfo(typeof(SavedPrefs)).Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    private void RejectInvertedRunDelay(Dictionary<string, object?> accepted, List<string> ignored)
    {
        if (!accepted.ContainsKey(RunDelayMinKey) && !accepted.ContainsKey(RunDelayMaxKey))
        {
            return;
        }

        int min = accepted.TryGetValue(RunDelayMinKey, out object? newMin) ? (int)newMin! : _data.CommandRunDelayMinSeconds;
        int max = accepted.TryGetValue(RunDelayMaxKey, out object? newMax) ? (int)newMax! : _data.CommandRunDelayMaxSeconds;
        if (min <= max)
        {
            return;
        }

        foreach (string key in (string[])[RunDelayMinKey, RunDelayMaxKey])
        {
            if (accepted.Remove(key))
            {
                ignored.Add(key);
            }
        }

        Log.LogWarning("Ignored imported command run delay: minimum {Min} s is above maximum {Max} s", min, max);
    }

    private static bool IsExportable(string key, JsonNode? value) =>
        !ModelSourceKeys.Contains(key) || ((value is JsonValue json) && json.TryGetValue(out string? source) && IsExportableModelSource(source));
}
