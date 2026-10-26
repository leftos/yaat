using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A student working clearance delivery (OAK_DEL) gets the departure's first call as a clearance request, never "ready to
/// taxi", even with an AI ground answering at OAK (YAAT-308): IFR names the destination; VFR names a direction of flight
/// clear of nearby airports and the filed altitude. A beacon code (SQ) or a PDC sent by TDLSS answers the request. A
/// student answering ground with delivery out of its worked range gets the same first call, to itself (YAAT-1221).
/// </summary>
[Collection("NavDbMutator")]
public class DeliveryClearanceRequestE2ETests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public DeliveryClearanceRequestE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Ifr_StandSpawn_AsksDeliveryForItsClearance_AndSquawkAnswersIt()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, []) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);

        TerminalEntry request = Assert.Single(PilotLines(run));
        Assert.StartsWith($"{run.DeliveryRadioName}, at ", request.Message, StringComparison.Ordinal);
        Assert.EndsWith(", with information Alpha, IFR to Los Angeles Airport.", request.Message, StringComparison.Ordinal);
        Assert.Equal(PilotPendingRequestKind.Clearance, run.Aircraft.PendingPilotRequest!.Kind);
        Assert.True(run.Aircraft.HasMadeInitialContact);

        CommandResult squawk = run.Engine.SendCommand("N152SP", "SQ 4321");
        Assert.True(squawk.Success, squawk.Message);
        Assert.False(run.Aircraft.PendingPilotRequest!.IsOpen);

        Tick(run.Engine, 300);
        // The squawk readback, and no taxi call and no follow-up after the answer.
        Assert.DoesNotContain(PilotLines(run), e => e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase));
        Assert.Single(PilotLines(run), e => e.Message.Contains("IFR to", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SQ 4321")]
    [InlineData("RANDSQ")]
    [InlineData("SQVFR")]
    public void Ifr_ClearanceRequest_IsAnsweredByEveryBeaconCodeOrPdcCommand(string command)
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, []) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");

        CommandResult answer = run.Engine.SendCommand("N152SP", command);

        Assert.True(answer.Success, answer.Message);
        Assert.False(run.Aircraft.PendingPilotRequest!.IsOpen);
    }

    [Fact]
    public void Ifr_ClearanceRequest_IsAnsweredByAPdcSentByTdlss_AndDoesNotFollowUp()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, []) is not { } run)
        {
            return;
        }

        run.Engine.InitializeFromArtcc();
        Tick(run.Engine, 30);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");
        Assert.Equal("N152SP", run.Engine.World.ActiveFrequency.AwaitingControllerResponseTo);

        SendPdc(run);

        Assert.Equal(PilotPendingRequestResponseState.Satisfied, run.Aircraft.PendingPilotRequest!.ResponseState);
        Assert.NotNull(run.Aircraft.Ground.ClearanceObtainedAtSeconds);
        Assert.Null(run.Engine.World.ActiveFrequency.AwaitingControllerResponseTo);
        Tick(run.Engine, (int)PilotRequestTracker.NormalFollowUpDelaySeconds + 30);
        Assert.Single(PilotLines(run), e => e.Message.Contains("IFR to", StringComparison.Ordinal));
    }

    [Fact]
    public void Ifr_ClearanceRequest_StaysOpen_WhenTdlssFails()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, []) is not { } run)
        {
            return;
        }

        // No InitializeFromArtcc: no TDLS facility config is loaded, so there is no PDC to queue or send.
        Tick(run.Engine, 30);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");

        CommandResult sent = run.Engine.SendCommand("N152SP", PdcSendCommand);

        Assert.False(sent.Success, "TDLSS sent a PDC with no TDLS facility loaded");
        Assert.True(
            run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance },
            "a failed TDLSS closed the request"
        );
        Assert.Equal(PilotPendingRequestResponseState.None, run.Aircraft.PendingPilotRequest!.ResponseState);
    }

    [Fact]
    public void Ifr_ClearanceRequest_IsNotAnsweredByATimedPresetTdlss()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, [(PdcSendCommand, 60)]) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");

        Tick(run.Engine, 40);

        Assert.Empty(run.Engine.Scenario!.PresetQueue);
        Assert.True(
            run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance },
            "a preset TDLSS closed the request"
        );
    }

    [Fact]
    public void Tdlss_LeavesAnOpenTaxiRequestOpen()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, []) is not { } run)
        {
            return;
        }

        run.Engine.InitializeFromArtcc();
        PilotRequestTracker.RecordRequest(
            run.Aircraft,
            PilotPendingRequestKind.Taxi,
            nowSeconds: 0,
            new PilotSpeechText("ready to taxi", "ready to taxi"),
            PilotRequestContext.None
        );

        SendPdc(run);

        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi }, "TDLSS closed a taxi request");
        Assert.Equal(PilotPendingRequestResponseState.None, run.Aircraft.PendingPilotRequest!.ResponseState);
    }

    [Fact]
    public void DeliveryStudentAtAnotherAirport_GetsNoClearanceRequest()
    {
        if (LoadWithStudent("N152SP", "IFR", "KLAX", 9000, [], "SFO_DEL") is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);

        Assert.False(run.Aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Clearance }, "a clearance request to SFO delivery");
        Assert.DoesNotContain(run.Terminal, e => e.Message.EndsWith("IFR to Los Angeles Airport.", StringComparison.Ordinal));
    }

    [Fact]
    public void GroundStudent_LowestDelivery_GetsTheClearanceRequest_ThenTheTaxiCall()
    {
        if (LoadWithStudent("N152SP", "IFR", "KLAX", 9000, [], "OAK_GND") is not { } run)
        {
            return;
        }

        run.Engine.Scenario!.StudentLowestWorkedLevel = AtcPositionLevel.Delivery;
        Tick(run.Engine, 30);

        TerminalEntry request = Assert.Single(PilotLines(run));
        Assert.StartsWith($"{run.DeliveryRadioName}, at ", request.Message, StringComparison.Ordinal);
        Assert.EndsWith(", with information Alpha, IFR to Los Angeles Airport.", request.Message, StringComparison.Ordinal);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");

        // No taxi call while the clearance is still owed.
        Tick(run.Engine, 30);
        Assert.DoesNotContain(PilotLines(run), e => e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PilotPendingRequestKind.Clearance, run.Aircraft.PendingPilotRequest!.Kind);

        CommandResult squawk = run.Engine.SendCommand("N152SP", "SQ 4321");
        Assert.True(squawk.Success, squawk.Message);
        double clearedAt = run.Engine.Scenario!.ElapsedSeconds;
        Assert.Equal(clearedAt, run.Aircraft.Ground.ClearanceObtainedAtSeconds);

        // The crew sets up its clearance before it calls for taxi.
        while (run.Engine.Scenario.ElapsedSeconds < clearedAt + InitialCallupCall.TaxiCallAfterClearanceSeconds - 1)
        {
            run.Engine.TickOneSecond();
            Assert.DoesNotContain(PilotLines(run), e => e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase));
        }

        Tick(run.Engine, 60);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi }, "no ground call after the clearance");
        Assert.Equal(run.DeliveryRadioName, run.Aircraft.PendingPilotRequest!.FacilityCallName);
        TerminalEntry taxiCall = Assert.Single(PilotLines(run), e => e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("IFR to", taxiCall.Message, StringComparison.Ordinal);
        Assert.True(run.Aircraft.Ground.InitialCallupDecisionProcessed);
    }

    [Fact]
    public void GroundStudent_LowestGround_IfrAsksGroundForItsClearanceFirst()
    {
        if (LoadWithStudent("N152SP", "IFR", "KLAX", 9000, [], "OAK_GND") is not { } run)
        {
            return;
        }

        run.Engine.Scenario!.StudentLowestWorkedLevel = AtcPositionLevel.Ground;
        Tick(run.Engine, 30);

        TerminalEntry call = Assert.Single(PilotLines(run));
        Assert.StartsWith($"{run.DeliveryRadioName}, at ", call.Message, StringComparison.Ordinal);
        Assert.EndsWith(", with information Alpha, IFR to Los Angeles Airport.", call.Message, StringComparison.Ordinal);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");
        Assert.Null(run.Aircraft.Ground.ClearanceObtainedAtSeconds);

        CommandResult squawk = run.Engine.SendCommand("N152SP", "SQ 4321");
        Assert.True(squawk.Success, squawk.Message);
        Tick(run.Engine, (int)InitialCallupCall.TaxiCallAfterClearanceSeconds + 30);

        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi }, "no ground call after the clearance");
        Assert.Equal(run.DeliveryRadioName, run.Aircraft.PendingPilotRequest!.FacilityCallName);
        Assert.Single(PilotLines(run), e => e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A preset push already put the aircraft past its clearance, so a ground student covering delivery gets the
    /// crew's ordinary ready-to-taxi call, never a clearance request (YAAT-1221).</summary>
    [Fact]
    public void GroundStudent_LowestGround_AfterPresetPush_IfrMakesItsGroundCall_NotAClearanceRequest()
    {
        if (LoadWithStudent("N152SP", "IFR", "KLAX", 9000, [("PUSH", 0)], "OAK_GND") is not { } run)
        {
            return;
        }

        run.Engine.Scenario!.StudentLowestWorkedLevel = AtcPositionLevel.Ground;

        Assert.True(
            TickUntil(run.Engine, () => run.Aircraft.PendingPilotRequest is { IsOpen: true }, 400) >= 0,
            "the aircraft should call after the push"
        );
        Assert.True(
            run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            "the ground call to the student, not a clearance request"
        );
        Assert.Equal(run.DeliveryRadioName, run.Aircraft.PendingPilotRequest!.FacilityCallName);
        Assert.DoesNotContain(PilotLines(run), e => e.Message.EndsWith("IFR to Los Angeles Airport.", StringComparison.Ordinal));
    }

    /// <summary>Top-down coverage (YAAT-1221): an IFR jet on a push-back stand with no clearance asks the lowest position the
    /// student works for its clearance first, and asks for pushback only once the clearance is obtained.</summary>
    [Theory]
    [InlineData(AtcPositionLevel.Delivery)]
    [InlineData(AtcPositionLevel.Ground)]
    public void GroundStudent_PushBackStandIfrJet_AsksForItsClearanceBeforePushback(AtcPositionLevel lowest)
    {
        if (LoadGate31Jet(lowest, "OAK_GND") is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);

        TerminalEntry first = Assert.Single(PilotLines(run));
        Assert.Equal("Oakland Ground, at gate 31, with information Alpha, IFR to Los Angeles Airport.", first.Message);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");

        CommandResult squawk = run.Engine.SendCommand("SWA1391", "SQ 4321");
        Assert.True(squawk.Success, squawk.Message);
        Tick(run.Engine, (int)InitialCallupCall.TaxiCallAfterClearanceSeconds + 30);

        Assert.True(
            run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback },
            "no pushback request after the clearance"
        );
        Assert.Equal("Oakland Ground, at gate 31, with information Alpha, request pushback.", run.Aircraft.PendingPilotRequest!.LastPilotLine);
    }

    /// <summary>The live report's sequence (YAAT-1221): lowest = GND at load, DEL set after the stand call. The uncleared IFR
    /// departure never asks for pushback, first call or follow-up.</summary>
    [Fact]
    public void GroundStudent_DeliverySetMidSession_UnclearedIfrJetNeverAsksForPushback()
    {
        if (LoadGate31Jet(AtcPositionLevel.Ground, "OAK_GND") is not { } run)
        {
            return;
        }

        for (int second = 1; second <= 300; second++)
        {
            if (second == 60)
            {
                run.Engine.Scenario!.StudentLowestWorkedLevel = AtcPositionLevel.Delivery;
            }

            run.Engine.TickOneSecond();
            Assert.DoesNotContain(PilotLines(run), e => e.Message.EndsWith("request pushback.", StringComparison.Ordinal));
        }

        Assert.Null(run.Aircraft.Ground.ClearanceObtainedAtSeconds);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");
    }

    /// <summary>S1-OAK-2/3/4's SWA1391 (B737, IFR to KLAX, gate 31, a push-back stand) under a solo student on
    /// <paramref name="studentCallsign"/> working down to <paramref name="lowest"/> (null when no level is set, so the student
    /// answers every call type), with no AI position staffed, as in a live solo room.</summary>
    private Run? LoadGate31Jet(AtcPositionLevel? lowest, string studentCallsign)
    {
        var groundData = new TestAirportGroundData();
        if ((_zoa is null) || (groundData.GetLayout("OAK") is null))
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => terminal.Add(entry);
        List<string> warnings = engine.LoadScenario(Gate31JetScenario, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = true;
        PositionConfig ground = _zoa.FindPositionByCallsign(studentCallsign, facilityHint: null)!;
        scenario.StudentPosition = _zoa.ResolvePosition(ground.Id);
        scenario.StudentPositionType = "GND";
        scenario.StudentLowestWorkedLevel = lowest;
        AircraftState aircraft = engine.FindAircraft("SWA1391") ?? throw new InvalidOperationException("SWA1391 did not spawn");
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(aircraft, aircraft.Ground.Layout));
        return new Run(engine, aircraft, terminal, ground.RadioName.Trim());
    }

    [Fact]
    public void GroundStudent_LowestDelivery_AiDelivery_GetsNoClearanceRequest()
    {
        if (LoadWithStudent("N152SP", "IFR", "KLAX", 9000, [], "OAK_GND") is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        scenario.StudentLowestWorkedLevel = AtcPositionLevel.Delivery;
        // ZOA's catalog leaves delivery out unless overridden (AiPositionResolver), so the AI delivery is built by hand.
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa!), TestAiPositions.TowerCab(_zoa!, "OAK_DEL", ControlRole.Ground, "OAK")]);
        Tick(run.Engine, 30);

        Assert.False(
            run.Aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Clearance },
            "a clearance request with an AI delivery staffed"
        );
        Assert.DoesNotContain(run.Terminal, e => e.Message.EndsWith("IFR to Los Angeles Airport.", StringComparison.Ordinal));
    }

    [Fact]
    public void ClearanceObtained_RoundTripsInTheSnapshot()
    {
        var ops = new AircraftGroundOps { ClearanceObtainedAtSeconds = 125, TakeoffRollStartedAtSeconds = 300 };

        var restored = AircraftGroundOps.FromSnapshot(ops.ToSnapshot(), null);
        Assert.Equal(125, restored.ClearanceObtainedAtSeconds);
        Assert.Equal(300, restored.TakeoffRollStartedAtSeconds);
        var empty = AircraftGroundOps.FromSnapshot(new AircraftGroundOps().ToSnapshot(), null);
        Assert.Null(empty.ClearanceObtainedAtSeconds);
        Assert.Null(empty.TakeoffRollStartedAtSeconds);
    }

    [Fact]
    public void Ifr_UnansweredClearanceRequest_FollowsUp()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, []) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30 + (int)PilotRequestTracker.NormalFollowUpDelaySeconds);

        Assert.Equal(2, PilotLines(run).Count(e => e.Message.Contains("IFR to Los Angeles", StringComparison.Ordinal)));
        Assert.DoesNotContain(PilotLines(run), e => e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Vfr_StandSpawn_AsksForAVfrDeparture_WithADirectionAndItsAltitude()
    {
        if (Load("N152SP", "VFR", "KSAC", 4500, []) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);

        TerminalEntry request = Assert.Single(PilotLines(run));
        string direction = run.Aircraft.Ground.VfrDepartureDirection!;
        Assert.EndsWith(
            $", with information Alpha, VFR departure to the {direction}, at {PhraseologyVerbalizer.CompactAltitude(4500)}.",
            request.Message,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("Sacramento", request.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PilotPendingRequestKind.Clearance, run.Aircraft.PendingPilotRequest!.Kind);

        Assert.True(run.Engine.SendCommand("N152SP", "SQ 4322").Success);
        Assert.False(run.Aircraft.PendingPilotRequest!.IsOpen);
    }

    [Fact]
    public void Vfr_Direction_IsTheSameAcrossTwoRuns()
    {
        if ((Load("N152SP", "VFR", "KSAC", 4500, []) is not { } first) || (Load("N152SP", "VFR", "KSAC", 4500, []) is not { } second))
        {
            return;
        }

        Tick(first.Engine, 30);
        Tick(second.Engine, 30);

        Assert.NotNull(first.Aircraft.Ground.VfrDepartureDirection);
        Assert.Equal(first.Aircraft.Ground.VfrDepartureDirection, second.Aircraft.Ground.VfrDepartureDirection);
        Assert.Equal(PilotLines(first).Single().Message, PilotLines(second).Single().Message);
    }

    [Fact]
    public void Vfr_Direction_NeverPointsAtAnAirportWithinTenMiles_WhenAClearSectorExists()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double lat, double lon) = navDb.GetAirportPosition("OAK")!.Value;
        var oak = new LatLon(lat, lon);
        var neighbours = navDb
            .FindAirportsWithin(oak, VfrDepartureDirection.ClearRadiusNm)
            .Where(n => !NavigationDatabase.AirportIdsMatch(n.Id, "OAK"))
            .ToList();
        Assert.NotEmpty(neighbours);
        var occupied = neighbours.Select(n => Cardinal(GeoMath.BearingTo(oak, n.Position))).ToHashSet();
        Assert.True(occupied.Count < 4, "every OAK sector has a neighbour; the clear-sector rule is untested here");

        foreach (string callsign in new[] { "N152SP", "N738SP", "N43778", "N52417", "N123AB", "N9911X", "N4455Q", "N7077Z" })
        {
            string? chosen = VfrDepartureDirection.Choose(callsign, "OAK");
            Assert.NotNull(chosen);
            Assert.DoesNotContain(chosen, occupied);
        }
    }

    /// <summary>A preset push already put the aircraft past its clearance, so the delivery student gets no clearance
    /// request: the crew makes its ordinary ground call instead.</summary>
    [Fact]
    public void AfterPresetPush_DeliveryStudent_GetsNoClearanceRequest()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, [("PUSH", 0)]) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 200);

        // The push and its ground call to the AI ground, over the run the old clearance-request test covered.
        Assert.NotEmpty(PilotLines(run));
        Assert.False(
            run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance },
            "a clearance request after a preset push"
        );
        Assert.DoesNotContain(PilotLines(run), e => e.Message.EndsWith("IFR to Los Angeles Airport.", StringComparison.Ordinal));
    }

    /// <summary>An IFR arrival the scenario places on taxiway K calls for taxi to parking, never for an IFR clearance: the
    /// clearance-first branch is for a departure from the airport the aircraft is on (YAAT-1221).</summary>
    [Fact]
    public void Ifr_ArrivalOnATaxiway_AsksForTaxiToParking_NotForItsClearance()
    {
        if (LoadArrivalOnTaxiway() is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);

        TerminalEntry call = Assert.Single(PilotLines(run));
        Assert.Equal("Oakland Ground, on taxiway K, with information Alpha, request taxi to parking.", call.Message);
        Assert.DoesNotContain("IFR to", call.Message, StringComparison.Ordinal);
        Assert.Equal(PilotPendingRequestKind.Taxi, run.Aircraft.PendingPilotRequest!.Kind);
    }

    /// <summary>With someone else covering delivery at the airport (an AI <c>…_DEL</c>), the pilot knows delivery is online
    /// and the lowest position the student works — ground — gets the pushback request first (YAAT-1221).</summary>
    [Fact]
    public void GroundStudent_AiDeliveryStaffed_GetsThePushbackRequestFirst()
    {
        if (LoadGate31Jet(AtcPositionLevel.Ground, "OAK_GND") is not { } run)
        {
            return;
        }

        // ZOA's catalog leaves delivery out unless overridden (AiPositionResolver), so the AI delivery is built by hand. Its
        // role keeps it off the ground call: a …_DEL entry is recognized by callsign, wherever the AI's role puts it.
        run.Engine.Scenario!.SetAiStaffedPositions([TestAiPositions.TowerCab(_zoa!, "OAK_DEL", ControlRole.Approach, "OAK")]);

        Tick(run.Engine, 30);

        TerminalEntry first = Assert.Single(PilotLines(run));
        Assert.Equal("Oakland Ground, at gate 31, with information Alpha, request pushback.", first.Message);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback }, "no pushback request");
        Assert.Null(run.Aircraft.Ground.ClearanceObtainedAtSeconds);
    }

    /// <summary>A student with no lowest level set answers every call type (YAAT-1221): an uncleared IFR jet on a push-back
    /// stand at its own airport asks for its clearance first, then for pushback once it is answered.</summary>
    [Fact]
    public void DeliveryStudent_NoLowestLevel_GetsTheClearanceRequestThenThePushbackRequest()
    {
        if (LoadGate31Jet(null, "OAK_DEL") is not { } run)
        {
            return;
        }

        Tick(run.Engine, 30);

        TerminalEntry first = Assert.Single(PilotLines(run));
        Assert.Equal("Oakland Clearance, at gate 31, with information Alpha, IFR to Los Angeles Airport.", first.Message);
        Assert.True(run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance }, "no open clearance request");

        CommandResult squawk = run.Engine.SendCommand("SWA1391", "SQ 4321");
        Assert.True(squawk.Success, squawk.Message);
        Tick(run.Engine, (int)InitialCallupCall.TaxiCallAfterClearanceSeconds + 30);

        Assert.True(
            run.Aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback },
            "no pushback request after the clearance"
        );
        Assert.Equal(run.DeliveryRadioName, run.Aircraft.PendingPilotRequest!.FacilityCallName);
        Assert.EndsWith(", request pushback.", run.Aircraft.PendingPilotRequest!.LastPilotLine, StringComparison.Ordinal);
    }

    /// <summary>An IFR arrival (KLAX→KOAK) on taxiway K at OAK under a solo OAK_GND student working down to ground, with no
    /// AI position staffed.</summary>
    private Run? LoadArrivalOnTaxiway()
    {
        var groundData = new TestAirportGroundData();
        if ((_zoa is null) || (groundData.GetLayout("OAK") is null))
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => terminal.Add(entry);
        List<string> warnings = engine.LoadScenario(TaxiwayArrivalScenario, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = true;
        PositionConfig ground = _zoa.FindPositionByCallsign("OAK_GND", facilityHint: null)!;
        scenario.StudentPosition = _zoa.ResolvePosition(ground.Id);
        scenario.StudentPositionType = "GND";
        scenario.StudentLowestWorkedLevel = AtcPositionLevel.Ground;
        AircraftState aircraft =
            engine.FindAircraft(TaxiwayArrivalCallsign) ?? throw new InvalidOperationException($"{TaxiwayArrivalCallsign} did not spawn");
        return new Run(engine, aircraft, terminal, ground.RadioName.Trim());
    }

    private static string Cardinal(double bearing)
    {
        double b = ((bearing % 360.0) + 360.0) % 360.0;
        return ((int)((b + 45.0) % 360.0 / 90.0)) switch
        {
            0 => "north",
            1 => "east",
            2 => "south",
            _ => "west",
        };
    }

    private sealed record Run(SimulationEngine Engine, AircraftState Aircraft, List<TerminalEntry> Terminal, string DeliveryRadioName);

    private const string TaxiwayArrivalCallsign = "N52417";

    /// <summary>TDLSS with the nine PDC fields in canonical order.</summary>
    private const string PdcSendCommand = "TDLSS 10 MIN|OAKLAND4|ALTAM||CLIMB VIA SID|5000||120.9|";

    private const string Gate31JetScenario = """
        {
          "id": "delivery-gate31-jet",
          "name": "Clearance before pushback",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "SWA1391",
              "aircraftType": "B737/L",
              "transponderMode": "Off",
              "startingConditions": { "type": "Parking", "parking": "31" },
              "flightplan": {
                "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000,
                "cruiseSpeed": 250, "route": "CNDEL5 SUSEY EBAYE BURGL IRNMN2", "remarks": "/V/",
                "aircraftType": "B737/L"
              },
              "presetCommands": []
            }
          ]
        }
        """;

    /// <summary>An IFR arrival the scenario places on taxiway K at OAK (the coordinate spawn the ground call-up tests use).</summary>
    private const string TaxiwayArrivalScenario = """
        {
          "id": "delivery-taxiway-arrival",
          "name": "Arrival on taxiway K",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N52417",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.73212069, "lon": -122.22503752 } },
              "flightplan": {
                "rules": "IFR", "departure": "KLAX", "destination": "KOAK", "cruiseAltitude": 9000,
                "cruiseSpeed": 120, "route": "", "remarks": "", "aircraftType": "C172"
              },
              "airportId": "OAK"
            }
          ]
        }
        """;

    /// <summary>Queues and sends the PDC by data link (TDLSQ, then <see cref="PdcSendCommand"/>).</summary>
    private static void SendPdc(Run run)
    {
        CommandResult queued = run.Engine.SendCommand(run.Aircraft.Callsign, "TDLSQ");
        Assert.True(queued.Success, queued.Message);
        CommandResult sent = run.Engine.SendCommand(run.Aircraft.Callsign, PdcSendCommand);
        Assert.True(sent.Success, sent.Message);
    }

    private static List<TerminalEntry> PilotLines(Run run) =>
        [.. run.Terminal.Where(e => (e.Kind == "SayPilot") && string.Equals(e.Callsign, run.Aircraft.Callsign, StringComparison.OrdinalIgnoreCase))];

    private Run? Load(string callsign, string rules, string destination, int cruise, (string Command, int Offset)[] presets) =>
        LoadWithStudent(callsign, rules, destination, cruise, presets, "OAK_DEL");

    private Run? LoadWithStudent(
        string callsign,
        string rules,
        string destination,
        int cruise,
        (string Command, int Offset)[] presets,
        string deliveryCallsign
    )
    {
        var groundData = new TestAirportGroundData();
        if ((_zoa is null) || (groundData.GetLayout("OAK") is null))
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => terminal.Add(entry);
        List<string> warnings = engine.LoadScenario(
            ScenarioJson(callsign, rules, destination, cruise, presets),
            42,
            MagneticDeclination.EvaluationDateUtc
        );
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa)]);
        scenario.SoloTrainingMode = true;
        PositionConfig delivery = _zoa.FindPositionByCallsign(deliveryCallsign, facilityHint: null)!;
        scenario.StudentPosition = _zoa.ResolvePosition(delivery.Id);
        scenario.StudentPositionType = "GND";
        AircraftState aircraft = engine.FindAircraft(callsign) ?? throw new InvalidOperationException($"{callsign} did not spawn");
        return new Run(engine, aircraft, terminal, delivery.RadioName.Trim());
    }

    private static string ScenarioJson(string callsign, string rules, string destination, int cruise, (string Command, int Offset)[] presets)
    {
        string presetJson = string.Join(
            ", ",
            presets.Select((p, i) => $$"""{ "id": "p{{i}}", "command": "{{p.Command}}", "timeOffset": {{p.Offset}} }""")
        );
        return $$"""
            {
              "id": "delivery-{{callsign}}",
              "name": "Clearance delivery request",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "{{callsign}}",
                  "aircraftType": "C172",
                  "transponderMode": "C",
                  "startingConditions": { "type": "Parking", "parking": "SIG1" },
                  "flightplan": { "rules": "{{rules}}", "departure": "KOAK", "destination": "{{destination}}", "cruiseAltitude": {{cruise}}, "cruiseSpeed": 110, "route": "", "remarks": "", "aircraftType": "C172" },
                  "presetCommands": [ {{presetJson}} ]
                }
              ]
            }
            """;
    }

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    /// <summary>Ticks one second at a time until <paramref name="done"/> holds; the ticks it took, or -1 when it never held.</summary>
    private static int TickUntil(SimulationEngine engine, Func<bool> done, int maxSeconds)
    {
        for (int i = 1; i <= maxSeconds; i++)
        {
            engine.TickOneSecond();
            if (done())
            {
                return i;
            }
        }

        return -1;
    }
}
