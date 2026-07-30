using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// GitHub issue #316: a departure holding short of 28L on taxiway F, re-routed with
/// <c>TAXI F C HS 10R RWY 28R</c>, taxied straight across 28L — with another aircraft lined up and
/// waiting on it — and only then stopped, at the hold-short bar on the far (Charlie) side.
///
/// SFO is the shape that breaks the pairing: F (south of 28L) and C (north of 28L) meet each other
/// exactly on the 28L centerline, so the two bars of that one crossing carry different taxiway names.
/// The route's entry/exit pairing walked forward only while the taxiway name matched the start
/// taxiway, so it never found the C-side bar as the exit half of the crossing the aircraft was
/// already parked at, and annotated that far bar as a fresh crossing entry instead.
///
/// A hold-short must always bind to the bar on the side the aircraft approaches from — including
/// when the aircraft is already standing on it.
/// </summary>
public class Issue316NearSideHoldShortTests(ITestOutputHelper output)
{
    /// <summary>
    /// Resolves the near (Foxtrot) and far (Charlie) 10R/28L bars by taxiway membership. Ids are
    /// geometry-coupled and renumber on every layout regeneration, so they are never hardcoded.
    /// </summary>
    private static (GroundNode Near, GroundNode Far)? ResolveCrossingBars(AirportGroundLayout layout)
    {
        var near = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "F");
        var far = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "C");
        return (near is null) || (far is null) ? null : (near, far);
    }

    private static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("SFO");
    }

    [Fact]
    public void TaxiFC_HoldShort10R_FromTheFoxtrotBar_HoldsOnTheNearSide()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var bars = ResolveCrossingBars(layout);
        if (bars is not { } crossing)
        {
            return;
        }

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: crossing.Near.Id,
            taxiwayNames: ["F", "C"],
            out string? failReason,
            new ExplicitPathOptions
            {
                AirportId = "SFO",
                ExplicitHoldShorts = ["10R"],
                DestinationRunway = "28R",
            },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        output.WriteLine($"near=#{crossing.Near.Id} far=#{crossing.Far.Id} summary={route.ToSummary()}");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  hold-short #{hs.NodeId} {hs.TargetName} {hs.Reason}");
        }

        var explicitHold = Assert.Single(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.Equal(crossing.Near.Id, explicitHold.NodeId);
        Assert.DoesNotContain(route.HoldShortPoints, hs => hs.NodeId == crossing.Far.Id);
    }

    [Fact]
    public void TaxiFC_NoHoldShort_FromTheFoxtrotBar_MarksTheCrossingOnTheNearSide()
    {
        // Same geometry without the HS token. The crossing is still annotated on the near side; it is
        // GroundCommandHandler's implicit first-crossing clearance that lets the aircraft go, and that
        // only ever clears a RunwayCrossing point — so the side must be right here too.
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var bars = ResolveCrossingBars(layout);
        if (bars is not { } crossing)
        {
            return;
        }

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: crossing.Near.Id,
            taxiwayNames: ["F", "C"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DestinationRunway = "28R" },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        output.WriteLine($"near=#{crossing.Near.Id} far=#{crossing.Far.Id} summary={route.ToSummary()}");

        var crossingHold = Assert.Single(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.RunwayCrossing);
        Assert.Equal(crossing.Near.Id, crossingHold.NodeId);
        Assert.DoesNotContain(route.HoldShortPoints, hs => hs.NodeId == crossing.Far.Id);
    }

    [Fact]
    public void ImplicitAnnotator_FromTheFoxtrotBar_NeverMarksTheFarSideBar()
    {
        // HoldShortAnnotator.AddImplicitRunwayHoldShorts is the older twin of the materialiser's
        // annotation pass (it runs over an appended parking extension and over a runway-exit route).
        // It pairs crossings the same way and must not mistake the exit-side bar for a new entry
        // when the crossing straddles two taxiway names.
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var bars = ResolveCrossingBars(layout);
        if (bars is not { } crossing)
        {
            return;
        }

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: crossing.Near.Id,
            taxiwayNames: ["F", "C"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DestinationRunway = "28R" },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        var holdShorts = new List<HoldShortPoint>();
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, route.Segments, holdShorts);
        foreach (var hs in holdShorts)
        {
            output.WriteLine($"  hold-short #{hs.NodeId} {hs.TargetName} {hs.Reason}");
        }

        Assert.DoesNotContain(holdShorts, hs => hs.NodeId == crossing.Far.Id);
    }

    [Fact]
    public void TaxiFC_HoldShort10R_FromFurtherBackOnFoxtrot_StillHoldsOnTheNearSide()
    {
        // The near bar is a mid-route node here rather than the start node, which already worked.
        // Pinned so the fix to the start-node case cannot regress it.
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var bars = ResolveCrossingBars(layout);
        if (bars is not { } crossing)
        {
            return;
        }

        // One hop back along F, away from the runway.
        var backOnF = crossing
            .Near.Edges.Where(e => string.Equals(e.TaxiwayName, "F", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.OtherNode(crossing.Near))
            .OrderByDescending(n => GeoMath.DistanceNm(n.Position, crossing.Far.Position))
            .FirstOrDefault();
        if (backOnF is null)
        {
            return;
        }

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: backOnF.Id,
            taxiwayNames: ["F", "C"],
            out string? failReason,
            new ExplicitPathOptions
            {
                AirportId = "SFO",
                ExplicitHoldShorts = ["10R"],
                DestinationRunway = "28R",
            },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        output.WriteLine($"from=#{backOnF.Id} summary={route.ToSummary()}");

        var explicitHold = Assert.Single(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.Equal(crossing.Near.Id, explicitHold.NodeId);
        Assert.DoesNotContain(route.HoldShortPoints, hs => hs.NodeId == crossing.Far.Id);
    }
}
