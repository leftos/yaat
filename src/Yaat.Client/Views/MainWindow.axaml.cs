using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.VStrips;
using Yaat.Client.Views.VTdls;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Views;

public partial class MainWindow : Window
{
    private readonly WindowGeometryHelper _geometryHelper;
    private readonly WindowProfileService _windowProfileService;
    private TerminalWindow? _terminalWindow;
    private DataGridWindow? _dataGridWindow;
    private GroundViewWindow? _groundViewWindow;
    private RadarViewWindow? _radarViewWindow;
    private ControllersWindow? _controllersWindow;
    private MetarWindow? _metarWindow;
    private WeatherTimelineEditorWindow? _weatherEditorWindow;
    private ArrivalGeneratorsEditorWindow? _arrivalGeneratorsEditorWindow;

    // Test hooks — read-only views of the subordinate windows the MainWindow creates
    // in response to IsDataGridPoppedOut / IsGroundViewPoppedOut / IsRadarViewPoppedOut
    // flipping. Not used in production; present so headless lifecycle tests can assert
    // a pop-out actually spawned a window.
    internal DataGridWindow? DataGridWindow => _dataGridWindow;
    internal GroundViewWindow? GroundViewWindow => _groundViewWindow;
    internal RadarViewWindow? RadarViewWindow => _radarViewWindow;
    internal ControllersWindow? ControllersWindow => _controllersWindow;
    internal MetarWindow? MetarWindow => _metarWindow;
    internal TerminalWindow? TerminalWindow => _terminalWindow;
    private bool _restoringGrid;
    private bool _isConfirmedClose;
    private bool _isMainWindowClosing;
    private string? _sortColumnKey;
    private ListSortDirection? _sortDirection;
    private CancellationTokenSource? _autoConnectCts;
    private Avalonia.Threading.DispatcherTimer? _timelineMarkerTimer;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel(new AvaloniaFilePickerService(this));
        DataContext = vm;

        // Apply the saved Interface font size into the app-level dynamic resources
        // so tabs/buttons/lists/panels render at the user's preferred size from launch.
        App.ApplyInterfaceFontSize(vm.Preferences.InterfaceFontSize);

        _geometryHelper = new WindowGeometryHelper(this, vm.Preferences, "Main", 1200, 700);
        _geometryHelper.Restore();
        _geometryHelper.SetBaseTitle(vm.WindowTitle);
        vm.PropertyChanged += OnMainWindowTitleChanged;

        _timelineMarkerTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timelineMarkerTimer.Tick += async (_, _) => await vm.RefreshTimelineMarkersAsync();
        _timelineMarkerTimer.Start();
        Closed += (_, _) => _timelineMarkerTimer?.Stop();

        var markerOverlay = this.FindControl<ItemsControl>("TimelineMarkerOverlay");
        if (markerOverlay is not null)
        {
            // Bubble (not Tunnel): the marker template's Border is the deepest visual; we
            // want its DataContext-bearing element to be the first thing we look at. With
            // Tunnel, any future addition inside the overlay subtree (context menus, tooltip
            // surfaces) would also trip this handler before reaching its target.
            markerOverlay.AddHandler(PointerPressedEvent, OnTimelineMarkerPressed, RoutingStrategies.Bubble);
        }

        var bookmarkOverlay = this.FindControl<ItemsControl>("BookmarkMarkerOverlay");
        if (bookmarkOverlay is not null)
        {
            // Left-click seeks to the bookmark; right-click is handled by the tick's
            // ContextMenu (Rename/Delete), so OnTimelineBookmarkPressed gates on left button.
            bookmarkOverlay.AddHandler(PointerPressedEvent, OnTimelineBookmarkPressed, RoutingStrategies.Bubble);
        }

        vm.BookmarkNamePromptRequested += OnBookmarkNamePromptRequested;
        vm.TakeControlConfirmation = ConfirmTakeControlAsync;

        _windowProfileService = new WindowProfileService(vm.Preferences);

        var settingsItem = this.FindControl<MenuItem>("SettingsMenuItem");
        if (settingsItem is not null)
        {
            settingsItem.Click += OnSettingsClick;
        }

        var connectItem = this.FindControl<MenuItem>("ConnectMenuItem");
        if (connectItem is not null)
        {
            connectItem.Click += OnConnectClick;
        }

        var disconnectItem = this.FindControl<MenuItem>("DisconnectMenuItem");
        if (disconnectItem is not null)
        {
            disconnectItem.Click += OnDisconnectClick;
        }

        var loadItem = this.FindControl<MenuItem>("LoadScenarioMenuItem");
        if (loadItem is not null)
        {
            loadItem.Click += OnLoadScenarioClick;
        }

        var loadWeatherItem = this.FindControl<MenuItem>("LoadWeatherMenuItem");
        if (loadWeatherItem is not null)
        {
            loadWeatherItem.Click += OnLoadWeatherClick;
        }

        var sessionReportItem = this.FindControl<MenuItem>("SessionReportMenuItem");
        if (sessionReportItem is not null)
        {
            sessionReportItem.Click += OnSessionReportClick;
        }

        var newWeatherItem = this.FindControl<MenuItem>("NewWeatherMenuItem");
        if (newWeatherItem is not null)
        {
            newWeatherItem.Click += OnNewWeatherClick;
        }

