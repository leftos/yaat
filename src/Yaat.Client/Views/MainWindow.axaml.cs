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
        DataContext = new MainViewModel();

        var browseBtn = this.FindControl<Button>(
            "BrowseButton");
        if (browseBtn is not null)
            browseBtn.Click += OnBrowseClick;

        var cmdInput = this.FindControl<TextBox>(
            "CommandInput");
        if (cmdInput is not null)
            cmdInput.KeyDown += OnCommandKeyDown;
    }

    private async void OnBrowseClick(
        object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open Scenario",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON Files")
                    {
                        Patterns = ["*.json"]
                    },
                    FilePickerFileTypes.All
                ]
            });

        if (files.Count > 0
            && DataContext is MainViewModel vm)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null)
                vm.ScenarioFilePath = path;
        }
    }

    private void OnCommandKeyDown(
        object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is MainViewModel vm
            && vm.SendCommandCommand.CanExecute(null))
        {
            vm.SendCommandCommand.Execute(null);
        }
    }
}
