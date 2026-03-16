using Xunit;

namespace Yaat.Sim.Tests;

public class WeatherTimelineTests
{
    // -------------------------------------------------------------------------
    // Single period — constant weather at any time
    // -------------------------------------------------------------------------

    [Fact]
    public void SinglePeriod_ReturnsConstantWeather()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                Precipitation = "None",
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM FEW200"],
            }
        );

        var at0 = timeline.GetWeatherAt(0);
        var at600 = timeline.GetWeatherAt(600);
        var at3600 = timeline.GetWeatherAt(3600);

        Assert.Single(at0.WindLayers);
        Assert.Equal(270, at0.WindLayers[0].Direction);
        Assert.Equal(10, at0.WindLayers[0].Speed);
        Assert.Equal("None", at0.Precipitation);
        Assert.Single(at0.Metars);

        Assert.Equal(270, at600.WindLayers[0].Direction);
        Assert.Equal(270, at3600.WindLayers[0].Direction);
    }

    // -------------------------------------------------------------------------
    // Two periods — snap transition (transitionMinutes = 0)
    // -------------------------------------------------------------------------

    [Fact]
    public void TwoPeriods_SnapTransition_ChangesInstantly()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                Precipitation = "None",
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 280,
                        Speed = 12,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM"],
            },
            new WeatherPeriod
            {
                StartMinutes = 20,
                TransitionMinutes = 0,
                Precipitation = "Rain",
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 250,
                        Speed = 15,
                    },
                ],
                Metars = ["KSFO 031853Z 25015G22KT 6SM -RA"],
            }
        );

        // Before transition: period A
        var before = timeline.GetWeatherAt(19 * 60 + 59);
        Assert.Equal(280, before.WindLayers[0].Direction);
        Assert.Equal(12, before.WindLayers[0].Speed);
        Assert.Equal("None", before.Precipitation);

        // At transition: period B
        var at = timeline.GetWeatherAt(20 * 60);
        Assert.Equal(250, at.WindLayers[0].Direction);
        Assert.Equal(15, at.WindLayers[0].Speed);
        Assert.Equal("Rain", at.Precipitation);
    }

    // -------------------------------------------------------------------------
    // Two periods — gradual transition
    // -------------------------------------------------------------------------

    [Fact]
    public void TwoPeriods_GradualTransition_InterpolatesAtMidpoint()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 280,
                        Speed = 10,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 20,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 260,
                        Speed = 20,
                    },
                ],
            }
        );

        // At transition start (minute 20): t=0, should be period A values
        var atStart = timeline.GetWeatherAt(20 * 60);
        Assert.Equal(280, atStart.WindLayers[0].Direction, 1);
        Assert.Equal(10, atStart.WindLayers[0].Speed, 1);

        // At midpoint (minute 25): t=0.5
        var atMid = timeline.GetWeatherAt(25 * 60);
        Assert.Equal(270, atMid.WindLayers[0].Direction, 1);
        Assert.Equal(15, atMid.WindLayers[0].Speed, 1);

        // At transition end (minute 30): fully period B
        var atEnd = timeline.GetWeatherAt(30 * 60);
        Assert.Equal(260, atEnd.WindLayers[0].Direction, 1);
        Assert.Equal(20, atEnd.WindLayers[0].Speed, 1);
    }

    [Fact]
    public void TwoPeriods_GradualTransition_InterpolatesAtQuarterPoints()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 10,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 30,
                    },
                ],
            }
        );

        // t=0.25 (minute 12.5)
        var atQ1 = timeline.GetWeatherAt(12.5 * 60);
        Assert.Equal(15, atQ1.WindLayers[0].Speed, 1);

        // t=0.75 (minute 17.5)
        var atQ3 = timeline.GetWeatherAt(17.5 * 60);
        Assert.Equal(25, atQ3.WindLayers[0].Speed, 1);
    }

    // -------------------------------------------------------------------------
    // Wind direction 360/0 wraparound
    // -------------------------------------------------------------------------

    [Fact]
    public void WindDirectionWrap_350To010_InterpolatesThroughNorth()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 350,
                        Speed = 10,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 10,
                        Speed = 10,
                    },
                ],
            }
        );

        // Midpoint should be ~360/0 (north), NOT ~180 (south)
        var atMid = timeline.GetWeatherAt(15 * 60);
        double dir = atMid.WindLayers[0].Direction;

        // Should be very close to 0/360
        Assert.True(dir < 5 || dir > 355, $"Expected direction near 0/360, got {dir}");
    }

    [Fact]
    public void WindDirectionWrap_010To350_InterpolatesThroughNorth()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 10,
                        Speed = 10,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 350,
                        Speed = 10,
                    },
                ],
            }
        );

        var atMid = timeline.GetWeatherAt(15 * 60);
        double dir = atMid.WindLayers[0].Direction;
        Assert.True(dir < 5 || dir > 355, $"Expected direction near 0/360, got {dir}");
    }

    // -------------------------------------------------------------------------
    // METARs and precipitation snap at transition start
    // -------------------------------------------------------------------------

    [Fact]
    public void MetarsAndPrecipitation_SnapAtTransitionStart()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                Precipitation = "None",
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM"],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                Precipitation = "Rain",
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 250,
                        Speed = 20,
                    },
                ],
                Metars = ["KSFO 031853Z 25015G22KT 6SM -RA"],
            }
        );

        // Just before transition: period A
        var before = timeline.GetWeatherAt(9 * 60 + 59);
        Assert.Equal("None", before.Precipitation);
        Assert.Equal("KSFO 031753Z 28012KT 10SM", before.Metars[0]);

        // At transition start: precipitation and METARs snap to B
        var atStart = timeline.GetWeatherAt(10 * 60);
        Assert.Equal("Rain", atStart.Precipitation);
        Assert.Equal("KSFO 031853Z 25015G22KT 6SM -RA", atStart.Metars[0]);

        // During transition: still B's METARs/precipitation
        var during = timeline.GetWeatherAt(15 * 60);
        Assert.Equal("Rain", during.Precipitation);
    }

    // -------------------------------------------------------------------------
    // Before first period returns first period's weather
    // -------------------------------------------------------------------------

    [Fact]
    public void BeforeFirstPeriod_ReturnsFirstPeriodWeather()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 5,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
            }
        );

        // t=0, before first period at minute 5
        var result = timeline.GetWeatherAt(0);
        Assert.Equal(270, result.WindLayers[0].Direction);
    }

    // -------------------------------------------------------------------------
    // Three+ periods — correct sequencing
    // -------------------------------------------------------------------------

    [Fact]
    public void ThreePeriods_CorrectSequencing()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Precipitation = "None",
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 20,
                    },
                ],
                Precipitation = "Rain",
            },
            new WeatherPeriod
            {
                StartMinutes = 20,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 90,
                        Speed = 30,
                    },
                ],
                Precipitation = "Snow",
            }
        );

        var at5 = timeline.GetWeatherAt(5 * 60);
        Assert.Equal(270, at5.WindLayers[0].Direction);
        Assert.Equal("None", at5.Precipitation);

        var at15 = timeline.GetWeatherAt(15 * 60);
        Assert.Equal(180, at15.WindLayers[0].Direction);
        Assert.Equal("Rain", at15.Precipitation);

        var at25 = timeline.GetWeatherAt(25 * 60);
        Assert.Equal(90, at25.WindLayers[0].Direction);
        Assert.Equal("Snow", at25.Precipitation);
    }

    [Fact]
    public void ThreePeriods_GradualTransitions()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 10,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 5,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 20,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 20,
                TransitionMinutes = 5,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 40,
                    },
                ],
            }
        );

        // Mid first transition (minute 12.5): interpolating 10→20
        var mid1 = timeline.GetWeatherAt(12.5 * 60);
        Assert.Equal(15, mid1.WindLayers[0].Speed, 1);

        // After first transition (minute 16): fully period B
        var post1 = timeline.GetWeatherAt(16 * 60);
        Assert.Equal(20, post1.WindLayers[0].Speed, 1);

        // Mid second transition (minute 22.5): interpolating 20→40
        var mid2 = timeline.GetWeatherAt(22.5 * 60);
        Assert.Equal(30, mid2.WindLayers[0].Speed, 1);
    }

    // -------------------------------------------------------------------------
    // Overlapping transitions — B's transition truncates when C starts
    // -------------------------------------------------------------------------

    [Fact]
    public void OverlappingTransitions_Truncated()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 10,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 20, // Would extend to minute 30
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 50,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 15, // Starts at 15, truncating B's transition from 20 to 15
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 100,
                    },
                ],
            }
        );

        // At minute 12.5: interpolating A→B, but transition truncated to end at minute 15
        // t = (12.5*60 - 10*60) / (15*60 - 10*60) = 150/300 = 0.5
        var mid = timeline.GetWeatherAt(12.5 * 60);
        Assert.Equal(30, mid.WindLayers[0].Speed, 1);

        // At minute 15: period C takes over
        var atC = timeline.GetWeatherAt(15 * 60);
        Assert.Equal(100, atC.WindLayers[0].Speed, 1);
    }

    // -------------------------------------------------------------------------
    // Multiple wind layers
    // -------------------------------------------------------------------------

    [Fact]
    public void MultipleWindLayers_AllInterpolated()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 10,
                    },
                    new WindLayer
                    {
                        Altitude = 6000,
                        Direction = 270,
                        Speed = 20,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 30,
                    },
                    new WindLayer
                    {
                        Altitude = 6000,
                        Direction = 270,
                        Speed = 40,
                    },
                ],
            }
        );

        var mid = timeline.GetWeatherAt(15 * 60);
        Assert.Equal(2, mid.WindLayers.Count);
        Assert.Equal(20, mid.WindLayers[0].Speed, 1);
        Assert.Equal(30, mid.WindLayers[1].Speed, 1);
    }

    [Fact]
    public void DifferentLayerCounts_SnapsToTarget()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 180,
                        Speed = 10,
                    },
                ],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 20,
                    },
                    new WindLayer
                    {
                        Altitude = 6000,
                        Direction = 270,
                        Speed = 30,
                    },
                ],
            }
        );

        // During transition with different layer counts: snaps to target
        var mid = timeline.GetWeatherAt(15 * 60);
        Assert.Equal(2, mid.WindLayers.Count);
        Assert.Equal(20, mid.WindLayers[0].Speed);
        Assert.Equal(30, mid.WindLayers[1].Speed);
    }

    // -------------------------------------------------------------------------
    // Empty periods list
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyPeriods_ReturnsEmptyProfile()
    {
        var timeline = new WeatherTimeline { Periods = [] };
        var result = timeline.GetWeatherAt(0);
        Assert.Empty(result.WindLayers);
    }

    // -------------------------------------------------------------------------
    // HasMeaningfulChange
    // -------------------------------------------------------------------------

    [Fact]
    public void HasMeaningfulChange_BothNull_ReturnsFalse()
    {
        Assert.False(WeatherTimeline.HasMeaningfulChange(null, null));
    }

    [Fact]
    public void HasMeaningfulChange_OneNull_ReturnsTrue()
    {
        var profile = new WeatherProfile { WindLayers = [new WindLayer { Direction = 270, Speed = 10 }] };
        Assert.True(WeatherTimeline.HasMeaningfulChange(null, profile));
        Assert.True(WeatherTimeline.HasMeaningfulChange(profile, null));
    }

    [Fact]
    public void HasMeaningfulChange_SmallWindChange_ReturnsFalse()
    {
        var a = new WeatherProfile { WindLayers = [new WindLayer { Direction = 270, Speed = 10 }] };
        var b = new WeatherProfile { WindLayers = [new WindLayer { Direction = 270.5, Speed = 10.3 }] };
        Assert.False(WeatherTimeline.HasMeaningfulChange(a, b));
    }

    [Fact]
    public void HasMeaningfulChange_LargeWindChange_ReturnsTrue()
    {
        var a = new WeatherProfile { WindLayers = [new WindLayer { Direction = 270, Speed = 10 }] };
        var b = new WeatherProfile { WindLayers = [new WindLayer { Direction = 272, Speed = 10 }] };
        Assert.True(WeatherTimeline.HasMeaningfulChange(a, b));
    }

    [Fact]
    public void HasMeaningfulChange_PrecipitationChange_ReturnsTrue()
    {
        var a = new WeatherProfile { Precipitation = "None", WindLayers = [new WindLayer { Direction = 270, Speed = 10 }] };
        var b = new WeatherProfile { Precipitation = "Rain", WindLayers = [new WindLayer { Direction = 270, Speed = 10 }] };
        Assert.True(WeatherTimeline.HasMeaningfulChange(a, b));
    }

    // -------------------------------------------------------------------------
    // Cloud layer (METAR) interpolation during transitions
    // -------------------------------------------------------------------------

    [Fact]
    public void CeilingInterpolates_DuringTransition()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM BKN050 OVC100 A3002"],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 250,
                        Speed = 15,
                    },
                ],
                Metars = ["KSFO 031853Z 25015KT 3SM OVC015 A2990"],
            }
        );

        // At midpoint (minute 15, t=0.5): ceiling should interpolate 5000→1500
        var mid = timeline.GetWeatherAt(15 * 60);
        var weather = mid.GetWeatherForAirport("KSFO");
        Assert.NotNull(weather);
        Assert.NotNull(weather!.CeilingFeetAgl);
        Assert.True(Math.Abs(weather.CeilingFeetAgl!.Value - 3250) < 100, $"Expected ceiling ~3250, got {weather.CeilingFeetAgl.Value}");
    }

    [Fact]
    public void VisibilityInterpolates_DuringTransition()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM FEW200 A3002"],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 250,
                        Speed = 15,
                    },
                ],
                Metars = ["KSFO 031853Z 25015KT 3SM OVC015 A2990"],
            }
        );

        // At midpoint (minute 15, t=0.5): visibility should interpolate 10→3
        var mid = timeline.GetWeatherAt(15 * 60);
        var weather = mid.GetWeatherForAirport("KSFO");
        Assert.NotNull(weather);
        Assert.NotNull(weather!.VisibilityStatuteMiles);
        Assert.Equal(6.5, weather.VisibilityStatuteMiles!.Value, 0.5);
    }

    [Fact]
    public void AltimeterInterpolates_DuringTransition()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM FEW200 A3002"],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 250,
                        Speed = 15,
                    },
                ],
                Metars = ["KSFO 031853Z 25015KT 3SM OVC015 A2990"],
            }
        );

        // At midpoint (minute 15, t=0.5): altimeter should interpolate 30.02→29.90
        var mid = timeline.GetWeatherAt(15 * 60);
        var weather = mid.GetWeatherForAirport("KSFO");
        Assert.NotNull(weather);
        Assert.NotNull(weather!.AltimeterInHg);
        Assert.Equal(29.96, weather.AltimeterInHg!.Value, 0.02);
    }

    [Fact]
    public void OutsideTransition_NoMetarOverrides()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM BKN050 A3002"],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 5,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 250,
                        Speed = 15,
                    },
                ],
                Metars = ["KSFO 031853Z 25015KT 3SM OVC015 A2990"],
            }
        );

        // Before transition (minute 5): no overrides, normal METAR parsing
        var before = timeline.GetWeatherAt(5 * 60);
        Assert.Null(before.ParsedMetarOverrides);

        // After transition (minute 16): no overrides
        var after = timeline.GetWeatherAt(16 * 60);
        Assert.Null(after.ParsedMetarOverrides);
    }

    [Fact]
    public void StationOnlyInOnePeriod_NoOverrideForThatStation()
    {
        var timeline = MakeTimeline(
            new WeatherPeriod
            {
                StartMinutes = 0,
                TransitionMinutes = 0,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 270,
                        Speed = 10,
                    },
                ],
                Metars = ["KSFO 031753Z 28012KT 10SM FEW200 A3002"],
            },
            new WeatherPeriod
            {
                StartMinutes = 10,
                TransitionMinutes = 10,
                WindLayers =
                [
                    new WindLayer
                    {
                        Altitude = 3000,
                        Direction = 250,
                        Speed = 15,
                    },
                ],
                Metars = ["KOAK 031853Z 25015KT 5SM SCT020 A2995"],
            }
        );

        // During transition: KSFO only in period A, KOAK only in period B → no overrides
        var mid = timeline.GetWeatherAt(15 * 60);
        Assert.Null(mid.ParsedMetarOverrides);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static WeatherTimeline MakeTimeline(params WeatherPeriod[] periods)
    {
        return new WeatherTimeline
        {
            Name = "Test",
            ArtccId = "ZOA",
            Periods = [.. periods],
        };
    }
}
