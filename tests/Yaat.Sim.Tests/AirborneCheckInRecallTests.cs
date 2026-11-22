using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1513: the solo VFR airborne check-in states the type, position, altitude and request. To a tower a transit asks
/// for a transition (AIM 3-2-5.b.3); to approach or center it asks for a Bravo transition when the track enters a Class B
/// the pilot is not cleared into and is not bound for (AIM 3-2-3.d.2), else for flight following. A check-in that enters a
/// Class B is recorded as that Class B's request, so the pilot never asks a second time; any other is an initial-contact
/// request: unanswered, the pilot re-calls once, "how do you hear me?" (AIM 4-2-3.a.3), 45 s on.
/// </summary>
public sealed class AirborneCheckInRecallTests
{
    private const string Callsign = "N436MS";
    private const string SpokenCallsign = "november four three six mike sierra";

    // West of the SFO Class B over the ocean at 7,500 ft (the SoloBravoImplicitClearanceTests fixture): heading east at
    // about 138 kt true the track reaches the 6,000 ft shelf inside the 120 s request lookahead, not the 60 s hold one;
    // from DistantWestRing only inside the 300 s check-in lookahead.
    private static readonly LatLon FarWestRing = new(37.62, -122.916);
    private static readonly LatLon DistantWestRing = new(37.62, -123.021);
    private const double CruiseAltitude = 7500;
    private const double CruiseIas = 120;

    private static readonly LatLon SfoPos = new(37.6213, -122.3790);
    private static readonly LatLon OakPos = new(37.7213, -122.2208);

    public AirborneCheckInRecallTests() => TestVnasData.EnsureInitialized();

    private static string Spoken(string airport) => PhraseologyVerbalizer.SpellAirportName(airport);

    /// <summary>
    /// The case table: each student position, for a transit with a destination, one without, a Class B ahead, a
    /// destination inside the Class B ahead, and a flight landing at the primary airport. "{dest}" is the destination
    /// spoken.
    /// </summary>
    [Theory]
    [InlineData("TWR", 270, "KMOD", "SFO", "request transition westbound to {dest}.")]
    [InlineData("TWR", 270, "", "SFO", "request transition westbound.")]
    [InlineData("TWR", 90, "KMOD", "SFO", "request transition eastbound to {dest}.")]
    [InlineData("TWR", 90, "KSFO", "OAK", "request transition eastbound to {dest}.")]
    [InlineData("TWR", 270, "KSFO", "SFO", "inbound for landing, with information Alpha.")]
    [InlineData("APP", 270, "KMOD", "SFO", "request flight following to {dest}.")]
    [InlineData("APP", 270, "", "SFO", "request flight following, VFR westbound.")]
    [InlineData("APP", 90, "KMOD", "SFO", "request Bravo transition eastbound to {dest}.")]
    [InlineData("APP", 90, "KSFO", "OAK", "request flight following to {dest}.")]
    [InlineData("APP", 270, "KSFO", "SFO", "request landing, with information Alpha.")]
    [InlineData("CTR", 270, "KMOD", "SFO", "request flight following to {dest}.")]
    [InlineData("CTR", 270, "", "SFO", "request flight following, VFR westbound.")]
    [InlineData("CTR", 90, "KMOD", "SFO", "request Bravo transition eastbound to {dest}.")]
    [InlineData("CTR", 90, "KSFO", "OAK", "request flight following to {dest}.")]
    [InlineData("CTR", 270, "KSFO", "SFO", "request flight following to {dest}.")]
    public void Vfr_CaseTable_StatesTheRequest(string position, double heading, string destination, string primary, string request)
    {
        AircraftState ac = Airborne(FarWestRing, heading, destination);
        SimScenarioState scenario = SoloScenario(position, primary);

        PilotSpeechText line = CheckIn(ac, scenario);

        Assert.EndsWith(", " + request.Replace("{dest}", Spoken(destination), StringComparison.Ordinal), line.Tts);
        Assert.Contains(", skylane", line.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void Vfr_ApproachTransit_AnchorsThePositionOnThePrimaryAirport()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "KMOD");

        PilotSpeechText line = CheckIn(ac, SoloScenario("APP", "SFO"));

        Assert.StartsWith($"approach, {SpokenCallsign}, skylane, ", line.Tts);
        Assert.Contains(
            $" miles west of {Spoken("SFO")} at seven thousand five hundred, request Bravo transition",
            line.Tts,
            StringComparison.Ordinal
        );
        Assert.StartsWith($"approach, {Callsign}, C182, ", line.Terminal);
        Assert.EndsWith($" at 7500, request Bravo transition eastbound to {Spoken("KMOD")}.", line.Terminal);
    }

