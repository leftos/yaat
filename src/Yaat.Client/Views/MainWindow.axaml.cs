using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class MainWindow : Window
{
    private TerminalWindow? _terminalWindow;
    private bool _restoringGrid;
    private string? _sortColumnKey;
    private ListSortDirection? _sortDirection;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        new WindowGeometryHelper(this, vm.Preferences, "Main", 1200, 700).Restore();

        var browseBtn = this.FindControl<Button>("BrowseButton");
        if (browseBtn is not null)
        {
            browseBtn.Click += OnBrowseClick;
        }

        var dataGrid = this.FindControl<DataGrid>("AircraftGrid");
        if (dataGrid is not null)
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

            dataGrid.ColumnReordered += (_, _) =>
                SaveGridLayout(dataGrid, vm.Preferences);
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
        }

        var settingsBtn = this.FindControl<Button>("SettingsButton");
        if (settingsBtn is not null)
        {
            settingsBtn.Click += OnSettingsClick;
        }

        vm.PropertyChanged += OnViewModelPropertyChanged;

        WireDistanceFlyout(vm);
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

        var columnOrder = dataGrid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(GetColumnKey)
            .ToList();

        prefs.SetGridLayout(new SavedGridLayout
        {
            ColumnOrder = columnOrder,
            SortColumn = _sortColumnKey,
            SortDirection = _sortDirection,
        });
    }

    private void WireDistanceFlyout(MainViewModel vm)
    {
        var dataGrid = this.FindControl<DataGrid>("AircraftGrid");
        if (dataGrid is null)
        {
            return;
        }

        // Defer until the grid is fully loaded so column headers exist
        dataGrid.Loaded += (_, _) =>
        {
            var header = this.FindControl<TextBlock>("DistanceHeader");
            if (header is null)
            {
                return;
            }

            var input = new TextBox
            {
                Watermark = "Fix or FRD...",
            };
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

            var flyout = new Flyout
            {
                Content = panel,
                Placement = PlacementMode.BottomEdgeAlignedLeft,
            };

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
                else if (e.Key == Key.Down && listBox.IsVisible
                         && listBox.ItemCount > 0)
                {
                    listBox.SelectedIndex = 0;
                    listBox.Focus();
                    e.Handled = true;
                }
            };

            listBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter
                    && listBox.SelectedItem is string sel)
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

    private static void UpdateDistFixSuggestions(
        MainViewModel vm, TextBox input, ListBox listBox)
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
        int lo = 0, hi = allNames.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (string.Compare(
                allNames[mid], 0, text, 0, text.Length,
                StringComparison.OrdinalIgnoreCase) < 0)
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
            if (!allNames[i].StartsWith(
                text, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            listBox.Items.Add(allNames[i]);
            count++;
        }

        listBox.IsVisible = count > 0;
    }

    private static void ApplyDistanceFix(
        MainViewModel vm, string? text, Flyout flyout)
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
        if (e.PropertyName != nameof(MainViewModel.IsTerminalDocked))
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (!vm.IsTerminalDocked)
        {
            _terminalWindow = new TerminalWindow
            {
                DataContext = vm,
            };
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

    private void OnTerminalWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsTerminalDocked = true;
        }
        _terminalWindow = null;
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open Scenario",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON Files") { Patterns = ["*.json"] }, FilePickerFileTypes.All],
            }
        );

        if (files.Count > 0 && DataContext is MainViewModel vm)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null)
            {
                vm.ScenarioFilePath = path;
            }
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
