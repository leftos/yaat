using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.Views;

public partial class SettingsWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<SettingsWindow>();

    /// <summary>Style class marking a button that captures a key combo for a keybind setting.</summary>
    private const string KeyCaptureClass = "key-capture";

    /// <summary>Style class marking a link-style button that opens another section; its Tag is the target section.</summary>
    private const string SectionLinkClass = "section-link";

    /// <summary>Style class on the sidebar rows that are group headers.</summary>
    private const string GroupHeaderClass = "group-header";

    /// <summary>Style class on the row a Settings search lands on in the selected section.</summary>
    private const string SearchHitClass = "search-hit";

    private readonly IFilePickerService _filePicker;

    private readonly UserPreferences _preferences;

    // Where every Import / Export hub opened from this window stages its import, for the window's whole session.
    private readonly SettingsViewModelImportTarget _importTarget;

    private readonly GeneralSection _general = new();

    private readonly CommandVerbsSection _verbs = new();

    private readonly MacrosSection _macros = new();

    private readonly SpeechSection _speech = new();

    private readonly Dictionary<SettingsSectionId, Control> _sections;

    private SettingsSearchResult _search = SettingsSearchResult.Unfiltered("");

    // The sidebar rows on show: every row, or the sections the search matched under their group headers.
    private IReadOnlyList<SettingsNavItem> _navItems = SettingsNavigation.Items;

    // The highlighted row, and the label in it that the search found.
    private Control? _highlighted;

    private Control? _highlightedLabel;

    // Set while the sidebar's rows are swapped for a new search, when the selection changes are this window's own.
    private bool _updatingNav;

    // The input events Settings stops from reaching the other windows while it is open.
    private static readonly Avalonia.Interactivity.RoutedEvent[] BlockedInputEvents =
    [
        PointerPressedEvent,
        PointerReleasedEvent,
        PointerWheelChangedEvent,
        KeyDownEvent,
        KeyUpEvent,
        TextInputEvent,
    ];

    // The windows whose input this window blocks while it is open, unblocked when it closes.
    private readonly List<Window> _blockedWindows = [];

    // The one handler added to and removed from every blocked window.
    private readonly EventHandler<Avalonia.Interactivity.RoutedEventArgs> _blockInput;

    // The class handlers that stop the tap gestures in the blocked windows while this window is open, disposed when it closes.
    private readonly List<IDisposable> _gestureBlocks = [];

    // The standalone window (the designer, XamlLoader, the guide's screenshots): it has no caller to hand it preferences or
    // a favorites store, so it builds its own.
    public SettingsWindow()
        : this(new UserPreferences(), audioCapture: null, speechSampleStore: null, new FavoriteStore(FavoriteStore.DefaultRootDir)) { }

    /// <summary>Builds the window over the preferences it edits.</summary>
    /// <param name="preferences">The preferences OK and Apply write.</param>
    /// <param name="audioCapture">The microphone capture the audio sections list devices from; null when there is none.</param>
    /// <param name="speechSampleStore">The store of saved speech samples; null when there is none.</param>
    /// <param name="favorites">The favorites store a favorites import from the Import / Export hub changes on OK or Apply.</param>
    public SettingsWindow(
        UserPreferences preferences,
        AudioCaptureService? audioCapture,
        SpeechSampleStore? speechSampleStore,
        FavoriteStore favorites
    )
    {
        InitializeComponent();
        _filePicker = FilePickerFactory.Create(this);
        _preferences = preferences;
        _blockInput = OnBlockedInput;

        var vm = new SettingsViewModel(preferences, audioCapture, speechSampleStore);
        ViewModel = vm;
        DataContext = vm;
        _importTarget = new SettingsViewModelImportTarget(vm, favorites);

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

        SectionNav.ItemsSource = _navItems;
        SectionNav.ContainerPrepared += OnNavContainerPrepared;
        SectionNav.SelectionChanged += OnNavSelectionChanged;
        SettingsSearchBox.TextChanged += OnSearchTextChanged;
        SettingsSearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SelectSection(SettingsSectionId.General);
    }

    /// <summary>The settings this window edits; also its DataContext and every section's.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Shows <paramref name="id"/> in the content pane and selects its sidebar row. When a search has filtered the
    /// section out of the sidebar, the search is cleared first.
    /// </summary>
    public void SelectSection(SettingsSectionId id)
    {
        if (NavRowFor(id) is null)
        {
            // TextChanged arrives after this method returns, so the sidebar is restored here; the event then finds the
            // search already cleared and does nothing.
            SettingsSearchBox.Text = "";
            ApplySearch("");
        }

        SectionNav.SelectedItem = NavRowFor(id);
    }

    /// <summary>The view showing <paramref name="id"/>, whether or not it is the section on show.</summary>
    public Control SectionView(SettingsSectionId id) => _sections[id];

    private SettingsNavItem? NavRowFor(SettingsSectionId id) => _navItems.FirstOrDefault(item => item.Id == id);

    // The Keys section's keybind rows come from an item template, so they have no controls until the section has been laid
    // out in the open window. It is laid out once here, before the section on show is put back, so IsShown (which calls
    // FindLabel on the live controls) sees the generated Keys rows before the section is first opened; they stay when it
    // is swapped out.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        BlockOtherWindows();
        object? shown = SectionHost.Content;
        if (!ReferenceEquals(_sections[SettingsSectionId.Keys], shown))
        {
            SectionHost.Content = _sections[SettingsSectionId.Keys];
            UpdateLayout();
        }

        SectionHost.Content = shown;
        UpdateLayout();
    }

    // Each section holds the window's view model itself, so a section keeps its bindings while another is shown.
    private Dictionary<SettingsSectionId, Control> CreateSections(SettingsViewModel vm)
    {
        var sections = new Dictionary<SettingsSectionId, Control>
        {
            [SettingsSectionId.General] = _general,
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
        _general.ImportExportButton.Click += (_, _) => OpenImportExport([], ImportExportTab.Export, selectedMacros: null);
        _macros.ImportMacrosButton.Click += (_, _) => OpenImportExport([SettingsItemType.Macros], ImportExportTab.Import, selectedMacros: null);
        _macros.ExportMacrosButton.Click += (_, _) => OpenImportExport([SettingsItemType.Macros], ImportExportTab.Export, selectedMacros: null);
        _macros.ExportSelectedMacrosButton.Click += (_, _) =>
            OpenImportExport([SettingsItemType.Macros], ImportExportTab.Export, selectedMacros: SelectedMacros());
        _macros.MacroDataGrid.SelectionChanged += (_, _) =>
            _macros.ExportSelectedMacrosButton.IsEnabled = _macros.MacroDataGrid.SelectedItems.Count > 0;
        _macros.BrowseCrcAliasDirectoryButton.Click += OnBrowseCrcAliasDirectoryClick;
        _verbs.ImportVerbsButton.Click += (_, _) => OpenImportExport([SettingsItemType.Verbs], ImportExportTab.Import, selectedMacros: null);
        _verbs.ExportVerbsButton.Click += (_, _) => OpenImportExport([SettingsItemType.Verbs], ImportExportTab.Export, selectedMacros: null);
        _speech.BrowseLlmModelButton.Click += OnBrowseLlmModelClick;
    }

    // Drops whatever an import staged and was never applied: the staged copy of the favorites goes with it. Every close
    // (OK, Cancel, the title bar, or a close after a failed Apply) comes through here, so the other windows always come back.
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            UnblockOtherWindows();
            _importTarget.Dispose();
        }
        finally
        {
            base.OnClosed(e);
        }
    }

    // Settings is modal over every YAAT window, pop-outs included: each window open now gets no pointer or key input while
    // it is open, and a press on one brings Settings forward. The windows are not disabled, so they keep their normal look
    // and the live preview shows the colours as they will be. The windows Settings opens itself (the hub, file dialogs,
    // confirmations) open later, so they stay usable.
    //
    // The tap gestures (Tapped, DoubleTapped, RightTapped, Holding) are raised once the pointer press has finished routing,
    // handled or not, as bubble-only events the tunnel handler never sees, so they are stopped by class handlers that mark
    // them handled at the source element, ahead of the element's own handler (a double-tap on a popped-out aircraft list
    // would otherwise open the Flight Plan Editor).
    private void BlockOtherWindows()
    {
        IReadOnlyList<Window> open = OpenWindows.All;
        if (open.Count == 0)
        {
            Log.LogDebug("No windows registered; Settings is not modal over other windows");
        }

        foreach (Window window in open)
        {
            if (ReferenceEquals(window, this))
            {
                continue;
            }

            // Listed first so a handler added before a throw mid-loop is still removed on close.
            _blockedWindows.Add(window);
            foreach (Avalonia.Interactivity.RoutedEvent routedEvent in BlockedInputEvents)
            {
                window.AddHandler(routedEvent, _blockInput, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            }
        }

        const Avalonia.Interactivity.RoutingStrategies bubble = Avalonia.Interactivity.RoutingStrategies.Bubble;
        _gestureBlocks.Add(TappedEvent.AddClassHandler<Avalonia.Interactivity.Interactive>(OnBlockedGesture, bubble, handledEventsToo: true));
        _gestureBlocks.Add(DoubleTappedEvent.AddClassHandler<Avalonia.Interactivity.Interactive>(OnBlockedGesture, bubble, handledEventsToo: true));
        _gestureBlocks.Add(RightTappedEvent.AddClassHandler<Avalonia.Interactivity.Interactive>(OnBlockedGesture, bubble, handledEventsToo: true));
        _gestureBlocks.Add(HoldingEvent.AddClassHandler<Avalonia.Interactivity.Interactive>(OnBlockedGesture, bubble, handledEventsToo: true));
    }

    private void UnblockOtherWindows()
    {
        foreach (IDisposable gestureBlock in _gestureBlocks)
        {
            gestureBlock.Dispose();
        }

        _gestureBlocks.Clear();

        foreach (Window window in _blockedWindows)
        {
            foreach (Avalonia.Interactivity.RoutedEvent routedEvent in BlockedInputEvents)
            {
                window.RemoveHandler(routedEvent, _blockInput);
            }
        }

        _blockedWindows.Clear();
    }

    // Runs for every element a tap gesture routes through, in every window: only the gestures in a blocked window are stopped.
    private void OnBlockedGesture(Avalonia.Interactivity.Interactive target, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((TopLevel.GetTopLevel(target) is Window window) && _blockedWindows.Contains(window))
        {
            e.Handled = true;
        }
    }

    private void OnBlockedInput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (e.RoutedEvent == PointerPressedEvent)
        {
            this.RestoreAndActivate();
        }
    }

    // The macro rows selected in the grid, in grid order, trimmed, without the rows lacking a name or an expansion (as
    // SettingsViewModel.ExportMacros leaves out of an export of every macro).
    private List<SavedMacro> SelectedMacros() =>
        [
            .. ViewModel
                .MacroRows.Where(r => _macros.MacroDataGrid.SelectedItems.Contains(r))
                .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
                .Select(r => new SavedMacro { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() }),
        ];

    // The hub opened from Settings stages an import in this window (OK or Apply commits it, Cancel drops it) and exports
    // what the window shows, unsaved edits included; a macros export holds only selectedMacros when it is not null.
    private async void OpenImportExport(SettingsItemType[] preselected, ImportExportTab initialTab, IReadOnlyList<SavedMacro>? selectedMacros)
    {
        try
        {
            var hub = new ImportExportWindow(
                _preferences,
                _importTarget,
                new SettingsViewModelExportSource(ViewModel, _preferences, _importTarget, selectedMacros),
                new HashSet<SettingsItemType>(preselected),
                initialTab
            );
            await hub.ShowOverAsync(this);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Settings' Import / Export failed");
        }
    }

    // Group headers are labels: they take no pointer or focus, so only a section row can be selected. The row comes
    // from the container's own item, since a search shows only some of the rows.
    private void OnNavContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        Control container = e.Container;
        if (container is not ContentControl { Content: SettingsNavItem item })
        {
            throw new InvalidOperationException(
                $"Settings sidebar row {e.Index} holds {(container as ContentControl)?.Content}, not a SettingsNavItem"
            );
        }

        container.Classes.Set(GroupHeaderClass, item.IsHeader);
        container.Focusable = !item.IsHeader;
        container.IsHitTestVisible = !item.IsHeader;
        string name = item.MatchCount switch
        {
            null => item.Title,
            1 => $"{item.Title}, 1 match",
            { } count => $"{item.Title}, {count} matches",
        };
        AutomationProperties.SetName(container, name);
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
        if (_updatingNav)
        {
            return;
        }

        if (SectionNav.SelectedItem is SettingsNavItem { Id: { } id })
        {
            ShowSection(id);
            return;
        }

        // A group header (or nothing) became selected: put the selection back on the section it left.
        SettingsSectionId previous =
            e.RemovedItems.OfType<SettingsNavItem>().FirstOrDefault(removed => !removed.IsHeader)?.Id ?? ViewModel.SelectedSection;
        if (NavRowFor(previous) is { } row)
        {
            Dispatcher.UIThread.Post(() => SectionNav.SelectedItem = row);
        }
    }

    private void ShowSection(SettingsSectionId id)
    {
        ClearHighlight();
        SectionTitle.Text = SettingsNavigation.ItemFor(id).Title;
        SectionHost.Content = _sections[id];
        ViewModel.SelectedSection = id;

        // Putting a section back in the window can rebuild the rows of its item templates on the next layout pass, so in the
        // open window the section is laid out first and the match is found among the rows on screen.
        if (IsVisible)
        {
            UpdateLayout();
        }

        if (_search.FirstMatchIn(id) is { } match)
        {
            HighlightMatch(_sections[id], match);
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        string query = SettingsSearchBox.Text ?? "";
        if (query != _search.Query)
        {
            ApplySearch(query);
        }
    }

    // Escape with a query clears it and stops there, so the Cancel button does not close the window; with no query it
    // reaches Cancel as before. Enter never reaches OK: it moves to the highlighted setting, or stays in the box.
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Escape) && !string.IsNullOrEmpty(SettingsSearchBox.Text))
        {
            SettingsSearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FocusTargetOfHighlight()?.Focus();
        }
    }

    // The control Enter moves to: the first focusable control from the highlighted label on (the checkbox or button
    // itself, or the input beside or below a text label).
    private Control? FocusTargetOfHighlight()
    {
        if ((_highlightedLabel is null) || (SectionHost.Content is not Control section))
        {
            return null;
        }

        List<Control> controls = [.. section.GetLogicalDescendants().OfType<Control>()];
        return controls
            .Skip(controls.IndexOf(_highlightedLabel))
            .FirstOrDefault(control => control.Focusable && control.IsEffectivelyEnabled && control.IsEffectivelyVisible);
    }

    // Filters the sidebar to the sections the query matches, counting only settings on show. The section on show stays
    // selected while it matches, and otherwise the first matching section opens; with no match at all the content pane
    // keeps its section and the sidebar says so.
    private void ApplySearch(string query)
    {
        ClearHighlight();
        _search = SettingsSearch.Run(query, [.. SettingsSearchCatalog.Entries.Where(IsShown)]);
        _navItems = _search.NavItems();
        NoMatchText.IsVisible = _search.IsFiltered && (_search.Matches.Count == 0);
        SettingsNavItem? row = NavRowFor(ViewModel.SelectedSection) ?? _navItems.FirstOrDefault(item => !item.IsHeader);

        _updatingNav = true;
        try
        {
            SectionNav.ItemsSource = _navItems;
            SectionNav.SelectedItem = row;
        }
        finally
        {
            _updatingNav = false;
        }

        if (row?.Id is { } id)
        {
            ShowSection(id);
        }
    }

    // Lights up the match's row, opening any group that hides it, and scrolls it into view once laid out.
    private void HighlightMatch(Control section, SettingsSearchEntry match)
    {
        if (FindLabel(section, match) is not { } label)
        {
            Log.LogWarning("Settings search: no control labelled {Label} in the {Section} section", match.Label, match.Section);
            return;
        }

        foreach (Expander group in label.GetLogicalAncestors().OfType<Expander>())
        {
            group.SetCurrentValue(Expander.IsExpandedProperty, true);
        }

        Control row = RowOf(label);
        row.Classes.Add(SearchHitClass);
        _highlighted = row;
        _highlightedLabel = label;
        Dispatcher.UIThread.Post(() => row.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void ClearHighlight()
    {
        _highlighted?.Classes.Remove(SearchHitClass);
        _highlighted = null;
        _highlightedLabel = null;
    }

    // A setting counts only while its control is shown: its label and every parent up to the section are visible. A
    // collapsed Expander does not hide its content this way, so a setting in one still counts and its group opens.
    private bool IsShown(SettingsSearchEntry entry)
    {
        Control section = _sections[entry.Section];
        for (Control? control = FindLabel(section, entry); control is not null; control = control.GetLogicalParent() as Control)
        {
            if (!control.IsVisible)
            {
                return false;
            }

            if (ReferenceEquals(control, section))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The control in <paramref name="section"/> showing <paramref name="entry"/>'s label: the first after its heading
    /// when it has one, and for a link the section-link button itself. Null when the section shows no such label.
    /// </summary>
    public static Control? FindLabel(Control section, SettingsSearchEntry entry)
    {
        List<Control> controls = [.. section.GetLogicalDescendants().OfType<Control>()];
        int start = 0;
        if (entry.Within is { } within)
        {
            start = controls.FindIndex(control => TextOf(control) == within) + 1;
            if (start == 0)
            {
                return null;
            }
        }

        return controls
            .Skip(start)
            .FirstOrDefault(control => (TextOf(control) == entry.Label) && (!entry.IsLink || control.Classes.Contains(SectionLinkClass)));
    }

    private static string? TextOf(Control control) =>
        control switch
        {
            TextBlock text => text.Text,
            ContentControl { Content: string content } => content,
            _ => null,
        };

    /// <summary>
    /// The row a search highlights for <paramref name="label"/>: a text label on one line with its control lights up
    /// with it; any other label (a checkbox, a button) lights up alone.
    /// </summary>
    public static Control RowOf(Control label) =>
        ((label is TextBlock) && (label.Parent is StackPanel { Orientation: Orientation.Horizontal } row)) ? row : label;

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
}
