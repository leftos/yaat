using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #398: a <c>HS $spot</c> whose name collides with a runway designator must not corrupt
/// that runway's crossing on the same route.
///
/// <para>
/// <c>RouteMaterialiser.MatchesExplicitHoldShort</c> tests each explicit hold-short target against a
/// runway bar with a bare <c>runwayId.Contains(target.Target)</c> — with no <c>IsSpot</c> guard, unlike
/// its two sibling matchers (<c>FindBoundHoldShort</c> and <c>HoldShortAnnotator.TargetMatches</c>),
/// which were guarded in the #394 spot-hold-short change. A spot named for a number (the <c>$</c> sigil is
/// stripped at parse, so <c>HS $9</c> → target <c>"9"</c>) therefore matches a runway whose end is that
/// number, and the crossing is mislabeled <see cref="HoldShortReason.ExplicitHoldShort"/> instead of
/// <see cref="HoldShortReason.RunwayCrossing"/>. Consequence: AutoCross (which only auto-clears
/// RunwayCrossing) leaves the aircraft stopped at a runway the controller never named, and the readback
/// echoes a phantom "hold short of runway 9".
/// </para>
///
/// <para>
/// Reproduced on the committed IAH layout, which has an unpaired runway <c>9/27</c> crossed by taxiway
/// SK and a taxi spot literally named "9". SFO (the #394 fixture) has only L/R-paired runways, so its
/// numeric spots never collide — which is why the existing tests miss this.
/// </para>
/// </summary>
public class Issue398SpotHoldShortRunwayCollisionTests(ITestOutputHelper output)
{
    private static AirportGroundLayout? LoadIah()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("IAH");
    }

    [Fact]
    public void TaxiCrossingRunway9_WithHsSpot9_KeepsRunwayCrossingReason()
    {
        var layout = LoadIah();
        if (layout is null)
        {
            return;
        }

        // The 9/27 hold-short on SK, found by name (ids renumber with geometry).
        var holdShort = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "9", "SK");
        Assert.NotNull(holdShort);

        // Confirm the collision precondition: the airport really has a spot literally named "9".
        Assert.NotNull(layout.FindSpotNodeByName("9"));

        // The two SK arms at the bar: one continues across the runway centerline, the other is the
        // taxiway approach. Start on the approach side and route across so 9/27 is a *crossing*.
        GroundNode? approachSide = null;
        GroundNode? runwaySide = null;
        foreach (var edge in holdShort.Edges)
        {
            if (!edge.MatchesTaxiway("SK"))
            {
                continue;
            }

            var neighbor = edge.OtherNode(holdShort);
            if (neighbor.Edges.Any(e => e.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase)))
            {
                runwaySide = neighbor;
            }
            else
            {
                approachSide = neighbor;
            }
        }

        Assert.NotNull(approachSide);
        Assert.NotNull(runwaySide);

        // The SK node on the far side of the runway (a plain SK edge off the runway-side node, not the bar).
        GroundNode? destAcross = null;
        foreach (var edge in runwaySide.Edges)
        {
            if (!edge.MatchesTaxiway("SK") || edge.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var neighbor = edge.OtherNode(runwaySide);
            if (neighbor.Id != holdShort.Id)
            {
                destAcross = neighbor;
            }
        }

        Assert.NotNull(destAcross);

        TaxiRoute? Resolve(string? holdShortTarget)
        {
            var options = new ExplicitPathOptions
            {
                ExplicitHoldShorts = holdShortTarget is null ? null : [HoldShortTarget.Parse(holdShortTarget)],
                DestinationHintNode = destAcross,
            };
            var route = TaxiPathfinder.ResolveExplicitPath(layout, approachSide.Id, ["SK"], out string? fail, options, AircraftCategory.Jet);
            Assert.True(route is not null, $"pathfinder failed: {fail}");
            return route;
        }

        HoldShortPoint RunwayPoint(TaxiRoute route) => Assert.Single(route.HoldShortPoints, h => h.NodeId == holdShort.Id);

        // Baseline: a plain crossing (no explicit hold-short) is a RunwayCrossing.
        var baseline = Resolve(null)!;
        Assert.Equal(HoldShortReason.RunwayCrossing, RunwayPoint(baseline).Reason);

        // Control: HS $8 — IAH has no runway 8, so the crossing stays a RunwayCrossing.
        var control = Resolve("$8")!;
        Assert.Equal(HoldShortReason.RunwayCrossing, RunwayPoint(control).Reason);

        // The bug: HS $9 collides with runway 9's end. The 9/27 crossing must still be a plain
        // RunwayCrossing (the spot hold-short belongs to the spot node, not this runway bar).
        var colliding = Resolve("$9")!;
        var runwayPoint = RunwayPoint(colliding);
        output.WriteLine($"9/27 crossing with HS $9: reason={runwayPoint.Reason} target={runwayPoint.TargetName}");
        Assert.Equal(HoldShortReason.RunwayCrossing, runwayPoint.Reason);
    }
}
