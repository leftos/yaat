using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

public class PatternCommandHandlerTests
{
    public PatternCommandHandlerTests()
    {
        TestVnasData.EnsureInitialized();
    }

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
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(280),
            Altitude = altitude,
            IndicatedAirspeed = groundSpeed,
            IsOnGround = onGround,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
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
        var (northLat, northLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, new TrueHeading(10.0), 2.0);
        var ac = MakeAircraft(lat: northLat, lon: northLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, null, null);

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
        var (southLat, southLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, new TrueHeading(190.0), 2.0);
        var ac = MakeAircraft(lat: southLat, lon: southLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, null, null);

        Assert.True(result.Success);
        Assert.DoesNotContain("crossing midfield", result.Message!);
        Assert.DoesNotContain(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    [Fact]
    public void TryEnterPattern_WrongSideBase_InsertsMidfieldCrossing()
    {
        var rwy = DefaultRunway();
        var (northLat, northLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, new TrueHeading(10.0), 2.0);
        var ac = MakeAircraft(lat: northLat, lon: northLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Base, null, null);

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
        var (farLat, farLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, new TrueHeading(190.0), 10.0);
        var ac = MakeAircraft(lat: farLat, lon: farLon, altitude: 3000);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, null, null);

        Assert.True(result.Success);
        Assert.Contains(ac.Phases!.Phases, p => p is PatternEntryPhase);
    }

