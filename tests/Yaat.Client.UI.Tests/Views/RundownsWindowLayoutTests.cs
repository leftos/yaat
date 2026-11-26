using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A layout carries the Rundowns window's open state as it carries the Favorites panel's: saving one records whether the
/// window is open, and applying it opens or closes the window.
/// </summary>
public class RundownsWindowLayoutTests
{
    private static readonly HashSet<string> RundownsRowOnly = [ViewSettingsCopyCatalog.LayoutRundownsWindowKey];

    [AvaloniaFact]
    public void CaptureCurrent_WithTheWindowOpen_RecordsItOpen()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            RundownsWindow.ShowOrActivate(vm);
            MainWindowHost.Pump();

            SavedLayout layout = new LayoutService(vm.Preferences).CaptureCurrent("rundowns", vm);

            Assert.True(layout.IsRundownsWindowOpen);
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_OpensTheWindow_AndAppliedClosedClosesIt()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "open", IsRundownsWindowOpen = true }, RundownsRowOnly);
            MainWindowHost.Pump();

            Assert.True(RundownsWindow.IsOpen(vm));

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "closed", IsRundownsWindowOpen = false }, RundownsRowOnly);
            MainWindowHost.Pump();

            Assert.False(RundownsWindow.IsOpen(vm));
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_WithNoSavedState_LeavesTheWindowAsItIs()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            var opened = RundownsWindow.ShowOrActivate(vm);
            MainWindowHost.Pump();

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "old", IsRundownsWindowOpen = null }, RundownsRowOnly);
            MainWindowHost.Pump();

            Assert.True(RundownsWindow.IsOpen(vm));
            Assert.True(opened.IsVisible);
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    private static void TearDown(MainWindow main, MainViewModel vm)
    {
        RundownsWindow.Close(vm);
        MainWindowHost.Pump();
        MainWindowHost.CloseAll(main);
    }
}