        var editWeatherItem = this.FindControl<MenuItem>("EditWeatherMenuItem");
        if (editWeatherItem is not null)
        {
            editWeatherItem.Click += OnEditWeatherClick;
            editWeatherItem.IsEnabled = vm.HasActiveWeather;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.HasActiveWeather))
                {
                    editWeatherItem.IsEnabled = vm.HasActiveWeather;
                }
            };
        }

        var editArrivalGeneratorsItem = this.FindControl<MenuItem>("EditArrivalGeneratorsMenuItem");
        if (editArrivalGeneratorsItem is not null)
        {
            editArrivalGeneratorsItem.Click += OnEditArrivalGeneratorsClick;
            editArrivalGeneratorsItem.IsEnabled = vm.HasScenario;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.HasScenario))
                {
                    editArrivalGeneratorsItem.IsEnabled = vm.HasScenario;
                }
            };
        }

        var recentItem = this.FindControl<MenuItem>("RecentScenariosMenuItem");
        if (recentItem is not null)
        {
            recentItem.IsEnabled = vm.IsInRoom && vm.Preferences.RecentScenarios.Count > 0;
            PopulateRecentScenarios(recentItem, vm);
            recentItem.SubmenuOpened += OnRecentScenariosSubmenuOpened;
        }

        var recentWeatherItem = this.FindControl<MenuItem>("RecentWeatherMenuItem");
        if (recentWeatherItem is not null)
        {
            recentWeatherItem.IsEnabled = vm.IsInRoom && vm.Preferences.RecentWeatherFiles.Count > 0;
            PopulateRecentWeather(recentWeatherItem, vm);
            recentWeatherItem.SubmenuOpened += OnRecentWeatherSubmenuOpened;
        }

        var copyViewItem = this.FindControl<MenuItem>("CopyViewSettingsMenuItem");
        if (copyViewItem is not null)
        {
            copyViewItem.Click += OnCopyViewSettingsClick;
        }

        var windowProfilesItem = this.FindControl<MenuItem>("WindowProfilesMenuItem");
        if (windowProfilesItem is not null)
        {
            PopulateWindowProfilesMenu(windowProfilesItem, vm);
            vm.Preferences.WindowProfilesChanged += () => PopulateWindowProfilesMenu(windowProfilesItem, vm);
        }

        var favoritesPanelItem = this.FindControl<MenuItem>("FavoritesPanelMenuItem");
        if (favoritesPanelItem is not null)
        {
            favoritesPanelItem.Click += OnFavoritesPanelClick;
        }

        var crcItem = this.FindControl<MenuItem>("ConfigureCrcMenuItem");
        if (crcItem is not null)
        {
            crcItem.Click += OnConfigureCrcClick;
        }

        var aboutItem = this.FindControl<MenuItem>("AboutMenuItem");
        if (aboutItem is not null)
        {
            aboutItem.Click += OnAboutClick;
        }

        var cheatsheetItem = this.FindControl<MenuItem>("HelpCheatsheetMenuItem");
        if (cheatsheetItem is not null)
        {
            cheatsheetItem.Click += OnCommandCheatsheetClick;
        }

        WireUrlMenuItem("HelpGettingStartedMenuItem", DocLinks.GettingStarted);
        WireUrlMenuItem("HelpUserGuideMenuItem", DocLinks.UserGuide);
        WireUrlMenuItem("HelpCommandsMenuItem", DocLinks.Commands);
        WireUrlMenuItem("HelpChangelogMenuItem", DocLinks.Changelog);
        WireUrlMenuItem("HelpReportBugMenuItem", DocLinks.Issues);
        WireUrlMenuItem("HelpOpenRepoMenuItem", DocLinks.Repo);

        var embeddedView = this.FindControl<DataGridView>("EmbeddedDataGridView");
        var dataGrid = embeddedView?.GetDataGrid();
        if (dataGrid is not null)
        {
            SetupDataGrid(dataGrid, vm);
            vm.GridLayoutReset += () => ResetLiveGrid(dataGrid);
        }

        // Focus the command input box on request — after a speech transcription (opt-in) or when
        // the focus-input hotkey fires from any YAAT window. Routes to whichever CommandInputView
        // is actually visible: the embedded one when docked, the popped-out TerminalWindow's
        // otherwise. Mirrors the GridLayoutReset → ResetLiveGrid wiring pattern (viewmodel raises a
        // parameterless event, view forwards to the relevant control).
        vm.RequestCommandInputFocus += FocusActiveCommandInput;

        vm.PropertyChanged += OnViewModelPropertyChanged;

        if (vm.IsDataGridPoppedOut)
        {
            OpenDataGridWindow(vm);
        }

        if (vm.IsGroundViewPoppedOut)
        {
            OpenGroundViewWindow(vm);
        }

        if (vm.IsRadarViewPoppedOut)
        {
            OpenRadarViewWindow(vm);
        }

        if (vm.IsControllersPoppedOut)
        {
            OpenControllersWindow(vm);
        }

        if (vm.IsMetarPoppedOut)
        {
            OpenMetarWindow(vm);
        }

        if (!vm.IsTerminalDocked)
        {
            _terminalWindow = new TerminalWindow(vm.Preferences) { DataContext = vm };
            _terminalWindow.Closing += OnTerminalWindowClosing;
            _terminalWindow.Show();
        }

        WireStripsEntryWindows(vm);
        WireTdlsEntryWindows(vm);

        // Sync the content grid's row heights to the initial pop-out state
        // since the partial methods fired before we subscribed above. Without
        // this, a session that restored with all tabs popped out would still
        // hold Row 0 at 3* (an empty 75 % stripe above the terminal).
        UpdateContentGridLayout(vm);

        // Re-validate SelectedTabIndex now that every dynamic TabItem (Strips
        // and TDLS) is materialized — see EnsureSelectedTabVisible's comment.
        vm.EnsureSelectedTabVisible();

        // Force the TabControl's actual SelectedIndex to match the VM.
        // Avalonia's two-way SelectedIndex binding doesn't propagate VM->
        // TabControl when the VM's value was set BEFORE the TabControl
        // materialized: the TabControl initializes to SelectedIndex=0 (its
        // own default), the binding tries to set SelectedIndex=N where N is
        // out of range for the 3 static TabItems, the TabControl silently
        // keeps 0, and never writes back. After the dynamic tabs are added
        // here, the indices are in range — but adding items doesn't
        // re-trigger the binding, so we have to push the value explicitly.
        // Symptom without this: Aircraft List content rendered in the docked
        // tab area even though the DataGrid tab is popped out and the only
        // docked tabs are Strips/TDLS.
        var tabControl = this.FindControl<TabControl>("MainTabControl");
        if (tabControl is not null && tabControl.SelectedIndex != vm.SelectedTabIndex)
        {
            tabControl.SelectedIndex = vm.SelectedTabIndex;
        }

        var slider = this.FindControl<Slider>("TimelineSlider");
        if (slider is not null)
        {
            SetupTimelineSlider(slider, vm);
        }

        ApplyKeybinds(vm.Preferences);

        // Start the process-wide keyboard hook so PTT works while the user has another app
        // focused (CRC, a browser, a PDF, etc.). Dispose handled in OnClosing.
        _globalKeyHook = new GlobalKeyHookService();
        _globalKeyHook.KeyDown += OnGlobalKeyDown;
        _globalKeyHook.KeyUp += OnGlobalKeyUp;
        _globalKeyHook.Start();

        // Wire the "Show speech recognition debugging..." items on both mic-status menus (one for
        // the active indicator, one for the "mic: off" stub). Both open the same SpeechDebugWindow.
        foreach (var debugItemName in new[] { "MicMenuDebugItem", "MicOffMenuDebugItem" })
        {
            var item = this.FindControl<MenuItem>(debugItemName);
            if (item is not null)
            {
                item.Click += OnShowSpeechDebugClick;
            }
        }

        if (App.AutoConnectTarget is { } target)
        {
            _autoConnectCts = new CancellationTokenSource();
            _ = AutoConnectAsync(vm, target, _autoConnectCts.Token);
        }
    }

    private SpeechDebugWindow? _speechDebugWindow;
    private SessionReportWindow? _sessionReportWindow;

    private void OnFavoritesPanelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            FavoritesPanelWindow.ShowOrActivate(vm, this);
        }
    }

    private void OnShowSpeechDebugClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // Reuse the existing window if the user opens it twice — bring it to the front instead of
        // creating a duplicate. Nulled out on Closed so the next request opens a fresh one.
        if (_speechDebugWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        _speechDebugWindow = new SpeechDebugWindow(vm.SpeechService, vm.SpeechSampleStore, vm.Preferences, vm.AudioCapture);
        _speechDebugWindow.Closed += (_, _) => _speechDebugWindow = null;
        _speechDebugWindow.Show(this);
    }

    private static void SetupTimelineSlider(Slider slider, MainViewModel vm)
    {
        var isInteracting = false;

        // Sync VM → slider when user is not interacting
        slider.Value = vm.ScenarioElapsedSeconds;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ScenarioElapsedSeconds) && !isInteracting)
            {
                slider.Value = vm.ScenarioElapsedSeconds;
            }
        };

        // Tunnel to catch pointer before the slider/thumb handles it
        slider.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) =>
            {
                isInteracting = true;
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel
        );

        slider.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, _) =>
            {
                if (isInteracting)
                {
                    var target = slider.Value;
                    isInteracting = false;
                    _ = vm.RewindToSeconds(target);
                }
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel
        );

        // Fallback: if pointer leaves the slider while pressed, capture is lost
        slider.AddHandler(
            InputElement.PointerCaptureLostEvent,
            (_, _) =>
            {
                if (isInteracting)
                {
                    var target = slider.Value;
                    isInteracting = false;
                    _ = vm.RewindToSeconds(target);
                }
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel
        );
    }

    private static async Task AutoConnectAsync(MainViewModel vm, string target, CancellationToken ct)
    {
        var log = AppLog.CreateLogger("AutoConnect");

        string url;
        var match = vm.Preferences.SavedServers.FirstOrDefault(s => s.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            url = match.Url;
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            url = target;
        }
        else
        {
            vm.StatusText = $"--autoconnect: '{target}' is not a saved server name or valid URL";
            return;
        }

        const int maxAttempts = 30;
        const int delayMs = 2000;
        bool connected = false;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var error = await vm.AttemptConnectAsync(url, ct);

            // User opened the Connect dialog (or otherwise took over) while we
            // were retrying — bail silently so we don't clobber their status
            // text or fight their manual connection on the next iteration.
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (error is null)
            {
                connected = true;
                break;
            }

            // Validation errors (missing identity info) — don't retry
            if (!error.StartsWith("Error:"))
            {
                vm.StatusText = error;
                return;
            }

            if (attempt < maxAttempts)
            {
                vm.StatusText = $"--autoconnect: waiting for server... ({attempt}/{maxAttempts})";
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (!connected)
        {
            return;
        }

        if (App.AutoLoadScenarioId is not { } scenarioId)
        {
            return;
        }

        await AutoCreateRoomAndLoadScenario(vm, scenarioId, log);
    }

    private static async Task AutoCreateRoomAndLoadScenario(MainViewModel vm, string scenarioId, ILogger log)
    {
        try
        {
            log.LogInformation("Auto-creating room for scenario {ScenarioId}", scenarioId);
            vm.StatusText = "Creating room...";
            await vm.CreateRoomCommand.ExecuteAsync(null);

            if (!vm.IsInRoom)
            {
                log.LogWarning("Auto-create room failed — cannot load scenario");
                return;
            }

            log.LogInformation("Fetching scenario {ScenarioId} from vNAS API", scenarioId);
            vm.StatusText = $"Fetching scenario {scenarioId}...";
            var dataService = new TrainingDataService();
            var json = await dataService.GetScenarioJsonAsync(scenarioId);

            if (json is null)
            {
                log.LogWarning("Failed to fetch scenario {ScenarioId}", scenarioId);
                vm.StatusText = $"--scenario: failed to fetch scenario '{scenarioId}'";
                return;
            }

            log.LogInformation("Auto-loading scenario {ScenarioId}", scenarioId);
            await vm.AutoLoadScenarioFromJsonAsync(json, scenarioId, scenarioId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Auto-load scenario failed");
            vm.StatusText = $"--scenario: {ex.Message}";
        }
    }

    private void SetupDataGrid(DataGrid dataGrid, MainViewModel vm)
    {
        foreach (var col in dataGrid.Columns)
        {
            var inner = GetColumnSortComparer(col);
            col.CustomSortComparer = new GroupStableSortComparer(inner);
        }

        RestoreGridLayout(dataGrid, vm.Preferences);

        WireColumnHeaderRightClick(dataGrid, vm);

        dataGrid.ColumnReordered += (_, _) => SaveGridLayout(dataGrid, vm.Preferences);
        dataGrid.Sorting += (_, e) =>
        {
            if (_restoringGrid)
            {
                return;
            }

            var clickedKey = GetColumnKey(e.Column);
            if (clickedKey == _sortColumnKey)
            {
                _sortDirection = _sortDirection switch
                {
                    ListSortDirection.Ascending => ListSortDirection.Descending,
                    _ => null,
                };
                if (_sortDirection is null)
                {
                    _sortColumnKey = null;
                }
            }
            else
            {
                _sortColumnKey = clickedKey;
                _sortDirection = ListSortDirection.Ascending;
            }

            SaveGridLayout(dataGrid, vm.Preferences);
        };

        dataGrid.PointerCaptureLost += (_, _) =>
        {
            if (!_restoringGrid)
            {
                SaveGridLayout(dataGrid, vm.Preferences);
            }
        };

        WireDistanceFlyout(vm, dataGrid);
    }

    private static string GetColumnKey(DataGridColumn column)
    {
        if (column.Header is string headerText)
        {
            return headerText;
        }

        if (!string.IsNullOrEmpty(column.SortMemberPath))
        {
            return column.SortMemberPath;
        }

        return column.DisplayIndex.ToString();
    }

    private static IComparer GetColumnSortComparer(DataGridColumn column)
    {
        if (column.Header is string header && header == "Status")
        {
            return StatusSortComparer.Instance;
        }

        // Extract the binding property name for property-based sorting
        string? propertyName = null;
        if (!string.IsNullOrEmpty(column.SortMemberPath))
        {
            propertyName = column.SortMemberPath;
        }
        else if (column is DataGridBoundColumn bound && bound.Binding is Binding binding && !string.IsNullOrEmpty(binding.Path))
        {
            propertyName = binding.Path;
        }

        if (!string.IsNullOrEmpty(propertyName))
        {
            return new PropertySortComparer(propertyName);
        }

        return Comparer<object>.Default;
    }

    private void RestoreGridLayout(DataGrid dataGrid, UserPreferences prefs)
    {
        var layout = prefs.GridLayout;
        if (layout is null)
        {
            return;
        }

        _restoringGrid = true;
        try
        {
            if (layout.ColumnOrder is { Count: > 0 })
            {
                var keyToColumn = new Dictionary<string, DataGridColumn>();
                foreach (var col in dataGrid.Columns)
                {
                    keyToColumn[GetColumnKey(col)] = col;
                }

                int displayIndex = 0;
                foreach (var key in layout.ColumnOrder)
                {
                    if (keyToColumn.Remove(key, out var col))
                    {
                        col.DisplayIndex = displayIndex;
                        displayIndex++;
                    }
                }
            }

            if (layout.SortColumn is not null && layout.SortDirection is not null)
            {
                foreach (var col in dataGrid.Columns)
                {
                    if (GetColumnKey(col) == layout.SortColumn)
                    {
                        col.Sort(layout.SortDirection.Value);
                        _sortColumnKey = layout.SortColumn;
                        _sortDirection = layout.SortDirection;
                        break;
                    }
                }
            }

            if (layout.ColumnWidths is { Count: > 0 })
            {
                foreach (var col in dataGrid.Columns)
                {
                    if (layout.ColumnWidths.TryGetValue(GetColumnKey(col), out var width))
                    {
                        col.Width = new DataGridLength(width);
                    }
                }
            }

            if (layout.HiddenColumns is { Count: > 0 })
            {
                var hidden = new HashSet<string>(layout.HiddenColumns);
                foreach (var col in dataGrid.Columns)
                {
                    if (hidden.Contains(GetColumnKey(col)))
                    {
                        col.IsVisible = false;
                    }
                }
            }
        }
        finally
        {
            _restoringGrid = false;
        }
    }

    private void SaveGridLayout(DataGrid dataGrid, UserPreferences prefs)
    {
        if (_restoringGrid)
        {
            return;
        }

        var columnOrder = dataGrid.Columns.OrderBy(c => c.DisplayIndex).Select(GetColumnKey).ToList();

        Dictionary<string, double>? columnWidths = null;
        List<string>? hiddenColumns = null;
        foreach (var col in dataGrid.Columns)
        {
            if (!col.Width.IsAuto)
            {
                columnWidths ??= [];
                columnWidths[GetColumnKey(col)] = col.ActualWidth;
            }
            if (!col.IsVisible)
            {
                hiddenColumns ??= [];
                hiddenColumns.Add(GetColumnKey(col));
            }
        }

        prefs.SetGridLayout(
            new SavedGridLayout
            {
                ColumnOrder = columnOrder,
                SortColumn = _sortColumnKey,
                SortDirection = _sortDirection,
                ColumnWidths = columnWidths,
                HiddenColumns = hiddenColumns,
            }
        );
    }

    private void ResetLiveGrid(DataGrid dataGrid)
    {
        _restoringGrid = true;
        try
        {
            _sortColumnKey = null;
            _sortDirection = null;

            for (int i = 0; i < dataGrid.Columns.Count; i++)
            {
                var col = dataGrid.Columns[i];
                col.DisplayIndex = i;
                col.Width = DataGridLength.Auto;
                col.IsVisible = true;
                col.ClearSort();
            }
        }
        finally
        {
            _restoringGrid = false;
        }
    }

    private void WireColumnHeaderRightClick(DataGrid dataGrid, MainViewModel vm)
    {
        dataGrid.Loaded += (_, _) =>
        {
            // Find the column headers presenter and attach right-click
            var headersPresenter = dataGrid.GetVisualDescendants().OfType<DataGridColumnHeadersPresenter>().FirstOrDefault();
            if (headersPresenter is null)
            {
                return;
            }

            headersPresenter.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(headersPresenter).Properties.IsRightButtonPressed)
                {
                    return;
                }

                e.Handled = true;

                var entries = new List<ColumnEntry>();
                foreach (var col in dataGrid.Columns.OrderBy(c => c.DisplayIndex))
                {
                    entries.Add(
                        new ColumnEntry
                        {
                            Key = GetColumnKey(col),
                            Name = GetColumnKey(col),
                            IsVisible = col.IsVisible,
                        }
                    );
                }

                Dictionary<string, double>? currentWidths = null;
                foreach (var col in dataGrid.Columns)
                {
                    if (!col.Width.IsAuto)
                    {
                        currentWidths ??= [];
                        currentWidths[GetColumnKey(col)] = col.ActualWidth;
                    }
                }

                var defaultOrder = dataGrid.Columns.Select(GetColumnKey).ToList();
                var chooser = new ColumnChooserWindow(
                    entries,
                    vm.ShowOnlyActiveAircraft,
                    vm.DataGridAlternatingRowColor,
                    currentWidths,
                    _sortColumnKey,
                    _sortDirection,
                    defaultOrder
                );
                var ownerWindow = TopLevel.GetTopLevel(dataGrid) as Window ?? this;
                await chooser.ShowDialog(ownerWindow);

                if (!chooser.Confirmed)
                {
                    return;
                }

                _restoringGrid = true;
                try
                {
                    int displayIndex = 0;
                    var keyToColumn = new Dictionary<string, DataGridColumn>();
                    foreach (var col in dataGrid.Columns)
                    {
                        keyToColumn[GetColumnKey(col)] = col;
                    }

                    foreach (var entry in chooser.Entries)
                    {
                        if (keyToColumn.TryGetValue(entry.Key, out var col))
                        {
                            col.IsVisible = entry.IsVisible;
                            col.DisplayIndex = displayIndex;
                            displayIndex++;
                        }
                    }
                }
                finally
                {
                    _restoringGrid = false;
                }

                if (chooser.ImportedLayout is { } imported)
                {
                    if (imported.ColumnWidths is { Count: > 0 })
                    {
                        foreach (var col in dataGrid.Columns)
                        {
                            if (imported.ColumnWidths.TryGetValue(GetColumnKey(col), out var width))
                            {
                                col.Width = new DataGridLength(width);
                            }
                        }
                    }

                    if (imported.SortColumn is not null && imported.SortDirection is not null)
                    {
                        foreach (var col in dataGrid.Columns)
                        {
                            if (GetColumnKey(col) == imported.SortColumn)
                            {
                                col.Sort(imported.SortDirection.Value);
                                _sortColumnKey = imported.SortColumn;
                                _sortDirection = imported.SortDirection;
                                break;
                            }
                        }
                    }
                }

                vm.ShowOnlyActiveAircraft = chooser.ShowOnlyActive;
                vm.DataGridAlternatingRowColor = chooser.AlternatingRowColor;
                SaveGridLayout(dataGrid, vm.Preferences);
            };
        };
    }

    private static void WireDistanceFlyout(MainViewModel vm, DataGrid dataGrid)
    {
        dataGrid.Loaded += (_, _) =>
        {
            TextBlock? header = null;
            foreach (var col in dataGrid.Columns)
            {
                if (col.SortMemberPath == "DistanceFromFix" && col.Header is TextBlock tb)
                {
                    header = tb;
                    break;
                }
            }

            if (header is null)
            {
                return;
            }

            var input = new TextBox { Watermark = "Fix or FRD..." };
            var listBox = new ListBox
            {
                MaxHeight = 160,
                IsVisible = false,
                Padding = new Avalonia.Thickness(2),
                Background = Avalonia.Media.Brush.Parse("#2D2D30"),
                BorderBrush = Avalonia.Media.Brush.Parse("#3F3F46"),
                BorderThickness = new Avalonia.Thickness(1),
            };

            var panel = new StackPanel
            {
                Width = 220,
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Reference Fix",
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        Margin = new Avalonia.Thickness(0, 0, 0, 4),
                    },
                    input,
                    listBox,
                },
            };

            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };

            // Open on middle-click or Ctrl/Cmd+click on the Distance column header
            var columnHeader = header.GetVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault() ?? (Control)header;
            columnHeader.PointerPressed += (_, e) =>
            {
                var props = e.GetCurrentPoint(columnHeader).Properties;
                if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && PlatformHelper.HasActionModifier(e.KeyModifiers)))
                {
                    e.Handled = true;
                    flyout.ShowAt(columnHeader);
                }
            };

            flyout.Opened += (_, _) =>
            {
                input.Text = "";
                listBox.Items.Clear();
                listBox.IsVisible = false;
                input.Focus();
            };

            input.TextChanged += (_, _) =>
            {
                UpdateDistFixSuggestions(vm, input, listBox);
            };

            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyDistanceFix(vm, input.Text, flyout);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    flyout.Hide();
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && listBox.IsVisible && listBox.ItemCount > 0)
                {
                    listBox.SelectedIndex = 0;
                    listBox.Focus();
                    e.Handled = true;
                }
            };

            listBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && listBox.SelectedItem is string sel)
                {
                    ApplyDistanceFix(vm, sel, flyout);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    flyout.Hide();
                    e.Handled = true;
                }
            };

            listBox.DoubleTapped += (_, _) =>
            {
                if (listBox.SelectedItem is string sel)
                {
                    ApplyDistanceFix(vm, sel, flyout);
                }
            };
        };
    }

    private static void UpdateDistFixSuggestions(MainViewModel vm, TextBox input, ListBox listBox)
    {
        var text = input.Text?.Trim().ToUpperInvariant() ?? "";
        listBox.Items.Clear();

        if (!vm.CommandInput.NavDbReady || text.Length == 0)
        {
            listBox.IsVisible = false;
            return;
        }

        var allNames = NavigationDatabase.Instance.AllFixNames;
        int lo = 0,
            hi = allNames.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (string.Compare(allNames[mid], 0, text, 0, text.Length, StringComparison.OrdinalIgnoreCase) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        int count = 0;
        for (int i = lo; i < allNames.Length && count < 10; i++)
        {
            if (!allNames[i].StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            listBox.Items.Add(allNames[i]);
            count++;
        }

        listBox.IsVisible = count > 0;
    }

    private static void ApplyDistanceFix(MainViewModel vm, string? text, Flyout flyout)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        vm.SetDistanceReference(text.Trim());
        flyout.Hide();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsTerminalDocked):
                HandleTerminalPopOut(vm);
                break;
            case nameof(MainViewModel.IsAnyTabVisible):
                UpdateContentGridLayout(vm);
                break;
            case nameof(MainViewModel.IsDataGridPoppedOut):
                HandleDataGridPopOut(vm);
                break;
            case nameof(MainViewModel.IsGroundViewPoppedOut):
                HandleGroundViewPopOut(vm);
                break;
            case nameof(MainViewModel.IsRadarViewPoppedOut):
                HandleRadarViewPopOut(vm);
                break;
            case nameof(MainViewModel.IsControllersPoppedOut):
                HandleControllersPopOut(vm);
                break;
            case nameof(MainViewModel.IsMetarPoppedOut):
                HandleMetarPopOut(vm);
                break;
            case nameof(MainViewModel.ActiveScenarioId):
                RefreshRecentScenariosEnabled(vm);
                break;
            case nameof(MainViewModel.ActiveRoomId):
                RefreshRecentMenusEnabled(vm);
                break;
        }
    }

    private void HandleTerminalPopOut(MainViewModel vm)
    {
        UpdateContentGridLayout(vm);

        if (!vm.IsTerminalDocked)
        {
            _terminalWindow = new TerminalWindow(vm.Preferences) { DataContext = vm };
            _terminalWindow.Closing += OnTerminalWindowClosing;
            _terminalWindow.Show();
        }
        else
        {
            if (_terminalWindow is not null)
            {
                _terminalWindow.Closing -= OnTerminalWindowClosing;
                _terminalWindow.Close();
                _terminalWindow = null;
            }
        }
    }

    /// <summary>
    /// Resizes the central content grid's rows to match the current
    /// pop-out state. Row 0 (TabControl) collapses when every tab is
    /// popped out; row 2 (Terminal) collapses when the terminal is
    /// popped out. With both popped out the entire grid is hidden via
    /// the <c>IsContentGridVisible</c> binding so only the menu bar
    /// remains. Bindings handle <c>IsVisible</c>; this call only fixes
    /// row heights so docked content fills the freed space instead of
    /// leaving the popped-out region holding 75 % of empty grid.
    /// </summary>
    private void UpdateContentGridLayout(MainViewModel vm)
    {
        var grid = this.FindControl<Grid>("ContentGrid");
        if (grid is not { RowDefinitions.Count: >= 3 })
        {
            return;
        }

        grid.RowDefinitions[0].Height = vm.IsAnyTabVisible ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        grid.RowDefinitions[2].Height = vm.IsTerminalDocked ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    private void HandleDataGridPopOut(MainViewModel vm)
    {
        if (vm.IsDataGridPoppedOut)
        {
            OpenDataGridWindow(vm);
        }
        else
        {
            CloseDataGridWindow();
        }
    }

    private void HandleGroundViewPopOut(MainViewModel vm)
    {
        if (vm.IsGroundViewPoppedOut)
        {
            OpenGroundViewWindow(vm);
        }
        else
        {
            CloseGroundViewWindow();
        }
    }

    private void HandleRadarViewPopOut(MainViewModel vm)
    {
        if (vm.IsRadarViewPoppedOut)
        {
            OpenRadarViewWindow(vm);
        }
        else
        {
            CloseRadarViewWindow();
        }
    }

    private void HandleControllersPopOut(MainViewModel vm)
    {
        if (vm.IsControllersPoppedOut)
        {
            OpenControllersWindow(vm);
        }
        else
        {
            CloseControllersWindow();
        }
    }

    private void HandleMetarPopOut(MainViewModel vm)
    {
        if (vm.IsMetarPoppedOut)
        {
            OpenMetarWindow(vm);
        }
        else
        {
            CloseMetarWindow();
        }
    }

    // ── Strips entries: tabs, pop-out windows, View menu ──────

    // Per-entry materializations managed in response to StripsEntries changes.
    // TabItem is the docked representation; VStripsViewWindow is the popped-out
    // representation; exactly one is shown at a time, flipped by entry.IsPoppedOut.
    private readonly Dictionary<VStripsDockEntryViewModel, TabItem> _stripsTabItems = [];
    private readonly Dictionary<VStripsDockEntryViewModel, VStripsViewWindow> _stripsWindows = [];

    // Test hook — see note on DataGridWindow/GroundViewWindow/RadarViewWindow above.
    internal IReadOnlyDictionary<VStripsDockEntryViewModel, VStripsViewWindow> StripsWindows => _stripsWindows;

    private void WireStripsEntryWindows(MainViewModel vm)
    {
        foreach (var entry in vm.StripsEntries)
        {
            AttachStripsEntry(vm, entry);
        }
        vm.StripsEntries.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (VStripsDockEntryViewModel added in e.NewItems)
                {
                    AttachStripsEntry(vm, added);
                }
            }
            if (e.OldItems is not null)
            {
                foreach (VStripsDockEntryViewModel removed in e.OldItems)
                {
                    DetachStripsEntry(removed);
                }
            }
            RebuildStripsSubmenu(vm);
        };
        RebuildStripsSubmenu(vm);
    }

    private void AttachStripsEntry(MainViewModel vm, VStripsDockEntryViewModel entry)
    {
        // Create the docked TabItem and add it as a sibling of the other main tabs.
        var tabControl = this.FindControl<TabControl>("MainTabControl");
        if (tabControl is not null)
        {
            var tab = new TabItem
            {
                Header = entry.TabTitle,
                Content = new VStripsView { DataContext = entry.Vm },
                IsVisible = !entry.IsPoppedOut,
            };
            _stripsTabItems[entry] = tab;
            tabControl.Items.Add(tab);
        }

        entry.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VStripsDockEntryViewModel.IsPoppedOut):
                    if (entry.IsPoppedOut)
                    {
                        OpenStripsEntryWindow(vm, entry);
                    }
                    else
                    {
                        CloseStripsEntryWindow(entry);
                    }
                    if (_stripsTabItems.TryGetValue(entry, out var t))
                    {
                        t.IsVisible = !entry.IsPoppedOut;
                    }
                    RebuildStripsSubmenu(vm);
                    break;
                case nameof(VStripsDockEntryViewModel.TabTitle):
                    if (_stripsTabItems.TryGetValue(entry, out var titleTab))
                    {
                        titleTab.Header = entry.TabTitle;
                    }
                    RebuildStripsSubmenu(vm);
                    break;
            }
        };

        if (entry.IsPoppedOut)
        {
            OpenStripsEntryWindow(vm, entry);
        }
    }

    private void DetachStripsEntry(VStripsDockEntryViewModel entry)
    {
        CloseStripsEntryWindow(entry);
        if (_stripsTabItems.Remove(entry, out var tab))
        {
            var tabControl = this.FindControl<TabControl>("MainTabControl");
            tabControl?.Items.Remove(tab);
        }
    }

    private void OpenStripsEntryWindow(MainViewModel vm, VStripsDockEntryViewModel entry)
    {
        if (_stripsWindows.ContainsKey(entry))
        {
            return;
        }
        var window = new VStripsViewWindow(vm.Preferences, entry.Vm.FacilityId, entry.TabTitle) { DataContext = entry.Vm };
        _stripsWindows[entry] = window;
        window.Closing += (_, _) =>
        {
            if (!IsClosingFromShutdown(_isMainWindowClosing))
            {
                entry.IsPoppedOut = false;
            }
            _stripsWindows.Remove(entry);
        };
        window.Show();
    }

    private void CloseStripsEntryWindow(VStripsDockEntryViewModel entry)
    {
        if (_stripsWindows.TryGetValue(entry, out var window))
        {
            _stripsWindows.Remove(entry);
            window.Close();
        }
    }

    /// <summary>
    /// Rebuilds the View → Strips submenu from current
    /// <see cref="MainViewModel.StripsEntries"/>. Each entry becomes a
    /// checkable 'Pop Out …' item; non-student entries also get a
    /// 'Close …' action. A trailing 'New Strips Tab…' item opens a
    /// facility picker. Called whenever the collection or any entry's
    /// pop-out state / facility name changes.
    /// </summary>
    private void RebuildStripsSubmenu(MainViewModel vm)
    {
        var submenu = this.FindControl<MenuItem>("StripsSubmenu");
        if (submenu is null)
        {
            return;
        }

        var items = new List<object>();
        foreach (var entry in vm.StripsEntries)
        {
            var popOut = new MenuItem
            {
                Header = $"Pop Out {entry.TabTitle}",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = entry.IsPoppedOut,
                Tag = entry,
            };
            popOut.Click += (_, _) =>
            {
                if (popOut.Tag is VStripsDockEntryViewModel e)
                {
                    e.IsPoppedOut = !e.IsPoppedOut;
                }
            };
            items.Add(popOut);

            if (!entry.IsStudentEntry)
            {
                var close = new MenuItem { Header = $"Close {entry.TabTitle}", Tag = entry };
                close.Click += (_, _) =>
                {
                    if (close.Tag is VStripsDockEntryViewModel e)
                    {
                        vm.CloseStripsEntryCommand.Execute(e);
                    }
                };
                items.Add(close);
            }
        }
        items.Add(new Separator());
        // Build "New Strips Tab..." as a real parent submenu (not a leaf with
        // Click). Showing a MenuFlyout from a leaf MenuItem inside an open
        // menu chain doesn't work in Avalonia — the parent menu loses focus
        // and dismisses, taking the flyout's anchor with it. The submenu
        // pattern (also used by RecentScenariosMenuItem and
        // CopyViewSettingsMenuItem) populates dynamically on SubmenuOpened.
        var newTabItem = new MenuItem { Header = "_New Strips Tab..." };
        // Pre-seed with one placeholder so Avalonia recognises this as a real
        // parent and shows the expand arrow before the user opens it.
        newTabItem.Items.Add(new MenuItem { Header = "(Loading...)", IsEnabled = false });
        newTabItem.SubmenuOpened += OnNewStripsTabSubmenuOpened;
        items.Add(newTabItem);

        submenu.ItemsSource = items;
    }

    private void OnNewStripsTabSubmenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem submenu || DataContext is not MainViewModel vm)
        {
            return;
        }
        submenu.Items.Clear();

        var studentVm = vm.StripsEntries.FirstOrDefault()?.Vm;
        if (studentVm is null || studentVm.AccessibleFacilities.Count == 0)
        {
            submenu.Items.Add(new MenuItem { Header = "(No accessible facilities)", IsEnabled = false });
            return;
        }

        var existingIds = vm
            .StripsEntries.Select(x => x.Vm.FacilityId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(System.StringComparer.Ordinal);
        var added = 0;
        foreach (var facility in studentVm.AccessibleFacilities)
        {
            if (existingIds.Contains(facility.FacilityId))
            {
                continue;
            }
            var header = facility.IsStudentFacility ? $"{facility.FacilityName} (own)" : facility.FacilityName;
            var item = new MenuItem { Header = header, Tag = facility };
            item.Click += async (_, _) =>
            {
                if (item.Tag is Services.AccessibleFacilityDto f)
                {
                    await vm.OpenStripsEntryForFacilityAsync(f.FacilityId);
                }
            };
            submenu.Items.Add(item);
            added++;
        }

        if (added == 0)
        {
            submenu.Items.Add(new MenuItem { Header = "(All accessible facilities already open)", IsEnabled = false });
        }
    }

    // ── vTDLS entries: tabs, pop-out windows, View menu ───────
    //
    // One-for-one mirror of the Strips block above. Kept in lockstep so a
    // refactor to one applies the same shape to the other.

    private readonly Dictionary<VTdlsDockEntryViewModel, TabItem> _tdlsTabItems = [];
    private readonly Dictionary<VTdlsDockEntryViewModel, VTdlsViewWindow> _tdlsWindows = [];

    internal IReadOnlyDictionary<VTdlsDockEntryViewModel, VTdlsViewWindow> TdlsWindows => _tdlsWindows;

    private void WireTdlsEntryWindows(MainViewModel vm)
    {
        foreach (var entry in vm.TdlsEntries)
        {
            AttachTdlsEntry(vm, entry);
        }
        vm.TdlsEntries.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (VTdlsDockEntryViewModel added in e.NewItems)
                {
                    AttachTdlsEntry(vm, added);
                }
            }
            if (e.OldItems is not null)
            {
                foreach (VTdlsDockEntryViewModel removed in e.OldItems)
                {
                    DetachTdlsEntry(removed);
                }
            }
            RebuildTdlsSubmenu(vm);
        };
        RebuildTdlsSubmenu(vm);
    }

    private void AttachTdlsEntry(MainViewModel vm, VTdlsDockEntryViewModel entry)
    {
        var tabControl = this.FindControl<TabControl>("MainTabControl");
        if (tabControl is not null)
        {
            var tab = new TabItem
            {
                Header = entry.TabTitle,
                Content = new VTdlsView { DataContext = entry.Vm },
                IsVisible = !entry.IsPoppedOut,
            };
            _tdlsTabItems[entry] = tab;
            tabControl.Items.Add(tab);
        }

        entry.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VTdlsDockEntryViewModel.IsPoppedOut):
                    if (entry.IsPoppedOut)
                    {
                        OpenTdlsEntryWindow(vm, entry);
                    }
                    else
                    {
                        CloseTdlsEntryWindow(entry);
                    }
                    if (_tdlsTabItems.TryGetValue(entry, out var t))
                    {
                        t.IsVisible = !entry.IsPoppedOut;
                    }
                    RebuildTdlsSubmenu(vm);
                    break;
                case nameof(VTdlsDockEntryViewModel.TabTitle):
                    if (_tdlsTabItems.TryGetValue(entry, out var titleTab))
                    {
                        titleTab.Header = entry.TabTitle;
                    }
                    if (_tdlsWindows.TryGetValue(entry, out var titleWindow))
                    {
                        titleWindow.SetWindowTitle(entry.TabTitle);
                    }
                    RebuildTdlsSubmenu(vm);
                    break;
            }
        };

        if (entry.IsPoppedOut)
        {
            OpenTdlsEntryWindow(vm, entry);
        }
    }

    private void DetachTdlsEntry(VTdlsDockEntryViewModel entry)
    {
        CloseTdlsEntryWindow(entry);
        if (_tdlsTabItems.Remove(entry, out var tab))
        {
            var tabControl = this.FindControl<TabControl>("MainTabControl");
            tabControl?.Items.Remove(tab);
        }
    }

    private void OpenTdlsEntryWindow(MainViewModel vm, VTdlsDockEntryViewModel entry)
    {
        if (_tdlsWindows.ContainsKey(entry))
        {
            return;
        }
        var window = new VTdlsViewWindow(vm.Preferences, entry.Vm.FacilityId, entry.TabTitle) { DataContext = entry.Vm };
        _tdlsWindows[entry] = window;
        window.Closing += (_, _) =>
        {
            if (!IsClosingFromShutdown(_isMainWindowClosing))
            {
                entry.IsPoppedOut = false;
            }
            _tdlsWindows.Remove(entry);
        };
        window.Show();
    }

    private void CloseTdlsEntryWindow(VTdlsDockEntryViewModel entry)
    {
        if (_tdlsWindows.TryGetValue(entry, out var window))
        {
            _tdlsWindows.Remove(entry);
            window.Close();
        }
    }

    private void RebuildTdlsSubmenu(MainViewModel vm)
    {
        var submenu = this.FindControl<MenuItem>("TdlsSubmenu");
        if (submenu is null)
        {
            return;
        }

        var items = new List<object>();
        foreach (var entry in vm.TdlsEntries)
        {
            var popOut = new MenuItem
            {
                Header = $"Pop Out {entry.TabTitle}",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = entry.IsPoppedOut,
                Tag = entry,
            };
            popOut.Click += (_, _) =>
            {
                if (popOut.Tag is VTdlsDockEntryViewModel e)
                {
                    e.IsPoppedOut = !e.IsPoppedOut;
                }
            };
            items.Add(popOut);

            if (!entry.IsStudentEntry)
            {
                var close = new MenuItem { Header = $"Close {entry.TabTitle}", Tag = entry };
                close.Click += (_, _) =>
                {
                    if (close.Tag is VTdlsDockEntryViewModel e)
                    {
                        vm.CloseTdlsEntryCommand.Execute(e);
                    }
                };
                items.Add(close);
            }
        }
        items.Add(new Separator());
        var newTabItem = new MenuItem { Header = "_New vTDLS Tab..." };
        newTabItem.Items.Add(new MenuItem { Header = "(Loading...)", IsEnabled = false });
        newTabItem.SubmenuOpened += OnNewTdlsTabSubmenuOpened;
        items.Add(newTabItem);

        submenu.ItemsSource = items;
    }

    private void OnNewTdlsTabSubmenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem submenu || DataContext is not MainViewModel vm)
        {
            return;
        }
        submenu.Items.Clear();

        var studentVm = vm.TdlsEntries.FirstOrDefault()?.Vm;
        if (studentVm is null || studentVm.AccessibleFacilities.Count == 0)
        {
            submenu.Items.Add(new MenuItem { Header = "(No accessible TDLS facilities)", IsEnabled = false });
            return;
        }

        var existingIds = vm.TdlsEntries.Select(x => x.Vm.FacilityId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(System.StringComparer.Ordinal);
        var added = 0;
        foreach (var facility in studentVm.AccessibleFacilities)
        {
            if (existingIds.Contains(facility.FacilityId))
            {
                continue;
            }
            var header = facility.IsStudentFacility ? $"{facility.FacilityName} (own)" : facility.FacilityName;
            var item = new MenuItem { Header = header, Tag = facility };
            item.Click += async (_, _) =>
            {
                if (item.Tag is Services.AccessibleFacilityDto f)
                {
                    await vm.OpenTdlsEntryForFacilityAsync(f.FacilityId);
                }
            };
            submenu.Items.Add(item);
            added++;
        }

        if (added == 0)
        {
            submenu.Items.Add(new MenuItem { Header = "(All accessible facilities already open)", IsEnabled = false });
        }
    }

    private void OpenDataGridWindow(MainViewModel vm)
    {
        _dataGridWindow = new DataGridWindow(vm.Preferences) { DataContext = vm };
        _dataGridWindow.Closing += OnDataGridWindowClosing;
        _dataGridWindow.Show();

        var popOutView = _dataGridWindow.FindControl<DataGridView>("PopOutDataGridView");
        var popOutGrid = popOutView?.GetDataGrid();
        if (popOutGrid is not null)
        {
            SetupDataGrid(popOutGrid, vm);
        }
    }

    private void CloseDataGridWindow()
    {
        if (_dataGridWindow is not null)
        {
            _dataGridWindow.Closing -= OnDataGridWindowClosing;
            _dataGridWindow.Close();
            _dataGridWindow = null;
        }
    }

    private void OpenGroundViewWindow(MainViewModel vm)
    {
        _groundViewWindow = new GroundViewWindow(vm.Preferences) { DataContext = vm };
        _groundViewWindow.Closing += OnGroundViewWindowClosing;
        _groundViewWindow.Show();
    }

    private void CloseGroundViewWindow()
    {
        if (_groundViewWindow is not null)
        {
            _groundViewWindow.Closing -= OnGroundViewWindowClosing;
            _groundViewWindow.Close();
            _groundViewWindow = null;
        }
    }

    private void OpenRadarViewWindow(MainViewModel vm)
    {
        _radarViewWindow = new RadarViewWindow(vm.Preferences) { DataContext = vm };
        _radarViewWindow.Closing += OnRadarViewWindowClosing;
        _radarViewWindow.Show();
    }

    private void CloseRadarViewWindow()
    {
        if (_radarViewWindow is not null)
        {
            _radarViewWindow.Closing -= OnRadarViewWindowClosing;
            _radarViewWindow.Close();
            _radarViewWindow = null;
        }
    }

    private void OpenControllersWindow(MainViewModel vm)
    {
        _controllersWindow = new ControllersWindow(vm.Preferences) { DataContext = vm };
        _controllersWindow.Closing += OnControllersWindowClosing;
        _controllersWindow.Show();
        _ = vm.RefreshOnlineControllersCommand.ExecuteAsync(null);
    }

    private void CloseControllersWindow()
    {
        if (_controllersWindow is not null)
        {
            _controllersWindow.Closing -= OnControllersWindowClosing;
            _controllersWindow.Close();
            _controllersWindow = null;
        }
    }

    private void OpenMetarWindow(MainViewModel vm)
    {
        _metarWindow = new MetarWindow(vm.Preferences) { DataContext = vm };
        _metarWindow.Closing += OnMetarWindowClosing;
        _metarWindow.Show();
    }

    private void CloseMetarWindow()
    {
        if (_metarWindow is not null)
        {
            _metarWindow.Closing -= OnMetarWindowClosing;
            _metarWindow.Close();
            _metarWindow = null;
        }
    }

    // Pop-out close handlers revert the dock flag only when the user closed *this* window
    // manually. During app shutdown (X-button on MainWindow, File > Exit, Velopack restart,
    // CancelKeyPress) the pop-outs are closed by the framework and the persisted "popped
    // out" flag must survive so the next launch restores the same layout. The local
    // _isMainWindowClosing flag covers X-button-on-MainWindow; AppLifetime.IsShuttingDown
    // covers every other shutdown path that may close pop-outs before MainWindow.OnClosing
    // has a chance to fire.
    private static bool IsClosingFromShutdown(bool isMainWindowClosing) => isMainWindowClosing || AppLifetime.IsShuttingDown;

    private void OnTerminalWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!IsClosingFromShutdown(_isMainWindowClosing) && DataContext is MainViewModel vm)
        {
            vm.IsTerminalDocked = true;
        }
        _terminalWindow = null;
    }

    private void OnDataGridWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!IsClosingFromShutdown(_isMainWindowClosing) && DataContext is MainViewModel vm)
        {
            vm.IsDataGridPoppedOut = false;
        }
        _dataGridWindow = null;
    }

    private void OnGroundViewWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!IsClosingFromShutdown(_isMainWindowClosing) && DataContext is MainViewModel vm)
        {
            vm.IsGroundViewPoppedOut = false;
        }
        _groundViewWindow = null;
    }

    private void OnRadarViewWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!IsClosingFromShutdown(_isMainWindowClosing) && DataContext is MainViewModel vm)
        {
            vm.IsRadarViewPoppedOut = false;
        }
        _radarViewWindow = null;
    }

    private void OnControllersWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!IsClosingFromShutdown(_isMainWindowClosing) && DataContext is MainViewModel vm)
        {
            vm.IsControllersPoppedOut = false;
        }
        _controllersWindow = null;
    }

    private void OnMetarWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!IsClosingFromShutdown(_isMainWindowClosing) && DataContext is MainViewModel vm)
        {
            vm.IsMetarPoppedOut = false;
        }
        _metarWindow = null;
    }

    private void RefreshRecentScenariosEnabled(MainViewModel vm)
    {
        var recentItem = this.FindControl<MenuItem>("RecentScenariosMenuItem");
        if (recentItem is not null)
        {
            recentItem.IsEnabled = vm.IsInRoom && vm.Preferences.RecentScenarios.Count > 0;
        }
    }

    private void RefreshRecentMenusEnabled(MainViewModel vm)
    {
        var recentScenarios = this.FindControl<MenuItem>("RecentScenariosMenuItem");
        if (recentScenarios is not null)
        {
            recentScenarios.IsEnabled = vm.IsInRoom && vm.Preferences.RecentScenarios.Count > 0;
        }

        var recentWeather = this.FindControl<MenuItem>("RecentWeatherMenuItem");
        if (recentWeather is not null)
        {
            recentWeather.IsEnabled = vm.IsInRoom && vm.Preferences.RecentWeatherFiles.Count > 0;
        }
    }

    private async void OnLoadScenarioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var window = new LoadScenarioWindow(vm.Preferences, vm.Connection);
        var result = await window.ShowDialog<ScenarioLoadResult?>(this);
        if (result is null)
        {
            return;
        }

        if (result.FilePath is not null)
        {
            vm.ScenarioFilePath = result.FilePath;
            await vm.LoadScenarioCommand.ExecuteAsync(null);
        }
        else if (result.ApiScenarioId is not null)
        {
            await LoadScenarioFromApiAsync(vm, result.ApiScenarioId, result.ApiScenarioName);
        }
    }

    private void OnRecentScenariosSubmenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || DataContext is not MainViewModel vm)
        {
            return;
        }

        PopulateRecentScenarios(menu, vm);
    }

    private void PopulateWindowProfilesMenu(MenuItem menu, MainViewModel vm)
    {
        menu.Items.Clear();

        var saveItem = new MenuItem { Header = "Save Current as Profile..." };
        saveItem.Click += async (_, _) => await OnSaveCurrentWindowProfileAsync(vm);
        menu.Items.Add(saveItem);

        var manageItem = new MenuItem { Header = "Manage Profiles..." };
        manageItem.Click += async (_, _) => await OnManageWindowProfilesAsync(vm);
        menu.Items.Add(manageItem);

        var profiles = vm.Preferences.WindowProfiles;
        if (profiles.Count == 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "(No saved profiles)", IsEnabled = false });
            return;
        }

        menu.Items.Add(new Separator());
        foreach (var profile in profiles)
        {
            var item = new MenuItem { Header = profile.Name, Tag = profile.Name };
            item.Click += async (_, e) =>
            {
                if (e.Source is MenuItem clicked && clicked.Tag is string name)
                {
                    await ApplyWindowProfileByNameAsync(vm, name);
                }
            };
            menu.Items.Add(item);
        }
    }

    private async System.Threading.Tasks.Task OnSaveCurrentWindowProfileAsync(MainViewModel vm)
    {
        var existing = vm.Preferences.WindowProfiles.Select(p => p.Name);
        var dlg = new SaveWindowProfileDialog(existing, null);
        await dlg.ShowDialog(this);

        if (string.IsNullOrWhiteSpace(dlg.ProfileName))
        {
            return;
        }

        var profile = _windowProfileService.CaptureCurrent(dlg.ProfileName, vm);
        vm.Preferences.SaveWindowProfile(profile);
        vm.StatusText = $"Saved window profile \"{profile.Name}\"";
    }

    private async System.Threading.Tasks.Task OnManageWindowProfilesAsync(MainViewModel vm)
    {
        var dlg = new ManageWindowProfilesDialog(vm.Preferences);
        await dlg.ShowDialog(this);

        switch (dlg.Action)
        {
            case ManageWindowProfilesAction.Apply when dlg.SelectedProfileName is { } name:
                await ApplyWindowProfileByNameAsync(vm, name);
                break;
            case ManageWindowProfilesAction.UpdateFromCurrent when dlg.SelectedProfileName is { } name:
                var refreshed = _windowProfileService.CaptureCurrent(name, vm);
                vm.Preferences.SaveWindowProfile(refreshed);
                vm.StatusText = $"Updated window profile \"{name}\" from current arrangement";
                break;
        }
    }

    private async System.Threading.Tasks.Task ApplyWindowProfileByNameAsync(MainViewModel vm, string name)
    {
        var profile = vm.Preferences.GetWindowProfile(name);
        if (profile is null)
        {
            vm.StatusText = $"Window profile \"{name}\" not found";
            return;
        }

        _windowProfileService.StagePreferences(profile);

        // Flip the pop-out toggles. The OnIs*PoppedOutChanged handlers on
        // MainViewModel + OnViewModelPropertyChanged here will create or
        // destroy the corresponding pop-out windows. New windows read the
        // freshly-staged geometry preferences on construction.
        vm.IsTerminalDocked = profile.IsTerminalDocked;
        vm.IsDataGridPoppedOut = profile.IsDataGridPoppedOut;
        vm.IsGroundViewPoppedOut = profile.IsGroundViewPoppedOut;
        vm.IsRadarViewPoppedOut = profile.IsRadarViewPoppedOut;
        vm.IsControllersPoppedOut = profile.IsControllersPoppedOut;
        vm.IsMetarPoppedOut = profile.IsMetarPoppedOut;

        // Defer geometry push and grid-layout apply so any windows that were
        // just opened by the toggle flips above have actually entered the
        // ActiveHelpers registry. Without the Post, the helpers list still
        // reflects pre-flip state.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var helper in WindowGeometryHelper.GetActiveHelpers())
            {
                if (profile.WindowGeometries.TryGetValue(helper.WindowName, out var geo))
                {
                    helper.ApplyGeometry(geo);
                }
            }

            if (profile.DataGridLayout is not null)
            {
                ApplyGridLayoutToLiveGrids(vm);
            }
        });

        vm.StatusText = $"Applied window profile \"{name}\"";
    }

    /// <summary>
    /// Re-runs the column-restore path on whichever DataGrid instances are
    /// currently materialized. Both the embedded grid (always present) and
    /// the popped-out grid (present when DataGridWindow is open) share their
    /// column-key set; resetting then re-restoring keeps both consistent so a
    /// later pop-out toggle doesn't flash the previous layout.
    /// </summary>
    private void ApplyGridLayoutToLiveGrids(MainViewModel vm)
    {
        var embedded = this.FindControl<DataGridView>("EmbeddedDataGridView")?.GetDataGrid();
        if (embedded is not null)
        {
            ResetGridVisibility(embedded);
            RestoreGridLayout(embedded, vm.Preferences);
        }

        var popOut = _dataGridWindow?.FindControl<DataGridView>("PopOutDataGridView")?.GetDataGrid();
        if (popOut is not null)
        {
            ResetGridVisibility(popOut);
            RestoreGridLayout(popOut, vm.Preferences);
        }
    }

    private void ResetGridVisibility(DataGrid grid)
    {
        _restoringGrid = true;
        try
        {
            foreach (var col in grid.Columns)
            {
                col.IsVisible = true;
                col.Width = DataGridLength.Auto;
            }
        }
        finally
        {
            _restoringGrid = false;
        }
    }

    private void PopulateRecentScenarios(MenuItem menu, MainViewModel vm)
    {
        menu.Items.Clear();
        var recent = vm.Preferences.RecentScenarios;
        if (recent.Count == 0 || !vm.IsInRoom)
        {
            menu.IsEnabled = false;
            menu.Items.Add(new MenuItem { Header = recent.Count == 0 ? "(No recent scenarios)" : "(Join a room first)", IsEnabled = false });
            return;
        }

        menu.IsEnabled = true;
        foreach (var entry in recent)
        {
            var item = new MenuItem { Header = entry.Name, Tag = entry };
            item.Click += OnRecentScenarioClick;
            ToolTip.SetTip(item, entry.IsApi ? $"API: {entry.ApiId}" : entry.FilePath);
            menu.Items.Add(item);
        }
    }

    private async void OnCopyViewSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await OnCopyViewSettingsAsync(vm);
        }
    }

    private async System.Threading.Tasks.Task OnCopyViewSettingsAsync(MainViewModel vm)
    {
        if (vm.ActiveScenarioId is null)
        {
            return;
        }

        var context = new CopyViewSettingsContext
        {
            Preferences = vm.Preferences,
            CurrentScenarioId = vm.ActiveScenarioId,
            CurrentScenarioName = vm.ActiveScenarioName ?? vm.ActiveScenarioId,
            CurrentAirport = vm.ActiveScenarioPrimaryAirportId,
            CurrentGround = vm.Ground.CaptureSettings(),
            CurrentRadar = vm.Radar.CaptureSettings(),
            CurrentLayout = _windowProfileService.CaptureCurrent("(current)", vm),
            ResolveMapName = vm.Radar.ResolveMapName,
        };

        var dlg = new CopyViewSettingsDialog(context);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed || dlg.SourceId is null)
        {
            return;
        }

        if (dlg.SourceKind == CopySourceKind.Scenario)
        {
            ApplyScenarioViewCopy(vm, dlg.SourceId, dlg.SelectedKeys);
        }
        else
        {
            var profile = vm.Preferences.GetWindowProfile(dlg.SourceId);
            if (profile is not null)
            {
                await ApplyWindowProfilePartialAsync(vm, profile, dlg.SelectedKeys);
            }
        }
    }

    private static void ApplyScenarioViewCopy(MainViewModel vm, string sourceScenarioId, IReadOnlyList<string> selectedKeys)
    {
        var selected = new HashSet<string>(selectedKeys);
        var prefs = vm.Preferences;

        var sourceGround = prefs.GetGroundSettings(sourceScenarioId);
        if (sourceGround is not null && ViewSettingsCopyCatalog.GroundGroups.Any(g => selected.Contains(g.Key)))
        {
            var merged = vm.Ground.CaptureSettings();
            foreach (var group in ViewSettingsCopyCatalog.GroundGroups)
            {
                if (selected.Contains(group.Key))
                {
                    group.Copy(sourceGround, merged);
                }
            }

            vm.Ground.ApplyCopiedSettings(merged);
        }

        var sourceRadar = prefs.GetRadarSettings(sourceScenarioId);
        if (sourceRadar is not null && ViewSettingsCopyCatalog.RadarGroups.Any(g => selected.Contains(g.Key)))
        {
            var merged = vm.Radar.CaptureSettings();
            foreach (var group in ViewSettingsCopyCatalog.RadarGroups)
            {
                if (selected.Contains(group.Key))
                {
                    group.Copy(sourceRadar, merged);
                }
            }

            vm.Radar.ApplyCopiedSettings(merged);
        }

        vm.StatusText = $"Copied {selected.Count} view-setting group(s) from the selected scenario";
    }

    private async System.Threading.Tasks.Task ApplyWindowProfilePartialAsync(
        MainViewModel vm,
        SavedWindowProfile profile,
        IReadOnlyList<string> selectedKeys
    )
    {
        var selected = new HashSet<string>(selectedKeys);
        var geometryKeys = new HashSet<string>(selected.Where(k => k.StartsWith("geo:", StringComparison.Ordinal)).Select(k => k["geo:".Length..]));
        var includeGrid = selected.Contains("columns");
        var includePopouts = selected.Contains("popouts");

        _windowProfileService.StagePreferencesPartial(profile, geometryKeys, includeGrid);

        if (includePopouts)
        {
            vm.IsTerminalDocked = profile.IsTerminalDocked;
            vm.IsDataGridPoppedOut = profile.IsDataGridPoppedOut;
            vm.IsGroundViewPoppedOut = profile.IsGroundViewPoppedOut;
            vm.IsRadarViewPoppedOut = profile.IsRadarViewPoppedOut;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var helper in WindowGeometryHelper.GetActiveHelpers())
            {
                if (geometryKeys.Contains(helper.WindowName) && profile.WindowGeometries.TryGetValue(helper.WindowName, out var geo))
                {
                    helper.ApplyGeometry(geo);
                }
            }

            if (includeGrid && profile.DataGridLayout is not null)
            {
                ApplyGridLayoutToLiveGrids(vm);
            }
        });

        vm.StatusText = $"Copied layout from profile \"{profile.Name}\"";
    }

    private async void OnRecentScenarioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RecentScenario entry } || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (entry.IsApi)
        {
            await LoadScenarioFromApiAsync(vm, entry.ApiId!, entry.Name);
        }
        else
        {
            if (!File.Exists(entry.FilePath))
            {
                vm.StatusText = $"File not found: {entry.FilePath}";
                return;
            }

            vm.ScenarioFilePath = entry.FilePath;
            await vm.LoadScenarioCommand.ExecuteAsync(null);
        }
    }

    private static async Task LoadScenarioFromApiAsync(MainViewModel vm, string apiScenarioId, string? displayName = null)
    {
        vm.StatusText = "Loading scenario…";
        await vm.LoadScenarioFromIdAsync(apiScenarioId, displayName);
    }

    private static async Task LoadWeatherFromApiAsync(MainViewModel vm, string apiWeatherId, string? displayName = null)
    {
        vm.StatusText = "Fetching weather…";
        var trainingData = new TrainingDataService();
        var json = await trainingData.GetWeatherJsonAsync(apiWeatherId);
        if (json is not null)
        {
            await vm.LoadWeatherFromJsonAsync(json, displayName ?? apiWeatherId, apiWeatherId);
        }
        else
        {
            vm.StatusText = "Failed to fetch weather from API";
        }
    }

    private void OnRecentWeatherSubmenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || DataContext is not MainViewModel vm)
        {
            return;
        }

        PopulateRecentWeather(menu, vm);
    }

    private void PopulateRecentWeather(MenuItem menu, MainViewModel vm)
    {
        menu.Items.Clear();
        var recent = vm.Preferences.RecentWeatherFiles;
        if (recent.Count == 0 || !vm.IsInRoom)
        {
            menu.IsEnabled = false;
            menu.Items.Add(new MenuItem { Header = recent.Count == 0 ? "(No recent weather)" : "(Join a room first)", IsEnabled = false });
            return;
        }

        menu.IsEnabled = true;
        foreach (var entry in recent)
        {
            var item = new MenuItem { Header = entry.Name, Tag = entry };
            item.Click += OnRecentWeatherClick;
            ToolTip.SetTip(item, entry.IsApi ? $"API: {entry.ApiId}" : entry.FilePath);
            menu.Items.Add(item);
        }
    }

    private async void OnRecentWeatherClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RecentWeather entry } || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (entry.IsApi)
        {
            await LoadWeatherFromApiAsync(vm, entry.ApiId!, entry.Name);
            return;
        }

        if (!File.Exists(entry.FilePath))
        {
            vm.StatusText = $"File not found: {entry.FilePath}";
            return;
        }

        await vm.LoadWeatherCommand.ExecuteAsync(entry.FilePath);
    }

    private async void OnLoadWeatherClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var window = new LoadWeatherWindow(vm.Preferences);
        var result = await window.ShowDialog<WeatherLoadResult?>(this);
        if (result is null)
        {
            return;
        }

        if (result.FilePath is not null)
        {
            await vm.LoadWeatherCommand.ExecuteAsync(result.FilePath);
        }
        else if (result.ApiWeatherId is not null)
        {
            await LoadWeatherFromApiAsync(vm, result.ApiWeatherId, result.ApiWeatherName);
        }
    }

    private async void OnTimelineMarkerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only act when the pressed visual sits inside a marker template (DataContext =
        // TimelineMarkerVm). Anything else routed up to the overlay — empty marker-rail
        // background, future context-menu surfaces, etc. — gets ignored without setting
        // e.Handled so the event continues to bubble.
        if (e.Source is not Visual visual)
        {
            return;
        }

        var control = visual as Control ?? visual.GetVisualAncestors().OfType<Control>().FirstOrDefault();
        while (control is not null)
        {
            if (control.DataContext is TimelineMarkerVm marker)
            {
                if (DataContext is MainViewModel vm)
                {
                    e.Handled = true;
                    await vm.RewindToSeconds(marker.TimeSeconds);
                }
                return;
            }
            control = control.GetVisualParent() as Control;
        }
    }

    private TimelineBookmarkVm? _bookmarkBeingNamed;

    private async void OnTimelineBookmarkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual visual)
        {
            return;
        }

        // Right-click opens the tick's ContextMenu (Rename/Delete); only seek on left-click.
        if (!e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var control = visual as Control ?? visual.GetVisualAncestors().OfType<Control>().FirstOrDefault();
        while (control is not null)
        {
            if (control.DataContext is TimelineBookmarkVm bookmark)
            {
                if (DataContext is MainViewModel vm)
                {
                    e.Handled = true;
                    await vm.RewindToSeconds(bookmark.TimeSeconds);
                }
                return;
            }
            control = control.GetVisualParent() as Control;
        }
    }

    private void OnBookmarkNamePromptRequested(TimelineBookmarkVm bookmark)
    {
        _bookmarkBeingNamed = bookmark;
        var popup = this.FindControl<Popup>("BookmarkNamePopup");
        var textBox = this.FindControl<TextBox>("BookmarkNameText");
        if (popup is null || textBox is null)
        {
            return;
        }

        textBox.Text = bookmark.Name ?? string.Empty;
        popup.IsOpen = true;
        textBox.Focus();
        textBox.SelectAll();
    }

    private void OnBookmarkNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitBookmarkName();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseBookmarkNamePopup();
            e.Handled = true;
        }
    }

    private void OnBookmarkNameSubmit(object? sender, RoutedEventArgs e)
    {
        CommitBookmarkName();
    }

    private void OnBookmarkNameCancel(object? sender, RoutedEventArgs e)
    {
        CloseBookmarkNamePopup();
    }

    private void CommitBookmarkName()
    {
        var textBox = this.FindControl<TextBox>("BookmarkNameText");
        if (_bookmarkBeingNamed is not null && textBox is not null)
        {
            var text = textBox.Text?.Trim();
            _bookmarkBeingNamed.Name = string.IsNullOrEmpty(text) ? null : text;
        }
        CloseBookmarkNamePopup();
    }

    private void CloseBookmarkNamePopup()
    {
        var popup = this.FindControl<Popup>("BookmarkNamePopup");
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
        _bookmarkBeingNamed = null;
    }

    private async void OnSessionReportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsConnected)
        {
            return;
        }

        if (_sessionReportWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        try
        {
            var report = await vm.Connection.GetSessionReportAsync();
            if (report is null)
            {
                vm.StatusText = "No session report available";
                return;
            }

            var window = new SessionReportWindow(vm.Connection.GetSessionReportAsync, vm.ShowAircraftOnTimelineAsync);
            window.Closed += (_, _) => _sessionReportWindow = null;
            _sessionReportWindow = window;
            new WindowGeometryHelper(window, vm.Preferences, "SessionReport", 1000, 700).Restore();
            await window.StartAsync();
            window.Show(this);
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Session report error: {ex.Message}";
        }
    }

    private void OnDisconnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // The bound DisconnectCommand handles the actual disconnect; this
        // handler just stops any in-flight autoconnect retry loop so it
        // doesn't immediately reconnect us.
        _autoConnectCts?.Cancel();
    }

    private async void OnConnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // User is taking over connection management — stop any autoconnect
        // retry loop so it can't disconnect the manual connection by racing
        // ServerConnection.ConnectAsync against it.
        _autoConnectCts?.Cancel();

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        ConnectWindow? connectWindow = null;
        var connectVm = new ConnectViewModel(
            vm.Preferences.SavedServers,
            vm.Preferences.LastUsedServerUrl,
            vm.Preferences.VatsimCid,
            vm.Preferences.UserInitials,
            vm.Preferences.ArtccId,
            connectAction: vm.AttemptConnectAsync,
            saveAction: (servers, lastUrl) => vm.Preferences.SetSavedServers(servers, lastUrl),
            identitySaveAction: (cid, initials, artcc) =>
            {
                vm.Preferences.SetVatsimCid(cid);
                vm.Preferences.SetUserInitials(initials);
                vm.Preferences.SetArtccId(artcc);
            },
            closeAction: () => connectWindow?.Close()
        );
        connectWindow = new ConnectWindow(connectVm, vm.Preferences);
        await connectWindow.ShowDialog(this);
    }

    private async void OnConfigureCrcClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!CrcConfigService.IsCrcInstalled())
        {
            await ShowMessageAsync("CRC is not installed on this computer.");
            return;
        }

        if (CrcConfigService.AreYaatEntriesPresent())
        {
            await ShowMessageAsync("CRC already has YAAT server environments configured.");
            return;
        }

        CrcConfigService.Configure();
        await ShowMessageAsync("YAAT server environments added to CRC. Restart CRC to pick up changes.");
    }

    private async void OnAboutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var about = new AboutWindow();
        await about.ShowDialog(this);
    }

    private void OnCommandCheatsheetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "command-cheatsheet.html");
        UrlLauncher.OpenInBrowser(File.Exists(local) ? local : DocLinks.CommandCheatsheet);
    }

    private void WireUrlMenuItem(string name, string url)
    {
        var item = this.FindControl<MenuItem>(name);
        if (item is not null)
        {
            item.Click += (_, _) => UrlLauncher.OpenInBrowser(url);
        }
    }

    private async Task ShowMessageAsync(string message)
    {
        var box = MessageBoxManager.GetMessageBoxStandard("YAAT", message, ButtonEnum.Ok);
        await box.ShowWindowDialogAsync(this);
    }

    private async void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // Snapshot current visual state for rollback on cancel
        var snapshotGroundColors = vm.Ground.ColorScheme;
        var snapshotSatBrightness = vm.Ground.SatelliteImageBrightness;
        var snapshotMapBrightness = vm.Ground.VideoMapOverlayBrightness;
        var snapshotGndBrightness = vm.Ground.YaatLayoutBrightness;
        var snapshotDataGridScale = vm.DataGridScale;
        var snapshotAssignmentTintEnabled = vm.Preferences.AssignmentTintEnabled;
        var snapshotAssignmentTintColor = vm.Preferences.AssignmentTintColor;
        var snapshotUnassignedTintEnabled = vm.Preferences.UnassignedTintEnabled;
        var snapshotUnassignedTintColor = vm.Preferences.UnassignedTintColor;
        var snapshotSelectedColor = vm.Preferences.SelectedColor;
        var snapshotTerminalFontSize = vm.TerminalFontSize;
        var snapshotInterfaceFontSize = vm.Preferences.InterfaceFontSize;
        var snapshotStripsZoomPercent = vm.Preferences.StripsZoomPercent;
        var snapshotTdlsZoomPercent = vm.Preferences.TdlsZoomPercent;

        // Suppress the strips on-panel zoom-persist path while the dialog is open so
        // transient preview values aren't written to preferences (final value is
        // persisted by the dialog's Save).
        vm.IsSettingsPreviewActive = true;

        var dialog = new SettingsWindow(vm.Preferences, vm.AudioCapture, vm.SpeechSampleStore);
        var settingsVm = dialog.DataContext as SettingsViewModel;

        // Subscribe to live preview
        if (settingsVm is not null)
        {
            settingsVm.VisualSettingsChanged += OnPreview;
        }

        await dialog.ShowDialog(this);

        // Unsubscribe
        if (settingsVm is not null)
        {
            settingsVm.VisualSettingsChanged -= OnPreview;
        }

        if (settingsVm?.Saved == true)
        {
            // Apply final saved state (non-visual settings like keybinds, command scheme)
            vm.RefreshCommandScheme();
            vm.DataGridScale = vm.Preferences.DataGridFontSize / 12.0;
            vm.TerminalFontSize = vm.Preferences.TerminalFontSize;
            App.ApplyInterfaceFontSize(vm.Preferences.InterfaceFontSize);
            vm.ApplyStripsZoomPercent(vm.Preferences.StripsZoomPercent);
            vm.ApplyTdlsZoomPercent(vm.Preferences.TdlsZoomPercent);
            vm.RefreshIsSpeechEnabledFromPrefs();
            vm.RefreshWindowTitleFromPrefs();
            ApplyKeybinds(vm.Preferences);
            // Visual settings already applied via preview — just ensure final state is consistent
            SyncAllRadarViewTint();
            vm.Ground.ColorScheme = vm.Preferences.GroundColors;
            vm.Ground.SatelliteImageBrightness = vm.Preferences.GroundSatelliteImageBrightness;
            vm.Ground.VideoMapOverlayBrightness = vm.Preferences.GroundVideoMapOverlayBrightness;
            vm.Ground.YaatLayoutBrightness = vm.Preferences.GroundYaatLayoutBrightness;
        }
        else
        {
            // Cancel — rollback to snapshot
            vm.Ground.ColorScheme = snapshotGroundColors;
            vm.Ground.SatelliteImageBrightness = snapshotSatBrightness;
            vm.Ground.VideoMapOverlayBrightness = snapshotMapBrightness;
            vm.Ground.YaatLayoutBrightness = snapshotGndBrightness;
            vm.DataGridScale = snapshotDataGridScale;
            vm.TerminalFontSize = snapshotTerminalFontSize;
            App.ApplyInterfaceFontSize(snapshotInterfaceFontSize);
            vm.ApplyStripsZoomPercent(snapshotStripsZoomPercent);
            vm.ApplyTdlsZoomPercent(snapshotTdlsZoomPercent);
            vm.Preferences.SetAssignmentTint(snapshotAssignmentTintEnabled, snapshotAssignmentTintColor);
            vm.Preferences.SetUnassignedTint(snapshotUnassignedTintEnabled, snapshotUnassignedTintColor);
            vm.Preferences.SetSelectedColor(snapshotSelectedColor);
            SyncAllRadarViewTint();
        }

        vm.IsSettingsPreviewActive = false;

        return;

        void OnPreview()
        {
            if (settingsVm is null)
            {
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                vm.Ground.ColorScheme = settingsVm.GetCurrentGroundColors();
                vm.Ground.SatelliteImageBrightness = settingsVm.GroundSatelliteImageBrightness;
                vm.Ground.VideoMapOverlayBrightness = settingsVm.GroundVideoMapOverlayBrightness;
                vm.Ground.YaatLayoutBrightness = settingsVm.GroundYaatLayoutBrightness;
                vm.DataGridScale = settingsVm.DataGridFontSize / 12.0;
                vm.TerminalFontSize = settingsVm.TerminalFontSize;
                App.ApplyInterfaceFontSize(settingsVm.InterfaceFontSize);
                vm.ApplyStripsZoomPercent(settingsVm.StripsZoomPercent);
                vm.ApplyTdlsZoomPercent(settingsVm.TdlsZoomPercent);
                vm.Preferences.SetAssignmentTint(settingsVm.AssignmentTintEnabled, settingsVm.AssignmentTintColor);
                vm.Preferences.SetUnassignedTint(settingsVm.UnassignedTintEnabled, settingsVm.UnassignedTintColor);
                vm.Preferences.SetSelectedColor(settingsVm.SelectedColor);
                SyncAllRadarViewTint();
            });
        }
    }

    private void OnNewWeatherClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        OpenWeatherEditor(WeatherTimelineEditorViewModel.CreateEmpty(vm.Preferences.ArtccId), vm);
    }

    private void OnEditWeatherClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.ActiveWeatherJson is null)
        {
            return;
        }

        OpenWeatherEditor(WeatherTimelineEditorViewModel.FromJson(vm.ActiveWeatherJson), vm);
    }

    private void OpenWeatherEditor(WeatherTimelineEditorViewModel editorVm, MainViewModel vm)
    {
        if (_weatherEditorWindow is not null)
        {
            _weatherEditorWindow.Activate();
            return;
        }

        _weatherEditorWindow = new WeatherTimelineEditorWindow(
            editorVm,
            vm.Preferences,
            async (json, name) =>
            {
                await vm.LoadWeatherFromJsonAsync(json, name);
            }
        );
        _weatherEditorWindow.Closing += (_, _) => _weatherEditorWindow = null;
        _weatherEditorWindow.Show();
    }

    private void OnEditArrivalGeneratorsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.HasScenario)
        {
            return;
        }

        if (_arrivalGeneratorsEditorWindow is not null)
        {
            _arrivalGeneratorsEditorWindow.Activate();
            return;
        }

        var editorVm = new ArrivalGeneratorsEditorViewModel(vm.LatestArrivalGenerators, vm.LatestPositions, vm.LatestRunwayIds);

        _arrivalGeneratorsEditorWindow = new ArrivalGeneratorsEditorWindow(
            editorVm,
            vm.Preferences,
            async json => await vm.Connection.LoadArrivalGeneratorsAsync(json),
            () => vm.LoadedScenarioJson
        );
        _arrivalGeneratorsEditorWindow.Closing += (_, _) => _arrivalGeneratorsEditorWindow = null;
        _arrivalGeneratorsEditorWindow.Show();
    }

    private Key _takeControlKey = Key.T;
    private KeyModifiers _takeControlModifiers = KeyModifiers.Control;
    private Key _alwaysOnTopKey = Key.T;
    private KeyModifiers _alwaysOnTopModifiers = KeyModifiers.Control | KeyModifiers.Shift;
    private Key _quickBookmarkKey = Key.B;
    private KeyModifiers _quickBookmarkModifiers = KeyModifiers.Control;
    private Key _pttKey = Key.RightCtrl;
    private KeyModifiers _pttModifiers = KeyModifiers.None;

    private void OnMainWindowTitleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.WindowTitle) && sender is MainViewModel vm)
        {
            _geometryHelper.SetBaseTitle(vm.WindowTitle);
        }
    }

    // Global keyboard hook — lets PTT trigger while another application has focus. Started from
    // the constructor and disposed on window close. The in-window OnKeyDown/OnKeyUp handlers stay
    // as a backup path (when the hook fails to start or the user doesn't grant accessibility
    // permissions on macOS).
    private GlobalKeyHookService? _globalKeyHook;

    // Edge-triggered PTT flag. The global hook delivers auto-repeat key presses as separate
    // events even though the physical key is held, and we want StartPtt to fire exactly once
    // per hold-down. Flipped back to false in the KeyUp handler.
    private bool _globalPttActive;

    private void SyncAllRadarViewTint()
    {
        // Embedded RadarView
        foreach (var rv in this.GetVisualDescendants().OfType<RadarView>())
        {
            rv.SyncAssignmentTint();
        }

        // Pop-out RadarView
        if (_radarViewWindow is not null)
        {
            var poppedRadar = _radarViewWindow.GetVisualDescendants().OfType<RadarView>().FirstOrDefault();
            poppedRadar?.SyncAssignmentTint();
        }
    }

    private void ApplyKeybinds(UserPreferences prefs)
    {
        var cmdView = this.FindControl<CommandInputView>("CommandInputView");
        if (cmdView is not null && SettingsViewModel.ParseKeybind(prefs.AircraftSelectKey, out var selKey, out var selMods))
        {
            cmdView.SetAircraftSelectKeybind(selKey, selMods);
        }

        if (SettingsViewModel.ParseKeybind(prefs.TakeControlKey, out var takeKey, out var takeMods))
        {
            _takeControlKey = takeKey;
            _takeControlModifiers = takeMods;
        }

        if (SettingsViewModel.ParseKeybind(prefs.AlwaysOnTopKey, out var topKey, out var topMods))
        {
            _alwaysOnTopKey = topKey;
            _alwaysOnTopModifiers = topMods;
        }

        if (SettingsViewModel.ParseKeybind(prefs.QuickBookmarkKey, out var bmKey, out var bmMods))
        {
            _quickBookmarkKey = bmKey;
            _quickBookmarkModifiers = bmMods;
        }

        if (SettingsViewModel.ParseKeybind(prefs.PttKey, out var pttKey, out var pttMods))
        {
            _pttKey = pttKey;
            _pttModifiers = pttMods;
        }
    }

    /// <summary>
    /// Returns true if the pressed key matches the configured PTT keybind. Modifier-only keybinds
    /// (e.g. RightCtrl) are matched by key alone because when Ctrl is pressed, <c>e.KeyModifiers</c>
    /// also includes the Control flag — comparing modifiers strictly would never match.
    /// </summary>
    private bool IsPttKeyEvent(KeyEventArgs e)
    {
        if (e.Key != _pttKey)
        {
            return false;
        }

        return IsModifierOnlyKey(_pttKey) || e.KeyModifiers == _pttModifiers;
    }

    private static bool IsModifierOnlyKey(Key key)
    {
        return key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    /// <summary>
    /// Confirms the destructive, playback-ending Take Control. Wired into
    /// <see cref="MainViewModel.TakeControlConfirmation"/> so the command gates on it. Returns
    /// true when the user clicks "Take Control", false on Cancel/Esc.
    /// </summary>
    private async Task<bool> ConfirmTakeControlAsync()
    {
        var dialog = new Window
        {
            Title = "End playback and take control?",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var confirmButton = new Button
        {
            Content = "Take Control",
            Width = 110,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 80,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            IsCancel = true,
        };

        var confirmed = false;
        confirmButton.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Taking control stops the replay and discards the playback timeline, switching to live control. This can't be undone.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { confirmButton, cancelButton },
                },
            },
        };

        await dialog.ShowDialog(this);
        return confirmed;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isConfirmedClose && DataContext is MainViewModel vm && vm.IsConnected && vm.IsInRoom && vm.HasScenario)
        {
            e.Cancel = true;

            var dialog = new Window
            {
                Title = "Confirm Exit",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false,
            };

            var yesButton = new Button
            {
                Content = "Exit",
                Width = 80,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            var noButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };

            var confirmed = false;
            yesButton.Click += (_, _) =>
            {
                confirmed = true;
                dialog.Close();
            };
            noButton.Click += (_, _) => dialog.Close();

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "A scenario is currently loaded. Are you sure you want to exit?",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yesButton, noButton },
                    },
                },
            };

            await dialog.ShowDialog(this);

            if (confirmed)
            {
                _isConfirmedClose = true;
                Close();
            }
        }

        // Sticky: only ever set to true, never reset to false. OnClosing is async void and
        // re-enters itself when the confirm-exit dialog is shown — Entry #1 cancels the close
        // (e.Cancel=true), Entry #2 fires from the inner Close() with a fresh args object and
        // correctly sets the flag. Without this guard, Entry #1 resumes after the await and
        // overwrites the flag back to false using its stale e1.Cancel=true, which makes the
        // child pop-out windows treat the shutdown as a manual close and clobber their
        // popped-out state in preferences.
        if (!e.Cancel)
        {
            _isMainWindowClosing = true;
            // Mirror to the app-wide flag so pop-out windows' Closing handlers see the same
            // signal whether the shutdown originated here (X button) or from File > Exit.
            AppLifetime.MarkShuttingDown();
        }

        if (_isMainWindowClosing && _globalKeyHook is { } hook)
        {
            hook.KeyDown -= OnGlobalKeyDown;
            hook.KeyUp -= OnGlobalKeyUp;
            hook.Dispose();
            _globalKeyHook = null;
        }

        if (_isMainWindowClosing && _autoConnectCts is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
            _autoConnectCts = null;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Handles a global-hook PTT key press (fires regardless of which window has focus). Marshals
    /// to the UI thread because <see cref="GlobalKeyHookService"/> raises events on a background
    /// thread. Edge-triggered via <see cref="_globalPttActive"/> so auto-repeat key presses only
    /// trigger <c>StartPtt</c> once per hold.
    /// </summary>
    private void OnGlobalKeyDown(Key key, KeyModifiers modifiers)
    {
        if (!MatchesPttBinding(key, modifiers))
        {
            return;
        }

        if (_globalPttActive)
        {
            return;
        }

        _globalPttActive = true;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SpeechService.StartPtt();
            }
        });
    }

    private void OnGlobalKeyUp(Key key, KeyModifiers modifiers)
    {
        // Match on key alone for the release path — modifiers may have already been released
        // before the main key, which would cause MatchesPttBinding (which compares modifiers for
        // non-modifier-only keys) to reject the event and leave PTT stuck on.
        if (key != _pttKey)
        {
            return;
        }

        if (!_globalPttActive)
        {
            return;
        }

        _globalPttActive = false;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainViewModel vm && vm.SpeechService.Status is SpeechStatus.Recording)
            {
                vm.SpeechService.StopPtt();
            }
        });
    }

    private bool MatchesPttBinding(Key key, KeyModifiers modifiers)
    {
        if (key != _pttKey)
        {
            return false;
        }

        return IsModifierOnlyKey(_pttKey) || modifiers == _pttModifiers;
    }

    /// <summary>
    /// Focuses whichever command input is currently visible: the embedded one in MainWindow when
    /// the terminal is docked, or the popped-out <see cref="TerminalWindow"/>'s when it isn't.
    /// Activating the owning window brings it forward so the focused box is on the active surface.
    /// Wired to <see cref="MainViewModel.RequestCommandInputFocus"/>.
    /// </summary>
    private void FocusActiveCommandInput()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.IsTerminalDocked)
        {
            Activate();
            this.FindControl<CommandInputView>("CommandInputView")?.FocusCommandInput();
        }
        else
        {
            _terminalWindow?.FocusCommandInput();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (
            e.Key == _takeControlKey
            && e.KeyModifiers == _takeControlModifiers
            && DataContext is MainViewModel takeVm
            && takeVm.SelectedAircraft is not null
        )
        {
            _ = takeVm.TakeControlAsync(takeVm.SelectedAircraft.Callsign);
            e.Handled = true;
            return;
        }

        if (e.Key == _alwaysOnTopKey && e.KeyModifiers == _alwaysOnTopModifiers)
        {
            _geometryHelper.ToggleTopmost();
            e.Handled = true;
            return;
        }

        if (
            e.Key == _quickBookmarkKey
            && e.KeyModifiers == _quickBookmarkModifiers
            && DataContext is MainViewModel bookmarkVm
            && bookmarkVm.IsTimelineAvailable
        )
        {
            bookmarkVm.QuickAddBookmarkCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // PTT start. Windows auto-repeats held keys, so StartPtt is a no-op after the first press
        // and SpeechRecognitionService only actually records once. Works even when the command
        // input has focus because this handler runs at the Window level.
        if (IsPttKeyEvent(e) && DataContext is MainViewModel pttVm)
        {
            if (pttVm.SpeechService.StartPtt())
            {
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (IsPttKeyEvent(e) && DataContext is MainViewModel pttVm)
        {
            if (pttVm.SpeechService.Status is SpeechStatus.Recording)
            {
                pttVm.SpeechService.StopPtt();
                e.Handled = true;
                return;
            }
        }

        base.OnKeyUp(e);
    }
}
