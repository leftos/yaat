using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Settings window's side of the Import / Export hub: an import opened from Settings is staged here like any other
/// edit (Apply or OK commits it, Cancel drops it), and an export from Settings reads what the window shows. Macros and
/// verbs stage in their grids, the bundled preferences in the section fields, and saved layouts and the Aircraft List
/// column layout in values Apply writes.
/// </summary>
public partial class SettingsViewModel
{
    private const string RunDelayMinKey = "commandRunDelayMinSeconds";
    private const string RunDelayMaxKey = "commandRunDelayMaxSeconds";

    private static readonly Lazy<IReadOnlyDictionary<string, BundledField>> BundledFields = new(BuildBundledFields);

    /// <summary>Saved layouts an import staged; null while none is staged.</summary>
    private List<SavedLayout>? _stagedLayouts;

    /// <summary>The Aircraft List column layout an import staged; null while none is staged.</summary>
    private SavedGridLayout? _stagedGridLayout;

    /// <summary>The parking call-up rate an import staged, written exactly by Apply unless the slider is moved after it.</summary>
    private int? _importedSoloParkingInitialCallupRatePercent;

    /// <summary>The favorites writes imports staged, in the order staged; Apply runs them and drops them.</summary>
    private readonly List<Action<UserPreferences>> _stagedFavoritesWrites = [];

    /// <summary>The macros the grid shows, without the rows lacking a name or an expansion.</summary>
    public IReadOnlyList<SavedMacro> StagedMacros => ExportMacros();

    /// <summary>The saved layouts as Apply would leave them.</summary>
    public IReadOnlyList<SavedLayout> StagedLayouts => _stagedLayouts ?? _preferences.Layouts;

    /// <summary>The Aircraft List column layout as Apply would leave it; null when none was ever saved or staged.</summary>
    public SavedGridLayout? StagedGridLayout => _stagedGridLayout ?? _preferences.GridLayout;

    /// <summary>Whether the last Apply wrote a staged Aircraft List column layout, which the host then applies to the live grids.</summary>
    public bool LastApplyCommittedGridLayout { get; private set; }

    /// <summary>Makes the macro grid exactly this list.</summary>
    public void StageMacros(IReadOnlyList<SavedMacro> macros)
    {
        MacroRows.Clear();
        foreach (SavedMacro macro in macros)
        {
            MacroRows.Add(
                new MacroRow
                {
                    Name = macro.Name,
                    Expansion = macro.Expansion,
                    RemoveAction = r => MacroRows.Remove(r),
                }
            );
        }

        OnTestCommandInputChanged(TestCommandInput);
    }

    /// <summary>Overlays the verb grid: each listed command the grid shows takes the file's aliases, every other row keeps its own.</summary>
    /// <returns>How many rows changed.</returns>
    public int StageVerbs(CommandSchemeImport verbs)
    {
        int changed = 0;
        foreach ((CanonicalCommandType type, List<string> aliases) in verbs.Verbs)
        {
            if ((VerbMappings.FirstOrDefault(r => r.CommandType == type) is { } row) && !row.AliasesList.SequenceEqual(aliases))
            {
                row.Aliases = string.Join(", ", aliases);
                changed++;
            }
        }

        OnTestCommandInputChanged(TestCommandInput);
        return changed;
    }

    /// <summary>Stages the saved layouts Apply writes, replacing the whole list.</summary>
    public void StageLayouts(IReadOnlyList<SavedLayout> layouts) => _stagedLayouts = [.. layouts];

    /// <summary>Stages the Aircraft List column layout Apply writes.</summary>
    public void StageGridLayout(SavedGridLayout layout) => _stagedGridLayout = layout;

    /// <summary>
    /// Stages a write to the favorites (the store, or the loaded sets in the preferences) that Apply runs with the
    /// preferences it commits to, after every write staged before it.
    /// </summary>
    public void StageFavoritesWrite(Action<UserPreferences> write) => _stagedFavoritesWrites.Add(write);

