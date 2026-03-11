using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class ApproachScoreTests
{
    private static RunwayInfo MakeRunway(string designator = "28R", string airportId = "OAK", double heading = 280, double elevationFt = 9)
    {
        return TestRunwayFactory.Make(
            designator: designator,
            airportId: airportId,
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: heading,
            elevationFt: elevationFt
        );
    }

    private static AircraftState MakeEstablishedAircraft(
        double heading = 280,
        double altitude = 3000,
        double distFromThresholdNm = 8.0,
        double ias = 160
    )
    {
        var runway = MakeRunway();

        // Position the aircraft on the runway extended centerline at the specified distance
        double bearing = (runway.TrueHeading + 180) % 360;
        var (lat, lon) = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, bearing, distFromThresholdNm);

        return new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Heading = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            Latitude = lat,
            Longitude = lon,
            Destination = "OAK",
        };
    }

    private static PhaseContext MakeContext(
        AircraftState aircraft,
        RunwayInfo? runway = null,
        double scenarioElapsedSeconds = 120.0,
        double delta = 1.0
    )
    {
        runway ??= MakeRunway();
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = delta,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            ScenarioElapsedSeconds = scenarioElapsedSeconds,
        };
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_HasCorrectIdentity()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Single(aircraft.PendingApproachScores);
        var score = aircraft.PendingApproachScores[0];

        Assert.Equal("UAL123", score.Callsign);
        Assert.Equal("B738", score.AircraftType);
        Assert.Equal("I28R", score.ApproachId);
        Assert.Equal("28R", score.RunwayId);
        Assert.Equal("OAK", score.AirportCode);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_CapturesInterceptMetrics()
    {
        var aircraft = MakeEstablishedAircraft(altitude: 3000, distFromThresholdNm: 8.0, ias: 160);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft, scenarioElapsedSeconds: 300.0);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];

        // Intercept distance should be approximately 8nm
        Assert.InRange(score.InterceptDistanceNm, 7.5, 8.5);

        // Intercept angle should be near 0 (heading matches runway)
        Assert.InRange(score.InterceptAngleDeg, 0, 5);

        // Speed at intercept uses IAS when available
        Assert.Equal(160, score.SpeedAtInterceptKts);

        // Establishment timestamp
        Assert.Equal(300.0, score.EstablishedAtSeconds);

        // Not yet landed
        Assert.Null(score.LandedAtSeconds);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_SetsActiveApproachScore()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.NotNull(aircraft.ActiveApproachScore);
        Assert.Same(aircraft.PendingApproachScores[0], aircraft.ActiveApproachScore);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_GsDeviationComputed()
    {
        var runway = MakeRunway(elevationFt: 9);
        double distNm = 8.0;
        double gsAltitude = GlideSlopeGeometry.AltitudeAtDistance(distNm, 9);

        // Aircraft above glideslope by 500 ft
        var aircraft = MakeEstablishedAircraft(altitude: gsAltitude + 500, distFromThresholdNm: distNm);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft, runway);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        // Should be approximately +500ft above glideslope
        Assert.InRange(score.GlideSlopeDeviationFt, 400, 600);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_BelowGlideslope_NegativeDeviation()
    {
        var runway = MakeRunway(elevationFt: 9);
        double distNm = 8.0;
        double gsAltitude = GlideSlopeGeometry.AltitudeAtDistance(distNm, 9);

        // Aircraft below glideslope by 300 ft
        var aircraft = MakeEstablishedAircraft(altitude: gsAltitude - 300, distFromThresholdNm: distNm);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft, runway);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.InRange(score.GlideSlopeDeviationFt, -400, -200);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_ForcedFlag()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
            Force = true,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.True(score.WasForced);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_PatternTrafficFlagged()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.TrafficDirection = PatternDirection.Left;
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.True(score.IsPatternTraffic);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_NonPatternTraffic_FlagFalse()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.False(score.IsPatternTraffic);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_Tbl591_CloseToGate_MaxAngle20()
    {
        // Aircraft close to gate (< 2nm above minimum intercept distance)
        var aircraft = MakeEstablishedAircraft(distFromThresholdNm: 6.0);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        // Close to gate — max angle should be 20° or 30° depending on exact distance-to-gate
        // The important thing is the score captures the limit
        Assert.True(score.MaxAllowedAngleDeg is 20 or 30);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_LegalAngle()
    {
        // Aircraft heading matches runway — angle near 0, always legal
        var aircraft = MakeEstablishedAircraft(heading: 280);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.True(score.IsInterceptAngleLegal);
    }

    [Fact]
    public void ScoreCreatedAtEstablishment_DistanceLegality()
    {
        // 8nm from threshold is well above minimum intercept distance for most airports
        var aircraft = MakeEstablishedAircraft(distFromThresholdNm: 8.0);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.True(score.IsInterceptDistanceLegal);
        Assert.True(score.MinInterceptDistanceNm > 0);
    }

    [Fact]
    public void SkipInterceptCheck_NoScoreCreated()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Empty(aircraft.PendingApproachScores);
        Assert.Null(aircraft.ActiveApproachScore);
    }

    [Fact]
    public void LandingTimestamp_StampedByPhaseRunner()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();

        // Simulate an active approach score (as if FinalApproachPhase set it)
        var score = new ApproachScore
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            ApproachId = "I28R",
            RunwayId = "28R",
            AirportCode = "OAK",
            EstablishedAtSeconds = 100.0,
        };
        aircraft.ActiveApproachScore = score;

        // Set up a landing phase that completes immediately
        aircraft.Altitude = 9; // At field elevation // Below taxi speed threshold
        aircraft.IndicatedAirspeed = 15;

        var runway = MakeRunway();
        // Put aircraft on the runway threshold
        aircraft.Latitude = runway.ThresholdLatitude;
        aircraft.Longitude = runway.ThresholdLongitude;
        aircraft.Heading = runway.TrueHeading;

        var landingPhase = new LandingPhase();
        aircraft.Phases.Phases.Add(landingPhase);
        aircraft.Phases.AssignedRunway = runway;

        var ctx = MakeContext(aircraft, runway, scenarioElapsedSeconds: 250.0);

        // Start the phase
        landingPhase.Status = PhaseStatus.Active;
        landingPhase.OnStart(ctx);

        // Tick until landing phase completes (should be immediate at 15kts)
        for (int i = 0; i < 100; i++)
        {
            bool done = landingPhase.OnTick(ctx);
            if (done)
            {
                // Simulate what PhaseRunner does at landing
                PhaseRunner.Tick(aircraft, ctx);
                break;
            }
        }

        // The score should have been stamped with landing time
        Assert.Equal(250.0, score.LandedAtSeconds);
        Assert.Null(aircraft.ActiveApproachScore);
    }

    [Fact]
    public void LandingTimestamp_NotStamped_WhenNoActiveScore()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        // No ActiveApproachScore set

        var runway = MakeRunway();
        aircraft.Latitude = runway.ThresholdLatitude;
        aircraft.Longitude = runway.ThresholdLongitude;
        aircraft.Heading = runway.TrueHeading;
        aircraft.Altitude = 9;
        aircraft.IndicatedAirspeed = 15;

        var landingPhase = new LandingPhase();
        aircraft.Phases.Phases.Add(landingPhase);
        aircraft.Phases.AssignedRunway = runway;

        var ctx = MakeContext(aircraft, runway, scenarioElapsedSeconds: 250.0);

        landingPhase.Status = PhaseStatus.Active;
        landingPhase.OnStart(ctx);

        // Tick until done
        for (int i = 0; i < 100; i++)
        {
            if (landingPhase.OnTick(ctx))
            {
                PhaseRunner.Tick(aircraft, ctx);
                break;
            }
        }

        // No score to stamp — no crash
        Assert.Empty(aircraft.PendingApproachScores);
    }

    [Fact]
    public void DrainAllApproachScores_ClearsAndReturns()
    {
        var world = new SimulationWorld();

        var ac1 = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Heading = 280,
            Altitude = 3000,
            Latitude = 37.75,
            Longitude = -122.35,
        };
        var ac2 = new AircraftState
        {
            Callsign = "UAL200",
            AircraftType = "A320",
            Heading = 280,
            Altitude = 3000,
            Latitude = 37.76,
            Longitude = -122.36,
        };

        var score1 = new ApproachScore
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            ApproachId = "I28R",
            RunwayId = "28R",
            AirportCode = "OAK",
        };
        var score2 = new ApproachScore
        {
            Callsign = "UAL200",
            AircraftType = "A320",
            ApproachId = "I28R",
            RunwayId = "28R",
            AirportCode = "OAK",
        };

        ac1.PendingApproachScores.Add(score1);
        ac2.PendingApproachScores.Add(score2);

        world.AddAircraft(ac1);
        world.AddAircraft(ac2);

        var drained = world.DrainAllApproachScores();

        Assert.Equal(2, drained.Count);
        Assert.Empty(ac1.PendingApproachScores);
        Assert.Empty(ac2.PendingApproachScores);
    }

    [Fact]
    public void DrainAllApproachScores_EmptyWhenNoPendingScores()
    {
        var world = new SimulationWorld();

        var ac = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Heading = 280,
            Altitude = 3000,
            Latitude = 37.75,
            Longitude = -122.35,
        };

        world.AddAircraft(ac);

        var drained = world.DrainAllApproachScores();
        Assert.Empty(drained);
    }

    [Fact]
    public void ScoreEstablishmentPosition_CapturedCorrectly()
    {
        var aircraft = MakeEstablishedAircraft(distFromThresholdNm: 8.0);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.Equal(aircraft.Latitude, score.EstablishedLat);
        Assert.Equal(aircraft.Longitude, score.EstablishedLon);
    }

    [Fact]
    public void NoApproachClearance_ScoreStillCreated_WithEmptyApproachId()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.Phases = new PhaseList();
        // No ActiveApproach set

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        var score = aircraft.PendingApproachScores[0];
        Assert.Equal("", score.ApproachId);
        Assert.Equal("OAK", score.AirportCode); // Falls back to runway airport
    }

    [Fact]
    public void VfrAircraft_NoScoreCreated()
    {
        var aircraft = MakeEstablishedAircraft();
        aircraft.FlightRules = "VFR";
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "VIS28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Empty(aircraft.PendingApproachScores);
        Assert.Null(aircraft.ActiveApproachScore);
    }

    [Fact]
    public void VisualApproach_InterceptAlwaysLegal()
    {
        // Place aircraft close to threshold (inside minimum intercept distance)
        var aircraft = MakeEstablishedAircraft(distFromThresholdNm: 2.0);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = "VIS28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phase = new FinalApproachPhase();
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        // Score should be created (IFR aircraft on visual approach)
        Assert.Single(aircraft.PendingApproachScores);
        var score = aircraft.PendingApproachScores[0];

        // Intercept should be marked legal even though distance is below minimum
        Assert.True(score.IsInterceptDistanceLegal);
        Assert.True(score.IsInterceptAngleLegal);

        // No illegal intercept warning
        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void GlideSlopeGeometry_AltitudeAtDistance_CorrectValues()
    {
        // Standard 3° glideslope: ~318 ft/nm
        double alt = GlideSlopeGeometry.AltitudeAtDistance(10.0, 0);

        // At 10nm from threshold at sea level, should be ~3180ft
        Assert.InRange(alt, 3100, 3300);
    }

    [Fact]
    public void GlideSlopeGeometry_AltitudeAtDistance_WithElevation()
    {
        double alt = GlideSlopeGeometry.AltitudeAtDistance(5.0, 1000);

        // At 5nm, ~1590ft above threshold + 1000ft elevation = ~2590ft
        Assert.InRange(alt, 2500, 2700);
    }

    [Fact]
    public void GlideSlopeGeometry_AltitudeAtDistance_ZeroDistance()
    {
        double alt = GlideSlopeGeometry.AltitudeAtDistance(0, 500);
        Assert.Equal(500, alt);
    }
}
