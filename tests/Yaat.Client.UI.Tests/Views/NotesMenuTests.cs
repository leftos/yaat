using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// View › Bars › Notes…: the item opens one Notes window with or without a scenario, Ctrl+Shift+N stamps the note at the
/// caret, and closing the window writes the text out at once.
/// </summary>
public sealed class NotesMenuTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("notes-ui-").FullName;

    private string FilePath => Path.Combine(_dir, "notes.txt");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [AvaloniaFact]
    public void MenuItem_IsEnabledWithoutAScenario_OpensOneWindow_AndASecondClickReusesIt()
    {
        (MainWindow main, MainViewModel _) = MainWindowHost.Boot();
        NotesWindow? opened = null;
        try
        {
            MenuItem item = main.FindControl<MenuItem>("NotesMenuItem")!;
            Assert.True(item.IsEnabled);

            Click(item);
            opened = Assert.Single(OpenWindows.All.OfType<NotesWindow>());

            Click(item);

            Assert.Same(opened, Assert.Single(OpenWindows.All.OfType<NotesWindow>()));
        }
        finally
        {
            opened?.Close();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void OpeningNotes_FocusesTheEditorText()
    {
        (MainWindow main, MainViewModel _) = MainWindowHost.Boot();
        NotesWindow? opened = null;
        try
        {
            MenuItem item = main.FindControl<MenuItem>("NotesMenuItem")!;

            Click(item);
            opened = Assert.Single(OpenWindows.All.OfType<NotesWindow>());
            TextEditor editor = opened.FindControl<TextEditor>("NotesEditor")!;
            // FocusManager.GetFocusedElement() is the app-wide keyboard focus, so this shows the button took it.
            Button copyAll = opened.FindControl<Button>("CopyAllButton")!;
            copyAll.Focusable = true;
            copyAll.Focus();
            MainWindowHost.Pump();
            Assert.Same(copyAll, opened.FocusManager!.GetFocusedElement());

            main.Activate();
            MainWindowHost.Pump();
            Click(item);

            Assert.Same(opened, Assert.Single(OpenWindows.All.OfType<NotesWindow>()));
            Assert.Same(editor.TextArea, opened.FocusManager!.GetFocusedElement());
        }
        finally
        {
            opened?.Close();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void Stamp_IsOffWithNoScenario_AndOnOnceAScenarioLoads()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        NotesWindow? opened = null;
        try
        {
            Click(main.FindControl<MenuItem>("NotesMenuItem")!);
            opened = Assert.Single(OpenWindows.All.OfType<NotesWindow>());
            NotesViewModel notes = Assert.IsType<NotesViewModel>(opened.DataContext);
            Assert.False(notes.StampCommand.CanExecute(null));

            vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-1", "OAK Ground", "OAK", true, 1, [], [], [], SessionStartUtc: null));
            MainWindowHost.Pump();

            Assert.True(vm.HasScenario);
            Assert.True(notes.StampCommand.CanExecute(null));
        }
        finally
        {
            opened?.Close();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void CtrlShiftN_InsertsTheStampAtTheCaret()
    {
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            TextEditor editor = window.FindControl<TextEditor>("NotesEditor")!;
            notes.Text = "ab";
            MainWindowHost.Pump();
            editor.Focus();
            editor.CaretOffset = 1;
            MainWindowHost.Pump();

            window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control | RawInputModifiers.Shift);
            MainWindowHost.Pump();

            Assert.Equal("a[75:05 N123AB] b", editor.Document.Text);
            Assert.Equal("a[75:05 N123AB] b", notes.Text);
            Assert.Equal(1 + "[75:05 N123AB] ".Length, editor.CaretOffset);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CtrlF_Opens_The_Find_Panel()
    {
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            TextEditor editor = window.FindControl<TextEditor>("NotesEditor")!;

            PressSearchChord(window, editor, FindChord());

            Assert.True(editor.SearchPanel.IsOpened);
            Assert.False(editor.SearchPanel.IsReplaceMode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CtrlH_Opens_The_Find_Panel_In_Replace_Mode()
    {
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            TextEditor editor = window.FindControl<TextEditor>("NotesEditor")!;

            PressSearchChord(window, editor, ReplaceChord());

            Assert.True(editor.SearchPanel.IsOpened);
            Assert.True(editor.SearchPanel.IsReplaceMode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Undo_Cannot_Erase_The_Loaded_Notes()
    {
        File.WriteAllText(FilePath, "abc");
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            TextEditor editor = window.FindControl<TextEditor>("NotesEditor")!;
            MainWindowHost.Pump();
            Assert.Equal("abc", editor.Document.Text);

            Assert.False(editor.Undo());
            MainWindowHost.Pump();

            Assert.Equal("abc", notes.Text);
            Assert.Equal("abc", editor.Document.Text);
        }
        finally
        {
            window.Close();
        }

        MainWindowHost.Pump();
        Assert.Equal("abc", File.ReadAllText(FilePath));
    }

    [AvaloniaFact]
    public void ClosingTheWindow_WritesTheTextAtOnce()
    {
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        notes.Text = "written on close";

        window.Close();
        MainWindowHost.Pump();

        Assert.Equal("written on close", File.ReadAllText(FilePath));
    }

    [AvaloniaFact]
    public void The_Print_Button_Shows_Only_On_Windows()
    {
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            Assert.Equal(OperatingSystem.IsWindows(), window.FindControl<Button>("PrintButton")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // The chords the window binds: Ctrl+F and Ctrl+H, or Cmd+F and Cmd+Alt+F on macOS, with the focus in the editor's text
    // area (the TextEditor itself is not focusable).
    private static (PhysicalKey Key, RawInputModifiers Modifiers) FindChord() =>
        OperatingSystem.IsMacOS() ? (PhysicalKey.F, RawInputModifiers.Meta) : (PhysicalKey.F, RawInputModifiers.Control);

    private static (PhysicalKey Key, RawInputModifiers Modifiers) ReplaceChord() =>
        OperatingSystem.IsMacOS() ? (PhysicalKey.F, RawInputModifiers.Meta | RawInputModifiers.Alt) : (PhysicalKey.H, RawInputModifiers.Control);

    private static void PressSearchChord(Window window, TextEditor editor, (PhysicalKey Key, RawInputModifiers Modifiers) chord)
    {
        editor.TextArea.Focus();
        MainWindowHost.Pump();
        Assert.True(editor.TextArea.IsFocused);

        window.KeyPressQwerty(chord.Key, chord.Modifiers);
        MainWindowHost.Pump();
    }

    // A held clock: the debounced save never fires on its own, so only the stamp and the close can be what the test sees.
    private NotesViewModel NewViewModel() =>
        new(new NotesStore(FilePath, NullLogger.Instance), new FakeTimeProvider(), () => "75:05", () => "N123AB");

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
    }
}
