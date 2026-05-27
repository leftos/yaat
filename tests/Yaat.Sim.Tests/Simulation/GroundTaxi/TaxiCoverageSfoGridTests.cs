using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Nightly grid: every SFO parking spot taxis to each of three departure
/// runways (28R, 28L, 10L) under <see cref="TaxiBudgetEvaluator"/> guards.
///
/// <para>
/// Gated with <c>[Trait("Category", "Nightly")]</c> so the per-PR CI run
/// (which uses <c>--filter "Category!=Nightly"</c>) skips this set. The
/// scheduled nightly workflow runs it with <c>--filter "Category=Nightly"</c>.
/// </para>
///
/// <para>
/// Uses B738 (jet) for every spawn — SFO is jet-dominant in real ops, and
/// the smoke suite (<see cref="TaxiCoverageSfoTests"/>) already covers
/// piston + jet on curated pairs. SFO has ~239 parking spots so this grid
/// is the larger of the two per-airport sweeps.
/// </para>
///
/// <para>
/// Pairs with no graph route from origin to destination are silently
/// skipped.
/// </para>
/// </summary>
[Trait("Category", "Nightly")]
public class TaxiCoverageSfoGridTests(ITestOutputHelper output)
{
    // 1L has no hold-short nodes in the SFO layout; using 1R as the
    // departure proxy on that direction.
    private static readonly string[] DepartureRunways = ["28R", "28L", "10L"];

    public static IEnumerable<object[]> AllParkingToRunways()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            yield break;
        }

        foreach (var parking in layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking).OrderBy(n => n.Id))
        {
            if (parking.Name is null)
            {
                continue;
            }
            foreach (var runway in DepartureRunways)
            {
                var pair = new TaxiPair(
                    PairId: $"SFO_{parking.Name}-to-{runway}_jet",
                    AirportId: "SFO",
                    OriginName: parking.Name,
                    OriginKind: TaxiNodeKind.Parking,
                    DestinationName: runway,
                    DestinationKind: TaxiNodeKind.RunwayExit,
                    DestinationRunway: runway,
                    Category: AircraftCategory.Jet,
                    AircraftType: TaxiCoverageData.DefaultJetType
                );
                yield return new object[] { pair.PairId, pair };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllParkingToRunways))]
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
            output.WriteLine($"SKIP {pair.PairId}: destination '{pair.DestinationName}' not found");
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
            output.WriteLine($"SKIP {pair.PairId}: origin '{pair.OriginName}' not found");
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
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Warning).InitializeSimLog();
        return new SimulationEngine(groundData);
    }
}
