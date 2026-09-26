using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Solo training: a VFR aircraft outside the SFO Class B orbits at the boundary until it is cleared in. A controller
/// instruction that applies now and can only be flown by entering the Bravo (a vector or a direct into it, a pattern
/// entry or an approach clearance at an airport inside it, or, while the pilot waits on the Bravo, a vector toward it or
/// any pattern entry or approach clearance) is read as the clearance: the pilot reads it back as one and the boundary
/// hold never re-arms. Approaching the boundary, a pilot in contact with the student asks for the clearance first.
/// </summary>
public sealed class SoloBravoImplicitClearanceTests
{
    private const string Callsign = "N436MS";
    private const string SpokenCallsign = "november four three six mike sierra";

    // West of the SFO Class B over the ocean, at 7,500 ft: inside the band of the 6,000 ft shelf whose western edge
    // runs along 122.853 W at this latitude. At about 138 kt true, Near (1.5 nm out) reaches the ring inside the 60 s
    // hold lookahead, Far (3 nm out) only inside the 120 s request lookahead, and Distant (8 nm out) only inside the
    // 300 s lookahead of a pilot already waiting on the Bravo.
    private static readonly LatLon NearWestRing = new(37.62, -122.885);
    private static readonly LatLon FarWestRing = new(37.62, -122.916);
    private static readonly LatLon DistantWestRing = new(37.62, -123.021);
    private const double CruiseAltitude = 7500;
    private const double CruiseIas = 120;

    public SoloBravoImplicitClearanceTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Orbit_ThenVectorIntoBravo_ClearsAndDoesNotReArm()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, destination: "KMOD");
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PhaseList holdList = ac.Phases!;
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(holdList.Phases));
        Assert.Equal(AirspaceClass.Bravo, hold.AirspaceClass);
        Assert.Equal(AirspaceHoldMode.Orbit, hold.Mode);
        PhaseContext ctx = Context(ac);
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        CommandResult result = engine.SendCommand(Callsign, "FH 090");
        Assert.True(result.Success, result.Message);
        PhaseRunner.Tick(ac, ctx);
        Assert.True(holdList.IsComplete, "the vector ends the hold");

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        Assert.Same(holdList, ac.Phases);
        Assert.True(ac.Phases!.IsComplete, "no fresh hold may re-arm and turn the aircraft away from the vector");
        Assert.Equal(90, ac.Targets.AssignedMagneticHeading?.Degrees ?? double.NaN, 0.5);
        Assert.True(ac.IsClearedIntoBravo);
        // The pilot asked to go through the Bravo, so it reads the clearance back the same way.
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("fly heading 090, cleared through the bravo", readback.Text);
    }

    [Fact]
    public void VectorIntoBravo_ReadbackSaysClearedIntoTheBravo()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "FH 090");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsClearedIntoBravo);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("fly heading 090, cleared into the bravo", readback.Text);
        Assert.EndsWith($", cleared into the bravo, {SpokenCallsign}.", readback.SpeechText);
    }

    [Fact]
    public void VectorAwayFromBravo_GrantsNothing()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsClearedIntoBravo);
        AssertNoBravoClause(ac);
    }

    [Fact]
    public void DirectToFixAcrossBravo_WithinWindow_Grants()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "DCT SFO");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsClearedIntoBravo);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.EndsWith(", cleared into the bravo", readback.Text);
    }

    /// <summary>
    /// The grant is decided when the instruction is issued. With a pilot reaction delay the pattern entry has not been
    /// applied yet, so the airport must come from the command as its handler resolves it, not from the phase list the
    /// immediate dispatch has already rewritten: both paths grant alike for SFO while the filed destination is Modesto.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PatternEntryAtBravoAirportNotTheDestination_GrantsOnImmediateAndDelayedPaths(bool reactionDelayed)
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KMOD");
        ac.Phases = new PhaseList { AssignedRunway = Yaat.Sim.Data.NavigationDatabase.Instance.GetRunway("SFO", "28R") };
        Assert.NotNull(ac.Phases.AssignedRunway);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        if (reactionDelayed)
        {
            scenario.CommandRunDelayMinSeconds = 5;
            scenario.CommandRunDelayMaxSeconds = 5;
        }

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal(reactionDelayed, ac.DeferredDispatches.Count > 0);
        Assert.True(ac.IsClearedIntoBravo);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.EndsWith(", cleared into the bravo", readback.Text);
    }

    [Fact]
    public void PatternEntryAtBravoAirport_Grants()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KSFO");
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "ERD 28R");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsClearedIntoBravo);
    }

    [Fact]
    public void ApproachClearanceAtBravoAirport_Grants()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KSFO");
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "CAPP ILS28R");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsClearedIntoBravo);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.EndsWith(", cleared into the bravo", readback.Text);
    }

    [Fact]
    public void Contact_DoesNotGrant()
    {
        ArtccConfigRoot zoa = LoadZoaOrSkip();
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, destination: "KMOD");
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        scenario.ArtccConfig = zoa;
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);

        CommandResult result = engine.SendCommand(Callsign, "CT OAK_TWR");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsClearedIntoBravo);
        // The tracker still counts a contact instruction as the answer to the request.
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, ac.PendingPilotRequest!.ResponseState);
    }

    [Fact]
    public void AfterContact_WaitsForCheckInBeforeReRequest()
    {
        ArtccConfigRoot zoa = LoadZoaOrSkip();
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "KMOD");
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        scenario.ArtccConfig = zoa;
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.True(engine.SendCommand(Callsign, "CT OAK_TWR").Success);
        Assert.False(ac.PendingPilotRequest!.IsOpen);
        ac.PendingPilotTransmissions.Clear();

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        // Sent to another frequency, the pilot has not checked in with the student again, so it does not call the
        // student for the clearance.
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.PendingPilotRequest!.IsOpen);
    }

    [Theory]
    [InlineData("KMOD", "request clearance through the bravo")]
    [InlineData("KSFO", "request clearance into the bravo")]
    public void ApproachingBravo_RequestsClearanceOnce_ThroughOrInto(string destination, string expected)
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: destination);
        SimScenarioState scenario = SoloScenario();
        AirspaceBoundaryCrossing? crossing = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, lookaheadSeconds: 120);
        Assert.Equal(AirspaceClass.Bravo, crossing?.Volume.Class);
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, lookaheadSeconds: 60));

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        PilotTransmission request = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Proactive, request.Kind);
        Assert.Equal(expected + ".", request.Text);
        Assert.Equal($"{SpokenCallsign}, {expected}.", request.SpeechText);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.True(ac.PendingPilotRequest!.IsOpen);
    }

    [Fact]
    public void NotYetInContact_DoesNotRequest()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "KMOD");
        ac.HasMadeInitialContact = false;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void OtherRequestOpen_DoesNotOverwrite()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "KMOD");
        var closedTraffic = new PilotSpeechText("request closed traffic.", $"{SpokenCallsign}, request closed traffic.");
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Landing, nowSeconds: 0, closedTraffic, PilotRequestContext.None);

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Equal(PilotPendingRequestKind.Landing, ac.PendingPilotRequest?.Kind);
        Assert.True(ac.PendingPilotRequest!.IsOpen);
    }

    [Fact]
    public void WaitingThenVectorAway_GrantsNothing()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, destination: "KMOD");
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsClearedIntoBravo);
        AssertNoBravoClause(ac);
    }

    [Fact]
    public void WaitingThenVectorIntoBravoBeyond120s_Grants()
    {
        AircraftState ac = Airborne(DistantWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, _) = SoloEngine(ac);
        PilotSpeechText through = PilotResponder.BuildBravoClearanceRequest(ac, BravoClearanceWording.Through);
        PilotRequestTracker.RecordRequest(
            ac,
            PilotPendingRequestKind.AirspaceEntry,
            nowSeconds: 0,
            through,
            PilotRequestContext.Airspace(AirspaceClass.Bravo, "SFO", new LatLon(37.6213, -122.3790))
        );
        var eastbound = new TrueHeading(100);
        Assert.Null(
            AirspaceDatabase.Default.FindLevelTrackClassBEntry(
                ac.Position,
                ac.Altitude,
                eastbound,
                ac.GroundSpeed,
                lookaheadSeconds: 120,
                identFilter: null
            )
        );

        CommandResult result = engine.SendCommand(Callsign, "FH 090");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsClearedIntoBravo);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("fly heading 090, cleared through the bravo", readback.Text);
    }

    [Fact]
    public void ConditionalHeading_DoesNotGrantAtIssue()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, _) = SoloEngine(ac);

        CommandResult result = engine.SendCommand(Callsign, "AT SFO FH 090");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsClearedIntoBravo);
        AssertNoBravoClause(ac);
    }

    [Fact]
    public void IfrOrOnGround_NotGranted()
    {
        AircraftState ifr = Airborne(NearWestRing, trueHeading: 270, destination: "KMOD");
        ifr.FlightPlan.FlightRules = "IFR";
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ifr);

        CommandResult result = engine.SendCommand(Callsign, "FH 090");

        Assert.True(result.Success, result.Message);
        Assert.False(ifr.IsClearedIntoBravo);
        AssertNoBravoClause(ifr);

        AircraftState onGround = Airborne(NearWestRing, trueHeading: 270, destination: "KMOD");
        onGround.IsOnGround = true;
        var vectorIn = new CompoundCommand([new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(90))])]);
        Assert.Null(ImplicitBravoClearance.TryGrant(onGround, vectorIn, wait: null, scenario, AirspaceDatabase.Default, LookupAirport));
        Assert.False(onGround.IsClearedIntoBravo);
    }

    [Fact]
    public void CharlieEntryFirst_DoesNotHideBravoRequest()
    {
        // Westbound at 2,500 ft east of Oakland: the track enters the OAK Class C first (122.123 W) and the SFO Class B
        // 2,100 ft shelf about 6 nm later (122.257 W), both inside 120 s at this speed.
        AircraftState ac = Airborne(new LatLon(37.75, -122.115), trueHeading: 270, destination: "KMOD");
        ac.Altitude = 2500;
        ac.IndicatedAirspeed = 220;
        AirspaceBoundaryCrossing? first = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, lookaheadSeconds: 120);
        Assert.Equal(AirspaceClass.Charlie, first?.Volume.Class);

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        PilotTransmission request = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("request clearance through the bravo.", request.Text);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.Equal("SFO", ac.PendingPilotRequest!.AirspaceIdent);
    }

    [Fact]
    public void RpoMode_NoGrantNoClauseNoRequest()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, destination: "KMOD");
        SimScenarioState scenario = SoloScenario();
        scenario.SoloTrainingMode = false;
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = scenario };
        engine.World.AddAircraft(ac);

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        CommandResult result = engine.SendCommand(Callsign, "FH 090");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsClearedIntoBravo);
        Assert.Null(ac.PendingPilotRequest);
    }

    private static void AssertNoBravoClause(AircraftState ac) =>
        Assert.DoesNotContain(ac.PendingPilotTransmissions, t => t.Text.Contains("bravo", StringComparison.OrdinalIgnoreCase));

    private static ArtccConfigRoot LoadZoaOrSkip()
    {
        // CT resolves its target against the ARTCC's positions.
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            Assert.Skip("ZOA ARTCC config not available");
        }

        return zoa;
    }

    private static (SimulationEngine Engine, SimScenarioState Scenario) SoloEngine(AircraftState ac)
    {
        SimScenarioState scenario = SoloScenario();
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = scenario };
        engine.World.AddAircraft(ac);
        return (engine, scenario);
    }

    private static AircraftState Airborne(LatLon position, double trueHeading, string destination)
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
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
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
