using Avalonia.Controls;
using Avalonia.Input;

namespace Yaat.Client.Views.Settings;

/// <summary>Settings section. Command verbs: the verb table, Try it out, import, export and reset.</summary>
public partial class CommandVerbsSection : UserControl
{
    public CommandVerbsSection()
    {
        InitializeComponent();
        TryItOutBox.KeyDown += OnTryItOutKeyDown;
    }

    // The box tests a command as it is typed, so Enter has nothing to do there; left unhandled it would bubble
    // to the window's default OK button, which applies every pending setting and closes Settings.
    private static void OnTryItOutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
        }
    }
}
