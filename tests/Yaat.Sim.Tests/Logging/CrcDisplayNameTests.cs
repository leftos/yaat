using Xunit;
using Yaat.Sim.Logging;

namespace Yaat.Sim.Tests.Logging;

/// <summary>
/// A CRC client's display name is "{callsign} ({realName})"; the client reads the real name back out of it to scrub
/// its logs when the server sent no participant list.
/// </summary>
public class CrcDisplayNameTests
{
    [Theory]
    [InlineData("OAK_TWR", "Jane Doe", "OAK_TWR (Jane Doe)")]
    [InlineData("OAK_TWR", null, "OAK_TWR")]
    [InlineData(null, "Jane Doe", "Jane Doe")]
    [InlineData("", "", "unknown")]
    [InlineData(null, null, "unknown")]
    public void Build_FormatsEveryBranch(string? callsign, string? realName, string expected) =>
        Assert.Equal(expected, CrcDisplayName.Build(callsign, realName));

    [Fact]
    public void TryGetRealName_ReadsBackTheNameBuildWrote()
    {
        Assert.True(CrcDisplayName.TryGetRealName(CrcDisplayName.Build("SFO_GND", "Bob (Rob) Jones"), out string? name));
        Assert.Equal("Bob (Rob) Jones", name);
    }

    [Theory]
    [InlineData("OAK_TWR")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetRealName_WithoutACallsignAndName_FindsNone(string? displayName) =>
        Assert.False(CrcDisplayName.TryGetRealName(displayName, out _));
}
