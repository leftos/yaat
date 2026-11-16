using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia.Enums;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// The Notes window: free text for a training note, kept by its <see cref="NotesViewModel"/> until cleared, with a stamp of
/// the sim time and the selected callsign at the caret (the Stamp button or Ctrl+Shift+N). One window per view model.
/// </summary>
public partial class NotesWindow : Window, IAlwaysOnTopToggle
{
    private static readonly ILogger Log = AppLog.CreateLogger<NotesWindow>();
    private static readonly ConditionalWeakTable<NotesViewModel, NotesWindow> OpenWindows = [];

    // The stamp chord, parsed once; null only if the chord text were unparseable.
    private static readonly (Key Key, KeyModifiers Modifiers)? StampKey = KeybindHelper.ParseKeybind(
        WindowHotkeys.NotesStampChord,
        out Key stampKey,
        out KeyModifiers stampModifiers
    )
        ? (stampKey, stampModifiers)
        : null;

    private readonly NotesViewModel _viewModel;
    private readonly WindowGeometryHelper _geometryHelper;
    private readonly TextBox _textBox;

    // Parameterless ctor for the Avalonia designer / XamlLoader and for GuideCapture's offscreen render: empty notes that are
    // never read from or written to the user's file, with no scenario behind them, so the stamp is off.
    public NotesWindow()
        : this(new UserPreferences(), new NotesViewModel(NotesStore.Discarding(), TimeProvider.System, () => null, () => null)) { }

    /// <summary>Creates the window over a view model.</summary>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="viewModel">The notes the window shows.</param>
    public NotesWindow(UserPreferences preferences, NotesViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "Notes", 420, 320);
        _geometryHelper.Restore();

        _textBox = this.FindControl<TextBox>("NotesTextBox")!;
        this.FindControl<Button>("CopyAllButton")!.Click += OnCopyAllClick;
        this.FindControl<Button>("ClearButton")!.Click += OnClearClick;
        viewModel.StampRequested += OnStampRequested;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Closed += OnClosed;
    }

    /// <summary>Shows the window for <paramref name="viewModel"/>, or brings the open one forward.</summary>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="viewModel">The notes the window shows.</param>
    /// <returns>The open window.</returns>
    public static NotesWindow ShowOrActivate(UserPreferences preferences, NotesViewModel viewModel)
    {
        if (OpenWindows.TryGetValue(viewModel, out NotesWindow? existing))
        {
            existing.RestoreAndActivate();
            return existing;
        }

        var window = new NotesWindow(preferences, viewModel);
        OpenWindows.Add(viewModel, window);

        // Shown un-owned (bare Show()) like the other persistent tool windows, so it can go behind the main window.
        window.Show();
        return window;
    }

    /// <inheritdoc />
    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if ((StampKey is not (Key key, KeyModifiers modifiers)) || (e.Key != key) || (e.KeyModifiers != modifiers))
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.StampCommand.CanExecute(null))
        {
            _viewModel.StampCommand.Execute(null);
        }
    }

    // The stamp replaces the selection, or goes in at the caret when nothing is selected, and the caret ends after it.
    private void OnStampRequested(object? sender, EventArgs e)
    {
        int start = _textBox.SelectionStart;
        int end = _textBox.SelectionEnd;
        if (start == end)
        {
            start = end = _textBox.CaretIndex;
        }

        if (_viewModel.InsertStamp(start, end) is not int caret)
        {
            return;
        }
        _textBox.SelectionStart = caret;
        _textBox.SelectionEnd = caret;
        _textBox.CaretIndex = caret;
    }

    // Clearing notes that hold text asks first, and only Yes clears and saves; No, or closing the box, changes nothing.
    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Text.Length > 0)
        {
            ButtonResult result = await MessageBoxPresenter.ShowStandardAsync(
                this,
                "Clear notes?",
                "Clear all notes? This cannot be undone.",
                ButtonEnum.YesNo
            );
            if (result != ButtonResult.Yes)
            {
                return;
            }
        }

        _viewModel.ClearCommand.Execute(null);
    }

    private async void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not IClipboard clipboard)
        {
            return;
        }
        try
        {
            await clipboard.SetTextAsync(_viewModel.Text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            Log.LogWarning(ex, "The notes could not be copied to the clipboard");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        OpenWindows.Remove(_viewModel);
        _viewModel.StampRequested -= OnStampRequested;
        _viewModel.Flush();
    }
}
