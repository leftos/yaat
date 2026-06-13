using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Active-position (TCP) indicator/dropdown in the terminal input bar (issue #199 follow-up).
/// The selection mirrors the server's persistent active position: server-originated updates
/// (bootstrap seed / <c>PositionDisplayChanged</c>) must NOT echo an <c>AS</c> command, while a
/// user pick from the dropdown must send one. A unit-constructed VM has no live server connection,
/// so an attempted <c>AS</c> send fails and surfaces as an "AS error" in <see cref="MainViewModel.StatusText"/>;
/// that makes StatusText the distinguishing observable between the two paths.
/// </summary>
public class MainViewModelActivePositionTests
{
    private static OnlineControllerDto Controller(string? tcp) =>
        new("OAK_TWR", "OAK Tower", "120.000", "OAK", "Oakland", "Tower", tcp, null, "Real Name", true, true);

    [AvaloniaFact]
    public void SetActiveTcpFromServer_SeedsIndicator_WithoutSendingAs()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.StatusText = "sentinel";

        vm.SetActiveTcpFromServer("3Y");

        Assert.Equal("3Y", vm.ActiveTcp);
        Assert.True(vm.ShowActiveTcpSelector);
        Assert.Contains("3Y", vm.ActiveTcpOptions);
        Assert.Equal("sentinel", vm.StatusText); // server-originated update must not echo an AS command
    }

    [AvaloniaFact]
    public void SelectingTcp_SendsAsCommand()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetActiveTcpFromServer("3Y");
        vm.StatusText = "sentinel";

        // Simulate the dropdown writing a new selection back to the bound property.
        vm.ActiveTcp = "4U";

        // No live connection → the AS send fails and surfaces as an AS error, proving the
        // user-pick path attempted to send (unlike the suppressed server path above).
        Assert.StartsWith("AS error", vm.StatusText);
    }

    [AvaloniaFact]
    public void RebuildOptions_KeepsCurrentTcpFirst_AddsOnlineControllers_Deduped()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.OnlineControllers.Add(Controller("4U"));
        vm.OnlineControllers.Add(Controller("3Y")); // same as the current TCP — must dedupe
        vm.OnlineControllers.Add(Controller(null)); // no TCP — excluded

        vm.SetActiveTcpFromServer("3Y");

        Assert.Equal("3Y", vm.ActiveTcpOptions[0]); // current TCP listed first
        Assert.Contains("4U", vm.ActiveTcpOptions);
        Assert.Equal(2, vm.ActiveTcpOptions.Count); // 3Y + 4U; the duplicate and the null-TCP entry are dropped
    }

    [AvaloniaFact]
    public void ClearingActiveTcp_HidesIndicatorAndEmptiesOptions()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetActiveTcpFromServer("3Y");
        Assert.True(vm.ShowActiveTcpSelector);

        vm.SetActiveTcpFromServer(null);

        Assert.Null(vm.ActiveTcp);
        Assert.False(vm.ShowActiveTcpSelector);
        Assert.Empty(vm.ActiveTcpOptions);
    }
}
