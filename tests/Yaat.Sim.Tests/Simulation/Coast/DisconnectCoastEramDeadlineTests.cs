using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Coast;

/// <summary>
/// The ERAM facet of a disconnect coast ends <see cref="SimScenarioState.EramDisconnectCoastSeconds"/> after the track's
/// last 12 s ERAM sweep — the last position CRC drew — not after the removal: the sweep grid is the callsign's phase
/// (FNV-1a of the callsign, mod 12) and the last sweep is never earlier than the aircraft's spawn. The fixture's
/// callsign, N152SP, has phase 0, so its sweeps fall on every multiple of 12 sim seconds.
/// </summary>
public class DisconnectCoastEramDeadlineTests
{
    private const string Callsign = "N152SP";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public DisconnectCoastEramDeadlineTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The fixture's aircraft, airborne well above every ZOA surface display and OAK's ERAM floor, with the scenario
    /// clock at <paramref name="removalSimSeconds"/>; null when the ZOA configuration is not available.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft)? AirborneAt(double removalSimSeconds)
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        engine.RunSecond(new SpineCapturingHost(engine));
        AircraftState ac = engine.World.GetSnapshot()[0];
        ac.Altitude = 5000;
        ac.IsOnGround = false;
        engine.TickSurfaceMembership();
        engine.Scenario!.ElapsedSeconds = removalSimSeconds;
        return (engine, ac);
    }

    private static double EramDeadlineAfterDelete(SimulationEngine engine)
    {
        engine.DeleteAircraft(Callsign, "DEL");
        AircraftDisconnectCoast coast = Assert.Contains(
            Callsign,
            (IReadOnlyDictionary<string, AircraftDisconnectCoast>)engine.Scenario!.DisconnectCoasts
        );
        return Assert.Single(coast.Facets, f => f.Scope == DisconnectCoastScope.Eram).DeadlineSimSeconds;
    }

    [Theory]
    [InlineData("N152SP", 0)]
    [InlineData("SWA5456", 4)]
    [InlineData("UAL123", 11)]
    public void TheSweepPhase_IsFnv1aOfTheCallsignMod12(string callsign, int expectedOffset)
    {
        Assert.Equal(expectedOffset, EramSweepGrid.OffsetSeconds(callsign));
        Assert.Equal(expectedOffset + 120, EramSweepGrid.LastSweepSimSeconds(callsign, expectedOffset + 131.5));
        Assert.Equal(expectedOffset - 12, EramSweepGrid.LastSweepSimSeconds(callsign, expectedOffset - 0.5));
    }

    [Fact]
    public void ARemovalMidCycle_EndsTheCoast24SecondsAfterTheLastSweep()
    {
        if (AirborneAt(125) is not { } setup)
        {
            return;
        }

        SimulationEngine engine = setup.Engine;

        // The last sweep before 125 s was at 120 s, so the coast ends at 144 s, 19 s after the removal.
        Assert.Equal(144, EramDeadlineAfterDelete(engine));
    }

    [Fact]
    public void ARemovalOnASweep_EndsTheCoast24SecondsAfterTheRemoval()
    {
        if (AirborneAt(120) is not { } setup)
        {
            return;
        }

        Assert.Equal(144, EramDeadlineAfterDelete(setup.Engine));
    }

    [Fact]
    public void AnAircraftNeverSweptSinceItsSpawn_CoastsFromItsSpawn()
    {
        if (AirborneAt(125) is not { } setup)
        {
            return;
        }

        // Spawned after the 120 s sweep: the server swept it on first sight, at its spawn.
        setup.Aircraft.SpawnedAtSeconds = 123;

        Assert.Equal(147, EramDeadlineAfterDelete(setup.Engine));
    }
}
