using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class CommandInputView : UserControl
{
    private Key _aircraftSelectKey = Key.Add;
    private KeyModifiers _aircraftSelectModifiers = KeyModifiers.None;
    private Popup? _commandPopup;

    public CommandInputView()
    {
        InitializeComponent();
    }

    public void SetAircraftSelectKeybind(Key key, KeyModifiers modifiers)
    {
        _aircraftSelectKey = key;
        _aircraftSelectModifiers = modifiers;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

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

        var sigHelpPrev = this.FindControl<Button>("SigHelpPrev");
        if (sigHelpPrev is not null)
        {
            sigHelpPrev.Click += OnSigHelpPrevClick;
        }

        var sigHelpNext = this.FindControl<Button>("SigHelpNext");
        if (sigHelpNext is not null)
        {
            sigHelpNext.Click += OnSigHelpNextClick;
        }

        var saveFavItem = this.FindControl<MenuItem>("SaveAsFavoriteMenuItem");
        if (saveFavItem is not null)
        {
            saveFavItem.Click += OnSaveAsFavoriteClick;
        }

        // Drive popup IsOpen from code-behind so it respects this view's visibility.
        // Two CommandInputView instances share the same VM — the hidden embedded one
        // must not open its popup (would appear at 0,0).
        _commandPopup = this.FindControl<Popup>("CommandPopup");
        if (DataContext is MainViewModel vm)
        {
            vm.CommandInput.PropertyChanged += OnCommandInputPropertyChanged;
        }

        // Dismiss popups when the parent window loses focus (prevents topmost overlay over other apps)
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Deactivated += OnWindowDeactivated;
        }
    }

    private void OnCommandInputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsPopupVisible" && _commandPopup is not null && DataContext is MainViewModel vm)
        {
            _commandPopup.IsOpen = vm.CommandInput.IsPopupVisible && IsVisible;
        }
    }

    public void FocusCommandInput()
    {
        var cmdInput = this.FindControl<TextBox>("CommandInput");
        cmdInput?.Focus();
    }

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var cmdInput = sender as TextBox;
        var input = vm.CommandInput;

        if (e.Key == _aircraftSelectKey && e.KeyModifiers == _aircraftSelectModifiers)
        {
            input.DismissSuggestions();
            vm.SelectAircraftFromInput();
            e.Handled = true;
            return;
        }

        // Alt+Up/Down: cycle signature help overloads
        if (e.Key is Key.Up or Key.Down && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (input.SignatureHelp.IsVisible && input.SignatureHelp.OverloadCount > 1)
            {
                if (e.Key == Key.Up)
                {
                    input.SignatureHelp.PreviousOverload();
                }
                else
                {
                    input.SignatureHelp.NextOverload();
                }

                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (input.IsSuggestionsVisible || input.SignatureHelp.IsVisible)
                {
                    input.DismissSuggestions();
                    input.SignatureHelp.Dismiss();
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

                    var accepted = input.AcceptSuggestion(vm.CommandText);
                    if (accepted is not null)
                    {
                        vm.CommandText = accepted.Value.Text;
                        MoveCaret(cmdInput, accepted.Value.Caret);
                    }
                }
                e.Handled = true;
                return;

            case Key.Enter:
                if (input.IsSuggestionsVisible && input.SelectedSuggestionIndex >= 0 && vm.Preferences.AutoExpandSuggestionOnEnter)
                {
                    var expanded = input.AcceptSuggestion(vm.CommandText);
                    if (expanded is not null)
                    {
                        vm.CommandText = expanded.Value.Text;
                        MoveCaret(cmdInput, expanded.Value.Caret);
                    }
                }
                input.DismissSuggestions();
                input.SignatureHelp.Dismiss();
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

        var accepted = vm.CommandInput.AcceptSuggestion(vm.CommandText);
        if (accepted is not null)
        {
            vm.CommandText = accepted.Value.Text;
            var cmdInput = this.FindControl<TextBox>("CommandInput");
            MoveCaret(cmdInput, accepted.Value.Caret);
            cmdInput?.Focus();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CommandInput.DismissSuggestions();
            vm.CommandInput.SignatureHelp.Dismiss();
        }
    }

    private void OnSigHelpPrevClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CommandInput.SignatureHelp.PreviousOverload();
        }
    }

    private void OnSigHelpNextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CommandInput.SignatureHelp.NextOverload();
        }
    }

    private void OnSaveAsFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(vm.CommandText))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            var favBar = window.FindControl<FavoritesBarView>("FavoritesBar");
            favBar?.OpenAddFlyoutForCommand(vm.CommandText);
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
