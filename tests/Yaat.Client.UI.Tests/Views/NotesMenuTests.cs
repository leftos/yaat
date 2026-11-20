using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            TextBox box = window.FindControl<TextBox>("NotesTextBox")!;
            notes.Text = "ab";
            MainWindowHost.Pump();
            box.Focus();
            box.CaretIndex = 1;
            MainWindowHost.Pump();

            window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control | RawInputModifiers.Shift);
            MainWindowHost.Pump();

            Assert.Equal("a[75:05 N123AB] b", notes.Text);
            Assert.Equal(1 + "[75:05 N123AB] ".Length, box.CaretIndex);
        }
        finally
        {
            window.Close();
        }
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

    // A held clock: the debounced save never fires on its own, so only the stamp and the close can be what the test sees.
    private NotesViewModel NewViewModel() =>
        new(new NotesStore(FilePath, NullLogger.Instance), new FakeTimeProvider(), () => "75:05", () => "N123AB");

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
    }
}
