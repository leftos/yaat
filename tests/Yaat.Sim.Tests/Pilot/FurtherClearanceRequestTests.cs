using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// An IFR aircraft on the clearance-limit direct to KOAK asks the radar student for further clearance about five minutes
/// out, reports holding when it reaches the field, and re-asks every five minutes until a lateral clearance moves it on.
/// Solo training only; VFR never asks. Real KOAK navdata. A pilot tuned away from the student's frequency, an AI position
/// included, says none of these calls until retuned, and an open request stays open with its re-ask held.
/// </summary>
public class FurtherClearanceRequestTests
{
    private const string AirportName = "Oakland Airport";
    private const string SpokenCallsign = "united three twenty five";
    private const string RequestTerminal = $"5 min from {AirportName}, request further clearance.";
    private const string RequestTts = $"approach, {SpokenCallsign}, five minutes from {AirportName}, request further clearance.";
    private const string ReportTerminal = $"holding over {AirportName}, 9,000.";
    private const string ReportTts = $"approach, {SpokenCallsign}, holding over {AirportName}, nine thousand.";
    private const string ReaskTerminal = $"holding over {AirportName}, 9,000, request further clearance.";
    private const string ReaskTts = $"approach, {SpokenCallsign}, holding over {AirportName}, nine thousand, request further clearance.";
    private const double AltitudeFt = 9000;
    private const double HoldStartSeconds = 1000;

    public FurtherClearanceRequestTests() => TestVnasData.EnsureInitialized();

