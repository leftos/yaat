using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class LiveWeatherServiceTests
{
    [Fact]
    public void ParseMetars_VrbWindDirection_ParsesAllStations()
    {
        const string json = """
            [
              {"icaoId":"KOAK","wdir":270,"wspd":8,"rawOb":"METAR KOAK 081953Z 27008KT 10SM FEW200 21/12 A2999"},
              {"icaoId":"KLVK","wdir":"VRB","wspd":5,"rawOb":"METAR KLVK 081953Z VRB05KT 10SM CLR 24/09 A2997"}
            ]
            """;

        var metars = LiveWeatherService.ParseMetars(json);

        Assert.NotNull(metars);
        Assert.Equal(2, metars.Count);

        var oak = metars[0];
        Assert.Equal(270, oak.Wdir);
        Assert.Equal(8, oak.Wspd);
        Assert.StartsWith("METAR KOAK", oak.RawOb);

        var lvk = metars[1];
        Assert.Null(lvk.Wdir);
        Assert.Equal(5, lvk.Wspd);
        Assert.StartsWith("METAR KLVK", lvk.RawOb);
    }

    [Fact]
    public void ParseMetars_MalformedElement_SkipsOnlyThatElement()
    {
        const string json = """
            [
              {"icaoId":"KSFO","wdir":350,"wspd":10,"rawOb":"METAR KSFO 081956Z 35010KT 10SM FEW200 19/03 A3016"},
              {"icaoId":"KBAD","wdir":{"unexpected":"object"},"wspd":[1,2],"rawOb":42},
              {"icaoId":"KHWD","wdir":260,"wspd":5,"rawOb":"METAR KHWD 081953Z 26005KT 10SM CLR 22/11 A2998"}
            ]
            """;

        var metars = LiveWeatherService.ParseMetars(json);

        Assert.NotNull(metars);
        Assert.Equal(2, metars.Count);
        Assert.StartsWith("METAR KSFO", metars[0].RawOb);
        Assert.StartsWith("METAR KHWD", metars[1].RawOb);
    }

    [Fact]
    public void ParseMetars_NumericStringWind_ParsesAsNumber()
    {
        const string json = """
            [{"icaoId":"KCCR","wdir":"320","wspd":"7","rawOb":"METAR KCCR 081953Z 32007KT 10SM CLR 23/10 A2998"}]
            """;

        var metars = LiveWeatherService.ParseMetars(json);

        Assert.NotNull(metars);
        var ccr = Assert.Single(metars);
        Assert.Equal(320, ccr.Wdir);
        Assert.Equal(7, ccr.Wspd);
    }
}
