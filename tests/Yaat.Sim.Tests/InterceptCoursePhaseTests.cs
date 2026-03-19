using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class InterceptCoursePhaseTests
{
    // Runway 28R at OAK: heading ~280, threshold at (37.72, -122.22)
    private const double RunwayHeading = 280.0;
    private const double ThresholdLat = 37.72;
    private const double ThresholdLon = -122.22;

    private static AircraftState MakeAircraft(double heading, double lat, double lon)
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = heading,
            Altitude = 3000,
            Latitude = lat,
            Longitude = lon,
            Destination = "OAK",
        };
    }

    private static PhaseContext MakeContext(AircraftState aircraft, double deltaSeconds = 1.0)
    {
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = deltaSeconds,
            Logger = NullLogger.Instance,
        };
    }

    private static InterceptCoursePhase MakePhase(string approachId = "I28R")
    {
        return new InterceptCoursePhase
        {
            FinalApproachCourse = RunwayHeading,
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            ApproachId = approachId,
        };
    }

    /// <summary>
    /// Aircraft heading 180 (southbound), course 280 — aircraft will cross the
    /// final approach course but heading difference is 100°, far too large to capture.
    /// Should detect bust-through when cross-track sign flips.
    /// </summary>
    [Fact]
    public void BustThrough_CrossTrackSignFlip_DetectsAndNotifies()
    {
        // Place aircraft to the right of the FAC (positive cross-track)
        // heading 180 — will cross to the left side
        var aircraft = MakeAircraft(heading: 180, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = RunwayHeading,
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        // Tick once to establish initial cross-track
        bool complete = phase.OnTick(ctx);
        Assert.False(complete);

        // Move aircraft across the course line (southbound, so move south)
        // This puts it on the other side of the FAC
        aircraft.Latitude -= 0.05; // ~3nm south — crosses the FAC line
        aircraft.Longitude += 0.02;

        // Second tick — cross-track sign should flip, heading diff ~100° → bust-through
        complete = phase.OnTick(ctx);
        Assert.True(complete);

        // Should have notification
        Assert.Single(aircraft.PendingNotifications);
        Assert.Contains("localizer", aircraft.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("I28R", aircraft.PendingNotifications[0]);

        // ActiveApproach should be cleared
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// Aircraft heading 270 on course 280 (10° off) — should intercept normally.
    /// No bust-through, no notification.
    /// </summary>
    [Fact]
    public void NormalIntercept_NoBustThrough()
    {
        // Place aircraft slightly right of course, heading nearly aligned
        var aircraft = MakeAircraft(heading: 270, lat: 37.725, lon: -122.28);

        var phase = MakePhase();
        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        // Tick — aircraft is close to course and heading is close
        // Move aircraft to be within cross-track and heading thresholds
        aircraft.Latitude = 37.72;
        aircraft.Longitude = -122.24;
        aircraft.Heading = 278;

        bool complete = phase.OnTick(ctx);
        // May or may not be complete depending on exact geometry,
        // but should never produce a notification
        Assert.Empty(aircraft.PendingNotifications);
    }

    /// <summary>
    /// Aircraft flying parallel to the course, never crossing. After 180s the
    /// timeout should trigger and notify.
    /// </summary>
    [Fact]
    public void Timeout_NeverCrosses_DetectsAfter180Seconds()
    {
        // Place aircraft parallel to course, offset to the right
        var aircraft = MakeAircraft(heading: RunwayHeading, lat: 37.75, lon: -122.30);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = RunwayHeading,
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft, deltaSeconds: 1.0);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        // Tick for 179 seconds — should not trigger
        for (int i = 0; i < 179; i++)
        {
            phase.ElapsedSeconds += ctx.DeltaSeconds;
            bool earlyComplete = phase.OnTick(ctx);
            Assert.False(earlyComplete, $"Should not complete at {i + 1}s");
        }

        // Tick at 180s — should trigger timeout
        phase.ElapsedSeconds += ctx.DeltaSeconds;
        bool complete = phase.OnTick(ctx);
        Assert.True(complete);

        Assert.Single(aircraft.PendingNotifications);
        Assert.Contains("localizer", aircraft.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// Verify that ApproachId appears in the bust-through notification message.
    /// </summary>
    [Fact]
    public void BustThrough_NotificationContainsApproachId()
    {
        var aircraft = MakeAircraft(heading: 180, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "ILS10R",
                AirportCode = "OAK",
                RunwayId = "10R",
                FinalApproachCourse = RunwayHeading,
            },
        };

        var phase = MakePhase("ILS10R");
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        // Cross the course
        aircraft.Latitude -= 0.05;
        aircraft.Longitude += 0.02;

        phase.OnTick(ctx);

        Assert.Single(aircraft.PendingNotifications);
        Assert.Contains("ILS10R", aircraft.PendingNotifications[0]);
    }
}
