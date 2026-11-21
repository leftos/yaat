using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Solo training: <c>RMOB [minutes]</c> tells a VFR pilot to remain outside the Class B (FAA 7110.65 §7-9-2.a). The pilot
/// reads it back, keeps holding at the boundary, takes no implicit Class B clearance and raises no request of its own for
/// that Class B until an explicit <c>CLBRV</c>. A pilot that was waiting asks once more, "any estimate on the bravo?",
/// 300 s later (or at the expected minutes plus 60 s). A vector or direct into that Class B draws the §7-9-2 NOTE 1
/// advisory in place of the readback, and the hold does not release.
/// </summary>
public sealed class RemainOutsideBravoTests
{
    private const string Callsign = "N436MS";
    private const string SpokenCallsign = "november four three six mike sierra";
    private const string Sfo = "SFO";
    private const string AnyEstimate = "any estimate on the bravo?";

    // The SFO Class B geometry SoloBravoImplicitClearanceTests uses: west of the 6,000 ft shelf at 7,500 ft, whose western
    // edge runs along 122.853 W at this latitude. Near reaches the ring inside 60 s, Far inside 120 s.
    private static readonly LatLon NearWestRing = new(37.62, -122.885);
    private static readonly LatLon FarWestRing = new(37.62, -122.916);
    private static readonly LatLon DistantWestRing = new(37.62, -123.021);

    // East of the western edge: laterally inside the 6,000 ft shelf.
    private static readonly LatLon InsideWestShelf = new(37.62, -122.80);

    // Climbing out of KOAK 28R beneath the SFO 2,100 ft shelf (Issue308AirspaceLevelOffTests).
    private static readonly LatLon UnderSfoShelf = new(37.7387, -122.2474);

    // Over Sacramento: far more than 30 nm from any Class B.
    private static readonly LatLon OverSacramento = new(38.69, -121.59);

    private const double CruiseAltitude = 7500;
    private const double CruiseIas = 120;

    public RemainOutsideBravoTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void RMOB_WhileOrbitingWithAnOpenRequest_DeniesIt_ReadsBack_KeepsHolding()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PhaseList holdList = OrbitWithOpenRequest(ac, scenario);
        PilotPendingRequest request = ac.PendingPilotRequest!;

        CommandResult result = engine.SendCommand(Callsign, "RMOB");

        Assert.True(result.Success, result.Message);
        Assert.Equal(PilotPendingRequestResponseState.Denied, request.ResponseState);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("remain outside the bravo", readback.Text);
        Assert.EndsWith($"remain outside the bravo, {SpokenCallsign}.", readback.SpeechText);

        PhaseContext ctx = Context(ac);
        for (int i = 0; i < 5; i++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        Assert.Same(holdList, ac.Phases);
        Assert.False(holdList.IsComplete, "the pilot keeps holding outside the bravo");
        Assert.IsType<AirspaceBoundaryHoldPhase>(holdList.CurrentPhase);
    }