    /// <summary>
    /// Sets the section field of each bundled preference in the object whose value meets its rule, as
    /// <see cref="UserPreferences.ImportBundlePreferences"/> checks them against the fields shown; every other key is
    /// ignored and every other field kept.
    /// </summary>
    public PreferencesImportResult StagePreferences(JsonObject incoming)
    {
        (IReadOnlyDictionary<string, object?> accepted, PreferencesImportResult result) = UserPreferences.ReadBundledPreferences(
            incoming,
            CommandRunDelayMinSeconds,
            CommandRunDelayMaxSeconds
        );
        foreach ((string key, object? parsed) in accepted)
        {
            FieldFor(key).Write(this, parsed);
        }

        RecomputeKeybindClashes();
        return result;
    }

    /// <summary>
    /// The bundled preferences as the section fields hold them, as a bundle's <c>preferences.json</c>; a model source
    /// that names a local file is left out, as in an export of the saved preferences.
    /// </summary>
    public SettingsBundleEntry ExportStagedPreferences()
    {
        var staged = new JsonObject();
        foreach (string key in UserPreferences.BundledPreferenceKeys)
        {
            JsonNode? value = JsonSerializer.SerializeToNode(
                FieldFor(key).Read(this),
                UserPreferences.PreferenceType(key),
                UserPreferences.JsonOptions
            );
            if (UserPreferences.IsExportable(key, value))
            {
                staged[key] = value;
            }
        }

        return new SettingsBundleEntry(
            SettingsItemType.Preferences,
            SettingsBundleFormats.PreferencesJson,
            "preferences.json",
            JsonSerializer.SerializeToUtf8Bytes(staged, UserPreferences.JsonOptions)
        );
    }

    private void ApplyStagedFavoritesWrites()
    {
        foreach (Action<UserPreferences> write in _stagedFavoritesWrites)
        {
            write(_preferences);
        }

        _stagedFavoritesWrites.Clear();
    }

    private void ApplyStagedLayouts()
    {
        if (_stagedLayouts is not null)
        {
            _preferences.ReplaceLayouts(_stagedLayouts);
            _stagedLayouts = null;
        }

        LastApplyCommittedGridLayout = _stagedGridLayout is not null;
        if (_stagedGridLayout is not null)
        {
            _preferences.SetGridLayout(_stagedGridLayout);
            _stagedGridLayout = null;
        }
    }

    private void StageSoloParkingInitialCallupRate(int ratePercent)
    {
        int interval = SoloPacing.ParkingInitialCallupRateToIntervalSeconds(ratePercent);
        _importedSoloParkingInitialCallupRatePercent = ratePercent;
        _appliedSoloParkingInitialCallupIntervalSeconds = interval;
        SoloParkingInitialCallupIntervalSeconds = interval;
    }

    private static BundledField FieldFor(string key) =>
        BundledFields.Value.TryGetValue(key, out BundledField? field)
            ? field
            : throw new InvalidOperationException(
                $"The bundled preference '{key}' has no Settings field; add it to SettingsViewModel's bundled fields (SettingsViewModel.Bundle.cs)."
            );

    private static IReadOnlyDictionary<string, BundledField> BuildBundledFields()
    {
        IEnumerable<(string Key, BundledField Field)> keybinds = KeybindDescriptors.Select(descriptor =>
            Field(
                char.ToLowerInvariant(descriptor.Id[0]) + descriptor.Id[1..] + "Key",
                vm => vm.KeybindRows.Single(r => r.Descriptor.Id == descriptor.Id).Combo,
                (vm, combo) => vm.KeybindRows.Single(r => r.Descriptor.Id == descriptor.Id).SetCombo(combo)
            )
        );

        return ScenarioFields()
            .Concat(RadarFields())
            .Concat(DisplayFields())
            .Concat(ColorFields())
            .Concat(keybinds)
            .ToDictionary(f => f.Key, f => f.Field, StringComparer.Ordinal);
    }

    // The value is the preference's own type (UserPreferences.PreferenceType), as an import reads it and an export writes it.
    private static (string Key, BundledField Field) Field<T>(string key, Func<SettingsViewModel, T> read, Action<SettingsViewModel, T> write) =>
        (key, new BundledField(vm => read(vm), (vm, value) => write(vm, (T)value!)));

