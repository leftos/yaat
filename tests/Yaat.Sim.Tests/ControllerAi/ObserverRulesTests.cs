using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>The four observer watchdogs, each driven through a hand-built tick context so the clock can jump.</summary>
public class ObserverRulesTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ObserverRulesTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void StuckAircraft_OpensWhenATaxiingAircraftStopsMoving_AndClosesWhenItMovesAgain()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 28R").Success);
        var taxiing = AiTestHost.TickUntil(engine, AiTestHost.Callsign, ac => ac.Phases?.CurrentPhase is TaxiingPhase, 30);
        var rule = new StuckAircraftRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        double now = engine.Scenario!.ElapsedSeconds;

        // Anchor, then the same position 200 s later: stuck.
        rule.Evaluate(Scope(engine, [taxiing], ground, now, memos));
        rule.Evaluate(Scope(engine, [taxiing], ground, now + StuckAircraftRule.StuckAfterSeconds + 20, memos));
        var opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.StuckAircraft, opened.Kind);
        Assert.Equal(ground.PositionId, opened.PositionId);
        Assert.Equal(AiTestHost.Callsign, opened.SubjectKey);
        Assert.Contains("Taxiing", opened.Detail);

        // It taxis on: the episode closes.
        AiTestHost.Tick(engine, 40);
        var moved = engine.FindAircraft(AiTestHost.Callsign)!;
        rule.Evaluate(Scope(engine, [moved], ground, engine.Scenario.ElapsedSeconds + 300, memos));
        var closed = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyEventKind.Closed, closed.Event);
    }

    [Fact]
    public void StuckAircraft_IgnoresACommandedHold_AndWaitsLongerForAYield()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 28R").Success);
        var taxiing = AiTestHost.TickUntil(engine, AiTestHost.Callsign, ac => ac.Phases?.CurrentPhase is TaxiingPhase, 30);
        var rule = new StuckAircraftRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        double now = engine.Scenario!.ElapsedSeconds;

        // HOLD is sequencing (7110.65 3-8-1), not a stall: a held taxier never trips, however long it sits.
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "HOLD").Success);
        Assert.NotNull(taxiing.Ground.Hold);
        rule.Evaluate(Scope(engine, [taxiing], ground, now, memos));
        rule.Evaluate(Scope(engine, [taxiing], ground, now + 1000, memos));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());

        // A detector-imposed stop (yielding to traffic ahead) is a departure queue: 180 s is routine, 600 s is not.
        taxiing.Ground.Hold = null;
        taxiing.Ground.SpeedLimit = 0;
        taxiing.Ground.AutoYieldTarget = "UAL1";
        rule.Evaluate(Scope(engine, [taxiing], ground, now, memos));
        rule.Evaluate(Scope(engine, [taxiing], ground, now + StuckAircraftRule.StuckAfterSeconds + 20, memos));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());
        rule.Evaluate(Scope(engine, [taxiing], ground, now + StuckAircraftRule.YieldingStuckAfterSeconds + 1, memos));
        var opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Contains("yielding to UAL1", opened.Detail);
    }

    [Fact]
    public void StuckAircraft_NeverFlagsAnAircraftWaitingForACommand()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        var parked = engine.FindAircraft(AiTestHost.Callsign)!;
        var rule = new StuckAircraftRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);

        rule.Evaluate(Scope(engine, [parked], ground, 0, memos));
        rule.Evaluate(Scope(engine, [parked], ground, 1000, memos));

        Assert.Empty(engine.Scenario!.AiAnomalies.Drain());
    }

    [Fact]
    public void UnansweredPilotRequest_OpensPastTheFollowUpHorizon_AndClosesWhenAnswered()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        AiTestHost.Tick(engine, 7);
        var parked = engine.FindAircraft(AiTestHost.Callsign)!;
        Assert.True(parked.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi });
        var rule = new UnansweredPilotRequestRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        double asked = parked.PendingPilotRequest.FirstRequestedAtSeconds;

        // Open but not yet re-asked (a STANDBY re-bases the pilot's clock the same way): not overdue, however old.
        rule.Evaluate(Scope(engine, [parked], ground, asked + PilotRequestTracker.NormalFollowUpDelaySeconds + 30, memos));
        Assert.Empty(engine.Scenario!.AiAnomalies.Drain());

        // The pilot follows up on its own clock: now the request is one the controller has ignored twice.
        AiTestHost.TickUntil(engine, AiTestHost.Callsign, ac => ac.PendingPilotRequest!.LastRequestedAtSeconds > asked, 200);
        rule.Evaluate(Scope(engine, [parked], ground, engine.Scenario.ElapsedSeconds, memos));
        var opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.UnansweredPilotRequest, opened.Kind);
        Assert.Contains("Taxi", opened.Detail);

        Assert.True(engine.DispatchAiCommand(ground, AiTestHost.Callsign, "TAXIAUTO 28R").Success);
        rule.Evaluate(Scope(engine, [parked], ground, engine.Scenario.ElapsedSeconds + 1, memos));
        Assert.Equal(AiAnomalyEventKind.Closed, Assert.Single(engine.Scenario.AiAnomalies.Drain()).Event);
    }

    [Fact]
    public void UnansweredPilotRequest_NeverOverdue_WhileTheDepartureIsHeldForRelease()
    {
        if (_zoa is null)
        {
            return;
        }

        var tower = TestAiPositions.OakTower(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        var linedUp = new AircraftState
        {
            Callsign = "N7",
            AircraftType = "C172",
            Position = new LatLon(37.7213, -122.2208),
            IsOnGround = true,
            AirportId = "OAK",
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                Departure = "KOAK",
                Destination = "KLAX",
            },
            Phases = new PhaseList(),
            PendingPilotRequest = new PilotPendingRequest
            {
                Kind = PilotPendingRequestKind.Takeoff,
                FirstRequestedAtSeconds = 0,
                LastRequestedAtSeconds = 130,
                LastPilotLine = "ready for departure",
                LastPilotLineTts = "ready for departure",
            },
        };
        linedUp.Phases.Add(new LinedUpAndWaitingPhase());
        linedUp.Ground.HeldForRelease = true;
        var rule = new UnansweredPilotRequestRule();

        rule.Evaluate(ConflictScope(engine, [linedUp], tower, 300, []));
        Assert.Empty(engine.Scenario!.AiAnomalies.Drain());

        linedUp.Ground.HeldForRelease = false;
        rule.Evaluate(ConflictScope(engine, [linedUp], tower, 301, []));
        Assert.Equal(AiAnomalyKind.UnansweredPilotRequest, Assert.Single(engine.Scenario.AiAnomalies.Drain()).Kind);
    }

    [Fact]
    public void UnansweredPilotRequest_IsNotTheTowersProblem()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        AiTestHost.Tick(engine, 7);
        var parked = engine.FindAircraft(AiTestHost.Callsign)!;

        // The parked aircraft is in Ground's jurisdiction; evaluated for the tower it is simply not the tower's.
        new UnansweredPilotRequestRule().Evaluate(ConflictScope(engine, [parked], tower, 500, []));

        Assert.Empty(engine.Scenario!.AiAnomalies.Drain());
    }

    [Fact]
    public void HandoffUnaccepted_OpensAfterTheAutoAcceptDelayPlusGrace_ForTheRadarPositionInvolved()
    {
        if (_zoa is null)
        {
            return;
        }

        var approach = TestAiPositions.NorCalApproach(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        var aircraft = AiTestHost.Airborne("AAL1", 37.9, -122.0, 8000);
        aircraft.Track.Owner = approach.Identity;
        aircraft.Track.HandoffPeer = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");
        aircraft.Track.OnHandoff = true;
        aircraft.Track.HandoffInitiatedAt = 100;
        var rule = new HandoffUnacceptedRule();
        double horizon = engine.Scenario!.AutoAcceptDelay.TotalSeconds + HandoffUnacceptedRule.GraceSeconds;

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon - 1, []));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon + 1, []));
        var opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.HandoffUnaccepted, opened.Kind);
        Assert.Contains("OAK_14_CTR", opened.Detail);

        aircraft.Track.HandoffAccepted = true;
        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon + 2, []));
        Assert.Equal(AiAnomalyEventKind.Closed, Assert.Single(engine.Scenario.AiAnomalies.Drain()).Event);

        // A cab position never tracks: no-op even with the handoff pending.
        aircraft.Track.HandoffAccepted = false;
        rule.Evaluate(ConflictScope(engine, [aircraft], TestAiPositions.OakTower(_zoa), 100 + horizon + 3, []));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());
    }

    [Fact]
    public void ConflictAlert_OpensPerConflictIdInTheJurisdiction_AndClosesWhenTheAlertClears()
    {
        if (_zoa is null)
        {
            return;
        }

        var approach = TestAiPositions.NorCalApproach(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        var a = AiTestHost.Airborne("AAL1", 37.9, -122.0, 8000);
        var b = AiTestHost.Airborne("UAL2", 37.9, -122.01, 8000);
        a.Track.Owner = approach.Identity;
        var conflict = new ActiveConflict
        {
            Id = "AAL1|UAL2",
            CallsignA = "AAL1",
            CallsignB = "UAL2",
        };
        var rule = new ConflictAlertInAiJurisdictionRule();

        rule.Evaluate(ConflictScope(engine, [a, b], approach, 10, [conflict]));
        var opened = Assert.Single(engine.Scenario!.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.ConflictAlertInAiJurisdiction, opened.Kind);
        Assert.Equal("AAL1|UAL2", opened.SubjectKey);

        rule.Evaluate(ConflictScope(engine, [a, b], approach, 40, []));
        var closed = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyEventKind.Closed, closed.Event);
        Assert.Equal(30, closed.DurationSeconds);

        // Neither aircraft in the position's jurisdiction: not its finding.
        a.Track.Owner = null;
        rule.Evaluate(ConflictScope(engine, [a, b], approach, 50, [conflict]));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());

        // An acknowledged alert is a controller who has seen it: the episode closes.
        a.Track.Owner = approach.Identity;
        rule.Evaluate(ConflictScope(engine, [a, b], approach, 60, [conflict]));
        Assert.Equal(AiAnomalyEventKind.Opened, Assert.Single(engine.Scenario.AiAnomalies.Drain()).Event);
        conflict.IsAcknowledged = true;
        rule.Evaluate(ConflictScope(engine, [a, b], approach, 70, [conflict]));
        Assert.Equal(AiAnomalyEventKind.Closed, Assert.Single(engine.Scenario.AiAnomalies.Drain()).Event);
    }

    private static AiRuleScope Scope(
        SimulationEngine engine,
        IReadOnlyList<AircraftState> aircraft,
        AiPositionConfig position,
        double now,
        Dictionary<string, AiAircraftMemo> memos
    ) => Scope(engine, aircraft, position, now, memos, []);

    private static AiRuleScope ConflictScope(
        SimulationEngine engine,
        IReadOnlyList<AircraftState> aircraft,
        AiPositionConfig position,
        double now,
        IReadOnlyList<ActiveConflict> conflicts
    ) => Scope(engine, aircraft, position, now, new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal), conflicts);

    private static AiRuleScope Scope(
        SimulationEngine engine,
        IReadOnlyList<AircraftState> aircraft,
        AiPositionConfig position,
        double now,
        Dictionary<string, AiAircraftMemo> memos,
        IReadOnlyList<ActiveConflict> conflicts
    )
    {
        var context = AiTestHost.Context(engine, aircraft, [position], now, conflicts);
        return new AiRuleScope
        {
            Tick = context,
            Position = position,
            Jurisdiction = context.View.Jurisdiction(position),
            Memos = memos,
        };
    }
}
