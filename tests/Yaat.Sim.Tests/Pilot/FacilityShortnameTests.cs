using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public class FacilityShortnameTests
{
    [Theory]
    [InlineData("OAK_TWR", "Tower")]
    [InlineData("SFO_ATCT", "Tower")]
    [InlineData("NCT_F_APP", "Approach")]
    [InlineData("NCT_S_DEP", "Departure")]
    [InlineData("OAK_GND", "Ground")]
    [InlineData("ZOA_NV_CTR", "Center")]
    [InlineData("OAK_RMP", "Ramp")]
    [InlineData("OAK_DEL", "Clearance")]
    [InlineData("OAK_CD", "Clearance")]
    [InlineData("OAK_FSS", "Radio")]
    public void From_MapsKnownSuffix(string callsign, string expected)
    {
        Assert.Equal(expected, FacilityShortname.From(callsign));
    }

    [Theory]
    [InlineData("oak_twr", "Tower")]
    [InlineData("OAK_Twr", "Tower")]
    public void From_IsCaseInsensitive(string callsign, string expected)
    {
        Assert.Equal(expected, FacilityShortname.From(callsign));
    }

    [Fact]
    public void From_FallsBackToVerbatimForUnknownSuffix()
    {
        Assert.Equal("OAK_OPS", FacilityShortname.From("OAK_OPS"));
    }

    [Fact]
    public void From_FallsBackToVerbatimWhenNoUnderscore()
    {
        Assert.Equal("UNICOM", FacilityShortname.From("UNICOM"));
    }

    [Fact]
    public void From_HandlesEmptyOrWhitespace()
    {
        Assert.Equal("", FacilityShortname.From(""));
        Assert.Equal("   ", FacilityShortname.From("   "));
    }
}
