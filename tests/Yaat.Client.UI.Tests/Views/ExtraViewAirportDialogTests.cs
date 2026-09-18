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
        ExtraViewAirportDialog dialog = Open(["KOAK", "KSFO"], "KZZZ", _ => false);
        try
        {
            Button? ok = dialog.FindControl<Button>("OkButton");
            TextBlock? status = dialog.FindControl<TextBlock>("StatusText");

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
        ExtraViewAirportDialog dialog = Open(["KOAK", "KSFO"], null, _ => true);
        try
        {
            ListBox? list = dialog.FindControl<ListBox>("AirportList");
            TextBox? airportBox = dialog.FindControl<TextBox>("AirportTextBox");
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
        ExtraViewAirportDialog dialog = Open(["KOAK"], null, _ => true);
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;

        TextBox? airportBox = dialog.FindControl<TextBox>("AirportTextBox");
        Button? ok = dialog.FindControl<Button>("OkButton");
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
        ExtraViewAirportDialog dialog = Open(["KOAK"], "KOAK", _ => true);

        Button? cancel = dialog.FindControl<Button>("CancelButton");
        Assert.NotNull(cancel);
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dialog.AirportId);
    }
}
