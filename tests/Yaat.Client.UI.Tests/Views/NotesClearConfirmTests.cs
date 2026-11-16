using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MsBox.Avalonia.Windows;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Notes window's Clear button asks before it empties notes that hold text: Yes clears and saves as before, No keeps the
/// notes and the file, and empty notes clear with no box at all.
/// </summary>
public sealed class NotesClearConfirmTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("notes-clear-").FullName;

    private string FilePath => Path.Combine(_dir, "notes.txt");

    public void Dispose()
    {
        AutomationMode.IsEnabled = false;
        Directory.Delete(_dir, recursive: true);
    }

    [AvaloniaFact]
    public void ClearWithText_AnsweredNo_KeepsTheTextAndTheSavedNotes()
    {
        AutomationMode.IsEnabled = true;
        File.WriteAllText(FilePath, "abc");
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            ClickClear(window);
            MsBoxWindow box = OpenBox(window);
            Assert.Equal("Clear notes?", box.Title);

            Click(box, "No");

            Assert.Equal("abc", notes.Text);
            Assert.Equal("abc", File.ReadAllText(FilePath));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClearWithText_AnsweredYes_EmptiesTheNotesAndSavesThem()
    {
        AutomationMode.IsEnabled = true;
        File.WriteAllText(FilePath, "abc");
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            ClickClear(window);
            MsBoxWindow box = OpenBox(window);

            Click(box, "Yes");

            Assert.Equal("", notes.Text);
            Assert.Equal("", File.ReadAllText(FilePath));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClearWithNoText_OpensNoBox_AndLeavesTheNotesEmpty()
    {
        AutomationMode.IsEnabled = true;
        NotesViewModel notes = NewViewModel();
        var window = NotesWindow.ShowOrActivate(new UserPreferences(), notes);
        try
        {
            ClickClear(window);

            Assert.Empty(window.OwnedWindows);
            Assert.Equal("", notes.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // A held clock: the debounced save never fires on its own, so only a clear can be what the file sees.
    private NotesViewModel NewViewModel() =>
        new(new NotesStore(FilePath, NullLogger.Instance), new FakeTimeProvider(), () => "75:05", () => "N123AB");

    private static void ClickClear(Window window)
    {
        window.FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MainWindowHost.Pump();
    }

    private static MsBoxWindow OpenBox(Window owner)
    {
        MainWindowHost.Pump();
        MsBoxWindow box = Assert.IsType<MsBoxWindow>(Assert.Single(owner.OwnedWindows));
        Assert.True(box.IsVisible);
        box.UpdateLayout();
        MainWindowHost.Pump();
        return box;
    }

    // The standard view keeps every button in the tree and shows only the ones its ButtonEnum names.
    private static void Click(MsBoxWindow box, string content)
    {
        Button button = Assert.Single(
            box.GetVisualDescendants().OfType<Button>(),
            b => (b.IsEffectivelyVisible) && (string.Equals(b.Content as string, content, StringComparison.Ordinal))
        );
        Point? center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), box);
        Assert.NotNull(center);

        box.MouseDown(center.Value, MouseButton.Left);
        box.MouseUp(center.Value, MouseButton.Left);
        MainWindowHost.Pump();
    }
}
