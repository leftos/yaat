using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// A shadow aircraft is driven by external samples, not <see cref="FlightPhysics"/>: it is
/// dead-reckoned along the last sample's track/ground speed/vertical speed between samples,
/// adopts each fresh sample unconditionally, and is re-derived from the latest sample every
/// tick so replaying the same samples reproduces the same positions.
/// </summary>
public class LiveTrafficKinematicsTests
{
    private static readonly LatLon Origin = new(37.0, -122.0);

    public LiveTrafficKinematicsTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LiveTrafficSample Sample(
        double observedAt,
        LatLon position,
        double altitudeFt,
        double groundSpeedKts,
        double trueTrackDeg,
        double? verticalSpeedFpm,
        LiveTrafficSource source
    ) => new(observedAt, position.Lat, position.Lon, altitudeFt, groundSpeedKts, trueTrackDeg, verticalSpeedFpm, source, 4521);

    private static LiveTrafficSample AirborneSample(double observedAt, double verticalSpeedFpm = -600) =>
        Sample(observedAt, Origin, 10_000, 250, 90, verticalSpeedFpm, LiveTrafficSource.Stars);

    private static AircraftState Shadow(LiveTrafficSample sample) =>
        LiveTrafficKinematics.CreateShadow(
            "UAL123",
            "B738",
            sample,
            new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KSFO",
                Destination = "KLAX",
            }
        );

    private static SimulationEngine EngineWith(WeatherProfile? weather, params AircraftState[] aircraft)
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.Weather = weather;
        foreach (var ac in aircraft)
        {
            engine.World.AddAircraft(ac);
        }

        return engine;
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    [Fact]
    public void GroundAcceleration_IsTheLeastSquaresSlopeOfTheReportedSpeeds_InsideTheWindow()
    {
        var ac = Shadow(Sample(0, Origin, 0, 10, 280, 0, LiveTrafficSource.Asdex));
        foreach (var (t, gs) in new[] { (1.0, 14.0), (2.0, 21.0), (3.0, 24.0), (4.0, 31.0), (5.0, 35.0) })
        {
            LiveTrafficKinematics.Apply(ac, Sample(t, Origin, 0, gs, 280, 0, LiveTrafficSource.Asdex));
        }

        // Points at t = 1..5 (t = 0 falls outside the 4 s window): slope of a straight-line fit through (14, 21, 24, 31, 35) is 5.2 kt/s.
        Assert.Equal(5.2, LiveTrafficKinematics.GroundAcceleration(ac.LiveTraffic!, 4.0)!.Value, 2);
    }

    [Fact]
    public void GroundAcceleration_IsUnknown_WithTooFewSamples_OrWhileCoasting()
    {
        var ac = Shadow(Sample(0, Origin, 0, 10, 280, 0, LiveTrafficSource.Asdex));
        Assert.Null(LiveTrafficKinematics.GroundAcceleration(ac.LiveTraffic!, 4.0));
        LiveTrafficKinematics.Apply(ac, Sample(1, Origin, 0, 15, 280, 0, LiveTrafficSource.Asdex));
        Assert.Null(LiveTrafficKinematics.GroundAcceleration(ac.LiveTraffic!, 4.0));
        LiveTrafficKinematics.Apply(ac, Sample(2, Origin, 0, 20, 280, 0, LiveTrafficSource.Asdex));
        Assert.NotNull(LiveTrafficKinematics.GroundAcceleration(ac.LiveTraffic!, 4.0));

        ac.LiveTraffic!.IsCoasting = true;
        Assert.Null(LiveTrafficKinematics.GroundAcceleration(ac.LiveTraffic, 4.0));
    }

    [Fact]
    public void CreateShadow_IsShadowWithSampleStateAndReportedBeacon()
    {
        var ac = Shadow(AirborneSample(0));

        Assert.True(ac.IsShadow);
        Assert.Null(ac.Phases);
        Assert.Equal(Origin, ac.Position);
        Assert.Equal(10_000, ac.Altitude);
        Assert.False(ac.IsOnGround);
        Assert.Equal(4521u, ac.Transponder.Code);
        Assert.Equal(LiveTrafficSource.Stars, ac.LiveTraffic!.Source);
        Assert.InRange(ac.GroundSpeed, 249.5, 250.5);
        Assert.InRange(ac.TrueHeading.Degrees, 89.5, 90.5);
    }

    [Fact]
    public void DeadReckonsBetweenSamples_AlongTrackAtGroundSpeed()
    {
        var ac = Shadow(AirborneSample(0));
        var engine = EngineWith(null, ac);

        TickSeconds(engine, 10);

        double expectedNm = 250 * 10 / 3600.0;
        var expected = GeoMath.ProjectPoint(Origin, new TrueHeading(90), expectedNm);
        Assert.InRange(GeoMath.DistanceNm(expected, ac.Position), 0, 0.005);
        Assert.InRange(ac.Altitude, 9_900 - 1, 9_900 + 1);
        Assert.Equal(-600, ac.VerticalSpeed);
        Assert.InRange(ac.GroundSpeed, 249.5, 250.5);
        Assert.InRange(ac.TrueTrack.Degrees, 89.5, 90.5);
        Assert.True(ac.HasBeenAirborne);
    }

    [Fact]
    public void MotionIsSubTickResolution_NotOncePerSecond()
    {
        var ac = Shadow(AirborneSample(0));
        var engine = EngineWith(null, ac);

        engine.TickPhysics(0.25);

        double expectedNm = 250 * 0.25 / 3600.0;
        Assert.InRange(GeoMath.DistanceNm(Origin, ac.Position), expectedNm * 0.9, expectedNm * 1.1);
    }

    [Fact]
    public void ShadowIgnoresControlTargetsAndPhysics()
    {
        var ac = Shadow(AirborneSample(0));
        ac.Targets.TargetTrueHeading = new TrueHeading(180);
        ac.Targets.TargetAltitude = 5_000;
        ac.Targets.TargetSpeed = 150;
        var engine = EngineWith(null, ac);

        TickSeconds(engine, 30);

        var expected = GeoMath.ProjectPoint(Origin, new TrueHeading(90), 250 * 30 / 3600.0);
        Assert.InRange(GeoMath.DistanceNm(expected, ac.Position), 0, 0.01);
        Assert.InRange(ac.TrueTrack.Degrees, 89.5, 90.5);
    }

    [Fact]
    public void AirVectorMatchesSampledGroundSpeed_UnderCrosswind()
    {
        var weather = new WeatherProfile
        {
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = 360,
                    Speed = 100,
                },
                new WindLayer
                {
                    Altitude = 30_000,
                    Direction = 360,
                    Speed = 100,
                },
            ],
        };
        var ac = Shadow(AirborneSample(0, verticalSpeedFpm: 0));
        var engine = EngineWith(weather, ac);

        TickSeconds(engine, 5);

        // Wind from 360 blows south; the air vector must point north of the 090 track to hold it.
        Assert.InRange(ac.GroundSpeed, 249.5, 250.5);
        Assert.InRange(ac.TrueTrack.Degrees, 89.5, 90.5);
        Assert.InRange(ac.TrueHeading.Degrees, 60, 80);
        Assert.True(ac.IndicatedAirspeed > 0);
        Assert.NotEqual(0, ac.Declination);
        Assert.NotEqual(ac.TrueHeading.Degrees, ac.MagneticHeading.Degrees);
        var expected = GeoMath.ProjectPoint(Origin, new TrueHeading(90), 250 * 5 / 3600.0);
        Assert.InRange(GeoMath.DistanceNm(expected, ac.Position), 0, 0.005);
    }

    [Fact]
    public void CoastsAfterDeliverySilence_AndKeepsMoving()
    {
        var ac = Shadow(AirborneSample(0));
        var engine = EngineWith(null, ac);

        TickSeconds(engine, 45);
        Assert.False(ac.LiveTraffic!.IsCoasting);

        TickSeconds(engine, 1);
        Assert.True(ac.LiveTraffic.IsCoasting);

        var before = ac.Position;
        TickSeconds(engine, 1);
        Assert.True(GeoMath.DistanceNm(before, ac.Position) > 0.05);
    }

    [Fact]
    public void SamplesDeliveredLate_DoNotCoast_WhileTheyKeepComing()
    {
        var ac = Shadow(AirborneSample(0));
        var engine = EngineWith(null, ac);
        TickSeconds(engine, 15);

        // The feed delivers ~11 s behind real time (SCDS latency): observed at 4, applied at 15.
        Assert.True(engine.ApplyLiveTrafficSample("UAL123", AirborneSample(4), null));

        Assert.False(ac.LiveTraffic!.IsCoasting);
        Assert.InRange(ac.LiveTraffic.SecondsSinceSample, 10.99, 11.01); // placement still ages from the observation

        TickSeconds(engine, 45); // at the STARS silence backstop, not past it
        Assert.False(ac.LiveTraffic.IsCoasting);

        TickSeconds(engine, 1); // 46 s of delivery silence: the pipe has gone quiet on this track
        Assert.True(ac.LiveTraffic.IsCoasting);
    }

    [Fact]
    public void FreshSample_ResetsCoastAndAdoptsPositionUnconditionally()
    {
        var ac = Shadow(AirborneSample(0));
        var engine = EngineWith(null, ac);
        TickSeconds(engine, 46);
        Assert.True(ac.LiveTraffic!.IsCoasting);

        var jumped = GeoMath.ProjectPoint(Origin, new TrueHeading(180), 1.0);
        Assert.True(LiveTrafficKinematics.Apply(ac, Sample(46, jumped, 9_000, 200, 180, -300, LiveTrafficSource.Stars)));

        Assert.False(ac.LiveTraffic.IsCoasting);
        Assert.Equal(jumped, ac.Position);
        Assert.Equal(9_000, ac.Altitude);
        Assert.Equal(0, ac.LiveTraffic.SecondsSinceSample);

        TickSeconds(engine, 1);
        var expected = GeoMath.ProjectPoint(jumped, new TrueHeading(180), 200 / 3600.0);
        Assert.InRange(GeoMath.DistanceNm(expected, ac.Position), 0, 0.005);
    }

    [Fact]
    public void SourceFlaggedCoast_IsCoastingFromTheSampleOn_AndClearsOnAFreshReturn()
    {
        var ac = Shadow(AirborneSample(0) with { SourceCoasting = true });
        Assert.True(ac.LiveTraffic!.IsCoasting);

        LiveTrafficKinematics.Advance(ac, 1.0, null, 1.0);
        Assert.True(ac.LiveTraffic.IsCoasting);
        Assert.True(GeoMath.DistanceNm(Origin, ac.Position) > 0.05); // still dead-reckoned while coasting

        LiveTrafficKinematics.Apply(ac, AirborneSample(4.5));
        Assert.False(ac.LiveTraffic.IsCoasting);
        Assert.False(ac.LiveTraffic.SourceCoasting);
    }

    [Fact]
    public void DeadReckonedClimb_LevelsAtTheFeedAssignedAltitude()
    {
        var ac = Shadow(AirborneSample(0, verticalSpeedFpm: 2000) with { AssignedAltitudeFt = 11_000 });
        LiveTrafficKinematics.Advance(ac, 120, null, 120);
        Assert.Equal(11_000, ac.Altitude);
    }

    [Fact]
    public void DeadReckonedDescent_LevelsAtTheFeedInterimAltitude()
    {
        var ac = Shadow(AirborneSample(0, verticalSpeedFpm: -2000) with { InterimAltitudeFt = 9_000, AssignedAltitudeFt = 5_000 });
        LiveTrafficKinematics.Advance(ac, 120, null, 120);
        Assert.Equal(9_000, ac.Altitude);
    }

    [Fact]
    public void DeadReckonedClimb_IgnoresAStaleClearanceBelowTheObservedAltitude()
    {
        var ac = Shadow(AirborneSample(0, verticalSpeedFpm: 2000) with { AssignedAltitudeFt = 5_000 });
        LiveTrafficKinematics.Advance(ac, 120, null, 120);
        Assert.Equal(10_000, ac.Altitude); // holds the observed altitude, never dragged toward the stale clearance
    }

    [Fact]
    public void SurfaceSample_FreezesPastTheProjectionCap()
    {
        var ac = Shadow(Sample(0, Origin, 0, 20, 90, 0, LiveTrafficSource.Asdex));
        LiveTrafficKinematics.Advance(ac, 60, null, 60);

        double capped = 20 * LiveTrafficKinematics.AsdexProjectionCapSeconds / 3600.0;
        Assert.InRange(GeoMath.DistanceNm(Origin, ac.Position), capped - 0.01, capped + 0.01);
    }

    [Fact]
    public void DeadReckonedDescent_FloorsAtTheFieldElevation_NotSeaLevel()
    {
        var navDb = NavigationDatabase.Instance;
        var den = navDb.FindNearestSizeableAirport(new LatLon(39.8617, -104.6731), 10_000, 5)!.Value;
        double field = navDb.GetAirportElevation(den.Id)!.Value;
        Assert.True(field > 5_000, $"{den.Id} elevation {field}");

        // Short final at DEN, sinking at 1 500 fpm, then the feed goes quiet for a minute.
        var ac = Shadow(Sample(0, new LatLon(den.Lat, den.Lon), field + 200, 130, 170, -1500, LiveTrafficSource.Stars));
        LiveTrafficKinematics.Advance(ac, 60, null, 60);

        Assert.Equal(field, ac.Altitude, 0);
    }

    [Fact]
    public void OutOfOrderSample_IsIgnored()
    {
        var ac = Shadow(AirborneSample(5));

        var stale = Sample(3, GeoMath.ProjectPoint(Origin, new TrueHeading(0), 2), 8_000, 100, 0, 0, LiveTrafficSource.Eram);
        Assert.False(LiveTrafficKinematics.Apply(ac, stale));

        Assert.Equal(Origin, ac.Position);
        Assert.Equal(10_000, ac.Altitude);
        Assert.Equal(LiveTrafficSource.Stars, ac.LiveTraffic!.Source);
    }

    [Fact]
    public void VerticalSpeed_DerivedFromAltitudeDeltaWhenTheFeedHasNone()
    {
        var ac = Shadow(Sample(0, Origin, 10_000, 250, 90, null, LiveTrafficSource.Eram));
        Assert.Equal(0, ac.VerticalSpeed);

        LiveTrafficKinematics.Apply(ac, Sample(10, Origin, 9_900, 250, 90, null, LiveTrafficSource.Eram));

        Assert.InRange(ac.VerticalSpeed, -600 - 1, -600 + 1);
    }

    [Fact]
    public void SurfaceSample_IsOnGroundWithWheelSpeed()
    {
        var ac = Shadow(Sample(0, Origin, 10, 18, 45, null, LiveTrafficSource.Asdex));
        var engine = EngineWith(null, ac);

        TickSeconds(engine, 2);

        Assert.True(ac.IsOnGround);
        Assert.Equal(18, ac.IndicatedAirspeed);
        Assert.InRange(ac.GroundSpeed, 17.9, 18.1);
        Assert.InRange(ac.TrueHeading.Degrees, 44.9, 45.1);
        Assert.False(ac.HasBeenAirborne);
    }

    [Fact]
    public void Snapshot_RoundTripsLiveTrafficState()
    {
        var ac = Shadow(AirborneSample(3));
        ac.LiveTraffic!.SecondsSinceSample = 2.5;
        ac.LiveTraffic.IsCoasting = true;
        ac.LiveTraffic.ExternalId = "gufi-1";

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);

        Assert.True(restored.IsShadow);
        var lt = restored.LiveTraffic!;
        Assert.Equal(LiveTrafficSource.Stars, lt.Source);
        Assert.Equal(3, lt.ObservedAtSimSeconds);
        Assert.Equal(2.5, lt.SecondsSinceSample);
        Assert.Equal(Origin, lt.SamplePosition);
        Assert.Equal(10_000, lt.SampleAltitude);
        Assert.Equal(250, lt.SampleGroundSpeed);
        Assert.Equal(90, lt.SampleTrueTrack);
        Assert.Equal(-600, lt.SampleVerticalSpeed);
        Assert.True(lt.IsCoasting);
        Assert.Equal("gufi-1", lt.ExternalId);
    }

    [Fact]
    public void NormalAircraftSnapshot_HasNoLiveTraffic()
    {
        var ac = new AircraftState
        {
            Callsign = "N1",
            AircraftType = "C172",
            Position = Origin,
        };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);

        Assert.False(restored.IsShadow);
        Assert.Null(restored.LiveTraffic);
    }

    [Fact]
    public void StatusDescriber_ShowsLiveAndCoast()
    {
        var ac = Shadow(AirborneSample(0));
        Assert.Equal("LIVE", AircraftStatusDescriber.Describe(ac, AircraftStatusContext.None).Text);

        ac.LiveTraffic!.IsCoasting = true;
        Assert.Equal("LIVE CST", AircraftStatusDescriber.Describe(ac, AircraftStatusContext.None).Text);
    }

    [Fact]
    public void PilotProactive_SkipsShadows()
    {
        var ac = Shadow(AirborneSample(0));
        var engine = EngineWith(null, ac);
        engine.Scenario!.SoloTrainingMode = true;

        TickSeconds(engine, 5);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Empty(ac.PendingWarnings);
    }
}
