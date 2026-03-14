using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class CommandInputView : UserControl
{
    private Key _aircraftSelectKey = Key.Add;
    private KeyModifiers _aircraftSelectModifiers = KeyModifiers.None;

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

        // Dismiss popups when the parent window loses focus (prevents topmost overlay over other apps)
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Deactivated += OnWindowDeactivated;
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

        var text = vm.CommandInput.AcceptSuggestion(vm.CommandText);
        if (text is not null)
        {
            vm.CommandText = text;
            var cmdInput = this.FindControl<TextBox>("CommandInput");
            MoveCaret(cmdInput, text.Length);
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

    private static void MoveCaret(TextBox? textBox, int position)
    {
        if (textBox is not null)
        {
            textBox.CaretIndex = position;
        }
    }
}
