using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>The status-bar live-traffic indicator text, one line per feed state.</summary>
public class LiveTrafficStatusTextTests
{
    [Fact]
    public void NotConfigured_AndNull_ReadNotConfigured()
    {
        Assert.Equal("LIVE · not configured", MainViewModel.FormatLiveTrafficStatus(null));
        Assert.Equal("LIVE · not configured", MainViewModel.FormatLiveTrafficStatus(new LiveTrafficStatusDto(false, false, null, 0)));
    }

    [Fact]
    public void Disconnected_ReadsDisconnected()
    {
        Assert.Equal("LIVE · disconnected", MainViewModel.FormatLiveTrafficStatus(new LiveTrafficStatusDto(true, false, 120, 3)));
    }

    [Fact]
    public void Connected_ShowsTracksAndAge()
    {
        Assert.Equal("LIVE · 42 tracks · 3 s", MainViewModel.FormatLiveTrafficStatus(new LiveTrafficStatusDto(true, true, 2.6, 42)));
        Assert.Equal("LIVE · 1 track", MainViewModel.FormatLiveTrafficStatus(new LiveTrafficStatusDto(true, true, null, 1)));
    }
}
