using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// The dispatch advisory (step 2 of the in-phase Class B work): a pattern entry, MLT/MRT or approach clearance whose
/// whole cleared path enters an uncleared Class B has the solo pilot read back "that'll take us into the bravo" and
/// record the Class B request; in an RPO session the instructor gets one warning line instead and nothing else changes.
/// Real navdata: the SFO Class B, KOAK 28R (a satellite outside it), KSFO (inside it) and KSQL (under a shelf).
/// </summary>
public sealed class BravoDispatchAdvisoryTests : IDisposable
{
    private const string Callsign = "N123AB";
    private const string Sfo = "SFO";
    private const double CruiseAltitude = 7500;
    private const string Advisory = ", that'll take us into the bravo";
    private const string AdvisoryWithRequest = ", that'll take us into the bravo, request bravo clearance";
    private const string RpoWarningPattern = @"^N123AB: .+ cleared path enters the SFO Class B uncleared in \d+ s \(RPO session: no hold\)$";

    private static readonly LatLon NearWestRing = new(37.62, -122.885);
    private static readonly LatLon Ksfo = new(37.6213, -122.3790);
    private static readonly LatLon Ksql = new(37.5119, -122.2494);

    private readonly IDisposable _navScope;

    public BravoDispatchAdvisoryTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    // --- D1-D6: the solo readback ---

    [Fact]
    public void Dispatch_PatternEntryAtOak28R_PathEntersSfoBravo_ReadsBackThatllTakeUsIntoTheBravo_AndRecordsTheClassBRequest()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(solo: true);

