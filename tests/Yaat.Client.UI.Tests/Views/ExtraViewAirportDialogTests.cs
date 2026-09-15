using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The base-airport picker a new Radar/Ground View window opens behind (issue #434): the ARTCC list fills
/// the text box, the text box is authoritative, and Open refuses an airport the navigation database does
/// not know.
/// </summary>
public class ExtraViewAirportDialogTests
{
    private static ExtraViewAirportDialog Open(IReadOnlyList<string> airports, string? initial, Func<string, bool> isKnownAirport)
    {
        var dialog = new ExtraViewAirportDialog("New Radar Window", airports, initial, isKnownAirport);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    [AvaloniaFact]
    public void Ok_IsDisabled_WhileTheAirportIsUnknown()
    {
        var dialog = Open(["KOAK", "KSFO"], "KZZZ", _ => false);
        try
        {
            var ok = dialog.FindControl<Button>("OkButton");
            var status = dialog.FindControl<TextBlock>("StatusText");

            Assert.NotNull(ok);
            Assert.False(ok.IsEnabled);
            Assert.NotNull(status);
            Assert.True(status.IsVisible);
            Assert.Equal("Unknown airport", status.Text);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void SelectingAListItem_FillsTheTextBox()
    {
        var dialog = Open(["KOAK", "KSFO"], null, _ => true);
        try
        {
            var list = dialog.FindControl<ListBox>("AirportList");
            var airportBox = dialog.FindControl<TextBox>("AirportTextBox");
            Assert.NotNull(list);
            Assert.NotNull(airportBox);

            list.SelectedItem = "KSFO";
            Dispatcher.UIThread.RunJobs();

            // The text box is what Open reads, so a list pick has to land in it.
            Assert.Equal("KSFO", airportBox.Text);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Ok_SetsTheUpperCasedAirportAndCloses()
    {
        var dialog = Open(["KOAK"], null, _ => true);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;

        var airportBox = dialog.FindControl<TextBox>("AirportTextBox");
        var ok = dialog.FindControl<Button>("OkButton");
        Assert.NotNull(airportBox);
        Assert.NotNull(ok);

        airportBox.Text = " ksjc ";
        Dispatcher.UIThread.RunJobs();
        ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // Free text is accepted for any known airport, trimmed and upper-cased for the instance.
        Assert.Equal("KSJC", dialog.AirportId);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void Cancel_LeavesTheAirportNull()
    {
        var dialog = Open(["KOAK"], "KOAK", _ => true);

        var cancel = dialog.FindControl<Button>("CancelButton");
        Assert.NotNull(cancel);
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dialog.AirportId);
    }
}
