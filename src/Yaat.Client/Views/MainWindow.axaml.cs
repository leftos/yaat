using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class MainWindow : Window
{
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

        var cmdInput = this.FindControl<TextBox>("CommandInput");
        if (cmdInput is not null)
        {
            cmdInput.KeyDown += OnCommandKeyDown;
        }

        var settingsBtn = this.FindControl<Button>("SettingsButton");
        if (settingsBtn is not null)
        {
            settingsBtn.Click += OnSettingsClick;
        }
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

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel vm)
        {
            vm.SelectedAircraft = null;
            vm.CommandText = "";
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is MainViewModel vm2 && vm2.SendCommandCommand.CanExecute(null))
        {
            vm2.SendCommandCommand.Execute(null);
        }
    }
}
