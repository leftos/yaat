using Xunit;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class SimulationWorldTests
{
    private static AircraftState MakeAircraft(string callsign, string cid = "")
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Cid = cid,
        };
    }

    [Fact]
    public void AddAircraft_GeneratesCid_WhenEmpty()
    {
        var world = new SimulationWorld();
        var ac = MakeAircraft("AAL100");

        world.AddAircraft(ac);

        Assert.NotEmpty(ac.Cid);
        Assert.Equal(3, ac.Cid.Length);
    }

    [Fact]
    public void AddAircraft_PreservesCid_WhenProvided()
    {
        var world = new SimulationWorld();
        var ac = MakeAircraft("AAL100", cid: "500");

        world.AddAircraft(ac);

        Assert.Equal("500", ac.Cid);
    }

    [Fact]
    public void GetSnapshot_ReturnsShallowCopy()
    {
        var world = new SimulationWorld();
        world.AddAircraft(MakeAircraft("AAL100"));

        var snapshot = world.GetSnapshot();
        snapshot.Add(MakeAircraft("EXTRA1"));
        snapshot.RemoveAt(0);

        Assert.Single(world.GetSnapshot());
    }

    [Fact]
    public void GetSnapshot_ReturnsAllAircraft()
    {
        var world = new SimulationWorld();
        world.AddAircraft(MakeAircraft("AAL100"));
        world.AddAircraft(MakeAircraft("UAL200"));
        world.AddAircraft(MakeAircraft("DAL300"));

        Assert.Equal(3, world.GetSnapshot().Count);
    }

    [Fact]
    public void RemoveAircraft_RemovesMatchingCallsign()
    {
        var world = new SimulationWorld();
        world.AddAircraft(MakeAircraft("AAL100"));

        world.RemoveAircraft("AAL100");

        Assert.Empty(world.GetSnapshot());
    }

    [Fact]
    public void RemoveAircraft_NoMatch_DoesNothing()
    {
        var world = new SimulationWorld();
        world.AddAircraft(MakeAircraft("AAL100"));

        world.RemoveAircraft("UAL200");

        Assert.Single(world.GetSnapshot());
    }

    [Fact]
    public void DrainAllWarnings_ReturnsAndClears()
    {
        var world = new SimulationWorld();
        var ac = MakeAircraft("AAL100");
        ac.PendingWarnings.Add("test warning");
        world.AddAircraft(ac);

        var first = world.DrainAllWarnings();
        var second = world.DrainAllWarnings();

        Assert.Single(first);
        Assert.Equal(("AAL100", "test warning"), first[0]);
        Assert.Empty(second);
    }

    [Fact]
    public void DrainAllNotifications_ReturnsAndClears()
    {
        var world = new SimulationWorld();
        var ac = MakeAircraft("UAL200");
        ac.PendingNotifications.Add("test notification");
        world.AddAircraft(ac);

        var first = world.DrainAllNotifications();
        var second = world.DrainAllNotifications();

        Assert.Single(first);
        Assert.Equal(("UAL200", "test notification"), first[0]);
        Assert.Empty(second);
    }

    [Fact]
    public void DrainAllApproachScores_ReturnsAndClears()
    {
        var world = new SimulationWorld();
        var ac = MakeAircraft("DAL300");
        ac.PendingApproachScores.Add(
            new ApproachScore
            {
                Callsign = "DAL300",
                AircraftType = "B738",
                ApproachId = "I28R",
                RunwayId = "28R",
                AirportCode = "OAK",
            }
        );
        world.AddAircraft(ac);

        var first = world.DrainAllApproachScores();
        var second = world.DrainAllApproachScores();

        Assert.Single(first);
        Assert.Equal("DAL300", first[0].Callsign);
        Assert.Empty(second);
    }

    [Fact]
    public void Clear_ReturnsCount_AndEmptiesWorld()
    {
        var world = new SimulationWorld();
        world.AddAircraft(MakeAircraft("AAL100"));
        world.AddAircraft(MakeAircraft("UAL200"));
        world.AddAircraft(MakeAircraft("DAL300"));

        int count = world.Clear();

        Assert.Equal(3, count);
        Assert.Empty(world.GetSnapshot());
    }

    [Fact]
    public void Clear_SetsGroundLayoutToNull()
    {
        var world = new SimulationWorld();
        world.GroundLayout = new Yaat.Sim.Data.Airport.AirportGroundLayout { AirportId = "OAK" };

        world.Clear();

        Assert.Null(world.GroundLayout);
    }

    [Fact]
    public void GenerateBeaconCode_AllDigitsOctal()
    {
        for (int i = 0; i < 100; i++)
        {
            uint code = SimulationWorld.GenerateBeaconCode(Random.Shared);
            string digits = code.ToString();
            foreach (char digit in digits)
            {
                Assert.True(digit >= '0' && digit <= '7', $"Non-octal digit '{digit}' in beacon code {code}");
            }
        }
    }

    [Fact]
    public void Tick_CallsPreTick()
    {
        var world = new SimulationWorld();
        world.AddAircraft(MakeAircraft("AAL100"));

        var invokedCallsigns = new List<string>();
        world.Tick(1.0, (ac, _) => invokedCallsigns.Add(ac.Callsign));

        Assert.Contains("AAL100", invokedCallsigns);
    }
}
