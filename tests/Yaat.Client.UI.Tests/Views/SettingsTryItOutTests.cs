using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Enter in the Command verbs "Try it out" box stays in the box: it does not reach the window's default OK
/// button, so testing a command neither applies the pending settings nor closes the window.
/// </summary>
public class SettingsTryItOutTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void EnterInTryItOut_LeavesTheWindowOpen_AndAppliesNothing()
    {
        using var scope = new PreferencesFileScope();
        var window = new SettingsWindow();
        int applied = 0;
        window.ViewModel.Applied += () => applied++;
        window.ShowAndRunLayout();

        try
        {
            window.SelectSection(SettingsSectionId.CommandVerbs);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            TextBox box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "TryItOutBox");
            box.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("FH 270");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("FH 270", window.ViewModel.TestCommandInput);

            window.DispatchKey(Key.Enter);

            Assert.True(window.IsVisible, "Enter in Try it out must not close Settings");
            Assert.Equal(0, applied);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
