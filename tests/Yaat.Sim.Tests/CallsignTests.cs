using Xunit;

namespace Yaat.Sim.Tests;

public class CallsignTests
{
    [Theory]
    [InlineData("UAL238")]
    [InlineData("N427MX")]
    [InlineData("JBU-12")]
    [InlineData("ABC")]
    [InlineData("A")]
    [InlineData("1234567")]
    [InlineData("N12345")]
    [InlineData("AAL2839")]
    public void IsValid_AcceptsAlphanumericAndDashUpToSeven(string callsign)
    {
        Assert.True(Callsign.IsValid(callsign), $"Expected '{callsign}' to be valid");
    }

    [Theory]
    [InlineData("*T")]
    [InlineData("*T BRIXX")]
    [InlineData("FOO BAR")]
    [InlineData("FOO/BAR")]
    [InlineData("foo123")]
    [InlineData("UaL238")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("TOOLONGS")]
    [InlineData("BRIXX*")]
    [InlineData(" UAL238")]
    [InlineData("UAL238 ")]
    [InlineData("UAL.238")]
    [InlineData("N42_42")]
    public void IsValid_RejectsInvalid(string? callsign)
    {
        Assert.False(Callsign.IsValid(callsign), $"Expected '{callsign ?? "<null>"}' to be invalid");
    }
}