    [Fact]
    public void RMOB_WithMinutes_ReadsBackOneFiveMinutes()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90);
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "RMOB 15");

        Assert.True(result.Success, result.Message);
        Assert.Equal(15, ac.RemainOutsideBravo?.Minutes);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("remain outside the bravo, expect clearance in 15 minutes", readback.Text);
        Assert.EndsWith($"remain outside the bravo, expect clearance in one five minutes, {SpokenCallsign}.", readback.SpeechText);
    }

    [Fact]
    public void RMOB_NoRequest_ProjectedEntry_SetsTheStateForThatBravo()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90);
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "RMOB");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        Assert.Null(ac.RemainOutsideBravo!.AskDueSeconds);
        Assert.Null(ac.ClearedBravoIdent);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void RMOB_InsideTheBravo_QueriesIntentions_KeepsTheClearance_NoState()
    {
        AircraftState ac = Airborne(InsideWestShelf, trueHeading: 90);
        Assert.Contains(AirspaceDatabase.Default.FindContaining(ac.Position, ac.Altitude), v => v.Class == AirspaceClass.Bravo);
        ac.GrantBravoClearance(Sfo);
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "RMOB 5");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.Null(ac.RemainOutsideBravo);
        PilotTransmission reply = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("we're inside the bravo, say intentions", reply.Text);
        Assert.EndsWith($"we're inside the bravo, say intentions, {SpokenCallsign}.", reply.SpeechText);
    }

    [Fact]
    public void RMOB_LevelledUnderTheShelf_IsOutside_KeepsTheLevelOff()
    {
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3);
        ac.Altitude = 1485;
        ac.IndicatedAirspeed = 80;
        ac.VerticalSpeed = 926;
        ac.Targets.TargetAltitude = 3500;
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        RecordSfoRequest(ac);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(AirspaceHoldMode.LevelOff, hold.Mode);
        PhaseContext ctx = Context(ac);
        PhaseRunner.Tick(ac, ctx);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "RMOB");
        PhaseRunner.Tick(ac, ctx);

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("remain outside the bravo", readback.Text);
        Assert.Same(hold, ac.Phases?.CurrentPhase);
        Assert.Equal(2000, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void RMOB_NoBravoWithin30Nm_SaysSayAgain_NoState()
    {
        AircraftState ac = Airborne(OverSacramento, trueHeading: 90);
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "RMOB");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.RemainOutsideBravo);
        PilotTransmission reply = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("say again, we're not near the bravo", reply.Text);
        Assert.EndsWith($"say again, we're not near the bravo, {SpokenCallsign}.", reply.SpeechText);
    }

    [Fact]
    public void RMOB_WhenClearedForThatBravoOutsideIt_EndsTheClearance()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90);
        ac.GrantBravoClearance(Sfo);
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "RMOB");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.ClearedBravoIdent);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
    }

    [Theory]
    [InlineData("RMOB 0")]
    [InlineData("RMOB 61")]
    [InlineData("RMOB X")]
    public void RMOB_Minutes0Or61_IsAParseError(string text) => Assert.False(CommandParser.Parse(text).IsSuccess);

    [Theory]
    [InlineData("RMOB", null)]
    [InlineData("RMOB 1", 1)]
    [InlineData("RMOB 5", 5)]
    [InlineData("RMOB5", 5)]
    [InlineData("RMOB 60", 60)]
    public void RMOB_Parses(string text, int? minutes)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);

        RemainOutsideBravoCommand command = Assert.IsType<RemainOutsideBravoCommand>(parsed.Value);
        Assert.Equal(minutes, command.Minutes);
    }

    [Theory]
    [InlineData("RMOB", 300)]
    [InlineData("RMOB 5", 360)]
    public void AnyEstimateAsk_At300sOrMinutesPlus60s_Once(string rmob, int askAfterSeconds)
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        double issuedAt = RmobWhileOrbiting(engine, scenario, ac, rmob);

        TickUntil(engine, scenario, issuedAt + askAfterSeconds - 1);
        Assert.NotEqual(AnyEstimate, ac.PendingPilotRequest?.LastPilotLine);

        TickUntil(engine, scenario, issuedAt + askAfterSeconds + 1);
        PilotPendingRequest ask = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(AnyEstimate, ask.LastPilotLine);
        Assert.Equal($"{SpokenCallsign}, {AnyEstimate}", ask.LastPilotLineTts);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ask.Kind);
        Assert.Equal(Sfo, ask.AirspaceIdent);
        Assert.True(ask.IsOpen);
        Assert.InRange(ask.FirstRequestedAtSeconds, issuedAt + askAfterSeconds, issuedAt + askAfterSeconds + 1);
        Assert.True(ac.RemainOutsideBravo!.AskVoiced);
        Assert.Null(ac.RemainOutsideBravo.AskDueSeconds);
    }

    [Fact]
    public void AnyEstimate_Unanswered_RepeatsOnceAfter120s_ThenStops()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotPendingRequest ask = AskAnyEstimate(engine, scenario, ac);
        double askedAt = ask.FirstRequestedAtSeconds;

        TickUntil(engine, scenario, askedAt + 121);
        Assert.Equal(1, ask.FollowUpsVoiced);
        Assert.Equal(AnyEstimate, ask.LastPilotLine);
        Assert.InRange(ask.LastRequestedAtSeconds, askedAt + 120, askedAt + 121);

        TickUntil(engine, scenario, askedAt + 500);
        Assert.Equal(1, ask.FollowUpsVoiced);
        Assert.Same(ask, ac.PendingPilotRequest);
        Assert.False(ask.IsOpen, "the unanswered ask stops after its one repeat");
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
    }

    [Fact]
    public void AnyEstimate_SecondRmob_DeniesAndStopsTheAsks()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotPendingRequest ask = AskAnyEstimate(engine, scenario, ac);

        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);

        Assert.Equal(PilotPendingRequestResponseState.Denied, ask.ResponseState);
        Assert.Null(ac.RemainOutsideBravo!.AskDueSeconds);
        TickSeconds(engine, 600);
        Assert.Same(ask, ac.PendingPilotRequest);
        Assert.Equal(0, ask.FollowUpsVoiced);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void AnyEstimate_Standby_EndsTheAsks_PilotKeepsHolding()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotPendingRequest ask = AskAnyEstimate(engine, scenario, ac);

        Assert.True(engine.SendCommand(Callsign, "STBY").Success);
        Assert.Equal(PilotPendingRequestResponseState.Standby, ask.ResponseState);

        TickSeconds(engine, 600);
        Assert.Same(ask, ac.PendingPilotRequest);
        Assert.Equal(0, ask.FollowUpsVoiced);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void VectorIntoTheBravo_WhileTheAskIsOpen_LeavesItOpen_AndItRepeatsOnce()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotPendingRequest ask = AskAnyEstimate(engine, scenario, ac);
        double askedAt = ask.FirstRequestedAtSeconds;

        Assert.True(engine.SendCommand(Callsign, "FH 090").Success);

        Assert.True(ask.IsOpen, "a vector into the bravo does not answer the ask");
        Assert.Equal(PilotPendingRequestResponseState.None, ask.ResponseState);
        Assert.Null(ac.ClearedBravoIdent);
        TickUntil(engine, scenario, askedAt + 121);
        Assert.Same(ask, ac.PendingPilotRequest);
        Assert.Equal(1, ask.FollowUpsVoiced);
        Assert.Equal(AnyEstimate, ask.LastPilotLine);
    }

    [Fact]
    public void Clbrv_AfterRmob_ClearsTheState_EndsTheHold_GrantsTheClearance()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PhaseList holdList = OrbitWithOpenRequest(ac, scenario);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));

        CommandResult result = engine.SendCommand(Callsign, "CLBRV");
        PhaseRunner.Tick(ac, Context(ac));

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.RemainOutsideBravo);
        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.True(holdList.IsComplete, "the clearance ends the hold");
    }

    [Fact]
    public void VectorIntoTheBravo_UnderRmob_SaysTheAdvisory_NoGrant_HoldNotReleased()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PhaseList holdList = OrbitWithOpenRequest(ac, scenario);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 090");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.ClearedBravoIdent);
        PilotTransmission advisory = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Readback, advisory.Kind);
        Assert.Equal("heading 090 takes us into the bravo, we're not cleared", advisory.Text);
        Assert.EndsWith($"heading zero niner zero takes us into the bravo, we're not cleared, {SpokenCallsign}.", advisory.SpeechText);

        PhaseContext ctx = Context(ac);
        for (int i = 0; i < 5; i++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        Assert.False(holdList.IsComplete, "the vector does not release the hold while the pilot must remain outside");
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        Assert.Same(holdList, ac.Phases);
        Assert.Equal(90, ac.Targets.AssignedMagneticHeading?.Degrees ?? double.NaN, 0.5);

        // Cleared in, the pilot resumes the stored vector.
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        PhaseRunner.Tick(ac, ctx);
        Assert.True(holdList.IsComplete);
        Assert.Equal(new MagneticHeading(90).ToTrue(ac.Declination).Degrees, ac.Targets.TargetTrueHeading?.Degrees ?? double.NaN, 0.5);
    }

    [Fact]
    public void DirectAcrossTheBravo_UnderRmob_HoldStopsItAtTheBoundary()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270);
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.True(ac.IsRemainOutsideBravo(Sfo), "the nearest Class B within 30 nm");
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "DCT SFO");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.ClearedBravoIdent);
        PilotTransmission advisory = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("direct SFO takes us into the bravo, we're not cleared", advisory.Text);
        Assert.EndsWith($" takes us into the bravo, we're not cleared, {SpokenCallsign}.", advisory.SpeechText);

        for (int i = 0; i < 300; i++)
        {
            engine.TickOneSecond();
            Assert.False(InsideSfo(ac), $"the pilot under RMOB entered the SFO Class B {i + 1} s after the direct");
        }

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.ClearedBravoIdent);

        // Cleared in, the pilot flies the direct the hold kept.
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        engine.TickOneSecond();
        Assert.Equal(Sfo, ac.Targets.NavigationRoute.FirstOrDefault()?.Name);
    }

    [Fact]
    public void VectorAwayFromTheBravo_UnderRmob_NormalReadback_HoldEnds()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PhaseList holdList = OrbitWithOpenRequest(ac, scenario);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 270");
        PhaseRunner.Tick(ac, Context(ac));

        Assert.True(result.Success, result.Message);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("fly heading 270", readback.Text);
        Assert.True(holdList.IsComplete, "a vector that stays out of the bravo ends the hold");
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
    }

    [Fact]
    public void PatternEntryAtABravoAirport_UnderRmob_NoImplicitGrant()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KSFO");
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.ClearedBravoIdent);
        Assert.DoesNotContain(ac.PendingPilotTransmissions, t => t.Text.Contains("cleared into the bravo", StringComparison.OrdinalIgnoreCase));
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
    }

    [Fact]
    public void NoOwnClearanceRequest_UnderRmob()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        ac.PendingPilotTransmissions.Clear();

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void FrequencyChange_CancelsTheAsk_KeepsTheState()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        OrbitWithOpenRequest(ac, scenario);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.NotNull(ac.RemainOutsideBravo!.AskDueSeconds);

        Assert.True(engine.SendCommand(Callsign, "FCA").Success);
        Assert.True(ac.IsTunedAwayFrom(scenario.StudentPosition));
        PilotProactive.TickRemainOutsideBravoAsk(ac, scenario, LookupAirport);

        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        Assert.Null(ac.RemainOutsideBravo!.AskDueSeconds);
        Assert.False(ac.RemainOutsideBravo.AskVoiced);
    }

    [Fact]
    public void LandingOrLeaving_DoesNotClearTheRmobState()
    {
        // Leaving: engine-ticked out of the Class B's area, through every solo tick.
        AircraftState leaving = Airborne(InsideWestShelf, trueHeading: 270);
        (SimulationEngine engine, _) = SoloEngine(leaving);
        leaving.StartRemainOutsideBravo(Sfo, minutes: null, askDueSeconds: null);
        Assert.True(InsideSfo(leaving));
        TickSeconds(engine, 240);
        Assert.DoesNotContain(AirspaceDatabase.Default.ClassBAreas[Sfo], volume => volume.ContainsLateral(leaving.Position));
        Assert.True(leaving.IsRemainOutsideBravo(Sfo));

        RunwayInfo runway = Assert.IsType<RunwayInfo>(Yaat.Sim.Data.NavigationDatabase.Instance.GetRunway(Sfo, "28R"));
        AircraftState ac = Airborne(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), trueHeading: runway.TrueHeading.Degrees);
        ac.Altitude = runway.ElevationFt + 25;
        ac.IndicatedAirspeed = 70;
        ac.StartRemainOutsideBravo(Sfo, minutes: 5, askDueSeconds: null);
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
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
    }

    [Fact]
    public void CompoundVectorAndDescent_UnderRmob_ReadsBackBothWithTheAdvisory()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        OrbitWithOpenRequest(ac, scenario);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 090, DM 035");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.ClearedBravoIdent);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Readback, readback.Kind);
        Assert.StartsWith("heading 090 takes us into the bravo, we're not cleared, ", readback.Text);
        Assert.EndsWith(
            "heading zero niner zero takes us into the bravo, we're not cleared, "
                + $"descend and maintain three thousand five hundred, {SpokenCallsign}.",
            readback.SpeechText
        );
    }

    [Fact]
    public void CompoundVectorAndContact_UnderRmob_ReadsBackTheContact()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        OrbitWithOpenRequest(ac, scenario);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 090, FCA");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsTunedAwayFrom(scenario.StudentPosition), "the frequency change is acted on");
        Assert.Contains(ac.PendingPilotTransmissions, t => (t.Kind == PilotTransmissionKind.Readback) && (t.Text == "good day."));
        Assert.Contains(
            ac.PendingPilotTransmissions,
            t => (t.Kind == PilotTransmissionKind.Readback) && (t.Text == "heading 090 takes us into the bravo, we're not cleared")
        );
    }

    [Fact]
    public void RelativeTurnIntoTheBravo_UnderRmob_ReadsBackTheDegrees()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90);
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "LT 20");

        Assert.True(result.Success, result.Message);
        PilotTransmission advisory = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("turn left 20 degrees takes us into the bravo, we're not cleared", advisory.Text);
        Assert.EndsWith($"turn left two zero degrees takes us into the bravo, we're not cleared, {SpokenCallsign}.", advisory.SpeechText);
    }

    [Fact]
    public void Vector200sOutIntoTheRmobBravo_GetsTheAdvisory()
    {
        AircraftState ac = Airborne(DistantWestRing, trueHeading: 90);
        (SimulationEngine engine, _) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.True(ac.IsRemainOutsideBravo(Sfo), "the nearest Class B within 30 nm");
        Assert.Null(ac.RemainOutsideBravo!.AskDueSeconds);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 090");

        Assert.True(result.Success, result.Message);
        PilotTransmission advisory = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("heading 090 takes us into the bravo, we're not cleared", advisory.Text);
    }

    [Fact]
    public void Vector200sOut_NoRmob_OrbitsOutsideAndNeverEnters()
    {
        AircraftState ac = Airborne(DistantWestRing, trueHeading: 90);
        (SimulationEngine engine, _) = SoloEngine(ac);

        Assert.True(engine.SendCommand(Callsign, "FH 090").Success);
        Assert.Null(ac.ClearedBravoIdent);

        for (int i = 0; i < 420; i++)
        {
            engine.TickOneSecond();
            Assert.False(InsideSfo(ac), $"the uncleared pilot entered the SFO Class B {i + 1} s after the vector");
        }

        Assert.Null(ac.ClearedBravoIdent);
        Assert.Null(ac.RemainOutsideBravo);
    }

    /// <summary>Whether the aircraft is inside a volume of the SFO Class B, in 3D.</summary>
    private static bool InsideSfo(AircraftState ac) =>
        AirspaceDatabase
            .Default.FindClassBContaining(ac.Position, ac.Altitude)
            .Any(volume => AirspaceDatabase.Default.ClassBAreas[Sfo].Contains(volume));

    /// <summary>The boundary tick puts the aircraft in an orbit outside the SFO Class B with an open request, two ticks in.</summary>
    private static PhaseList OrbitWithOpenRequest(AircraftState ac, SimScenarioState scenario)
    {
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PhaseList holdList = ac.Phases!;
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(holdList.Phases));
        Assert.Equal(AirspaceHoldMode.Orbit, hold.Mode);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        PhaseContext ctx = Context(ac);
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);
        ac.PendingPilotTransmissions.Clear();
        return holdList;
    }

    /// <summary>Engine-ticked: the pilot orbits with an open request, and <paramref name="rmob"/> answers it; returns when.</summary>
    private static double RmobWhileOrbiting(SimulationEngine engine, SimScenarioState scenario, AircraftState ac, string rmob)
    {
        engine.TickOneSecond();
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        double issuedAt = scenario.ElapsedSeconds;
        Assert.True(engine.SendCommand(Callsign, rmob).Success);
        Assert.Equal(PilotPendingRequestResponseState.Denied, ac.PendingPilotRequest!.ResponseState);
        return issuedAt;
    }

    /// <summary>Engine-ticked until the pilot under RMOB has asked "any estimate on the bravo?"; returns that request.</summary>
    private static PilotPendingRequest AskAnyEstimate(SimulationEngine engine, SimScenarioState scenario, AircraftState ac)
    {
        double issuedAt = RmobWhileOrbiting(engine, scenario, ac, "RMOB");
        TickUntil(engine, scenario, issuedAt + 301);
        PilotPendingRequest ask = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(AnyEstimate, ask.LastPilotLine);
        Assert.True(ask.IsOpen);
        return ask;
    }

    private static void RecordSfoRequest(AircraftState ac)
    {
        PilotPendingRequest request = PilotRequestTracker.RecordRequest(
            ac,
            PilotPendingRequestKind.AirspaceEntry,
            nowSeconds: 0,
            PilotResponder.BuildBravoClearanceRequest(ac, BravoClearanceWording.Through),
            PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, new LatLon(37.6213, -122.3790))
        );
        request.BravoWording = BravoClearanceWording.Through;
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
        for (int i = 0; (i < 1000) && (scenario.ElapsedSeconds < elapsedSeconds); i++)
        {
            engine.TickOneSecond();
        }

        Assert.True(scenario.ElapsedSeconds >= elapsedSeconds, $"the scenario clock stopped at {scenario.ElapsedSeconds}");
    }

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
