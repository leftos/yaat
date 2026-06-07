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
/// with preset <c>RWY 9 TAXI P S HS 12</c>. It taxis P/S and holds short of runway 12.
/// At t=229 the controller issues <c>CROSS 12</c>.
///
/// Two regressions are covered:
///
/// 1. <b>Manual spawn must replay.</b> <c>SimulationEngine.ReplayCommand</c> gated
///    <c>SpawnNow</c>/<c>SpawnDelay</c> behind the <c>FindAircraft is null</c> guard, but
///    those act on the delayed-spawn queue (the aircraft is intentionally not active yet),
///    so every recorded manual spawn was silently dropped on replay and on server-driven
///    snapshot regeneration. THY41J therefore never appeared in this bundle's snapshots.
///    With the fix it spawns during replay.
///
/// 2. <b>CROSS of an intermediate hold-short whose destination runway is unreachable.</b>
///    Runway 9 is not routable from parking via P/S — the taxi pathfinder cannot route a
///    taxi across the runway-12 crossing to reach runway 9 — so the route legitimately ends
///    at the hold-short of 12, with runway 9 stashed as the assigned runway. <c>CROSS 12</c>
///    must then clear the hold-short, cross runway 12, and hold past the far-side bars
///    (the aircraft cannot continue toward runway 9 because its stored path does not reach
///    it). It must NOT line up on / continue to runway 9, and runway 9 stays stashed.
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
    public void CrossIntermediateHoldShort_DestinationRunwayUnreachable_CrossesAndHolds()
    {
        var setup = BuildReplay();
        if (setup is null)
        {
            return;
        }

        using var archive = setup.Value.Archive;
        var engine = setup.Value.Engine;
        var layout = setup.Value.Layout;

        bool sawHoldingShortOf12BeforeCross = false;
        bool sawCrossing = false;
        bool everHeldShortOfRunway9 = false;

        // Replay through the spawn (t=91), taxi-out, hold-short, and the recorded
        // CROSS 12 (t=229, +3 s recorded reaction delay) to the settled far side.
        for (int t = 1; t <= 245; t++)
        {
            engine.ReplayOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                continue;
            }

            var phase = aircraft.Phases?.CurrentPhase;

            if (phase is HoldingShortPhase hs && hs.HoldShort.TargetName is { } target)
            {
                var id = RunwayIdentifier.Parse(target);
                // Before CROSS: holding short of runway 12, its destination runway (9) stashed
                // but the route stopped here because 9 was unreachable.
                if (t < 232 && id.Contains("12"))
                {
                    sawHoldingShortOf12BeforeCross = true;
                    Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.HoldShort.Reason);
                    Assert.True(
                        aircraft.Ground.AssignedTaxiRoute?.IsComplete,
                        "Route completes at the runway-12 hold-short (runway 9 unreachable via P/S)"
                    );
                    Assert.Equal("09", aircraft.Phases?.AssignedRunway?.Designator);
                }

                everHeldShortOfRunway9 |= id.Contains("9");
            }

            sawCrossing |= phase is CrossingRunwayPhase;

            if (t is 220 or 233 or 240 or 245)
            {
                NearestNodeHelper.Log(output, $"t={t} phase={phase?.GetType().Name ?? "null"}", aircraft, layout);
            }
        }

        var final = engine.FindAircraft(Callsign);
        Assert.NotNull(final);

        Assert.True(sawHoldingShortOf12BeforeCross, $"{Callsign} should hold short of runway 12 before CROSS 12");
        Assert.True(sawCrossing, $"{Callsign} should cross runway 12 after CROSS 12");

        // Crossed and stopped past the far-side bars awaiting further taxi (its stored path
        // cannot reach runway 9), NOT lined up on or continuing to runway 9.
        Assert.IsType<HoldingInPositionPhase>(final.Phases?.CurrentPhase);
        Assert.False(everHeldShortOfRunway9, $"{Callsign} must not continue to / hold short of runway 9 — its route never reached it");

        // Destination runway stays stashed for a later taxi clearance.
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
