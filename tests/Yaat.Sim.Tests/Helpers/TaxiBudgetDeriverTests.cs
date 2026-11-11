using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// <see cref="TaxiBudgetDeriver.Derive"/> plans the pair's route with the pair's own aircraft class: a piston pair's
/// budget is derived from the route the simulator would fly for that piston, not from a hard-coded jet's route.
/// </summary>
public class TaxiBudgetDeriverTests
{
    [Fact]
    public void Derive_NonJetOakSmokePair_PlansTheRouteOfThePairsOwnClass()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));

        List<string> mismatches = [];
        int checkedPairs = 0;
        foreach (TaxiPair pair in TaxiCoverageData.OakSmoke.Where(p => p.Category != AircraftCategory.Jet))
        {
            GroundNode? destination = TaxiCoverageRunner.ResolveNode(
                layout,
                pair.DestinationName,
                pair.DestinationKind,
                pair.DestinationRunway,
                requireForwardLineup: true
            );
            if (destination is null)
            {
                continue;
            }

            GroundNode? origin = TaxiCoverageRunner.ResolveNode(
                layout,
                pair.OriginName,
                pair.OriginKind,
                null,
                requireForwardLineup: false,
                tieBreakerToNode: destination
            );
            if (origin is null)
            {
                continue;
            }

            var taxiClass = new TaxiClass(pair.Category, WakeTurbulenceData.WakeClassForType(pair.AircraftType, pair.Category));
            TaxiRoute? expected = TaxiPathfinder.FindRoute(layout, origin.Id, destination.Id, taxiClass.Category, taxiClass.Wake);
            if (expected is null)
            {
                mismatches.Add($"{pair.PairId}: no {pair.Category} route from #{origin.Id} to #{destination.Id}");
                continue;
            }

            TaxiRoute planned = TaxiBudgetDeriver.Derive(layout, origin.Id, destination.Id, taxiClass).PlannedRoute;
            checkedPairs++;
            string plannedIds = NodeIds(planned);
            string expectedIds = NodeIds(expected);
            if (!string.Equals(plannedIds, expectedIds, StringComparison.Ordinal))
            {
                mismatches.Add($"{pair.PairId}: planned [{plannedIds}] but the {pair.Category} route is [{expectedIds}]");
            }
        }

        Assert.True(checkedPairs > 0, "no non-Jet OAK smoke pair resolved both endpoints, so the test proved nothing");
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static string NodeIds(TaxiRoute route) =>
        $"{route.Segments[0].FromNodeId} " + string.Join(" ", route.Segments.Select(segment => segment.ToNodeId));
}
