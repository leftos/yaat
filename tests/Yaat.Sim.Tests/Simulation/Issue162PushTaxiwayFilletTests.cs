using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #162: <c>PUSH T5B</c> on DAL451 (B739) at SFO
/// stopped with the aircraft center on the curved fillet connecting the
/// parking ramp to T5B, leaving the tail just barely over the visible T5B
/// line instead of placing the center on a straight T5B segment.
///
/// Root cause: <see cref="AirportGroundLayout.FindExitByTaxiway"/> iterates
/// <see cref="GroundNode.Edges"/> (a mixed list of <see cref="GroundEdge"/>
/// and <see cref="GroundArc"/>) and treats them identically. A node whose
/// only T5B-named connections are arcs at the fillet entry from the ramp
/// matches, so pushback stops there instead of on a straight T5B edge.
///
/// Recording: <c>S1-SFO-4 | FD/CD/GC 19/10</c>. DAL451 spawns at
/// <c>(37.615525, -122.382720)</c>; <c>PUSH T5B</c> fires at t=101; the
/// pushback phase completes by t=120.
/// </summary>
public class Issue162PushTaxiwayFilletTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue162-push-t5b-fillet-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Pure layout test: <c>FindExitByTaxiway</c> called from DAL451's spawn
    /// position must return a node that has at least one <em>straight</em>
    /// <see cref="GroundEdge"/> labeled T5B — not a node whose only T5B
    /// connections are <see cref="GroundArc"/> fillets.
    /// </summary>
    [Fact]
    public void FindExitByTaxiway_PrefersNodeWithStraightTaxiwayEdge()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var fromPos = new LatLon(37.615525, -122.382720);
        var target = layout.FindExitByTaxiway(fromPos, "T5B");
        Assert.NotNull(target);

        bool hasStraightT5B = target.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway("T5B") && !e.IsRunwayCenterline);

        string edgeKinds = string.Join(", ", target.Edges.Select(e => $"{(e is GroundArc ? "arc" : "edge")}[{e.TaxiwayName}]"));
        output.WriteLine($"FindExitByTaxiway returned node #{target.Id} edges=[{edgeKinds}]");

        Assert.True(
            hasStraightT5B,
            $"Returned node #{target.Id} has no straight T5B GroundEdge — only arcs. " + "Pushback would stop on the fillet from the ramp into T5B."
        );
    }

    /// <summary>
    /// E2E replay: at t=125 (5s after the push-back phase completes) DAL451's
    /// nearest node must have a straight T5B <see cref="GroundEdge"/>. The
    /// buggy behavior parked the aircraft center on node #2161, whose only
    /// T5B-named connections are arcs.
    /// </summary>
    [Fact]
    public void DAL451_PushT5B_EndsNearStraightT5BEdge()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 125);

        var ac = engine.FindAircraft("DAL451");
        Assert.NotNull(ac);

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        NearestNodeHelper.Log(output, "t=125 DAL451:", ac, layout, count: 5);

        var nearest = layout.Nodes.Values.OrderBy(n => GeoMath.DistanceNm(ac.Position, n.Position)).First();

        bool onStraightT5B = nearest.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway("T5B"));

        string edgeKinds = string.Join(", ", nearest.Edges.Select(e => $"{(e is GroundArc ? "arc" : "edge")}[{e.TaxiwayName}]"));
        output.WriteLine($"DAL451 final pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) " + $"nearest=#{nearest.Id} edges=[{edgeKinds}]");

        Assert.True(
            onStraightT5B,
            $"DAL451 ended nearest node #{nearest.Id}, which has no straight T5B GroundEdge — "
                + "pushback stopped on a fillet arc instead of T5B proper."
        );
    }
}
