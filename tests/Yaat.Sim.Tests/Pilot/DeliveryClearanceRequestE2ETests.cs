using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A student working clearance delivery (OAK_DEL) gets the departure's first call as a clearance request, never "ready to
/// taxi", even with an AI ground answering at OAK (YAAT-308): IFR names the destination; VFR names a direction of flight
/// clear of nearby airports and the filed altitude. A beacon code (SQ) or a PDC sent by TDLSS answers the request.
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

    [Fact]
    public void AfterPush_DeliveryStudent_WordsTheLocationAsPushedBackFrom()
    {
        if (Load("N152SP", "IFR", "KLAX", 9000, [("PUSH", 0)]) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 200);

        // The request, and its follow-up when the push and set-up took long enough: never a taxi call.
        Assert.NotEmpty(PilotLines(run));
        Assert.All(PilotLines(run), e => Assert.Matches("^Oakland Clearance, pushed back from .*, IFR to Los Angeles Airport[.]$", e.Message));
        Assert.Equal(PilotPendingRequestKind.Clearance, run.Aircraft.PendingPilotRequest!.Kind);
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

    /// <summary>TDLSS with the nine PDC fields in canonical order.</summary>
    private const string PdcSendCommand = "TDLSS 10 MIN|OAKLAND4|ALTAM||CLIMB VIA SID|5000||120.9|";

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

        var engine = new SimulationEngine(groundData);
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
}
