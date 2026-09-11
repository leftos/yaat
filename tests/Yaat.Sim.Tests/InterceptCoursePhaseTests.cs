using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

public class InterceptCoursePhaseTests
{
    // Runway 28R at OAK: heading ~280, threshold at (37.72, -122.22)
    private const double RunwayHeading = 280.0;
    private const double ThresholdLat = 37.72;
    private const double ThresholdLon = -122.22;

    private static AircraftState MakeAircraft(double heading, double lat, double lon, string type = "B738")
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = type,
            TrueHeading = new TrueHeading(heading),
            Altitude = 3000,
            Position = new LatLon(lat, lon),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
    }

    private static PhaseContext MakeContext(AircraftState aircraft, double deltaSeconds = 1.0, AircraftCategory category = AircraftCategory.Jet)
    {
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = category,
            DeltaSeconds = deltaSeconds,
            Logger = NullLogger.Instance,
        };
    }

    private static InterceptCoursePhase MakePhase(string approachId = "I28R", bool forced = false, double? assignedHeading = null)
    {
        return new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(RunwayHeading),
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            ApproachId = approachId,
            AssignedInterceptHeading = assignedHeading is { } hdg ? new MagneticHeading(hdg) : null,
            ForcedIntercept = forced,
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
                FinalApproachCourse = new TrueHeading(RunwayHeading),
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
        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.05, aircraft.Position.Lon + 0.02); // ~3nm south — crosses the FAC line

        // Second tick — cross-track sign should flip, heading diff ~100° → bust-through
        complete = phase.OnTick(ctx);
        Assert.True(complete);

        // Should have the pilot's refusal
        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("localizer", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("I28R", aircraft.PendingWarnings[0]);

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
        aircraft.Position = new LatLon(37.72, -122.24);
        aircraft.TrueHeading = new TrueHeading(278);

        bool complete = phase.OnTick(ctx);
        // May or may not be complete depending on exact geometry,
        // but should never produce a notification
        Assert.Empty(aircraft.PendingWarnings);
    }

    /// <summary>
    /// Aircraft flying parallel to the course, never crossing. After 180s the timeout triggers — and the
    /// pilot asks for vectors rather than reporting a localizer it never reached.
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
                FinalApproachCourse = new TrueHeading(RunwayHeading),
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

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("unable to intercept the localizer, request vectors", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passing through", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// 20° intercept: aircraft heading 260, course 280 — crosses centerline and
    /// captures because 20° ≤ 30° threshold. Phase completes (hands off to FinalApproachPhase).
    /// </summary>
    [Fact]
    public void Capture_20DegIntercept_CompletesOnCrossing()
    {
        // Aircraft heading 260 on course 280 = 20° intercept
        var aircraft = MakeAircraft(heading: 260, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        // First tick — establish initial cross-track
        bool complete = phase.OnTick(ctx);
        Assert.False(complete);

        // Move aircraft across the course line
        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        // Second tick — cross-track sign flips, 20° ≤ 30° → capture
        complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.Phases.ActiveApproach);
        Assert.Equal(RunwayHeading, ctx.Targets.TargetTrueHeading!.Value.Degrees);
    }

    /// <summary>
    /// 30° intercept (at boundary): heading 250, course 280 — captures because
    /// 30° ≤ 30° threshold.
    /// </summary>
    [Fact]
    public void Capture_30DegIntercept_CompletesOnCrossing()
    {
        var aircraft = MakeAircraft(heading: 250, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        bool complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// 35° intercept: heading 245, course 280 — bust-through because 35° > 30°.
    /// </summary>
    [Fact]
    public void BustThrough_35DegIntercept_DetectsOnCrossing()
    {
        var aircraft = MakeAircraft(heading: 245, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        bool complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("localizer", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// Helicopter, 40° intercept: heading 240, course 280. TBL 5-9-1 allows a helicopter 45° at
    /// 2 miles or more from the gate, so this cut is legal and must capture — the same geometry
    /// busts a jet through (see <see cref="BustThrough_35DegIntercept_DetectsOnCrossing"/>).
    /// </summary>
    [Fact]
    public void Capture_Helicopter_40DegIntercept_CompletesOnCrossing()
    {
        var aircraft = MakeAircraft(heading: 240, lat: 37.74, lon: -122.23, type: "EC45");
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new HelicopterLandingPhase());

        var ctx = MakeContext(aircraft, category: AircraftCategory.Helicopter);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        bool complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.Phases.ActiveApproach);
        Assert.Equal(RunwayHeading, ctx.Targets.TargetTrueHeading!.Value.Degrees);
    }

    /// <summary>
    /// Helicopter, 50° intercept: heading 230, course 280. The helicopter allowance is 45°, not
    /// unbounded — beyond it the aircraft still busts through.
    /// </summary>
    [Fact]
    public void BustThrough_Helicopter_50DegIntercept_DetectsOnCrossing()
    {
        var aircraft = MakeAircraft(heading: 230, lat: 37.74, lon: -122.23, type: "EC45");
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new HelicopterLandingPhase());

        var ctx = MakeContext(aircraft, category: AircraftCategory.Helicopter);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        bool complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("localizer", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// 5° shallow intercept: heading 275, course 280 — captures quickly.
    /// </summary>
    [Fact]
    public void Capture_5DegShallowIntercept_CompletesOnCrossing()
    {
        var aircraft = MakeAircraft(heading: 275, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        bool complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// Verify that Capture() records InterceptCaptureDistanceNm on the ActiveApproach clearance.
    /// </summary>
    [Fact]
    public void Capture_RecordsCaptureDistance()
    {
        // Aircraft heading 270 on course 280 (10° off) — will capture
        // Place aircraft ~8nm from threshold, slightly off course
        var reciprocal = new TrueHeading(RunwayHeading).ToReciprocal();
        var onCourse = GeoMath.ProjectPoint(ThresholdLat, ThresholdLon, reciprocal, 8.0);

        var aircraft = MakeAircraft(heading: 270, lat: onCourse.Lat, lon: onCourse.Lon);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        // Should capture on first tick (already on course, heading close)
        bool complete = phase.OnTick(ctx);
        Assert.True(complete, "Phase should complete (capture)");

        // ActiveApproach should have capture distance and angle recorded
        Assert.NotNull(aircraft.Phases.ActiveApproach);
        Assert.NotNull(aircraft.Phases.ActiveApproach.InterceptCaptureDistanceNm);
        Assert.InRange(aircraft.Phases.ActiveApproach.InterceptCaptureDistanceNm!.Value, 7.0, 9.0);
        Assert.NotNull(aircraft.Phases.ActiveApproach.InterceptCaptureAngleDeg);
        Assert.InRange(aircraft.Phases.ActiveApproach.InterceptCaptureAngleDeg!.Value, 5.0, 15.0);
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
                FinalApproachCourse = new TrueHeading(RunwayHeading),
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
        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.05, aircraft.Position.Lon + 0.02);

        phase.OnTick(ctx);

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("ILS10R", aircraft.PendingWarnings[0]);
    }

    /// <summary>
    /// ForcedIntercept bypasses the 30° gate at the centerline-crossing check.
    /// 60° intercept — heading 220, course 280. Without ForcedIntercept this would bust through
    /// (see <see cref="BustThrough_35DegIntercept_DetectsOnCrossing"/>); with it set, the phase captures
    /// at the sign-flip, ActiveApproach is preserved, and no "localizer" notification is emitted.
    /// </summary>
    [Fact]
    public void ForcedIntercept_CapturesAtSteepAngle_NoNotification()
    {
        var aircraft = MakeAircraft(heading: 220, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
                Force = true,
            },
        };

        var phase = MakePhase(forced: true);
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        // First tick — establish initial cross-track
        phase.OnTick(ctx);

        // Move aircraft across the course line
        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        bool complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.Phases.ActiveApproach);
        Assert.Equal(RunwayHeading, ctx.Targets.TargetTrueHeading!.Value.Degrees);
    }

    /// <summary>
    /// Regression guard for the base (non-forced) 60° case. Same geometry as
    /// <see cref="ForcedIntercept_CapturesAtSteepAngle_NoNotification"/> but with forced=false —
    /// the phase must still bust through and clear the approach clearance.
    /// </summary>
    [Fact]
    public void NonForced_60DegIntercept_StillBustsThrough()
    {
        var aircraft = MakeAircraft(heading: 220, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };

        var phase = MakePhase(forced: false);
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);

        bool complete = phase.OnTick(ctx);
        Assert.True(complete);
        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("localizer", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// Snapshot round-trip preserves ForcedIntercept.
    /// </summary>
    [Fact]
    public void Snapshot_RoundTrip_PreservesForcedIntercept()
    {
        var phase = MakePhase(forced: true);
        phase.Status = PhaseStatus.Active;

        var dto = (InterceptCoursePhaseDto)phase.ToSnapshot();
        Assert.True(dto.ForcedIntercept);

        var restored = InterceptCoursePhase.FromSnapshot(dto);
        Assert.True(restored.ForcedIntercept);
    }

    // --- vNAS approach ids and the assigned-vector leniency (issue #429) ---

    // KFAT ILS Y runway 29R: published FAC 293° magnetic / ≈306° true (≈13°E variation).
    private const double FatFinalApproachCourseTrue = 306.0;
    private const double FatDeclination = 13.0;
    private const string FatApproachId = "I29RY";

    /// <summary>
    /// Builds the crossing geometry for the KFAT case: the aircraft sits 1.5nm off the final approach
    /// course 8nm from the threshold, and the caller teleports it to <paramref name="crossed"/> for the
    /// second tick so the cross-track sign flips.
    /// </summary>
    private static (LatLon Before, LatLon Crossed) FatCrossingPositions()
    {
        var reciprocal = new TrueHeading(FatFinalApproachCourseTrue).ToReciprocal();
        var onCourse = GeoMath.ProjectPoint(ThresholdLat, ThresholdLon, reciprocal, 8.0);
        var before = GeoMath.ProjectPoint(onCourse.Lat, onCourse.Lon, new TrueHeading(36.0), 1.5);
        var crossed = GeoMath.ProjectPoint(onCourse.Lat, onCourse.Lon, new TrueHeading(216.0), 1.5);
        return (new LatLon(before.Lat, before.Lon), new LatLon(crossed.Lat, crossed.Lon));
    }

    /// <summary>
    /// Crossing geometry for an arbitrary final approach course: the aircraft starts 1.5nm to one side of
    /// the course 8nm out and the caller teleports it 1.5nm to the other side for the second tick, so the
    /// cross-track sign flips.
    /// </summary>
    private static (LatLon Before, LatLon Crossed) CrossingPositions(TrueHeading course)
    {
        var onCourse = GeoMath.ProjectPoint(ThresholdLat, ThresholdLon, course.ToReciprocal(), 8.0);
        var before = GeoMath.ProjectPoint(onCourse.Lat, onCourse.Lon, new TrueHeading(course.Degrees + 90.0), 1.5);
        var crossed = GeoMath.ProjectPoint(onCourse.Lat, onCourse.Lon, new TrueHeading(course.Degrees - 90.0), 1.5);
        return (new LatLon(before.Lat, before.Lon), new LatLon(crossed.Lat, crossed.Lon));
    }

    private static PhaseList MakeCrossingPhaseList(InterceptCoursePhase phase, string approachId, string runwayId, TrueHeading course)
    {
        var phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = approachId,
                AirportCode = "OAK",
                RunwayId = runwayId,
                FinalApproachCourse = course,
            },
        };
        phases.Add(phase);
        phases.Add(new FinalApproachPhase());
        phases.Add(new LandingPhase());
        return phases;
    }

    private static InterceptCoursePhase MakeFatPhase(double assignedHeading)
    {
        return new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(FatFinalApproachCourseTrue),
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            ApproachId = FatApproachId,
            AssignedInterceptHeading = new MagneticHeading(assignedHeading),
        };
    }

    private static PhaseList MakeFatPhaseList(InterceptCoursePhase phase)
    {
        var phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = FatApproachId,
                AirportCode = "FAT",
                RunwayId = "29R",
                FinalApproachCourse = new TrueHeading(FatFinalApproachCourseTrue),
            },
        };
        phases.Add(phase);
        phases.Add(new FinalApproachPhase());
        phases.Add(new LandingPhase());
        return phases;
    }

    /// <summary>
    /// Issue #429. A vNAS/CIFP approach id carries a multiple-approach letter ("I29RY" = ILS Y runway
    /// 29R), which the old designator regex refused — the runway-number heading fell back to the FAC and
    /// the 33° true-heading-vs-FAC diff (13°E variation on a 20° cut) busted the aircraft through. With
    /// the designator parsed, runway 29R gives 290° and the cut is legal. The controller's vector lives on
    /// the phase (<c>AssignedInterceptHeading</c>) because CAPP clears the assigned heading as it installs
    /// the approach, so <c>Targets.AssignedMagneticHeading</c> is null by the first tick.
    /// </summary>
    [Fact]
    public void Capture_VnasApproachIdSuffix_UsesRunwayNumberLeniency()
    {
        var (before, crossed) = FatCrossingPositions();
        var aircraft = MakeAircraft(heading: 273, lat: before.Lat, lon: before.Lon);
        aircraft.Declination = FatDeclination;
        var phase = MakeFatPhase(assignedHeading: 260);
        aircraft.Phases = MakeFatPhaseList(phase);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));

        aircraft.Position = crossed;

        Assert.True(phase.OnTick(ctx));
        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// Issue #429 guard: the ≤30° gate is unchanged. Same KFAT geometry, but the aircraft is flying a cut
    /// that fails against both the FAC and the runway-number heading and the controller's vector is 31° off
    /// the runway number — every leniency misses and the aircraft still reports passing through.
    /// </summary>
    [Fact]
    public void BustThrough_AssignedHeading31Deg_StillBusts()
    {
        var (before, crossed) = FatCrossingPositions();
        var aircraft = MakeAircraft(heading: 245, lat: before.Lat, lon: before.Lon);
        aircraft.Declination = FatDeclination;
        var phase = MakeFatPhase(assignedHeading: 259);
        aircraft.Phases = MakeFatPhaseList(phase);

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));

        aircraft.Position = crossed;

        Assert.True(phase.OnTick(ctx));
        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("passing through the localizer", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FatApproachId, aircraft.PendingWarnings[0], StringComparison.Ordinal);
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// The runway number stands in for the final approach course only on an aligned straight-in. On an LDA
    /// to runway 28R whose course is 255° — 25° off the runway — the aircraft is 35° off the course it was
    /// told to intercept, and neither the runway-number term (10°) nor the controller's vector measured
    /// against the runway number (10°) may rescue it. Zero variation, so magnetic equals true throughout:
    /// only the offset gate decides.
    /// </summary>
    [Fact]
    public void BustThrough_OffsetFinal_RunwayNumberLeniencyDoesNotApply()
    {
        var course = new TrueHeading(255.0);
        var (before, crossed) = CrossingPositions(course);
        var aircraft = MakeAircraft(heading: 290, lat: before.Lat, lon: before.Lon);
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = course,
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            ApproachId = "L28R",
            AssignedInterceptHeading = new MagneticHeading(290),
        };
        aircraft.Phases = MakeCrossingPhaseList(phase, "L28R", "28R", course);

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));

        aircraft.Position = crossed;

        Assert.True(phase.OnTick(ctx));
        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("passing through the localizer", aircraft.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(aircraft.Phases.ActiveApproach);
    }

    /// <summary>
    /// A runway number is magnetic, so it must be compared with the aircraft's magnetic heading. Runway 28
    /// at a 10°W-variation field, course 271° true (281° magnetic — aligned), aircraft on 240° true: 31°
    /// off the course, but 250° magnetic, exactly the 30° cut the runway number allows. Measuring the
    /// runway number against the true heading reads 40° and busts the aircraft through.
    /// </summary>
    [Fact]
    public void Capture_WestVariation_RunwayNumberTermUsesMagneticHeading()
    {
        var course = new TrueHeading(271.0);
        var (before, crossed) = CrossingPositions(course);
        var aircraft = MakeAircraft(heading: 240, lat: before.Lat, lon: before.Lon);
        aircraft.Declination = -10.0;
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = course,
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            ApproachId = "I28R",
            AssignedInterceptHeading = null,
        };
        aircraft.Phases = MakeCrossingPhaseList(phase, "I28R", "28", course);

        var ctx = MakeContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));

        aircraft.Position = crossed;

        Assert.True(phase.OnTick(ctx));
        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.Phases.ActiveApproach);
        Assert.Equal(course.Degrees, ctx.Targets.TargetTrueHeading!.Value.Degrees);
    }

    /// <summary>
    /// Snapshot round-trip preserves the assigned intercept vector — a restored session must judge the
    /// intercept against the same angle the controller gave.
    /// </summary>
    [Fact]
    public void Snapshot_RoundTrip_PreservesAssignedInterceptHeading()
    {
        var phase = MakePhase(assignedHeading: 260);
        phase.Status = PhaseStatus.Active;

        var dto = (InterceptCoursePhaseDto)phase.ToSnapshot();
        Assert.Equal(260, dto.AssignedInterceptHeadingDeg);

        var restored = InterceptCoursePhase.FromSnapshot(dto);
        Assert.Equal(260, restored.AssignedInterceptHeading!.Value.Degrees);
    }
}
