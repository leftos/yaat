using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Per-PR smoke set: every <see cref="TaxiCoverageData.OakSmoke"/> pair must
/// reach its destination within the time + cumulative-turn budgets derived by
/// <see cref="TaxiBudgetDeriver"/>, and never sit unmoving for an unreasonable
/// stretch outside a hold-short / runway-crossing / parked phase.
///
/// This is the breadth-coverage net for the "aircraft spins in a corner" bug
/// class. Existing recording-based regression tests
/// (<see cref="OakNorthFieldTaxiSpinTests"/> and friends) pin specific past
/// bugs from real bundles; this suite tries to catch the next one
/// preventively by sweeping curated parking-to-runway and parking-to-parking
/// pairs across the field.
/// </summary>
public class TaxiCoverageOakTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Pairs() => TaxiCoverageData.OakSmoke.Select(p => new object[] { p.PairId, p });

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pair_ReachesDestinationWithinBudgets(string pairId, TaxiPair pair)
    {
        _ = pairId;
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine($"SKIP {pair.PairId}: NavigationDb not initialized");
            return;
        }

        var layout = new TestAirportGroundData().GetLayout(pair.AirportId);
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
                $"SKIP {pair.PairId}: destination '{pair.DestinationName}' ({pair.DestinationKind}) not found in {pair.AirportId} layout"
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
            output.WriteLine($"SKIP {pair.PairId}: origin '{pair.OriginName}' ({pair.OriginKind}) not found in {pair.AirportId} layout");
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

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }
}
