using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Source checks on the Settings window. Every binding and every named control the tabbed window had
/// still exists somewhere in <c>SettingsWindow.axaml</c> or the section views under <c>Views/Settings/</c>,
/// so moving the window to a sidebar of sections drops no setting.
/// </summary>
public partial class SettingsWindowSourceTests
{
    // Each "Attribute={Binding ...}" of the tabbed window, once per occurrence, whitespace collapsed.
    // Bindings to another element (ElementName) were tab plumbing and are not settings. The per-page reset
    // buttons (command verbs, macros, colours) are left out: the footer's Reset section replaced them.
    private static readonly string[] ExpectedBindings =
    [
        "Text={Binding UserInitials}",
        "IsChecked={Binding DiscordRichPresenceEnabled}",
        "IsChecked={Binding SoloTrainingMode}",
        "Value={Binding SoloGoAroundProbabilityPercent}",
        "Text={Binding SoloGoAroundProbabilityPercent, StringFormat='{}{0}%'}",
        "Value={Binding SoloParkingInitialCallupIntervalSeconds}",
        "Text={Binding SoloParkingInitialCallupIntervalLabel}",
        "Value={Binding SoloArrivalGeneratorRatePercent}",
        "Text={Binding SoloArrivalGeneratorRatePercent, StringFormat='{}{0}%'}",
        "IsChecked={Binding AutoAcceptEnabled}",
        "IsVisible={Binding AutoAcceptEnabled}",
        "Value={Binding AutoAcceptDelaySeconds}",
        "Value={Binding CommandRunDelayMinSeconds}",
        "Value={Binding CommandRunDelayMaxSeconds}",
        "ItemsSource={Binding AutoDeleteOptions}",
        "SelectedIndex={Binding SelectedAutoDeleteIndex}",
        "Value={Binding DepartureAutoDeleteDistanceNm}",
        "IsChecked={Binding ValidateDctFixes}",
        "IsChecked={Binding AutoClearedToLandGnd}",
        "IsChecked={Binding AutoClearedToLandTwr}",
        "IsChecked={Binding AutoClearedToLandApp}",
        "IsChecked={Binding AutoClearedToLandCtr}",
        "IsChecked={Binding AutoCrossRunway}",
        "IsChecked={Binding AutoPullUpToParallel}",
        "IsChecked={Binding AutoGoAroundOnOccupiedRunway}",
        "IsChecked={Binding AutoRejectTakeoffOnOccupiedRunway}",
        "IsChecked={Binding AutoArrivalSpacingOnOccupiedRunwayGnd}",
        "IsChecked={Binding AutoArrivalSpacingOnOccupiedRunwayTwr}",
        "ItemsSource={Binding VfrCommandsForIfrOptions}",
        "SelectedIndex={Binding SelectedVfrCommandsForIfrIndex}",
        "IsChecked={Binding RpoShowPilotSpeech}",
        "IsChecked={Binding RpoPilotSpeechAudibleAlert}",
        "Value={Binding DataGridFontSize}",
        "Value={Binding RadarDatablockFontSize}",
        "Value={Binding RadarFlyoutFontSize}",
        "Value={Binding GroundDatablockFontSize}",
        "Value={Binding GroundLabelFontSize}",
        "Value={Binding TerminalFontSize}",
        "Value={Binding InterfaceFontSize}",
        "Value={Binding StripsZoomPercent}",
        "Value={Binding TdlsZoomPercent}",
        "IsVisible={Binding IsMacOs}",
        "ItemsSource={Binding RendererModeOptions}",
        "SelectedIndex={Binding SelectedRendererModeIndex}",
        "ItemsSource={Binding SignatureHelpPlacementOptions}",
        "SelectedIndex={Binding SelectedSignatureHelpPlacementIndex}",
        "IsChecked={Binding AutoExpandSuggestionOnEnter}",
        "IsChecked={Binding EuroScopeMode}",
        "IsChecked={Binding FlashNoLandingClearance}",
        "IsChecked={Binding ShowConflictAlerts}",
        "IsChecked={Binding ShowTypeMismatchHints}",
        "IsChecked={Binding ShowAtpa}",
        "IsChecked={Binding SyncStudentDatablockColors}",
        "IsChecked={Binding MarkStudentLimitedDatablocks}",
        "IsChecked={Binding CollapseStudentDatablocks}",
        "IsChecked={Binding SyncStudentLeaderDirection}",
        "IsChecked={Binding GroundHideDataBlocksByDefault}",
        "IsChecked={Binding GroundShowTaxiRouteOnHover}",
        "IsChecked={Binding GroundShowAllTaxiRoutes}",
        "IsChecked={Binding MvaHintDefaultGnd}",
        "IsChecked={Binding MvaHintDefaultTwr}",
        "IsChecked={Binding MvaHintDefaultApp}",
        "IsChecked={Binding MvaHintDefaultCtr}",
        "IsChecked={Binding ShowSpeechBubbles}",
        "IsVisible={Binding ShowSpeechBubbles}",
        "IsChecked={Binding SpeechBubblesStayUntilClicked}",
        "IsVisible={Binding !SpeechBubblesStayUntilClicked}",
        "Value={Binding SpeechBubbleDurationMultiplier}",
        "IsChecked={Binding ShowWarningSpeechBubbles}",
        "IsChecked={Binding AlwaysShowGroundBubblesOnRadar}",
        "Value={Binding TpaConeHalfAngleDegrees}",
        "Value={Binding ScrollSensitivityPercent}",
        "Text={Binding ScrollSensitivityPercent, StringFormat='{}{0:0}%'}",
        "IsChecked={Binding RaiseWindowsTogether}",
        "IsChecked={Binding MainWindowTopmost}",
        "IsChecked={Binding GroundViewTopmost}",
        "IsChecked={Binding RadarViewTopmost}",
        "IsChecked={Binding DataGridTopmost}",
        "IsChecked={Binding TerminalTopmost}",
        "IsChecked={Binding VStripsTopmost}",
        "IsChecked={Binding FavoritesPanelTopmost}",
        "Color={Binding GroundBackgroundColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundTaxiwayColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundTaxiLabelColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundRampEdgeColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundHoldShortColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundRunwayFillColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundRunwayOutlineColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundAircraftColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding GroundDatablockTextColor, Converter={StaticResource HexColorConverter}}",
        "Value={Binding GroundBrightness}",
        "Text={Binding GroundBrightness, StringFormat='{}{0}%'}",
        "Value={Binding GroundSatelliteImageBrightness}",
        "Text={Binding GroundSatelliteImageBrightness, StringFormat='{}{0}%'}",
        "Value={Binding GroundVideoMapOverlayBrightness}",
        "Text={Binding GroundVideoMapOverlayBrightness, StringFormat='{}{0}%'}",
        "Value={Binding GroundYaatLayoutBrightness}",
        "Text={Binding GroundYaatLayoutBrightness, StringFormat='{}{0}%'}",
        "IsChecked={Binding AssignmentTintEnabled}",
        "IsVisible={Binding AssignmentTintEnabled}",
        "Color={Binding AssignmentTintColor, Converter={StaticResource HexColorConverter}}",
        "IsChecked={Binding UnassignedTintEnabled}",
        "IsVisible={Binding UnassignedTintEnabled}",
        "Color={Binding UnassignedTintColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding SelectedColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalCommandColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalResponseColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalSystemColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalSayColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalPilotSpeechColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalWarningColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalErrorColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalChatColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalTdlsColor, Converter={StaticResource HexColorConverter}}",
        "Color={Binding TerminalStripColor, Converter={StaticResource HexColorConverter}}",
        "Text={Binding TestCommandInput}",
        "Text={Binding TestCommandResult}",
        "IsVisible={Binding TestCommandResult, Converter={x:Static StringConverters.IsNotNullOrEmpty}}",
        "Classes.error={Binding TestCommandIsError}",
        "ItemsSource={Binding GroupedVerbMappings}",
        "Binding={Binding CommandName}",
        "Binding={Binding Aliases}",
        "Binding={Binding Example}",
        "Text={Binding CrcAliasDirectory}",
        "Text={Binding CrcAliasDirectoryHint}",
        "Command={Binding AddMacroCommand}",
        "ItemsSource={Binding MacroRows}",
        "Binding={Binding Name}",
        "Binding={Binding Expansion}",
        "Binding={Binding Preview}",
        "Command={Binding RemoveCommand}",
        "ItemsSource={Binding AudioInputDevices}",
        "SelectedItem={Binding SelectedAudioInputDeviceDisplay}",
        "ItemsSource={Binding AudioOutputDevices}",
        "SelectedItem={Binding SelectedAudioOutputDeviceDisplay}",
        "IsExpanded={Binding SpeechEnabled, Mode=OneWay}",
        "IsChecked={Binding SpeechEnabled}",
        "IsChecked={Binding AutoFocusInputAfterSpeech}",
        "ItemsSource={Binding WhisperLmKitModels}",
        "SelectedItem={Binding SelectedWhisperLmKitModel}",
        "Text={Binding DisplayName}",
        "Text={Binding ApproxSizeMb, StringFormat='({0} MB)'}",
        "IsVisible={Binding IsLocallyAvailable}",
        "Text={Binding SelectedWhisperLmKitModel.Description}",
        "Command={Binding DownloadSelectedWhisperModelCommand}",
        "IsEnabled={Binding SelectedWhisperLmKitModel.CanDownload, FallbackValue=False}",
        "Command={Binding DeleteSelectedWhisperModelCommand}",
        "IsEnabled={Binding SelectedWhisperLmKitModel.CanDelete, FallbackValue=False}",
        "Text={Binding SelectedWhisperLmKitModel.StatusMessage}",
        "Value={Binding SelectedWhisperLmKitModel.DownloadProgress}",
        "IsVisible={Binding SelectedWhisperLmKitModel.IsDownloading}",
        "ItemsSource={Binding LlmLmKitModels}",
        "SelectedItem={Binding SelectedLlmLmKitModel}",
        "Text={Binding DisplayName}",
        "Text={Binding ApproxSizeMb, StringFormat='({0} MB)'}",
        "IsVisible={Binding GpuRecommended}",
        "IsVisible={Binding IsLocallyAvailable}",
        "Text={Binding SelectedLlmLmKitModel.Description}",
        "Command={Binding DownloadSelectedLlmModelCommand}",
        "IsEnabled={Binding SelectedLlmLmKitModel.CanDownload, FallbackValue=False}",
        "Command={Binding DeleteSelectedLlmModelCommand}",
        "IsEnabled={Binding SelectedLlmLmKitModel.CanDelete, FallbackValue=False}",
        "Text={Binding SelectedLlmLmKitModel.StatusMessage}",
        "Value={Binding SelectedLlmLmKitModel.DownloadProgress}",
        "IsVisible={Binding SelectedLlmLmKitModel.IsDownloading}",
        "Text={Binding LlmModelPath, StringFormat='Source: {0}'}",
        "IsVisible={Binding LlmModelPath, Converter={x:Static StringConverters.IsNotNullOrEmpty}}",
        "Text={Binding LmKitGpuSnapshot.Summary}",
        "IsVisible={Binding IsCudaBackendSupported}",
        "IsVisible={Binding IsCudaBackendSupported}",
        "Text={Binding CudaBackend.StatusMessage}",
        "IsVisible={Binding CudaBackend.StatusMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}",
        "Value={Binding CudaBackend.Progress}",
        "IsVisible={Binding CudaBackend.IsBusy}",
        "Command={Binding InstallCudaBackendCommand}",
        "IsVisible={Binding !CudaBackend.IsInstalled}",
        "IsEnabled={Binding !CudaBackend.IsBusy}",
        "Command={Binding CancelCudaBackendInstallCommand}",
        "IsVisible={Binding CudaBackend.IsBusy}",
        "Command={Binding UninstallCudaBackendCommand}",
        "IsVisible={Binding CudaBackend.IsInstalled}",
        "IsEnabled={Binding !CudaBackend.IsBusy}",
        "Value={Binding LlmGpuLayers}",
        "Content={Binding PttKeyDisplay}",
        "Command={Binding StartPttKeyCaptureCommand}",
        "IsChecked={Binding SpeechTelemetryEnabled}",
        "IsChecked={Binding SpeechSampleCaptureEnabled}",
        "IsEnabled={Binding !SpeechTelemetryEnabled}",
        "Value={Binding SpeechSampleCacheMaxMb}",
        "Command={Binding OpenSpeechSamplesFolderCommand}",
        "Command={Binding DeleteAllSpeechSamplesCommand}",
        "IsExpanded={Binding PilotVoiceEnabled, Mode=OneWay}",
        "IsChecked={Binding PilotVoiceEnabled}",
        "Value={Binding PilotVoiceVolume}",
        "Text={Binding PilotVoiceVolume, StringFormat='{}{0}%'}",
        "IsChecked={Binding PilotVoiceRadioFxEnabled}",
        "Text={Binding PiperVoice.StatusMessage}",
        "IsVisible={Binding PiperVoice.StatusMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}",
        "Value={Binding PiperVoice.Progress}",
        "IsVisible={Binding PiperVoice.IsBusy}",
        "Command={Binding InstallPiperVoicePackCommand}",
        "IsVisible={Binding !PiperVoice.IsInstalled}",
        "IsEnabled={Binding !PiperVoice.IsBusy}",
        "Command={Binding CancelPiperVoicePackInstallCommand}",
        "IsVisible={Binding PiperVoice.IsBusy}",
        "Command={Binding UninstallPiperVoicePackCommand}",
        "IsVisible={Binding PiperVoice.IsInstalled}",
        "IsEnabled={Binding !PiperVoice.IsBusy}",
        // The Keys section lists its keys from the view model's keybind rows, one capture button per row.
        "ItemsSource={Binding KeysSectionKeybindRows}",
        "Content={Binding Display}",
        "Command={Binding StartCaptureCommand}",
        "IsChecked={Binding IsAdminMode}",
        "IsVisible={Binding IsAdminMode}",
        "Text={Binding AdminPassword}",
    ];

