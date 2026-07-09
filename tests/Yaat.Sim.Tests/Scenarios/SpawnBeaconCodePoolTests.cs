using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// ADD-command spawns draw their discrete beacon code from the room's <see cref="BeaconCodePool"/> — the
/// same allocator that filing a flight plan uses — so codes come from the facility's configured banks and
/// never duplicate a live aircraft's. VFR cold calls squawk 1200 and consume no code from the pool.
/// </summary>
public class SpawnBeaconCodePoolTests
{
    // Mirrors ZOA's real vNAS beacon-code banks.
    private static BeaconCodePool ZoaPool() =>
        new([
            new BeaconCodeBankConfig
            {
                Type = "Vfr",
                Start = 101,
                End = 160,
            },
            new BeaconCodeBankConfig
            {
                Type = "Ifr",
                Start = 401,
                End = 436,
            },
        ]);

    private static SpawnRequest BearingRequest(FlightRulesKind rules) =>
        new()
        {
            Rules = rules,
            Weight = WeightClass.Small,
            Engine = EngineKind.Piston,
            PositionType = SpawnPositionType.Bearing,
            Bearing = 360,
            DistanceNm = 10,
            Altitude = 4500,
        };

    private static AircraftState? Spawn(FlightRulesKind rules, BeaconCodePool pool, int seed)
    {
        var (state, error) = AircraftGenerator.Generate(BearingRequest(rules), "OAK", [], groundLayout: null, new Random(seed), pool);
        Assert.Null(error);
        return state;
    }

    [Fact]
    public void IfrAdd_DrawsFromConfiguredIfrBank()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var state = Spawn(FlightRulesKind.Ifr, ZoaPool(), 42);

        Assert.NotNull(state);
        Assert.InRange(state.Transponder.AssignedCode, 401u, 436u);
        Assert.Equal(state.Transponder.AssignedCode, state.Transponder.Code);
    }

    /// <summary>
    /// The old random draw had no duplicate check — two spawns could squawk the same code.
    /// </summary>
    [Fact]
    public void ConsecutiveIfrAdds_GetDistinctCodes()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var pool = ZoaPool();
        var codes = new HashSet<uint>();
        for (int i = 0; i < 12; i++)
        {
            var state = Spawn(FlightRulesKind.Ifr, pool, 42);
            Assert.NotNull(state);
            Assert.True(codes.Add(state.Transponder.AssignedCode), $"duplicate beacon code {state.Transponder.AssignedCode:D4} on spawn {i}");
        }
    }

    /// <summary>
    /// A VFR cold call squawks 1200 and must not consume a discrete code — the next IFR spawn still gets
    /// the first code in the bank.
    /// </summary>
    [Fact]
    public void VfrAdd_Squawks1200_AndConsumesNoPoolCode()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var pool = ZoaPool();

        var vfr = Spawn(FlightRulesKind.Vfr, pool, 42);
        Assert.NotNull(vfr);
        Assert.Equal(0u, vfr.Transponder.AssignedCode);
        Assert.Equal(1200u, vfr.Transponder.Code);

        var ifr = Spawn(FlightRulesKind.Ifr, pool, 43);
        Assert.NotNull(ifr);
        Assert.Equal(401u, ifr.Transponder.AssignedCode);
    }

    /// <summary>
    /// A facility whose config defines an IFR bank but no VFR bank must not strand a spawn on the illegal
    /// all-zeros squawk. No spawn may ever end up transmitting 0000.
    /// </summary>
    [Fact]
    public void IfrAdd_FacilityWithOnlyVfrBank_NeverSquawksZero()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var pool = new BeaconCodePool([
            new BeaconCodeBankConfig
            {
                Type = "Vfr",
                Start = 101,
                End = 160,
            },
        ]);

        var state = Spawn(FlightRulesKind.Ifr, pool, 42);

        Assert.NotNull(state);
        Assert.NotEqual(0u, state.Transponder.Code);
        Assert.NotEqual(0u, state.Transponder.AssignedCode);
        Assert.True(BeaconCodePool.IsAssignableCode(state.Transponder.AssignedCode));
    }

    /// <summary>
    /// With no banks configured the pool falls back to sequential 0001, 0002, … — the same fallback filing
    /// a flight plan uses, so spawn and file agree.
    /// </summary>
    [Fact]
    public void IfrAdd_NoBanksConfigured_FallsBackToSequential()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var pool = new BeaconCodePool();

        Assert.Equal(1u, Spawn(FlightRulesKind.Ifr, pool, 42)!.Transponder.AssignedCode);
        Assert.Equal(2u, Spawn(FlightRulesKind.Ifr, pool, 43)!.Transponder.AssignedCode);
    }
}
