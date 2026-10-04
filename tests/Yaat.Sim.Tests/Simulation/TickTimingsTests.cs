using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Pins the opt-in <see cref="SimulationEngine.TickTimings"/> sink. The step trace is always on and sees order; this
/// sink is separate, null in production, and records one bucket per spine step under its <see cref="StepId"/> name,
/// one <c>Physics</c> bucket per physics sub-tick, the three segment rollups <c>PrePhysics</c> / <c>PostPhysics</c> /
/// <c>EndOfSecond</c>, and the physics internals (<c>Physics.WorldTick</c>, …). Attach a dictionary and the whole
/// spine, replay included, records into it; <see cref="SimulationEngine.Replay"/> clears it first.
/// </summary>
public class TickTimingsTests
{
    private const string Physics = nameof(StepId.Physics);
    private const string PrePhysics = "PrePhysics";
    private const string PostPhysics = "PostPhysics";
    private const string EndOfSecond = "EndOfSecond";

    /// <summary>A minimal scenario: the replay path only needs a JSON the loader accepts before it clears the sink.</summary>
    private const string EmptyScenario = """
        {
          "id": "tick-timings",
          "name": "Tick Timings",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": []
        }
        """;

    public TickTimingsTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void AttachedSink_RecordsEveryStepOnceAndPhysicsFourTimes()
    {
        SimulationEngine engine = SpineTraceTests.BuildEngine();
        engine.TickTimings = [];

        engine.TickOneSecond();

        Assert.NotNull(engine.TickTimings);
        Dictionary<string, (int Count, double Ms)> timings = engine.TickTimings;

        foreach (StepId id in Enum.GetValues<StepId>())
        {
            if (id == StepId.Physics)
            {
                continue;
            }

            Assert.True(timings.TryGetValue(id.ToString(), out (int Count, double Ms) bucket), $"no bucket for step {id}");
            Assert.Equal(1, bucket.Count);
        }

        Assert.Equal(SimulationEngine.PhysicsSubTickRate, timings[Physics].Count);

        foreach (string rollup in new[] { PrePhysics, PostPhysics, EndOfSecond })
        {
            Assert.True(timings.TryGetValue(rollup, out (int Count, double Ms) bucket), $"no bucket for rollup {rollup}");
            Assert.Equal(1, bucket.Count);
        }

        foreach (KeyValuePair<string, (int Count, double Ms)> kvp in timings)
        {
            Assert.True(kvp.Value.Ms >= 0, $"{kvp.Key} recorded a negative millisecond total");
            if (kvp.Key.StartsWith("Physics.", StringComparison.Ordinal))
            {
                Assert.True(kvp.Value.Count >= 1, $"{kvp.Key} recorded a bucket with no samples");
            }
        }
    }

    [Fact]
    public void AttachedSink_AccumulatesAcrossSeconds()
    {
        SimulationEngine engine = SpineTraceTests.BuildEngine();
        engine.TickTimings = [];

        engine.TickOneSecond();
        engine.TickOneSecond();

        Assert.NotNull(engine.TickTimings);
        Dictionary<string, (int Count, double Ms)> timings = engine.TickTimings;
        Assert.Equal(2 * SimulationEngine.PhysicsSubTickRate, timings[Physics].Count);
        Assert.Equal(2, timings[nameof(StepId.TerminalEntries)].Count);
    }

    [Fact]
    public void NoSink_RecordsNothingAndDoesNotThrow()
    {
        SimulationEngine engine = SpineTraceTests.BuildEngine();

        engine.TickOneSecond();

        Assert.Null(engine.TickTimings);
        Assert.Equal("(no tick timings recorded)", engine.DumpTickTimings());
    }

    [Fact]
    public void Replay_ClearsTheSink()
    {
        var recording = new SessionRecording
        {
            Version = 4,
            ScenarioJson = EmptyScenario,
            RngSeed = 1,
            Actions = [],
            TotalElapsedSeconds = 0,
        };

        var engine = new SimulationEngine(new TestAirportGroundData()) { TickTimings = new() { ["sentinel"] = (1, 0.0) } };

        engine.Replay(recording, 0);

        Assert.NotNull(engine.TickTimings);
        Assert.False(engine.TickTimings.ContainsKey("sentinel"), "Replay should have cleared the sink");
    }
}
