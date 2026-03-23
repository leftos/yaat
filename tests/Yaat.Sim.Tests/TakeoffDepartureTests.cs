using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests that TakeoffPhase.ApplyDepartureHeading computes correct heading
/// from each DepartureInstruction variant at liftoff.
/// </summary>
public class TakeoffDepartureTests
{
    private const double FieldElevation = 0;
    private const double RunwayHeading = 280;

    private static RunwayInfo MakeRunway(double heading = RunwayHeading)
    {
        return TestRunwayFactory.Make(designator: "28", airportId: "KSFO", heading: heading, elevationFt: FieldElevation);
    }

    /// <summary>
    /// Creates a TakeoffPhase with the given departure instruction,
    /// runs OnStart + ticks until airborne, and returns the resulting
    /// TargetHeading and PreferredTurnDirection.
    /// </summary>
    private static (double? TargetHeading, TurnDirection? TurnDir) RunTakeoff(DepartureInstruction departure, double runwayHeading = RunwayHeading)
    {
        var runway = MakeRunway(runwayHeading);
        var phase = new TakeoffPhase();
        phase.SetAssignedDeparture(departure);

        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(runwayHeading),
            Altitude = FieldElevation,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        // Tick until airborne (ground roll accelerates to Vr)
        for (int i = 0; i < 300; i++)
        {
            if (phase.OnTick(ctx))
            {
                break;
            }

            // Manually climb after liftoff
            if (!aircraft.IsOnGround)
            {
                aircraft.Altitude += 50;
            }
        }

        return (targets.TargetTrueHeading?.Degrees, targets.PreferredTurnDirection);
    }

    [Fact]
    public void DefaultDeparture_KeepsRunwayHeading()
    {
        var (hdg, dir) = RunTakeoff(new DefaultDeparture());
        Assert.Equal(RunwayHeading, hdg);
        Assert.Null(dir);
    }

    [Fact]
    public void RunwayHeadingDeparture_KeepsRunwayHeading()
    {
        var (hdg, dir) = RunTakeoff(new RunwayHeadingDeparture());
        Assert.Equal(RunwayHeading, hdg);
        Assert.Null(dir);
    }

