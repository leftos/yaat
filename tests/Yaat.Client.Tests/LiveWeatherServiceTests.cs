using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

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

    // -------------------------------------------------------------------------
    // Surface layer assembly: VRB stations, gusts, and dddVddd spreads
    // -------------------------------------------------------------------------

    private static LiveWeatherService.MetarJson Station(string rawOb, int? wdir, int? wspd) =>
        new()
        {
            RawOb = rawOb,
            Wdir = wdir,
            Wspd = wspd,
        };

    // Declination proxy for the assembled layer: METAR text winds are TRUE and the layer
    // stores magnetic, so expectations convert the same way the service does.
    private static readonly LatLon Reference = new(37.7, -122.4);

    private static double Mag(double trueDeg) => MagneticDeclination.TrueToMagnetic(trueDeg, Reference.Lat, Reference.Lon);

    [Fact]
    public void BuildSurfaceWindLayer_VrbStation_ContributesSpeedNotDirection()
    {
        // The VRB station's 6 kt joins the speed average; the direction average comes from
        // the directional stations alone.
        var layer = LiveWeatherService.BuildSurfaceWindLayer(
            [
                Station("METAR KOAK 081953Z 27010KT 10SM CLR 21/12 A2999", 270, 10),
                Station("METAR KLVK 081953Z VRB06KT 10SM CLR 24/09 A2997", null, 6),
            ],
            Reference
        );

        Assert.NotNull(layer);
        Assert.Equal(Mag(270), layer.Direction, 1);
        Assert.Equal(8, layer.Speed, 1);
        Assert.Null(layer.Variable);
    }

    [Fact]
    public void BuildSurfaceWindLayer_AllVrb_ProducesVariableLayer()
    {
        var layer = LiveWeatherService.BuildSurfaceWindLayer(
            [
                Station("METAR KOAK 081953Z VRB04KT 10SM CLR 21/12 A2999", null, 4),
                Station("METAR KLVK 081953Z VRB06KT 10SM CLR 24/09 A2997", null, 6),
            ],
            Reference
        );

        Assert.NotNull(layer);
        Assert.True(layer.Variable);
        Assert.Equal(5, layer.Speed, 1);
    }

    [Fact]
    public void BuildSurfaceWindLayer_GustsAndSpread_MinedFromRawText()
    {
        // 21015G25KT 180V240 → gusts 25, half-spread 30 on the assembled layer.
        var layer = LiveWeatherService.BuildSurfaceWindLayer(
            [Station("METAR KOAK 081953Z 21015G25KT 180V240 10SM CLR 21/12 A2999", 210, 15)],
            Reference
        );

        Assert.NotNull(layer);
        Assert.Equal(Mag(210), layer.Direction, 1);
        Assert.Equal(15, layer.Speed, 1);
        Assert.NotNull(layer.Gusts);
        Assert.Equal(25, layer.Gusts!.Value, 1);
        Assert.NotNull(layer.DirectionVariabilityDeg);
        Assert.Equal(30, layer.DirectionVariabilityDeg!.Value, 1);
    }

    [Fact]
    public void BuildSurfaceWindLayer_SteadyStations_NoSpuriousVariability()
    {
        var layer = LiveWeatherService.BuildSurfaceWindLayer([Station("METAR KOAK 081953Z 27010KT 10SM CLR 21/12 A2999", 270, 10)], Reference);

        Assert.NotNull(layer);
        Assert.Null(layer.Gusts);
        Assert.Null(layer.DirectionVariabilityDeg);
        Assert.Null(layer.Variable);
    }
}
