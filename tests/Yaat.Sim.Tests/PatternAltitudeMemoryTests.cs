using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

public class PatternAltitudeMemoryTests
{
    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static AircraftState MakePatternAircraft(Phase currentPhase)
    {
        var runway = DefaultRunway();
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(100),
            Altitude = 1100,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "KTEST",
            Destination = "KTEST",
        };

        var waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var phases = new PhaseList { AssignedRunway = runway };
        phases.TrafficDirection = PatternDirection.Left;
        phases.Add(currentPhase);
        phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        ac.Phases = phases;
        return ac;
    }

    // -------------------------------------------------------------------------
    // CM/DM during pattern sets PatternAltitudeOverrideFt
    // -------------------------------------------------------------------------

    [Fact]
    public void ClimbMaintain_DuringDownwind_SetsPatternAltitudeOverride()
    {
        var waypoints = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var ac = MakePatternAircraft(new DownwindPhase { Waypoints = waypoints });

        FlightCommandHandler.ApplyClimbMaintain(new ClimbMaintainCommand(1500), ac);

        Assert.Equal(1500, ac.PatternAltitudeOverrideFt);
    }

    [Fact]
    public void DescendMaintain_DuringUpwind_SetsPatternAltitudeOverride()
    {
        var waypoints = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var ac = MakePatternAircraft(new UpwindPhase { Waypoints = waypoints });

        FlightCommandHandler.ApplyDescendMaintain(new DescendMaintainCommand(800), ac);

        Assert.Equal(800, ac.PatternAltitudeOverrideFt);
    }

    // -------------------------------------------------------------------------
    // CM/DM does NOT clear pattern phases
    // -------------------------------------------------------------------------

    [Fact]
    public void ClimbMaintain_DuringDownwind_DoesNotClearPattern()
    {
        var waypoints = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var dw = new DownwindPhase { Waypoints = waypoints };
        var ac = MakePatternAircraft(dw);

        // CanAcceptCommand should return Allowed for CM
        var acceptance = dw.CanAcceptCommand(CanonicalCommandType.ClimbMaintain);
        Assert.Equal(CommandAcceptance.Allowed, acceptance);
    }

    [Fact]
    public void DescendMaintain_DuringBase_DoesNotClearPattern()
    {
        var waypoints = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var bp = new BasePhase { Waypoints = waypoints };
        var ac = MakePatternAircraft(bp);

        var acceptance = bp.CanAcceptCommand(CanonicalCommandType.DescendMaintain);
        Assert.Equal(CommandAcceptance.Allowed, acceptance);
    }

    [Fact]
    public void ClimbMaintain_AllPatternPhases_ReturnAllowed()
    {
        var waypoints = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, PatternDirection.Left, null, null, null);

        Assert.Equal(CommandAcceptance.Allowed, new UpwindPhase { Waypoints = waypoints }.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
        Assert.Equal(CommandAcceptance.Allowed, new CrosswindPhase { Waypoints = waypoints }.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
        Assert.Equal(CommandAcceptance.Allowed, new DownwindPhase { Waypoints = waypoints }.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
        Assert.Equal(CommandAcceptance.Allowed, new BasePhase { Waypoints = waypoints }.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
    }

    // -------------------------------------------------------------------------
    // CM/DM outside pattern mode does NOT set override
    // -------------------------------------------------------------------------

    [Fact]
    public void ClimbMaintain_OutsidePatternMode_DoesNotSetOverride()
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(280),
            Altitude = 3000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
            Departure = "KTEST",
        };
        // No phases or TrafficDirection = null
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };

        FlightCommandHandler.ApplyClimbMaintain(new ClimbMaintainCommand(5000), ac);

        Assert.Null(ac.PatternAltitudeOverrideFt);
    }

    // -------------------------------------------------------------------------
    // Auto-cycle uses altitude override
    // -------------------------------------------------------------------------

    [Fact]
    public void PatternGeometry_Compute_UsesAltitudeOverride()
    {
        var runway = DefaultRunway();
        var waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, 1500, null);

        Assert.Equal(1500, waypoints.PatternAltitude);
    }

    [Fact]
    public void PatternGeometry_Compute_DefaultAltitude_WhenNoOverride()
    {
        var runway = DefaultRunway();
        var waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, null, null);

        // Piston TPA = field elevation (100) + 1000 AGL = 1100
        Assert.Equal(1100, waypoints.PatternAltitude);
    }

    // -------------------------------------------------------------------------
    // Snapshot round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void PatternAltitudeOverride_SurvivesSnapshotRoundTrip()
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(280),
            Altitude = 1100,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "KTEST",
            PatternAltitudeOverrideFt = 1500,
        };

        var dto = ac.ToSnapshot();
        Assert.Equal(1500, dto.PatternAltitudeOverrideFt);

        var restored = AircraftState.FromSnapshot(dto, null);
        Assert.Equal(1500, restored.PatternAltitudeOverrideFt);
    }

    // -------------------------------------------------------------------------
    // Changing runway clears override
    // -------------------------------------------------------------------------

    [Fact]
    public void TryChangePatternDirection_WithNewRunway_ClearsAltitudeOverride()
    {
        // TryChangePatternDirection resolves the runway via NavigationDatabase,
        // so we test the clearing behavior at OAK via real data.
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return; // Skip if test data not available
        }

        var runway = TestVnasData.NavigationDb.GetRunway("KOAK", "28L");
        if (runway is null)
        {
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(100),
            Altitude = 1100,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "KOAK",
            Destination = "KOAK",
            PatternAltitudeOverrideFt = 1500,
        };
        var waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var phases = new PhaseList { AssignedRunway = runway };
        phases.TrafficDirection = PatternDirection.Left;
        phases.Add(new DownwindPhase { Waypoints = waypoints });
        phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        ac.Phases = phases;

        // Changing to 28R (different runway) should clear the altitude override
        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Right, "28R", null);
        Assert.True(result.Success);
        Assert.Null(ac.PatternAltitudeOverrideFt);
    }

    [Fact]
    public void TryChangePatternDirection_SameRunway_KeepsAltitudeOverride()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway = TestVnasData.NavigationDb.GetRunway("KOAK", "28L");
        if (runway is null)
        {
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(100),
            Altitude = 1100,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "KOAK",
            Destination = "KOAK",
            PatternAltitudeOverrideFt = 1500,
        };
        var waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var phases = new PhaseList { AssignedRunway = runway };
        phases.TrafficDirection = PatternDirection.Left;
        phases.Add(new DownwindPhase { Waypoints = waypoints });
        phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        ac.Phases = phases;

        // Changing direction only (no new runway) should keep the altitude override
        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Right, null, null);
        Assert.True(result.Success);
        Assert.Equal(1500, ac.PatternAltitudeOverrideFt);
    }
}
