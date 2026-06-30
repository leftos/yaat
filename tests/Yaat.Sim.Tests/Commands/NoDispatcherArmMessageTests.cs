using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A command that reaches the dispatcher's fallback arm (no handler in <c>ApplyCommand</c>) used to
/// surface a developer sentinel — <c>__NO_DISPATCHER_ARM__ no dispatcher arm for TaxiCommand (...)</c>
/// — straight to the user. The most common trigger is a ground command (TAXI) sent to an airborne
/// aircraft. The user must see a plain, actionable message instead, and the marker string must never
/// leak into user-facing text.
/// </summary>
[Collection("NavDbMutator")]
public class NoDispatcherArmMessageTests
{
    public NoDispatcherArmMessageTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState AirborneAircraft() =>
        new()
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(120),
            TrueTrack = new TrueHeading(120),
            Altitude = 3000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };

    [Fact]
    public void GroundCommand_ToAirborneAircraft_ReturnsFriendlyMessage()
    {
        var ac = AirborneAircraft();
        var taxi = new TaxiCommand(["W1"], [], DestinationRunway: "30");

        var result = CommandDispatcher.Dispatch(taxi, ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.DoesNotContain("__", result.Message);
        Assert.DoesNotContain("dispatcher arm", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ground", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.NoDispatcherArm);
    }
}
