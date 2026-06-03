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
    // SFO: node 867 = 01L/19R hold-short on taxiway G (arrival exit); the pure (pre-fillet) G/B
    // intersection is node 155 (tangent-cut fillet nodes 1395-1398 surround it).
    private const int GHoldShortNode = 867;
    private const int GbIntersection = 155;

    // SFO: node 152 = pure F1/B intersection; K crosses 10R/28L at hold-short nodes 849 and 857,
    // and 10L/28R at 830/831. Node 46 is K's far (south) terminus — the wrong-way stop.
    private const int F1bIntersection = 152;
    private static readonly int[] Rwy10RHoldShortsOnK = [849, 857];
    private const int KSouthTerminus = 46;

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

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: GHoldShortNode,
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
        Assert.Equal(GbIntersection, route.Segments[^1].ToNodeId);
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

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: F1bIntersection,
            taxiwayNames: ["B", "K"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", ExplicitHoldShorts = ["10R"] },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        output.WriteLine($"terminus={route.Segments[^1].ToNodeId} segs={route.Segments.Count}");

        // The K walk reaches the 10R/28L hold-short instead of heading to K's far (south) terminus.
        Assert.Contains(route.Segments, s => Rwy10RHoldShortsOnK.Contains(s.ToNodeId));
        Assert.DoesNotContain(route.Segments, s => s.ToNodeId == KSouthTerminus);
    }
}