    private static (string Key, BundledField Field)[] ScenarioFields() =>
        [
            Field("autoAcceptEnabled", vm => vm.AutoAcceptEnabled, (vm, v) => vm.AutoAcceptEnabled = v),
            Field("autoAcceptDelaySeconds", vm => vm.AutoAcceptDelaySeconds, (vm, v) => vm.AutoAcceptDelaySeconds = v),
            Field(RunDelayMinKey, vm => vm.CommandRunDelayMinSeconds, (vm, v) => vm.CommandRunDelayMinSeconds = v),
            Field(RunDelayMaxKey, vm => vm.CommandRunDelayMaxSeconds, (vm, v) => vm.CommandRunDelayMaxSeconds = v),
            Field(
                "autoDeleteOverride",
                vm => IndexToAutoDeleteOverride(vm.SelectedAutoDeleteIndex),
                (vm, v) => vm.SelectedAutoDeleteIndex = AutoDeleteOverrideToIndex(v)
            ),
            Field(
                "departureAutoDeleteDistanceNm",
                vm => vm.DepartureAutoDeleteDistanceNm is { } distanceNm ? (double?)(double)distanceNm : null,
                (vm, v) => vm.DepartureAutoDeleteDistanceNm = v is { } distanceNm ? (decimal)distanceNm : null
            ),
            Field("validateDctFixes", vm => vm.ValidateDctFixes, (vm, v) => vm.ValidateDctFixes = v),
            Field("euroScopeMode", vm => vm.EuroScopeMode, (vm, v) => vm.EuroScopeMode = v),
            Field(
                "vfrCommandsForIfr",
                vm => ((VfrCommandsForIfr)vm.SelectedVfrCommandsForIfrIndex).ToString(),
                (vm, v) => vm.SelectedVfrCommandsForIfrIndex = (int)Enum.Parse<VfrCommandsForIfr>(v)
            ),
            Field("soloTrainingMode", vm => vm.SoloTrainingMode, (vm, v) => vm.SoloTrainingMode = v),
            Field(
                "soloParkingInitialCallupRatePercent",
                vm => vm.StagedSoloParkingInitialCallupRatePercent(),
                (vm, v) => vm.StageSoloParkingInitialCallupRate(v)
            ),
            Field("soloArrivalGeneratorRatePercent", vm => vm.SoloArrivalGeneratorRatePercent, (vm, v) => vm.SoloArrivalGeneratorRatePercent = v),
            Field("soloGoAroundProbabilityPercent", vm => vm.SoloGoAroundProbabilityPercent, (vm, v) => vm.SoloGoAroundProbabilityPercent = v),
            Field("rpoShowPilotSpeech", vm => vm.RpoShowPilotSpeech, (vm, v) => vm.RpoShowPilotSpeech = v),
            Field("rpoPilotSpeechAudibleAlert", vm => vm.RpoPilotSpeechAudibleAlert, (vm, v) => vm.RpoPilotSpeechAudibleAlert = v),
            Field("pilotVoiceEnabled", vm => vm.PilotVoiceEnabled, (vm, v) => vm.PilotVoiceEnabled = v),
            Field("pilotVoiceVolume", vm => vm.PilotVoiceVolume, (vm, v) => vm.PilotVoiceVolume = v),
            Field("pilotVoiceRadioFxEnabled", vm => vm.PilotVoiceRadioFxEnabled, (vm, v) => vm.PilotVoiceRadioFxEnabled = v),
            Field("pilotVoiceSpeechRate", vm => vm.PilotVoiceSpeechRate, (vm, v) => vm.PilotVoiceSpeechRate = v),
            Field("autoClearedToLandGnd", vm => vm.AutoClearedToLandGnd, (vm, v) => vm.AutoClearedToLandGnd = v),
            Field("autoClearedToLandTwr", vm => vm.AutoClearedToLandTwr, (vm, v) => vm.AutoClearedToLandTwr = v),
            Field("autoClearedToLandApp", vm => vm.AutoClearedToLandApp, (vm, v) => vm.AutoClearedToLandApp = v),
            Field("autoClearedToLandCtr", vm => vm.AutoClearedToLandCtr, (vm, v) => vm.AutoClearedToLandCtr = v),
            Field("autoCrossRunway", vm => vm.AutoCrossRunway, (vm, v) => vm.AutoCrossRunway = v),
            Field("autoPullUpToParallel", vm => vm.AutoPullUpToParallel, (vm, v) => vm.AutoPullUpToParallel = v),
            Field("autoGoAroundOnOccupiedRunway", vm => vm.AutoGoAroundOnOccupiedRunway, (vm, v) => vm.AutoGoAroundOnOccupiedRunway = v),
            Field(
                "autoRejectTakeoffOnOccupiedRunway",
                vm => vm.AutoRejectTakeoffOnOccupiedRunway,
                (vm, v) => vm.AutoRejectTakeoffOnOccupiedRunway = v
            ),
            Field(
                "autoArrivalSpacingOnOccupiedRunwayGnd",
                vm => vm.AutoArrivalSpacingOnOccupiedRunwayGnd,
                (vm, v) => vm.AutoArrivalSpacingOnOccupiedRunwayGnd = v
            ),
            Field(
                "autoArrivalSpacingOnOccupiedRunwayTwr",
                vm => vm.AutoArrivalSpacingOnOccupiedRunwayTwr,
                (vm, v) => vm.AutoArrivalSpacingOnOccupiedRunwayTwr = v
            ),
            Field("raiseWindowsTogether", vm => vm.RaiseWindowsTogether, (vm, v) => vm.RaiseWindowsTogether = v),
        ];

