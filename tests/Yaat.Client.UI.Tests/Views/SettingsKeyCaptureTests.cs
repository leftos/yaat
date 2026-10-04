using Avalonia;
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
/// Clicking a key-capture button in Settings and pressing a key combo stores the combo and ends the
/// capture, for every key-capture button, the Quick bookmark key included. While a capture runs, the keys
/// the window's OK and Cancel buttons answer to are captured instead; outside a capture they work as usual.
/// </summary>
public class SettingsKeyCaptureTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void QuickBookmarkKeyButton_CapturesTheComboAndEndsCapture()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();

        try
        {
            ClickQuickBookmarkKeyButton(window);
            Assert.True(window.ViewModel.IsCapturingKey);

            window.DispatchKey(Key.L, RawInputModifiers.Control | RawInputModifiers.Shift);

            Assert.Equal("Ctrl + Shift + L", window.ViewModel.QuickBookmarkKeyDisplay);
            Assert.False(window.ViewModel.IsCapturingKey);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData(Key.Enter, true, true, 0)]
    [InlineData(Key.Escape, true, true, 0)]
    [InlineData(Key.Space, true, true, 0)]
    [InlineData(Key.Escape, false, false, 0)]
    [InlineData(Key.Enter, false, false, 1)]
    public void OkAndCancelKeys_AreCapturedWhileCapturing_AndCloseTheWindowOtherwise(Key key, bool capturing, bool staysOpen, int applies)
    {
        using var scope = new PreferencesFileScope();
        var window = new SettingsWindow();
        int applied = 0;
        window.ViewModel.Applied += () => applied++;
        window.ShowAndRunLayout();

        try
        {
            if (capturing)
            {
                ClickQuickBookmarkKeyButton(window);
                Assert.True(window.ViewModel.IsCapturingKey);
            }

            window.DispatchKey(key);

            Assert.Equal(staysOpen, window.IsVisible);
            Assert.Equal(applies, applied);
            if (capturing)
            {
                Assert.Equal(SettingsViewModel.KeyComboToDisplay(key.ToString()), window.ViewModel.QuickBookmarkKeyDisplay);
                Assert.False(window.ViewModel.IsCapturingKey);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void ClickQuickBookmarkKeyButton(SettingsWindow window)
    {
        window.SelectSection(SettingsSectionId.Keys);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Button button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "QuickBookmarkKeyButton");
        button.BringIntoView();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
