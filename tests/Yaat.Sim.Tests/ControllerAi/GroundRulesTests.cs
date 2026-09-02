using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// The Ground brain's decision rules, each driven by hand over a real engine: the rule sees the world through a
/// recording sink, the test plays host by dispatching what the rule issued.
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

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 300, Speed = 12 }] };
        var aircraft = AiTestHost.TickUntil(
            engine,
            AiTestHost.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            15
        );
        var host = new RuleHost(engine, ground);
        var rule = new AnswerTaxiOutRule();
        double now = engine.Scenario!.ElapsedSeconds;

        // The think time runs from the first evaluation; nothing before it.
        rule.Evaluate(host.Scope([aircraft], now));
        Assert.Empty(host.Sink.Issued);
        rule.Evaluate(host.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        var request = Assert.Single(host.Sink.Issued);
        Assert.Equal("TAXIAUTO 30", request.Canonical);
        Assert.Equal("answer-taxi-out", request.Intent.Rule);
        Assert.Contains("most nearly aligned", request.Intent.Rationale);
        var memo = host.Memos[AiTestHost.Callsign];
        Assert.Equal(GroundIntent.TaxiIssued, memo.Intent);
        Assert.Same(request, memo.InFlight);

        // In flight: no second issue. Dispatched and answered: the request closes and the rule stays quiet.
        rule.Evaluate(host.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + 10));
        Assert.Single(host.Sink.Issued);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, request.Canonical).Success);
        memo.Complete(success: true, now + AiPacing.ThinkMaxSeconds + 11);
        Assert.False(aircraft.PendingPilotRequest!.IsOpen);
        rule.Evaluate(host.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + 20));
        Assert.Single(host.Sink.Issued);
    }

    [Fact]
    public void AnswerTaxiOut_RetriesTwice_ThenGivesUp()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        var aircraft = AiTestHost.TickUntil(
            engine,
            AiTestHost.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            15
        );
        var host = new RuleHost(engine, ground);
        var rule = new AnswerTaxiOutRule();
        double now = engine.Scenario!.ElapsedSeconds + AiPacing.ThinkMaxSeconds;
        rule.Evaluate(host.Scope([aircraft], now - AiPacing.ThinkMaxSeconds));

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            rule.Evaluate(host.Scope([aircraft], now));
            Assert.Equal(attempt, host.Sink.Issued.Count);
            var memo = host.Memos[AiTestHost.Callsign];
            memo.Complete(success: false, now + 1);
            Assert.Equal(attempt, memo.Rejections);
            // Backed off: an evaluation inside the backoff issues nothing.
            rule.Evaluate(host.Scope([aircraft], now + 2));
            Assert.Equal(attempt, host.Sink.Issued.Count);
            now += 1 + (AiAircraftMemo.RetryBackoffSeconds * attempt);
        }

        Assert.True(host.Memos[AiTestHost.Callsign].GaveUp);
        rule.Evaluate(host.Scope([aircraft], now + 1000));
        Assert.Equal(3, host.Sink.Issued.Count);
    }

    [Fact]
    public void RunwayCrossing_CombinedPosition_ClearsEachCrossingInTurn_NamingTheNearEnd_ThenReachesTheDepartureRunway()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 30").Success);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        var route = aircraft.Ground.AssignedTaxiRoute!;
        var crossings = route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing).ToList();
        Assert.Equal(2, crossings.Count);
        var host = new RuleHost(engine, ground);
        var rule = new RunwayCrossingRule();
        var issuedAt = new List<(string Canonical, int? PendingNode, bool FirstBarCleared)>();

        for (
            int t = 0;
            t < 900 && aircraft.Phases?.CurrentPhase is not HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway };
            t++
        )
        {
            engine.TickOneSecond();
            var layout = engine.ResolveGroundLayout(aircraft);
            rule.Evaluate(host.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
            foreach (var request in host.Sink.Issued.Skip(issuedAt.Count).ToList())
            {
                var pending = TaxiRouteProgress.NextUnclearedCrossing(aircraft, layout);
                Assert.NotNull(pending);
                Assert.True(
                    pending.DistanceFt <= RunwayCrossingRule.PreClearDistanceFt,
                    $"issued {request.Canonical} {pending.DistanceFt:F0} ft early"
                );
                Assert.Equal($"CROSS {TaxiRouteProgress.NearestCrossingEnd(aircraft, pending.Point.TargetName!, layout)}", request.Canonical);
                issuedAt.Add((request.Canonical, pending.Point.NodeId, crossings[0].IsCleared));
                Assert.True(engine.SendCommand(AiTestHost.Callsign, request.Canonical).Success, request.Canonical);
                host.Memos[AiTestHost.Callsign].Complete(success: true, engine.Scenario.ElapsedSeconds);
            }
        }

        Assert.Equal(2, issuedAt.Count);
        Assert.Equal(crossings[0].NodeId, issuedAt[0].PendingNode);
        Assert.Equal(crossings[1].NodeId, issuedAt[1].PendingNode);
        Assert.False(issuedAt[0].FirstBarCleared);
        // One crossing at a time (7110.65 §3-7-2.a.3): the second is cleared only after the first bar was.
        Assert.True(issuedAt[1].FirstBarCleared);
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(GroundIntent.None, host.Memos[AiTestHost.Callsign].Intent);
    }

    [Fact]
    public void RunwayCrossing_NeverNamesTheDepartureRunwayBar()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 28R").Success);
        var aircraft = AiTestHost.TickUntil(engine, AiTestHost.Callsign, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 300);
        Assert.Equal(HoldShortReason.DestinationRunway, ((HoldingShortPhase)aircraft.Phases!.CurrentPhase!).HoldShort.Reason);
        var host = new RuleHost(engine, ground);

        // The departure bar is Local's; the crossing rule has nothing to clear. (The engine would accept a CROSS here and
        // taxi the aircraft across its own departure runway — the trap the rule must never fall into.)
        Assert.Null(TaxiRouteProgress.NextUnclearedCrossing(aircraft, engine.ResolveGroundLayout(aircraft)));
        new RunwayCrossingRule().Evaluate(host.Scope([aircraft], engine.Scenario!.ElapsedSeconds + 100));
        Assert.Empty(host.Sink.Issued);
    }

    [Fact]
    public void RunwayCrossingGate_ArrivalInsideTheFinalGateBlocks_BeyondItDoesNot()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        foreach (var (distanceNm, expectClear) in new[] { (1.5, false), (6.0, true) })
        {
            var engine = AiTestHost.Load(
                TwoAircraftOnFinalTemplate.Replace("DISTANCE", distanceNm.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                _zoa,
                7,
                [ground]
            );
            var crosser = engine.FindAircraft(AiTestHost.Callsign)!;
            var arrival = engine.FindAircraft("N2FL")!;
            var layout = engine.ResolveGroundLayout(crosser);
            var pavement = RunwayCrossingGate.PavementFor("28R/10L", RunwayOccupancy.AirportRunways("OAK"));
            Assert.NotNull(pavement);

            bool clear = RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out var reason);

            Assert.Equal(expectClear, clear);
            if (!expectClear)
            {
                Assert.Contains("N2FL", reason);
            }

            // The same geometry two miles up is an overflight, not an arrival: a departed jet climbing out over the
            // field must not close the runway to crossings.
            var overflight = AiTestHost.Airborne("SWA919", arrival.Position.Lat, arrival.Position.Lon, pavement.AirportElevationFt + 10_000);
            overflight.TrueHeading = arrival.TrueHeading;
            Assert.True(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, overflight], layout, out _));
        }
    }

    [Fact]
    public void RunwayCrossingGate_LandingAndRolloutBlock_AHoldOnTheCrosserBlocks()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(TwoAircraftOnFinalTemplate.Replace("DISTANCE", "1.5"), _zoa, 7, [ground]);
        Assert.True(engine.SendCommand("N2FL", "CLAND").Success);
        var crosser = engine.FindAircraft(AiTestHost.Callsign)!;
        var pavement = RunwayCrossingGate.PavementFor("28R/10L", RunwayOccupancy.AirportRunways("OAK"))!;
        var layout = engine.ResolveGroundLayout(crosser);

        var arrival = AiTestHost.TickUntil(engine, "N2FL", ac => ac.IsOnGround, 200);
        Assert.False(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out var rollout));
        Assert.Contains("N2FL", rollout);

        arrival = AiTestHost.TickUntil(engine, "N2FL", ac => ac.Phases?.CurrentPhase is HoldingAfterExitPhase, 300);
        Assert.True(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out _));

        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 30").Success);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "HOLD").Success);
        Assert.False(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, arrival], layout, out var held));
        Assert.Contains("hold", held);
    }

    [Fact]
    public void RunwayCrossing_WithLocalStaffed_AsksOnceOnTheTerminal_ThenTimesOut()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground, tower]);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 30").Success);
        var aircraft = AiTestHost.TickUntil(
            engine,
            AiTestHost.Callsign,
            ac => ac.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.RunwayCrossing },
            400
        );
        aircraft.PendingWarnings.Clear();
        var host = new RuleHost(engine, ground, tower);
        var rule = new RunwayCrossingRule();
        double now = engine.Scenario!.ElapsedSeconds;

        // The request is a transmission: it waits for the think time like any other.
        rule.Evaluate(host.Scope([aircraft], now));
        Assert.Empty(aircraft.PendingWarnings);
        rule.Evaluate(host.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        Assert.Empty(host.Sink.Issued);
        var line = Assert.Single(aircraft.PendingWarnings);
        Assert.StartsWith("[AI-COORD] OAK_GND requests cross runway ", line);
        Assert.Contains(" at B for ", line);
        Assert.EndsWith($" for {AiTestHost.Callsign}", line);
        Assert.Equal(GroundIntent.CrossingRequested, host.Memos[AiTestHost.Callsign].Intent);
        Assert.Empty(engine.Scenario.AiAnomalies.Drain());

        // Past the timeout: the anomaly opens and Ground asks again rather than going silent.
        rule.Evaluate(host.Scope([aircraft], now + AiPacing.ThinkMaxSeconds + RunwayCrossingRule.CoordinationTimeoutSeconds));
        var opened = Assert.Single(engine.Scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.CoordinationTimeout, opened.Kind);
        Assert.Equal(AiTestHost.Callsign, opened.SubjectKey);
        Assert.Equal(2, aircraft.PendingWarnings.Count);

        // The human clears it: the request is satisfied and the episode closes.
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "CROSS 28R").Success);
        AiTestHost.TickUntil(engine, AiTestHost.Callsign, ac => ac.Phases?.CurrentPhase is not HoldingShortPhase, 30);
        rule.Evaluate(host.Scope([aircraft], engine.Scenario.ElapsedSeconds));
        var closed = engine.Scenario.AiAnomalies.Drain();
        Assert.Contains(closed, e => e.Kind == AiAnomalyKind.CoordinationTimeout && e.Event == AiAnomalyEventKind.Closed);
    }

    [Fact]
    public void HandToLocal_TransfersShortOfTheDepartureRunway_WithNoCrossingAhead()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"29\""), _zoa, 7, [ground, tower]);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 30").Success);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        Assert.DoesNotContain(aircraft.Ground.AssignedTaxiRoute!.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing);
        var host = new RuleHost(engine, ground, tower);
        var rule = new HandToLocalRule();
        double? distanceAtIssue = null;
        bool wasTaxiing = false;

        for (int t = 0; t < 400 && host.Sink.Issued.Count == 0; t++)
        {
            engine.TickOneSecond();
            var layout = engine.ResolveGroundLayout(aircraft);
            var before = TaxiRouteProgress.DistanceToDestinationBarFt(aircraft, layout);
            rule.Evaluate(host.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
            if (host.Sink.Issued.Count > 0)
            {
                distanceAtIssue = before;
                wasTaxiing = aircraft.Phases?.CurrentPhase is TaxiingPhase;
            }
        }

        var request = Assert.Single(host.Sink.Issued);
        Assert.Equal("CT OAK_TWR", request.Canonical);
        Assert.True(wasTaxiing);
        Assert.NotNull(distanceAtIssue);
        Assert.InRange(distanceAtIssue.Value, 0, HandToLocalRule.HandoffDistanceFt);
        Assert.Equal(GroundIntent.HandedToLocal, host.Memos[AiTestHost.Callsign].Intent);

        // Once handed off, never again for this aircraft.
        host.Memos[AiTestHost.Callsign].Complete(success: true, engine.Scenario!.ElapsedSeconds);
        rule.Evaluate(host.Scope([aircraft], engine.Scenario.ElapsedSeconds + 30));
        Assert.Single(host.Sink.Issued);
    }

    [Fact]
    public void HandToLocal_WaitsWhileACrossingIsStillAhead()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground, tower]);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 30").Success);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        var host = new RuleHost(engine, ground, tower);
        var rule = new HandToLocalRule();

        // Nobody clears the crossings: the aircraft stops at the first bar and Ground never transfers it.
        for (int t = 0; t < 300; t++)
        {
            engine.TickOneSecond();
            rule.Evaluate(host.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Empty(host.Sink.Issued);
    }

    [Fact]
    public void HandToLocal_TransfersNothingInACombinedCab()
    {
        if (_zoa is null)
        {
            return;
        }

        // Nobody holds Local: Ground works the runway itself and there is no tower to send the pilot to (7110.65 §2-1-17.a).
        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"29\""), _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "TAXIAUTO 30").Success);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        var host = new RuleHost(engine, ground);
        var rule = new HandToLocalRule();

        for (int t = 0; t < 400 && aircraft.Phases?.CurrentPhase is not HoldingShortPhase; t++)
        {
            engine.TickOneSecond();
            rule.Evaluate(host.Scope([aircraft], engine.Scenario!.ElapsedSeconds));
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Empty(host.Sink.Issued);
    }

    [Fact]
    public void RunwayCrossingGate_AGoAroundOverTheRunwayBlocks()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(TwoAircraftOnFinalTemplate.Replace("DISTANCE", "1.5"), _zoa, 7, [ground]);
        Assert.True(engine.SendCommand("N2FL", "GA").Success);
        var crosser = engine.FindAircraft(AiTestHost.Callsign)!;
        var pavement = RunwayCrossingGate.PavementFor("28R/10L", RunwayOccupancy.AirportRunways("OAK"))!;
        var layout = engine.ResolveGroundLayout(crosser);

        // Climbing down the runway: invisible to the on-final test, a runway user all the same (7110.65 §3-7-2.a.7.1).
        var goingAround = AiTestHost.TickUntil(engine, "N2FL", ac => RunwayOccupancy.IsWithinPavement(ac.Position, pavement), 240);
        Assert.False(goingAround.IsOnGround);
        Assert.InRange(goingAround.Altitude - pavement.AirportElevationFt, 0, RunwayCrossingGate.OverRunwayMaxAglFt);
        Assert.False(RunwayCrossingGate.IsClear(crosser, pavement, [crosser, goingAround], layout, out var reason));
        Assert.Contains("over runway", reason);
    }

    [Fact]
    public void AnswerTaxiIn_TaxiesTheArrivalToTheParkingItAskedFor_OrRepicksATakenOne()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.OnFinalAtOak, _zoa, 7, [ground]);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, "CLAND").Success);
        var aircraft = AiTestHost.TickUntil(
            engine,
            AiTestHost.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi, ParkingName: not null },
            500
        );
        var requested = aircraft.PendingPilotRequest!.ParkingName!;
        var host = new RuleHost(engine, ground);
        var rule = new AnswerTaxiInRule();
        double now = engine.Scenario!.ElapsedSeconds;

        rule.Evaluate(host.Scope([aircraft], now));
        rule.Evaluate(host.Scope([aircraft], now + AiPacing.ThinkMaxSeconds));
        var request = Assert.Single(host.Sink.Issued);
        Assert.Equal($"TAXIAUTO @{requested}", request.Canonical);
        Assert.Equal(GroundIntent.TaxiInIssued, host.Memos[AiTestHost.Callsign].Intent);

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
        var fresh = new RuleHost(engine, ground);
        rule.Evaluate(fresh.Scope([aircraft, claimant], now + 100));
        rule.Evaluate(fresh.Scope([aircraft, claimant], now + 100 + AiPacing.ThinkMaxSeconds));
        var repicked = Assert.Single(fresh.Sink.Issued);
        Assert.StartsWith("TAXIAUTO @", repicked.Canonical);
        Assert.NotEqual(request.Canonical, repicked.Canonical);
        Assert.Contains("is taken", repicked.Intent.Rationale);
        Assert.True(engine.SendCommand(AiTestHost.Callsign, repicked.Canonical).Success);
    }

    /// <summary>Plays host for one rule: a recording sink, one pacing, one memo table, and scopes at chosen times.</summary>
    private sealed class RuleHost(SimulationEngine engine, params AiPositionConfig[] staffed)
    {
        public RecordingAiCommandSink Sink { get; } = new();

        public Dictionary<string, AiAircraftMemo> Memos { get; } = new(StringComparer.Ordinal);

        private readonly AiPacing _pacing = new();

        public AiRuleScope Scope(IReadOnlyList<AircraftState> aircraft, double now)
        {
            _pacing.BeginTick();
            var context = AiTestHost.Context(engine, aircraft, staffed, now, [], Sink);
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
