using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>The four observer watchdogs, each driven through a hand-built tick context so the clock can jump.</summary>
public class ObserverRulesTests
{
    private static readonly TrackOwner Nct2B = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Nct4Q = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");

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

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 28R").Success);
        AircraftState taxiing = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is TaxiingPhase, 30);
        var rule = new StuckAircraftRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        double now = engine.Scenario!.ElapsedSeconds;

        // Anchor, then the same position 200 s later: stuck.
        rule.Evaluate(Scope(engine, [taxiing], ground, now, memos));
        rule.Evaluate(Scope(engine, [taxiing], ground, now + StuckAircraftRule.StuckAfterSeconds + 20, memos));
        AiAnomalyEvent opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.StuckAircraft, opened.Kind);
        Assert.Equal(ground.PositionId, opened.PositionId);
        Assert.Equal(AiTestFixture.Callsign, opened.SubjectKey);
        Assert.Contains("Taxiing", opened.Detail);

        // It taxis on: the episode closes.
        AiTestFixture.Tick(engine, 40);
        AircraftState moved = engine.FindAircraft(AiTestFixture.Callsign)!;
        rule.Evaluate(Scope(engine, [moved], ground, engine.Scenario.ElapsedSeconds + 300, memos));
        AiAnomalyEvent closed = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyEventKind.Closed, closed.Event);
    }

    [Fact]
    public void StuckAircraft_IgnoresACommandedHold_AndWaitsLongerForAYield()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 28R").Success);
        AircraftState taxiing = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is TaxiingPhase, 30);
        var rule = new StuckAircraftRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        double now = engine.Scenario!.ElapsedSeconds;

        // HOLD is sequencing (7110.65 3-8-1), not a stall: a held taxier never trips, however long it sits.
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "HOLD").Success);
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
        AiAnomalyEvent opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Contains("yielding to UAL1", opened.Detail);

        // The detector lifts its limit a moment before the aircraft rolls: a stall that yielded keeps the longer threshold
        // even when the limit is gone at the instant the rule looks.
        var sticky = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        engine.Scenario.AiAnomalies.Clear();
        taxiing.Ground.SpeedLimit = 0;
        rule.Evaluate(Scope(engine, [taxiing], ground, now, sticky));
        taxiing.Ground.SpeedLimit = null;
        taxiing.Ground.AutoYieldTarget = null;
        rule.Evaluate(Scope(engine, [taxiing], ground, now + StuckAircraftRule.StuckAfterSeconds + 120, sticky));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());
        rule.Evaluate(Scope(engine, [taxiing], ground, now + StuckAircraftRule.YieldingStuckAfterSeconds + 1, sticky));
        Assert.Contains("after yielding", Assert.Single(engine.Scenario.AiAnomalies.Drain()).Detail);
    }

    /// <summary>
    /// A give-way still pending on the landing runway never stops the aircraft there, so it is not an ordered stop: an arrival that
    /// goes nowhere on its exit under one is a stuck exit and trips the watchdog, while the same stop under an ordered hold is
    /// sequencing (7110.65 §3-8-1) and never trips it.
    /// </summary>
    [Fact]
    public void StuckAircraft_RaisesAStalledExitUnderAPendingGiveWay_ButNotUnderAnOrderedHold()
    {
        if (_zoa is null)
        {
            return;
        }

        // A runway exit is on the runway still: local control's (7110.65 §3-1-3), so the tower position owns it.
        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        ShortFinalArrival.Spawned spawned =
            ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "B738", "ARR1", 1.0) ?? throw new Xunit.Sdk.XunitException("no arrival");
        AircraftState arrival = spawned.Aircraft;
        int exit = SfoGroundHarness.TickUntil(spawned.Engine, () => arrival.Phases?.CurrentPhase is RunwayExitPhase, 400, null);
        Assert.True(exit > 0, $"never reached the exit: {arrival.Phases?.CurrentPhase?.Name}");

        var rule = new StuckAircraftRule();
        double now = spawned.Engine.Scenario!.ElapsedSeconds;

        // The give-way was given on the rollout and is still pending: nothing has judged or aged it, and it is not a stop.
        arrival.Ground.Hold = HoldDirective.GiveWay("UAL123");
        Assert.True(arrival.HasGiveWayPendingOnLandingRunway, $"no pending give-way in {arrival.Phases?.CurrentPhase?.Name}");
        var pending = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        AiRuleScope scope = Scope(spawned.Engine, [arrival], tower, now, pending);
        Assert.Equal("ARR1", Assert.Single(scope.Jurisdiction).Callsign);
        rule.Evaluate(scope);
        rule.Evaluate(Scope(spawned.Engine, [arrival], tower, now + StuckAircraftRule.StuckAfterSeconds + 20, pending));
        AiAnomalyEvent opened = Assert.Single(spawned.Engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.StuckAircraft, opened.Kind);
        Assert.Contains("Runway Exit", opened.Detail);

        // The same stop under an ordered hold is a controller-sequenced aircraft, not a stall.
        spawned.Engine.Scenario.AiAnomalies.Clear();
        arrival.Ground.Hold = HoldDirective.HoldPosition;
        Assert.False(arrival.HasGiveWayPendingOnLandingRunway);
        var held = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        rule.Evaluate(Scope(spawned.Engine, [arrival], tower, now, held));
        rule.Evaluate(Scope(spawned.Engine, [arrival], tower, now + 1000, held));
        Assert.Empty(spawned.Engine.Scenario.AiAnomalies.Drain());
    }

    [Fact]
    public void StuckAircraft_NeverFlagsAnAircraftWaitingForACommand()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        AircraftState parked = engine.FindAircraft(AiTestFixture.Callsign)!;
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

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        AiTestFixture.Tick(engine, 7);
        AircraftState parked = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.True(parked.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi });
        var rule = new UnansweredPilotRequestRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);
        double asked = parked.PendingPilotRequest.FirstRequestedAtSeconds;

        // Open but not yet re-asked (a STANDBY re-bases the pilot's clock the same way): not overdue, however old.
        rule.Evaluate(Scope(engine, [parked], ground, asked + PilotRequestTracker.NormalFollowUpDelaySeconds + 30, memos));
        Assert.Empty(engine.Scenario!.AiAnomalies.Drain());

        // The pilot follows up on its own clock: now the request is one the controller has ignored twice.
        AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.PendingPilotRequest!.LastRequestedAtSeconds > asked, 200);
        rule.Evaluate(Scope(engine, [parked], ground, engine.Scenario.ElapsedSeconds, memos));
        AiAnomalyEvent opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.UnansweredPilotRequest, opened.Kind);
        Assert.Contains("Taxi", opened.Detail);

        Assert.True(engine.DispatchAiCommand(ground, AiTestFixture.Callsign, "TAXIAUTO 28R").Success);
        rule.Evaluate(Scope(engine, [parked], ground, engine.Scenario.ElapsedSeconds + 1, memos));
        Assert.Equal(AiAnomalyEventKind.Closed, Assert.Single(engine.Scenario.AiAnomalies.Drain()).Event);
    }

    /// <summary>
    /// A pushback request answered "standby" and asked again 90 s later is an acknowledged wait (P/CG STAND BY), not an
    /// unanswered request; it is flagged once it goes unanswered after that re-ask and the pilot asks once more.
    /// </summary>
    [Fact]
    public void UnansweredPilotRequest_ReaskAfterStandby_OpensNothing_UntilUnansweredAgain()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        (SimulationEngine engine, AircraftState parked) = PushbackReaskedAfterStandby(ground, standby: true);
        var rule = new UnansweredPilotRequestRule();
        var memos = new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal);

        rule.Evaluate(Scope(engine, [parked], ground, engine.Scenario!.ElapsedSeconds, memos));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());

        double reaskedAt = parked.PendingPilotRequest!.LastRequestedAtSeconds;
        AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.PendingPilotRequest!.LastRequestedAtSeconds > reaskedAt, 200);
        Assert.False(parked.PendingPilotRequest.ReaskedAfterStandby);
        rule.Evaluate(Scope(engine, [parked], ground, engine.Scenario.ElapsedSeconds, memos));
        AiAnomalyEvent opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.UnansweredPilotRequest, opened.Kind);
        Assert.Contains("Pushback", opened.Detail);
    }

    /// <summary>A pushback request never answered is flagged at its first re-ask, as any request is.</summary>
    [Fact]
    public void UnansweredPilotRequest_PushbackNeverAnswered_OpensAtTheReask()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        (SimulationEngine engine, AircraftState parked) = PushbackReaskedAfterStandby(ground, standby: false);
        Assert.False(parked.PendingPilotRequest!.ReaskedAfterStandby);

        new UnansweredPilotRequestRule().Evaluate(
            Scope(engine, [parked], ground, engine.Scenario!.ElapsedSeconds, new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal))
        );

        Assert.Equal(AiAnomalyKind.UnansweredPilotRequest, Assert.Single(engine.Scenario.AiAnomalies.Drain()).Kind);
    }

    /// <summary>A request re-asked after a standby keeps that through a snapshot round trip: restored mid-wait, it opens nothing.</summary>
    [Fact]
    public void UnansweredPilotRequest_ReaskAfterStandby_SurvivesASnapshotRoundTrip()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        (SimulationEngine engine, AircraftState parked) = PushbackReaskedAfterStandby(ground, standby: true);

        var restored = AircraftState.FromSnapshot(parked.ToSnapshot(), parked.Ground.Layout);
        Assert.True(restored.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback, ReaskedAfterStandby: true });
        new UnansweredPilotRequestRule().Evaluate(
            Scope(engine, [restored], ground, engine.Scenario!.ElapsedSeconds, new Dictionary<string, AiAircraftMemo>(StringComparer.Ordinal))
        );

        Assert.Empty(engine.Scenario.AiAnomalies.Drain());
    }

    /// <summary>
    /// A B738 parked on KOAK's push-back stand S7 with the pushback request its stand call opened, answered "standby" when
    /// <paramref name="standby"/>, ticked until the pilot asks again on its own clock.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Parked) PushbackReaskedAfterStandby(AiPositionConfig ground, bool standby)
    {
        string jetAtS7 = AiTestFixture
            .ParkedAtOak.Replace("\"C172\"", "\"B738\"", StringComparison.Ordinal)
            .Replace("\"cruiseSpeed\": 100", "\"cruiseSpeed\": 430", StringComparison.Ordinal)
            .Replace("\"parking\": \"SIG1\"", "\"parking\": \"S7\"", StringComparison.Ordinal);
        SimulationEngine engine = AiTestFixture.Load(jetAtS7, _zoa!, 7, [ground]);
        AircraftState parked = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback },
            15
        );
        double asked = parked.PendingPilotRequest!.LastRequestedAtSeconds;
        if (standby)
        {
            Assert.True(engine.DispatchAiCommand(ground, AiTestFixture.Callsign, "STBY").Success);
            Assert.Equal(PilotPendingRequestResponseState.Standby, parked.PendingPilotRequest!.ResponseState);
        }

        AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.PendingPilotRequest!.LastRequestedAtSeconds > asked, 200);
        Assert.True(
            parked.PendingPilotRequest
                is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback, ResponseState: PilotPendingRequestResponseState.None }
        );
        Assert.Equal(standby, parked.PendingPilotRequest.ReaskedAfterStandby);
        return (engine, parked);
    }

    [Fact]
    public void UnansweredPilotRequest_NeverOverdue_WhileTheDepartureIsHeldForRelease()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
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

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        AiTestFixture.Tick(engine, 7);
        AircraftState parked = engine.FindAircraft(AiTestFixture.Callsign)!;

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

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        AircraftState aircraft = AiTestFixture.Airborne("AAL1", 37.9, -122.0, 8000);
        aircraft.Track.Owner = approach.Identity;
        aircraft.Track.HandoffPeer = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");
        aircraft.Track.OnHandoff = true;
        aircraft.Track.HandoffInitiatedAt = 100;
        var rule = new HandoffUnacceptedRule();
        double horizon = engine.Scenario!.AutoAcceptDelay.TotalSeconds + HandoffUnacceptedRule.GraceSeconds;

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon - 1, []));
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon + 1, []));
        AiAnomalyEvent opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
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
    public void HandoffUnaccepted_InSoloMode_WaitsForTheFlooredDelayPlusGrace()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(2);
        AircraftState aircraft = AiTestFixture.Airborne("AAL1", 37.9, -122.0, 8000);
        aircraft.Track.Owner = approach.Identity;
        aircraft.Track.HandoffPeer = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");
        aircraft.Track.OnHandoff = true;
        aircraft.Track.HandoffInitiatedAt = 100;
        var rule = new HandoffUnacceptedRule();
        double horizon = 5 + HandoffUnacceptedRule.GraceSeconds;

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon - 1, []));
        Assert.Empty(scenario.AiAnomalies.Drain());

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon + 1, []));
        AiAnomalyEvent opened = Assert.Single(scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.HandoffUnaccepted, opened.Kind);
    }

    [Fact]
    public void HandoffUnaccepted_HandoffWithheldBelowCoverage_OpensNoAnomaly()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        AircraftState aircraft = AiTestFixture.Airborne("AAL1", 37.9, -122.0, 1000);
        aircraft.Track.Owner = approach.Identity;
        aircraft.Track.HandoffPeer = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");
        aircraft.Track.OnHandoff = true;
        aircraft.Track.HandoffInitiatedAt = 100;
        var rule = new HandoffUnacceptedRule();
        double horizon = engine.Scenario!.EffectiveAutoAcceptDelaySeconds + HandoffUnacceptedRule.GraceSeconds;

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon + 5, []));

        Assert.Empty(engine.Scenario.AiAnomalies.Drain());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(8000)]
    public void HandoffOfLiveFeedOwnedTrack_NeverRaisesAnomaly(double feetAboveField)
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = NctEngine(_zoa);
        AttendanceTestSupport.Attend(engine, "4U");
        AircraftState aircraft = AiTestFixture.Airborne("AAL1", 37.75, -122.25, 0);
        aircraft.Altitude = FieldElevationResolver.Resolve(aircraft, NavigationDatabase.Instance) + feetAboveField;
        aircraft.Track.SetOwnerFromLiveFeed(approach.Identity);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.OnHandoff = true;
        aircraft.Track.HandoffInitiatedAt = 100;

        // Withheld or not, the peer is attended, so only the feed ownership keeps the watchdog quiet.
        bool belowCoverage = feetAboveField < DisconnectCoastRules.EramCoverageFloorAglFt;
        Assert.Equal(belowCoverage, TrackEngine.IsAutoAcceptWithheld(aircraft, NavigationDatabase.Instance));
        Assert.True(TrackEngine.IsHandoffPeerControlledByCrc(Nct4U, engine.Scenario!, engine.Attendance, engine.ConsolidationState));
        var rule = new HandoffUnacceptedRule();
        double horizon = engine.Scenario!.EffectiveAutoAcceptDelaySeconds + HandoffUnacceptedRule.GraceSeconds;

        // A handoff mirrored from the live feed completes when the feed says so, not on the room's clock.
        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon + 5, []));

        Assert.Empty(engine.Scenario.AiAnomalies.Drain());
    }

    [Fact]
    public void GroundHandoffToCrcAttendedPeer_NeverRaisesAnomaly()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = NctEngine(_zoa);
        AttendanceTestSupport.Attend(engine, "4U");
        AircraftState parked = engine.FindAircraft(AiTestFixture.Callsign)!;
        parked.Track.Owner = approach.Identity;
        parked.Track.HandoffPeer = Nct4U;
        parked.Track.OnHandoff = true;
        parked.Track.HandoffInitiatedAt = 100;
        Assert.True(parked.IsOnGround);
        Assert.True(TrackEngine.IsHandoffPeerControlledByCrc(Nct4U, engine.Scenario!, engine.Attendance, engine.ConsolidationState));
        var rule = new HandoffUnacceptedRule();
        double horizon = engine.Scenario!.EffectiveAutoAcceptDelaySeconds + HandoffUnacceptedRule.GraceSeconds;

        // A radar controller takes a surface aircraft only once it is airborne and tagged (7110.65 5-4-6.a, 2-1-15.c):
        // the human attending 4U is not behind while it taxis.
        rule.Evaluate(ConflictScope(engine, [parked], approach, 100 + horizon + 5, []));

        Assert.Empty(engine.Scenario.AiAnomalies.Drain());
    }

    [Fact]
    public void LowHandoffToCrcAttendedPeer_RaisesAnomalyAfterHorizon()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = NctEngine(_zoa);
        AttendanceTestSupport.Attend(engine, "4U");

        // Auto-accept withholds the low handoff, but it never takes one for an attended position anyway: the human
        // working 4U has to accept it, so the watchdog still flags it.
        AssertLowHandoffOpensPastTheHorizon(engine, approach, Nct4U);
    }

    [Fact]
    public void LowHandoffToPeerConsolidatedUnderAttended_RaisesAnomaly()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = NctEngine(_zoa);
        AttendanceTestSupport.Attend(engine, "4U");

        // 4Q is unattended, but its airspace has consolidated under the attended 4U.
        SimScenarioState scenario = engine.Scenario!;
        Assert.False(engine.Attendance.IsTcpAttended(TrackResolver.FindTcpByCode(scenario, "4Q")!));
        Assert.True(TrackEngine.IsHandoffPeerControlledByCrc(Nct4Q, scenario, engine.Attendance, engine.ConsolidationState));
        AssertLowHandoffOpensPastTheHorizon(engine, approach, Nct4Q);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LowHandoffToStudentPosition_RaisesAnomaly_InSoloAndNonSolo(bool soloTrainingMode)
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = NctEngine(_zoa);
        engine.Scenario!.SoloTrainingMode = soloTrainingMode;

        AssertLowHandoffOpensPastTheHorizon(engine, approach, Nct2B);
    }

    /// <summary>
    /// The OAK scenario with the student on NCT 2B and the NCT 4U and 4Q positions in the scenario's ATC list, so a
    /// handoff to either resolves to a TCP the attendance check can see. The real NCT hierarchy makes 4U 4Q's parent.
    /// </summary>
    private static SimulationEngine NctEngine(ArtccConfigRoot zoa)
    {
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = Nct2B;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;
        AddAtcPosition(scenario, Nct4U, "4U");
        AddAtcPosition(scenario, Nct4Q, "4Q");
        return engine;
    }

    private static void AddAtcPosition(SimScenarioState scenario, TrackOwner owner, string tcpCode) =>
        scenario.AtcPositions.Add(
            new ResolvedAtcPosition
            {
                Source = new ScenarioAtc(),
                Owner = owner,
                Tcp = TrackResolver.FindTcpByCode(scenario, tcpCode)!,
            }
        );

    /// <summary>
    /// A handoff from <paramref name="approach"/> to <paramref name="peer"/> of a track 500 ft above the field, below
    /// the ERAM coverage floor, so auto-accept withholds it; the watchdog stays quiet up to the auto-accept delay plus
    /// grace and opens the anomaly past it.
    /// </summary>
    private static void AssertLowHandoffOpensPastTheHorizon(SimulationEngine engine, AiPositionConfig approach, TrackOwner peer)
    {
        AircraftState aircraft = AiTestFixture.Airborne("AAL1", 37.75, -122.25, 0);
        aircraft.Altitude = FieldElevationResolver.Resolve(aircraft, NavigationDatabase.Instance) + 500;
        aircraft.Track.Owner = approach.Identity;
        aircraft.Track.HandoffPeer = peer;
        aircraft.Track.OnHandoff = true;
        aircraft.Track.HandoffInitiatedAt = 100;
        Assert.True(TrackEngine.IsAutoAcceptWithheld(aircraft, NavigationDatabase.Instance));
        var rule = new HandoffUnacceptedRule();
        SimScenarioState scenario = engine.Scenario!;
        double horizon = scenario.EffectiveAutoAcceptDelaySeconds + HandoffUnacceptedRule.GraceSeconds;

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon - 1, []));
        Assert.Empty(scenario.AiAnomalies.Drain());

        rule.Evaluate(ConflictScope(engine, [aircraft], approach, 100 + horizon + 1, []));
        AiAnomalyEvent opened = Assert.Single(scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.HandoffUnaccepted, opened.Kind);
        Assert.Contains(peer.Callsign, opened.Detail);
    }

    [Fact]
    public void ConflictAlert_OpensPerConflictIdInTheJurisdiction_AndClosesWhenTheAlertClears()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig approach = TestAiPositions.NorCalApproach(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        AircraftState a = AiTestFixture.Airborne("AAL1", 37.9, -122.0, 8000);
        AircraftState b = AiTestFixture.Airborne("UAL2", 37.9, -122.01, 8000);
        a.Track.Owner = approach.Identity;
        var conflict = new ActiveConflict
        {
            Id = "AAL1|UAL2",
            CallsignA = "AAL1",
            CallsignB = "UAL2",
        };
        var rule = new ConflictAlertInAiJurisdictionRule();

        rule.Evaluate(ConflictScope(engine, [a, b], approach, 10, [conflict]));
        AiAnomalyEvent opened = Assert.Single(engine.Scenario!.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.ConflictAlertInAiJurisdiction, opened.Kind);
        Assert.Equal("AAL1|UAL2", opened.SubjectKey);

        rule.Evaluate(ConflictScope(engine, [a, b], approach, 40, []));
        AiAnomalyEvent closed = Assert.Single(engine.Scenario.AiAnomalies.Drain());
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
        AiTickContext context = AiTestFixture.Context(engine, aircraft, [position], now, conflicts, new EngineAiCommandSink(engine));
        return new AiRuleScope
        {
            Tick = context,
            Position = position,
            Jurisdiction = context.View.Jurisdiction(position),
            Memos = memos,
            Pacing = new AiPacing(),
        };
    }
}