        Assert.IsType<PatternEntryPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.False(run.Entry.Vertical);
        PilotTransmission readback = Readback(run.Aircraft);
        Assert.EndsWith(AdvisoryWithRequest, readback.Text);
        Assert.Contains($"{AdvisoryWithRequest}, ", readback.SpeechText);
        Assert.EndsWith(".", readback.SpeechText);
        Assert.Null(run.Aircraft.ClearedBravoIdent);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(run.Aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, request.Kind);
        Assert.Equal(Sfo, request.AirspaceIdent);
        Assert.Equal(BravoClearanceWording.Through, request.BravoWording);
        Assert.True(request.IsOpen);
    }

    [Fact]
    public void Dispatch_ApproachClearanceAtOak_PathClipsTheBravo_SameAdvisory()
    {
        AircraftState ac = WestOfTheRing(1.5);
        SimulationEngine engine = Engine(ac, solo: true).Engine;

        CommandResult result = engine.SendCommand(Callsign, "CAPP ILS28R");

        Assert.True(result.Success, result.Message);
        BravoPathEntry entry = Assert.IsType<BravoPathEntry>(PendingEntry(ac));
        Assert.False(entry.Vertical);
        Assert.EndsWith(AdvisoryWithRequest, Readback(ac).Text);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(Sfo, request.AirspaceIdent);
        Assert.True(request.IsOpen);
    }

    [Fact]
    public void Dispatch_KsfoPatternEntry_GrantedBravo_NoAdvisoryNoRequest()
    {
        AircraftState ac = WestOfTheRing(3);
        ac.FlightPlan.Destination = "KSFO";
        // The cleared path would enter the SFO Class B (the axis ray meets the ring inside the request lookahead): only the
        // grant suppresses the advisory.
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));
        SimulationEngine engine = Engine(ac, solo: true).Engine;

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        PilotTransmission readback = Readback(ac);
        Assert.EndsWith(", cleared into the bravo", readback.Text);
        Assert.DoesNotContain("that'll take us into the bravo", readback.Text);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void Dispatch_UnderRmob_PathEntersTheBravo_AdvisoryDropsTheRequestClause_NoRequestRecorded()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(
            solo: true,
            before: a => a.StartRemainOutsideBravo(Sfo, minutes: null, askDueSeconds: null)
        );
        AircraftState ac = run.Aircraft;

        Assert.Equal(Sfo, ac.RemainOutsideBravo?.Ident);
        Assert.False(run.Entry.Vertical);
        Assert.EndsWith(Advisory, Readback(ac).Text);
        Assert.DoesNotContain("request bravo clearance", Readback(ac).Text);
        Assert.Null(ac.PendingPilotRequest);
        Assert.Null(ac.ClearedBravoIdent);
    }

    [Fact]
    public void Dispatch_ClearedIntoThatBravo_NoAdvisory()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(solo: true, before: a => a.GrantBravoClearance(Sfo));

        Assert.False(run.Entry.Vertical);
        AssertNoBravoWords(run.Aircraft);
        Assert.Null(run.Aircraft.PendingPilotRequest);
        Assert.Equal(Sfo, run.Aircraft.ClearedBravoIdent);
    }

    [Fact]
    public void Dispatch_PathStaysClear_ReadbackUnchanged()
    {
        // East of Oakland at 1,500 ft: below every SFO shelf its path crosses, so the cleared path enters none of them.
        AircraftState ac = Airborne(new LatLon(37.72, -121.95), trueHeading: 270, altitude: 1500);
        SimulationEngine engine = Engine(ac, solo: true).Engine;

        CommandResult result = engine.SendCommand(Callsign, "EF 28R");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Phases?.CurrentPhase);
        Assert.Null(PendingEntry(ac));
        AssertNoBravoWords(ac);
        Assert.Null(ac.PendingPilotRequest);
    }

    // --- D7-D9: the vertical entry, inside the area, and the ray fallback ---

    [Fact]
    public void Dispatch_PathStaysUnderAShelfNearKsql_NoAdvisory()
    {
        AircraftState ac = ClimbingIntoAShelfNearKsql();
        BravoPathEntry entry = Assert.IsType<BravoPathEntry>(PendingEntry(ac));
        Assert.True(entry.Vertical, "the cap owns a climb into the shelf above");
        var compound = new CompoundCommand([new ParsedBlock(null, [new EnterRightDownwindCommand("28R")])]);

        Assert.Null(
            PilotProactive.FindDispatchBravoAdvisory(ac, compound, AirspaceDatabase.Default, includeVerticalEntries: false, a => Context(a, Oak28R()))
        );
    }

    [Fact]
    public void Dispatch_AircraftInsideTheOuterShelf_PathIntoTheNextVolume_NoAdvisory()
    {
        AircraftState ac = InsideTheOuterShelfShortOfTheNextVolume();
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, BravoPathPredictor.TriggerLookaheadSeconds, AirspaceClass.Bravo));
        Assert.True(
            AirspaceDatabase.Default.ClassBArea(Sfo).Any(v => v.Contains(ac.Position, ac.Altitude)),
            "the aircraft is inside a volume of the area"
        );
        SimulationEngine engine = Engine(ac, solo: true).Engine;

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        AssertNoBravoWords(ac);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void Dispatch_MidfieldCrossingWithNoPathSource_UsesTheRay_SameWords()
    {
        AircraftState ac = WestOfTheRing(1.5);
        SimulationEngine engine = Engine(ac, solo: true).Engine;

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        Assert.IsType<MidfieldCrossingPhase>(ac.Phases!.CurrentPhase);
        Assert.Null(BravoPathPredictor.PredictPending(ac, ac.Phases, Context(ac, Oak28R()), BravoPathPredictor.ReleaseHorizonSeconds));
        Assert.EndsWith(AdvisoryWithRequest, Readback(ac).Text);
        Assert.Equal(Sfo, Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest).AirspaceIdent);
    }

    // --- A running phase is judged as it runs, not as a phase just starting ---

    [Fact]
    public void Dispatch_RunningPatternEntryPastItsLeadIn_Mrt_JudgesTheRunningPath_NoTurnBack()
    {
        AircraftState ac = WestOfTheRing(1.5);
        PatternEntryPhase oak = OakPatternEntry();
        LatLon entryPoint = new(oak.EntryLat, oak.EntryLon);
        LatLon leadIn = GeoMath.ProjectPoint(ac.Position, new TrueHeading(270), 3);
        PatternWaypoints waypoints = OakLeftPattern();
        PatternEntryPhase entry = Active(EntryAt(entryPoint, leadIn));
        ac.Phases = new PhaseList { AssignedRunway = Oak28R() };
        ac.Phases.Add(entry);
        ac.Phases.Add(new DownwindPhase { Waypoints = waypoints });
        ac.Phases.Add(new BasePhase { Waypoints = waypoints });
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new LandingPhase());
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "ENTRY", Position = entryPoint });
        SimulationEngine engine = Engine(ac, solo: true).Engine;

        CommandResult result = engine.SendCommand(Callsign, "MRT");

        Assert.True(result.Success, result.Message);
        Assert.Same(entry, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.NotEmpty(ac.Targets.NavigationRoute);
        PhaseContext ctx = Context(ac, Oak28R());
        PredictedPath running = Assert.IsType<PredictedPath>(
            BravoPathPredictor.Predict(ac, ac.Phases, entry, ctx, BravoPathPredictor.ReleaseHorizonSeconds)
        );
        BravoPathEntry expected = Assert.IsType<BravoPathEntry>(BravoPathPredictor.FirstEntry(running, AirspaceDatabase.Default, 0, null));
        PredictedPath notStarted = Assert.IsType<PredictedPath>(
            BravoPathPredictor.PredictPending(ac, ac.Phases, ctx, BravoPathPredictor.ReleaseHorizonSeconds)
        );
        BravoPathEntry turnedBack = Assert.IsType<BravoPathEntry>(BravoPathPredictor.FirstEntry(notStarted, AirspaceDatabase.Default, 0, null));
        Assert.NotEqual(expected.Seconds, turnedBack.Seconds);
        var compound = new CompoundCommand([new ParsedBlock(null, [new MakeRightTrafficCommand("28R", null)])]);

        PilotProactive.BravoDispatchAdvisory advisory = Assert.IsType<PilotProactive.BravoDispatchAdvisory>(
            PilotProactive.FindDispatchBravoAdvisory(ac, compound, AirspaceDatabase.Default, includeVerticalEntries: false, a => Context(a, Oak28R()))
        );

        Assert.Equal(expected.Seconds, advisory.Seconds);
    }

    [Fact]
    public void Dispatch_HoldStandingInFrontOfThePhase_JudgesNothing()
    {
        // In free flight the pilot's own hold is the current phase: the in-phase tick judges no held phase, and nor does
        // the dispatch finder.
        AircraftState ac = WestOfTheRing(1.5);
        SimScenarioState scenario = Scenario(solo: true);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases!.CurrentPhase);
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, BravoPathPredictor.TriggerLookaheadSeconds, AirspaceClass.Bravo));
        var compound = new CompoundCommand([new ParsedBlock(null, [new EnterRightDownwindCommand("28R")])]);

        Assert.Null(
            PilotProactive.FindDispatchBravoAdvisory(ac, compound, AirspaceDatabase.Default, includeVerticalEntries: false, a => Context(a, Oak28R()))
        );
    }

    // --- D10 and the beyond-60-second window ---

    [Fact]
    public void Dispatch_ReactionDelayed_NoAdvisoryAtReadback_TheHoldSaysItLater()
    {
        AircraftState ac = RunningPatternEntryIntoTheBravo();
        (SimulationEngine engine, SimScenarioState scenario) = Engine(ac, solo: true);
        scenario.CommandRunDelayMinSeconds = 5;
        scenario.CommandRunDelayMaxSeconds = 5;

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        Assert.NotEmpty(ac.DeferredDispatches);
        Assert.DoesNotContain(ac.PendingPilotTransmissions, t => t.Text.Contains("bravo", StringComparison.OrdinalIgnoreCase));
        Assert.Null(ac.PendingPilotRequest);

        // The same fixture, cleared at once, advises: without the deferral guard the delayed readback would too.
        AircraftState prompt = RunningPatternEntryIntoTheBravo();
        SimulationEngine promptEngine = Engine(prompt, solo: true).Engine;
        Assert.True(promptEngine.SendCommand(Callsign, "ERD 28R").Success, "the immediate dispatch");
        Assert.EndsWith(AdvisoryWithRequest, Readback(prompt).Text);

        // The delay fires; the in-phase hold says the words once the phase runs and the entry is inside 60 s.
        for (int t = 0; (t < 30) && (ac.DeferredDispatches.Count > 0); t++)
        {
            engine.TickOneSecond();
        }

        for (int t = 0; (t < 30) && (ac.Phases?.HeldPhase is null); t++)
        {
            engine.TickOneSecond();
        }

        Assert.NotNull(ac.Phases!.HeldPhase);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, request.Kind);
        Assert.Equal(Sfo, request.AirspaceIdent);
        Assert.Equal("request clearance through the bravo.", request.LastPilotLine);
        Assert.Null(ac.InPhaseOrbitAdvisoryOwed);
    }

    [Fact]
    public void Dispatch_EntryBeyond60s_AdvisesAtIssue_AndTheHoldComesLaterInsideTheWindow()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(solo: true, minSeconds: BravoPathPredictor.TriggerLookaheadSeconds + 1);
        AircraftState ac = run.Aircraft;
        SimScenarioState scenario = run.Engine.Scenario!;

        Assert.True(run.Entry.Seconds > BravoPathPredictor.TriggerLookaheadSeconds, $"entry in {run.Entry.Seconds:F0} s");
        Assert.EndsWith(AdvisoryWithRequest, Readback(ac).Text);
        Assert.True(Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest).IsOpen);

        // Inside the window, the in-phase hold puts the orbit in.
        ac.Position = GeoMath.ProjectPoint(run.Entry.Point, new TrueHeading(270), 1.0);
        ac.Phases!.CurrentPhase!.Status = PhaseStatus.Active;
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
    }

    // --- Requests: replace, keep, and the orbit advisory that follows ---

    [Fact]
    public void Dispatch_AdvisoryRequest_ReplacesAnotherOpenRequest_KeepsAnOpenRequestForTheSameBravo()
    {
        DispatchRun replacedRun = DispatchPatternEntryIntoTheBravo(
            solo: true,
            before: a =>
                PilotRequestTracker.RecordRequest(
                    a,
                    PilotPendingRequestKind.Landing,
                    nowSeconds: 0,
                    new PilotSpeechText("request closed traffic.", "request closed traffic."),
                    PilotRequestContext.None
                )
        );
        PilotPendingRequest replaced = Assert.IsType<PilotPendingRequest>(replacedRun.Aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, replaced.Kind);
        Assert.Equal(Sfo, replaced.AirspaceIdent);
        Assert.True(replaced.IsOpen);

        PilotPendingRequest? held = null;
        DispatchRun keptRun = DispatchPatternEntryIntoTheBravo(
            solo: true,
            before: a =>
            {
                PilotSpeechText through = PilotResponder.BuildBravoClearanceRequest(a, BravoClearanceWording.Through);
                held = PilotRequestTracker.RecordRequest(
                    a,
                    PilotPendingRequestKind.AirspaceEntry,
                    nowSeconds: -30,
                    through,
                    PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, NearWestRing)
                );
                held.BravoWording = BravoClearanceWording.Through;
            }
        );

        Assert.Same(held, keptRun.Aircraft.PendingPilotRequest);
        Assert.True(held!.IsOpen);
        Assert.Equal(-30, held.FirstRequestedAtSeconds);
    }

    [Fact]
    public void Dispatch_OpenSameBravoRequest_ReadbackDropsTheRequestClause()
    {
        PilotPendingRequest? held = null;
        DispatchRun run = DispatchPatternEntryIntoTheBravo(
            solo: true,
            before: a =>
            {
                PilotSpeechText through = PilotResponder.BuildBravoClearanceRequest(a, BravoClearanceWording.Through);
                held = PilotRequestTracker.RecordRequest(
                    a,
                    PilotPendingRequestKind.AirspaceEntry,
                    nowSeconds: 0,
                    through,
                    PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, NearWestRing)
                );
                held.BravoWording = BravoClearanceWording.Through;
            }
        );

        Assert.EndsWith(Advisory, Readback(run.Aircraft).Text);
        Assert.DoesNotContain("request bravo clearance", Readback(run.Aircraft).Text);
        Assert.Same(held, run.Aircraft.PendingPilotRequest);
        Assert.True(held!.IsOpen);
    }

    [Fact]
    public void Dispatch_OpenSameBravoRequest_WithAGrantlessVector_ReadbackAsksAndOneRequestStaysOpen()
    {
        // The compound's vector answers the open request without granting the Bravo, so the request is closed by the same
        // dispatch: the readback asks again and the recorded request is the one the pilot said.
        PilotPendingRequest? held = null;
        DispatchRun run = DispatchPatternEntryIntoTheBravo(
            solo: true,
            command: "ERD 28R; FH 270",
            before: a =>
            {
                PilotSpeechText through = PilotResponder.BuildBravoClearanceRequest(a, BravoClearanceWording.Through);
                held = PilotRequestTracker.RecordRequest(
                    a,
                    PilotPendingRequestKind.AirspaceEntry,
                    nowSeconds: 0,
                    through,
                    PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, NearWestRing)
                );
                held.BravoWording = BravoClearanceWording.Through;
            }
        );

        Assert.NotNull(held);
        Assert.NotSame(held, run.Aircraft.PendingPilotRequest);
        Assert.EndsWith(AdvisoryWithRequest, Readback(run.Aircraft).Text);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(run.Aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, request.Kind);
        Assert.Equal(Sfo, request.AirspaceIdent);
        Assert.True(request.IsOpen);
    }

    [Fact]
    public void Dispatch_AdvisoryThenTheHold_OrbitAdvisoryDropsTheRequestClause_NoDuplicateRequest()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(solo: true, maxSeconds: BravoPathPredictor.TriggerLookaheadSeconds);
        AircraftState ac = run.Aircraft;
        PilotPendingRequest recorded = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        double firstAsked = recorded.FirstRequestedAtSeconds;
        ac.PendingPilotTransmissions.Clear();

        ac.Phases!.CurrentPhase!.Status = PhaseStatus.Active;
        PilotProactive.TickAirspaceBoundaryRespect(ac, run.Engine.Scenario!, AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        PilotTransmission orbit = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(OrbitAdvisory(hold), orbit.Text);
        Assert.Same(recorded, ac.PendingPilotRequest);
        Assert.Equal(firstAsked, recorded.FirstRequestedAtSeconds);
    }

    [Fact]
    public void Orbit_OpenSameBravoRequestFromThePilot_OrbitAdvisoryDropsTheRequestClause()
    {
        AircraftState ac = WestOfTheRing(1.5);
        SimScenarioState scenario = Scenario(solo: true);
        PilotSpeechText through = PilotResponder.BuildBravoClearanceRequest(ac, BravoClearanceWording.Through);
        PilotPendingRequest held = PilotRequestTracker.RecordRequest(
            ac,
            PilotPendingRequestKind.AirspaceEntry,
            nowSeconds: 0,
            through,
            PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, NearWestRing)
        );
        held.BravoWording = BravoClearanceWording.Through;
        ac.Phases!.Add(Active(OakPatternEntry()));

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        PilotTransmission orbit = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(OrbitAdvisory(hold), orbit.Text);
        Assert.Same(held, ac.PendingPilotRequest);
    }

    [Fact]
    public void Orbit_QueuedRequestRepeat_KeepsItsPlace_WhenTheAdvisoryDropsTheClause()
    {
        AircraftState ac = WestOfTheRing(1.5);
        SimScenarioState scenario = Scenario(solo: true);
        PilotSpeechText through = PilotResponder.BuildBravoClearanceRequest(ac, BravoClearanceWording.Through);
        PilotPendingRequest held = PilotRequestTracker.RecordRequest(
            ac,
            PilotPendingRequestKind.AirspaceEntry,
            nowSeconds: 0,
            through,
            PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, NearWestRing)
        );
        held.BravoWording = BravoClearanceWording.Through;
        ac.Phases!.Add(Active(OakPatternEntry()));
        PilotResponder.QueueSoloPilotTransmission(ac, through, PilotTransmissionKind.Proactive, initialContact: false);

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Contains(ac.PendingPilotTransmissions, t => t.Text == "request clearance through the bravo.");
        Assert.Contains(ac.PendingPilotTransmissions, t => t.Text == OrbitAdvisory(hold));
    }

    [Fact]
    public void Dispatch_Replay_SameAdvisoryAtTheSameSecond()
    {
        AircraftState live = WestOfTheRing(1.5);
        (SimulationEngine liveEngine, _) = Engine(live, solo: true);
        liveEngine.TickOneSecond();
        Assert.True(liveEngine.SendCommand(Callsign, "ERD 28R").Success, "the live dispatch");

        AircraftState replayed = WestOfTheRing(1.5);
        (SimulationEngine replayEngine, _) = Engine(replayed, solo: true);
        replayEngine.ArmReplay([new RecordedCommand(1, Callsign, "ERD 28R", "", SimulationEngine.LocalConnectionId) { Accepted = true }]);
        replayEngine.ReplayOneSecond();

        Assert.Equal(Readback(live).Text, Readback(replayed).Text);
        Assert.Equal(Readback(live).SpeechText, Readback(replayed).SpeechText);
        Assert.Equal(live.PendingPilotRequest!.LastPilotLine, replayed.PendingPilotRequest!.LastPilotLine);
        Assert.Equal(live.PendingPilotRequest.LastRequestedAtSeconds, replayed.PendingPilotRequest.LastRequestedAtSeconds);
        Assert.Equal(RayEntrySeconds(live), RayEntrySeconds(replayed));
    }

    // --- D11-D15: the RPO warning ---

    [Fact]
    public void Rpo_Dispatch_PathEntersWithin60s_WarnsOnce_ReadbackAndPendingRequestUnchanged_TickAddsNoSecondWarning()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(solo: false, maxSeconds: BravoPathPredictor.TriggerLookaheadSeconds);
        AircraftState ac = run.Aircraft;

        Assert.Matches(RpoWarningPattern, Assert.Single(ac.PendingWarnings));
        Assert.Equal(Sfo, ac.InPhaseWarnedArea);
        Assert.Null(ac.ClearedBravoIdent);
        Assert.Null(ac.PendingPilotRequest);
        Assert.Empty(ac.PendingPilotTransmissions);

        ac.Phases!.CurrentPhase!.Status = PhaseStatus.Active;
        PilotProactive.TickRpoPathWarning(ac, run.Engine.Scenario!, AirspaceDatabase.Default, a => Context(a, Oak28R()));

        Assert.Single(ac.PendingWarnings);
        Assert.Equal(Sfo, ac.InPhaseWarnedArea);
    }

    [Fact]
    public void Rpo_Dispatch_EntryBeyond60s_WarnsAtDispatch_AndAgainWhenInside60s()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(solo: false, minSeconds: BravoPathPredictor.TriggerLookaheadSeconds + 1);
        AircraftState ac = run.Aircraft;

        Assert.Matches(RpoWarningPattern, Assert.Single(ac.PendingWarnings));
        Assert.Null(ac.InPhaseWarnedArea);

        ac.Position = GeoMath.ProjectPoint(run.Entry.Point, new TrueHeading(270), 1.0);
        ac.Phases!.CurrentPhase!.Status = PhaseStatus.Active;
        PilotProactive.TickRpoPathWarning(ac, run.Engine.Scenario!, AirspaceDatabase.Default, a => Context(a, Oak28R()));

        Assert.Equal(2, ac.PendingWarnings.Count);
        Assert.Equal(Sfo, ac.InPhaseWarnedArea);
    }

    [Fact]
    public void Rpo_Dispatch_InsideTheArea_NoWarning()
    {
        AircraftState ac = InsideTheOuterShelfShortOfTheNextVolume();
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, BravoPathPredictor.TriggerLookaheadSeconds, AirspaceClass.Bravo));
        Assert.True(
            AirspaceDatabase.Default.ClassBArea(Sfo).Any(v => v.Contains(ac.Position, ac.Altitude)),
            "the aircraft is inside a volume of the area"
        );
        SimulationEngine engine = Engine(ac, solo: false).Engine;

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.PendingWarnings);
        Assert.Null(ac.InPhaseWarnedArea);
    }

    [Fact]
    public void Rpo_Dispatch_ClearedAircraft_NoWarning()
    {
        DispatchRun run = DispatchPatternEntryIntoTheBravo(solo: false, before: a => a.GrantBravoClearance(Sfo));

        Assert.Equal(Sfo, run.Aircraft.ClearedBravoIdent);
        Assert.Empty(run.Aircraft.PendingWarnings);
    }

    [Fact]
    public void Rpo_Dispatch_VerticalShelfEntry_Warns()
    {
        AircraftState ac = ClimbingIntoAShelfNearKsql();
        var compound = new CompoundCommand([new ParsedBlock(null, [new EnterRightDownwindCommand("28R")])]);

        PilotProactive.BravoDispatchAdvisory advisory = Assert.IsType<PilotProactive.BravoDispatchAdvisory>(
            PilotProactive.FindDispatchBravoAdvisory(ac, compound, AirspaceDatabase.Default, includeVerticalEntries: true, a => Context(a, Oak28R()))
        );
        Assert.True(advisory.Vertical);

        PilotProactive.WarnRpoAtDispatch(ac, advisory);

        Assert.Matches(RpoWarningPattern, Assert.Single(ac.PendingWarnings));
        Assert.Equal(Sfo, ac.InPhaseWarnedArea);
    }

    [Fact]
    public void Rpo_Dispatch_SnapshotIsOtherwiseUnchanged()
    {
        DispatchRun warnedRun = DispatchPatternEntryIntoTheBravo(solo: false);
        DispatchRun latchedRun = DispatchPatternEntryIntoTheBravo(solo: false, before: a => a.InPhaseWarnedArea = Sfo);

        Assert.Single(warnedRun.Aircraft.PendingWarnings);
        Assert.Empty(latchedRun.Aircraft.PendingWarnings);
        Assert.Equal(SnapshotWithoutTheLatch(warnedRun.Aircraft), SnapshotWithoutTheLatch(latchedRun.Aircraft));
    }

    // --- Helpers ---

    /// <summary>A dispatch through the engine, and the Class B entry its cleared path was found to make.</summary>
    private sealed record DispatchRun(SimulationEngine Engine, AircraftState Aircraft, BravoPathEntry Entry);

    /// <summary>
    /// An engine and an aircraft whose "ERD 28R" builds a right-side pattern entry into the runway 28R downwind whose
    /// cleared path enters the SFO Class B (from the south that join is a wrong-side midfield crossing, which has no path to
    /// test), found over a grid north-west of Oakland; <paramref name="before"/> runs on the aircraft before the dispatch,
    /// and the entry's seconds must lie inside the range asked for.
    /// </summary>
    private static DispatchRun DispatchPatternEntryIntoTheBravo(
        bool solo,
        Action<AircraftState>? before = null,
        double minSeconds = 0,
        double maxSeconds = 3600,
        string command = "ERD 28R"
    )
    {
        for (double lat = 37.78; lat <= 38.0; lat += 0.04)
        {
            for (double lon = -122.94; lon <= -122.42; lon += 0.04)
            {
                AircraftState ac = Airborne(new LatLon(lat, lon), trueHeading: 140, altitude: CruiseAltitude);
                before?.Invoke(ac);
                (SimulationEngine engine, _) = Engine(ac, solo);
                if (!engine.SendCommand(Callsign, command).Success || ac.Phases?.CurrentPhase is not PatternEntryPhase)
                {
                    continue;
                }

                if (AirspaceDatabase.Default.ClassBArea(Sfo).Any(v => v.Contains(ac.Position, ac.Altitude)))
                {
                    continue;
                }

                if (PendingEntry(ac) is not { Vertical: false } entry || (entry.Seconds < minSeconds) || (entry.Seconds > maxSeconds))
                {
                    continue;
                }

                return new DispatchRun(engine, ac, entry);
            }
        }

        throw new InvalidOperationException("no right-side pattern entry whose cleared path enters the SFO Class B");
    }

    /// <summary>A C172 1.5 nm west of the SFO ring, eastbound at 7,500 ft, on a running KOAK 28R pattern entry whose path enters it.</summary>
    private static AircraftState RunningPatternEntryIntoTheBravo()
    {
        AircraftState ac = WestOfTheRing(1.5);
        ac.Phases = new PhaseList { AssignedRunway = Oak28R() };
        ac.Phases.Add(Active(OakPatternEntry()));
        return ac;
    }

    /// <summary>The orbit advisory's terminal words for <paramref name="hold"/>, with its request clause dropped.</summary>
    private static string OrbitAdvisory(AirspaceBoundaryHoldPhase hold) =>
        $"that'll take us into the bravo; we'll orbit {(hold.OrbitDirection == TurnDirection.Left ? "left" : "right")} here.";

    private static PredictedPath? PendingPath(AircraftState ac) =>
        BravoPathPredictor.PredictPending(ac, ac.Phases!, Context(ac, Oak28R()), BravoPathPredictor.ReleaseHorizonSeconds);

    /// <summary>The straight-ray Class B entry the finder falls back to for a phase with no path source.</summary>
    private static double RayEntrySeconds(AircraftState ac) =>
        AirspaceDatabase.Default.FindFirstProjectedEntry(ac, BravoPathPredictor.TriggerLookaheadSeconds, AirspaceClass.Bravo)!.TimeToEntrySeconds;

    private static BravoPathEntry? PendingEntry(AircraftState ac) =>
        PendingPath(ac) is { } path ? BravoPathPredictor.FirstEntry(path, AirspaceDatabase.Default, marginNm: 0, onlyArea: null) : null;

    private static PilotTransmission Readback(AircraftState ac) =>
        Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);

    private static void AssertNoBravoWords(AircraftState ac) =>
        Assert.DoesNotContain(ac.PendingPilotTransmissions, t => t.Text.Contains("bravo", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A C172 1.5 nm west of the SFO ring at 7,500 ft, 130 kt indicated, in contact with a solo student, <paramref name="nm"/>
    /// of travel short of where its eastbound ray meets the ring.
    /// </summary>
    private static AircraftState WestOfTheRing(double nm)
    {
        AircraftState probe = Airborne(NearWestRing, trueHeading: 90, altitude: CruiseAltitude);
        LatLon ring = AirspaceDatabase.Default.FindFirstProjectedEntry(probe, 60, AirspaceClass.Bravo)!.Intersection;
        return Airborne(GeoMath.ProjectPoint(ring, new TrueHeading(270), nm), trueHeading: 90, altitude: CruiseAltitude);
    }

    /// <summary>
    /// An eastbound C172 at 7,500 ft inside the SFO volume its ray first enters, 1 nm short of the next SFO volume east
    /// of it: already inside the area, its cleared path runs into a second volume of it.
    /// </summary>
    private static AircraftState InsideTheOuterShelfShortOfTheNextVolume()
    {
        AircraftState probe = WestOfTheRing(0);
        IReadOnlyList<AirspaceVolume> sfo = AirspaceDatabase.Default.ClassBArea(Sfo);
        AirspaceVolume outer = sfo.First(v => v.Contains(GeoMath.ProjectPoint(probe.Position, new TrueHeading(90), 0.1), CruiseAltitude));
        for (double nm = 0.2; nm < 30; nm += 0.1)
        {
            LatLon point = GeoMath.ProjectPoint(probe.Position, new TrueHeading(90), nm);
            if (sfo.Any(v => (v != outer) && v.Contains(point, CruiseAltitude)))
            {
                return Airborne(GeoMath.ProjectPoint(point, new TrueHeading(270), 1.0), trueHeading: 90, altitude: CruiseAltitude);
            }
        }

        throw new InvalidOperationException("no second SFO volume east of the ring");
    }

    /// <summary>
    /// A C172 under an SFO shelf near KSQL, laterally inside it and clear of every lower floor, 300 ft below the shelf
    /// floor, climbing on an Active approach whose fixes lie on inside the same footprint: the path climbs into the shelf
    /// from below, a vertical entry.
    /// </summary>
    private static AircraftState ClimbingIntoAShelfNearKsql()
    {
        IReadOnlyList<AirspaceVolume> sfo = AirspaceDatabase.Default.ClassBArea(Sfo);
        for (double nm = 0; nm <= 8; nm += 1)
        {
            for (int bearing = 0; bearing < 360; bearing += 30)
            {
                LatLon point = GeoMath.ProjectPoint(Ksql, new TrueHeading(bearing), nm);
                if (ClimbingAt(point, sfo) is { } ac)
                {
                    return ac;
                }
            }
        }

        throw new InvalidOperationException("no SFO shelf near KSQL");
    }

    private static AircraftState? ClimbingAt(LatLon point, IReadOnlyList<AirspaceVolume> sfo)
    {
        List<AirspaceVolume> over = [.. sfo.Where(v => v.ContainsLateral(point)).OrderBy(v => v.LowerFtMsl)];
        if ((over.Count == 0) || (over[0].LowerFtMsl < 1500))
        {
            return null;
        }

        int floor = over[0].LowerFtMsl;
        for (int heading = 0; heading < 360; heading += 45)
        {
            LatLon one = GeoMath.ProjectPoint(point, new TrueHeading(heading), 1.0);
            LatLon two = GeoMath.ProjectPoint(point, new TrueHeading(heading), 2.0);
            if (!over[0].ContainsLateral(one) || !over[0].ContainsLateral(two) || sfo.Any(v => v.ContainsLateral(one) && (v.LowerFtMsl < floor)))
            {
                continue;
            }

            AircraftState ac = Airborne(point, heading, floor - 300);
            ac.VerticalSpeed = 1500;
            ac.Targets.TargetAltitude = floor + 1500;
            ac.Phases!.Add(
                new ApproachNavigationPhase
                {
                    Status = PhaseStatus.Active,
                    Fixes = [new ApproachFix("ONE", one.Lat, one.Lon), new ApproachFix("TWO", two.Lat, two.Lon)],
                }
            );
            if (PendingEntry(ac) is { Vertical: true })
            {
                return ac;
            }
        }

        return null;
    }

    private static PatternWaypoints OakLeftPattern() =>
        PatternGeometry.Compute(
            new PatternGeometryRequest(Oak28R(), AircraftCategory.Piston, "C172", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = null,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

    private static PatternEntryPhase OakPatternEntry()
    {
        RunwayInfo runway = Oak28R();
        return EntryAt(GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + (-90), 1.0));
    }

    private static PatternEntryPhase EntryAt(LatLon point, LatLon? leadIn = null) =>
        new()
        {
            EntryLat = point.Lat,
            EntryLon = point.Lon,
            LeadInLat = leadIn?.Lat,
            LeadInLon = leadIn?.Lon,
            PatternAltitude = Oak28R().ElevationFt + 1000,
            Kind = PatternEntryKind.Direct,
        };

    private static AircraftState Airborne(LatLon position, double trueHeading, double altitude)
    {
        var ac = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            AirportId = "OAK",
            Position = position,
            TrueHeading = new TrueHeading(trueHeading),
            TrueTrack = new TrueHeading(trueHeading),
            Altitude = altitude,
            IndicatedAirspeed = 130,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                HasFlightPlan = true,
                Departure = "KHAF",
                Destination = "KOAK",
            },
            Phases = new PhaseList(),
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(trueHeading);
        ac.HasMadeInitialContact = true;
        ac.HasControllerAcknowledgedInitialContact = true;
        return ac;
    }

    private static T Active<T>(T phase)
        where T : Phase
    {
        phase.Status = PhaseStatus.Active;
        return phase;
    }

    private static PhaseContext Context(AircraftState ac, RunwayInfo runway) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("OAK 28R missing");

    private static (SimulationEngine Engine, SimScenarioState Scenario) Engine(AircraftState ac, bool solo)
    {
        SimScenarioState scenario = Scenario(solo);
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = scenario };
        engine.World.AddAircraft(ac);
        return (engine, scenario);
    }

    private static SimScenarioState Scenario(bool solo) =>
        new()
        {
            ScenarioId = "test-bravo-dispatch-advisory",
            ScenarioName = "Bravo Dispatch Advisory",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = solo,
            PrimaryAirportId = Sfo,
            StudentPositionType = "APP",
        };

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => Ksfo,
            "HAF" or "KHAF" => new LatLon(37.5133, -122.5008),
            _ => null,
        };

    /// <summary>
    /// The aircraft's snapshot as JSON without <see cref="AircraftState.InPhaseWarnedArea"/>, the one member the warning may
    /// write.
    /// </summary>
    private static string SnapshotWithoutTheLatch(AircraftState ac)
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default))!.AsObject();
        foreach (
            string key in json.Select(p => p.Key)
                .Where(k => k.Equals(nameof(AircraftState.InPhaseWarnedArea), StringComparison.OrdinalIgnoreCase))
                .ToList()
        )
        {
            json.Remove(key);
        }

        return json.ToJsonString();
    }
}
