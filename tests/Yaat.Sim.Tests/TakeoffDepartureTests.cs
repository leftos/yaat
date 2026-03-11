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
            Heading = runwayHeading,
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

        return (targets.TargetHeading, targets.PreferredTurnDirection);
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
        var (hdg, dir) = RunTakeoff(new FlyHeadingDeparture(270, null));
        Assert.Equal(270, hdg);
        Assert.Null(dir);
    }

    [Fact]
    public void FlyHeading_RightTurn()
    {
        var (hdg, dir) = RunTakeoff(new FlyHeadingDeparture(090, TurnDirection.Right));
        Assert.Equal(90, hdg);
        Assert.Equal(TurnDirection.Right, dir);
    }

    [Fact]
    public void FlyHeading_LeftTurn()
    {
        var (hdg, dir) = RunTakeoff(new FlyHeadingDeparture(180, TurnDirection.Left));
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
        var (hdg, dir) = RunTakeoff(new DirectFixDeparture("SUNOL", 37.5, -121.8));
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
}
