using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Tools → Copy traffic feed URL puts the room's datafeed URL on the clipboard: the connected server's base URL plus
/// <c>/feed/{roomId}/v3/vatsim-data.json</c>, and only while the client is connected to a server and in a room.
/// </summary>
public class MainViewModelTrafficFeedUrlTests
{
    private const string ServerUrl = "https://host:5130/";
    private const string RoomId = "ABCD1234";

    private static MainViewModel ConnectedInRoom() =>
        new(new FakeFilePickerService())
        {
            ConnectedServerUrl = ServerUrl,
            IsConnected = true,
            ActiveRoomId = RoomId,
        };

    [Theory]
    [InlineData("https://host:5130")]
    [InlineData("https://host:5130/")]
    public void TrafficFeedUrl_IsServerBasePlusRoomFeedPath(string serverBaseUrl) =>
        Assert.Equal("https://host:5130/feed/ABCD1234/v3/vatsim-data.json", MainViewModel.BuildTrafficFeedUrl(serverBaseUrl, "ABCD1234"));

    [AvaloniaFact]
    public void CopyTrafficFeedUrlCommand_IsDisabledWithNoRoom()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { ConnectedServerUrl = ServerUrl, IsConnected = true };

        Assert.False(vm.CopyTrafficFeedUrlCommand.CanExecute(null));

        vm.ActiveRoomId = RoomId;
        Assert.True(vm.CopyTrafficFeedUrlCommand.CanExecute(null));

        vm.ActiveRoomId = null;
        Assert.False(vm.CopyTrafficFeedUrlCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void CopyTrafficFeedUrlCommand_IsDisabledWhenDisconnected()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { ConnectedServerUrl = ServerUrl, ActiveRoomId = RoomId };

        Assert.False(vm.CopyTrafficFeedUrlCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void CopyTrafficFeedUrlCommand_IsDisabledWithNoServerUrl()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { IsConnected = true, ActiveRoomId = RoomId };

        Assert.False(vm.CopyTrafficFeedUrlCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void IsConnectedChange_RaisesCopyTrafficFeedUrlCanExecuteChanged()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { ConnectedServerUrl = ServerUrl, ActiveRoomId = RoomId };
        bool raised = false;
        vm.CopyTrafficFeedUrlCommand.CanExecuteChanged += (_, _) => raised = true;

        vm.IsConnected = true;

        Assert.True(raised, "CopyTrafficFeedUrlCommand did not raise CanExecuteChanged when IsConnected changed");
    }

    [AvaloniaFact]
    public void ActiveRoomIdChange_RaisesCopyTrafficFeedUrlCanExecuteChanged()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { ConnectedServerUrl = ServerUrl, IsConnected = true };
        bool raised = false;
        vm.CopyTrafficFeedUrlCommand.CanExecuteChanged += (_, _) => raised = true;

        vm.ActiveRoomId = RoomId;

        Assert.True(raised, "CopyTrafficFeedUrlCommand did not raise CanExecuteChanged when ActiveRoomId changed");
    }

    [AvaloniaFact]
    public void ConnectedServerUrlChange_RaisesCopyTrafficFeedUrlCanExecuteChanged()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { IsConnected = true, ActiveRoomId = RoomId };
        bool raised = false;
        vm.CopyTrafficFeedUrlCommand.CanExecuteChanged += (_, _) => raised = true;

        vm.ConnectedServerUrl = ServerUrl;

        Assert.True(raised, "CopyTrafficFeedUrlCommand did not raise CanExecuteChanged when ConnectedServerUrl changed");
        Assert.True(vm.CopyTrafficFeedUrlCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CopyTrafficFeedUrl_WritesTheRoomFeedUrlToTheClipboard()
    {
        MainViewModel vm = ConnectedInRoom();
        var written = new List<string>();
        vm.ClipboardWriter = text =>
        {
            written.Add(text);
            return Task.CompletedTask;
        };

        await vm.CopyTrafficFeedUrlCommand.ExecuteAsync(null);

        Assert.Equal(["https://host:5130/feed/ABCD1234/v3/vatsim-data.json"], written);
        Assert.Equal("Traffic feed URL copied", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task CopyTrafficFeedUrl_ClipboardFailure_SetsErrorStatus()
    {
        MainViewModel vm = ConnectedInRoom();
        vm.ClipboardWriter = _ => throw new InvalidOperationException("clipboard unavailable");

        await vm.CopyTrafficFeedUrlCommand.ExecuteAsync(null);

        Assert.Equal("Failed to copy the traffic feed URL", vm.StatusText);
    }
}
