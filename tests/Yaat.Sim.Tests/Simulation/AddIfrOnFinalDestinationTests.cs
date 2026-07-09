using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the bug where the <c>ADD</c> command spawning an IFR aircraft on
/// final does not auto-populate <see cref="AircraftFlightPlan.Destination"/>
/// with the scenario's primary airport.
///
/// Reproduction: in <c>add-ifr-final-destination-recording.yaat-bug-report-bundle.zip</c>
/// (S2-OAK-5 Practical Exam Prep), at t=796s the user issues <c>ADD I L J 30 30</c>
/// against scenario <c>primaryAirportId="OAK"</c>. The spawned aircraft (ASA1196)
/// receives an empty Destination — the user immediately amends the FP to set
/// <c>Destination="KOAK"</c> manually.
///
/// Expected: <see cref="AircraftGenerator.GenerateOnFinal"/> should populate
/// <c>FlightPlan.Destination</c> with the canonical ICAO id of the primary
/// airport (e.g. <c>"KOAK"</c>) for IFR spawns. VFR spawns are unchanged.
/// </summary>
public class AddIfrOnFinalDestinationTests(ITestOutputHelper output)
{
    private static SpawnRequest MakeOnFinalRequest(FlightRulesKind rules) =>
        new()
        {
            Rules = rules,
            Weight = WeightClass.Large,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnFinal,
            RunwayId = "30",
            FinalDistanceNm = 30,
        };

    [Fact]
    public void GenerateOnFinal_Ifr_SetsDestinationToCanonicalIcao()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("Skipped: NavData not available");
            return;
        }

        var (state, error) = AircraftGenerator.Generate(
            request: MakeOnFinalRequest(FlightRulesKind.Ifr),
            primaryAirportId: "OAK",
            existingAircraft: [],
            groundLayout: null,
            rng: new Random(42),
            beaconPool: new BeaconCodePool()
        );

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal("IFR", state.FlightPlan.FlightRules);
        Assert.Equal("KOAK", state.FlightPlan.Destination);
    }

    [Fact]
    public void GenerateOnFinal_Ifr_PrimaryAirportAlreadyIcao_PassesThrough()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("Skipped: NavData not available");
            return;
        }

        var (state, error) = AircraftGenerator.Generate(
            request: MakeOnFinalRequest(FlightRulesKind.Ifr),
            primaryAirportId: "KOAK",
            existingAircraft: [],
            groundLayout: null,
            rng: new Random(42),
            beaconPool: new BeaconCodePool()
        );

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal("KOAK", state.FlightPlan.Destination);
    }

    [Fact]
    public void GenerateOnFinal_Vfr_LeavesDestinationEmpty()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("Skipped: NavData not available");
            return;
        }

        // Use Small/Piston for a valid VFR combination.
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Vfr,
            Weight = WeightClass.Small,
            Engine = EngineKind.Piston,
            PositionType = SpawnPositionType.OnFinal,
            RunwayId = "30",
            FinalDistanceNm = 30,
        };

        var (state, error) = AircraftGenerator.Generate(
            request,
            primaryAirportId: "OAK",
            existingAircraft: [],
            groundLayout: null,
            rng: new Random(42),
            beaconPool: new BeaconCodePool()
        );

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal("VFR", state.FlightPlan.FlightRules);
        Assert.Equal("", state.FlightPlan.Destination);
    }
}
