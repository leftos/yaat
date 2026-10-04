using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.Views;

public partial class SettingsWindow : Window
{
    private static readonly FilePickerFilter MacroFileType = new("YAAT Macros", ["*.yaat-macros.json"]);

    private static readonly FilePickerFilter VerbsFileType = new("YAAT Command Verbs", ["*" + CommandSchemeFile.Extension]);

    private static readonly FilePickerFilter JsonFileType = new("JSON Files", ["*.json"]);

    private static readonly ILogger Log = AppLog.CreateLogger<SettingsWindow>();

    /// <summary>Style class marking a button that captures a key combo for a keybind setting.</summary>
    private const string KeyCaptureClass = "key-capture";

    /// <summary>Style class marking a link-style button that opens another section; its Tag is the target section.</summary>
    private const string SectionLinkClass = "section-link";

    /// <summary>Style class on the sidebar rows that are group headers.</summary>
    private const string GroupHeaderClass = "group-header";

    private readonly IFilePickerService _filePicker;

    private readonly CommandVerbsSection _verbs = new();

    private readonly MacrosSection _macros = new();

    private readonly SpeechSection _speech = new();

    private readonly Dictionary<SettingsSectionId, Control> _sections;

    public SettingsWindow()
        : this(new UserPreferences(), audioCapture: null, speechSampleStore: null) { }

    public SettingsWindow(UserPreferences preferences)
        : this(preferences, audioCapture: null, speechSampleStore: null) { }

    public SettingsWindow(UserPreferences preferences, AudioCaptureService? audioCapture)
        : this(preferences, audioCapture, speechSampleStore: null) { }

    public SettingsWindow(UserPreferences preferences, AudioCaptureService? audioCapture, SpeechSampleStore? speechSampleStore)
    {
        InitializeComponent();
        _filePicker = FilePickerFactory.Create(this);

        var vm = new SettingsViewModel(preferences, audioCapture, speechSampleStore);
        ViewModel = vm;
        DataContext = vm;

        // Fire-and-forget by design: the model pickers and GPU panel fill in a moment later so the
        // window opens immediately. LoadModelCatalogsAsync handles its own failures and never throws,
        // so there is no unobserved exception to chase here.
        _ = vm.LoadModelCatalogsAsync();

        new WindowGeometryHelper(this, preferences, "Settings", 1040, 720).Restore();

        _sections = CreateSections(vm);
        WireButtons();

        // Every button carrying the key-capture class takes part, wherever it sits in the window. The capture
        // listens on the tunnel, ahead of OK (the default button) and Cancel, so Enter or Escape is captured
        // as a key rather than closing the window.
        AddHandler(KeyDownEvent, OnKeyCaptureKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnKeyCaptureLostFocus);
        AddHandler(Button.ClickEvent, OnSectionLinkClick);

        SectionNav.ContainerPrepared += OnNavContainerPrepared;
        SectionNav.SelectionChanged += OnNavSelectionChanged;
        SelectSection(SettingsSectionId.General);
    }

    /// <summary>The settings this window edits; also its DataContext and every section's.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>Shows <paramref name="id"/> in the content pane and selects its sidebar row.</summary>
    public void SelectSection(SettingsSectionId id) => SectionNav.SelectedItem = SettingsNavigation.ItemFor(id);

    // Each section holds the window's view model itself, so a section keeps its bindings while another is shown.
    private Dictionary<SettingsSectionId, Control> CreateSections(SettingsViewModel vm)
    {
        var sections = new Dictionary<SettingsSectionId, Control>
        {
            [SettingsSectionId.General] = new GeneralSection(),
            [SettingsSectionId.Appearance] = new AppearanceSection(),
            [SettingsSectionId.ScenarioDefaults] = new ScenarioDefaultsSection(),
            [SettingsSectionId.Radar] = new RadarSection(),
            [SettingsSectionId.Ground] = new GroundSection(),
            [SettingsSectionId.AircraftList] = new AircraftListSection(),
            [SettingsSectionId.StripsAndTdls] = new StripsAndTdlsSection(),
            [SettingsSectionId.Terminal] = new TerminalSection(),
            [SettingsSectionId.CommandInput] = new CommandInputSection(),
            [SettingsSectionId.CommandVerbs] = _verbs,
            [SettingsSectionId.Macros] = _macros,
            [SettingsSectionId.Keys] = new KeysSection(),
            [SettingsSectionId.Speech] = _speech,
            [SettingsSectionId.AudioDevices] = new AudioDevicesSection(),
            [SettingsSectionId.ServerAdmin] = new ServerAdminSection(),
        };

        foreach (Control section in sections.Values)
        {
            section.DataContext = vm;
        }

        return sections;
    }

