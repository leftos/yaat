using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class YaatReconnectPolicyTests
{
    private static TimeSpan? Delay(long previousRetryCount, TimeSpan elapsed) =>
        new YaatReconnectPolicy().NextRetryDelay(new RetryContext { PreviousRetryCount = previousRetryCount, ElapsedTime = elapsed });

    [Fact]
    public void EarlyRetries_RampUp()
    {
        Assert.Equal(TimeSpan.Zero, Delay(0, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(2), Delay(1, TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(5), Delay(2, TimeSpan.FromSeconds(4)));
        Assert.Equal(TimeSpan.FromSeconds(10), Delay(3, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void SteadyState_RetriesEvery15Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), Delay(4, TimeSpan.FromSeconds(19)));
        Assert.Equal(TimeSpan.FromSeconds(15), Delay(40, TimeSpan.FromMinutes(9)));
    }

    [Fact]
    public void KeepsTrying_PastDefaultGiveUpWindow()
    {
        // The default WithAutomaticReconnect() policy gives up after ~42s. A deploy is down ~7-10
        // minutes, so the policy must still return a delay well past that point.
        Assert.NotNull(Delay(5, TimeSpan.FromMinutes(1)));
        Assert.NotNull(Delay(45, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void GivesUp_AfterReconnectWindowElapses()
    {
        Assert.Null(Delay(60, TimeSpan.FromMinutes(15)));
        Assert.Null(Delay(80, TimeSpan.FromMinutes(20)));
    }
}
