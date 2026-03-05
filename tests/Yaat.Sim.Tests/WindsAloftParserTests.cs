using Xunit;

namespace Yaat.Sim.Tests;

public class WindsAloftParserTests
{
    // Real FD text sample (abbreviated)
    private const string SampleFd = """
        DATA BASED ON 031200Z
        VALID 031800Z   FOR USE 1400-2100Z. TEMPS NEG ABV 24000

        FT  3000    6000    9000   12000   18000   24000  30000  34000  39000
        SFO 2708   2620+11 2531+06 2542-06 2563-17 2576-27 257840 256945 258055
        OAK 2510   2515+10 2425+05 2540-07 2560-18 2575-28 256840 256045 257555
        SAC 9900   2410+12 2520+07 2535-05 2555-16 2570-27 256540 255945 257055
        """;

    [Fact]
    public void Parse_RealFdText_ReturnsStations()
    {
        var result = WindsAloftParser.Parse(SampleFd);

        Assert.Equal(3, result.Count);
        Assert.Equal("SFO", result[0].StationId);
        Assert.Equal("OAK", result[1].StationId);
        Assert.Equal("SAC", result[2].StationId);
    }

    [Fact]
    public void Parse_SfoStation_HasCorrectWinds()
    {
        var result = WindsAloftParser.Parse(SampleFd);
        var sfo = result[0];

        // 3000: 2708 → direction 270, speed 8
        var w3000 = sfo.Winds.First(w => w.AltitudeFt == 3000);
        Assert.Equal(270, w3000.DirectionTrue);
        Assert.Equal(8, w3000.SpeedKts);
        Assert.False(w3000.IsLightVariable);

        // 6000: 2620 → direction 260, speed 20
        var w6000 = sfo.Winds.First(w => w.AltitudeFt == 6000);
        Assert.Equal(260, w6000.DirectionTrue);
        Assert.Equal(20, w6000.SpeedKts);
    }

    [Fact]
    public void Parse_LightVariable_FlaggedCorrectly()
    {
        var result = WindsAloftParser.Parse(SampleFd);
        var sac = result[2];

        var w3000 = sac.Winds.First(w => w.AltitudeFt == 3000);
        Assert.True(w3000.IsLightVariable);
        Assert.Equal(0, w3000.DirectionTrue);
        Assert.Equal(0, w3000.SpeedKts);
    }

    [Fact]
    public void DecodeWind_Over100Kts()
    {
        // DD >= 50: 7320 → direction = (73-50)*10 = 230°, speed = 20+100 = 120 kts
        var wind = WindsAloftParser.DecodeWind(30000, "7320");
        Assert.NotNull(wind);
        Assert.Equal(230, wind.Value.DirectionTrue);
        Assert.Equal(120, wind.Value.SpeedKts);
        Assert.False(wind.Value.IsLightVariable);
    }

    [Fact]
    public void DecodeWind_LightVariable()
    {
        var wind = WindsAloftParser.DecodeWind(3000, "9900");
        Assert.NotNull(wind);
        Assert.True(wind.Value.IsLightVariable);
    }

    [Fact]
    public void DecodeWind_WithTemperatureSuffix()
    {
        // "2620+11" → strip temp, decode 2620 → 260° at 20 kts
        var wind = WindsAloftParser.DecodeWind(6000, "2620+11");
        Assert.NotNull(wind);
        Assert.Equal(260, wind.Value.DirectionTrue);
        Assert.Equal(20, wind.Value.SpeedKts);
    }

    [Fact]
    public void DecodeWind_NegativeTemperatureSuffix()
    {
        var wind = WindsAloftParser.DecodeWind(12000, "2542-06");
        Assert.NotNull(wind);
        Assert.Equal(250, wind.Value.DirectionTrue);
        Assert.Equal(42, wind.Value.SpeedKts);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(WindsAloftParser.Parse(""));
        Assert.Empty(WindsAloftParser.Parse("   "));
    }

    [Fact]
    public void Parse_NoFtHeader_ReturnsEmpty()
    {
        Assert.Empty(WindsAloftParser.Parse("some random text\nwith no FT header"));
    }

    [Fact]
    public void DecodeWind_InvalidCode_ReturnsNull()
    {
        Assert.Null(WindsAloftParser.DecodeWind(3000, "ABC"));
        Assert.Null(WindsAloftParser.DecodeWind(3000, "12"));
        Assert.Null(WindsAloftParser.DecodeWind(3000, "ABCD"));
    }

    [Fact]
    public void DecodeWind_NorthWind()
    {
        // 3610 → direction 360°, speed 10
        var wind = WindsAloftParser.DecodeWind(6000, "3610");
        Assert.NotNull(wind);
        Assert.Equal(360, wind.Value.DirectionTrue);
        Assert.Equal(10, wind.Value.SpeedKts);
    }
}
