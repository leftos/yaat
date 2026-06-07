using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Recording fixture: ZMA "T1: S2 Practical Exam (MIA East)". THY41J (B789, KMIA→LTFM)
/// is a delayed-spawn departure manually spawned at t=91 via a recorded SPAWN command,
/// with preset <c>RWY 9 TAXI P S HS 12</c>. It taxis P/S toward runway 9, holding short of
/// runway 12 (which taxiway S crosses en route). At t=229 the controller issues <c>CROSS 12</c>.
///
/// Two behaviors are covered:
///
/// 1. <b>Manual spawn must replay.</b> <c>SimulationEngine.ReplayCommand</c> gated
///    <c>SpawnNow</c>/<c>SpawnDelay</c> behind the <c>FindAircraft is null</c> guard, but
///    those act on the delayed-spawn queue (the aircraft is intentionally not active yet),
///    so every recorded manual spawn was silently dropped on replay and on server-driven
///    snapshot regeneration. THY41J therefore never appeared in this bundle's snapshots.
///    With the fix it spawns during replay.
///
/// 2. <b>Taxi to a destination runway across an intermediate runway crossing.</b> Taxiway S
///    runs the full length parallel to runway 09/27 and crosses the diagonal runway 12/30
///    en route, so <c>RWY 9 TAXI P S HS 12</c> must build a route all the way to runway 9
///    with runway 12 marked as an en-route hold-short — <c>HS 12</c> is a hold/authorization
///    marker, not a routing terminus. <c>CROSS 12</c> then clears that crossing and the
///    aircraft continues toward runway 9 instead of stopping past the runway-12 bars.
/// </summary>
public sealed class MiaSpawnReplayCrossTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/mia-spawn-replay-cross12-recording.zip";
    private const string Callsign = "THY41J";

    [Fact]
    public void RecordedManualSpawn_ReplaysIntoActiveWorld()
    {
        var setup = BuildReplay();
        if (setup is null)
        {
            return;
        }

        using var archive = setup.Value.Archive;
        var engine = setup.Value.Engine;

        // The SPAWN command is recorded at t=91; before then THY41J sits in the delayed queue.
        for (int t = 1; t <= 95; t++)
        {
            engine.ReplayOneSecond();
        }

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.DoesNotContain(engine.Scenario!.DelayedQueue, e => e.Aircraft.State.Callsign == Callsign);
        output.WriteLine($"{Callsign} active after replaying past the recorded SPAWN at t=91");
    }

    [Fact]
    public void Taxi_RoutesAcrossRunway12_ToDestinationRunway9()
    {
        var setup = BuildReplay();
        if (setup is null)
        {
            return;
        }

        using var archive = setup.Value.Archive;
        var engine = setup.Value.Engine;
        var layout = setup.Value.Layout;

        // Replay just past the manual spawn so the RWY 9 TAXI P S HS 12 preset has resolved.
        for (int t = 1; t <= 96; t++)
        {
            engine.ReplayOneSecond();
        }

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        int terminalNodeId = route.Segments[^1].ToNodeId;
        var terminalNode = layout.Nodes[terminalNodeId];
        output.WriteLine($"route segs={route.Segments.Count} terminal={terminalNodeId} ({terminalNode.Type} rwy={terminalNode.RunwayId})");
        foreach (var p in route.HoldShortPoints)
        {
            output.WriteLine($"  HSP node={p.NodeId} target={p.TargetName} reason={p.Reason}");
        }

        // The route must reach runway 9 (its lineup hold-short), not stop at the runway-12 crossing.
        Assert.Equal(GroundNodeType.RunwayHoldShort, terminalNode.Type);
        Assert.True(terminalNode.RunwayId?.Contains("9") ?? false, $"route should end at a runway-9 hold-short, ended at {terminalNode.RunwayId}");

        // Runway 12 must appear as an en-route (non-terminal) hold-short, marked from HS 12.
        var twelve = route.HoldShortPoints.FirstOrDefault(p => p.TargetName is { } n && RunwayIdentifier.Parse(n).Contains("12"));
        Assert.NotNull(twelve);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, twelve.Reason);
        Assert.NotEqual(terminalNodeId, twelve.NodeId);
    }

    [Fact]
    public void CrossRunway12_ContinuesTowardRunway9()
    {
        var setup = BuildReplay();
        if (setup is null)
        {
            return;
        }

        using var archive = setup.Value.Archive;
        var engine = setup.Value.Engine;
        var layout = setup.Value.Layout;

        bool sawHoldingShortOf12 = false;
        bool sawCrossing = false;

        for (int t = 1; t <= 245; t++)
        {
            engine.ReplayOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                continue;
            }

            var phase = aircraft.Phases?.CurrentPhase;
            if (phase is HoldingShortPhase hs && hs.HoldShort.TargetName is { } target && RunwayIdentifier.Parse(target).Contains("12"))
            {
                sawHoldingShortOf12 = true;
            }

            sawCrossing |= phase is CrossingRunwayPhase;

            if (t is 220 or 233 or 240 or 245)
            {
                NearestNodeHelper.Log(output, $"t={t} phase={phase?.GetType().Name ?? "null"}", aircraft, layout);
            }
        }

        var final = engine.FindAircraft(Callsign);
        Assert.NotNull(final);

        Assert.True(sawHoldingShortOf12, $"{Callsign} should hold short of runway 12 before CROSS 12");
        Assert.True(sawCrossing, $"{Callsign} should cross runway 12 after CROSS 12");

        // After crossing 12 the aircraft continues toward its destination runway 9 — it must NOT
        // stop in the idle hold past the runway-12 bars.
        Assert.IsNotType<HoldingInPositionPhase>(final.Phases?.CurrentPhase);
        Assert.Equal("09", final.Phases?.AssignedRunway?.Designator);
    }

    private (SimulationEngine Engine, AirportGroundLayout Layout, RecordingArchive Archive)? BuildReplay()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        var layout = archive.ReadLayout("mia");
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Debug).InitializeSimLog();

        var engine = new SimulationEngine(new SingleLayoutGroundData(layout));
        engine.Replay(archive.ToBaseSessionRecording(), 0);
        return (engine, layout, archive);
    }

    private sealed class SingleLayoutGroundData(AirportGroundLayout layout) : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId)
        {
            string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
            return string.Equals(shortId, layout.AirportId, StringComparison.OrdinalIgnoreCase) ? layout : null;
        }

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
