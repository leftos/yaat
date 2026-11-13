using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// A Ground rule that would issue a command needing the ground layout waits while the aircraft's airport is loading: it
/// issues nothing and is not marked issued, and once the airport is admitted it answers as it always does.
/// </summary>
public class LayoutLoadingRuleTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public LayoutLoadingRuleTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void AnswerTaxiIn_WhileTheAirportLoads_IssuesNothing_AndAnswersAfterTheAdmission()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CLAND").Success);
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi, ParkingName: not null },
            500
        );

        AssertWaitsWhileLoading(engine, ground, aircraft, new AnswerTaxiInRule(), $"TAXIAUTO @{aircraft.PendingPilotRequest!.ParkingName}");
    }

    [Fact]
    public void AnswerTaxiOut_WhileTheAirportLoads_IssuesNothing_AndAnswersAfterTheAdmission()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 300, Speed = 12 }] };
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            15
        );

        AssertWaitsWhileLoading(engine, ground, aircraft, new AnswerTaxiOutRule(), "TAXIAUTO 28R");
    }

    [Fact]
    public void RunwayCrossing_WhileTheAirportLoads_IssuesNothing_KeepsItsMemo_AndCrossesAfterTheAdmission()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30").Success);
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac =>
                TaxiRouteProgress.NextUnclearedCrossing(ac, engine.ResolveGroundLayout(ac)) is { } p
                && (p.DistanceFt <= RunwayCrossingRule.PreClearDistanceFt),
            900
        );

        // Local unstaffed: Ground works the crossing itself. The first pass starts the think time only.
        var probe = new Probe(engine, ground);
        var rule = new RunwayCrossingRule();
        double t0 = engine.Scenario!.ElapsedSeconds;
        rule.Evaluate(probe.Scope([aircraft], t0));
        Assert.Empty(probe.Sink.Issued);
        AiAircraftMemo memo = probe.Memos[AiTestFixture.Callsign];
        (GroundIntent intent, int? pendingNode) = (memo.Intent, memo.PendingCrossingNodeId);

        aircraft.Ground.Layout = null;
        engine.ApplyRecordedLayoutDeferred(new RecordedLayoutDeferred(t0, "OAK"));
        rule.Evaluate(probe.Scope([aircraft], t0 + AiPacing.ThinkMaxSeconds));
        Assert.Empty(probe.Sink.Issued);
        Assert.Equal(intent, memo.Intent);
        Assert.Equal(pendingNode, memo.PendingCrossingNodeId);

        // Admitted: the think time that ran while it loaded counts, so the crossing is cleared at once.
        engine.ApplyRecordedLayoutAdmitted(new RecordedLayoutAdmitted(t0, "OAK"));
        rule.Evaluate(probe.Scope([aircraft], t0 + AiPacing.ThinkMaxSeconds));
        AiCommandRequest cross = Assert.Single(probe.Sink.Issued);
        Assert.StartsWith("CROSS ", cross.Canonical, StringComparison.Ordinal);
        Assert.Equal(t0, memo.ObservedAtSeconds);
    }

    [Fact]
    public void PushOffStand_WhileTheAirportLoads_IssuesNothing_AndKeepsItsThinkTime()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        string jetOnRon9 = AiTestFixture
            .ParkedAtOak.Replace("\"C172\"", "\"B738\"", StringComparison.Ordinal)
            .Replace("\"cruiseSpeed\": 100", "\"cruiseSpeed\": 430", StringComparison.Ordinal)
            .Replace("\"parking\": \"SIG1\"", "\"parking\": \"RON9\"", StringComparison.Ordinal);
        SimulationEngine engine = AiTestFixture.Load(jetOnRon9, _zoa, 7, [ground]);
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback },
            15
        );
        var probe = new Probe(engine, ground);
        var rule = new PushOffStandRule();
        double t0 = engine.Scenario!.ElapsedSeconds;
        rule.Evaluate(probe.Scope([aircraft], t0));
        Assert.Empty(probe.Sink.Issued);

        aircraft.Ground.Layout = null;
        engine.ApplyRecordedLayoutDeferred(new RecordedLayoutDeferred(t0, "OAK"));
        rule.Evaluate(probe.Scope([aircraft], t0 + AiPacing.ThinkMaxSeconds));
        Assert.Empty(probe.Sink.Issued);

        engine.ApplyRecordedLayoutAdmitted(new RecordedLayoutAdmitted(t0, "OAK"));
        rule.Evaluate(probe.Scope([aircraft], t0 + AiPacing.ThinkMaxSeconds));
        AiCommandRequest answer = Assert.Single(probe.Sink.Issued);
        Assert.True(answer.Canonical.StartsWith("PUSH ", StringComparison.Ordinal) || (answer.Canonical == "STBY"), answer.Canonical);
        Assert.Equal(t0, probe.Memos[AiTestFixture.Callsign].ObservedAtSeconds);
    }

    // The airport's deferral and admission arrive as a replay applies them (the primary airport is loaded at the scenario
    // load, so the source never answers it loading); the aircraft is detached as one restored while its airport loads is.
    private static void AssertWaitsWhileLoading(
        SimulationEngine engine,
        AiPositionConfig ground,
        AircraftState aircraft,
        IDecisionRule rule,
        string expected
    )
    {
        double now = engine.Scenario!.ElapsedSeconds;
        aircraft.Ground.Layout = null;
        engine.ApplyRecordedLayoutDeferred(new RecordedLayoutDeferred(now, "OAK"));
        var probe = new Probe(engine, ground);

        rule.Evaluate(probe.Scope([aircraft], now));
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        rule.Evaluate(probe.Scope([aircraft], now + (2 * AiPacing.ThinkMaxSeconds)));
        Assert.Empty(probe.Sink.Issued);
        Assert.Null(probe.Memos[AiTestFixture.Callsign].InFlight);

        engine.ApplyRecordedLayoutAdmitted(new RecordedLayoutAdmitted(now, "OAK"));
        double later = now + 100;
        rule.Evaluate(probe.Scope([aircraft], later));
        rule.Evaluate(probe.Scope([aircraft], later + AiPacing.ThinkMaxSeconds));
        AiCommandRequest request = Assert.Single(probe.Sink.Issued);
        Assert.Equal(expected, request.Canonical);
        Assert.Same(request, probe.Memos[AiTestFixture.Callsign].InFlight);
    }

    /// <summary>One rule run alone: a recording sink, one pacing, one memo table, and scopes at chosen times.</summary>
    private sealed class Probe(SimulationEngine engine, AiPositionConfig ground)
    {
        public RecordingAiCommandSink Sink { get; } = new();

        public Dictionary<string, AiAircraftMemo> Memos { get; } = new(StringComparer.Ordinal);

        private readonly AiPacing _pacing = new();

        public AiRuleScope Scope(IReadOnlyList<AircraftState> aircraft, double now)
        {
            _pacing.BeginTick();
            AiTickContext context = AiTestFixture.Context(engine, aircraft, [ground], now, [], Sink);
            return new AiRuleScope
            {
                Tick = context,
                Position = ground,
                Jurisdiction = context.View.Jurisdiction(ground),
                Memos = Memos,
                Pacing = _pacing,
            };
        }
    }
}
