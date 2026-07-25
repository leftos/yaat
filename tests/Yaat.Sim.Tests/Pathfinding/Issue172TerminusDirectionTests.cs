using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Issue #172 sub-bugs #5/#8/#9 (terminus direction): the final-taxiway walk only biased toward a
/// runway <em>destination</em>, so a bare final taxiway picked an arbitrary (often wrong) direction.
///
/// Two cases:
///   * <c>UAL2164 TAXI G B</c> — B has no downstream constraint. The aircraft turned the wrong way
///     on B (controller had to HOLD + WARP it back). It must instead terminate at the pure G/B
///     intersection so the controller can turn it either way with a follow-up taxi.
///   * <c>EJA512 TAXI B K HS 10R</c> — K's 10R hold-short is the constraint, but it was wired as an
///     explicit hold-short, not a destination runway, so the walk ignored it and headed away from
///     10R (truncating the route). The walk must head toward the named hold-short.
/// </summary>
public class Issue172TerminusDirectionTests(ITestOutputHelper output)
{
    // Nodes are resolved by name, never by id: ids are geometry-coupled and renumber whenever the layout is
    // regenerated or a runway's dimensions change. SFO's G carries the 01L/19R hold-short arrivals off 19L/19R
    // hold at; K crosses 10R.

    private static AirportGroundLayout? SfoLayout(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiPathfinder", LogLevel.Debug).InitializeSimLog();
        return new TestAirportGroundData().GetLayout("SFO");
    }

    [Fact]
    public void TaxiGB_TerminatesAtGbIntersection_DoesNotWalkTaxiwayB()
    {
        var layout = SfoLayout(output);
        if (layout is null)
        {
            return;
        }

        var gHoldShort = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "01L", "G");
        var gbIntersection = layout.FindIntersectionNode("G", "B");
        if (gHoldShort is null || gbIntersection is null)
        {
            return;
        }

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: gHoldShort.Id,
            taxiwayNames: ["G", "B"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO" },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        for (int i = 0; i < route.Segments.Count; i++)
        {
            var s = route.Segments[i];
            output.WriteLine($"  [{i, 2}] {s.FromNodeId, 5} -> {s.ToNodeId, 5} ({s.TaxiwayName})");
        }

        // The route stops at the pure G/B intersection and never walks along B.
        Assert.Equal(gbIntersection.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.Segments, s => s.TaxiwayName.Equals("B", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaxiBK_HoldShort10R_WalksKTowardTheHoldShort()
    {
        var layout = SfoLayout(output);
        if (layout is null)
        {
            return;
        }

        var f1bIntersection = layout.FindIntersectionNode("F1", "B");
        var rwy10RHoldShortsOnK = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "10R", "K").Select(n => n.Id).ToHashSet();
        if (f1bIntersection is null || rwy10RHoldShortsOnK.Count == 0)
        {
            return;
        }

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: f1bIntersection.Id,
            taxiwayNames: ["B", "K"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", ExplicitHoldShorts = ["10R"] },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        output.WriteLine($"terminus={route.Segments[^1].ToNodeId} segs={route.Segments.Count}");

        // The K walk reaches the 10R/28L hold-short and stops there, rather than heading off the wrong
        // way toward K's far terminus (the original bug walked K away from the hold-short). Asserting the
        // route stops within a node of the hold-short is robust to layout renumbering.
        int lastHoldShortIdx = route.Segments.FindLastIndex(s => rwy10RHoldShortsOnK.Contains(s.ToNodeId));
        Assert.True(lastHoldShortIdx >= 0, "route never reached a 10R hold-short on K");
        Assert.True(route.Segments.Count - lastHoldShortIdx <= 2, "route walked well past the 10R hold-short the wrong way");
    }
}