    [Fact]
    public void TryEnterPattern_CloseAircraft_SkipsPatternEntryPhase()
    {
        var rwy = DefaultRunway();
        // Place aircraft very close to the downwind abeam point
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left, null, null, null);
        var ac = MakeAircraft(lat: wp.DownwindAbeamLat, lon: wp.DownwindAbeamLon, altitude: wp.PatternAltitude);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, null, null);

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

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, null, null);

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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(downwind);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.True(result.Success);
        Assert.True(downwind.IsExtended);
    }

    [Fact]
    public void TryExtendPattern_OnUpwind_SetsExtended()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
        var upwind = new UpwindPhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(upwind);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.True(result.Success);
        Assert.True(upwind.IsExtended);
    }

    [Fact]
    public void TryExtendPattern_OnCrosswind_SetsExtended()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
        var crosswind = new CrosswindPhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(crosswind);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.True(result.Success);
        Assert.True(crosswind.IsExtended);
    }

    [Fact]
    public void TryExtendPattern_OnBase_Fails()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.False(result.Success);
        Assert.Contains("base", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryExtendPattern_NotInPattern_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new InitialClimbPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryPatternTurnBase
    // -------------------------------------------------------------------------

    [Fact]
    public void TryPatternTurnBase_OnDownwind_AdvancesToBase()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Right, null, null);

        Assert.True(result.Success);
        Assert.Equal(PatternDirection.Right, ac.Phases!.TrafficDirection);
        Assert.Contains("right traffic", result.Message!);
    }

    [Fact]
    public void TryChangePatternDirection_NoRunway_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList(); // no runway

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left, null, null);

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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Right, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway(), TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = PatternCommandHandler.TrySetPatternSize(ac, 2.5);

        Assert.True(result.Success);
        Assert.Equal(2.5, ac.Pattern.SizeOverrideNm);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left, null, null, null);
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
        var rwy = DefaultRunway();
        // Place aircraft near the crosswind turn point
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left, null, null, null);
        var ac = MakeAircraft(lat: wp.CrosswindTurnLat, lon: wp.CrosswindTurnLon, altitude: wp.PatternAltitude);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Crosswind, null, null);

        Assert.True(result.Success);
        // Should have CrosswindPhase → DownwindPhase → BasePhase → FinalApproachPhase → landing
        var phases = ac.Phases!.Phases;
        Assert.Contains(phases, p => p is CrosswindPhase);
        Assert.Contains(phases, p => p is DownwindPhase);
        Assert.Contains(phases, p => p is BasePhase);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — short-final runway change rejection
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_RejectsRunwayChange_WhenOnShortFinal()
    {
        var rwy28L = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // Place aircraft 0.5nm from threshold on the extended centerline (short final)
        var (lat, lon) = GeoMath.ProjectPoint(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude, rwy28L.TrueHeading.ToReciprocal(), 0.5);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: (int)rwy28L.ElevationFt + 200);
        ac.AircraftType = "B738";
        ac.FlightPlan.Destination = "KOAK";

        // Set up PhaseList with active FinalApproachPhase for 28L
        var phases = new PhaseList { AssignedRunway = rwy28L };
        var finalPhase = new FinalApproachPhase();
        phases.Add(finalPhase);
        phases.Start(
            new PhaseContext
            {
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 1.0,
                Runway = rwy28L,
                FieldElevation = rwy28L.ElevationFt,
                Logger = NullLogger.Instance,
            }
        );
        ac.Phases = phases;

        // Request EF for 28R — must be rejected (committed to 28L). With the parallel-
        // runway sidestep path active, this case is now caught by the AGL gate (200 ft
        // AGL < the 500 ft sidestep floor) rather than by the loop check, so the
        // rejection message is "too low for sidestep" instead of "short final". Either
        // way, the assertion is the same: EF must not retarget below the stabilized-
        // approach floor.
        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28R", null);

        Assert.False(result.Success);
        Assert.Contains("too low", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryEnterPattern_AcceptsRunwayChange_WhenFarFromThreshold()
    {
        var rwy28L = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // Place aircraft 5nm from threshold (not short final)
        var (lat, lon) = GeoMath.ProjectPoint(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude, rwy28L.TrueHeading.ToReciprocal(), 5.0);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: 2000);
        ac.AircraftType = "B738";
        ac.FlightPlan.Destination = "KOAK";

        // Set up PhaseList with active FinalApproachPhase for 28L
        var phases = new PhaseList { AssignedRunway = rwy28L };
        var finalPhase = new FinalApproachPhase();
        phases.Add(finalPhase);
        phases.Start(
            new PhaseContext
            {
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 1.0,
                Runway = rwy28L,
                FieldElevation = rwy28L.ElevationFt,
                Logger = NullLogger.Instance,
            }
        );
        ac.Phases = phases;

        // Request EF for 28R — should be accepted (far enough from threshold)
        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28R", null);

        Assert.True(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — Final close-in aligned override
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_Final_CloseInAligned_UsesAircraftPositionAsEntry()
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // Place the aircraft 3.5 NM out on the FAC: inside the jet 4.7 NM standard
        // intercept, and well beyond the 2.0 NM minimum.
        var (lat, lon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 3.5);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: (int)rwy.ElevationFt + 1100);
        ac.AircraftType = "B738";
        ac.TrueHeading = rwy.TrueHeading;
        ac.FlightPlan.Destination = "KOAK";
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28L", null);

        Assert.True(result.Success);
        // useAircraftPositionAsEntry = true → entry distance ≈ 0 → no PatternEntryPhase queued.
        Assert.DoesNotContain(ac.Phases!.Phases, p => p is PatternEntryPhase);
        Assert.Contains(ac.Phases.Phases, p => p is FinalApproachPhase);
    }

    [Fact]
    public void TryEnterPattern_Final_OutsideStandardIntercept_UsesStandardEntry()
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // 6 NM out — well past the jet 4.7 NM standard intercept distance.
        var (lat, lon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 6.0);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: (int)rwy.ElevationFt + 1500);
        ac.AircraftType = "B738";
        ac.TrueHeading = rwy.TrueHeading;
        ac.FlightPlan.Destination = "KOAK";
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28L", null);

        Assert.True(result.Success);
        // Aircraft sits outside the close-in window, so the standard far entry path runs
        // and a PatternEntryPhase is queued (distToEntry > 1.0 NM).
        var entry = ac.Phases!.Phases.OfType<PatternEntryPhase>().FirstOrDefault();
        Assert.NotNull(entry);
        double entryToThresholdNm = GeoMath.DistanceNm(
            new LatLon(entry.EntryLat, entry.EntryLon),
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude)
        );
        // Standard intercept: PatternAltitudeAgl(Jet) / FeetPerNm(3°) ≈ 1500/318 ≈ 4.7 NM.
        Assert.InRange(entryToThresholdNm, 4.0, 5.5);
    }

    [Fact]
    public void TryEnterPattern_Final_CloseInButMisaligned_FallsThroughToLoopCheck()
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // 3.5 NM out (close-in zone for jets) but heading 35° off the runway — outside
        // the 30° angle-off envelope, so the close-in path is skipped and the standard
        // loop check runs. With this geometry, the loop check rejects ("short final").
        var (lat, lon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 3.5);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: (int)rwy.ElevationFt + 1100);
        ac.AircraftType = "B738";
        ac.TrueHeading = new TrueHeading(rwy.TrueHeading.Degrees - 35.0);
        ac.FlightPlan.Destination = "KOAK";
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28L", null);

        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryEnterPattern_Final_InsideMinimumDistance_FallsThroughToLoopCheck()
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // 1.5 NM out, jet — inside the 2.0 NM jet minimum perpendicular base/final.
        // The close-in alongTrack-min safety gate fails, so close-in is skipped
        // and the standard loop check runs. With this geometry (entry behind
        // aircraft, total turn ≈ 360°, IFR jet radius too large) the loop check
        // rejects "short final". The behavior under test is the fall-through —
        // close-in does not reject from inside its block; it defers to the loop
        // check (so an aircraft that *can* fly the standard teardrop still
        // gets a chance, instead of being rejected outright).
        var (lat, lon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 1.5);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: (int)rwy.ElevationFt + 500);
        ac.AircraftType = "B738";
        ac.TrueHeading = rwy.TrueHeading;
        ac.FlightPlan.Destination = "KOAK";
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28L", null);

        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryEnterPattern_Final_TooHighForCloseIn_FallsThroughToLoopCheck()
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // 3.5 NM out, aligned, but at 8000 ft — alongTrack ≥ 2.0 jet minimum
        // (alongTrack gate passes), but the descent the close-in path can absorb
        // at jet pattern descent rate over a 3.5 NM straight-in is far less
        // than 7991 ft, so the altitude-feasibility gate fails and we fall
        // through to the standard loop check (which rejects "short final").
        var (lat, lon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 3.5);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: 8000);
        ac.AircraftType = "B738";
        ac.TrueHeading = rwy.TrueHeading;
        ac.FlightPlan.Destination = "KOAK";
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28L", null);

        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryEnterPattern_Final_CloseInWithExplicitFinalDistance_SkipsCloseInPath()
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L not found");
        // 3.5 NM out, aligned, jet — same shape as the close-in success case, but
        // the controller supplies an explicit finalDistanceNm = 5.0. Close-in
        // detection's `finalDistanceNm is null` gate excludes this path; the standard
        // loop check runs against an entry 1.5 NM behind the aircraft and rejects.
        // The success-vs-reject delta vs the close-in-aligned test proves the gate.
        var (lat, lon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 3.5);
        var ac = MakeAircraft(lat: lat, lon: lon, altitude: (int)rwy.ElevationFt + 1100);
        ac.AircraftType = "B738";
        ac.TrueHeading = rwy.TrueHeading;
        ac.FlightPlan.Destination = "KOAK";
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28L", 5.0);

        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // TryClearedToLand — preconditions
    // -------------------------------------------------------------------------

    [Fact]
    public void TryClearedToLand_Airborne_WithRunway_Succeeds()
    {
        var ac = MakeAircraft(altitude: 1500, onGround: false);
        ac.Phases!.Add(new LandingPhase());

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand(), ac);

        Assert.True(result.Success);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Equal("28", ac.Phases.ClearedRunwayId);
    }

    [Fact]
    public void TryClearedToLand_NoPendingApproach_Fails()
    {
        // Silent-failure case: CLAND used to set the LandingClearance flag even
        // when there was no pending LandingPhase to replace. Aircraft would never
        // actually transition to LandingPhase.
        var ac = MakeAircraft(altitude: 1500, onGround: false);
        // No LandingPhase added — just an aircraft in cruise/enroute with a runway assigned.

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand(), ac);

        Assert.False(result.Success);
        Assert.Contains("no pending approach", result.Message!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Null(ac.Phases!.LandingClearance);
    }

    [Theory]
    [InlineData("TG")]
    [InlineData("SG")]
    [InlineData("LA")]
    [InlineData("COPT")]
    public void TrySetupOption_NoPendingApproach_Fails(string verb)
    {
        // Silent-failure case: TG/SG/LA/COPT used to set the LandingClearance
        // flag even when there was no pending approach phase. The aircraft would
        // never actually fly the option.
        var ac = MakeAircraft(altitude: 1500, onGround: false);

        var result = verb switch
        {
            "TG" => PatternCommandHandler.TrySetupTouchAndGo(ac, null),
            "SG" => PatternCommandHandler.TrySetupStopAndGo(ac, null),
            "LA" => PatternCommandHandler.TrySetupLowApproach(ac, null),
            "COPT" => PatternCommandHandler.TrySetupClearedForOption(ac, null),
            _ => throw new Xunit.Sdk.XunitException($"Unknown verb: {verb}"),
        };

        Assert.False(result.Success);
        Assert.Contains("no landing phase", result.Message!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Null(ac.Phases!.LandingClearance);
    }

    [Theory]
    [InlineData("TG", ClearanceType.ClearedTouchAndGo)]
    [InlineData("SG", ClearanceType.ClearedStopAndGo)]
    [InlineData("LA", ClearanceType.ClearedLowApproach)]
    [InlineData("COPT", ClearanceType.ClearedForOption)]
    public void TrySetupOption_PendingLandingPhase_Succeeds(string verb, ClearanceType expectedClearance)
    {
        var ac = MakeAircraft(altitude: 1500, onGround: false);
        ac.Phases!.Add(new LandingPhase());

        var result = verb switch
        {
            "TG" => PatternCommandHandler.TrySetupTouchAndGo(ac, null),
            "SG" => PatternCommandHandler.TrySetupStopAndGo(ac, null),
            "LA" => PatternCommandHandler.TrySetupLowApproach(ac, null),
            "COPT" => PatternCommandHandler.TrySetupClearedForOption(ac, null),
            _ => throw new Xunit.Sdk.XunitException($"Unknown verb: {verb}"),
        };

        Assert.True(result.Success);
        Assert.Equal(expectedClearance, ac.Phases.LandingClearance);
    }

    [Fact]
    public void TryClearedToLand_OnGround_Fails()
    {
        // Cannot clear an aircraft to land while it's on the ground (taxiing,
        // pushed back, post-landing). Real ATC: CLAND is for inbound traffic
        // on or being vectored to an approach.
        var ac = MakeAircraft(altitude: 100, onGround: true);

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand(), ac);

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
        Assert.Null(ac.Phases!.LandingClearance);
    }

    [Fact]
    public void TryClearedToLand_NoAssignedRunway_Fails()
    {
        // Without an assigned runway the clearance has no target — silently
        // storing it as null was the prior bug (ClearedRunwayId would be null).
        var ac = MakeAircraft(altitude: 1500, onGround: false);
        ac.Phases!.AssignedRunway = null;

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand(), ac);

        Assert.False(result.Success);
        Assert.Contains("no runway assigned", result.Message!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Null(ac.Phases.LandingClearance);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — airborne gate
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(PatternEntryLeg.Downwind)]
    [InlineData(PatternEntryLeg.Crosswind)]
    [InlineData(PatternEntryLeg.Base)]
    [InlineData(PatternEntryLeg.Final)]
    [InlineData(PatternEntryLeg.Upwind)]
    public void TryEnterPattern_OnGround_Fails(PatternEntryLeg entryLeg)
    {
        // Pattern entry only makes sense airborne. Closed-traffic departures
        // should come via CTO MLT/MRT, not via ERD/ELD/etc on a ground aircraft.
        var ac = MakeAircraft(altitude: 100, onGround: true);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, entryLeg, null, null);

        Assert.False(result.Success);
        Assert.Contains("airborne", result.Message!, System.StringComparison.OrdinalIgnoreCase);
    }
}
