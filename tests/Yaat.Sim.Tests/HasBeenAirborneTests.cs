using Xunit;
using Yaat.Sim;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="AircraftState.HasBeenAirborne"/> is the persistent pre-departure discriminator
/// behind the CRC <c>FlightPlanStatus.Proposed</c>→Active flip (leftos/yaat#383): on-ground and
/// never airborne means the plan is still proposed; once the tick loop observes the aircraft
/// airborne the flag latches for life (so a landed arrival never regresses to Proposed) and it
/// must survive snapshot round-trips.
/// </summary>
public class HasBeenAirborneTests
{
    public HasBeenAirborneTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(bool onGround)
    {
        return new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(37.72, -122.22),
            TrueHeading = new TrueHeading(270),
            Altitude = onGround ? 6 : 4500,
            IndicatedAirspeed = onGround ? 0 : 110,
            IsOnGround = onGround,
        };
    }

    [Fact]
    public void Tick_AirborneAircraft_LatchesHasBeenAirborne()
    {
        var world = new SimulationWorld();
        var ac = MakeAircraft(onGround: false);
        world.AddAircraft(ac);
        Assert.False(ac.HasBeenAirborne);

        world.Tick(1.0, 1.0);

        Assert.True(ac.HasBeenAirborne);
    }

    [Fact]
    public void Tick_GroundAircraft_DoesNotSetHasBeenAirborne()
    {
        var world = new SimulationWorld();
        var ac = MakeAircraft(onGround: true);
        world.AddAircraft(ac);

        world.Tick(1.0, 1.0);

        Assert.False(ac.HasBeenAirborne);
    }

    [Fact]
    public void Snapshot_RoundTripsHasBeenAirborne()
    {
        var ac = MakeAircraft(onGround: true);
        ac.HasBeenAirborne = true;

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);

        Assert.True(restored.HasBeenAirborne);
    }
}
