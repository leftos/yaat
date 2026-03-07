using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.Views;

public partial class MainWindow : Window
{
    private TerminalWindow? _terminalWindow;
    private DataGridWindow? _dataGridWindow;
    private GroundViewWindow? _groundViewWindow;
    private RadarViewWindow? _radarViewWindow;
    private WeatherEditorWindow? _weatherEditorWindow;
    private bool _restoringGrid;
    private string? _sortColumnKey;
    private ListSortDirection? _sortDirection;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        new WindowGeometryHelper(this, vm.Preferences, "Main", 1200, 700).Restore();

        var settingsItem = this.FindControl<MenuItem>("SettingsMenuItem");
        if (settingsItem is not null)
        {
            settingsItem.Click += OnSettingsClick;
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

        var approachReportItem = this.FindControl<MenuItem>("ApproachReportMenuItem");
        if (approachReportItem is not null)
        {
            approachReportItem.Click += OnApproachReportClick;
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

        var embeddedView = this.FindControl<DataGridView>("EmbeddedDataGridView");
        var dataGrid = embeddedView?.GetDataGrid();
        if (dataGrid is not null)
        {
            SetupDataGrid(dataGrid, vm);
            vm.GridLayoutReset += () => ResetLiveGrid(dataGrid);
        }

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

        ApplyKeybinds(vm.Preferences);

        if (App.AutoConnect)
        {
            _ = vm.ConnectCommand.ExecuteAsync(null);
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

                var entries = new System.Collections.Generic.List<ColumnEntry>();
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

                var chooser = new ColumnChooserWindow(entries, vm.ShowOnlyActiveAircraft);
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
                    var keyToColumn = new System.Collections.Generic.Dictionary<string, DataGridColumn>();
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

                vm.ShowOnlyActiveAircraft = chooser.ShowOnlyActive;
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
                if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && Services.PlatformHelper.HasActionModifier(e.KeyModifiers)))
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

        var fixDb = vm.CommandInput.FixDb;
        if (fixDb is null || text.Length == 0)
        {
            listBox.IsVisible = false;
            return;
        }

        var allNames = fixDb.AllFixNames;
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
            case nameof(MainViewModel.IsDataGridPoppedOut):
                HandleDataGridPopOut(vm);
                break;
            case nameof(MainViewModel.IsGroundViewPoppedOut):
                HandleGroundViewPopOut(vm);
                break;
            case nameof(MainViewModel.IsRadarViewPoppedOut):
                HandleRadarViewPopOut(vm);
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
        var grid = this.FindControl<Grid>("ContentGrid");

        if (!vm.IsTerminalDocked)
        {
            if (grid is { RowDefinitions.Count: >= 3 })
            {
                grid.RowDefinitions[2].Height = GridLength.Auto;
            }

            _terminalWindow = new TerminalWindow { DataContext = vm };
            _terminalWindow.Closing += OnTerminalWindowClosing;
            _terminalWindow.Show();
        }
        else
        {
            if (grid is { RowDefinitions.Count: >= 3 })
            {
                grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            }

            if (_terminalWindow is not null)
            {
                _terminalWindow.Closing -= OnTerminalWindowClosing;
                _terminalWindow.Close();
                _terminalWindow = null;
            }
        }
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

    private void OpenDataGridWindow(MainViewModel vm)
    {
        _dataGridWindow = new DataGridWindow { DataContext = vm };
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
        _groundViewWindow = new GroundViewWindow { DataContext = vm };
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
        _radarViewWindow = new RadarViewWindow { DataContext = vm };
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

    private void OnTerminalWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsTerminalDocked = true;
        }
        _terminalWindow = null;
    }

    private void OnDataGridWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsDataGridPoppedOut = false;
        }
        _dataGridWindow = null;
    }

    private void OnGroundViewWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsGroundViewPoppedOut = false;
        }
        _groundViewWindow = null;
    }

    private void OnRadarViewWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsRadarViewPoppedOut = false;
        }
        _radarViewWindow = null;
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

        var window = new LoadScenarioWindow(vm.Preferences);
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

    private async void OnRecentScenarioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: Services.RecentScenario entry } || DataContext is not MainViewModel vm)
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
        vm.StatusText = "Fetching scenario…";
        var trainingData = new Services.TrainingDataService();
        var json = await trainingData.GetScenarioJsonAsync(apiScenarioId);
        if (json is not null)
        {
            await vm.LoadScenarioFromJsonAsync(json, displayName ?? apiScenarioId, apiScenarioId);
        }
        else
        {
            vm.StatusText = "Failed to fetch scenario from API";
        }
    }

    private static async Task LoadWeatherFromApiAsync(MainViewModel vm, string apiWeatherId, string? displayName = null)
    {
        vm.StatusText = "Fetching weather…";
        var trainingData = new Services.TrainingDataService();
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
        if (sender is not MenuItem { Tag: Services.RecentWeather entry } || DataContext is not MainViewModel vm)
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

    private async void OnApproachReportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsConnected)
        {
            return;
        }

        try
        {
            var report = await vm.Connection.GetApproachReportAsync();
            if (report is null)
            {
                vm.StatusText = "No approach data available";
                return;
            }

            var window = new ApproachReportWindow();
            new WindowGeometryHelper(window, vm.Preferences, "ApproachReport", 700, 500).Restore();
            window.LoadReport(report);
            await window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Approach report error: {ex.Message}";
        }
    }

    private async void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var dialog = new SettingsWindow(vm.Preferences);
        await dialog.ShowDialog(this);

        vm.RefreshCommandScheme();
        ApplyKeybinds(vm.Preferences);
    }

    private void OnNewWeatherClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        OpenWeatherEditor(ViewModels.WeatherEditorViewModel.CreateEmpty(vm.Preferences.ArtccId), vm);
    }

    private void OnEditWeatherClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.ActiveWeatherJson is null)
        {
            return;
        }

        OpenWeatherEditor(ViewModels.WeatherEditorViewModel.FromJson(vm.ActiveWeatherJson), vm);
    }

    private void OpenWeatherEditor(ViewModels.WeatherEditorViewModel editorVm, MainViewModel vm)
    {
        if (_weatherEditorWindow is not null)
        {
            _weatherEditorWindow.Activate();
            return;
        }

        _weatherEditorWindow = new WeatherEditorWindow(
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

    private Key _focusInputKey = Key.OemTilde;

    private void ApplyKeybinds(UserPreferences prefs)
    {
        var cmdView = this.FindControl<CommandInputView>("CommandInputView");
        if (cmdView is not null && Enum.TryParse<Key>(prefs.AircraftSelectKey, out var key))
        {
            cmdView.SetAircraftSelectKey(key);
        }

        if (Enum.TryParse<Key>(prefs.FocusInputKey, out var focusKey))
        {
            _focusInputKey = focusKey;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == _focusInputKey)
        {
            var cmdView = this.FindControl<CommandInputView>("CommandInputView");
            cmdView?.FocusCommandInput();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
