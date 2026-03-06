using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.Tests;

public class LiveWeatherServiceTests
{
    [Fact]
    public void ExtractTafInitialGroup_BasicTaf_TruncatesAtFm()
    {
        var taf = "TAF KSFO 061720Z 0618/0724 28012KT P6SM FEW250 FM070200 VRB03KT P6SM SCT012 BKN020";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        // Should contain the station and initial weather, not the FM group
        Assert.Contains("KSFO", result);
        Assert.Contains("28012KT", result);
        Assert.Contains("P6SM", result);
        Assert.Contains("FEW250", result);
        Assert.DoesNotContain("FM", result);
        Assert.DoesNotContain("SCT012", result);
    }

    [Fact]
    public void ExtractTafInitialGroup_WithBecmg_TruncatesAtBecmg()
    {
        var taf = "TAF KOAK 061720Z 0618/0724 30008KT 10SM SCT025 BECMG 0700/0702 25008KT 3SM BR OVC010";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        Assert.Contains("SCT025", result);
        Assert.DoesNotContain("BECMG", result);
        Assert.DoesNotContain("OVC010", result);
    }

    [Fact]
    public void ExtractTafInitialGroup_WithTempo_TruncatesAtTempo()
    {
        var taf = "TAF KJFK 061720Z 0618/0724 04006KT 6SM BKN015 TEMPO 0620/0624 3SM BR OVC008";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        Assert.Contains("BKN015", result);
        Assert.DoesNotContain("TEMPO", result);
        Assert.DoesNotContain("OVC008", result);
    }

    [Fact]
    public void ExtractTafInitialGroup_WithAmd_StripsAmd()
    {
        var taf = "TAF AMD KSFO 061720Z 0618/0724 28012KT P6SM FEW250";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        Assert.DoesNotContain("TAF", result);
        Assert.DoesNotContain("AMD", result);
        Assert.Contains("KSFO", result);
    }

    [Fact]
    public void ExtractTafInitialGroup_NoGroups_ReturnsFullContent()
    {
        var taf = "TAF KSFO 061720Z 0618/0724 28012KT P6SM FEW250";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        Assert.Contains("FEW250", result);
    }

    [Fact]
    public void ExtractTafInitialGroup_RemovesValidityPeriod()
    {
        var taf = "TAF KSFO 061720Z 0618/0724 28012KT P6SM FEW250";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        Assert.DoesNotContain("0618/0724", result);
    }

    [Fact]
    public void ExtractTafInitialGroup_ParseableByMetarParser()
    {
        var taf = "TAF KSFO 061720Z 0618/0724 28012KT P6SM FEW250 FM070200 VRB03KT P6SM SCT012 BKN020";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        var parsed = MetarParser.Parse(result);
        Assert.NotNull(parsed);
        Assert.Equal("KSFO", parsed.StationId);
        Assert.Equal(6.0, parsed.VisibilityStatuteMiles);
        Assert.Null(parsed.CeilingFeetAgl); // FEW only
    }

    [Fact]
    public void ExtractTafInitialGroup_LowCeiling_ParsedCorrectly()
    {
        var taf = "TAF KJFK 061720Z 0618/0724 04006KT 2SM BR BKN006 OVC011 FM070800 25008KT P6SM SCT250";
        var result = LiveWeatherService.ExtractTafInitialGroup(taf);
        Assert.NotNull(result);

        var parsed = MetarParser.Parse(result);
        Assert.NotNull(parsed);
        Assert.Equal("KJFK", parsed.StationId);
        Assert.Equal(2.0, parsed.VisibilityStatuteMiles);
        Assert.Equal(600, parsed.CeilingFeetAgl); // BKN006
    }

    [Fact]
    public void ExtractTafInitialGroup_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(LiveWeatherService.ExtractTafInitialGroup(null!));
        Assert.Null(LiveWeatherService.ExtractTafInitialGroup(""));
        Assert.Null(LiveWeatherService.ExtractTafInitialGroup("   "));
    }
}
