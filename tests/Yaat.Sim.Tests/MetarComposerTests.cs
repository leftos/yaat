using Xunit;

namespace Yaat.Sim.Tests;

public class MetarComposerTests
{
    private const string Base = "KOAK 121853Z 27012KT 10SM FEW025 18/12 A2992 RMK AO2 SLP132 T01780122";
    private static readonly DateTime Obs = new(2026, 6, 2, 18, 53, 0, DateTimeKind.Utc);

    private static ReportedConditions Cond(
        bool calm = false,
        int dir = 270,
        int speed = 12,
        int? gust = null,
        double? vis = 10,
        IReadOnlyList<MetarParser.CloudLayer>? layers = null,
        int? ceiling = null,
        double? alt = 29.92,
        bool precip = false
    ) => new(calm, dir, speed, gust, vis, layers ?? [], ceiling, alt, precip);

    [Fact]
    public void Compose_Calm_EmitsZeroWind()
    {
        var result = MetarComposer.Compose(Base, Cond(calm: true), Obs, isSpeci: false);
        Assert.Contains("00000KT", result);
        Assert.DoesNotContain("27012KT", result);
    }

    [Fact]
    public void Compose_GustAndThreeSixty_FormatsWind()
    {
        var result = MetarComposer.Compose(Base, Cond(dir: 360, speed: 8, gust: 18), Obs, isSpeci: false);
        Assert.Contains("36008G18KT", result);
    }

    [Theory]
    [InlineData(15.0, "10SM")]
    [InlineData(0.5, "1/2SM")]
    [InlineData(1.5, "1 1/2SM")]
    [InlineData(0.1, "M1/4SM")]
    [InlineData(2.0, "2SM")]
    [InlineData(2.9, "2 3/4SM")] // sub-3 must not round up to 3SM
    public void Compose_Visibility_Encoded(double vis, string expected)
    {
        var result = MetarComposer.Compose(Base, Cond(vis: vis), Obs, isSpeci: false);
        Assert.Contains(expected, result);
    }

    [Fact]
    public void Compose_NoLayers_EmitsClr()
    {
        var result = MetarComposer.Compose(Base, Cond(layers: []), Obs, isSpeci: false);
        Assert.Contains("CLR", result);
        Assert.DoesNotContain("FEW025", result);
    }

    [Fact]
    public void Compose_HighCloudsAboveAutomatedLimit_ReportedClear()
    {
        var layers = new List<MetarParser.CloudLayer> { new(MetarParser.CloudCover.Broken, 15000) };
        var result = MetarComposer.Compose(Base, Cond(layers: layers), Obs, isSpeci: false);
        Assert.Contains("CLR", result);
        Assert.DoesNotContain("BKN150", result);
    }

    [Fact]
    public void Compose_MixedLowAndHighClouds_OmitsHigh()
    {
        var layers = new List<MetarParser.CloudLayer> { new(MetarParser.CloudCover.Scattered, 2500), new(MetarParser.CloudCover.Broken, 20000) };
        var result = MetarComposer.Compose(Base, Cond(layers: layers), Obs, isSpeci: false);
        Assert.Contains("SCT025", result);
        Assert.DoesNotContain("BKN200", result);
    }

    [Fact]
    public void Compose_MultipleLayers_EmitsSortedSky()
    {
        var layers = new List<MetarParser.CloudLayer> { new(MetarParser.CloudCover.Overcast, 3000), new(MetarParser.CloudCover.Broken, 1500) };
        var result = MetarComposer.Compose(Base, Cond(layers: layers, ceiling: 1500), Obs, isSpeci: false);
        Assert.Contains("BKN015 OVC030", result);
        Assert.DoesNotContain("FEW025", result);
    }

    [Theory]
    [InlineData(29.925, "A2992")]
    [InlineData(30.05, "A3005")]
    [InlineData(29.92, "A2992")]
    public void Compose_Altimeter_Truncated(double inHg, string expected)
    {
        var result = MetarComposer.Compose(Base, Cond(alt: inHg), Obs, isSpeci: false);
        Assert.Contains(expected, result);
    }

    [Fact]
    public void Compose_Timestamp_Replaced()
    {
        var result = MetarComposer.Compose(Base, Cond(), Obs, isSpeci: false);
        Assert.Contains("021853Z", result);
        Assert.DoesNotContain("121853Z", result);
    }

    [Fact]
    public void Compose_Routine_PrefixesMetar()
    {
        var result = MetarComposer.Compose(Base, Cond(), Obs, isSpeci: false);
        Assert.StartsWith("METAR KOAK 021853Z", result);
    }

    [Fact]
    public void Compose_Special_PrefixesSpeci()
    {
        var result = MetarComposer.Compose(Base, Cond(), Obs, isSpeci: true);
        Assert.StartsWith("SPECI KOAK 021853Z", result);
    }

    [Fact]
    public void Compose_PreservesTempDewAndStableRemarks()
    {
        var result = MetarComposer.Compose(Base, Cond(), Obs, isSpeci: false);
        Assert.Contains("18/12", result);
        Assert.Contains("AO2", result);
        Assert.Contains("T01780122", result);
    }

    [Fact]
    public void Compose_StripsStalePressureRemark()
    {
        var result = MetarComposer.Compose(Base, Cond(alt: 29.96), Obs, isSpeci: false);
        Assert.DoesNotContain("SLP132", result);
        Assert.Contains("A2996", result);
    }

    [Fact]
    public void Compose_StripsPeriodRelativeRemarks()
    {
        const string withWindRemarks = "KOAK 121853Z 27012KT 10SM FEW025 18/12 A2992 RMK AO2 PK WND 28045/1955 WSHFT 1715 SLP132";
        var result = MetarComposer.Compose(withWindRemarks, Cond(), Obs, isSpeci: false);
        Assert.DoesNotContain("PK WND", result);
        Assert.DoesNotContain("WSHFT", result);
        Assert.DoesNotContain("SLP132", result);
        Assert.Contains("AO2", result);
    }

    [Fact]
    public void Compose_LeadingMetarKeyword_NotDuplicated()
    {
        const string withKeyword = "METAR KOAK 121853Z 27012KT 10SM FEW025 18/12 A2992";
        var result = MetarComposer.Compose(withKeyword, Cond(), Obs, isSpeci: false);
        Assert.StartsWith("METAR KOAK", result);
        Assert.DoesNotContain("METAR METAR", result);
    }
}
