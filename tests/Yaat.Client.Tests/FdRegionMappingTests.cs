using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class FdRegionMappingTests
{
    [Theory]
    [InlineData("ZOA", "sfo")]
    [InlineData("ZLA", "sfo")]
    [InlineData("ZBW", "bos")]
    [InlineData("ZNY", "bos")]
    [InlineData("ZTL", "mia")]
    [InlineData("ZAU", "chi")]
    [InlineData("ZFW", "dfw")]
    [InlineData("ZDV", "slc")]
    [InlineData("ZAN", "alaska")]
    public void GetRegion_KnownArtcc_ReturnsRegion(string artccId, string expected)
    {
        Assert.Equal(expected, FdRegionMapping.GetRegion(artccId));
    }

    [Fact]
    public void GetRegion_UnknownArtcc_ReturnsNull()
    {
        Assert.Null(FdRegionMapping.GetRegion("ZZZZ"));
    }

    [Fact]
    public void GetRegion_CaseInsensitive()
    {
        Assert.Equal("sfo", FdRegionMapping.GetRegion("zoa"));
        Assert.Equal("sfo", FdRegionMapping.GetRegion("Zoa"));
    }

    [Fact]
    public void GetRegion_EmptyString_ReturnsNull()
    {
        Assert.Null(FdRegionMapping.GetRegion(""));
    }
}
