using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The room's auto-accept travels as one delay where a negative value means off; the flyout shows a checkbox and a
/// 0-60 s delay, and keeps the delay while auto-accept is off.
/// </summary>
public class SessionAutoAcceptWireTests
{
    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 12, 12)]
    [InlineData(true, 60, 60)]
    [InlineData(false, 0, -1)]
    [InlineData(false, 12, -1)]
    public void ToWire_SendsTheDelayWhenOn_AndMinusOneWhenOff(bool enabled, int delaySeconds, int expected) =>
        Assert.Equal(expected, SessionAutoAcceptWire.ToWire(enabled, delaySeconds));

    [Theory]
    [InlineData(-1, 7, false, 7)]
    [InlineData(-5, 30, false, 30)]
    [InlineData(0, 7, true, 0)]
    [InlineData(12, 7, true, 12)]
    [InlineData(60, 7, true, 60)]
    public void FromWire_IsOffBelowZero_KeepingThePreviousDelay(int wire, int previousDelay, bool expectedEnabled, int expectedDelay) =>
        Assert.Equal((expectedEnabled, expectedDelay), SessionAutoAcceptWire.FromWire(wire, previousDelay));
}
