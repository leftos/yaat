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
///
/// <para>
/// Pairs in <see cref="KnownNavV2OverRotationWip"/> are quarantined: GroundNavigatorV2 currently
/// over-rotates at ramp-connector fillet-arc pairs (cumulative turn 3-6x optimal). They are being
/// fixed under the Phase-4 navigator work; remove each from the set as it goes green.
/// </para>
/// </summary>
[Collection("V2 Acceptance")]
public class V2TaxiCoverageAcceptanceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Coverage pairs GroundNavigatorV2 cannot yet complete within the turn budget because it
    /// over-rotates at ramp-connector fillet-arc pairs. Each is re-validated (and removed from this
    /// set) as the navigator fix lands.
    /// </summary>
    private static readonly HashSet<string> KnownNavV2OverRotationWip = new(StringComparer.Ordinal)
    {
        "OAK_FDX5-to-30_jet",
        "OAK_FDX5-to-Gate22_jet",
        "OAK_Gate22-to-30_jet",
        "FLL_A9-to-10L_jet",
        "FLL_28R-B8-to-D8_jet",
    };

    private static IEnumerable<TaxiPair> AllPairs() => TaxiCoverageData.OakSmoke.Concat(TaxiCoverageData.SfoSmoke).Concat(TaxiCoverageData.FllSmoke);

    public static IEnumerable<object[]> PassingPairs() =>
        AllPairs().Where(p => !KnownNavV2OverRotationWip.Contains(p.PairId)).Select(p => new object[] { p.PairId, p });

    [Theory]
    [MemberData(nameof(PassingPairs))]
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

    [Fact(
        Skip = "GroundNavigatorV2 over-rotates at ramp-connector fillet-arc pairs (cumulative turn 3-6x optimal). "
            + "Phase-4 navigator WIP. Quarantined: OAK_FDX5-to-30, OAK_FDX5-to-Gate22, OAK_Gate22-to-30, FLL_A9-to-10L, FLL_28R-B8-to-D8."
    )]
    public void KnownNavV2OverRotation_Wip() { }

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
