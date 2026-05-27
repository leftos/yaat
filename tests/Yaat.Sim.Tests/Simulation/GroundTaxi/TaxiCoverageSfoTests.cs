using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// SFO mirror of <see cref="TaxiCoverageOakTests"/>. Same harness; pair list
/// comes from <see cref="TaxiCoverageData.SfoSmoke"/>. Kept as a separate
/// class so per-airport test discovery and failure attribution stay clean,
/// and so the (slower) SFO layout cache only loads when SFO tests actually
/// run.
/// </summary>
public class TaxiCoverageSfoTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Pairs() => TaxiCoverageData.SfoSmoke.Select(p => new object[] { p.PairId, p });

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