    [Fact]
    public void Vfr_BravoAhead_NoDestination_KeepsTheDirection()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "");

        PilotSpeechText line = CheckIn(ac, SoloScenario("APP", "SFO"));

        Assert.EndsWith(" at seven thousand five hundred, request Bravo transition eastbound.", line.Tts);
    }

    [Fact]
    public void Vfr_BravoOnlyInsideTheCheckInLookahead_StillRequestsTheBravoTransition()
    {
        AircraftState ac = Airborne(DistantWestRing, trueHeading: 90, destination: "KMOD");
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));

        PilotSpeechText line = CheckIn(ac, SoloScenario("APP", "SFO"));

        Assert.EndsWith($", request Bravo transition eastbound to {Spoken("KMOD")}.", line.Tts);
    }

    [Fact]
    public void Vfr_ClearedIntoTheBravoAhead_RequestsFlightFollowing()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "KMOD");
        ac.GrantBravoClearance("SFO");

        PilotSpeechText line = CheckIn(ac, SoloScenario("APP", "SFO"));

        Assert.EndsWith($" at seven thousand five hundred, request flight following to {Spoken("KMOD")}.", line.Tts);
    }

    [Fact]
    public void Vfr_TypeWithNoSpokenName_SpellsTheDesignator()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "");
        ac.AircraftType = "XYZ1";

        PilotSpeechText line = CheckIn(ac, SoloScenario("APP", "SFO"));

        Assert.StartsWith($"approach, {SpokenCallsign}, xray yankee zulu one, ", line.Tts);
        Assert.StartsWith($"approach, {Callsign}, XYZ1, ", line.Terminal);
    }

    [Theory]
    [InlineData("APP", "KSFO", "OAK")]
    [InlineData("TWR", "KMOD", "SFO")]
    public void Vfr_CheckInWithTheBravoAhead_RecordsTheClassBRequest_WhateverItSays(string position, string destination, string primary)
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination);
        SimScenarioState scenario = SoloScenario(position, primary);

        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, request.Kind);
        Assert.Equal("SFO", request.AirspaceIdent);
        Assert.DoesNotContain("Bravo", request.LastPilotLineTts, StringComparison.Ordinal);
    }

    [Fact]
    public void BravoCheckIn_RecordsTheClassBRequest_AndTheBoundaryTickAsksNothingMore()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "KMOD");
        SimScenarioState scenario = SoloScenario("APP", "SFO");
        PilotSpeechText expected = CheckIn(ac, scenario);

        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, request.Kind);
        Assert.Equal(nameof(AirspaceClass.Bravo), request.AirspaceClass);
        Assert.Equal("SFO", request.AirspaceIdent);
        Assert.Equal(ImplicitBravoClearance.WordingFor(ac, "SFO", AirspaceDatabase.Default, LookupAirport), request.BravoWording);
        Assert.Equal(expected.Terminal, request.LastPilotLine);
        Assert.Equal(expected.Tts, request.LastPilotLineTts);

        // The check-in has gone out; the Class B ahead is the one the check-in asked for.
        ac.PendingPilotTransmissions.Clear();
        for (int second = 1; second <= 5; second++)
        {
            scenario.ElapsedSeconds = second;
            PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);
        }

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Same(request, ac.PendingPilotRequest);
    }

    [Fact]
    public void OpenInitialContact_DoesNotHoldTheClassBRequestBack_WhichReplacesIt()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 90, destination: "KMOD");
        ac.HasMadeInitialContact = true;
        SimScenarioState scenario = SoloScenario("APP", "SFO");
        var checkIn = new PilotSpeechText($"approach, {Callsign}, C182.", $"approach, {SpokenCallsign}, skylane.");
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.InitialContact, 0, checkIn, PilotRequestContext.Facility("approach"));

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        PilotTransmission call = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(PilotResponder.BuildBravoClearanceRequest(ac, ac.PendingPilotRequest!.BravoWording!.Value).Terminal, call.Text);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest.Kind);
    }

    [Fact]
    public void FlightFollowingCheckIn_Unanswered_RecallsOnce45SecondsOn()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KMOD");
        SimScenarioState scenario = SoloScenario("APP", "SFO");
        scenario.ElapsedSeconds = 10;

        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.InitialContact, request.Kind);
        Assert.Equal(45.0, PilotRequestTracker.InitialContactFollowUpDelaySeconds);
        Assert.Equal(10 + PilotRequestTracker.InitialContactFollowUpDelaySeconds, request.NextFollowUpDueSeconds);
        ac.PendingPilotTransmissions.Clear();

        List<(double Seconds, string Line)> calls = FollowUps(ac, scenario, from: 11, to: 600);

        (double seconds, string line) = Assert.Single(calls);
        Assert.Equal(10 + PilotRequestTracker.InitialContactFollowUpDelaySeconds, seconds);
        Assert.Equal($"approach, {Callsign}, how do you hear me?", line);
        Assert.Equal($"approach, {SpokenCallsign}, how do you hear me?", request.LastPilotLineTts);
        Assert.False(request.IsOpen);
    }

    [Theory]
    [InlineData("FH 270")]
    [InlineData("STBY")]
    public void FlightFollowingCheckIn_ControllerTransmissionBeforeTheRecall_CancelsIt(string command)
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        Assert.Equal(PilotPendingRequestKind.InitialContact, ac.PendingPilotRequest?.Kind);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, command);
        Assert.True(result.Success, result.Message);
        ac.PendingPilotTransmissions.Clear();

        Assert.False(ac.PendingPilotRequest!.IsOpen);
        Assert.Empty(FollowUps(ac, scenario, from: 1, to: 600));
    }

    [Fact]
    public void FlightFollowingCheckIn_ATimedPreset_DoesNotAnswerIt()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PilotPendingRequest request = ac.PendingPilotRequest!;
        scenario.PresetQueue.Add(
            new ScheduledPreset
            {
                Callsign = Callsign,
                Command = "FH 300",
                FireAtSeconds = 0,
            }
        );

        engine.TickOneSecond();

        Assert.Empty(scenario.PresetQueue);
        Assert.Same(request, ac.PendingPilotRequest);
        Assert.True(request.IsOpen);
    }

    [Fact]
    public void FlightFollowingCheckIn_TwoEngineRunsOfTheSameScenario_SayTheSameLinesAtTheSameTimes()
    {
        List<string> first = RunUnansweredCheckIn();
        List<string> second = RunUnansweredCheckIn();

        Assert.Equal(2, first.Count);
        Assert.Contains("how do you hear me?", first[1], StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public void InitialContactRequest_SurvivesASnapshotRoundTrip()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KMOD");
        SimScenarioState scenario = SoloScenario("APP", "SFO");
        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PilotPendingRequest request = ac.PendingPilotRequest!;

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);

        PilotPendingRequest restoredRequest = Assert.IsType<PilotPendingRequest>(restored.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.InitialContact, restoredRequest.Kind);
        Assert.Equal(request.FacilityCallName, restoredRequest.FacilityCallName);
        Assert.Equal(request.NextFollowUpDueSeconds, restoredRequest.NextFollowUpDueSeconds);
        Assert.Equal(request.LastPilotLine, restoredRequest.LastPilotLine);
        Assert.True(restoredRequest.IsOpen);
    }

    private static PilotSpeechText CheckIn(AircraftState ac, SimScenarioState scenario)
    {
        LatLon primary = LookupAirport(scenario.PrimaryAirportId!) ?? throw new InvalidOperationException("no primary airport");
        return Assert.IsType<PilotSpeechText>(PilotResponder.BuildAirborneCheckIn(ac, scenario, primary, AirspaceDatabase.Default, LookupAirport));
    }

    /// <summary>A fresh engine run of the unanswered check-in for 120 s: each pilot line said, with the second it was said.</summary>
    private static List<string> RunUnansweredCheckIn()
    {
        AircraftState ac = Airborne(FarWestRing, trueHeading: 270, destination: "KMOD");
        (SimulationEngine engine, _) = SoloEngine(ac);
        var lines = new List<string>();
        engine.TerminalEntryEmitted += entry =>
        {
            if ((entry.Callsign == Callsign) && (entry.Kind == "SayPilot"))
            {
                lines.Add($"{engine.Scenario?.ElapsedSeconds:F0} {entry.Message}");
            }
        };

        for (int second = 0; second < 120; second++)
        {
            engine.TickOneSecond();
        }

        return lines;
    }

    /// <summary>The tracker's follow-up run each second from <paramref name="from"/> to <paramref name="to"/>, the queue drained.</summary>
    private static List<(double Seconds, string Line)> FollowUps(AircraftState ac, SimScenarioState scenario, int from, int to)
    {
        var calls = new List<(double Seconds, string Line)>();
        for (int second = from; second <= to; second++)
        {
            scenario.ElapsedSeconds = second;
            PilotRequestTracker.TryQueueFollowUp(ac, second, scenario.StudentPosition, LookupAirport);
            calls.AddRange(ac.PendingPilotTransmissions.Select(t => ((double)second, t.Text)));
            ac.PendingPilotTransmissions.Clear();
        }

        return calls;
    }

    private static (SimulationEngine Engine, SimScenarioState Scenario) SoloEngine(AircraftState ac)
    {
        SimScenarioState scenario = SoloScenario("APP", "SFO");
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = scenario };
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
        return ac;
    }

    private static SimScenarioState SoloScenario(string positionType, string primary) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = primary,
            StudentPositionType = positionType,
        };

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "SFO" or "KSFO" => SfoPos,
            "OAK" or "KOAK" => OakPos,
            "HAF" or "KHAF" => new LatLon(37.5133, -122.5008),
            _ => null,
        };
}