    private static (string Key, BundledField Field)[] RadarFields() =>
        [
            Field("flashNoLandingClearance", vm => vm.FlashNoLandingClearance, (vm, v) => vm.FlashNoLandingClearance = v),
            Field("showConflictAlerts", vm => vm.ShowConflictAlerts, (vm, v) => vm.ShowConflictAlerts = v),
            Field("showTypeMismatchHints", vm => vm.ShowTypeMismatchHints, (vm, v) => vm.ShowTypeMismatchHints = v),
            Field("showAtpa", vm => vm.ShowAtpa, (vm, v) => vm.ShowAtpa = v),
            Field("tpaConeHalfAngleDegrees", vm => vm.TpaConeHalfAngleDegrees, (vm, v) => vm.TpaConeHalfAngleDegrees = v),
            Field("scrollSensitivity", vm => vm.ScrollSensitivityPercent / 100.0, (vm, v) => vm.ScrollSensitivityPercent = v * 100.0),
            Field("mvaHintDefaultGnd", vm => vm.MvaHintDefaultGnd, (vm, v) => vm.MvaHintDefaultGnd = v),
            Field("mvaHintDefaultTwr", vm => vm.MvaHintDefaultTwr, (vm, v) => vm.MvaHintDefaultTwr = v),
            Field("mvaHintDefaultApp", vm => vm.MvaHintDefaultApp, (vm, v) => vm.MvaHintDefaultApp = v),
            Field("mvaHintDefaultCtr", vm => vm.MvaHintDefaultCtr, (vm, v) => vm.MvaHintDefaultCtr = v),
            Field("showSpeechBubbles", vm => vm.ShowSpeechBubbles, (vm, v) => vm.ShowSpeechBubbles = v),
            Field("speechBubbleDurationMultiplier", vm => vm.SpeechBubbleDurationMultiplier, (vm, v) => vm.SpeechBubbleDurationMultiplier = v),
            Field("showWarningSpeechBubbles", vm => vm.ShowWarningSpeechBubbles, (vm, v) => vm.ShowWarningSpeechBubbles = v),
            Field("speechBubblesStayUntilClicked", vm => vm.SpeechBubblesStayUntilClicked, (vm, v) => vm.SpeechBubblesStayUntilClicked = v),
            Field("alwaysShowGroundBubblesOnRadar", vm => vm.AlwaysShowGroundBubblesOnRadar, (vm, v) => vm.AlwaysShowGroundBubblesOnRadar = v),
            Field("syncStudentDatablockColors", vm => vm.SyncStudentDatablockColors, (vm, v) => vm.SyncStudentDatablockColors = v),
            Field("markStudentLimitedDatablocks", vm => vm.MarkStudentLimitedDatablocks, (vm, v) => vm.MarkStudentLimitedDatablocks = v),
            Field("collapseStudentDatablocks", vm => vm.CollapseStudentDatablocks, (vm, v) => vm.CollapseStudentDatablocks = v),
            Field("syncStudentLeaderDirection", vm => vm.SyncStudentLeaderDirection, (vm, v) => vm.SyncStudentLeaderDirection = v),
            Field("assignmentTintEnabled", vm => vm.AssignmentTintEnabled, (vm, v) => vm.AssignmentTintEnabled = v),
            Field("assignmentTintColor", vm => vm.AssignmentTintColor, (vm, v) => vm.AssignmentTintColor = v),
            Field("unassignedTintEnabled", vm => vm.UnassignedTintEnabled, (vm, v) => vm.UnassignedTintEnabled = v),
            Field("unassignedTintColor", vm => vm.UnassignedTintColor, (vm, v) => vm.UnassignedTintColor = v),
            Field("selectedColor", vm => vm.SelectedColor, (vm, v) => vm.SelectedColor = v),
        ];