    private void WireButtons()
    {
        OkButton.Click += OnOkClick;
        ApplyButton.Click += OnApplyClick;
        CancelButton.Click += OnCancelClick;
        _macros.ImportMacrosButton.Click += OnImportMacrosClick;
        _macros.ExportSelectedMacrosButton.Click += OnExportSelectedClick;
        _macros.ExportAllMacrosButton.Click += OnExportAllClick;
        _macros.BrowseCrcAliasDirectoryButton.Click += OnBrowseCrcAliasDirectoryClick;
        _verbs.ImportVerbsButton.Click += OnImportVerbsClick;
        _verbs.ExportVerbsButton.Click += OnExportVerbsClick;
        _speech.BrowseLlmModelButton.Click += OnBrowseLlmModelClick;
    }

    // Group headers are labels: they take no pointer or focus, so only a section row can be selected.
    private void OnNavContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        SettingsNavItem item = SettingsNavigation.Items[e.Index];
        Control container = e.Container;
        container.Classes.Set(GroupHeaderClass, item.IsHeader);
        container.Focusable = !item.IsHeader;
        container.IsHitTestVisible = !item.IsHeader;
        AutomationProperties.SetName(container, item.Title);
        if (item.Id is { } id)
        {
            AutomationProperties.SetAutomationId(container, $"SettingsNav.{id}");
        }
        else
        {
            container.ClearValue(AutomationProperties.AutomationIdProperty);
        }
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SectionNav.SelectedItem is SettingsNavItem { Id: { } id } item)
        {
            SectionTitle.Text = item.Title;
            SectionHost.Content = _sections[id];
            ViewModel.SelectedSection = id;
            return;
        }

        // A group header (or nothing) became selected: put the selection back on the section it left.
        SettingsNavItem previous =
            e.RemovedItems.OfType<SettingsNavItem>().FirstOrDefault(removed => !removed.IsHeader)
            ?? SettingsNavigation.ItemFor(SettingsSectionId.General);
        Dispatcher.UIThread.Post(() => SectionNav.SelectedItem = previous);
    }

    private void OnSectionLinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((e.Source is Button button) && button.Classes.Contains(SectionLinkClass) && (button.Tag is SettingsSectionId target))
        {
            SelectSection(target);
            e.Handled = true;
        }
    }

    private static bool IsKeyCaptureButton(object? source) => (source is Button button) && button.Classes.Contains(KeyCaptureClass);

    private async void OnBrowseLlmModelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        string? path = await _filePicker.OpenFileAsync(
            new OpenFileOptions("Select LLM Model File (GGUF)", [new FilePickerFilter("GGUF Models", ["*.gguf"])])
        );
        if (!string.IsNullOrEmpty(path))
        {
            vm.LlmModelPath = path;
        }
    }

    private void OnOkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.ApplyCommand.Execute(null);
        }

        Close();
    }

    private void OnApplyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.ApplyCommand.Execute(null);
        }
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async void OnBrowseCrcAliasDirectoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        string? folder = await _filePicker.OpenFolderAsync(new OpenFolderOptions("Select CRC Aliases Folder"));
        if (folder is not null)
        {
            vm.CrcAliasDirectory = folder;
        }
    }

    private async void OnImportMacrosClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        string? path = await _filePicker.OpenFileAsync(new OpenFileOptions("Import Macros", [MacroFileType, JsonFileType]));
        if (path is null)
        {
            return;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            List<SavedMacro>? macros = await JsonSerializer.DeserializeAsync<List<SavedMacro>>(stream, UserPreferences.JsonOptions);
            if (macros is null || macros.Count == 0)
            {
                return;
            }

            var existingBaseNames = new HashSet<string>(
                vm.MacroRows.Select(r => MacroDefinition.ExtractBaseName(r.Name)),
                StringComparer.OrdinalIgnoreCase
            );

            var newMacros = new List<SavedMacro>();
            var conflicts = new List<MacroImportItem>();

            foreach (SavedMacro m in macros)
            {
                string baseName = MacroDefinition.ExtractBaseName(m.Name);
                if (existingBaseNames.Contains(baseName))
                {
                    MacroRow existingRow = vm.MacroRows.First(r =>
                        string.Equals(MacroDefinition.ExtractBaseName(r.Name), baseName, StringComparison.OrdinalIgnoreCase)
                    );

                    // Generate a default rename suggestion
                    string renameCandidate = GenerateRenameSuggestion(baseName, existingBaseNames, macros);

                    conflicts.Add(
                        new MacroImportItem
                        {
                            Macro = m,
                            ExistingExpansion = existingRow.Expansion,
                            RenamedName = renameCandidate,
                        }
                    );
                }
                else
                {
                    newMacros.Add(m);
                }
            }

            if (conflicts.Count == 0)
            {
                // No conflicts — import all directly
                vm.ImportMacros(new MacroImportResult { NewMacros = newMacros, Conflicts = [] });
                vm.MacroImportNote = "";
                vm.MacroImportIsError = false;
                return;
            }

            var importWindow = new MacroImportWindow(conflicts, newMacros, existingBaseNames);
            MacroImportResult? result = await DialogPresenter.ShowModalAsync<MacroImportResult?>(importWindow, this);
            if (result is not null)
            {
                vm.ImportMacros(result);
                vm.MacroImportNote = "";
                vm.MacroImportIsError = false;
            }
        }
        catch (Exception ex) when ((ex is JsonException) || (ex is IOException))
        {
            Log.LogWarning(ex, "Could not import macros from {Path}", path);
            vm.MacroImportNote = "Could not read that file as a macro file.";
            vm.MacroImportIsError = true;
        }
    }

    private async void OnImportVerbsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        string? path = await _filePicker.OpenFileAsync(new OpenFileOptions("Import Command Verbs", [VerbsFileType, JsonFileType]));
        if (path is null)
        {
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path);
            vm.ImportVerbs(CommandSchemeFile.Deserialize(json));
        }
        catch (Exception ex) when ((ex is JsonException) || (ex is IOException))
        {
            Log.LogWarning(ex, "Could not import command verbs from {Path}", path);
            vm.VerbImportNote = "Could not read that file as a command verb file.";
            vm.VerbImportIsError = true;
        }
    }

    private async void OnExportVerbsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        string? path = await _filePicker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Export Command Verbs",
                SuggestedFileName: "yaat-command-verbs" + CommandSchemeFile.Extension,
                Filters: [VerbsFileType],
                DefaultExtension: CommandSchemeFile.Extension.TrimStart('.')
            )
        );

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, CommandSchemeFile.Serialize(vm.ExportVerbs()));
            vm.VerbImportNote = $"Exported to {Path.GetFileName(path)}.";
            vm.VerbImportIsError = false;
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Could not export command verbs to {Path}", path);
            vm.VerbImportNote = "Could not write that file.";
            vm.VerbImportIsError = true;
        }
    }

    private static string GenerateRenameSuggestion(string baseName, HashSet<string> existingBaseNames, List<SavedMacro> incomingMacros)
    {
        var incomingBaseNames = new HashSet<string>(
            incomingMacros.Select(m => MacroDefinition.ExtractBaseName(m.Name)),
            StringComparer.OrdinalIgnoreCase
        );

        for (int i = 2; i < 100; i++)
        {
            string candidate = $"{baseName}_{i}";
            if (!existingBaseNames.Contains(candidate) && !incomingBaseNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}_renamed";
    }

    private async void OnExportSelectedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var selected = _macros.MacroDataGrid.SelectedItems.OfType<MacroRow>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        await ExportMacrosAsync(vm.ExportMacros(selected));
    }

    private async void OnExportAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        List<SavedMacro> all = vm.ExportMacros();
        if (all.Count == 0)
        {
            return;
        }

        await ExportMacrosAsync(all);
    }

    private void OnKeyCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsKeyCaptureButton(e.Source) && (DataContext is SettingsViewModel vm) && vm.IsCapturingKey)
        {
            vm.CaptureKey(e.Key, e.KeyModifiers);
            e.Handled = true;
        }
    }

    private void OnKeyCaptureLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IsKeyCaptureButton(e.Source) && (DataContext is SettingsViewModel vm))
        {
            vm.CancelKeyCapture();
        }
    }

    private async Task ExportMacrosAsync(List<SavedMacro> macros)
    {
        string? path = await _filePicker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Export Macros",
                SuggestedFileName: "macros.yaat-macros.json",
                Filters: [MacroFileType, JsonFileType],
                DefaultExtension: "yaat-macros.json"
            )
        );

        if (path is null)
        {
            return;
        }

        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, macros, UserPreferences.JsonOptions);
    }
}
