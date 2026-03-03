using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

        var recentItem = this.FindControl<MenuItem>("RecentScenariosMenuItem");
        if (recentItem is not null)
        {
            recentItem.IsEnabled = vm.Preferences.RecentScenarios.Count > 0;
            PopulateRecentScenarios(recentItem, vm);
            recentItem.SubmenuOpened += OnRecentScenariosSubmenuOpened;
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

        if (App.AutoConnect)
        {
            _ = vm.ConnectCommand.ExecuteAsync(null);
        }
    }

    private void SetupDataGrid(DataGrid dataGrid, MainViewModel vm)
    {
        foreach (var col in dataGrid.Columns)
        {
            if (col.Header is string header && header == "Status")
            {
                col.CustomSortComparer = StatusSortComparer.Instance;
                break;
            }
        }

        RestoreGridLayout(dataGrid, vm.Preferences);

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
        foreach (var col in dataGrid.Columns)
        {
            if (!col.Width.IsAuto)
            {
                columnWidths ??= [];
                columnWidths[GetColumnKey(col)] = col.ActualWidth;
            }
        }

        prefs.SetGridLayout(
            new SavedGridLayout
            {
                ColumnOrder = columnOrder,
                SortColumn = _sortColumnKey,
                SortDirection = _sortDirection,
                ColumnWidths = columnWidths,
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
                col.ClearSort();
            }
        }
        finally
        {
            _restoringGrid = false;
        }
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

            header.ContextFlyout = flyout;

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
            recentItem.IsEnabled = vm.Preferences.RecentScenarios.Count > 0;
        }
    }

    private async void OnLoadScenarioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var window = new LoadScenarioWindow(vm.Preferences);
        var filePath = await window.ShowDialog<string?>(this);
        if (filePath is not null)
        {
            vm.ScenarioFilePath = filePath;
            await vm.LoadScenarioCommand.ExecuteAsync(null);
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
        if (recent.Count == 0)
        {
            menu.IsEnabled = false;
            menu.Items.Add(new MenuItem { Header = "(No recent scenarios)", IsEnabled = false });
            return;
        }

        menu.IsEnabled = true;
        foreach (var entry in recent)
        {
            var item = new MenuItem { Header = entry.Name, Tag = entry.FilePath };
            item.Click += OnRecentScenarioClick;
            ToolTip.SetTip(item, entry.FilePath);
            menu.Items.Add(item);
        }
    }

    private async void OnRecentScenarioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path } || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (!File.Exists(path))
        {
            vm.StatusText = $"File not found: {path}";
            return;
        }

        vm.ScenarioFilePath = path;
        await vm.LoadScenarioCommand.ExecuteAsync(null);
    }

    private async void OnLoadWeatherClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var window = new LoadWeatherWindow(vm.Preferences);
        var filePath = await window.ShowDialog<string?>(this);
        if (filePath is not null)
        {
            await vm.LoadWeatherCommand.ExecuteAsync(filePath);
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
    }
}
