using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The router's chain checks (<c>CompoundPolicy</c>) parse a chain with the target aircraft's airports, so a chain naming
/// a fix that is only a CIFP terminal waypoint of them (CFBMS at KBCT) is refused or split exactly as one naming a
/// NavData fix would be, rather than failing open past the check.
/// </summary>
public class TerminalWaypointRouterTests
{
    public TerminalWaypointRouterTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine BuildEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
            },
        };

    [Fact]
    public void CtoDctCfbmsThenTurn_IsRefusedForKbctDeparture()
    {
        SimulationEngine engine = BuildEngine();
        // A lined-up aircraft whose filed departure is KBCT: the context comes from the flight plan, not the field it sits on.
        AircraftState ac = LinedUpAircraft.AtOak28R("UAL123");
        ac.FlightPlan.Departure = "KBCT";
        engine.World.AddAircraft(ac);

        ActionOutcome outcome = engine.Actions.Issue(new ActionInput("UAL123", "CTO DCT CFBMS, R270", "conn-1", "XX", Baked: null));

        Assert.False(outcome.Result.Success);
        Assert.Contains("cannot be paired with a takeoff clearance", outcome.Result.Message);
    }

    [Fact]
    public void CfixCfbmsThenHandoff_IsSplit()
    {
        SimulationEngine engine = BuildEngine();
        var ac = new AircraftState
        {
            Callsign = "RVJ100",
            AircraftType = "C25B",
            Position = new LatLon(26.70, -80.25),
            TrueHeading = new TrueHeading(180),
            Altitude = 4000,
            IndicatedAirspeed = 200,
            FlightPlan = new AircraftFlightPlan { Departure = "KBWI", Destination = "KBCT" },
        };
        engine.World.AddAircraft(ac);

        engine.Actions.Issue(new ActionInput("RVJ100", "CFIX CFBMS 1400, HO 3G", "conn-1", "XX", Baked: null));

        // Split, each unit routes and records on its own; unsplit, the line records once, whole.
        Assert.Equal(["CFIX CFBMS 1400", "HO 3G"], engine.Scenario!.ActionLog.OfType<RecordedCommand>().Select(r => r.Command));
    }
}