    // Each x:Name of the tabbed window that code wires up (the tab items themselves are left out), with the
    // footer's Save button now OK beside Apply and Reset section.
    private static readonly string[] ExpectedNames =
    [
        "OkButton",
        "ApplyButton",
        "ResetSectionButton",
        "CancelButton",
        "ImportVerbsButton",
        "ExportVerbsButton",
        "BrowseCrcAliasDirectoryButton",
        "ImportMacrosButton",
        "ExportMacrosButton",
        "ExportSelectedMacrosButton",
        "ImportExportButton",
        "MacroDataGrid",
        "BrowseLlmModelButton",
        "PttKeyButton",
    ];

    [Fact]
    public void EveryBindingOfTheTabbedWindowSurvives()
    {
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string text in ReadSettingsAxaml())
        {
            foreach (Match match in BindingAttributeRegex().Matches(text))
            {
                string binding = Normalize(match);
                actual[binding] = actual.GetValueOrDefault(binding) + 1;
            }
        }

        var missing = ExpectedBindings
            .GroupBy(b => b, StringComparer.Ordinal)
            .Where(g => actual.GetValueOrDefault(g.Key) < g.Count())
            .Select(g => $"{g.Key} (expected {g.Count()}, found {actual.GetValueOrDefault(g.Key)})")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryNamedControlOfTheTabbedWindowSurvives()
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (string text in ReadSettingsAxaml())
        {
            foreach (Match match in NameRegex().Matches(text))
            {
                actual.Add(match.Groups[1].Value);
            }
        }

