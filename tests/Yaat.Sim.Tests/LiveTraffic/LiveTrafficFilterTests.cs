using Xunit;
using Yaat.Sim.LiveTraffic;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>Canonical-string round-trips and validation for <see cref="LiveTrafficFilter"/>.</summary>
public class LiveTrafficFilterTests
{
    [Fact]
    public void EmptyAndNull_ParseToNone_AndSerializeEmpty()
    {
        Assert.True(LiveTrafficFilter.TryParse(null, out var fromNull, out _));
        Assert.True(LiveTrafficFilter.TryParse("", out var fromEmpty, out _));
        Assert.True(fromNull.IsNone);
        Assert.True(fromEmpty.IsNone);
        Assert.Equal("", LiveTrafficFilter.None.Serialize());
    }

    [Fact]
    public void FullFilter_RoundTripsThroughItsCanonicalString()
    {
        var text = "RULES=VFR;APT=OAK,SFO;MATCH=DEP;NOPLAN=1;CENTER=OAK090010;RADIUS=15";
        Assert.True(LiveTrafficFilter.TryParse(text, out var filter, out var error));
        Assert.Null(error);
        Assert.Equal(LiveTrafficRulesFilter.VfrOnly, filter.Rules);
        Assert.Equal(["OAK", "SFO"], filter.AirportCodes);
        Assert.Equal(LiveTrafficAirportMatch.Departure, filter.AirportMatch);
        Assert.True(filter.IncludeUnplanned);
        Assert.Equal("OAK090010", filter.RadiusCenter);
        Assert.Equal(15, filter.RadiusNm);
        Assert.Equal(text, filter.Serialize());
    }

    [Fact]
    public void Parse_NormalizesCaseWhitespaceAndDuplicates()
    {
        Assert.True(LiveTrafficFilter.TryParse(" rules=ifr ; apt = oak , sfo , OAK ", out var filter, out _));
        Assert.Equal(LiveTrafficRulesFilter.IfrOnly, filter.Rules);
        Assert.Equal(["OAK", "SFO"], filter.AirportCodes);
        Assert.Equal("RULES=IFR;APT=OAK,SFO", filter.Serialize());
    }

    [Theory]
    [InlineData("RULES=MAYBE")]
    [InlineData("APT=X")]
    [InlineData("APT=TOOLONGX")]
    [InlineData("MATCH=SOMETIMES")]
    [InlineData("CENTER=OAK")]
    [InlineData("RADIUS=15")]
    [InlineData("CENTER=OAK;RADIUS=0")]
    [InlineData("CENTER=OAK;RADIUS=9999")]
    [InlineData("CENTER=O A K;RADIUS=15")]
    [InlineData("BOGUS=1")]
    [InlineData("JUSTTEXT")]
    public void Parse_RefusesBadInput_WithAReason(string text)
    {
        Assert.False(LiveTrafficFilter.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AirportModifiers_WithoutAnAirportList_NormalizeAway()
    {
        Assert.True(LiveTrafficFilter.TryParse("MATCH=DEP;NOPLAN=1", out var filter, out _));
        Assert.True(filter.IsNone);
        Assert.Equal("", filter.Serialize());
    }

    [Fact]
    public void Describe_ReadsAsASentenceFragment()
    {
        Assert.Equal("none", LiveTrafficFilter.None.Describe());
        Assert.True(LiveTrafficFilter.TryParse("RULES=VFR;APT=OAK,SFO;NOPLAN=1;CENTER=SUNOL;RADIUS=12.5", out var filter, out _));
        Assert.Equal("VFR only; plans dep/dest OAK or SFO (+ no-plan); within 12.5 nm of SUNOL", filter.Describe());
    }
}
