using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear"/>: a
/// phase-clearing lateral command must let a climbing aircraft continue to its assigned altitude,
/// but must NOT command an aircraft vectored off a descent/approach to climb back to a higher
/// last-assigned altitude (FAA last-assigned-altitude doctrine).
/// </summary>
public class ResumeAssignedAltitudeAfterPhaseClearTests
{
    private static AircraftState Make(double altitude, bool onGround, double? assigned, double? target)
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Altitude = altitude,
            IsOnGround = onGround,
        };
        ac.Targets.AssignedAltitude = assigned;
        ac.Targets.TargetAltitude = target;
        return ac;
    }

    [Fact]
    public void Climbing_ResumesAssignedAltitude()
    {
        // TakeoffPhase scenario: climbing through 335 ft toward its ~409 ft handoff target while
        // assigned 1400. After the phase clears the climb must continue to the assigned 1400.
        var ac = Make(altitude: 335, onGround: false, assigned: 1400, target: 409);
        CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(ac, clearedGoAround: false);
        Assert.Equal(1400, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void DescendingBelowAssigned_DoesNotClimbBack()
    {
        // Vectored off an approach: descended to 1500 ft (target 1300) while the last assigned
        // altitude was 2000. assigned > current, but the cleared phase was descending — the
        // aircraft must hold present altitude, not climb back to 2000.
        var ac = Make(altitude: 1500, onGround: false, assigned: 2000, target: 1300);
        CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(ac, clearedGoAround: false);
        Assert.Equal(1300, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void LevelAtAssigned_LeavesTargetUnchanged()
    {
        var ac = Make(altitude: 1400, onGround: false, assigned: 1400, target: null);
        CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(ac, clearedGoAround: false);
        Assert.Null(ac.Targets.TargetAltitude);
    }

    [Fact]
    public void OnGround_LeavesTargetUnchanged()
    {
        var ac = Make(altitude: 9, onGround: true, assigned: 1400, target: 409);
        CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(ac, clearedGoAround: false);
        Assert.Equal(409, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void NoAssignedAltitude_LeavesTargetUnchanged()
    {
        var ac = Make(altitude: 800, onGround: false, assigned: null, target: 800);
        CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(ac, clearedGoAround: false);
        Assert.Equal(800, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void GoAroundMapAboveAssigned_DoesNotLowerTarget()
    {
        // GoAroundPhase climbs to the published MAP (4000) while AssignedAltitude still reflects the
        // last approach clearance (3000). FH clears the phase; the MAP target must survive.
        var ac = Make(altitude: 500, onGround: false, assigned: 3000, target: 4000);
        CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(ac, clearedGoAround: true);
        Assert.Equal(4000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void GoAroundMapBelowAssigned_HoldsMapAltitude()
    {
        // MAP altitude (2000) below the stale approach clearance (3000): GoAroundPhase climbs to the
        // published MAP while AssignedAltitude still holds the higher last approach clearance. FH
        // clears the go-around mid-climb; the aircraft must hold the MAP altitude, not climb to the
        // higher stale assigned altitude.
        var ac = Make(altitude: 500, onGround: false, assigned: 3000, target: 2000);
        CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(ac, clearedGoAround: true);
        Assert.Equal(2000, ac.Targets.TargetAltitude);
    }
}
