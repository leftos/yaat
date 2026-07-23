using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Transponder IDENT auto-expiry timing. The per-tick logic lives in
/// <see cref="AircraftTransponder.TickIdent"/> and is driven by
/// <see cref="SimulationEngine.TickTransponderIdents"/> (owned by Yaat.Sim so the standalone host
/// and the live server run the identical logic — the server no longer hand-writes its own copy).
/// </summary>
public class TransponderIdentTests
{
    [Fact]
    public void TickIdent_NotIdenting_IsNoOp()
    {
        var xpdr = new AircraftTransponder();

        xpdr.TickIdent(nowSeconds: 100);

        Assert.False(xpdr.IsIdenting);
        Assert.Null(xpdr.IdentStartedAt);
    }

    [Fact]
    public void TickIdent_FirstTick_StampsStartTime()
    {
        var xpdr = new AircraftTransponder { IsIdenting = true };

        xpdr.TickIdent(nowSeconds: 42);

        Assert.True(xpdr.IsIdenting);
        Assert.Equal(42, xpdr.IdentStartedAt);
    }

    [Fact]
    public void TickIdent_BeforeDurationElapses_StaysIdenting()
    {
        var xpdr = new AircraftTransponder { IsIdenting = true, IdentStartedAt = 10 };

        xpdr.TickIdent(nowSeconds: 10 + AircraftTransponder.IdentDurationSeconds - 1);

        Assert.True(xpdr.IsIdenting);
        Assert.Equal(10, xpdr.IdentStartedAt);
    }

    [Fact]
    public void TickIdent_AtDuration_ClearsIdent()
    {
        var xpdr = new AircraftTransponder { IsIdenting = true, IdentStartedAt = 10 };

        xpdr.TickIdent(nowSeconds: 10 + AircraftTransponder.IdentDurationSeconds);

        Assert.False(xpdr.IsIdenting);
        Assert.Null(xpdr.IdentStartedAt);
    }

    [Fact]
    public void TickTransponderIdents_ClearsExpiredIdentAcrossWorld()
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = NewScenario(elapsedSeconds: 10 + AircraftTransponder.IdentDurationSeconds),
        };
        var stale = NewAircraft("NSTALE");
        stale.Transponder.IsIdenting = true;
        stale.Transponder.IdentStartedAt = 10;
        var fresh = NewAircraft("NFRESH");
        fresh.Transponder.IsIdenting = true;
        fresh.Transponder.IdentStartedAt = 10 + AircraftTransponder.IdentDurationSeconds - 5;
        engine.World.AddAircraft(stale);
        engine.World.AddAircraft(fresh);

        engine.TickTransponderIdents();

        Assert.False(stale.Transponder.IsIdenting);
        Assert.Null(stale.Transponder.IdentStartedAt);
        Assert.True(fresh.Transponder.IsIdenting);
    }

    [Fact]
    public void TickPostPhysics_RunsIdentExpiry()
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = NewScenario(elapsedSeconds: 10 + AircraftTransponder.IdentDurationSeconds),
        };
        var ac = NewAircraft("NPOST");
        ac.Transponder.IsIdenting = true;
        ac.Transponder.IdentStartedAt = 10;
        engine.World.AddAircraft(ac);

        engine.TickPostPhysics();

        Assert.False(ac.Transponder.IsIdenting);
    }

    private static AircraftState NewAircraft(string callsign) => new() { Callsign = callsign, AircraftType = "C172" };

    private static SimScenarioState NewScenario(double elapsedSeconds) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
        };
}
