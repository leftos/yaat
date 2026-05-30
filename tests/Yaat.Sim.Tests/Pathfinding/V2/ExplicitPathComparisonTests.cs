using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Explicit-path V1↔V2 comparison over real named taxi instructions — the analogue of the auto-route
/// PathfinderGrid comparison, which only diffs <see cref="ITaxiPathfinder.FindRoute"/>. Both pathfinders
/// run on the same graph (default / Legacy fillets), so the comparison is apples-to-apples; the contract
/// is that V2 must be no zig-zaggier than V1 — <c>V2 U-turns ≤ V1 U-turns</c> — on every instructed
/// sequence it can resolve. The sequences mirror <c>TaxiRouteCatalog</c>-style departure instructions.
/// (Codex/Cursor replacement-gate: "expand comparison tests to explicit-path routes, not only auto
/// FindRoute".) The marquee issue-165 SKW3404 sequence is compared in
/// <c>SegmentExpanderTests.Issue165_V2_SkwRoute_ResolvesWithoutFailure</c> from its real spawn node.
/// </summary>
public class ExplicitPathComparisonTests(ITestOutputHelper output)
{
    private static readonly TestAirportGroundData GroundData = new();

    [Theory]
    [InlineData("OAK", "W", "30")]
    [InlineData("OAK", "K W", "28L")]
    [InlineData("SFO", "B M1", "1L")]
    public void ExplicitRoute_V2_NoWorseThanV1_OnUTurns(string airport, string path, string destRunway)
    {
        TestVnasData.EnsureInitialized();

        var layout = GroundData.GetLayout(airport);
        if (layout is null)
        {
            return; // geojson absent — silent skip per the house rule.
        }

        var waypoints = path.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int? start = FindFarStartOnTaxiway(layout, waypoints[0], destRunway);
        if (start is null)
        {
            output.WriteLine($"{airport} [{path}]: no node on taxiway {waypoints[0]} — skipping");
            return;
        }

        var options = new ExplicitPathOptions { AirportId = airport, DestinationRunway = destRunway };
        var cmp = PathfinderComparison.CompareExplicit(
            new TaxiPathfinderV1Adapter(),
            new TaxiPathfinderV2(),
            layout,
            start.Value,
            waypoints,
            options
        );
        output.WriteLine($"{airport} [{path}] from #{start}: " + PathfinderComparison.FormatReport(cmp));

        if (cmp.V1FailReason is not null)
        {
            // V1 cannot resolve from this derived start — there is nothing to compare V2 against.
            return;
        }

        Assert.Null(cmp.V2FailReason);
        Assert.True(cmp.V2UTurnCount <= cmp.V1UTurnCount, $"{airport} [{path}]: V2 U-turns ({cmp.V2UTurnCount}) must be ≤ V1 ({cmp.V1UTurnCount}).");
    }

    /// <summary>
    /// Deterministic start node for an explicit comparison: the node on the first instructed taxiway
    /// farthest from the destination runway's hold-short bars (a natural full-length departure start).
    /// The same node is used for V1 and V2, so route-realism of the start is irrelevant — only that both
    /// pathfinders see identical inputs. Falls back to the lowest-id node on the taxiway when the runway
    /// is unknown.
    /// </summary>
    private static int? FindFarStartOnTaxiway(AirportGroundLayout layout, string firstTaxiway, string destRunway)
    {
        var onTaxiway = layout.Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway(firstTaxiway))).ToList();
        if (onTaxiway.Count == 0)
        {
            return null;
        }

        var bars = layout.GetRunwayHoldShortNodes(destRunway);
        if (bars.Count > 0)
        {
            var target = bars[0].Position;
            return onTaxiway.OrderByDescending(n => GeoMath.DistanceNm(n.Position, target)).First().Id;
        }

        return onTaxiway.OrderBy(n => n.Id).First().Id;
    }
}
