using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Issue #450: the terminal's filter box is wide enough for a whole airline callsign and carries a ✕ button that
/// clears it; the button shows only while there is something to clear.
/// </summary>
public class TerminalSearchBoxTests
{
    private static (Window Window, MainViewModel Vm, TerminalPanelView View) ShowPanel()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var view = new TerminalPanelView();
        var window = new Window
        {
            Width = 900,
            Height = 300,
            Content = view,
            DataContext = vm,
        };
        window.ShowAndRunLayout();
        return (window, vm, view);
    }

    private static T Named<T>(TerminalPanelView view, string name)
        where T : Control => view.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void SearchBox_FitsAFullAirlineCallsign()
    {
        (Window window, MainViewModel _, TerminalPanelView view) = ShowPanel();
        try
        {
            TextBox box = Named<TextBox>(view, "TerminalSearchBox");
            var layout = new TextLayout("SKW5899", new Typeface(box.FontFamily, box.FontStyle, box.FontWeight), box.FontSize, Brushes.White);
            double inner = box.Bounds.Width - box.Padding.Left - box.Padding.Right - box.BorderThickness.Left - box.BorderThickness.Right;

            Assert.True(inner >= layout.Width, $"The filter box leaves {inner:F1}px for text; 'SKW5899' needs {layout.Width:F1}px");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClearButton_ShowsOnlyWithText_AndClearsTheFilter()
    {
        (Window window, MainViewModel vm, TerminalPanelView view) = ShowPanel();
        try
        {
            Button clear = Named<Button>(view, "ClearTerminalSearchButton");
            Assert.False(clear.IsVisible, "Nothing to clear, so the ✕ is hidden");

            vm.TerminalSearchText = "SKW5899";
            Dispatcher.UIThread.RunJobs();
            Assert.True(clear.IsVisible, "A filter is set, so the ✕ shows");

            clear.Command!.Execute(clear.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("", vm.TerminalSearchText);
            Assert.False(clear.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
