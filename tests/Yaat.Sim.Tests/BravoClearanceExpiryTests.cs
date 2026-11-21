using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Solo training: a VFR Class B clearance names one Class B (its area name, all of its shelf volumes), never carries
/// over to another, and ends when the aircraft is laterally outside that Class B with no re-entry projected (7110.65
/// §7-9-3.b) or when it lands (AIM 3-2-3.d.2.b). It survives a frequency change and a vertical exit inside the
/// footprint. While the pilot waits at the boundary its re-ask says where it is: orbiting outside, or levelled below.
/// </summary>
public sealed class BravoClearanceExpiryTests
{
    private const string Callsign = "N436MS";
    private const string SpokenCallsign = "november four three six mike sierra";
    private const string Sfo = "SFO";

    // The same SFO Class B geometry SoloBravoImplicitClearanceTests uses: west of the 6,000 ft shelf at 7,500 ft, whose
    // western edge runs along 122.853 W at this latitude. Near reaches the ring inside 60 s, Far inside 120 s, Distant
    // only inside 300 s.
    private static readonly LatLon NearWestRing = new(37.62, -122.885);
    private static readonly LatLon FarWestRing = new(37.62, -122.916);
    private static readonly LatLon DistantWestRing = new(37.62, -123.021);

    // East of the western edge: laterally inside the 6,000 ft shelf.
    private static readonly LatLon InsideWestShelf = new(37.62, -122.80);
    private static readonly LatLon SfoAirport = new(37.6213, -122.3790);
    private static readonly LatLon OaklandAirport = new(37.7213, -122.2208);

    // Climbing out of KOAK 28R beneath the SFO 2,100 ft shelf (Issue308AirspaceLevelOffTests).
    private static readonly LatLon UnderSfoShelf = new(37.7387, -122.2474);

    private const double CruiseAltitude = 7500;
    private const double CruiseIas = 120;

    public BravoClearanceExpiryTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Clbrv_WhileOrbitingOutsideTheBravo_ClearsThatAreaAndEndsTheHold()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PhaseList holdList = ac.Phases!;
        Assert.IsType<AirspaceBoundaryHoldPhase>(holdList.CurrentPhase);
        PhaseContext ctx = Context(ac);
        PhaseRunner.Tick(ac, ctx);

        CommandResult result = engine.SendCommand(Callsign, "CLBRV");
        PhaseRunner.Tick(ac, ctx);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.True(holdList.IsComplete, "the clearance ends the hold");