    [Fact]
    public void RelativeTurnRight90_Crosswind()
    {
        // Runway 280 + 90 right = 010
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(90, TurnDirection.Right));
        Assert.Equal(10, hdg);
        Assert.Equal(TurnDirection.Right, dir);
    }

    [Fact]
    public void RelativeTurnRight180_Downwind()
    {
        // Runway 280 + 180 right = 100
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(180, TurnDirection.Right));
        Assert.Equal(100, hdg);
        Assert.Equal(TurnDirection.Right, dir);
    }

    [Fact]
    public void RelativeTurnLeft90_Crosswind()
    {
        // Runway 280 - 90 left = 190
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(90, TurnDirection.Left));
        Assert.Equal(190, hdg);
        Assert.Equal(TurnDirection.Left, dir);
    }

    [Fact]
    public void RelativeTurnLeft180_Downwind()
    {
        // Runway 280 - 180 left = 100
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(180, TurnDirection.Left));
        Assert.Equal(100, hdg);
        Assert.Equal(TurnDirection.Left, dir);
    }

    [Fact]
    public void RelativeTurnRight270()
    {
        // Runway 280 + 270 = 550 → 190
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(270, TurnDirection.Right));
        Assert.Equal(190, hdg);
        Assert.Equal(TurnDirection.Right, dir);
    }

    [Fact]
    public void FlyHeading_NoDirection()
    {
        var (hdg, dir) = RunTakeoff(new FlyHeadingDeparture(new MagneticHeading(270), null));
        Assert.Equal(270, hdg);
        Assert.Null(dir);
    }

    [Fact]
    public void FlyHeading_RightTurn()
    {
        var (hdg, dir) = RunTakeoff(new FlyHeadingDeparture(new MagneticHeading(090), TurnDirection.Right));
        Assert.Equal(90, hdg);
        Assert.Equal(TurnDirection.Right, dir);
    }

    [Fact]
    public void FlyHeading_LeftTurn()
    {
        var (hdg, dir) = RunTakeoff(new FlyHeadingDeparture(new MagneticHeading(180), TurnDirection.Left));
        Assert.Equal(180, hdg);
        Assert.Equal(TurnDirection.Left, dir);
    }

    [Fact]
    public void OnCourseDeparture_KeepsRunwayHeading()
    {
        var (hdg, dir) = RunTakeoff(new OnCourseDeparture());
        Assert.Equal(RunwayHeading, hdg);
        Assert.Null(dir);
    }

    [Fact]
    public void DirectFixDeparture_KeepsRunwayHeading()
    {
        var (hdg, dir) = RunTakeoff(new DirectFixDeparture("SUNOL", 37.5, -121.8, null));
        Assert.Equal(RunwayHeading, hdg);
        Assert.Null(dir);
    }

    [Fact]
    public void ClosedTrafficDeparture_KeepsRunwayHeading()
    {
        var (hdg, dir) = RunTakeoff(new ClosedTrafficDeparture(PatternDirection.Right, null, null));
        Assert.Equal(RunwayHeading, hdg);
        Assert.Null(dir);
    }

    [Fact]
    public void RelativeTurnRight_Runway360()
    {
        // Runway 360 + 90 right = 090
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(90, TurnDirection.Right), 360);
        Assert.Equal(90, hdg);
        Assert.Equal(TurnDirection.Right, dir);
    }

    [Fact]
    public void RelativeTurnLeft_Runway010()
    {
        // Runway 010 - 90 left = 280
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(90, TurnDirection.Left), 10);
        Assert.Equal(280, hdg);
        Assert.Equal(TurnDirection.Left, dir);
    }

    // --- Heading wrapping edge cases ---

    [Fact]
    public void RelativeTurnRight_Runway350_Wraps()
    {
        // Runway 350 + 90 right = 440 → 80
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(90, TurnDirection.Right), 350);
        Assert.Equal(80, hdg);
        Assert.Equal(TurnDirection.Right, dir);
    }

    [Fact]
    public void RelativeTurnLeft_Runway020_Wraps()
    {
        // Runway 020 - 90 left = -70 → 290
        var (hdg, dir) = RunTakeoff(new RelativeTurnDeparture(90, TurnDirection.Left), 20);
        Assert.Equal(290, hdg);
        Assert.Equal(TurnDirection.Left, dir);
    }

    // --- Command acceptance during takeoff phases ---

    [Fact]
    public void TakeoffPhase_DuringGroundRoll_RejectsFlyHeading()
    {
        var phase = new TakeoffPhase();
        // Before airborne, most commands are rejected
        Assert.Equal(CommandAcceptance.Rejected, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    [Fact]
    public void TakeoffPhase_DuringGroundRoll_AllowsCancelTakeoff()
    {
        var phase = new TakeoffPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.CancelTakeoffClearance));
    }

    [Fact]
    public void TakeoffPhase_DuringGroundRoll_DeleteClearsPhase()
    {
        var phase = new TakeoffPhase();
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.Delete));
    }

    // --- Bug fix: TRDCT turn direction must survive into InitialClimbPhase ---

    [Fact]
    public void DirectFixDeparture_RightTurn_AircraftTurnsRightThroughPhysicsTicks()
    {
        // CTO TRDCT OAK30NUM — aircraft on RWY 28R (heading 280) should turn RIGHT
        // to reach a fix at bearing ~200. Shortest-path would go left (280→200 = -80°),
        // but TRDCT means turn right (280→360→200 = +280°).
        double runwayHdg = 280;
        var runway = TestRunwayFactory.Make(designator: "28R", airportId: "KOAK", heading: runwayHdg, elevationFt: FieldElevation);
        // Fix positioned so bearing from threshold is ~200° (south-southwest)
        double fixLat = 37.6;
        double fixLon = -122.3;

        var departure = new DirectFixDeparture("OAK30NUM", fixLat, fixLon, TurnDirection.Right);
        var departureRoute = new List<NavigationTarget>
        {
            new()
            {
                Name = "OAK30NUM",
                Latitude = fixLat,
                Longitude = fixLon,
            },
        };

        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = FieldElevation + 500, // already airborne, past takeoff phase
            IndicatedAirspeed = 90,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        // Start InitialClimbPhase with departure route
        var climbPhase = new InitialClimbPhase { Departure = departure, DepartureRoute = departureRoute };
        climbPhase.OnStart(ctx);

        // Verify direction was set
        Assert.Equal(TurnDirection.Right, targets.PreferredTurnDirection);

        // Run FlightPhysics ticks and verify the aircraft turns RIGHT (heading increases
        // from 280 through 300, 350, 0, etc.) rather than LEFT (280→260→240→200).
        double prevHeading = aircraft.TrueHeading.Degrees;
        bool turnedRight = false;
        bool turnedLeft = false;

        for (int tick = 0; tick < 120; tick++)
        {
            FlightPhysics.Update(aircraft, 1.0, null, null);

            double curHeading = aircraft.TrueHeading.Degrees;

            // Detect turn direction: if heading went from 280→290, that's right.
            // If heading went from 280→270, that's left.
            // Use signed angle difference to handle wrap-around.
            double delta = NormalizeAngle(curHeading - prevHeading);
            if (delta > 0.5)
            {
                turnedRight = true;
            }

            if (delta < -0.5)
            {
                turnedLeft = true;
            }

            prevHeading = curHeading;
        }

        Assert.True(turnedRight, "Aircraft should have turned right at some point");
        Assert.False(turnedLeft, "Aircraft should NOT have turned left — TRDCT means right turn");
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180)
        {
            angle -= 360;
        }

        while (angle < -180)
        {
            angle += 360;
        }

        return angle;
    }

    // --- Bug fix: CM/DM accepted during takeoff ---

    [Fact]
    public void TakeoffPhase_DuringGroundRoll_AllowsClimbMaintain()
    {
        var phase = new TakeoffPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
    }

    [Fact]
    public void TakeoffPhase_DuringGroundRoll_AllowsDescendMaintain()
    {
        var phase = new TakeoffPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.DescendMaintain));
    }

    [Fact]
    public void TakeoffPhase_Airborne_AllowsClimbMaintain()
    {
        // Need to get the phase to airborne state
        var runway = MakeRunway();
        var phase = new TakeoffPhase();
        phase.SetAssignedDeparture(new DefaultDeparture());

        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(RunwayHeading),
            Altitude = FieldElevation,
            Phases = phaseList,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        // Tick until airborne
        for (int i = 0; i < 300; i++)
        {
            phase.OnTick(ctx);
            if (!aircraft.IsOnGround)
            {
                break;
            }
        }

        Assert.False(aircraft.IsOnGround, "Aircraft should be airborne");
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
    }

    // --- Bug fix: CM/DM accepted during line-up phases ---

    [Fact]
    public void LineUpPhase_AllowsClimbMaintain()
    {
        var phase = new LineUpPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
    }

    [Fact]
    public void LineUpPhase_AllowsDescendMaintain()
    {
        var phase = new LineUpPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.DescendMaintain));
    }

    [Fact]
    public void LinedUpAndWaitingPhase_AllowsClimbMaintain()
    {
        var phase = new LinedUpAndWaitingPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
    }

    [Fact]
    public void LinedUpAndWaitingPhase_AllowsDescendMaintain()
    {
        var phase = new LinedUpAndWaitingPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.DescendMaintain));
    }

    // --- VFR departure turn deferral (issue #130) ---

    /// <summary>
    /// VFR aircraft with a heading departure should NOT have the heading applied
    /// during TakeoffPhase — it should keep runway heading until InitialClimbPhase
    /// applies it after DER + altitude conditions are met.
    /// </summary>
    [Fact]
    public void VFR_FlyHeading_KeepsRunwayHeadingAtLiftoff()
    {
        var runway = MakeRunway();
        var phase = new TakeoffPhase();
        phase.SetAssignedDeparture(new FlyHeadingDeparture(new MagneticHeading(060), null));

        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(RunwayHeading),
            Altitude = FieldElevation,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        // Tick until airborne
        for (int i = 0; i < 300; i++)
        {
            if (phase.OnTick(ctx))
            {
                break;
            }

            if (!aircraft.IsOnGround)
            {
                aircraft.Altitude += 50;
            }
        }

        // VFR: heading should still be runway heading, not 060
        Assert.Equal(RunwayHeading, targets.TargetTrueHeading!.Value.Degrees);
    }

    /// <summary>
    /// IFR aircraft with a heading departure should still turn immediately at liftoff
    /// (regression guard for issue #130 fix).
    /// </summary>
    [Fact]
    public void IFR_FlyHeading_StillTurnsAtLiftoff()
    {
        // Existing RunTakeoff creates IFR aircraft (FlightRules defaults to "IFR")
        var (hdg, _) = RunTakeoff(new FlyHeadingDeparture(new MagneticHeading(060), null));
        Assert.Equal(60, hdg);
    }

    /// <summary>
    /// VFR InitialClimbPhase with FlyHeadingDeparture should defer heading application
    /// until the aircraft is past the DER by 0.5nm AND within 300ft of pattern altitude.
    /// </summary>
    [Fact]
    public void VFR_InitialClimb_FlyHeading_DelaysUntilConditions()
    {
        double runwayHdg = 280;
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            heading: runwayHdg,
            elevationFt: FieldElevation,
            thresholdLat: 37.0,
            thresholdLon: -122.0,
            endLat: 37.01,
            endLon: -122.01
        );

        var departure = new FlyHeadingDeparture(new MagneticHeading(060), null);
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = FieldElevation + 400, // post-takeoff
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        var climbPhase = new InitialClimbPhase
        {
            Departure = departure,
            IsVfr = true,
            CruiseAltitude = 4500,
        };
        climbPhase.OnStart(ctx);

        // At 400ft AGL, heading should still be runway heading (not 060)
        // Pattern alt for piston = 1000ft AGL, turn threshold = 700ft AGL
        // TargetTrueHeading may be null (never set) or runway heading — either way, not 060
        Assert.True(
            (targets.TargetTrueHeading is null) || (targets.TargetTrueHeading.Value.Degrees == runwayHdg),
            $"Expected runway heading or null, got {targets.TargetTrueHeading?.Degrees}"
        );

        // Move aircraft to 700ft AGL AND past the DER
        aircraft.Altitude = FieldElevation + 700;
        // Project aircraft just past the DER along runway heading
        var pastDer = GeoMath.ProjectPoint(runway.EndLatitude, runway.EndLongitude, new TrueHeading(runwayHdg), 0.1);
        aircraft.Latitude = pastDer.Lat;
        aircraft.Longitude = pastDer.Lon;

        climbPhase.OnTick(ctx);

        // Now heading should be applied
        Assert.Equal(60, targets.TargetTrueHeading!.Value.Degrees);
    }

    /// <summary>
    /// VFR InitialClimbPhase requires BOTH conditions (altitude + DER) to trigger turn.
    /// Altitude alone or DER alone should not trigger.
    /// </summary>
    [Fact]
    public void VFR_InitialClimb_RequiresBothConditions()
    {
        double runwayHdg = 280;
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            heading: runwayHdg,
            elevationFt: FieldElevation,
            thresholdLat: 37.0,
            thresholdLon: -122.0,
            endLat: 37.01,
            endLon: -122.01
        );

        var departure = new FlyHeadingDeparture(new MagneticHeading(060), null);
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = FieldElevation + 400,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        var climbPhase = new InitialClimbPhase
        {
            Departure = departure,
            IsVfr = true,
            CruiseAltitude = 4500,
        };
        climbPhase.OnStart(ctx);

        // Case 1: altitude reached but still near threshold (not past DER)
        aircraft.Altitude = FieldElevation + 700;
        climbPhase.OnTick(ctx);
        Assert.True(
            (targets.TargetTrueHeading is null) || (targets.TargetTrueHeading.Value.Degrees == runwayHdg),
            $"Expected runway heading or null, got {targets.TargetTrueHeading?.Degrees}"
        );

        // Case 2: past DER but altitude too low
        aircraft.Altitude = FieldElevation + 400;
        var pastDer = GeoMath.ProjectPoint(runway.EndLatitude, runway.EndLongitude, new TrueHeading(runwayHdg), 0.6);
        aircraft.Latitude = pastDer.Lat;
        aircraft.Longitude = pastDer.Lon;
        climbPhase.OnTick(ctx);
        Assert.True(
            (targets.TargetTrueHeading is null) || (targets.TargetTrueHeading.Value.Degrees == runwayHdg),
            $"Expected runway heading or null, got {targets.TargetTrueHeading?.Degrees}"
        );
    }

    /// <summary>
    /// VFR DirectFixDeparture should not load navigation route until DER + altitude conditions.
    /// </summary>
    [Fact]
    public void VFR_InitialClimb_DirectFix_DelaysNavRoute()
    {
        double runwayHdg = 280;
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            heading: runwayHdg,
            elevationFt: FieldElevation,
            thresholdLat: 37.0,
            thresholdLon: -122.0,
            endLat: 37.01,
            endLon: -122.01
        );

        double fixLat = 37.6;
        double fixLon = -122.3;
        var departure = new DirectFixDeparture("OAK30NUM", fixLat, fixLon, null);
        var departureRoute = new List<NavigationTarget>
        {
            new()
            {
                Name = "OAK30NUM",
                Latitude = fixLat,
                Longitude = fixLon,
            },
        };

        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = FieldElevation + 400,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        var climbPhase = new InitialClimbPhase
        {
            Departure = departure,
            DepartureRoute = departureRoute,
            IsVfr = true,
            CruiseAltitude = 4500,
        };
        climbPhase.OnStart(ctx);

        // Nav route should NOT be loaded yet
        Assert.Empty(targets.NavigationRoute);

        // Move past DER + altitude
        aircraft.Altitude = FieldElevation + 700;
        var pastDer = GeoMath.ProjectPoint(runway.EndLatitude, runway.EndLongitude, new TrueHeading(runwayHdg), 0.6);
        aircraft.Latitude = pastDer.Lat;
        aircraft.Longitude = pastDer.Lon;
        climbPhase.OnTick(ctx);

        // Nav route should now be loaded
        Assert.NotEmpty(targets.NavigationRoute);
        Assert.Equal("OAK30NUM", targets.NavigationRoute[0].Name);
    }

    /// <summary>
    /// VFR OnCourseDeparture should defer navigation route loading until conditions met.
    /// </summary>
    [Fact]
    public void VFR_InitialClimb_OnCourse_DelaysNavRoute()
    {
        double runwayHdg = 280;
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            heading: runwayHdg,
            elevationFt: FieldElevation,
            thresholdLat: 37.0,
            thresholdLon: -122.0,
            endLat: 37.01,
            endLon: -122.01
        );

        var departure = new OnCourseDeparture();
        var departureRoute = new List<NavigationTarget>
        {
            new()
            {
                Name = "SNS",
                Latitude = 36.66,
                Longitude = -121.6,
            },
        };

        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = FieldElevation + 400,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        var climbPhase = new InitialClimbPhase
        {
            Departure = departure,
            DepartureRoute = departureRoute,
            IsVfr = true,
            CruiseAltitude = 4500,
        };
        climbPhase.OnStart(ctx);

        // Nav route should NOT be loaded yet
        Assert.Empty(targets.NavigationRoute);

        // Move past DER + altitude
        aircraft.Altitude = FieldElevation + 700;
        var pastDer = GeoMath.ProjectPoint(runway.EndLatitude, runway.EndLongitude, new TrueHeading(runwayHdg), 0.6);
        aircraft.Latitude = pastDer.Lat;
        aircraft.Longitude = pastDer.Lon;
        climbPhase.OnTick(ctx);

        // Nav route should now be loaded
        Assert.NotEmpty(targets.NavigationRoute);
        Assert.Equal("SNS", targets.NavigationRoute[0].Name);
    }

    /// <summary>
    /// When runway info is not available, VFR nav route should be loaded immediately
    /// (graceful degradation — can't check DER position).
    /// </summary>
    [Fact]
    public void VFR_NoRunway_AppliesImmediately()
    {
        var departure = new DirectFixDeparture("SUNOL", 37.5, -121.8, null);
        var departureRoute = new List<NavigationTarget>
        {
            new()
            {
                Name = "SUNOL",
                Latitude = 37.5,
                Longitude = -121.8,
            },
        };
        var phaseList = new PhaseList();
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(280),
            Altitude = 400,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = null, // no runway info
            FieldElevation = 0,
            Logger = NullLogger.Instance,
        };

        var climbPhase = new InitialClimbPhase
        {
            Departure = departure,
            DepartureRoute = departureRoute,
            IsVfr = true,
            CruiseAltitude = 4500,
        };
        climbPhase.OnStart(ctx);

        // Without runway, nav route should be loaded immediately (no DER to check)
        Assert.NotEmpty(targets.NavigationRoute);
        Assert.Equal("SUNOL", targets.NavigationRoute[0].Name);
    }

    // --- UpwindPhase altitude gate (issue #130) ---

    /// <summary>
    /// UpwindPhase should not complete (trigger crosswind turn) if the aircraft
    /// is below 300ft of pattern altitude, even if position-wise it has reached
    /// the crosswind turn point.
    /// </summary>
    [Fact]
    public void UpwindPhase_DoesNotCompleteBelow300OfPatternAlt()
    {
        double runwayHdg = 280;
        double patternAlt = 1000; // MSL (field elev 0 + 1000 AGL)
        var runway = TestRunwayFactory.Make(designator: "28", heading: runwayHdg, elevationFt: 0);

        // Create waypoints with the crosswind turn point at the DER
        var crosswindTurn = GeoMath.ProjectPoint(runway.EndLatitude, runway.EndLongitude, new TrueHeading(runwayHdg), 0.3);
        var waypoints = new PatternWaypoints
        {
            DepartureEndLat = runway.EndLatitude,
            DepartureEndLon = runway.EndLongitude,
            CrosswindTurnLat = crosswindTurn.Lat,
            CrosswindTurnLon = crosswindTurn.Lon,
            DownwindStartLat = crosswindTurn.Lat,
            DownwindStartLon = crosswindTurn.Lon,
            DownwindAbeamLat = runway.ThresholdLatitude,
            DownwindAbeamLon = runway.ThresholdLongitude,
            BaseTurnLat = runway.ThresholdLatitude,
            BaseTurnLon = runway.ThresholdLongitude,
            ThresholdLat = runway.ThresholdLatitude,
            ThresholdLon = runway.ThresholdLongitude,
            UpwindHeading = new TrueHeading(runwayHdg),
            CrosswindHeading = new TrueHeading((runwayHdg + 90) % 360),
            DownwindHeading = new TrueHeading((runwayHdg + 180) % 360),
            BaseHeading = new TrueHeading((runwayHdg + 270) % 360),
            FinalHeading = new TrueHeading(runwayHdg),
            PatternAltitude = patternAlt,
            Direction = PatternDirection.Left,
        };

        var upwindPhase = new UpwindPhase { Waypoints = waypoints };
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            // Position AT the crosswind turn point
            Latitude = crosswindTurn.Lat,
            Longitude = crosswindTurn.Lon,
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = 500, // 500ft MSL — below threshold of 700ft (1000 - 300)
            Phases = phaseList,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = 0,
            Logger = NullLogger.Instance,
        };

        upwindPhase.OnStart(ctx);
        bool complete = upwindPhase.OnTick(ctx);

        // Should NOT complete — altitude too low
        Assert.False(complete, "UpwindPhase should not complete below 300ft of pattern altitude");
    }

    /// <summary>
    /// UpwindPhase should complete when both position and altitude conditions are met.
    /// </summary>
    [Fact]
    public void UpwindPhase_CompletesWhenBothPositionAndAltitudeMet()
    {
        double runwayHdg = 280;
        double patternAlt = 1000;
        var runway = TestRunwayFactory.Make(designator: "28", heading: runwayHdg, elevationFt: 0);

        var crosswindTurn = GeoMath.ProjectPoint(runway.EndLatitude, runway.EndLongitude, new TrueHeading(runwayHdg), 0.3);
        var waypoints = new PatternWaypoints
        {
            DepartureEndLat = runway.EndLatitude,
            DepartureEndLon = runway.EndLongitude,
            CrosswindTurnLat = crosswindTurn.Lat,
            CrosswindTurnLon = crosswindTurn.Lon,
            DownwindStartLat = crosswindTurn.Lat,
            DownwindStartLon = crosswindTurn.Lon,
            DownwindAbeamLat = runway.ThresholdLatitude,
            DownwindAbeamLon = runway.ThresholdLongitude,
            BaseTurnLat = runway.ThresholdLatitude,
            BaseTurnLon = runway.ThresholdLongitude,
            ThresholdLat = runway.ThresholdLatitude,
            ThresholdLon = runway.ThresholdLongitude,
            UpwindHeading = new TrueHeading(runwayHdg),
            CrosswindHeading = new TrueHeading((runwayHdg + 90) % 360),
            DownwindHeading = new TrueHeading((runwayHdg + 180) % 360),
            BaseHeading = new TrueHeading((runwayHdg + 270) % 360),
            FinalHeading = new TrueHeading(runwayHdg),
            PatternAltitude = patternAlt,
            Direction = PatternDirection.Left,
        };

        var upwindPhase = new UpwindPhase { Waypoints = waypoints };
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            FlightRules = "VFR",
            Latitude = crosswindTurn.Lat,
            Longitude = crosswindTurn.Lon,
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = 750, // 750ft MSL — above threshold of 700ft (1000 - 300)
            Phases = phaseList,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = 0,
            Logger = NullLogger.Instance,
        };

        upwindPhase.OnStart(ctx);
        bool complete = upwindPhase.OnTick(ctx);

        // Should complete — both conditions met
        Assert.True(complete, "UpwindPhase should complete when at crosswind turn point and above altitude threshold");
    }

    [Fact]
    public void InitialClimbPhase_UsesTargetsAssignedAltitude_WhenSetDuringTakeoff()
    {
        // Simulates CM 014 issued during takeoff: Targets.AssignedAltitude = 1400
        var runway = MakeRunway();
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            TrueHeading = new TrueHeading(RunwayHeading),
            Altitude = FieldElevation + 400, // post-takeoff
            Phases = phaseList,
        };
        var targets = aircraft.Targets;

        // CM 014 was applied during takeoff — sets AssignedAltitude on targets
        targets.AssignedAltitude = 1400;

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        // InitialClimbPhase with NO explicit AssignedAltitude init property
        var climbPhase = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            IsVfr = true,
            CruiseAltitude = 4500,
        };
        climbPhase.OnStart(ctx);

        // Should use targets.AssignedAltitude (1400) not VFR cruise (4500)
        Assert.Equal(1400, targets.TargetAltitude);
    }
}