    private static LatLon Koak()
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetAirportPosition("KOAK");
        Assert.NotNull(pos);
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    /// <summary>A UAL325 B738 level at 9,000 ft heading west, <paramref name="minutesOut"/> minutes east of KOAK on the direct.</summary>
    private static AircraftState OnTheDirect(double minutesOut, string flightRules = "IFR")
    {
        LatLon koak = Koak();
        var ac = new AircraftState
        {
            Callsign = "UAL325",
            AircraftType = "B738",
            Position = koak,
            TrueHeading = new TrueHeading(270),
            Altitude = AltitudeFt,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            HasMadeInitialContact = true,
            HasControllerAcknowledgedInitialContact = true,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = flightRules,
                Destination = "KOAK",
            },
            ClearedDestination = "KOAK",
        };
        ac.Position = GeoMath.ProjectPoint(koak, new TrueHeading(90), ac.GroundSpeed * minutesOut / 60.0);
        ac.Targets.AssignedAltitude = AltitudeFt;
        ac.Targets.TargetAltitude = AltitudeFt;
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "KOAK",
                Position = koak,
                IsClearanceLimitDirect = true,
                ArrivalTrueCourse = 270,
            }
        );
        return ac;
    }

    private static TrackOwner Student() => TrackOwner.CreateStars("OAK_APP", "NCT", 3, "3O");

    private static TrackOwner AnotherPosition() => TrackOwner.CreateNonNas("ZOA_44");

    /// <summary>An AI-staffed center position as the controller AI publishes it, answering as <paramref name="position"/>.</summary>
    private static AiPositionConfig AiStaffed(TrackOwner position) =>
        new(ControlRole.Center, position, null, position.Callsign, position.Callsign, "Oakland Center", "ZOA", []);

    private static SimScenarioState Scenario(bool solo = true) =>
        new()
        {
            ScenarioId = "further-clearance",
            ScenarioName = "Further clearance request",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            SoloTrainingMode = solo,
            StudentPositionType = "APP",
        };

    /// <summary>The aircraft flown from a mile out on the direct until it holds over KOAK, nothing yet said.</summary>
    private static AircraftState HoldingOverKoak()
    {
        AircraftState ac = OnTheDirect(0.25);
        for (int t = 0; (t < 120) && ac.Targets.NavigationRoute.Exists(n => n.IsClearanceLimitDirect); t++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase).IsClearanceLimitHold);
        return ac;
    }

    /// <summary>The holding aircraft after its report, the report spoken and gone from the queue.</summary>
    private static AircraftState ReportedHolding()
    {
        AircraftState ac = HoldingOverKoak();
        SimScenarioState scenario = Scenario();
        scenario.ElapsedSeconds = HoldStartSeconds;
        PilotProactive.TickFurtherClearanceRequest(ac, scenario);
        Assert.Single(ac.PendingPilotTransmissions);
        ac.PendingPilotTransmissions.Clear();
        return ac;
    }

    private static void Dispatch(AircraftState ac, string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);
    }

    private static void AssertSingleCall(AircraftState ac, string terminal, string tts)
    {
        PilotTransmission call = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(terminal, call.Text);
        Assert.Equal(tts, call.SpeechText);
    }

    [Fact]
    public void DirectCreatedTwoMinutesOut_SaysTwoMinutes()
    {
        AircraftState ac = OnTheDirect(2.0);

        PilotProactive.TickFurtherClearanceRequest(ac, Scenario());

        AssertSingleCall(
            ac,
            $"2 min from {AirportName}, request further clearance.",
            $"approach, {SpokenCallsign}, two minutes from {AirportName}, request further clearance."
        );
    }

    [Fact]
    public void OneMinuteOut_SaysOneMinuteSingular()
    {
        AircraftState ac = OnTheDirect(1.2);

        PilotProactive.TickFurtherClearanceRequest(ac, Scenario());

        AssertSingleCall(
            ac,
            $"1 min from {AirportName}, request further clearance.",
            $"approach, {SpokenCallsign}, one minute from {AirportName}, request further clearance."
        );
    }

    [Fact]
    public void UnderOneMinute_SaysNothingUntilTheHoldingReport()
    {
        AircraftState ac = OnTheDirect(0.5);

        PilotProactive.TickFurtherClearanceRequest(ac, Scenario());

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void InTheHoldBeforeTheReport_DoesNotRepeatTheFiveMinuteCall()
    {
        AircraftState ac = OnTheDirect(4.5);
        SimScenarioState scenario = Scenario();
        scenario.ElapsedSeconds = HoldStartSeconds;
        PilotProactive.TickFurtherClearanceRequest(ac, scenario);
        Assert.Single(ac.PendingPilotTransmissions);
        ac.PendingPilotTransmissions.Clear();
        ac.Position = GeoMath.ProjectPoint(Koak(), new TrueHeading(90), 0.3);
        FlightPhysics.Update(ac, 1.0);
        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(hold.IsClearanceLimitHold);
        Assert.False(hold.ClearanceLimitReported);

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 300, studentPosition: null, airportLookup: _ => null));

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void AltitudeChangedInTheHold_ReaskStatesTheNewAltitude()
    {
        AircraftState ac = ReportedHolding();
        Dispatch(ac, "DM 070");

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 300, studentPosition: null, airportLookup: _ => null));

        AssertSingleCall(
            ac,
            $"holding over {AirportName}, 7,000, request further clearance.",
            $"approach, {SpokenCallsign}, holding over {AirportName}, seven thousand, request further clearance."
        );
    }

    [Fact]
    public void CancelIfrInTheHold_EndsTheReasks()
    {
        AircraftState ac = ReportedHolding();
        ac.FlightPlan.FlightRules = "VFR";

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 300, studentPosition: null, airportLookup: _ => null));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Equal(PilotPendingRequestResponseState.Superseded, ac.PendingPilotRequest!.ResponseState);
    }

    [Fact]
    public void AptInTheHoldAfterTheReport_AsksTheNewAirportOnApproach()
    {
        AircraftState ac = ReportedHolding();
        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(ac, "SJC");
        Assert.True(result.Success, result.Message);
        Assert.True(Assert.Single(ac.Targets.NavigationRoute).IsClearanceLimitDirect);

        (double Lat, double Lon)? ksjcPos = NavigationDatabase.Instance.GetAirportPosition("KSJC");
        Assert.NotNull(ksjcPos);
        var ksjc = new LatLon(ksjcPos.Value.Lat, ksjcPos.Value.Lon);
        ac.TrueHeading = new TrueHeading(270);
        ac.Position = GeoMath.ProjectPoint(ksjc, new TrueHeading(90), ac.GroundSpeed * 4.8 / 60.0);
        SimScenarioState scenario = Scenario();
        scenario.ElapsedSeconds = HoldStartSeconds + 600;

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        PilotTransmission call = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal($"5 min from {PhraseologyVerbalizer.SpellAirportName("KSJC")}, request further clearance.", call.Text);
    }

    [Fact]
    public void StandbyInTheHold_ReasksAfterFiveMinutes()
    {
        AircraftState ac = ReportedHolding();
        ParseResult<CompoundCommand> standby = CommandParser.ParseCompound("STBY");
        Assert.True(standby.IsSuccess, standby.Reason);
        double standbyAt = HoldStartSeconds + 100;
        PilotRequestTracker.ApplyControllerResponse(ac, standby.Value!, standbyAt);
        Assert.Equal(PilotPendingRequestResponseState.Standby, ac.PendingPilotRequest!.ResponseState);

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, standbyAt + 299, studentPosition: null, airportLookup: _ => null));
        Assert.Empty(ac.PendingPilotTransmissions);

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, standbyAt + 300, studentPosition: null, airportLookup: _ => null));
        Assert.Single(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void FiveMinutesOut_AsksForFurtherClearanceOnce()
    {
        AircraftState ac = OnTheDirect(4.8);
        SimScenarioState scenario = Scenario();

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);
        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        PilotTransmission call = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Proactive, call.Kind);
        Assert.Equal(RequestTerminal, call.Text);
        Assert.Equal(RequestTts, call.SpeechText);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.FurtherClearance, request.Kind);
        Assert.True(request.IsOpen);
        Assert.Equal(RequestTerminal, request.LastPilotLine);
    }

    /// <summary><see cref="OnTheDirect"/> with the route's last target an ordinary KOAK fix instead of the clearance-limit direct.</summary>
    private static AircraftState OnRouteEndingAtKoak(double minutesOut)
    {
        AircraftState ac = OnTheDirect(minutesOut);
        ac.Targets.NavigationRoute.Clear();
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "KOAK", Position = Koak() });
        return ac;
    }

    [Fact]
    public void RouteEndingAtTheField_AsksForFurtherClearanceFiveMinutesOut()
    {
        AircraftState ac = OnRouteEndingAtKoak(4.8);
        SimScenarioState scenario = Scenario();

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);
        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        PilotTransmission call = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(RequestTerminal, call.Text);
        Assert.Equal(RequestTts, call.SpeechText);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.FurtherClearance, request.Kind);
        Assert.True(request.IsOpen);
    }

    [Fact]
    public void RouteEndingAtTheField_PendingApproach_NeitherSlowsNorAsks()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("KOAK", "30");
        Assert.NotNull(runway);
        AircraftState ac = OnRouteEndingAtKoak(2.0);
        ac.Targets.TargetSpeed = 280;
        ac.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = new ApproachClearance
            {
                TransitionName = null,
                ApproachId = "I30",
                AirportCode = "KOAK",
                RunwayId = "30",
                FinalApproachCourse = runway.TrueHeading,
            },
            AssignedRunway = runway,
            ConnectingFix = null,
        };

        PilotProactive.TickFurtherClearanceRequest(ac, Scenario());
        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
        Assert.NotNull(ac.Approach.PendingClearance);
        Assert.Equal(280, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SixMinutesOut_SaysNothing()
    {
        AircraftState ac = OnTheDirect(6.0);

        PilotProactive.TickFurtherClearanceRequest(ac, Scenario());

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void NotSolo_SaysNothing()
    {
        AircraftState ac = OnTheDirect(4.5);

        PilotProactive.TickFurtherClearanceRequest(ac, Scenario(solo: false));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void Vfr_SaysNothing()
    {
        AircraftState ac = OnTheDirect(4.5, flightRules: "VFR");

        PilotProactive.TickFurtherClearanceRequest(ac, Scenario());

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void StillOnTheDirectAfterFiveMinutes_DoesNotRepeatTheFiveMinuteCall()
    {
        AircraftState ac = OnTheDirect(4.5);
        SimScenarioState scenario = Scenario();
        scenario.ElapsedSeconds = HoldStartSeconds;
        PilotProactive.TickFurtherClearanceRequest(ac, scenario);
        Assert.Single(ac.PendingPilotTransmissions);
        ac.PendingPilotTransmissions.Clear();

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 300, studentPosition: null, airportLookup: _ => null));

        Assert.Empty(ac.PendingPilotTransmissions);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.FurtherClearance, request.Kind);
        Assert.True(request.IsOpen);
    }

    [Fact]
    public void ReachingTheField_ReportsHoldingThenReasksEveryFiveMinutes()
    {
        AircraftState ac = HoldingOverKoak();
        SimScenarioState scenario = Scenario();
        scenario.ElapsedSeconds = HoldStartSeconds;

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        PilotTransmission report = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(ReportTerminal, report.Text);
        Assert.Equal(ReportTts, report.SpeechText);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.FurtherClearance, request.Kind);
        Assert.Equal(ReaskTerminal, request.LastPilotLine);
        Assert.Equal(ReaskTts, request.LastPilotLineTts);

        ac.PendingPilotTransmissions.Clear();
        PilotProactive.TickFurtherClearanceRequest(ac, scenario);
        Assert.Empty(ac.PendingPilotTransmissions);

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 299, studentPosition: null, airportLookup: _ => null));
        Assert.Empty(ac.PendingPilotTransmissions);

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 300, studentPosition: null, airportLookup: _ => null));
        PilotTransmission reask = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(ReaskTerminal, reask.Text);
        Assert.Equal(ReaskTts, reask.SpeechText);
    }

    [Fact]
    public void AHeadingInTheHold_EndsTheReasks()
    {
        AircraftState ac = ReportedHolding();
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("FH 270");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 300, studentPosition: null, airportLookup: _ => null));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Equal(PilotPendingRequestResponseState.Superseded, ac.PendingPilotRequest!.ResponseState);
    }

    [Fact]
    public void TunedAwayFromStudent_OnTheDirect_NoRequest()
    {
        AircraftState ac = OnTheDirect(4.8);
        SimScenarioState scenario = Scenario();
        scenario.StudentPosition = Student();
        ac.TunedPosition = AnotherPosition();

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void TunedAwayFromStudent_Holding_NoReport_ThenReportsAfterRetune()
    {
        AircraftState ac = HoldingOverKoak();
        SimScenarioState scenario = Scenario();
        scenario.ElapsedSeconds = HoldStartSeconds;
        scenario.StudentPosition = Student();
        ac.TunedPosition = AnotherPosition();
        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
        Assert.False(hold.ClearanceLimitReported);

        ac.TunedPosition = scenario.StudentPosition;
        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        AssertSingleCall(ac, ReportTerminal, ReportTts);
        Assert.True(hold.ClearanceLimitReported);
    }

    [Fact]
    public void TunedAwayFromStudent_OpenRequest_FollowUpHeld_ThenResumes()
    {
        AircraftState ac = ReportedHolding();
        SimScenarioState scenario = Scenario();
        scenario.ElapsedSeconds = HoldStartSeconds;
        scenario.StudentPosition = Student();
        ac.TunedPosition = AnotherPosition();
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        PilotPendingRequestResponseState state = request.ResponseState;
        double lastRequested = request.LastRequestedAtSeconds;
        double nextDue = request.NextFollowUpDueSeconds;

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 400, scenario.StudentPosition, _ => null));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.True(request.IsOpen);
        Assert.Equal(state, request.ResponseState);
        Assert.Equal(lastRequested, request.LastRequestedAtSeconds);
        Assert.Equal(nextDue, request.NextFollowUpDueSeconds);

        ac.TunedPosition = scenario.StudentPosition;
        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, HoldStartSeconds + 400, scenario.StudentPosition, _ => null));
        AssertSingleCall(ac, ReaskTerminal, ReaskTts);
    }

    [Fact]
    public void TunedToStudent_OnTheDirect_StillRequests()
    {
        AircraftState ac = OnTheDirect(4.8);
        SimScenarioState scenario = Scenario();
        scenario.StudentPosition = Student();
        ac.TunedPosition = scenario.StudentPosition;

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        AssertSingleCall(ac, RequestTerminal, RequestTts);
    }

    [Fact]
    public void TunedToAiStaffedPosition_OnTheDirect_NoRequest()
    {
        AircraftState ac = OnTheDirect(4.8);
        SimScenarioState scenario = Scenario();
        scenario.StudentPosition = Student();
        scenario.SetAiStaffedPositions([AiStaffed(AnotherPosition())]);
        ac.TunedPosition = AnotherPosition();

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void NoStudentPosition_TunedSomewhere_NoRequest()
    {
        AircraftState ac = OnTheDirect(4.8);
        SimScenarioState scenario = Scenario();
        scenario.StudentPosition = null;
        ac.TunedPosition = AnotherPosition();

        PilotProactive.TickFurtherClearanceRequest(ac, scenario);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }
}
