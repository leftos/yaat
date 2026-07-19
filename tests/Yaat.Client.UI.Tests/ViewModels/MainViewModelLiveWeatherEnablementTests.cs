using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Tests for GitHub issue #302: the "Load Live Weather" menu item stayed permanently greyed out,
/// which the reporter experienced as clicking it doing nothing.
///
/// <see cref="MainViewModel.LoadLiveWeatherCommand"/> gates on four conditions, one of which is
/// the navigation database being ready. Nav data loads asynchronously at startup, so a user who
/// connects and joins a room before it finishes spends both of the other CanExecute triggers
/// (<see cref="MainViewModel.IsConnected"/> and <see cref="MainViewModel.ActiveRoomId"/>) while
/// nav data is still loading. Marking nav data ready must itself re-query CanExecute, otherwise
/// the command stays disabled for the rest of the session.
///
/// The assertion is on <c>CanExecuteChanged</c> rather than <c>CanExecute</c>: the menu item has no
/// IsEnabled binding, so its enabled state is a cached CanExecute result that Avalonia only refreshes
/// when that event fires. <c>CanExecute</c> itself re-evaluates the predicate on every call and so
/// reports the correct answer even when the UI is stuck showing a stale one.
/// </summary>
public class MainViewModelLiveWeatherEnablementTests
{
    [AvaloniaFact]
    public void MarkNavDbReady_AfterConnectAndJoin_RaisesCanExecuteChanged()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetArtccId("ZOA");

        // Order matters: both of the other CanExecute triggers fire while nav data is still loading,
        // leaving the menu item's cached state disabled.
        vm.IsConnected = true;
        vm.ActiveRoomId = "room1";
        Assert.False(vm.LoadLiveWeatherCommand.CanExecute(null));

        var raised = 0;
        vm.LoadLiveWeatherCommand.CanExecuteChanged += (_, _) => raised++;

        vm.MarkNavDbReady();

        Assert.True(raised > 0, "MarkNavDbReady must re-query CanExecute or the menu item stays greyed out");
        Assert.True(vm.LoadLiveWeatherCommand.CanExecute(null));
    }
}
