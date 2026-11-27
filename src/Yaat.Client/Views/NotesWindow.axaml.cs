using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia.Enums;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

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
    private readonly TextEditor _editor;

    // True while one side's text is being written to the other, so a document change does not echo back into the document.
    private bool _syncingText;

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

        _editor = this.FindControl<TextEditor>("NotesEditor")!;
        BindSearchChords();
        _editor.Text = _viewModel.Text;
        _editor.Document.TextChanged += OnDocumentTextChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        this.FindControl<Button>("CopyAllButton")!.Click += OnCopyAllClick;
        this.FindControl<Button>("SaveAsButton")!.Click += OnSaveAsClick;
        this.FindControl<Button>("ClearButton")!.Click += OnClearClick;
        Button printButton = this.FindControl<Button>("PrintButton")!;
        printButton.IsVisible = OperatingSystem.IsWindows();
        printButton.Click += OnPrintClick;
        viewModel.StampRequested += OnStampRequested;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Closed += OnClosed;
        Opened += (_, _) => FocusEditor();
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
            existing.FocusEditor();
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

    // The find and replace gestures live in the text area's own key bindings, so they only fire once its caret has the
    // focus: a newly opened or re-fronted window puts it there rather than making the user click the text first.
    private void FocusEditor() => _editor.TextArea.Focus();

    // AvaloniaEdit's SearchPanel registers the find and replace commands but no chords for them, so the window binds the
    // gestures those commands carry: Ctrl+F and Ctrl+H, or Cmd+F and Cmd+Alt+F on macOS.
    private void BindSearchChords()
    {
        bool mac = OperatingSystem.IsMacOS();
        KeyModifiers command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyGesture replace = mac ? new KeyGesture(Key.F, command | KeyModifiers.Alt) : new KeyGesture(Key.H, command);
        _editor.TextArea.KeyBindings.Add(
            new KeyBinding { Gesture = new KeyGesture(Key.F, command), Command = AvaloniaEdit.ApplicationCommands.Find }
        );
        _editor.TextArea.KeyBindings.Add(new KeyBinding { Gesture = replace, Command = AvaloniaEdit.ApplicationCommands.Replace });
    }

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

    // The editor's text is the view model's text: a keystroke writes the document into the view model, and a change made
    // elsewhere (a clear, a load) writes the view model back into the document. The flag stops the two echoing each other.
    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_syncingText)
        {
            return;
        }

        _syncingText = true;
        try
        {
            _viewModel.Text = _editor.Document.Text;
        }
        finally
        {
            _syncingText = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName != nameof(NotesViewModel.Text)) || _syncingText)
        {
            return;
        }

        if (string.Equals(_editor.Document.Text, _viewModel.Text, StringComparison.Ordinal))
        {
            return;
        }

        _syncingText = true;
        try
        {
            // The Text setter clears the undo stack, so a Clear cannot be undone back into the notes.
            _editor.Text = _viewModel.Text;
        }
        finally
        {
            _syncingText = false;
        }
    }

    // The stamp replaces the selection, or goes in at the caret when nothing is selected, and the caret ends after it.
    private void OnStampRequested(object? sender, EventArgs e)
    {
        if (_viewModel.CurrentStamp() is not string stamp)
        {
            return;
        }

        int start = _editor.SelectionStart;
        int caret = start + stamp.Length;
        _editor.Document.Replace(start, _editor.SelectionLength, stamp);
        _editor.Select(caret, 0);
        _editor.CaretOffset = caret;
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

    // Save as writes a copy where the user chooses; the autosave file in the app data folder is left as it is.
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        string? path = null;
        try
        {
            IFilePickerService picker = FilePickerFactory.Create(this);
            path = await picker.SaveFileAsync(
                new SaveFileOptions(
                    Title: "Save notes as",
                    SuggestedFileName: "notes.txt",
                    Filters: [new FilePickerFilter("Text file", ["*.txt"])],
                    DefaultExtension: "txt"
                )
            );
            if (path is null)
            {
                return;
            }

            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _viewModel.SaveAs(stream);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "The notes could not be saved to {Path}", path);
            await MessageBoxPresenter.ShowStandardAsync(this, "Notes", $"Could not save the notes: {ex.Message}", ButtonEnum.Ok);
        }
    }

    // Printing goes through the OS: the current text lands in a print file under the app data folder, and the shell's print
    // verb opens it with the default text editor's print action.
    private void OnPrintClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string path = YaatPaths.Combine("notes-print.txt");
            Directory.CreateDirectory(YaatPaths.AppDataRoot);
            File.WriteAllText(path, _viewModel.Text);
            using var printing = Process.Start(
                new ProcessStartInfo
                {
                    FileName = path,
                    Verb = "print",
                    UseShellExecute = true,
                }
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            Log.LogWarning(ex, "The notes could not be printed");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        OpenWindows.Remove(_viewModel);
        _viewModel.StampRequested -= OnStampRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _editor.Document.TextChanged -= OnDocumentTextChanged;
        _viewModel.Flush();
    }
}
