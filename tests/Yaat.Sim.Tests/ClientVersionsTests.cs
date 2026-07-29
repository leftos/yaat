using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// The client-version gate turns these comparisons into a refusal to connect, so the failure
/// modes matter more than the happy path: a false "too old" locks a working client out of every
/// server, which is worse than letting an incompatible one through to a normal error.
/// </summary>
public class ClientVersionsTests
{
    [Theory]
    [InlineData("0.9.18-beta", 0, 9, 18)]
    [InlineData("0.9.18", 0, 9, 18)]
    [InlineData("0.9.18-beta+1a2b3c4", 0, 9, 18)]
    [InlineData("1.0", 1, 0, 0)]
    [InlineData("2", 2, 0, 0)]
    [InlineData("  0.10.3-alpha  ", 0, 10, 3)]
    public void TryParse_ReadsNumericCoreAndIgnoresSuffix(string input, int major, int minor, int patch)
    {
        Assert.True(ClientVersions.TryParse(input, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("beta")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.2.3")]
    [InlineData("1..3")]
    public void TryParse_RejectsWhatItCannotRead(string? input)
    {
        Assert.False(ClientVersions.TryParse(input, out _));
    }

    [Theory]
    [InlineData("0.9.17-beta", "0.9.18-beta")]
    [InlineData("0.9.9-beta", "0.9.18-beta")]
    [InlineData("0.8.99-beta", "0.9.0-beta")]
    public void IsOlderThan_DetectsAnOutdatedClient(string candidate, string required)
    {
        Assert.True(ClientVersions.IsOlderThan(candidate, required));
    }

    [Theory]
    [InlineData("0.9.18-beta", "0.9.18-beta")]
    [InlineData("0.9.19-beta", "0.9.18-beta")]
    [InlineData("1.0.0", "0.9.18-beta")]
    public void IsOlderThan_AcceptsCurrentOrNewer(string candidate, string required)
    {
        Assert.False(ClientVersions.IsOlderThan(candidate, required));
    }

    // The suffix is deliberately not part of the ordering: a release and its numeric equal are the
    // same version here, where SemVer would rank the prerelease lower and gate out a shipped build.
    [Fact]
    public void IsOlderThan_TreatsPrereleaseAndReleaseAsEqual()
    {
        Assert.False(ClientVersions.IsOlderThan("0.9.18-beta", "0.9.18"));
        Assert.False(ClientVersions.IsOlderThan("0.9.18", "0.9.18-beta"));
    }

    // A dev build whose assembly lacks version metadata reports "unknown"; gating it out would lock
    // developers off every server for a reason they cannot fix from the client.
    [Theory]
    [InlineData("unknown", "0.9.18-beta")]
    [InlineData(null, "0.9.18-beta")]
    [InlineData("0.9.17-beta", "not-a-version")]
    [InlineData("0.9.17-beta", null)]
    public void IsOlderThan_FailsOpenOnAnUnreadableVersion(string? candidate, string? required)
    {
        Assert.False(ClientVersions.IsOlderThan(candidate, required));
    }
}
