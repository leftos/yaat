using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// The plain-input form of <see cref="GroundConflictDetector.IsParkedOrHeld(bool, string?, double, double?)"/>, the
/// classification the client's push-route preview shares with the simulation: a passable obstacle is held or in a
/// stationary phase, and genuinely at rest — under 3 kt and commanding no forward speed.
/// </summary>
public class GroundConflictDetectorParkedOrHeldTests
{
    [Theory]
    [InlineData(true, "Taxi", 0.0, 0.0)]
    [InlineData(true, null, 0.0, null)]
    public void HeldAndAtRest_IsParkedOrHeld(bool isImmobile, string? phaseName, double groundSpeedKts, double? targetSpeedKts) =>
        Assert.True(GroundConflictDetector.IsParkedOrHeld(isImmobile, phaseName, groundSpeedKts, targetSpeedKts));

    [Theory]
    [InlineData("At Parking")]
    [InlineData("Holding After Pushback")]
    [InlineData("LinedUpAndWaiting")]
    [InlineData("Holding Short 28L")]
    public void StationaryPhaseAtRest_IsParkedOrHeld(string phaseName) =>
        Assert.True(GroundConflictDetector.IsParkedOrHeld(isImmobile: false, phaseName, groundSpeedKts: 0.0, targetSpeedKts: 0.0));

    [Fact]
    public void StationaryPhaseWithNoTargetSpeed_IsParkedOrHeld() =>
        Assert.True(GroundConflictDetector.IsParkedOrHeld(isImmobile: false, "At Parking", groundSpeedKts: 0.0, targetSpeedKts: null));

    [Theory]
    [InlineData(false, "Taxi", 15.0, 15.0)]
    [InlineData(false, "Holding After Pushback", 5.0, 0.0)]
    [InlineData(true, null, 5.0, 0.0)]
    public void Moving_IsNotParkedOrHeld(bool isImmobile, string? phaseName, double groundSpeedKts, double? targetSpeedKts) =>
        Assert.False(GroundConflictDetector.IsParkedOrHeld(isImmobile, phaseName, groundSpeedKts, targetSpeedKts));

    [Fact]
    public void LiningUpCreepingUnderCommand_IsNotParkedOrHeld() =>
        Assert.False(GroundConflictDetector.IsParkedOrHeld(isImmobile: false, "LiningUp", groundSpeedKts: 2.0, targetSpeedKts: 2.0));

    [Fact]
    public void NotHeldAndNoStationaryPhase_IsNotParkedOrHeld_EvenAtRest() =>
        Assert.False(GroundConflictDetector.IsParkedOrHeld(isImmobile: false, phaseName: null, groundSpeedKts: 0.0, targetSpeedKts: null));
}