        TickSeconds(engine, 10);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
    }

    [Fact]
    public void Cleared_LaterallyInside_KeptAndMarkedEntered()
    {
        AircraftState ac = Airborne(InsideWestShelf, trueHeading: 90);
        Assert.True(LaterallyInsideSfo(ac.Position));
        ac.GrantBravoClearance(Sfo);

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.True(ac.HasEnteredClearedBravo);
    }

    [Fact]
    public void Entered_ThenOutsideWithNoReEntryFor120s_Ended()
    {
        AircraftState ac = EnteredSfo();
        MoveTo(ac, DistantWestRing, trueHeading: 270);
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 1000);
        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 1119);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.Equal(1000, ac.BravoExitSinceSeconds);

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 1120);

        Assert.Null(ac.ClearedBravoIdent);
        Assert.Null(ac.BravoExitSinceSeconds);
        Assert.False(ac.HasEnteredClearedBravo);
    }

    [Fact]
    public void Entered_ThenBrieflyOutsideWithReEntryWithin120s_Kept()
    {
        AircraftState ac = EnteredSfo();
        MoveTo(ac, NearWestRing, trueHeading: 90);
        Assert.False(LaterallyInsideSfo(ac.Position));
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.True(ac.HasEnteredClearedBravo);
    }

    [Fact]
    public void ClearedNotYetEntered_EntryWithin300sButNot120s_Kept()
    {
        AircraftState ac = Airborne(DistantWestRing, trueHeading: 90);
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));
        Assert.NotNull(
            AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.WaitingEntryLookaheadSeconds, AirspaceClass.Bravo)
        );
        ac.GrantBravoClearance(Sfo);

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.False(ac.HasEnteredClearedBravo);
    }

    [Fact]
    public void ClearedNotYetEntered_TurnedAway_Kept()
    {
        AircraftState ac = Airborne(DistantWestRing, trueHeading: 270);
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.WaitingEntryLookaheadSeconds, AirspaceClass.Bravo));
        ac.GrantBravoClearance(Sfo);

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);
        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 600);

        // Before first entry no projection ends the clearance.
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.Null(ac.BravoExitSinceSeconds);
    }

    [Fact]
    public void Cleared_InsideTheFootprintDescendingBelowTheShelf_Kept()
    {
        AircraftState ac = EnteredSfo();
        ac.Altitude = 4000;
        ac.VerticalSpeed = -500;
        Assert.DoesNotContain(AirspaceDatabase.Default.FindContaining(ac.Position, ac.Altitude), v => v.Class == AirspaceClass.Bravo);

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
    }

    [Fact]
    public void Cleared_ThenFrequencyChangeApproved_Kept()
    {
        AircraftState ac = Airborne(InsideWestShelf, trueHeading: 90);
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        CommandResult result = engine.SendCommand(Callsign, "FCA");
        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
    }

    [Fact]
    public void Cleared_LandsInsideTheBravo_EndedAtTouchdown()
    {
        RunwayInfo runway = Assert.IsType<RunwayInfo>(Yaat.Sim.Data.NavigationDatabase.Instance.GetRunway(Sfo, "28R"));
        AircraftState ac = Airborne(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), trueHeading: runway.TrueHeading.Degrees);
        ac.Altitude = runway.ElevationFt + 25;
        ac.IndicatedAirspeed = 70;
        ac.GrantBravoClearance(Sfo);
        var phases = new PhaseList { AssignedRunway = runway };
        var landing = new LandingPhase();
        phases.Add(landing);
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            ScenarioElapsedSeconds = 245.0,
        };
        landing.OnStart(ctx);
        landing.CurrentState = LandingPhase.State.Touchdown;

        landing.OnTick(ctx);

        Assert.True(ac.IsOnGround);
        Assert.Null(ac.ClearedBravoIdent);
    }

    [Fact]
    public void Cleared_TouchAndGoInsideTheBravo_KeptAfterTheTouchAndGo()
    {
        RunwayInfo runway = Assert.IsType<RunwayInfo>(Yaat.Sim.Data.NavigationDatabase.Instance.GetRunway(Sfo, "28R"));
        AircraftState ac = Airborne(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), trueHeading: runway.TrueHeading.Degrees);
        ac.Altitude = runway.ElevationFt;
        ac.IndicatedAirspeed = 65;
        ac.GrantBravoClearance(Sfo);
        var phases = new PhaseList { AssignedRunway = runway };
        var touchAndGo = new TouchAndGoPhase();
        phases.Add(touchAndGo);
        ac.Phases = phases;
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            ScenarioElapsedSeconds = 245.0,
        };

        touchAndGo.OnStart(ctx);
        for (int i = 0; i < 5; i++)
        {
            touchAndGo.OnTick(ctx);
            PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);
        }

        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        // Climbing out in the pattern, still laterally inside the SFO Class B.
        ac.IsOnGround = false;
        ac.Altitude = runway.ElevationFt + 500;
        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.True(ac.HasEnteredClearedBravo);
    }

    [Fact]
    public void ClearedIntoAnotherBravo_SfoGateClosedAndThePilotRequestsSfo()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        ac.GrantBravoClearance("LAX");

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        PilotTransmission request = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("request clearance through the bravo.", request.Text);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.Equal(Sfo, ac.PendingPilotRequest!.AirspaceIdent);
        Assert.Equal("LAX", ac.ClearedBravoIdent);
    }

    [Fact]
    public void BareClbrv_ClearsTheBravoInsideElseAheadElseNothing()
    {
        AircraftState inside = Airborne(InsideWestShelf, trueHeading: 270);
        CommandResult insideResult = DispatchClbrv(inside);
        Assert.Equal(Sfo, inside.ClearedBravoIdent);
        Assert.Equal("Cleared into Bravo airspace", insideResult.Message);

        AircraftState ahead = Airborne(FarWestRing, trueHeading: 90);
        Assert.False(LaterallyInsideSfo(ahead.Position));
        CommandResult aheadResult = DispatchClbrv(ahead);
        Assert.Equal(Sfo, ahead.ClearedBravoIdent);
        Assert.Equal("Cleared into Bravo airspace", aheadResult.Message);

        AircraftState clear = Airborne(DistantWestRing, trueHeading: 270);
        CommandResult clearResult = DispatchClbrv(clear);
        Assert.True(clearResult.Success);
        Assert.Null(clear.ClearedBravoIdent);
        Assert.Equal("Cleared into Bravo airspace", clearResult.Message);
    }

    [Fact]
    public void Clbrv_OnTheGround_KeptAcrossGroundTicks()
    {
        AircraftState ac = Airborne(SfoAirport, trueHeading: 280);
        ac.IsOnGround = true;
        ac.IndicatedAirspeed = 0;
        Assert.True(DispatchClbrv(ac).Success);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);
        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.False(ac.HasEnteredClearedBravo, "the expiry tick does nothing on the ground");
    }

    [Fact]
    public void ImplicitVectorGrant_SetsTheArea_SecondVectorIntoItIsNotReadBackAsAClearance()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 270);
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "FH 090").Success);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.Contains(ac.PendingPilotTransmissions, t => t.Text.EndsWith(", cleared into the bravo", StringComparison.Ordinal));
        TickSeconds(engine, 10);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 095");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.DoesNotContain(ac.PendingPilotTransmissions, t => t.Text.Contains("bravo", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("KMOD", "through")]
    [InlineData("KSFO", "into")]
    public void OrbitHoldReask_SaysOrbitingOutsideTheBravo(string destination, string preposition)
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, destination);
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        Assert.Equal(AirspaceHoldMode.Orbit, Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase).Mode);
        ac.PendingPilotTransmissions.Clear();

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, nowSeconds: 130, studentPosition: null, LookupAirport));

        PilotTransmission reask = Assert.Single(ac.PendingPilotTransmissions);
        string line = $"orbiting outside the bravo, still request clearance {preposition} the bravo.";
        Assert.Equal(line, reask.Text);
        Assert.Equal($"{SpokenCallsign}, {line}", reask.SpeechText);
    }

    [Theory]
    [InlineData(BravoClearanceWording.Through, "through")]
    [InlineData(BravoClearanceWording.Into, "into")]
    public void LevelOffHoldReask_SaysLevelBelowTheBravo(BravoClearanceWording wording, string preposition)
    {
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3);
        ac.Altitude = 1485;
        ac.IndicatedAirspeed = 80;
        ac.VerticalSpeed = 926;
        ac.Targets.TargetAltitude = 3500;
        RecordSfoRequest(ac, PilotResponder.BuildBravoClearanceRequest(ac, wording), wording);
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(AirspaceHoldMode.LevelOff, hold.Mode);
        Assert.Equal(2000, hold.LevelOffCeilingFtMsl);
        ac.PendingPilotTransmissions.Clear();

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, nowSeconds: 130, studentPosition: null, LookupAirport));

        PilotTransmission reask = Assert.Single(ac.PendingPilotTransmissions);
        string request = $"still request clearance {preposition} the bravo.";
        Assert.Equal($"level 2000 below the bravo, {request}", reask.Text);
        Assert.Equal($"{SpokenCallsign}, level two thousand below the bravo, {request}", reask.SpeechText);
    }

    [Fact]
    public void OpenRequestWithNoHold_ReaskIsTheOriginalLine()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90);
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        Assert.Null(ac.Phases);
        ac.PendingPilotTransmissions.Clear();

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, nowSeconds: 130, studentPosition: null, LookupAirport));

        PilotTransmission reask = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("request clearance through the bravo.", reask.Text);
        Assert.Equal($"{SpokenCallsign}, request clearance through the bravo.", reask.SpeechText);
    }

    [Fact]
    public void CaptureWait_ReadsTheStoredWordingNotTheLineText()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90);
        RecordSfoRequest(ac, new PilotSpeechText("anything at all.", "anything at all."), BravoClearanceWording.Through);

        BravoClearanceWait? wait = ImplicitBravoClearance.CaptureWait(ac);

        Assert.NotNull(wait);
        Assert.Equal(Sfo, wait.Ident);
        Assert.Equal(BravoClearanceWording.Through, wait.Wording);
    }

    private static void RecordSfoRequest(AircraftState ac, PilotSpeechText line, BravoClearanceWording wording)
    {
        PilotPendingRequest request = PilotRequestTracker.RecordRequest(
            ac,
            PilotPendingRequestKind.AirspaceEntry,
            nowSeconds: 0,
            line,
            PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, SfoAirport)
        );
        request.BravoWording = wording;
    }

    // --- Engine-ticked runs: the solo tick order runs the expiry tick before the boundary-respect tick. ---

    [Fact]
    public void VectorGrantWhileStillTurning_KeptAndNoHoldInserted()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 270);
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "FH 090").Success);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        for (int i = 0; i < 60; i++)
        {
            engine.TickOneSecond();
            Assert.False(ac.Phases?.CurrentPhase is AirspaceBoundaryHoldPhase, $"a boundary hold was inserted at {i + 1} s");
        }

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
    }

    [Fact]
    public void ClbrvOnTheOutboundHalfOfTheOrbit_KeptAndHeadsInWithNoSecondRequest()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, _) = SoloEngine(ac);
        engine.TickOneSecond();
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        for (int i = 0; (i < 180) && !HeadsAwayFromSfo(ac); i++)
        {
            engine.TickOneSecond();
        }

        Assert.True(HeadsAwayFromSfo(ac), $"the orbit never turned outbound: heading {ac.TrueHeading.Degrees:F0}");
        PilotPendingRequest request = ac.PendingPilotRequest!;
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        TickSeconds(engine, 60);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.False(ac.Phases?.CurrentPhase is AirspaceBoundaryHoldPhase, "the hold ended and did not re-arm");
        Assert.Same(request, ac.PendingPilotRequest);
        Assert.False(ac.PendingPilotRequest!.IsOpen);
        Assert.Equal(90, ac.Targets.TargetTrueHeading?.Degrees ?? double.NaN, 1.0);
    }

    [Fact]
    public void PatternEntryGrantMoreThan300sOut_Kept()
    {
        // About 14 nm west of the SFO Class B's western edge: beyond the 300 s waiting lookahead at this speed.
        AircraftState ac = Airborne(new LatLon(37.62, -123.148), trueHeading: 90, destination: "KSFO");
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.WaitingEntryLookaheadSeconds, AirspaceClass.Bravo));
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "ERD 28R").Success);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        TickSeconds(engine, 60);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.False(ac.HasEnteredClearedBravo);
    }

    [Fact]
    public void ClbrvOnTheGroundUnderTheShelfAtOakland_KeptAfterAnEastboundDeparture()
    {
        AircraftState ac = Airborne(OaklandAirport, trueHeading: 90);
        ac.IsOnGround = true;
        ac.IndicatedAirspeed = 0;
        ac.Altitude = 9;
        Assert.True(LaterallyInsideSfo(ac.Position), "KOAK sits beneath the SFO Class B");
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        TickSeconds(engine, 5);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        // Lifted off eastbound, away from the SFO core, below the shelf.
        ac.IsOnGround = false;
        ac.Altitude = 600;
        ac.IndicatedAirspeed = 90;
        TickSeconds(engine, 60);

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
    }

    [Fact]
    public void AfterEntry_ExitsAndTurnsBackInWithin120s_Kept()
    {
        AircraftState ac = Airborne(InsideWestShelf, trueHeading: 270);
        (SimulationEngine engine, _) = SoloEngine(ac);
        ac.GrantBravoClearance(Sfo);
        engine.TickOneSecond();
        Assert.True(ac.HasEnteredClearedBravo);
        TickUntilOutsideSfo(engine, ac);
        TickSeconds(engine, 20);
        Assert.NotNull(ac.BravoExitSinceSeconds);

        Assert.True(engine.SendCommand(Callsign, "FH 090").Success);
        for (int i = 0; (i < 240) && !LaterallyInsideSfo(ac.Position); i++)
        {
            engine.TickOneSecond();
            Assert.Equal(Sfo, ac.ClearedBravoIdent);
        }

        Assert.True(LaterallyInsideSfo(ac.Position), "the aircraft flew back into the SFO Class B");
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.Null(ac.BravoExitSinceSeconds);
    }

    [Fact]
    public void AfterEntry_ExitsAndFliesAway_EndedAfter120sOutsideNotBefore()
    {
        AircraftState ac = Airborne(InsideWestShelf, trueHeading: 270);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        ac.GrantBravoClearance(Sfo);
        engine.TickOneSecond();
        Assert.True(ac.HasEnteredClearedBravo);
        TickUntilOutsideSfo(engine, ac);
        double exitedAt = Assert.NotNull(ac.BravoExitSinceSeconds);
        Assert.InRange(scenario.ElapsedSeconds - exitedAt, 0, 1);

        TickUntil(engine, scenario, exitedAt + 110);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);

        TickUntil(engine, scenario, exitedAt + 130);
        Assert.Null(ac.ClearedBravoIdent);
    }

    [Fact]
    public void HelicopterFullStopLandingInsideTheBravo_Ended()
    {
        AircraftState ac = Airborne(SfoAirport, trueHeading: 280);
        ac.AircraftType = "S76";
        ac.Altitude = 13;
        ac.IndicatedAirspeed = 0;
        ac.GrantBravoClearance(Sfo);
        var landing = new HelicopterLandingPhase();
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Helicopter,
            DeltaSeconds = 1.0,
            FieldElevation = 13,
            Logger = NullLogger.Instance,
        };
        landing.OnStart(ctx);

        Assert.True(landing.OnTick(ctx));

        Assert.True(ac.IsOnGround);
        Assert.Null(ac.ClearedBravoIdent);
    }

    [Fact]
    public void Pre56SnapshotWithTheOldFlag_RestoresNoClassBClearance()
    {
        AircraftState ac = Airborne(InsideWestShelf, trueHeading: 90);
        System.Text.Json.Nodes.JsonObject json = System
            .Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ac.ToSnapshot()))!
            .AsObject();
        Assert.False(json.ContainsKey("ClearedBravoIdent"));
        json["IsClearedIntoBravo"] = true;

        AircraftSnapshotDto dto = System.Text.Json.JsonSerializer.Deserialize<AircraftSnapshotDto>(json.ToJsonString())!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Null(restored.ClearedBravoIdent);
        Assert.False(restored.HasEnteredClearedBravo);
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static void TickUntil(SimulationEngine engine, SimScenarioState scenario, double elapsedSeconds)
    {
        for (int i = 0; (i < 600) && (scenario.ElapsedSeconds < elapsedSeconds); i++)
        {
            engine.TickOneSecond();
        }

        Assert.True(scenario.ElapsedSeconds >= elapsedSeconds, $"the scenario clock stopped at {scenario.ElapsedSeconds}");
    }

    private static void TickUntilOutsideSfo(SimulationEngine engine, AircraftState ac)
    {
        for (int i = 0; (i < 240) && LaterallyInsideSfo(ac.Position); i++)
        {
            engine.TickOneSecond();
        }

        Assert.False(LaterallyInsideSfo(ac.Position), "the aircraft never left the SFO Class B");
    }

    /// <summary>True while the aircraft's heading is in the western half, away from the SFO Class B to its east.</summary>
    private static bool HeadsAwayFromSfo(AircraftState ac) => ac.TrueHeading.Degrees is > 200 and < 340;

    private static CommandResult DispatchClbrv(AircraftState ac)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("CLBRV");
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        return CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(Random.Shared, soloTrainingMode: true));
    }

    /// <summary>A cleared aircraft that has been laterally inside the SFO Class B.</summary>
    private static AircraftState EnteredSfo()
    {
        AircraftState ac = Airborne(InsideWestShelf, trueHeading: 90);
        ac.GrantBravoClearance(Sfo);
        PilotProactive.TickBravoClearanceExpiry(ac, AirspaceDatabase.Default, nowSeconds: 0);
        Assert.True(ac.HasEnteredClearedBravo);
        return ac;
    }

    private static void MoveTo(AircraftState ac, LatLon position, double trueHeading)
    {
        ac.Position = position;
        ac.TrueHeading = new TrueHeading(trueHeading);
        ac.TrueTrack = new TrueHeading(trueHeading);
        ac.Targets.TargetTrueHeading = new TrueHeading(trueHeading);
    }

    private static bool LaterallyInsideSfo(LatLon position) =>
        AirspaceDatabase.Default.Volumes.Any(v => (v.Class == AirspaceClass.Bravo) && (v.Ident == Sfo) && v.ContainsLateral(position));

    private static (SimulationEngine Engine, SimScenarioState Scenario) SoloEngine(AircraftState ac)
    {
        SimScenarioState scenario = SoloScenario();
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = scenario };
        engine.World.AddAircraft(ac);
        return (engine, scenario);
    }

    private static AircraftState Airborne(LatLon position, double trueHeading, string destination = "KMOD")
    {
        var ac = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C182",
            Position = position,
            TrueHeading = new TrueHeading(trueHeading),
            TrueTrack = new TrueHeading(trueHeading),
            Altitude = CruiseAltitude,
            IndicatedAirspeed = CruiseIas,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                HasFlightPlan = true,
                Departure = "KHAF",
                Destination = destination,
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(trueHeading);
        ac.HasMadeInitialContact = true;
        ac.HasControllerAcknowledgedInitialContact = true;
        return ac;
    }

    private static SimScenarioState SoloScenario() =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = "SFO",
            StudentPositionType = "APP",
        };

    private static PhaseContext Context(AircraftState ac) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => new LatLon(37.6213, -122.3790),
            _ => null,
        };
}
