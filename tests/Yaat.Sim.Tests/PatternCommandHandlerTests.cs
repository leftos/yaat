using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class PatternCommandHandlerTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraft(
        double lat = 37.0,
        double lon = -122.0,
        double altitude = 1100,
        double groundSpeed = 200,
        bool onGround = false
    )
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = 280,
            Altitude = altitude,
            GroundSpeed = groundSpeed,
            IndicatedAirspeed = groundSpeed,
            IsOnGround = onGround,
            Departure = "TEST",
        };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        return ac;
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — wrong-side detection
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_WrongSideDownwind_InsertsMidfieldCrossing()
    {
        var rwy = DefaultRunway();
        // Left pattern: crosswind heading = 280-90 = 190. Pattern is south of runway.
        // Put aircraft NORTH of threshold (wrong side for left pattern).
        var (northLat, northLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 10.0, 2.0);
        var ac = MakeAircraft(lat: northLat, lon: northLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind);

        Assert.True(result.Success);
        Assert.Contains("crossing midfield", result.Message!);
        // Should have a MidfieldCrossingPhase in the phase list
        Assert.Contains(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    [Fact]
    public void TryEnterPattern_CorrectSide_NoMidfieldCrossing()
    {
        var rwy = DefaultRunway();
        // Left pattern south of runway. Put aircraft south.
        var (southLat, southLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 190.0, 2.0);
        var ac = MakeAircraft(lat: southLat, lon: southLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind);

        Assert.True(result.Success);
        Assert.DoesNotContain("crossing midfield", result.Message!);
        Assert.DoesNotContain(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    [Fact]
    public void TryEnterPattern_WrongSideBase_InsertsMidfieldCrossing()
    {
        var rwy = DefaultRunway();
        var (northLat, northLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 10.0, 2.0);
        var ac = MakeAircraft(lat: northLat, lon: northLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Base);

        Assert.True(result.Success);
        Assert.Contains(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — PatternEntryPhase for far aircraft
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_FarAircraft_InsertsPatternEntryPhase()
    {
        var rwy = DefaultRunway();
        // Place aircraft far south (correct side for left pattern, but >1nm from entry point)
        var (farLat, farLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 190.0, 10.0);
        var ac = MakeAircraft(lat: farLat, lon: farLon, altitude: 3000);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind);

        Assert.True(result.Success);
        Assert.Contains(ac.Phases!.Phases, p => p is PatternEntryPhase);
    }

    [Fact]
    public void TryEnterPattern_CloseAircraft_SkipsPatternEntryPhase()
    {
        var rwy = DefaultRunway();
        // Place aircraft very close to the downwind abeam point
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left);
        var ac = MakeAircraft(lat: wp.DownwindAbeamLat, lon: wp.DownwindAbeamLon, altitude: wp.PatternAltitude);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind);

        Assert.True(result.Success);
        Assert.DoesNotContain(ac.Phases!.Phases, p => p is PatternEntryPhase);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — no assigned runway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_NoRunway_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList(); // no runway

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind);

        Assert.False(result.Success);
        Assert.Contains("No assigned runway", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryExtendPattern
    // -------------------------------------------------------------------------

    [Fact]
    public void TryExtendPattern_OnDownwind_SetsExtended()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(downwind);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.True(result.Success);
        Assert.True(downwind.IsExtended);
    }

    [Fact]
    public void TryExtendPattern_NotOnDownwind_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new UpwindPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.False(result.Success);
        Assert.Contains("downwind only", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryPatternTurnBase
    // -------------------------------------------------------------------------

    [Fact]
    public void TryPatternTurnBase_OnDownwind_AdvancesToBase()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var downwind = new DownwindPhase { Waypoints = wp };
        var basep = new BasePhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(downwind);
        ac.Phases.Add(basep);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryPatternTurnBase(ac);

        Assert.True(result.Success);
        Assert.Equal(basep, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void TryPatternTurnBase_NotOnDownwind_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new UpwindPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryPatternTurnBase(ac);

        Assert.False(result.Success);
        Assert.Contains("Not on downwind", result.Message!);
    }

    // -------------------------------------------------------------------------
    // Pattern direction inference
    // -------------------------------------------------------------------------

    [Fact]
    public void TryChangePatternDirection_SetsTrafficDirection()
    {
        var ac = MakeAircraft();

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Right);

        Assert.True(result.Success);
        Assert.Equal(PatternDirection.Right, ac.Phases!.TrafficDirection);
        Assert.Contains("right traffic", result.Message!);
    }

    [Fact]
    public void TryChangePatternDirection_NoRunway_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList(); // no runway

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left);

        Assert.False(result.Success);
        Assert.Contains("No assigned runway", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryMakeTurn — 360 resumes same leg
    // -------------------------------------------------------------------------

    [Fact]
    public void TryMakeTurn_360OnDownwind_ResumesSameLeg()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var downwind = new DownwindPhase { Waypoints = wp };
        var basep = new BasePhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(downwind);
        ac.Phases.Add(basep);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryMakeTurn(ac, TurnDirection.Left, 360);

        Assert.True(result.Success);
        // Current phase should be the MakeTurnPhase
        Assert.IsType<MakeTurnPhase>(ac.Phases.CurrentPhase);
        // After the turn phase, there should be a fresh DownwindPhase, then BasePhase
        var remaining = ac.Phases.Phases.Skip(ac.Phases.CurrentIndex + 1).ToList();
        Assert.IsType<DownwindPhase>(remaining[0]);
        Assert.IsType<BasePhase>(remaining[1]);
    }

    [Fact]
    public void TryMakeTurn_270OnDownwind_DoesNotClonePhase()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var downwind = new DownwindPhase { Waypoints = wp };
        var basep = new BasePhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(downwind);
        ac.Phases.Add(basep);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryMakeTurn(ac, TurnDirection.Left, 270);

        Assert.True(result.Success);
        Assert.IsType<MakeTurnPhase>(ac.Phases.CurrentPhase);
        // After the turn, next phase should be BasePhase (not another DownwindPhase)
        var remaining = ac.Phases.Phases.Skip(ac.Phases.CurrentIndex + 1).ToList();
        Assert.IsType<BasePhase>(remaining[0]);
    }

    // -------------------------------------------------------------------------
    // TryPlan270
    // -------------------------------------------------------------------------

    [Fact]
    public void TryPlan270_OnDownwind_InsertsTurnBeforeBase()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var downwind = new DownwindPhase { Waypoints = wp };
        var basep = new BasePhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(downwind);
        ac.Phases.Add(basep);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryPlan270(ac);

        Assert.True(result.Success);
        Assert.Contains("Plan left 270", result.Message!);
        // Current phase is still DownwindPhase (not advanced)
        Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);
        // Next phase should be MakeTurnPhase(270), then BasePhase
        var remaining = ac.Phases.Phases.Skip(ac.Phases.CurrentIndex + 1).ToList();
        Assert.IsType<MakeTurnPhase>(remaining[0]);
        Assert.Equal(270.0, ((MakeTurnPhase)remaining[0]).TargetDegrees);
        Assert.IsType<BasePhase>(remaining[1]);
    }

    [Fact]
    public void TryPlan270_RightTraffic_UsesRightDirection()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Right);
        var downwind = new DownwindPhase { Waypoints = wp };
        var basep = new BasePhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Right };
        ac.Phases.Add(downwind);
        ac.Phases.Add(basep);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryPlan270(ac);

        Assert.True(result.Success);
        var turnPhase = (MakeTurnPhase)ac.Phases.Phases[ac.Phases.CurrentIndex + 1];
        Assert.Equal(TurnDirection.Right, turnPhase.Direction);
    }

    [Fact]
    public void TryPlan270_NotOnPatternLeg_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryPlan270(ac);

        Assert.False(result.Success);
        Assert.Contains("active pattern leg", result.Message!);
    }

    [Fact]
    public void TryPlan270_NoTrafficDirection_Fails()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryPlan270(ac);

        Assert.False(result.Success);
        Assert.Contains("active traffic pattern", result.Message!);
    }

    [Fact]
    public void TryPlan270_AlreadyPlanned_Fails()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        PatternCommandHandler.TryPlan270(ac);
        var result = PatternCommandHandler.TryPlan270(ac);

        Assert.False(result.Success);
        Assert.Contains("already planned", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryCancel270
    // -------------------------------------------------------------------------

    [Fact]
    public void TryCancel270_InProgress270_Fails_UseOtherCommand()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        // Start a 270 (advances into MakeTurnPhase)
        PatternCommandHandler.TryMakeTurn(ac, TurnDirection.Left, 270);
        Assert.IsType<MakeTurnPhase>(ac.Phases.CurrentPhase);

        // NO270 does NOT cancel in-progress turns — use FH/FPH/TB instead
        var result = PatternCommandHandler.TryCancel270(ac);
        Assert.False(result.Success);
    }

    [Fact]
    public void TryCancel270_Planned_RemovesPending()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        PatternCommandHandler.TryPlan270(ac);
        Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);

        var result = PatternCommandHandler.TryCancel270(ac);

        Assert.True(result.Success);
        Assert.Contains("Cancel planned 270", result.Message!);
        // MakeTurnPhase should be removed; next phase after downwind is BasePhase
        var remaining = ac.Phases.Phases.Skip(ac.Phases.CurrentIndex + 1).ToList();
        Assert.IsType<BasePhase>(remaining[0]);
    }

    [Fact]
    public void TryCancel270_NothingToCancel_Fails()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryCancel270(ac);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TrySetPatternSize
    // -------------------------------------------------------------------------

    [Fact]
    public void TrySetPatternSize_ValidSize_SetsOverride()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TrySetPatternSize(ac, 2.5);

        Assert.True(result.Success);
        Assert.Equal(2.5, ac.PatternSizeOverrideNm);
    }

    [Fact]
    public void TrySetPatternSize_TooSmall_Fails()
    {
        var ac = MakeAircraft();

        var result = PatternCommandHandler.TrySetPatternSize(ac, 0.1);

        Assert.False(result.Success);
        Assert.Contains("between", result.Message!);
    }

    [Fact]
    public void TrySetPatternSize_TooLarge_Fails()
    {
        var ac = MakeAircraft();

        var result = PatternCommandHandler.TrySetPatternSize(ac, 15.0);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryMakeNormalApproach
    // -------------------------------------------------------------------------

    [Fact]
    public void TryMakeNormalApproach_OnBase_ResetsFinalDistance()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var basep = new BasePhase { Waypoints = wp, FinalDistanceNm = 0.5 };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(basep);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryMakeNormalApproach(ac);

        Assert.True(result.Success);
        Assert.Null(basep.FinalDistanceNm);
    }

    [Fact]
    public void TryMakeNormalApproach_OnDownwind_Succeeds()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryMakeNormalApproach(ac);

        Assert.True(result.Success);
    }

    [Fact]
    public void TryMakeNormalApproach_NotOnPatternLeg_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryMakeNormalApproach(ac);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryMakeSTurns
    // -------------------------------------------------------------------------

    [Fact]
    public void TryMakeSTurns_InsertsAndAdvancesToSTurnPhase()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryMakeSTurns(ac, TurnDirection.Left, 3);

        Assert.True(result.Success);
        Assert.IsType<STurnPhase>(ac.Phases.CurrentPhase);
        var sturn = (STurnPhase)ac.Phases.CurrentPhase;
        Assert.Equal(TurnDirection.Left, sturn.InitialDirection);
        Assert.Equal(3, sturn.Count);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — crosswind entry
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_Crosswind_BuildsCrosswindSequence()
    {
        var ac = MakeAircraft();
        var rwy = DefaultRunway();
        // Place aircraft near the crosswind turn point
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left);
        ac = MakeAircraft(lat: wp.CrosswindTurnLat, lon: wp.CrosswindTurnLon, altitude: wp.PatternAltitude);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Crosswind);

        Assert.True(result.Success);
        // Should have CrosswindPhase → DownwindPhase → BasePhase → FinalApproachPhase → landing
        var phases = ac.Phases!.Phases;
        Assert.Contains(phases, p => p is CrosswindPhase);
        Assert.Contains(phases, p => p is DownwindPhase);
        Assert.Contains(phases, p => p is BasePhase);
    }
}
