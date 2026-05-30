using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.V2Acceptance;

/// <summary>
/// Phase-4 acceptance gate: the OAK/SFO/FLL taxi-coverage smoke pairs driven end-to-end over the
/// <em>all-V2</em> ground stack — V2 fillets (<see cref="FilletMode.V2"/>) + V2 pathfinder + V2
/// navigator (the latter two flipped by <see cref="V2AcceptanceFixture"/>). This is the permanent
/// replacement for the manual three-file source flip; it runs as part of the normal suite (in the
/// post-parallel sequential phase, so the global router flip is race-free).
///
/// <para>
/// Sibling to <see cref="FilletV2TaxiCoverageTests"/>, which exercises the SAME pairs on V2 fillets
/// but the V1 pathfinder + V1 navigator — that isolates fillet-geometry regressions; this isolates
/// the full V2 stack.
/// </para>
/// </summary>
[Collection("V2 Acceptance")]
public class V2TaxiCoverageAcceptanceTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Pairs() =>
        TaxiCoverageData.OakSmoke.Concat(TaxiCoverageData.SfoSmoke).Concat(TaxiCoverageData.FllSmoke).Select(p => new object[] { p.PairId, p });

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pair_ReachesDestinationWithinBudgets_OnAllV2(string pairId, TaxiPair pair)
    {
        _ = pairId;
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine($"SKIP {pair.PairId}: NavigationDb not initialized");
            return;
        }

        var layout = new TestAirportGroundData(FilletMode.V2).GetLayout(pair.AirportId);
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

        var groundData = new TestAirportGroundData(FilletMode.V2);
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }
}