    private static (string Key, BundledField Field)[] DisplayFields() =>
        [
            Field("radarDatablockFontSize", vm => vm.RadarDatablockFontSize, (vm, v) => vm.RadarDatablockFontSize = v),
            Field("radarFlyoutFontSize", vm => vm.RadarFlyoutFontSize, (vm, v) => vm.RadarFlyoutFontSize = v),
            Field("groundDatablockFontSize", vm => vm.GroundDatablockFontSize, (vm, v) => vm.GroundDatablockFontSize = v),
            Field("groundLabelFontSize", vm => vm.GroundLabelFontSize, (vm, v) => vm.GroundLabelFontSize = v),
            Field("dataGridFontSize", vm => vm.DataGridFontSize, (vm, v) => vm.DataGridFontSize = v),
            Field("terminalFontSize", vm => vm.TerminalFontSize, (vm, v) => vm.TerminalFontSize = v),
            Field("interfaceFontSize", vm => vm.InterfaceFontSize, (vm, v) => vm.InterfaceFontSize = v),
            Field("groundSatelliteImageBrightness", vm => vm.GroundSatelliteImageBrightness, (vm, v) => vm.GroundSatelliteImageBrightness = v),
            Field("groundVideoMapOverlayBrightness", vm => vm.GroundVideoMapOverlayBrightness, (vm, v) => vm.GroundVideoMapOverlayBrightness = v),
            Field("groundYaatLayoutBrightness", vm => vm.GroundYaatLayoutBrightness, (vm, v) => vm.GroundYaatLayoutBrightness = v),
            Field("groundHideDataBlocksByDefault", vm => vm.GroundHideDataBlocksByDefault, (vm, v) => vm.GroundHideDataBlocksByDefault = v),
            Field("groundShowTaxiRouteOnHover", vm => vm.GroundShowTaxiRouteOnHover, (vm, v) => vm.GroundShowTaxiRouteOnHover = v),
            Field("groundShowAllTaxiRoutes", vm => vm.GroundShowAllTaxiRoutes, (vm, v) => vm.GroundShowAllTaxiRoutes = v),
            Field(
                "signatureHelpPlacement",
                vm => vm.SelectedSignatureHelpPlacementIndex == 1 ? "Below" : "Above",
                (vm, v) => vm.SelectedSignatureHelpPlacementIndex = v == "Below" ? 1 : 0
            ),
            Field("autoExpandSuggestionOnEnter", vm => vm.AutoExpandSuggestionOnEnter, (vm, v) => vm.AutoExpandSuggestionOnEnter = v),
            Field("stripsZoomPercent", vm => vm.StripsZoomPercent, (vm, v) => vm.StripsZoomPercent = v),
            Field("tdlsZoomPercent", vm => vm.TdlsZoomPercent, (vm, v) => vm.TdlsZoomPercent = v),
            Field("speechEnabled", vm => vm.SpeechEnabled, (vm, v) => vm.SpeechEnabled = v),
            Field(
                "whisperModelSize",
                vm => vm.WhisperModelSize,
                (vm, v) =>
                {
                    vm.WhisperModelSize = v;
                    vm.SelectedWhisperLmKitModel = LmKitModelCatalog.FindById(vm.WhisperLmKitModels, v);
                }
            ),
            Field(
                "llmModelPath",
                vm => vm.LlmModelPath,
                (vm, v) =>
                {
                    vm.LlmModelPath = v;
                    vm.SelectedLlmLmKitModel = LmKitModelCatalog.FindById(vm.LlmLmKitModels, v);
                }
            ),
            Field("autoFocusInputAfterSpeech", vm => vm.AutoFocusInputAfterSpeech, (vm, v) => vm.AutoFocusInputAfterSpeech = v),
            Field("speechSampleCacheMaxMb", vm => vm.SpeechSampleCacheMaxMb, (vm, v) => vm.SpeechSampleCacheMaxMb = v),
        ];

