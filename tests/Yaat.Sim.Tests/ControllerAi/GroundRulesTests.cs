using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Brains;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// The Ground brain's decision rules, each driven by hand over a real engine: the rule sees the world through a
/// recording sink, the test dispatches what the rule issued itself.
/// </summary>
public class GroundRulesTests
{
    private const string TwoAircraftOnFinalTemplate = """
        {
          "id": "ai-crossing",
          "name": "AI crossing at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            },
            {
              "id": "a2",
              "aircraftId": "N2FL",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "OnFinal", "runway": "28R", "distanceFromRunway": DISTANCE },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public GroundRulesTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void AnswerTaxiOut_IssuesTaxiAutoToTheRunwayInUse_Once()
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
        var probe = new RuleProbe(engine, ground);
        var rule = new AnswerTaxiOutRule();
        double now = engine.Scenario!.ElapsedSeconds;

        // The think time runs from the first evaluation; nothing before it.
        rule.Evaluate(probe.Scope([aircraft], now));
        Assert.Empty(probe.Sink.Issued);
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        AiCommandRequest request = Assert.Single(probe.Sink.Issued);
        // OAK's knowledge: 12 kt from 300 is the west configuration and a C172 from the north field gets a 28.
        Assert.Equal("TAXIAUTO 28R", request.Canonical);
        Assert.Equal("answer-taxi-out", request.Intent.Rule);
        Assert.Contains("SFOW", request.Intent.Rationale);
        AiAircraftMemo memo = probe.Memos[AiTestFixture.Callsign];
        Assert.Equal(GroundIntent.TaxiIssued, memo.Intent);
        Assert.Same(request, memo.InFlight);

        // In flight: no second issue. Dispatched and answered: the request closes and the rule stays quiet.
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + 10));
        Assert.Single(probe.Sink.Issued);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, request.Canonical).Success);
        memo.Complete(success: true, now + AiPacing.ThinkMaxSeconds + 11);
        Assert.False(aircraft.PendingPilotRequest!.IsOpen);
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + 20));
        Assert.Single(probe.Sink.Issued);
    }

    /// <summary>
    /// The Ground brain answers a ready-to-taxi call from an either stand (KOAK MTN1) with a taxi-out, never a push: the
    /// stand's departure does not steer it.
    /// </summary>
    [Fact]
    public void GroundBrain_ParkedEitherStand_AnswersWithTaxiAuto()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(
            AiTestFixture.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"MTN1\""),
            _zoa,
            7,
            [ground]
        );
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 300, Speed = 12 }] };
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            15
        );
        Assert.Equal(StandDeparture.Either, ParkedStandDeparture.Of(aircraft, aircraft.Ground.Layout));
        var probe = new RuleProbe(engine, ground);
        var rule = new AnswerTaxiOutRule();
        double now = engine.Scenario!.ElapsedSeconds;

        rule.Evaluate(probe.Scope([aircraft], now));
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));

        AiCommandRequest request = Assert.Single(probe.Sink.Issued);
        Assert.StartsWith("TAXIAUTO ", request.Canonical, StringComparison.Ordinal);
        Assert.Equal("answer-taxi-out", request.Intent.Rule);
    }

    /// <summary>
    /// The defect (YAAT-532): a jet parked on a push-back stand asks Ground for a push, and Ground answers with the
    /// stand's first clear planned target as a recorded <c>PUSH</c>, which satisfies the request. The crew then reports
    /// "pushed back from gate" after its setup time, and the ready-to-taxi answer follows as for any pushed aircraft.
    /// </summary>
    [Fact]
    public void GroundBrain_PushbackRequest_AnsweredWithPush_ThenTaxiAuto()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("S7"), _zoa, 7, [ground]);
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 300, Speed = 12 }] };
        AircraftState aircraft = PushbackRequested(engine, AiTestFixture.Callsign, 15);
        // The stand the rule is about: a stand-data change that made S7 anything else must fail here, not silently.
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(aircraft, engine.ResolveGroundLayout(aircraft)));

        var probe = new RuleProbe(engine, ground);
        var rule = new PushOffStandRule();
        double now = engine.Scenario!.ElapsedSeconds;

        // The think time runs from the first evaluation; nothing before it.
        rule.Evaluate(probe.Scope([aircraft], now));
        Assert.Empty(probe.Sink.Issued);
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        AiCommandRequest push = Assert.Single(probe.Sink.Issued);
        Assert.StartsWith("PUSH ", push.Canonical, StringComparison.Ordinal);
        Assert.Equal("push-off-stand", push.Intent.Rule);
        Assert.StartsWith("pushback requested; PUSH ", push.Intent.Rationale, StringComparison.Ordinal);
        Assert.Equal(GroundIntent.PushIssued, probe.Memos[AiTestFixture.Callsign].Intent);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, push.Canonical).Success, push.Canonical);
        probe.Memos[AiTestFixture.Callsign].Complete(success: true, engine.Scenario!.ElapsedSeconds);
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, aircraft.PendingPilotRequest!.ResponseState);

        int budget = 400 + (int)InitialCallupCall.PostPushSetupDelaySeconds(AircraftCategory.Jet);
        AircraftState pushed = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            budget
        );
        Assert.Contains("pushed back from gate", pushed.PendingPilotRequest!.LastPilotLine, StringComparison.OrdinalIgnoreCase);

        // The push rule has nothing more to do; the pushed aircraft is taxied out as any other is.
        double after = engine.Scenario!.ElapsedSeconds;
        rule.Evaluate(probe.Scope([pushed], after));
        Assert.Single(probe.Sink.Issued);
        var answer = new AnswerTaxiOutRule();
        answer.Evaluate(probe.Scope([pushed], after));
        answer.Evaluate(probe.Scope([pushed], after + AiPacing.ThinkMaxSeconds));
        AiCommandRequest taxi = probe.Sink.Issued[^1];
        Assert.StartsWith("TAXIAUTO ", taxi.Canonical, StringComparison.Ordinal);
        Assert.Equal("answer-taxi-out", taxi.Intent.Rule);
    }

    /// <summary>
    /// A stand planned before the tick (<see cref="PushPlanStore.PlanParked"/>, the server's prepare step): the rule's
    /// first answer is a PUSH read from the store, with no read that misses and so no plan made on the tick.
    /// </summary>
    [Fact]
    public void PushOffStand_StandPlannedBeforeTheTick_PushesWithoutPlanningOnTheTick()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("S7"), _zoa, 7, [ground]);
        PushPlanStore store = engine.World.PushPlans;
        Assert.Equal(1, store.PlanParked(engine.World.GetSnapshot(), engine.ResolveGroundLayout, 2));
        AircraftState aircraft = PushbackRequested(engine, AiTestFixture.Callsign, 15);

        AiCommandRequest push = FirstAnswer(engine, ground, [aircraft]);

        Assert.StartsWith("PUSH ", push.Canonical, StringComparison.Ordinal);
        Assert.Equal(0, store.Misses);
        Assert.True(store.Hits >= 1, $"the rule read {store.Hits} plans from the store");
    }

    /// <summary>
    /// A stand never planned before the tick: the rule's read misses, so it plans the stand on the tick and stores it,
    /// and the next read of that stand is a hit.
    /// </summary>
    [Fact]
    public void PushOffStand_StandNeverPlanned_PlansOnTheTickAndStoresIt()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("S7"), _zoa, 7, [ground]);
        PushPlanStore store = engine.World.PushPlans;
        AircraftState aircraft = PushbackRequested(engine, AiTestFixture.Callsign, 15);

        AiCommandRequest push = FirstAnswer(engine, ground, [aircraft]);

        Assert.StartsWith("PUSH ", push.Canonical, StringComparison.Ordinal);
        Assert.Equal(1, store.Misses);
        AirportGroundLayout layout = engine.ResolveGroundLayout(aircraft)!;
        int hits = store.Hits;
        Assert.True(store.TryGet(layout, "S7", AirplaneDesignGroups.GroupForType("B738"), out PushTargetEntry? stored));
        Assert.NotNull(stored);
        Assert.Equal(hits + 1, store.Hits);
        Assert.Equal(1, store.Misses);
    }

    /// <summary>A piston is out of the rule's scope: a C172 on the same push-back stand asks to taxi and is never pushed.</summary>
    [Fact]
    public void GroundBrain_ParkedPushBackStand_PistonIsNotPushed()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(ParkedAt("S7", "C172", "100"), _zoa, 7, [ground]);
        AircraftState aircraft = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.PendingPilotRequest is { IsOpen: true }, 15);
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(aircraft, engine.ResolveGroundLayout(aircraft)));
        Assert.Equal(PilotPendingRequestKind.Taxi, aircraft.PendingPilotRequest!.Kind);
        AssertRuleIssuesNothing(engine, ground, aircraft);
    }

    /// <summary>A taxi-out stand keeps today's direct taxi-out: the crew asks to taxi and Ground never pushes it.</summary>
    [Fact]
    public void GroundBrain_ParkedTaxiOutStand_JetIsNotPushed()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("SIG4"), _zoa, 7, [ground]);
        AircraftState aircraft = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.PendingPilotRequest is { IsOpen: true }, 15);
        Assert.Equal(StandDeparture.TaxiOut, ParkedStandDeparture.Of(aircraft, engine.ResolveGroundLayout(aircraft)));
        Assert.Equal(PilotPendingRequestKind.Taxi, aircraft.PendingPilotRequest!.Kind);
        AssertRuleIssuesNothing(engine, ground, aircraft);
    }

    /// <summary>
    /// Every target of the stand blocked by a parked neighbour the aircraft already touches: Ground answers standby once,
    /// pushes nothing and taxis nothing; the aircraft waits at the stand until the neighbour leaves, when the same rule
    /// pushes it.
    /// </summary>
    [Fact]
    public void GroundBrain_PushbackRequest_EveryTargetBlocked_StandbyThenPushes()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("S7"), _zoa, 7, [ground]);
        AircraftState aircraft = PushbackRequested(engine, AiTestFixture.Callsign, 15);
        AircraftState blocker = ParkedOnTopOf(aircraft);
        var probe = new RuleProbe(engine, ground);
        var rule = new PushOffStandRule();
        double now = engine.Scenario!.ElapsedSeconds;

        for (double t = 0; t <= 60; t += 5)
        {
            rule.Evaluate(probe.Scope([aircraft, blocker], now + AiPacing.ThinkMaxSeconds + t));
            DispatchStandby(engine, probe);
        }

        AiCommandRequest standby = Assert.Single(probe.Sink.Issued);
        Assert.Equal("STBY", standby.Canonical);
        Assert.Equal("push-off-stand", standby.Intent.Rule);
        Assert.Contains("blocked", standby.Intent.Rationale, StringComparison.Ordinal);
        Assert.Equal(PilotPendingRequestResponseState.Standby, aircraft.PendingPilotRequest!.ResponseState);

        // The neighbour leaves: the first clear target of the same stand is pushed on the next evaluation.
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + 65));

        Assert.Equal(2, probe.Sink.Issued.Count);
        AiCommandRequest push = probe.Sink.Issued[^1];
        Assert.StartsWith("PUSH ", push.Canonical, StringComparison.Ordinal);
        Assert.Equal("push-off-stand", push.Intent.Rule);
        Assert.DoesNotContain(probe.Sink.Issued, issued => issued.Canonical.StartsWith("TAXI", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two jets asking for a push off neighbouring stands of one ramp: the first is pushed, the second is told to stand by
    /// and is pushed only once the first's tow has ended.
    /// </summary>
    [Fact]
    public void GroundBrain_SecondPushbackRequestInTheSameRamp_StandbyUntilTheFirstPushEnds()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(TwoJetsAt("RON9", "RON10"), _zoa, 7, [ground]);
        AircraftState first = PushbackRequested(engine, AiTestFixture.Callsign, 15);
        AircraftState second = PushbackRequested(engine, SecondCallsign, 180);
        var probe = new RuleProbe(engine, ground);
        var rule = new PushOffStandRule();
        double now = engine.Scenario!.ElapsedSeconds;

        rule.Evaluate(probe.Scope([first, second], now));
        rule.Evaluate(probe.Scope([first, second], now + AiPacing.ThinkMaxSeconds));
        AiCommandRequest push = Assert.Single(probe.Sink.Issued);
        Assert.StartsWith("PUSH ", push.Canonical, StringComparison.Ordinal);

        // Issued but not yet started: the second already waits for it.
        rule.Evaluate(probe.Scope([first, second], now + AiPacing.ThinkMaxSeconds + 1));
        rule.Evaluate(probe.Scope([first, second], now + (2 * AiPacing.ThinkMaxSeconds) + 1));
        AiCommandRequest standby = probe.Sink.Issued[^1];
        Assert.Equal(SecondCallsign, standby.Callsign);
        Assert.Equal("STBY", standby.Canonical);
        Assert.Contains(AiTestFixture.Callsign, standby.Intent.Rationale, StringComparison.Ordinal);
        DispatchStandby(engine, probe);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, push.Canonical).Success, push.Canonical);
        probe.Memos[AiTestFixture.Callsign].Complete(success: true, engine.Scenario!.ElapsedSeconds);
        AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is PushbackPhase, 30);

        // Every pass while the first is under tow: no push for the second, only a standby again if its crew asks again.
        int issuedBefore = probe.Sink.Issued.Count;
        for (int elapsed = 0; (first.Phases?.CurrentPhase is PushbackPhase) && (elapsed < 600); elapsed++)
        {
            rule.Evaluate(probe.Scope([first, second], engine.Scenario!.ElapsedSeconds + (2 * AiPacing.ThinkMaxSeconds)));
            Assert.All(probe.Sink.Issued.Skip(issuedBefore), issued => Assert.Equal("STBY", issued.Canonical));
            DispatchStandby(engine, probe);
            AiTestFixture.Tick(engine, 1);
        }

        Assert.False(
            first.Phases!.CurrentPhase is PushbackPhase,
            $"{first.Callsign} still under tow: yielding to {first.Ground.AutoYieldTarget ?? "nobody"}, "
                + $"limit {first.Ground.SpeedLimit}, gs {first.GroundSpeed:F1}"
        );
        double end = engine.Scenario!.ElapsedSeconds + (2 * AiPacing.ThinkMaxSeconds);
        rule.Evaluate(probe.Scope([first, second], end));
        AiCommandRequest secondPush = probe.Sink.Issued[^1];
        Assert.Equal(SecondCallsign, secondPush.Callsign);
        Assert.StartsWith("PUSH ", secondPush.Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two jets each parked across the other, each asking for a push: every target of each is blocked by the other, which
    /// is itself parked and waiting on it, so the wait can never end. The standby that finds it opens a
    /// <see cref="AiAnomalyKind.PushbackHeld"/> anomaly naming the stand and the outcomes, at the first standby.
    /// </summary>
    [Fact]
    public void PushOffStand_BlockersParkedAndWaitingOnIt_FlagsTheFirstStandby()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(TwoJetsAt("25", "26"), _zoa, 7, [ground]);
        AircraftState first = PushbackRequested(engine, AiTestFixture.Callsign, 15);
        AircraftState second = PushbackRequested(engine, SecondCallsign, 180);
        second.Position = first.Position;
        var probe = new RuleProbe(engine, ground);
        var rule = new PushOffStandRule();
        double now = engine.Scenario!.ElapsedSeconds;

        int standbys = 0;
        for (double t = 0; (t <= 60) && (standbys == 0); t += 1)
        {
            rule.Evaluate(probe.Scope([first, second], now + t));
            standbys = probe.Sink.Issued.Count(issued => (issued.Callsign == AiTestFixture.Callsign) && (issued.Canonical == "STBY"));
            if (standbys == 0)
            {
                Assert.False(Flagged(engine, ground, AiTestFixture.Callsign), "flagged before any standby");
            }

            DispatchStandby(engine, probe);
        }

        Assert.Equal(1, standbys);
        Assert.True(Flagged(engine, ground, AiTestFixture.Callsign), "the first standby did not flag the wait");
        AiAnomalyEvent opened = engine
            .Scenario.AiAnomalies.Drain()
            .First(e => (e.Kind == AiAnomalyKind.PushbackHeld) && (e.SubjectKey == AiTestFixture.Callsign));
        Assert.Contains("stand 25", opened.Detail, StringComparison.Ordinal);
        Assert.Contains($"blocked by {SecondCallsign}", opened.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(probe.Sink.Issued, issued => issued.Canonical.StartsWith("PUSH", StringComparison.Ordinal));
    }

    /// <summary>
    /// A jet held by a real pusher in its ramp area — a tug that never finishes here — is told to stand by at every asking;
    /// the wait is flagged at the third standby for the request, and not before.
    /// </summary>
    [Fact]
    public void PushOffStand_HeldByARealPusher_FlagsTheThirdStandbyAndNotBefore()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("25"), _zoa, 7, [ground]);
        AircraftState aircraft = PushbackRequested(engine, AiTestFixture.Callsign, 15);
        AirportGroundLayout layout = engine.ResolveGroundLayout(aircraft)!;
        AircraftState pusher = Pusher(layout, "OAK", StandPosition(layout, "26"), pushedBackFrom: "26");
        var probe = new RuleProbe(engine, ground);
        var rule = new PushOffStandRule();

        int standbys = 0;
        for (int second = 0; (second < 600) && (standbys < PushOffStandRule.StandbysBeforeAnomaly); second++)
        {
            rule.Evaluate(probe.Scope([aircraft, pusher], engine.Scenario!.ElapsedSeconds));
            int now = probe.Sink.Issued.Count(issued => issued.Canonical == "STBY");
            if (now > standbys)
            {
                standbys = now;
                Assert.True(
                    Flagged(engine, ground, AiTestFixture.Callsign) == (standbys >= PushOffStandRule.StandbysBeforeAnomaly),
                    $"standby {standbys}: flagged={Flagged(engine, ground, AiTestFixture.Callsign)}"
                );
            }

            DispatchStandby(engine, probe);
            AiTestFixture.Tick(engine, 1);
        }

        Assert.Equal(PushOffStandRule.StandbysBeforeAnomaly, standbys);
        Assert.All(probe.Sink.Issued, issued => Assert.Equal("STBY", issued.Canonical));
        Assert.Contains("same ramp area", probe.Sink.Issued[^1].Intent.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tug pushing at another airport, off a stand that has the name of one in this stand's ramp area, does not hold the
    /// push: only pushers on this aircraft's ground layout count.
    /// </summary>
    [Fact]
    public void PushOffStand_PusherAtAnotherAirport_DoesNotHoldThePush()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("25"), _zoa, 7, [ground]);
        AircraftState aircraft = PushbackRequested(engine, AiTestFixture.Callsign, 15);
        AirportGroundLayout layout = engine.ResolveGroundLayout(aircraft)!;
        AircraftState elsewhere = Pusher(layout, "SFO", new LatLon(37.6189, -122.3750), pushedBackFrom: "26");
        Assert.NotSame(layout, engine.ResolveGroundLayout(elsewhere));

        AiCommandRequest issued = FirstAnswer(engine, ground, [aircraft, elsewhere]);

        Assert.StartsWith("PUSH ", issued.Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// A snapshot restore resets the brain while the request stands by: the restored aircraft gets no second standby (the
    /// crew has not asked again) and no anomaly, though the push is still held.
    /// </summary>
    [Fact]
    public void PushOffStand_RestoredWithTheBrainResetDuringStandby_NoSecondStandbyAndNoAnomaly()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(JetAt("25"), _zoa, 7, [ground]);
        AircraftState aircraft = PushbackRequested(engine, AiTestFixture.Callsign, 15);
        AirportGroundLayout layout = engine.ResolveGroundLayout(aircraft)!;
        AircraftState pusher = Pusher(layout, "OAK", StandPosition(layout, "26"), pushedBackFrom: "26");
        var probe = new RuleProbe(engine, ground);
        Assert.Equal("STBY", FirstAnswer(probe, new PushOffStandRule(), engine, [aircraft, pusher]).Canonical);
        DispatchStandby(engine, probe);
        Assert.Equal(PilotPendingRequestResponseState.Standby, aircraft.PendingPilotRequest!.ResponseState);

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), layout);
        var reset = new RuleProbe(engine, ground);
        var rule = new PushOffStandRule();
        double now = engine.Scenario!.ElapsedSeconds;
        for (double t = 0; t <= 60; t += 5)
        {
            rule.Evaluate(reset.Scope([restored, pusher], now + AiPacing.ThinkMaxSeconds + t));
        }

        Assert.Empty(reset.Sink.Issued);
        Assert.False(Flagged(engine, ground, AiTestFixture.Callsign));
    }

    private const string SecondCallsign = "N153SP";

    /// <summary>
    /// Whether a <see cref="AiAnomalyKind.PushbackHeld"/> anomaly is open on <paramref name="callsign"/> for
    /// <paramref name="ground"/>.
    /// </summary>
    private static bool Flagged(SimulationEngine engine, AiPositionConfig ground, string callsign) =>
        engine.Scenario!.AiAnomalies.IsOpen(AiAnomalyKind.PushbackHeld, ground.PositionId, callsign);

    private static LatLon StandPosition(AirportGroundLayout layout, string stand) => layout.FindParkingByName(stand)!.Position;

    /// <summary>The first thing a fresh push rule issues over <paramref name="world"/> once the think time has run.</summary>
    private static AiCommandRequest FirstAnswer(SimulationEngine engine, AiPositionConfig ground, List<AircraftState> world) =>
        FirstAnswer(new RuleProbe(engine, ground), new PushOffStandRule(), engine, world);

    private static AiCommandRequest FirstAnswer(RuleProbe probe, PushOffStandRule rule, SimulationEngine engine, List<AircraftState> world)
    {
        double now = engine.Scenario!.ElapsedSeconds;
        rule.Evaluate(probe.Scope(world, now));
        rule.Evaluate(probe.Scope(world, now + AiPacing.ThinkMaxSeconds));
        return Assert.Single(probe.Sink.Issued);
    }

    /// <summary>
    /// A B738 under a tug push, not in the engine, at <paramref name="at"/> on <paramref name="airportId"/>'s ground: pushed
    /// off <paramref name="pushedBackFrom"/> (null for a tow from a non-stand origin), its push leg running 300 ft south.
    /// </summary>
    private static AircraftState Pusher(AirportGroundLayout layout, string airportId, LatLon at, string? pushedBackFrom)
    {
        LatLon legEnd = GeoMath.ProjectPoint(at, new TrueHeading(180), 300.0 / GeoMath.FeetPerNm);
        var pusher = new AircraftState
        {
            Callsign = "N8PSH",
            AircraftType = "B738",
            Position = at,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            AirportId = airportId,
            IndicatedAirspeed = 3,
            Phases = new PhaseList(),
        };
        pusher.Phases.Add(
            new PushbackPhase
            {
                Move = TugMove.ToPoint(PushbackLegKind.Push, legEnd),
                PlannedEnd = legEnd,
                StartsAtStand = pushedBackFrom is not null,
                ContinuesIntoNextMove = false,
                ContinuesStandPushOff = false,
            }
        );
        pusher.Phases.Start(CommandDispatcher.BuildMinimalContext(pusher, ActiveRunways.Empty, layout));
        pusher.Ground.PushedBackFrom = pushedBackFrom;
        pusher.Ground.PushbackTrueHeading = new TrueHeading(180);
        return pusher;
    }

    /// <summary>Ticks the stand call, the aircraft's open pushback request: 5 s after spawn, later when another call holds the pacing slot.</summary>
    private static AircraftState PushbackRequested(SimulationEngine engine, string callsign, int budgetSeconds) =>
        AiTestFixture.TickUntil(
            engine,
            callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback },
            budgetSeconds
        );

    /// <summary>The push rule over a minute of evaluations past the think time issues nothing for <paramref name="aircraft"/>.</summary>
    private static void AssertRuleIssuesNothing(SimulationEngine engine, AiPositionConfig ground, AircraftState aircraft)
    {
        var probe = new RuleProbe(engine, ground);
        var rule = new PushOffStandRule();
        double now = engine.Scenario!.ElapsedSeconds;
        rule.Evaluate(probe.Scope([aircraft], now));
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + 60));
        Assert.Empty(probe.Sink.Issued);
    }

    /// <summary>Dispatches every standby still in flight, as the host would, and closes it on its memo.</summary>
    private static void DispatchStandby(SimulationEngine engine, RuleProbe probe)
    {
        foreach ((string callsign, AiAircraftMemo memo) in probe.Memos)
        {
            if (memo.InFlight is { Canonical: "STBY" })
            {
                Assert.True(engine.SendCommand(callsign, "STBY").Success);
                memo.Complete(success: true, engine.Scenario!.ElapsedSeconds);
            }
        }
    }

    /// <summary>
    /// <see cref="AiTestFixture.ParkedAtOak"/>'s aircraft as <paramref name="type"/> parked on <paramref name="stand"/>:
    /// the rule's scope is jets and turboprops on a push-back stand.
    /// </summary>
    private static string JetAt(string stand) => ParkedAt(stand, "B738", "430");

    private static string ParkedAt(string stand, string type, string cruiseSpeed) =>
        AiTestFixture
            .ParkedAtOak.Replace("\"C172\"", $"\"{type}\"", StringComparison.Ordinal)
            .Replace("\"cruiseSpeed\": 100", $"\"cruiseSpeed\": {cruiseSpeed}", StringComparison.Ordinal)
            .Replace("\"parking\": \"SIG1\"", $"\"parking\": \"{stand}\"", StringComparison.Ordinal);

    /// <summary>
    /// Two B738s, <see cref="AiTestFixture.Callsign"/> on <paramref name="firstStand"/> and <see cref="SecondCallsign"/> on
    /// <paramref name="secondStand"/>.
    /// </summary>
    private static string TwoJetsAt(string firstStand, string secondStand)
    {
        string scenario = JetAt(firstStand);
        int start = scenario.IndexOf("    {", scenario.IndexOf("\"aircraft\"", StringComparison.Ordinal), StringComparison.Ordinal);
        int end = scenario.IndexOf("\n  ]", start, StringComparison.Ordinal);
        string entry = scenario[start..end].TrimEnd();
        string second = entry
            .Replace("\"id\": \"a1\"", "\"id\": \"a2\"", StringComparison.Ordinal)
            .Replace(AiTestFixture.Callsign, SecondCallsign, StringComparison.Ordinal)
            .Replace($"\"parking\": \"{firstStand}\"", $"\"parking\": \"{secondStand}\"", StringComparison.Ordinal);
        return scenario.Insert(end, ",\n" + second);
    }

    /// <summary>A parked neighbour sitting where <paramref name="subject"/> stands, so every one of its push targets grades blocked.</summary>
    private static AircraftState ParkedOnTopOf(AircraftState subject)
    {
        var blocker = new AircraftState
        {
            Callsign = "N9BLK",
            AircraftType = "B738",
            Position = subject.Position,
            TrueHeading = subject.TrueHeading,
            IsOnGround = true,
            AirportId = "OAK",
            Phases = new PhaseList(),
        };
        blocker.Phases.Add(new AtParkingPhase());
        return blocker;
    }

    [Fact]
    public void AnswerTaxiOut_RetriesTwice_ThenGivesUp()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            15
        );
        var probe = new RuleProbe(engine, ground);
        var rule = new AnswerTaxiOutRule();
        double now = engine.Scenario!.ElapsedSeconds + AiPacing.ThinkMaxSeconds;
        rule.Evaluate(probe.Scope([aircraft], now - AiPacing.ThinkMaxSeconds));

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            rule.Evaluate(probe.Scope([aircraft], now));
            Assert.Equal(attempt, probe.Sink.Issued.Count);
            AiAircraftMemo memo = probe.Memos[AiTestFixture.Callsign];
            memo.Complete(success: false, now + 1);
            Assert.Equal(attempt, memo.Rejections);
            // Backed off: an evaluation inside the backoff issues nothing.
            rule.Evaluate(probe.Scope([aircraft], now + 2));
            Assert.Equal(attempt, probe.Sink.Issued.Count);
            now += 1 + (AiAircraftMemo.RetryBackoffSeconds * attempt);
        }

        Assert.True(probe.Memos[AiTestFixture.Callsign].GaveUp);
        rule.Evaluate(probe.Scope([aircraft], now + 1000));
        Assert.Equal(3, probe.Sink.Issued.Count);
    }

    [Fact]
    public void RunwayCrossing_CombinedPosition_ClearsEachCrossingInTurn_NamingTheNearEnd_ThenReachesTheDepartureRunway()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30").Success);
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        var crossings = route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing).ToList();
        Assert.Equal(2, crossings.Count);
        var probe = new RuleProbe(engine, ground);
        var rule = new RunwayCrossingRule();
        var issuedAt = new List<(string Canonical, int? PendingNode, bool FirstBarCleared)>();

        for (
            int t = 0;
            t < 900 && aircraft.Phases?.CurrentPhase is not HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway };
            t++
        )
        {
            engine.TickOneSecond();
            AirportGroundLayout? layout = engine.ResolveGroundLayout(aircraft);
            rule.Evaluate(probe.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
            foreach (AiCommandRequest? request in probe.Sink.Issued.Skip(issuedAt.Count).ToList())
            {
                PendingHoldShort? pending = TaxiRouteProgress.NextUnclearedCrossing(aircraft, layout);
                Assert.NotNull(pending);
                Assert.True(
                    pending.DistanceFt <= RunwayCrossingRule.PreClearDistanceFt,
                    $"issued {request.Canonical} {pending.DistanceFt:F0} ft early"
                );
                Assert.Equal($"CROSS {TaxiRouteProgress.NearestCrossingEnd(aircraft, pending.Point.TargetName!, layout)}", request.Canonical);
                issuedAt.Add((request.Canonical, pending.Point.NodeId, crossings[0].IsCleared));
                Assert.True(engine.SendCommand(AiTestFixture.Callsign, request.Canonical).Success, request.Canonical);
                probe.Memos[AiTestFixture.Callsign].Complete(success: true, engine.Scenario.ElapsedSeconds);
            }
        }

        Assert.Equal(2, issuedAt.Count);
        Assert.Equal(crossings[0].NodeId, issuedAt[0].PendingNode);
        Assert.Equal(crossings[1].NodeId, issuedAt[1].PendingNode);
        Assert.False(issuedAt[0].FirstBarCleared);
        // One crossing at a time (7110.65 §3-7-2.a.3): the second is cleared only after the first bar was.
        Assert.True(issuedAt[1].FirstBarCleared);
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(GroundIntent.None, probe.Memos[AiTestFixture.Callsign].Intent);
    }

    [Fact]
    public void RunwayCrossing_NeverNamesTheDepartureRunwayBar()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 28R").Success);
        AircraftState aircraft = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 300);
        Assert.Equal(HoldShortReason.DestinationRunway, ((HoldingShortPhase)aircraft.Phases!.CurrentPhase!).HoldShort.Reason);
        var probe = new RuleProbe(engine, ground);

        // The departure bar is Local's; the crossing rule has nothing to clear. (The engine would accept a CROSS here and
        // taxi the aircraft across its own departure runway — the trap the rule must never fall into.)
        Assert.Null(TaxiRouteProgress.NextUnclearedCrossing(aircraft, engine.ResolveGroundLayout(aircraft)));
        new RunwayCrossingRule().Evaluate(probe.Scope([aircraft], engine.Scenario!.ElapsedSeconds + 100));
        Assert.Empty(probe.Sink.Issued);
    }

    [Fact]
    public void RunwayCrossingGate_ArrivalInsideTheFinalGateBlocks_BeyondItDoesNot()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        foreach ((double distanceNm, bool expectClear) in new[] { (1.5, false), (6.0, true) })
        {
            SimulationEngine engine = AiTestFixture.Load(
                TwoAircraftOnFinalTemplate.Replace("DISTANCE", distanceNm.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                _zoa,
                7,
                [ground]
            );
            AircraftState crosser = engine.FindAircraft(AiTestFixture.Callsign)!;
            AircraftState arrival = engine.FindAircraft("N2FL")!;
            AirportGroundLayout? layout = engine.ResolveGroundLayout(crosser);
            RunwayInfo? pavement = RunwayCrossingGate.PavementFor("28R/10L", RunwayOccupancy.AirportRunways("OAK"));
            Assert.NotNull(pavement);

            bool clear = RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out string? reason);

            Assert.Equal(expectClear, clear);
            if (!expectClear)
            {
                Assert.Contains("N2FL", reason);
            }

            // The same geometry two miles up is an overflight, not an arrival: a departed jet climbing out over the
            // field must not close the runway to crossings.
            AircraftState overflight = AiTestFixture.Airborne(
                "SWA919",
                arrival.Position.Lat,
                arrival.Position.Lon,
                pavement.AirportElevationFt + 10_000
            );
            overflight.TrueHeading = arrival.TrueHeading;
            Assert.True(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, overflight], layout, out _));
        }
    }

    [Fact]
    public void RunwayCrossingGate_FoulerPastTheBar_DoesNotCloseTheCrossing()
    {
        // The crossing gate is physical: an aircraft held with its nose over a 28R hold line is off the pavement, and the
        // ground-conflict detector, not the gate, keeps the crosser clear of it.
        if ((_zoa is null) || (RunwayBarFixture.RunwayBar("KOAK", "28R") is not { } oak))
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        AircraftState crosser = engine.FindAircraft(AiTestFixture.Callsign)!;
        RunwayInfo pavement = RunwayCrossingGate.PavementFor("28R/10L", RunwayOccupancy.AirportRunways("OAK"))!;
        AirportGroundLayout? layout = engine.ResolveGroundLayout(crosser);
        AircraftState fouler = RunwayOccupancyTests.SquareToTheMarking(oak.Runway, oak.Bar, "B738", facingRunway: true, 30.0, alongFt: 0);
        fouler.Phases = new PhaseList();
        fouler.Phases.Add(new TaxiingPhase());
        fouler.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        fouler.Ground.Hold = HoldDirective.HoldPosition;
        RunwayInfo end = RunwayOccupancy.AlignedEnd(fouler.TrueTrack.Degrees, pavement);
        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(fouler, end, layout)?.Kind);

        Assert.True(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, fouler], layout, out string? reason), reason);
    }

    [Fact]
    public void RunwayCrossingGate_LandingAndRolloutBlock_AHoldOnTheCrosserBlocks()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(TwoAircraftOnFinalTemplate.Replace("DISTANCE", "1.5"), _zoa, 7, [ground]);
        Assert.True(engine.SendCommand("N2FL", "CLAND").Success);
        AircraftState crosser = engine.FindAircraft(AiTestFixture.Callsign)!;
        RunwayInfo pavement = RunwayCrossingGate.PavementFor("28R/10L", RunwayOccupancy.AirportRunways("OAK"))!;
        AirportGroundLayout? layout = engine.ResolveGroundLayout(crosser);

        AircraftState arrival = AiTestFixture.TickUntil(engine, "N2FL", ac => ac.IsOnGround, 200);
        Assert.False(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out string? rollout));
        Assert.Contains("N2FL", rollout);

        arrival = AiTestFixture.TickUntil(engine, "N2FL", ac => ac.Phases?.CurrentPhase is HoldingAfterExitPhase, 300);
        Assert.True(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out _));

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30").Success);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "HOLD").Success);
        Assert.False(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out string? held));
        Assert.Contains("hold", held);
    }

    [Fact]
    public void RunwayCrossing_WithLocalStaffed_AsksOnceOnTheTerminal_ThenTimesOut()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground, tower]);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30").Success);
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.RunwayCrossing },
            400
        );
        aircraft.PendingWarnings.Clear();
        var probe = new RuleProbe(engine, ground, tower);
        var rule = new RunwayCrossingRule();
        double now = engine.Scenario!.ElapsedSeconds;

        // The request is a transmission: it waits for the think time like any other.
        rule.Evaluate(probe.Scope([aircraft], now));
        Assert.Empty(aircraft.PendingWarnings);
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        Assert.Empty(probe.Sink.Issued);
        string line = Assert.Single(aircraft.PendingWarnings);
        Assert.StartsWith("[AI-COORD] OAK_GND requests cross runway ", line);
        Assert.Contains(" at B for ", line);
        Assert.EndsWith($" for {AiTestFixture.Callsign}", line);
        Assert.Equal(GroundIntent.CrossingRequested, probe.Memos[AiTestFixture.Callsign].Intent);
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());

        // Past the timeout: the anomaly opens and Ground asks again rather than going silent.
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + RunwayCrossingRule.CoordinationTimeoutSeconds));
        AiAnomalyEvent opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.CoordinationTimeout, opened.Kind);
        Assert.Equal(AiTestFixture.Callsign, opened.SubjectKey);
        Assert.Equal(2, aircraft.PendingWarnings.Count);

        // The human clears it: the request is satisfied and the episode closes.
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CROSS 28R").Success);
        AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is not HoldingShortPhase, 30);
        rule.Evaluate(probe.Scope([aircraft], engine.Scenario.ElapsedSeconds));
        IReadOnlyList<AiAnomalyEvent> closed = engine.Scenario.AiAnomalies.Drain();
        Assert.Contains(closed, e => e.Kind == AiAnomalyKind.CoordinationTimeout && e.Event == AiAnomalyEventKind.Closed);
    }

    [Fact]
    public void HandToLocal_TransfersShortOfTheDepartureRunway_WithNoCrossingAhead()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        SimulationEngine engine = AiTestFixture.Load(
            AiTestFixture.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"29\""),
            _zoa,
            7,
            [ground, tower]
        );
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30").Success);
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.DoesNotContain(aircraft.Ground.AssignedTaxiRoute!.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing);
        var probe = new RuleProbe(engine, ground, tower);
        var rule = new HandToLocalRule();
        double? distanceAtIssue = null;
        bool wasTaxiing = false;

        for (int t = 0; t < 400 && probe.Sink.Issued.Count == 0; t++)
        {
            engine.TickOneSecond();
            AirportGroundLayout? layout = engine.ResolveGroundLayout(aircraft);
            double? before = TaxiRouteProgress.DistanceToDestinationBarFt(aircraft, layout);
            rule.Evaluate(probe.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
            if (probe.Sink.Issued.Count > 0)
            {
                distanceAtIssue = before;
                wasTaxiing = aircraft.Phases?.CurrentPhase is TaxiingPhase;
            }
        }

        AiCommandRequest request = Assert.Single(probe.Sink.Issued);
        Assert.Equal("CT OAK_TWR", request.Canonical);
        Assert.True(wasTaxiing);
        Assert.NotNull(distanceAtIssue);
        Assert.InRange(distanceAtIssue.Value, 0, HandToLocalRule.HandoffDistanceFt);
        Assert.Equal(GroundIntent.HandedToLocal, probe.Memos[AiTestFixture.Callsign].Intent);

        // Once handed off, never again for this aircraft.
        probe.Memos[AiTestFixture.Callsign].Complete(success: true, engine.Scenario!.ElapsedSeconds);
        rule.Evaluate(probe.Scope([aircraft], engine.Scenario.ElapsedSeconds + 30));
        Assert.Single(probe.Sink.Issued);
    }

    [Fact]
    public void HandToLocal_WaitsWhileACrossingIsStillAhead()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground, tower]);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30").Success);
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        var probe = new RuleProbe(engine, ground, tower);
        var rule = new HandToLocalRule();

        // Nobody clears the crossings: the aircraft stops at the first bar and Ground never transfers it.
        for (int t = 0; t < 300; t++)
        {
            engine.TickOneSecond();
            rule.Evaluate(probe.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Empty(probe.Sink.Issued);
    }

    [Fact]
    public void HandToLocal_TransfersNothingInACombinedCab()
    {
        if (_zoa is null)
        {
            return;
        }

        // Nobody holds Local: Ground works the runway itself and there is no tower to send the pilot to (7110.65 §2-1-17.a).
        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(
            AiTestFixture.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"29\""),
            _zoa,
            7,
            [ground]
        );
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30").Success);
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        var probe = new RuleProbe(engine, ground);
        var rule = new HandToLocalRule();

        for (int t = 0; t < 400 && aircraft.Phases?.CurrentPhase is not HoldingShortPhase; t++)
        {
            engine.TickOneSecond();
            rule.Evaluate(probe.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Empty(probe.Sink.Issued);
    }

    [Fact]
    public void RunwayCrossingGate_AGoAroundOverTheRunwayBlocks()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(TwoAircraftOnFinalTemplate.Replace("DISTANCE", "1.5"), _zoa, 7, [ground]);
        Assert.True(engine.SendCommand("N2FL", "GA").Success);
        AircraftState crosser = engine.FindAircraft(AiTestFixture.Callsign)!;
        RunwayInfo pavement = RunwayCrossingGate.PavementFor("28R/10L", RunwayOccupancy.AirportRunways("OAK"))!;
        AirportGroundLayout? layout = engine.ResolveGroundLayout(crosser);

        // Climbing down the runway: invisible to the on-final test, a runway user all the same (7110.65 §3-7-2.a.7.1).
        AircraftState goingAround = AiTestFixture.TickUntil(engine, "N2FL", ac => RunwayOccupancy.IsWithinPavement(ac.Position, pavement), 240);
        Assert.False(goingAround.IsOnGround);
        Assert.InRange(goingAround.Altitude - pavement.AirportElevationFt, 0, RunwayCrossingGate.OverRunwayMaxAglFt);
        Assert.False(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, goingAround], layout, out string? reason));
        Assert.Contains("over runway", reason);
    }

    [Fact]
    public void AnswerTaxiIn_TaxiesTheArrivalToTheParkingItAskedFor_OrRepicksATakenOne()
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
        string requested = aircraft.PendingPilotRequest!.ParkingName!;
        var probe = new RuleProbe(engine, ground);
        var rule = new AnswerTaxiInRule();
        double now = engine.Scenario!.ElapsedSeconds;

        rule.Evaluate(probe.Scope([aircraft], now));
        rule.Evaluate(probe.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        AiCommandRequest request = Assert.Single(probe.Sink.Issued);
        Assert.Equal($"TAXIAUTO @{requested}", request.Canonical);
        Assert.Equal(GroundIntent.TaxiInIssued, probe.Memos[AiTestFixture.Callsign].Intent);

        // The same request with the spot now claimed by someone else: the pilot's next choice instead.
        var claimant = new AircraftState
        {
            Callsign = "N9CL",
            AircraftType = "C172",
            IsOnGround = true,
            Position = aircraft.Position,
            AirportId = "OAK",
            PendingPilotRequest = new PilotPendingRequest
            {
                Kind = PilotPendingRequestKind.Taxi,
                FirstRequestedAtSeconds = now,
                LastPilotLine = "",
                LastPilotLineTts = "",
                ParkingName = requested,
            },
        };
        var fresh = new RuleProbe(engine, ground);
        rule.Evaluate(fresh.Scope([aircraft, claimant], now + 100));
        rule.Evaluate(fresh.Scope([aircraft, claimant], now + 100 + AiPacing.ThinkMaxSeconds));
        AiCommandRequest repicked = Assert.Single(fresh.Sink.Issued);
        Assert.StartsWith("TAXIAUTO @", repicked.Canonical);
        Assert.NotEqual(request.Canonical, repicked.Canonical);
        Assert.Contains("is taken", repicked.Intent.Rationale);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, repicked.Canonical).Success);
    }

    /// <summary>Everything one rule needs to run alone: a recording sink, one pacing, one memo table, and scopes at chosen times.</summary>
    private sealed class RuleProbe(SimulationEngine engine, params AiPositionConfig[] staffed)
    {
        public RecordingAiCommandSink Sink { get; } = new();

        public Dictionary<string, AiAircraftMemo> Memos { get; } = new(StringComparer.Ordinal);

        private readonly AiPacing _pacing = new();

        public AiRuleScope Scope(IReadOnlyList<AircraftState> aircraft, double now)
        {
            _pacing.BeginTick();
            AiTickContext context = AiTestFixture.Context(engine, aircraft, staffed, now, [], Sink);
            return new AiRuleScope
            {
                Tick = context,
                Position = staffed[0],
                Jurisdiction = context.View.Jurisdiction(staffed[0]),
                Memos = Memos,
                Pacing = _pacing,
            };
        }
    }
}
