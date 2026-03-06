using Xunit;
using Yaat.Sim;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

public class RngDeterminismTests
{
    [Fact]
    public void GenerateBeaconCode_SameSeed_SameSequence()
    {
        var rng1 = new Random(42);
        var rng2 = new Random(42);

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(SimulationWorld.GenerateBeaconCode(rng1), SimulationWorld.GenerateBeaconCode(rng2));
        }
    }

    [Fact]
    public void GenerateBeaconCode_DifferentSeeds_DifferentResults()
    {
        var rng1 = new Random(42);
        var rng2 = new Random(99);

        // Generate several codes — at least one should differ
        var codes1 = Enumerable.Range(0, 10).Select(_ => SimulationWorld.GenerateBeaconCode(rng1)).ToList();
        var codes2 = Enumerable.Range(0, 10).Select(_ => SimulationWorld.GenerateBeaconCode(rng2)).ToList();

        Assert.False(codes1.SequenceEqual(codes2));
    }

    [Fact]
    public void GenerateUniqueCid_SameSeed_SameSequence()
    {
        var world1 = new SimulationWorld { Rng = new Random(42) };
        var world2 = new SimulationWorld { Rng = new Random(42) };

        var ac1 = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };
        var ac2 = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };

        world1.AddAircraft(ac1);
        world2.AddAircraft(ac2);

        Assert.Equal(ac1.Cid, ac2.Cid);
    }

    [Fact]
    public void SimulationWorld_Rng_DefaultsToShared()
    {
        var world = new SimulationWorld();
        Assert.Same(Random.Shared, world.Rng);
    }

    [Fact]
    public void SimulationWorld_Rng_CanBeSeeded()
    {
        var world = new SimulationWorld();
        var seeded = new Random(123);
        world.Rng = seeded;
        Assert.Same(seeded, world.Rng);
    }

    [Fact]
    public void SeededWorld_TickDeterministic()
    {
        // Two worlds with same seed and same aircraft should produce identical results after ticking
        var world1 = new SimulationWorld { Rng = new Random(42) };
        var world2 = new SimulationWorld { Rng = new Random(42) };

        var ac1 = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Latitude = 37.8,
            Longitude = -122.3,
            Altitude = 5000,
            Heading = 270,
            IndicatedAirspeed = 250,
            Track = 270,
            Targets =
            {
                TargetHeading = 270,
                TargetAltitude = 5000,
                TargetSpeed = 250,
            },
        };

        var ac2 = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Latitude = 37.8,
            Longitude = -122.3,
            Altitude = 5000,
            Heading = 270,
            IndicatedAirspeed = 250,
            Track = 270,
            Targets =
            {
                TargetHeading = 270,
                TargetAltitude = 5000,
                TargetSpeed = 250,
            },
        };

        world1.AddAircraft(ac1);
        world2.AddAircraft(ac2);

        for (int i = 0; i < 30; i++)
        {
            world1.Tick(1.0, null);
            world2.Tick(1.0, null);
        }

        Assert.Equal(ac1.Latitude, ac2.Latitude, 10);
        Assert.Equal(ac1.Longitude, ac2.Longitude, 10);
        Assert.Equal(ac1.Altitude, ac2.Altitude, 4);
        Assert.Equal(ac1.Heading, ac2.Heading, 4);
    }
}
