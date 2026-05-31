using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.V2Acceptance;

/// <summary>
/// Regression for the V2-navigator pure-pursuit orbit. SFO's curved cargo/GA ramp taxiways (CG, SIG)
/// are represented on the V2 fillet graph as chains of very short (15–17 ft) straight chord segments
/// with ~20° bends. A jet carrying taxi speed into such a cluster overshot a chord's to-node and then
/// circled it at ~2–3 kt for ~70 s — a limit cycle the budget guard could only flag as "too slow".
///
/// <para>
/// Two layers protect against it now and both are exercised here (full V2 stack via
/// <see cref="V2AcceptanceFixture"/> + <see cref="FilletMode.Standard"/>): (1) the navigator advances to the
/// next segment once the aircraft's along-track projection passes the to-node instead of circling it,
/// and (2) the orbit invariant in <c>GroundNavigator.Tick</c> hard-fails (in tests) if a single
/// segment ever accumulates 360° of net turn. These short ramp→10L routes were the worst offenders
/// (CG3→10L ran 170 s / x1.52 over budget before the fix); they must now arrive within budget.
/// </para>
/// </summary>
[Collection("V2 Acceptance")]
public class SfoRampOrbitRegressionTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> OrbitPronePairs()
    {
        string[] origins = ["CG3", "CG2", "SIG4", "SIG2"];
        foreach (var origin in origins)
        {
            yield return new object[]
            {
                $"SFO_{origin}-to-10L_jet",
                new TaxiPair(
                    PairId: $"SFO_{origin}-to-10L_jet",
                    AirportId: "SFO",
                    OriginName: origin,
                    OriginKind: TaxiNodeKind.Parking,
                    DestinationName: "10L",
                    DestinationKind: TaxiNodeKind.RunwayExit,
                    DestinationRunway: "10L",
                    Category: AircraftCategory.Jet,
                    AircraftType: TaxiCoverageData.DefaultJetType
                ),
            };
        }
    }

    [Theory]
    [MemberData(nameof(OrbitPronePairs))]
    public void RampShortRoute_TaxisToRunwayWithinBudget_NoOrbit_OnAllV2(string pairId, TaxiPair pair)
    {
        _ = pairId;
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
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
            output.WriteLine($"SKIP {pair.PairId}: destination not found in SFO V2 layout");
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
            output.WriteLine($"SKIP {pair.PairId}: origin not found in SFO V2 layout");
            return;
        }

        // Run() asserts arrival within the time/turn budgets; the orbit invariant (ThrowOnOrbit, set by
        // the test module initializer) hard-fails if the navigator ever circles a node it cannot reach.
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
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Warning).InitializeSimLog();
        return new SimulationEngine(groundData);
    }
}
