using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Bug: with auto-cross runways enabled, an explicit <c>HS &lt;runway&gt;</c> in a TAXI
/// command is silently overridden — the aircraft taxies across the runway and stops on
/// the far (exit) side instead of holding short on the entry side.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts. N7LJ (LJ45) at parking SIG6 has
/// preset <c>TAXI D C B W W1 30 HS 28R</c> with spawn delay 1060s. AutoCrossRunway is
/// recorded ON at t=0. After the preset fires the route should contain exactly one 28R
/// hold-short, on the entry side, with reason ExplicitHoldShort and IsCleared=false.
///
/// Root cause: <see cref="HoldShortAnnotator.AddImplicitRunwayHoldShorts"/> adds the
/// entry-side as RunwayCrossing, then <see cref="HoldShortAnnotator.AddExplicitHoldShort"/>
/// also adds the exit-side as ExplicitHoldShort. Auto-cross clears RunwayCrossing but not
/// ExplicitHoldShort, so the entry-side gate opens and the exit-side gate stays shut.
/// </summary>
public class OakExplicitHsAutoCrossTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/66fd6538542e.zip";
    private const string Callsign = "N7LJ";
    private const int SpawnDelaySeconds = 1060;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: dump every hold-short on N7LJ's resolved route after the preset fires.
    /// Always passes — useful for tracing the bug and for regression debugging.
    /// </summary>
    [Fact]
    public void Diagnostic_LogN7ljHoldShorts()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay just past spawn so the preset TAXI has dispatched.
        engine.Replay(recording, SpawnDelaySeconds + 2);
        Assert.True(engine.Scenario!.AutoCrossRunway, "AutoCrossRunway must be ON for this scenario");

        var ac = engine.FindAircraft(Callsign);
        if (ac is null)
        {
            output.WriteLine($"{Callsign} not found at t={SpawnDelaySeconds + 2}");
            return;
        }

        var route = ac.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            output.WriteLine($"{Callsign} has no assigned taxi route at t={SpawnDelaySeconds + 2}");
            return;
        }

        output.WriteLine($"=== {Callsign} taxi route ({route.Segments.Count} segments) ===");
        output.WriteLine($"Summary: {route.ToSummary()}");
        output.WriteLine($"=== HoldShortPoints ({route.HoldShortPoints.Count}) ===");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  node={hs.NodeId} reason={hs.Reason} target={hs.TargetName} cleared={hs.IsCleared}");
        }
    }

    /// <summary>
    /// Core assertion: with auto-cross ON and an explicit <c>HS 28R</c>, N7LJ's route
    /// must end up with exactly one uncleared 28R hold-short on the entry side, marked
    /// <see cref="HoldShortReason.ExplicitHoldShort"/> so auto-cross can't silently clear it.
    /// </summary>
    [Fact]
    public void ExplicitHs28R_OverridesAutoCross_HoldsOnEntrySide()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, SpawnDelaySeconds + 2);
        Assert.True(engine.Scenario!.AutoCrossRunway, "AutoCrossRunway must be ON for this scenario");

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        // (1) Exactly one 28R hold-short — no duplicate exit-side entry.
        var holds28R = route.HoldShortPoints.Where(h => h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28R")).ToList();

        output.WriteLine($"28R hold-shorts: {holds28R.Count}");
        foreach (var h in holds28R)
        {
            output.WriteLine($"  node={h.NodeId} reason={h.Reason} cleared={h.IsCleared}");
        }

        Assert.Single(holds28R);
        var hs28R = holds28R[0];

        // (2) Reason is ExplicitHoldShort so auto-cross does not clear it.
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs28R.Reason);

        // (3) IsCleared must remain false after the auto-cross loop has run.
        Assert.False(hs28R.IsCleared, $"Explicit HS 28R should survive auto-cross (node={hs28R.NodeId})");

        // (4) Entry-side: must equal the FIRST 28R RunwayHoldShort node encountered along
        // the segments, not the second (exit-side) one.
        int? firstEntrySideNodeId = null;
        int seen28RNodes = 0;
        foreach (var seg in route.Segments)
        {
            if (
                layout.Nodes.TryGetValue(seg.ToNodeId, out var node)
                && node.Type == GroundNodeType.RunwayHoldShort
                && node.RunwayId is { } rwy
                && rwy.Contains("28R")
            )
            {
                seen28RNodes++;
                firstEntrySideNodeId ??= seg.ToNodeId;
            }
        }

        Assert.True(seen28RNodes >= 1, "Route should traverse at least one 28R hold-short node");
        Assert.NotNull(firstEntrySideNodeId);
        Assert.Equal(firstEntrySideNodeId, hs28R.NodeId);
    }
}