    private static (string Key, BundledField Field)[] ColorFields() =>
        [
            Field("groundBackgroundColor", vm => vm.GroundBackgroundColor, (vm, v) => vm.GroundBackgroundColor = v),
            Field("groundTaxiwayColor", vm => vm.GroundTaxiwayColor, (vm, v) => vm.GroundTaxiwayColor = v),
            Field("groundTaxiLabelColor", vm => vm.GroundTaxiLabelColor, (vm, v) => vm.GroundTaxiLabelColor = v),
            Field("groundRampEdgeColor", vm => vm.GroundRampEdgeColor, (vm, v) => vm.GroundRampEdgeColor = v),
            Field("groundHoldShortColor", vm => vm.GroundHoldShortColor, (vm, v) => vm.GroundHoldShortColor = v),
            Field("groundRunwayFillColor", vm => vm.GroundRunwayFillColor, (vm, v) => vm.GroundRunwayFillColor = v),
            Field("groundRunwayOutlineColor", vm => vm.GroundRunwayOutlineColor, (vm, v) => vm.GroundRunwayOutlineColor = v),
            Field("groundAircraftColor", vm => vm.GroundAircraftColor, (vm, v) => vm.GroundAircraftColor = v),
            Field("groundDatablockTextColor", vm => vm.GroundDatablockTextColor, (vm, v) => vm.GroundDatablockTextColor = v),
            Field("groundBrightness", vm => vm.GroundBrightness, (vm, v) => vm.GroundBrightness = v),
            Field("terminalCommandColor", vm => vm.TerminalCommandColor, (vm, v) => vm.TerminalCommandColor = v),
            Field("terminalResponseColor", vm => vm.TerminalResponseColor, (vm, v) => vm.TerminalResponseColor = v),
            Field("terminalSystemColor", vm => vm.TerminalSystemColor, (vm, v) => vm.TerminalSystemColor = v),
            Field("terminalSayColor", vm => vm.TerminalSayColor, (vm, v) => vm.TerminalSayColor = v),
            Field("terminalPilotSpeechColor", vm => vm.TerminalPilotSpeechColor, (vm, v) => vm.TerminalPilotSpeechColor = v),
            Field("terminalWarningColor", vm => vm.TerminalWarningColor, (vm, v) => vm.TerminalWarningColor = v),
            Field("terminalErrorColor", vm => vm.TerminalErrorColor, (vm, v) => vm.TerminalErrorColor = v),
            Field("terminalChatColor", vm => vm.TerminalChatColor, (vm, v) => vm.TerminalChatColor = v),
            Field("terminalTdlsColor", vm => vm.TerminalTdlsColor, (vm, v) => vm.TerminalTdlsColor = v),
            Field("terminalStripColor", vm => vm.TerminalStripColor, (vm, v) => vm.TerminalStripColor = v),
        ];

    /// <summary>How the window reads one bundled preference from its field, and writes its field from an imported value.</summary>
    private sealed record BundledField(Func<SettingsViewModel, object?> Read, Action<SettingsViewModel, object?> Write);
}
