using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// KOAK multi-runway-crossing taxi: a departure at parking JSX1 cleared
/// <c>RWY 30 TAXI C B W HS 28R</c> must build a route all the way to runway 30. Taxiway B crosses
/// <em>both</em> parallels (28R then 28L), so the route holds short of 28R (the named hold-short),
/// carries 28L as a further en-route crossing, and ends at runway 30 — reached <em>across</em> the
/// crossings, not truncated at the first one. <c>CROSS 28R</c> then clears the first crossing and the
/// aircraft continues; <c>RES CROSS 28L</c> clears the second so it proceeds to runway 30.
///
/// The route must stay on the cleared taxiways (C, B, W and their numbered connectors / RAMP) — it
/// may NOT detour onto taxiway A, which the controller did not name.
///
/// Uses the OAK practical-exam recording purely as the source of the real OAK layout; JSX1 is
/// node 604. This is a pathfinder-level assertion (the route the clearance produces); the CROSS
/// continuation across an intermediate crossing is the existing behavior exercised elsewhere.
/// </summary>
public sealed class OakMultiCrossingTaxiTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-cross-dest-runway-recording.yaat-bug-report-bundle.zip";

    [Fact]
    public void Taxi_FromJsx1_RoutesAcrossBothParallels_ToRunway30()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        var layout = archive.ReadLayout("oak");
        var category = AircraftCategorization.Categorize("E45X");

        var jsx1 = layout.FindParkingByName("JSX1");
        Assert.NotNull(jsx1);
        output.WriteLine($"JSX1 = node {jsx1.Id}");

        var opts = new ExplicitPathOptions
        {
            DestinationRunway = "30",
            ExplicitHoldShorts = ["28R"],
            AirportId = "OAK",
            DiagnosticLog = m => output.WriteLine("  " + m),
        };
        var route = TaxiPathfinder.ResolveExplicitPath(layout, jsx1.Id, ["C", "B", "W"], out var fail, opts, category);

        output.WriteLine($"failReason={fail ?? "(null)"}");
        Assert.NotNull(route);

        int terminalNodeId = route.Segments[^1].ToNodeId;
        var terminalNode = layout.Nodes[terminalNodeId];
        output.WriteLine($"route segs={route.Segments.Count} terminal={terminalNodeId} ({terminalNode.Type} rwy={terminalNode.RunwayId})");
        foreach (var p in route.HoldShortPoints)
        {
            output.WriteLine($"  HSP node={p.NodeId} target={p.TargetName} reason={p.Reason}");
        }

        // Reaches runway 30 (its lineup hold-short).
        Assert.Equal(GroundNodeType.RunwayHoldShort, terminalNode.Type);
        Assert.True(terminalNode.RunwayId?.Contains("30") ?? false, $"route should end at a runway-30 hold-short, ended at {terminalNode.RunwayId}");

        // Both parallels are en-route hold-shorts: 28R (explicitly named) and 28L (an implicit crossing).
        var hs28R = route.HoldShortPoints.FirstOrDefault(p => p.TargetName is { } n && RunwayIdentifier.Parse(n).Contains("28R"));
        Assert.NotNull(hs28R);
        Assert.NotEqual(terminalNodeId, hs28R.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs28R.Reason);

        var hs28L = route.HoldShortPoints.FirstOrDefault(p => p.TargetName is { } n && RunwayIdentifier.Parse(n).Contains("28L"));
        Assert.NotNull(hs28L);
        Assert.NotEqual(terminalNodeId, hs28L.NodeId);

        // Must not detour onto the unnamed letter taxiway A.
        Assert.DoesNotContain(route.Segments, s => s.TaxiwayName.Equals("A", StringComparison.OrdinalIgnoreCase));
    }
}
