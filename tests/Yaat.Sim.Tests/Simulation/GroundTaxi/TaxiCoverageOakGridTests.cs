using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Nightly grid: every OAK parking spot taxis to each of three departure
/// runways (28R, 28L, 30) under <see cref="TaxiBudgetEvaluator"/> guards.
///
/// <para>
/// Gated with <c>[Trait("Category", "Nightly")]</c> so the per-PR CI run
/// (which uses <c>--filter "Category!=Nightly"</c>) skips this set. The
/// scheduled nightly workflow runs it with <c>--filter "Category=Nightly"</c>.
/// </para>
///
/// <para>
/// The grid uses C172 (piston) for every spawn. The smoke suite
/// (<see cref="TaxiCoverageOakTests"/>) already covers mixed piston / jet
/// categories on curated pairs; the grid's job is route-coverage breadth.
/// </para>
///
/// <para>
/// Pairs with no graph route from origin to destination are silently
/// skipped — same convention as <c>OakAllParkingTaxiAutoTests</c>.
/// </para>
/// </summary>
[Trait("Category", "Nightly")]
public class TaxiCoverageOakGridTests(ITestOutputHelper output)
{
    private static readonly string[] DepartureRunways = ["28R", "28L", "30"];

    public static IEnumerable<object[]> AllParkingToRunways()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("OAK");
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
                    PairId: $"OAK_{parking.Name}-to-{runway}_piston",
                    AirportId: "OAK",
                    OriginName: parking.Name,
                    OriginKind: TaxiNodeKind.Parking,
                    DestinationName: runway,
                    DestinationKind: TaxiNodeKind.RunwayExit,
                    DestinationRunway: runway,
                    Category: AircraftCategory.Piston,
                    AircraftType: TaxiCoverageData.DefaultPistonType
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
        // Grid tests run quietly: a few hundred cases per airport, mostly
        // green. Per-case GroundCommandHandler info noise would balloon the
        // CI log. Restrict to Warning so genuine failures still surface.
        var logBuilder = SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Warning);
        // When YAAT_TAXI_TICK_RECORD is set (see TaxiCoverageRunner), also
        // enable navigator/phase debug logging so the captured run includes
        // internal navigator transitions for diagnosis.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YAAT_TAXI_TICK_RECORD")))
        {
            logBuilder = logBuilder
                .EnableCategory("GroundNavigator", LogLevel.Debug)
                .EnableCategory("TaxiingPhase", LogLevel.Debug)
                .EnableCategory("GroundCommandHandler", LogLevel.Debug);
        }
        logBuilder.InitializeSimLog();
        return new SimulationEngine(groundData);
    }
}
