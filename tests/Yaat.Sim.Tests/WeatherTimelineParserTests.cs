using System.Text.Json;
using Xunit;

namespace Yaat.Sim.Tests;

public class WeatherTimelineParserTests
{
    // -------------------------------------------------------------------------
    // V1 detection — existing ATCTrainer format
    // -------------------------------------------------------------------------

    [Fact]
    public void V1Json_ReturnsProfile()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                name = "SFOW",
                artccId = "ZOA",
                windLayers = new[]
                {
                    new
                    {
                        altitude = 3000,
                        direction = 280,
                        speed = 12,
                    },
                },
                metars = new[] { "KSFO 031753Z 28012KT 10SM FEW200" },
            }
        );

        var result = WeatherTimelineParser.Parse(json);

        Assert.True(result.IsProfile);
        Assert.False(result.IsTimeline);
        Assert.False(result.IsError);
        Assert.NotNull(result.Profile);
        Assert.Single(result.Profile!.WindLayers);
        Assert.Equal(280, result.Profile.WindLayers[0].Direction);
    }

    // -------------------------------------------------------------------------
    // V2 detection — periods array
    // -------------------------------------------------------------------------

    [Fact]
    public void V2Json_ReturnsTimeline()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                name = "SFOW to SFOE",
                artccId = "ZOA",
                periods = new[]
                {
                    new
                    {
                        startMinutes = 0,
                        transitionMinutes = 0,
                        windLayers = new[]
                        {
                            new
                            {
                                altitude = 3000,
                                direction = 280,
                                speed = 12,
                            },
                        },
                        metars = new[] { "KSFO 031753Z 28012KT 10SM" },
                    },
                    new
                    {
                        startMinutes = 20,
                        transitionMinutes = 10,
                        windLayers = new[]
                        {
                            new
                            {
                                altitude = 3000,
                                direction = 250,
                                speed = 15,
                            },
                        },
                        metars = new[] { "KSFO 031853Z 25015G22KT 6SM -RA" },
                    },
                },
            }
        );

        var result = WeatherTimelineParser.Parse(json);

        Assert.True(result.IsTimeline);
        Assert.False(result.IsProfile);
        Assert.NotNull(result.Timeline);
        Assert.Equal(2, result.Timeline!.Periods.Count);
        Assert.Equal("SFOW to SFOE", result.Timeline.Name);
    }

    // -------------------------------------------------------------------------
    // Invalid JSON
    // -------------------------------------------------------------------------

    [Fact]
    public void InvalidJson_ReturnsError()
    {
        var result = WeatherTimelineParser.Parse("not valid json {{{");

        Assert.True(result.IsError);
        Assert.Contains("Invalid JSON", result.Error);
    }

    // -------------------------------------------------------------------------
    // V2 with zero periods
    // -------------------------------------------------------------------------

    [Fact]
    public void V2_ZeroPeriods_ReturnsError()
    {
        var json = JsonSerializer.Serialize(new { name = "Empty", periods = Array.Empty<object>() });

        var result = WeatherTimelineParser.Parse(json);

        Assert.True(result.IsError);
        Assert.Contains("no periods", result.Error);
    }

    // -------------------------------------------------------------------------
    // V2 with period missing wind layers
    // -------------------------------------------------------------------------

    [Fact]
    public void V2_PeriodMissingWindLayers_ReturnsError()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                name = "Bad period",
                periods = new[]
                {
                    new
                    {
                        startMinutes = 0,
                        transitionMinutes = 0,
                        windLayers = Array.Empty<object>(),
                    },
                },
            }
        );

        var result = WeatherTimelineParser.Parse(json);

        Assert.True(result.IsError);
        Assert.Contains("no wind layers", result.Error);
    }

    // -------------------------------------------------------------------------
    // V2 periods sorted by startMinutes
    // -------------------------------------------------------------------------

    [Fact]
    public void V2_PeriodsAreSortedByStartMinutes()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                name = "Unsorted",
                periods = new[]
                {
                    new
                    {
                        startMinutes = 20,
                        transitionMinutes = 0,
                        windLayers = new[]
                        {
                            new
                            {
                                altitude = 3000,
                                direction = 250,
                                speed = 15,
                            },
                        },
                    },
                    new
                    {
                        startMinutes = 0,
                        transitionMinutes = 0,
                        windLayers = new[]
                        {
                            new
                            {
                                altitude = 3000,
                                direction = 280,
                                speed = 12,
                            },
                        },
                    },
                },
            }
        );

        var result = WeatherTimelineParser.Parse(json);

        Assert.True(result.IsTimeline);
        Assert.Equal(0, result.Timeline!.Periods[0].StartMinutes);
        Assert.Equal(20, result.Timeline.Periods[1].StartMinutes);
    }

    // -------------------------------------------------------------------------
    // V1 with no windLayers still parses (empty profile)
    // -------------------------------------------------------------------------

    [Fact]
    public void V1_NoWindLayers_ReturnsProfile()
    {
        var json = JsonSerializer.Serialize(new { name = "Calm", artccId = "ZOA" });

        var result = WeatherTimelineParser.Parse(json);

        Assert.True(result.IsProfile);
        Assert.Empty(result.Profile!.WindLayers);
    }
}
