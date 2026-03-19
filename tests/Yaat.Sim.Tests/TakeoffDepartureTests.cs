using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
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
        var (hdg, dir) = RunTakeoff(new ClosedTrafficDeparture(PatternDirection.Right));
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
