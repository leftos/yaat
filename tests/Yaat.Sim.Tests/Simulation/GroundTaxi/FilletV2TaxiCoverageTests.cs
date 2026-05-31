using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Sim-level validation gate for the fillet V2 arc generator.
///
/// Runs the OAK + SFO + FLL <see cref="TaxiCoverageData"/> smoke pairs — which
/// pass on the Legacy fillets with the production (V1) pathfinder — through the
/// same <see cref="TaxiCoverageRunner"/> harness, but on a layout filleted by the
/// V2 generator (<see cref="FilletMode.Standard"/>). The pathfinder stays on V1, so any
/// regression here isolates cleanly to V2 fillet geometry rather than routing.
///
/// The time / turn budgets are derived from the V2 layout's own optimal A* route,
/// so this is a "does an aircraft taxi the V2 graph without getting stuck or
/// wildly over-turning" gate — not a brittle Legacy-distance match. Production
/// stays on Legacy; this suite is what justifies flipping the default later
/// (see docs/plans/filletv2/v2-sim-validation.md).
/// </summary>
public class FilletV2TaxiCoverageTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Pairs() =>
        TaxiCoverageData.OakSmoke.Concat(TaxiCoverageData.SfoSmoke).Concat(TaxiCoverageData.FllSmoke).Select(p => new object[] { p.PairId, p });

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pair_ReachesDestinationWithinBudgets_OnV2Fillets(string pairId, TaxiPair pair)
    {
        _ = pairId;
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine($"SKIP {pair.PairId}: NavigationDb not initialized");
            return;
        }

        var layout = new TestAirportGroundData(FilletMode.Standard).GetLayout(pair.AirportId);
        Assert.NotNull(layout);

        var destination = TaxiCoverageRunner.ResolveNode(
            layout,
            pair.DestinationName,
            pair.DestinationKind,
            pair.DestinationRunway,
            requireForwardLineup: true
        );
        if (destination is null)
        {
            output.WriteLine(
                $"SKIP {pair.PairId}: destination '{pair.DestinationName}' ({pair.DestinationKind}) not found in {pair.AirportId} V2 layout"
            );
            return;
        }

        var origin = TaxiCoverageRunner.ResolveNode(
            layout,
            pair.OriginName,
            pair.OriginKind,
            null,
            requireForwardLineup: false,
            tieBreakerToNode: destination
        );
        if (origin is null)
        {
            output.WriteLine($"SKIP {pair.PairId}: origin '{pair.OriginName}' ({pair.OriginKind}) not found in {pair.AirportId} V2 layout");
            return;
        }

        TaxiCoverageRunner.Run(BuildEngine, pair, origin, destination, layout, output);
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData(FilletMode.Standard);
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }
}
