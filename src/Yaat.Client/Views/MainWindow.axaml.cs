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

        var suggestionList = this.FindControl<ListBox>("SuggestionList");
        if (suggestionList is not null)
        {
            suggestionList.Tapped += OnSuggestionTapped;
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
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var cmdInput = sender as TextBox;
        var input = vm.CommandInput;

        switch (e.Key)
        {
            case Key.Escape:
                if (input.IsSuggestionsVisible)
                {
                    input.DismissSuggestions();
                }
                else
                {
                    vm.SelectedAircraft = null;
                    vm.CommandText = "";
                }
                e.Handled = true;
                return;

            case Key.Up:
                if (input.IsSuggestionsVisible)
                {
                    input.MoveSelection(-1);
                }
                else
                {
                    var older = input.NavigateHistory(-1, vm.CommandText, vm.CommandHistory);
                    if (older is not null)
                    {
                        vm.CommandText = older;
                        MoveCaret(cmdInput, older.Length);
                    }
                }
                e.Handled = true;
                return;

            case Key.Down:
                if (input.IsSuggestionsVisible)
                {
                    input.MoveSelection(1);
                }
                else
                {
                    var newer = input.NavigateHistory(1, vm.CommandText, vm.CommandHistory);
                    if (newer is not null)
                    {
                        vm.CommandText = newer;
                        MoveCaret(cmdInput, newer.Length);
                    }
                }
                e.Handled = true;
                return;

            case Key.Tab:
                if (input.IsSuggestionsVisible)
                {
                    if (input.SelectedSuggestionIndex < 0 && input.Suggestions.Count > 0)
                    {
                        input.SelectedSuggestionIndex = 0;
                    }

                    var text = input.AcceptSuggestion(vm.CommandText);
                    if (text is not null)
                    {
                        vm.CommandText = text;
                        MoveCaret(cmdInput, text.Length);
                    }
                }
                e.Handled = true;
                return;

            case Key.Enter:
                input.DismissSuggestions();
                if (vm.SendCommandCommand.CanExecute(null))
                {
                    vm.SendCommandCommand.Execute(null);
                }
                e.Handled = true;
                return;
        }
    }

    private void OnSuggestionTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var text = vm.CommandInput.AcceptSuggestion(vm.CommandText);
        if (text is not null)
        {
            vm.CommandText = text;
            var cmdInput = this.FindControl<TextBox>("CommandInput");
            MoveCaret(cmdInput, text.Length);
            cmdInput?.Focus();
        }
    }

    private static void MoveCaret(TextBox? textBox, int position)
    {
        if (textBox is not null)
        {
            textBox.CaretIndex = position;
        }
    }
}
