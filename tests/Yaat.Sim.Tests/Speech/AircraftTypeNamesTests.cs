using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class AircraftTypeNamesTests
{
    [Fact]
    public void Count_IsAtLeastOneThousand()
    {
        // AircraftSpecs.json has ~2600 designators with some spoken form.
        Assert.True(AircraftTypeNames.Count >= 1000, $"Expected ≥1000 types, got {AircraftTypeNames.Count}");
    }

    // --- TryGetManufacturer ---

    [Theory]
    [InlineData("C172", "cessna")]
    [InlineData("C182", "cessna")]
    [InlineData("C208", "cessna")]
    [InlineData("C25C", "cessna")]
    [InlineData("BE20", "beech")]
    [InlineData("BE36", "beech")]
    [InlineData("BE58", "beech")]
    [InlineData("PA46", "piper")]
    [InlineData("SR22", "cirrus")]
    [InlineData("DA40", "diamond")]
    [InlineData("E50P", "embraer")]
    [InlineData("E55P", "embraer")]
    [InlineData("DHC6", "havilland")]
    public void TryGetManufacturer_KnownType_ReturnsExpected(string icao, string expected)
    {
        Assert.True(AircraftTypeNames.TryGetManufacturer(icao, out var mfr));
        Assert.Equal(expected, mfr);
    }

    [Fact]
    public void TryGetManufacturer_CaseInsensitive()
    {
        Assert.True(AircraftTypeNames.TryGetManufacturer("c172", out var mfr));
        Assert.Equal("cessna", mfr);
    }

    [Fact]
    public void TryGetManufacturer_UnknownType_ReturnsFalse()
    {
        Assert.False(AircraftTypeNames.TryGetManufacturer("ZQX9", out _));
    }

    [Fact]
    public void TryGetManufacturer_EmptyOrNull_ReturnsFalse()
    {
        Assert.False(AircraftTypeNames.TryGetManufacturer("", out _));
        Assert.False(AircraftTypeNames.TryGetManufacturer("   ", out _));
    }

    // --- TryGetFamily ---

    [Theory]
    [InlineData("C172", "skyhawk")]
    [InlineData("C182", "skylane")]
    [InlineData("C208", "caravan")]
    [InlineData("C25A", "citation")]
    [InlineData("C25C", "citation")]
    [InlineData("C500", "citation")]
    [InlineData("C510", "citation")]
    [InlineData("C525", "citation")]
    [InlineData("C550", "citation")]
    [InlineData("C560", "citation")]
    [InlineData("C680", "citation")]
    [InlineData("C750", "citation")]
    [InlineData("BE20", "king air")]
    [InlineData("BE30", "king air")]
    [InlineData("BE9L", "king air")]
    [InlineData("BE36", "bonanza")]
    [InlineData("BE58", "baron")]
    [InlineData("PA46", "malibu")]
    [InlineData("E50P", "phenom")]
    [InlineData("E55P", "phenom")]
    [InlineData("CL30", "challenger")]
    [InlineData("CL60", "challenger")]
    [InlineData("GLEX", "global express")]
    [InlineData("DHC6", "twin otter")]
    public void TryGetFamily_KnownType_ReturnsExpected(string icao, string expected)
    {
        Assert.True(AircraftTypeNames.TryGetFamily(icao, out var fam), $"Expected family for {icao}");
        Assert.Equal(expected, fam);
    }

    [Fact]
    public void TryGetFamily_UnknownType_ReturnsFalse()
    {
        Assert.False(AircraftTypeNames.TryGetFamily("ZQX9", out _));
    }

    // --- GetSpokenNames ---

    [Fact]
    public void GetSpokenNames_C172_ReturnsFamilyThenManufacturer()
    {
        var names = AircraftTypeNames.GetSpokenNames("C172");
        Assert.Equal(2, names.Count);
        Assert.Equal("skyhawk", names[0]); // family first
        Assert.Equal("cessna", names[1]);
    }

    [Fact]
    public void GetSpokenNames_BE20_ReturnsKingAirBigram()
    {
        var names = AircraftTypeNames.GetSpokenNames("BE20");
        Assert.Contains("king air", names);
        Assert.Contains("beech", names);
    }

    [Fact]
    public void GetSpokenNames_C25C_ReturnsCitationAndCessna()
    {
        var names = AircraftTypeNames.GetSpokenNames("C25C");
        Assert.Equal(2, names.Count);
        Assert.Equal("citation", names[0]);
        Assert.Equal("cessna", names[1]);
    }

    [Fact]
    public void GetSpokenNames_UnknownType_ReturnsEmpty()
    {
        Assert.Empty(AircraftTypeNames.GetSpokenNames("ZQX9"));
        Assert.Empty(AircraftTypeNames.GetSpokenNames(""));
        Assert.Empty(AircraftTypeNames.GetSpokenNames(null));
    }
}
