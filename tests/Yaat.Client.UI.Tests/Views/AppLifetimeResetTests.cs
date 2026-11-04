using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A completed <see cref="MainWindow"/> close latches the process-wide <see cref="AppLifetime.IsShuttingDown"/> flag,
/// which the running app never clears because its lifetime is one process. A test that closes its main window has to
/// clear it afterwards (as <see cref="MainWindowHost.CloseAll"/> does), or every later pop-out close in the same test
/// process reads as an app shutdown and keeps its popped-out flag.
/// </summary>
public class AppLifetimeResetTests
{
    [AvaloniaFact]
    public void PopOutClose_AfterAnEarlierTestClosedItsMainWindow_IsAUserClose()
    {
        // No earlier test's close has latched the process-wide flag: a leak here would hide a broken CloseAll.
        Assert.False(AppLifetime.IsShuttingDown, "an earlier test left the shutdown flag latched");

        // An earlier test in the same process: its teardown closes the main window for real, which latches the flag.
        (MainWindow earlier, _) = MainWindowHost.Boot();
        bool earlierClosed = false;
        earlier.Closed += (_, _) => earlierClosed = true;

        MainWindowHost.CloseAll(earlier);

        Assert.True(earlierClosed, "CloseAll really closes the main window");
        Assert.False(AppLifetime.IsShuttingDown, "CloseAll clears the shutdown flag its close latched");

        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsRadarViewPoppedOut = false;
            Dispatcher.UIThread.RunJobs();
            vm.IsRadarViewPoppedOut = true;
            Dispatcher.UIThread.RunJobs();
            RadarViewWindow popOut = main.RadarViewWindow!;
            Assert.NotNull(popOut);

            popOut.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsRadarViewPoppedOut, "a user close of the pop-out reverts its popped-out flag");
            Assert.Null(main.RadarViewWindow);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }
}