        List<string> missing = [.. ExpectedNames.Where(n => !actual.Contains(n))];
        Assert.Empty(missing);
    }

    [Fact]
    public void EverySpeechActionThatRunsAtOnce_SaysCancelDoesNotUndoIt()
    {
        string speech = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yaat.Client", "Views", "Settings", "SpeechSection.axaml"));

        // Whisper model, LLM model, CUDA runtime, saved samples, Piper voice pack.
        Assert.Equal(5, Regex.Matches(speech, Regex.Escape("Text=\"Takes effect at once; Cancel doesn't undo it.\"")).Count);
    }

    /// <summary>
    /// The session flyout's auto-accept is a checkbox and a 0-60 s delay, not a -1 sentinel, and its two per-position
    /// toggles name the student's position type.
    /// </summary>
    [Fact]
    public void TheSessionFlyout_HasNoMinusOneSentinel_AndBindsThePositionLabels()
    {
        string flyout = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yaat.Client", "Views", "CommandInputView.axaml"));

        Assert.DoesNotContain("Minimum=\"-1\"", flyout);
        Assert.Contains("IsChecked=\"{Binding SessionAutoAcceptEnabled}\"", flyout);
        Assert.Contains("Content=\"{Binding SessionAutoClearedToLandLabel}\"", flyout);
        Assert.Contains("Content=\"{Binding SessionAutoArrivalSpacingLabel}\"", flyout);
    }

    /// <summary>
    /// Every input control a section binds to the view model (a text box, checkbox, slider, number box, colour picker,
    /// combo box selection or command button) has a Settings search entry, so search reaches every setting. The Keys
    /// section's rows come from an item template over the keybind list, so instead of its template each key the section
    /// lists has one entry, in the section's order, labelled as its row is.
    /// </summary>
    [Fact]
    public void EveryBoundControlHasACatalogEntry()
    {
        KeybindDescriptor[] keysSectionKeys = [.. SettingsViewModel.KeybindDescriptors.Where(d => d.InKeysSection)];
        SettingsSearchEntry[] keyRows = [.. SettingsSearchCatalog.Entries.Where(e => (e.Section == SettingsSectionId.Keys) && !e.IsLink)];
        Assert.Equal(keysSectionKeys.Select(d => d.Label), keyRows.Select(e => e.Label));
        Assert.Equal(keysSectionKeys.Select(d => $"Keybind.{d.Id}"), keyRows.Select(e => e.Key));
        Assert.Equal(SettingsSearchCatalog.KeybindRowEntries, keyRows);

        var catalogued = SettingsSearchCatalog
            .Entries.Select(e => e.Key is { } key ? (e.Section, Key: key) : default)
            .Where(pair => pair.Key is not null)
            .ToHashSet();

        var missing = new List<string>();
        foreach ((SettingsSectionId section, XDocument document) in ReadSectionDocuments())
        {
            foreach (XElement element in document.Descendants().Where(e => !IsBindingPlumbing(e)))
            {
                foreach (XAttribute attribute in element.Attributes().Where(a => a.Value.StartsWith("{Binding ", StringComparison.Ordinal)))
                {
                    if (IsPlumbingAttribute(element, attribute))
                    {
                        continue;
                    }

                    string path = BindingPathRegex().Match(attribute.Value).Groups[1].Value;
                    if (!catalogued.Contains((section, path)))
                    {
                        missing.Add($"{section}: {element.Name.LocalName}.{attribute.Name.LocalName} -> {path}");
                    }
                }

                // A button the window wires up in code has no binding; the catalog keys it by its x:Name.
                if (
                    (element.Name.LocalName == "Button")
                    && (element.Attribute(XamlName) is { } name)
                    && !element.Attributes().Any(a => a.Name.LocalName == "Command")
                    && !catalogued.Contains((section, name.Value))
                )
                {
                    missing.Add($"{section}: Button x:Name={name.Value}");
                }
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// Each catalog entry's label (and heading, when set) is text its section shows, the heading comes before the label,
    /// a label that repeats in its section has a heading, a control entry's binding is in the section, and the link
    /// entries are exactly the section's link buttons. The Keys section's rows are generated from the keybind list, so
    /// their labels are not in its source; EveryCatalogEntry_ResolvesToItsOwnControlInItsSection finds them on screen.
    /// </summary>
    [Fact]
    public void EveryCatalogLabelExistsInItsSection()
    {
        var documents = ReadSectionDocuments().ToDictionary(pair => pair.Section, pair => pair.Document);
        var problems = new List<string>();
        foreach (SettingsSearchEntry entry in SettingsSearchCatalog.Entries.Except(SettingsSearchCatalog.KeybindRowEntries))
        {
            string source = documents[entry.Section].ToString();
            string quotedLabel = $"\"{entry.Label}\"";
            int labelAt = source.IndexOf(quotedLabel, StringComparison.Ordinal);
            if (labelAt < 0)
            {
                problems.Add($"{entry.Section}: no \"{entry.Label}\"");
                continue;
            }

            if (entry.Within is { } within)
            {
                int withinAt = source.IndexOf($"\"{within}\"", StringComparison.Ordinal);
                if ((withinAt < 0) || (source.IndexOf(quotedLabel, withinAt, StringComparison.Ordinal) < 0))
                {
                    problems.Add($"{entry.Section}: no \"{entry.Label}\" after heading \"{within}\"");
                }
            }
            else if (source.IndexOf(quotedLabel, labelAt + quotedLabel.Length, StringComparison.Ordinal) >= 0)
            {
                problems.Add($"{entry.Section}: \"{entry.Label}\" repeats in its section but has no heading");
            }

            if (
                (entry.Key is { } key)
                && !Regex.IsMatch(source, $@"\{{Binding {Regex.Escape(key)}[,}}]")
                && !source.Contains($"x:Name=\"{key}\"", StringComparison.Ordinal)
            )
            {
                problems.Add($"{entry.Section}: \"{entry.Label}\" is keyed {key}, which the section neither binds nor names");
            }
        }

        var actualLinks = documents
            .SelectMany(pair => pair.Value.Descendants().Where(IsSectionLink).Select(button => LinkOf(pair.Key, button)))
            .ToHashSet();
        var catalogLinks = SettingsSearchCatalog.Entries.Where(e => e.IsLink).Select(e => (e.Section, e.Label, e.LinkTarget!.Value)).ToHashSet();
        problems.AddRange(catalogLinks.Except(actualLinks).Select(link => $"catalog link {link} has no section-link button"));
        problems.AddRange(actualLinks.Except(catalogLinks).Select(link => $"section-link button {link} has no catalog entry"));

        Assert.Empty(problems);
    }

    // Bindings that are not a setting a user edits: display-only text and progress, the cells of a table (bound to its
    // rows, not the view model), anything inside an item template, and a flyout's content, which is not on screen for the
    // search to light up until its button opens it (the button itself is catalogued).
    private static readonly HashSet<string> PlumbingElements = new(StringComparer.Ordinal) { "TextBlock", "ProgressBar", "DataGridTextColumn" };

    private static readonly HashSet<string> PlumbingContainers = new(StringComparer.Ordinal) { "DataTemplate", "Flyout" };

    // Visibility, enabled and expanded toggles, the item sources of combo boxes and tables, and style classes.
    private static readonly HashSet<string> PlumbingAttributes = new(StringComparer.Ordinal)
    {
        "IsVisible",
        "IsEnabled",
        "IsExpanded",
        "ItemsSource",
        "Classes.error",
    };

    private static bool IsBindingPlumbing(XElement element) =>
        PlumbingElements.Contains(element.Name.LocalName) || element.AncestorsAndSelf().Any(e => PlumbingContainers.Contains(e.Name.LocalName));

    // A key-capture button's content shows the bound key; its command is the binding the catalog keys it by.
    private static bool IsPlumbingAttribute(XElement element, XAttribute attribute) =>
        PlumbingAttributes.Contains(attribute.Name.LocalName)
        || ((element.Name.LocalName == "Button") && (attribute.Name.LocalName == "Content") && HasClass(element, "key-capture"));

    private static readonly XName XamlName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

    private static bool HasClass(XElement element, string styleClass) =>
        ((string?)element.Attribute("Classes"))?.Split(' ').Contains(styleClass) == true;

    private static bool IsSectionLink(XElement element) => (element.Name.LocalName == "Button") && HasClass(element, "section-link");

    private static (SettingsSectionId Section, string Label, SettingsSectionId Target) LinkOf(SettingsSectionId section, XElement button)
    {
        string tag = (string?)button.Attribute("Tag") ?? "";
        Match target = Regex.Match(tag, @"SettingsSectionId\.(\w+)\}");
        return (section, (string?)button.Attribute("Content") ?? "", Enum.Parse<SettingsSectionId>(target.Groups[1].Value));
    }

    private static IEnumerable<(SettingsSectionId Section, XDocument Document)> ReadSectionDocuments()
    {
        string sections = Path.Combine(FindRepoRoot(), "src", "Yaat.Client", "Views", "Settings");
        return Enum.GetValues<SettingsSectionId>().Select(id => (id, XDocument.Load(Path.Combine(sections, $"{id}Section.axaml"))));
    }

    private static string Normalize(Match match) => $"{match.Groups[1].Value}={WhitespaceRegex().Replace(match.Groups[2].Value, " ")}";

    private static List<string> ReadSettingsAxaml()
    {
        string views = Path.Combine(FindRepoRoot(), "src", "Yaat.Client", "Views");
        var texts = new List<string> { File.ReadAllText(Path.Combine(views, "SettingsWindow.axaml")) };
        string sections = Path.Combine(views, "Settings");
        if (Directory.Exists(sections))
        {
            texts.AddRange(Directory.GetFiles(sections, "*.axaml").Select(File.ReadAllText));
        }

        return texts;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "yaat.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"yaat.slnx not found above {AppContext.BaseDirectory}");
    }

    [GeneratedRegex(@"([\w.:]+)=""(\{Binding[^""]*\})""")]
    private static partial Regex BindingAttributeRegex();

    [GeneratedRegex(@"x:Name=""(\w+)""")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\{Binding ([^,}]+)")]
    private static partial Regex BindingPathRegex();
}
