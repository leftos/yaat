using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Round-trip tests for <see cref="DepartureInstruction"/> snapshot serialization. A departure
/// stored on a still-taxiing aircraft (PhaseList.DepartureClearance) must survive a snapshot /
/// rewind without losing its identity — otherwise a closed-traffic or pattern-exit clearance
/// silently reverts to a plain straight-out departure on restore.
/// </summary>
public class DepartureInstructionSnapshotTests
{
    [Fact]
    public void ClosedTrafficDeparture_RoundTrips_PreservingDirectionRunwayAndAltitude()
    {
        var original = new ClosedTrafficDeparture(PatternDirection.Right, "28R", 1500);

        var restored = DepartureInstruction.FromSnapshot(original.ToSnapshot());

        var ct = Assert.IsType<ClosedTrafficDeparture>(restored);
        Assert.Equal(PatternDirection.Right, ct.Direction);
        Assert.Equal("28R", ct.RunwayId);
        Assert.Equal(1500, ct.PatternAltitude);
    }

    [Fact]
    public void ClosedTrafficDeparture_SameRunwayNoAltitude_RoundTrips()
    {
        var original = new ClosedTrafficDeparture(PatternDirection.Left, null, null);

        var restored = DepartureInstruction.FromSnapshot(original.ToSnapshot());

        var ct = Assert.IsType<ClosedTrafficDeparture>(restored);
        Assert.Equal(PatternDirection.Left, ct.Direction);
        Assert.Null(ct.RunwayId);
        Assert.Null(ct.PatternAltitude);
    }

    [Fact]
    public void PatternExitDeparture_RoundTrips_PreservingExitLegAndDirection()
    {
        var original = new PatternExitDeparture(PatternEntryLeg.Downwind, PatternDirection.Right);

        var restored = DepartureInstruction.FromSnapshot(original.ToSnapshot());

        var ped = Assert.IsType<PatternExitDeparture>(restored);
        Assert.Equal(PatternEntryLeg.Downwind, ped.ExitLeg);
        Assert.Equal(PatternDirection.Right, ped.Direction);
    }

    [Fact]
    public void RelativeTurnDeparture_RoundTrips()
    {
        var original = new RelativeTurnDeparture(45, TurnDirection.Left);

        var restored = DepartureInstruction.FromSnapshot(original.ToSnapshot());

        var rel = Assert.IsType<RelativeTurnDeparture>(restored);
        Assert.Equal(45, rel.Degrees);
        Assert.Equal(TurnDirection.Left, rel.Direction);
    }
}
