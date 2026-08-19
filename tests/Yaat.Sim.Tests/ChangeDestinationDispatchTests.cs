using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// DEST/APT must work through the general dispatcher, not only via yaat-server's interactive
/// intercept: scenario presets and any conditional form (`AT 5000 DEST KOAK`) queue a normal
/// <see cref="CommandBlock"/> whose apply action goes through <see cref="CommandDispatcher"/>.
/// Without a dispatcher arm the block fails at trigger-fire time with "Unable to Change
/// destination to ..." (seen live in the ZDV 14 NW Feeder scenario).
/// </summary>
public class ChangeDestinationDispatchTests
{
    public ChangeDestinationDispatchTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(double altitude) =>
        new()
        {
            Callsign = "TST01",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = altitude,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KSFO",
                Destination = "KDEN",
            },
        };

    [Fact]
    public void Unconditioned_Dispatch_ChangesDestination_Canonicalized()
    {
        var aircraft = MakeAircraft(altitude: 5000);
        var compound = CommandParser.ParseCompound("APT OAK");
        Assert.True(compound.IsSuccess);

        var result = CommandDispatcher.DispatchCompound(compound.Value!, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void Conditional_AtAltitude_ChangesDestination_WhenTriggerFires()
    {
        var aircraft = MakeAircraft(altitude: 3000);
        var compound = CommandParser.ParseCompound("AT 5000 APT OAK");
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, aircraft, TestDispatch.Context(Random.Shared));

        Assert.Single(aircraft.Queue.Blocks);
        Assert.Equal(BlockTriggerType.ReachAltitude, aircraft.Queue.Blocks[0].Trigger!.Type);

        // Below the trigger: nothing applied, destination untouched.
        FlightPhysics.Update(aircraft, 1.0);
        Assert.False(aircraft.Queue.Blocks[0].IsApplied);
        Assert.Equal("KDEN", aircraft.FlightPlan.Destination);

        // Within snap range of the trigger altitude: the block fires and the destination changes.
        aircraft.Altitude = 4998;
        FlightPhysics.Update(aircraft, 1.0);
        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void UnknownAirport_Fails_WithoutMutatingDestination()
    {
        var aircraft = MakeAircraft(altitude: 5000);
        var compound = CommandParser.ParseCompound("APT ZZZQ");
        Assert.True(compound.IsSuccess);

        var result = CommandDispatcher.DispatchCompound(compound.Value!, aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("Unknown airport", result.Message);
        Assert.Equal("KDEN", aircraft.FlightPlan.Destination);
    }
}
