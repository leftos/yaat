using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>Transmission pacing: stateless per-aircraft think time, one transmission per position per tick, a seeded gap.</summary>
public class AiPacingTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AiPacingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ThinkTime_IsAPureFunctionOfCallsignAndRule_WithinBounds()
    {
        foreach (var callsign in new[] { "N152SP", "SWA1234", "UAL1", "N7LJ" })
        {
            double think = AiPacing.ThinkTimeSeconds(callsign, "answer-taxi-out");
            Assert.InRange(think, AiPacing.ThinkMinSeconds, AiPacing.ThinkMaxSeconds);
            Assert.Equal(think, AiPacing.ThinkTimeSeconds(callsign, "answer-taxi-out"));
        }

        // Distinct rules draw independently, so one aircraft is not "quick" on every rule at once.
        Assert.NotEqual(AiPacing.ThinkTimeSeconds("N152SP", "answer-taxi-out"), AiPacing.ThinkTimeSeconds("N152SP", "hand-to-local"));
    }

    [Fact]
    public void Gap_IsDrawnFromTheAiRngStream_AndIsSeedStable()
    {
        var first = new AiPacing();
        var second = new AiPacing();
        first.MarkTransmitted(100, new SerializableRandom(11));
        second.MarkTransmitted(100, new SerializableRandom(11));

        Assert.Equal(first.NextTransmitAtSeconds, second.NextTransmitAtSeconds);
        Assert.InRange(
            first.NextTransmitAtSeconds,
            100 + AiPacing.MinGapSeconds - AiPacing.GapJitterSeconds,
            100 + AiPacing.MinGapSeconds + AiPacing.GapJitterSeconds
        );
        Assert.True(first.IssuedThisTick);
        Assert.False(first.CanTransmit(200));

        first.BeginTick();
        Assert.False(first.IssuedThisTick);
        Assert.False(first.CanTransmit(101));
        Assert.True(first.CanTransmit(100 + AiPacing.MinGapSeconds + AiPacing.GapJitterSeconds));

        first.Reset();
        Assert.True(first.CanTransmit(0));
    }

    [Fact]
    public void TryIssue_WaitsForTheThinkTime_ThenOnePerTick_ThenTheGap()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        var sink = new RecordingAiCommandSink();
        var pacing = new AiPacing();
        var memo = new AiAircraftMemo();
        var intent = new AiIntent("probe", "pacing test");
        const double now = 100;

        // The think-time clock starts on the first attempt; nothing goes out before it elapses.
        Assert.False(Scope(engine, aircraft, ground, now, pacing, sink).TryIssue(aircraft, memo, "TAXIAUTO 28R", intent));
        Assert.Equal("probe", memo.ObservedRule);
        Assert.Null(memo.InFlight);
        Assert.Empty(sink.Issued);
        double think = AiPacing.ThinkTimeSeconds(aircraft.Callsign, "probe");
        Assert.False(Scope(engine, aircraft, ground, now + think - 0.5, pacing, sink).TryIssue(aircraft, memo, "TAXIAUTO 28R", intent));

        var issuing = Scope(engine, aircraft, ground, now + think, pacing, sink);
        Assert.True(issuing.TryIssue(aircraft, memo, "TAXIAUTO 28R", intent));
        var request = Assert.Single(sink.Issued);
        Assert.Same(request, memo.InFlight);
        Assert.Equal("TAXIAUTO 28R", request.Canonical);
        Assert.Equal(ground.PositionId, request.From.PositionId);
        Assert.Equal(now + think, memo.IssuedAtSeconds);
        Assert.Equal(now + think + AiRuleScope.EffectGraceSeconds, memo.EffectDeadlineSeconds);
        Assert.False(memo.CanAct(now + think));

        // A second aircraft this tick waits: one transmission per position per tick.
        var other = new AiAircraftMemo();
        other.Observe("probe", 0);
        Assert.False(issuing.TryIssue(aircraft, other, "TAXIAUTO 28L", intent));

        // The next tick is inside the gap; a tick past the longest possible gap is not.
        pacing.BeginTick();
        Assert.False(Scope(engine, aircraft, ground, now + think + 1, pacing, sink).TryIssue(aircraft, other, "TAXIAUTO 28L", intent));
        pacing.BeginTick();
        double clear = now + think + AiPacing.MinGapSeconds + AiPacing.GapJitterSeconds;
        Assert.True(Scope(engine, aircraft, ground, clear, pacing, sink).TryIssue(aircraft, other, "TAXIAUTO 28L", intent));
        Assert.Equal(2, sink.Issued.Count);
    }

    [Fact]
    public void Memo_BoundedRetry_BacksOffAndGivesUpAfterTwoRetries()
    {
        var memo = new AiAircraftMemo();
        var request = new AiCommandRequest(null!, "N152SP", "TAXIAUTO 28R", new AiIntent("probe", ""));

        memo.MarkIssued(request, 10, 15);
        memo.Intent = GroundIntent.TaxiIssued;
        memo.Complete(success: false, now: 12);
        Assert.Null(memo.InFlight);
        Assert.Equal(GroundIntent.None, memo.Intent);
        Assert.Equal(1, memo.Rejections);
        Assert.False(memo.GaveUp);
        Assert.False(memo.CanAct(12 + AiAircraftMemo.RetryBackoffSeconds - 1));
        Assert.True(memo.CanAct(12 + AiAircraftMemo.RetryBackoffSeconds));

        memo.MarkIssued(request, 22, 15);
        memo.Complete(success: false, now: 23);
        Assert.Equal(2, memo.Rejections);
        Assert.False(memo.GaveUp);
        Assert.True(memo.CanAct(23 + (2 * AiAircraftMemo.RetryBackoffSeconds)));

        memo.MarkIssued(request, 43, 15);
        memo.Complete(success: false, now: 44);
        Assert.Equal(3, memo.Rejections);
        Assert.True(memo.GaveUp);
        Assert.False(memo.CanAct(10_000));

        // A success clears the ledger.
        var fresh = new AiAircraftMemo();
        fresh.MarkIssued(request, 0, 15);
        fresh.Complete(success: false, now: 1);
        fresh.MarkIssued(request, 11, 15);
        fresh.Complete(success: true, now: 12);
        Assert.Equal(0, fresh.Rejections);
        Assert.True(fresh.CanAct(12));
    }

    private static AiRuleScope Scope(
        SimulationEngine engine,
        AircraftState aircraft,
        AiPositionConfig position,
        double now,
        AiPacing pacing,
        IAiCommandSink sink
    )
    {
        var context = AiTestHost.Context(engine, [aircraft], [position], now, [], sink);
        return new AiRuleScope
        {
            Tick = context,
            Position = position,
            Jurisdiction = context.View.Jurisdiction(position),
            Memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal),
            Pacing = pacing,
        };
    }
}
