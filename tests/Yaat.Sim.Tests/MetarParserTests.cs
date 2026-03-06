using Xunit;

namespace Yaat.Sim.Tests;

public class MetarParserTests
{
    // -------------------------------------------------------------------------
    // Parse — visibility
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("KOAK 121853Z 27012KT 10SM FEW025 20/12 A2992", 10.0)]
    [InlineData("KOAK 121853Z 27012KT 3SM BR FEW025 20/12 A2992", 3.0)]
    [InlineData("KOAK 121853Z 27012KT 1/2SM FG FEW025 20/12 A2992", 0.5)]
    [InlineData("KOAK 121853Z 27012KT 1 1/2SM BR FEW025 20/12 A2992", 1.5)]
    [InlineData("KOAK 121853Z 27012KT P6SM FEW025 20/12 A2992", 6.0)]
    public void Parse_Visibility_Correct(string metar, double expected)
    {
        var result = MetarParser.Parse(metar);
        Assert.NotNull(result);
        Assert.Equal(expected, result.VisibilityStatuteMiles);
    }

    // -------------------------------------------------------------------------
    // Parse — ceiling
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ClearSky_NoCeiling()
    {
        var result = MetarParser.Parse("KOAK 121853Z 27012KT 10SM CLR 20/12 A2992");
        Assert.NotNull(result);
        Assert.Null(result.CeilingFeetAgl);
    }

    [Fact]
    public void Parse_SkyClear_NoCeiling()
    {
        var result = MetarParser.Parse("KOAK 121853Z 27012KT 10SM SKC 20/12 A2992");
        Assert.NotNull(result);
        Assert.Null(result.CeilingFeetAgl);
    }

    [Fact]
    public void Parse_FewScattered_NotCeiling()
    {
        var result = MetarParser.Parse("KOAK 121853Z 27012KT 10SM FEW025 SCT040 20/12 A2992");
        Assert.NotNull(result);
        Assert.Null(result.CeilingFeetAgl);
    }

    [Fact]
    public void Parse_Broken_IsCeiling()
    {
        var result = MetarParser.Parse("KOAK 121853Z 27012KT 10SM BKN025 20/12 A2992");
        Assert.NotNull(result);
        Assert.Equal(2500, result.CeilingFeetAgl);
    }

    [Fact]
    public void Parse_Overcast_IsCeiling()
    {
        var result = MetarParser.Parse("KOAK 121853Z 27012KT 10SM OVC010 20/12 A2992");
        Assert.NotNull(result);
        Assert.Equal(1000, result.CeilingFeetAgl);
    }

    [Fact]
    public void Parse_MultipleLayers_LowestBknWins()
    {
        var result = MetarParser.Parse("KOAK 121853Z 27012KT 3SM FEW010 SCT020 BKN035 OVC050 20/12 A2992");
        Assert.NotNull(result);
        Assert.Equal(3500, result.CeilingFeetAgl);
    }

    // -------------------------------------------------------------------------
    // Parse — station ID
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_StationId_Extracted()
    {
        var result = MetarParser.Parse("KSFO 121853Z 27012KT 10SM CLR 20/12 A2992");
        Assert.NotNull(result);
        Assert.Equal("KSFO", result.StationId);
    }

    // -------------------------------------------------------------------------
    // Parse — malformed / empty
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(MetarParser.Parse(null));
    }

    [Fact]
    public void Parse_Empty_ReturnsNull()
    {
        Assert.Null(MetarParser.Parse(""));
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        Assert.Null(MetarParser.Parse("KOAK"));
    }

    // -------------------------------------------------------------------------
    // FindStation
    // -------------------------------------------------------------------------

    [Fact]
    public void FindStation_MatchesByIcao()
    {
        var metars = new[] { "KSFO 121853Z 27012KT 10SM CLR 20/12 A2992", "KOAK 121853Z 27012KT 3SM BKN025 20/12 A2992" };

        var result = MetarParser.FindStation(metars, "OAK");
        Assert.NotNull(result);
        Assert.Equal("KOAK", result.StationId);
        Assert.Equal(3.0, result.VisibilityStatuteMiles);
    }

    [Fact]
    public void FindStation_NoMatch_ReturnsNull()
    {
        var metars = new[] { "KSFO 121853Z 27012KT 10SM CLR 20/12 A2992" };
        Assert.Null(MetarParser.FindStation(metars, "LAX"));
    }

    // -------------------------------------------------------------------------
    // ToIcao
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("OAK", "KOAK")]
    [InlineData("KOAK", "KOAK")]
    [InlineData("koak", "KOAK")]
    [InlineData("SFO", "KSFO")]
    public void ToIcao_ConvertCorrectly(string input, string expected)
    {
        Assert.Equal(expected, MetarParser.ToIcao(input));
    }

    // -------------------------------------------------------------------------
    // Parse — vertical visibility (VV / indefinite ceiling)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("KSFO 121853Z 04006KT 1/8SM FG VV006 02/02 A3037", 600)]
    [InlineData("KOAK 121853Z 04006KT 1/4SM FG VV002 02/02 A3037", 200)]
    [InlineData("KJFK 121853Z 04006KT 1/2SM FG VV010 02/02 A3037", 1000)]
    public void Parse_VerticalVisibility_IsCeiling(string metar, int expectedCeiling)
    {
        var result = MetarParser.Parse(metar);
        Assert.NotNull(result);
        Assert.Equal(expectedCeiling, result.CeilingFeetAgl);
    }

    [Fact]
    public void Parse_VV_LowerThanBkn_VVWins()
    {
        var result = MetarParser.Parse("KSFO 121853Z 04006KT 1/4SM FG VV003 BKN010 02/02 A3037");
        Assert.NotNull(result);
        Assert.Equal(300, result.CeilingFeetAgl);
    }

    [Fact]
    public void Parse_VV_HigherThanBkn_BknWins()
    {
        var result = MetarParser.Parse("KSFO 121853Z 04006KT 1SM BR VV015 BKN008 02/02 A3037");
        Assert.NotNull(result);
        Assert.Equal(800, result.CeilingFeetAgl);
    }

    // -------------------------------------------------------------------------
    // Parse — M prefix visibility (less than)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("KSFO 121853Z 04006KT M1/4SM FG VV002 02/02 A3037", 0.25)]
    [InlineData("KSFO 121853Z 04006KT M1/2SM FG VV003 02/02 A3037", 0.5)]
    public void Parse_MVisibility_Correct(string metar, double expected)
    {
        var result = MetarParser.Parse(metar);
        Assert.NotNull(result);
        Assert.Equal(expected, result.VisibilityStatuteMiles);
    }

    // -------------------------------------------------------------------------
    // Real-world METARs from aviationweather.gov
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_RealMetar_KsfoFewLayers_NoCeiling()
    {
        var result = MetarParser.Parse("KSFO 032056Z 30014KT 10SM FEW006 FEW040 16/11 A3011 RMK AO2 SLP196 T01610111 58016");
        Assert.NotNull(result);
        Assert.Equal("KSFO", result.StationId);
        Assert.Equal(10.0, result.VisibilityStatuteMiles);
        Assert.Null(result.CeilingFeetAgl); // FEW only, no ceiling
    }

    [Fact]
    public void Parse_RealMetar_KlaxBrokenHigh()
    {
        var result = MetarParser.Parse("KLAX 032053Z 26009KT 10SM FEW100 SCT180 BKN250 19/12 A3003 RMK AO2 SLP168 T01890117 58010");
        Assert.NotNull(result);
        Assert.Equal("KLAX", result.StationId);
        Assert.Equal(10.0, result.VisibilityStatuteMiles);
        Assert.Equal(25000, result.CeilingFeetAgl); // BKN250 = 25000ft
    }

    [Fact]
    public void Parse_RealMetar_KatlBrokenLow()
    {
        var result = MetarParser.Parse("KATL 032052Z 15004KT 10SM BKN024 17/11 A3032 RMK AO2 SLP266 T01670106 56016");
        Assert.NotNull(result);
        Assert.Equal("KATL", result.StationId);
        Assert.Equal(10.0, result.VisibilityStatuteMiles);
        Assert.Equal(2400, result.CeilingFeetAgl); // BKN024 = 2400ft
    }

    [Fact]
    public void Parse_RealMetar_KjfkLowIfrWithMixedFraction()
    {
        var result = MetarParser.Parse(
            "KJFK 032051Z 04006KT 2 1/2SM -RA BR BKN006 OVC011 02/02 A3037 RMK AO2 SFC VIS 4 SLP285 P0007 60015 T00220017 56033"
        );
        Assert.NotNull(result);
        Assert.Equal("KJFK", result.StationId);
        Assert.Equal(2.5, result.VisibilityStatuteMiles); // "2 1/2SM"
        Assert.Equal(600, result.CeilingFeetAgl); // BKN006 = 600ft (lowest BKN/OVC)
    }

    [Fact]
    public void Parse_RealMetar_MetarPrefix_Handled()
    {
        // Real METARs from AWC have "METAR" prefix
        var result = MetarParser.Parse("METAR KOAK 032053Z 24006KT 10SM FEW006 FEW020 FEW200 16/11 A3012 RMK AO2 SLP199 T01610111 58014");
        Assert.NotNull(result);
        Assert.Equal("KOAK", result.StationId);
        Assert.Equal(10.0, result.VisibilityStatuteMiles);
        Assert.Null(result.CeilingFeetAgl); // FEW only
    }
}
