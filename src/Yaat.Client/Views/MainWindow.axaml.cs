using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class MainWindow : Window
{
    private TerminalWindow? _terminalWindow;

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
        }

        var settingsBtn = this.FindControl<Button>("SettingsButton");
        if (settingsBtn is not null)
        {
            settingsBtn.Click += OnSettingsClick;
        }

        vm.PropertyChanged += OnViewModelPropertyChanged;
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
