using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// YAAT-1693: an IFR arrival never leaves its altitude on its own; with no descent clearance it asks the radar student
/// "request lower" about 60 s of flight before its top of descent, through <see cref="PilotRequestTracker"/>. Real KOAK
/// navdata and a generated jet on the real OAKES3 STAR joined by JARR, solo training with an approach student.
/// </summary>
public class DescentRequestTests
{
    private const string RequestLowerTerminal = "request lower.";

    /// <summary>The band around a held altitude the aircraft counts as level at.</summary>
    private const double LevelToleranceFt = 20.0;

    /// <summary>The fix whose published altitude is the staged jet's descent target: OAKES3's FFIST, between 9,000 and 7,000.</summary>
    private const string TargetFix = "FFIST";

    /// <summary>
    /// The along-route distance the staged jet's descent to FFIST needs, by hand: the harness spawns a B763 level at
    /// 16,000 ft above 250 KIAS (280 at the spawn, 260 by the top of descent), and FFIST's window plans to its upper
    /// value, 9,000 ft, so the descent is 7,000 ft. At 3 NM per 1,000 ft that is 21 NM; the profile term is smaller (the
    /// B763's descent rate at 16,000 ft interpolates to 3,000 - 6,000 / 35,000 x 2,000 = 2,657 fpm, so 7,000 ft takes
    /// 2.63 min, under 21 NM at any ground speed below 478 kt). The descent crosses 10,000 ft above 250 KIAS, so the slowdown adds 10 NM: 31 NM.
    /// </summary>
    private const double StagedRequiredNm = 31.0;

    /// <summary>
    /// Where the staged jet is past its top of descent: 2 NM inside <see cref="StagedRequiredNm"/>, so the call is due at
    /// once whatever the one-minute lead.
    /// </summary>
    private const double PastTopOfDescentNm = StagedRequiredNm - 2.0;

    /// <summary>
    /// Where the staged jet is well before its top of descent: 9 NM beyond <see cref="StagedRequiredNm"/>, more than the
    /// one-minute lead (60 s at its 353 kt ground speed is 5.9 NM) plus a margin.
    /// </summary>
    private const double WellBeforeTopOfDescentNm = StagedRequiredNm + 9.0;

    private readonly ITestOutputHelper _output;

    public DescentRequestTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void JarrOnly_PastTopOfDescent_RequestsLowerOnce()
    {
        if (StagePastTopOfDescent() is not { } staged)
        {
            return;
        }

        PilotTransmissionLog log = new(staged.Aircraft);
        Assert.True(TickUntilRequests(staged, log, 1, 30), "No request for lower within 30 s of becoming eligible past the top of descent");
        AircraftLastPilotTransmission call = Assert.Single(log.RequestsLower);
        Assert.Equal(PilotTransmissionKind.Proactive, call.Kind);
        Assert.Equal(PilotPendingRequestKind.Descent, staged.Aircraft.PendingPilotRequest!.Kind);
        Assert.True(staged.Aircraft.PendingPilotRequest.IsOpen);

        double altitude = staged.Aircraft.Altitude;
        TickChecking(staged, log, 60, () => Assert.InRange(staged.Aircraft.Altitude, altitude - LevelToleranceFt, altitude + LevelToleranceFt));
        Assert.Single(log.RequestsLower);
    }

    [Fact]
    public void JarrOnly_WellBeforeTopOfDescent_NoCall()
    {
        // Just past OAKES at 16,000 ft FFIST is 65 NM ahead along the route: the top of descent is 31 NM before it.
        if (StageBeforeTopOfDescent() is not { } staged)
        {
            return;
        }

        PilotTransmissionLog log = new(staged.Aircraft);
        int seconds = 0;
        while ((DistanceToTargetFixNm(staged.Aircraft) > WellBeforeTopOfDescentNm) && (seconds < 1800))
        {
            Tick(staged, log, 1);
            seconds++;
        }

        _output.WriteLine($"Flew {seconds} s well before the top of descent");
        Assert.True(seconds > 0, "The staging is not before the top of descent");
        Assert.Empty(log.RequestsLower);
        Assert.Null(staged.Aircraft.PendingPilotRequest);
    }

    [Fact]
    public void DescendVia_NoCall() => AssertNoCallAfter("DVIA", 120);

    [Fact]
    public void DescendMaintainBelow_NoCall() => AssertNoCallAfter("DM 110", 60);

    [Fact]
    public void CfixCrossing_NoCall() => AssertNoCallAfter("CFIX FFIST A090", 120);

    [Fact]
    public void ApproachCleared_NoCall() => AssertNoCallAfter("CAPP I30", 120);

    [Fact]
    public void LeftStudentFrequency_NoCall()
    {
        if (StagePastTopOfDescentWith(ac => ac.HasLeftStudentFrequency = true) is not { } staged)
        {
            return;
        }

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void Vfr_NoCall()
    {
        if (StagePastTopOfDescentWith(ac => ac.FlightPlan.FlightRules = "VFR") is not { } staged)
        {
            return;
        }

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void RpoMode_NoCall()
    {
        if (StagePastTopOfDescentRpo() is not { } staged)
        {
            return;
        }

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void ClimbingToAssignedAbove_NoCall()
    {
        if (StagePastTopOfDescentAfter("CM 180") is not { } staged)
        {
            return;
        }

        PilotTransmissionLog log = new(staged.Aircraft);
        int seconds = 0;
        while ((staged.Aircraft.Altitude < 18000 - LevelToleranceFt) && (seconds < 300))
        {
            Tick(staged, log, 1);
            seconds++;
        }

        _output.WriteLine($"Climbed for {seconds} s");
        Assert.True(seconds > 10, "The aircraft was not climbing");
        Assert.Empty(log.RequestsLower);
        Assert.Null(staged.Aircraft.PendingPilotRequest);
    }

    [Fact]
    public void ArrivalWithNoDestination_NoCall()
    {
        if (StagePastTopOfDescentWith(ac => ac.FlightPlan.Destination = "") is not { } staged)
        {
            return;
        }

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void CheckInNotAcknowledged_NoCall()
    {
        if (StagePastTopOfDescentWith(ac => ac.HasControllerAcknowledgedInitialContact = false) is not { } staged)
        {
            return;
        }

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void CenterStudent_UsesCenterCallName()
    {
        if (StagePastTopOfDescentCenterStudent() is not { } staged)
        {
            return;
        }

        PilotTransmissionLog log = new(staged.Aircraft);
        Assert.True(TickUntilRequests(staged, log, 1, 30), "No request for lower within 30 s past the top of descent");
        AircraftLastPilotTransmission call = Assert.Single(log.RequestsLower);
        Assert.StartsWith("center, ", call.SpeechText);
        Assert.EndsWith(", request lower.", call.SpeechText);
    }

    [Fact]
    public void AfterCall_DescendMaintain_SatisfiesAndNoRepeat()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, PilotTransmissionLog log) = after;
        SendOk(staged, "DM 060");
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, staged.Aircraft.PendingPilotRequest!.ResponseState);

        Tick(staged, log, 130);
        Assert.Single(log.RequestsLower);
    }

    [Fact]
    public void AtFixDescendMaintain_Queued_NoReaskBeforeItFires()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, PilotTransmissionLog log) = after;
        SendOk(staged, $"AT {TargetFix} DM 090");

        // FFIST is 29 NM ahead, about five minutes at 353 kt: the queued descent does not fire inside 130 s.
        Tick(staged, log, 130);
        Assert.True(staged.Aircraft.Targets.NavigationRoute.Exists(f => f.Name == TargetFix), $"The aircraft passed {TargetFix}");
        Assert.Single(log.RequestsLower);
    }

    [Fact]
    public void MissedAtTrigger_DescendMaintain_AsksAgain()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        // A block whose AT trigger was missed is never applied or removed (FlightPhysics.TrackFrdMiss): its descent will
        // not come, so the pilot asks again. Staged by marking the queued block missed.
        (Staged staged, PilotTransmissionLog log) = after;
        SendOk(staged, $"AT {TargetFix} DM 090");
        CommandBlock queued = Assert.Single(staged.Aircraft.Queue.Blocks, b => !b.IsApplied);
        queued.TriggerMissed = true;

        Assert.True(TickUntilRequests(staged, log, 2, 30), "No new request for lower within 30 s of the AT trigger being missed");
        Assert.Equal(PilotPendingRequestKind.Descent, staged.Aircraft.PendingPilotRequest!.Kind);
        Assert.True(staged.Aircraft.PendingPilotRequest.IsOpen);
    }

    [Fact]
    public void DescendMaintain_WithPilotReactionDelay_NoReask()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, PilotTransmissionLog log) = after;
        SimScenarioState scenario = staged.Engine.Scenario!;
        scenario.CommandRunDelayMinSeconds = 8;
        scenario.CommandRunDelayMaxSeconds = 8;
        SendOk(staged, "DM 110");
        Assert.NotEmpty(staged.Aircraft.DeferredDispatches);

        Tick(staged, log, 30);
        Assert.Single(log.RequestsLower);
    }

    [Fact]
    public void ClimbMaintainBelow_SatisfiesTheRequest() => AssertAnswerSatisfies("CM 110");

    [Fact]
    public void ExpediteBelow_SatisfiesTheRequest() => AssertAnswerSatisfies("EXP 110");

    [Fact]
    public void OpenRequest_DescendVia_Satisfies() => AssertAnswerSatisfies("DVIA");

    [Fact]
    public void OpenRequest_Cfix_Satisfies() => AssertAnswerSatisfies($"CFIX {TargetFix} A090");

    [Fact]
    public void OpenRequest_ClearedApproach_Satisfies() => AssertAnswerSatisfies("CAPP I30");

    [Fact]
    public void OpenRequest_ConstrainedDctf_Satisfies() => AssertAnswerSatisfies($"DCTF {TargetFix}/A090");

    [Fact]
    public void Contact_WhileOpen_NoRepeat()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        // A bare CT sends the aircraft to the position a handoff names.
        (Staged staged, PilotTransmissionLog log) = after;
        staged.Aircraft.Track.HandoffPeer = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        SendOk(staged, "CT");
        Tick(staged, log, 130);
        Assert.Single(log.RequestsLower);
    }

    [Fact]
    public void Hold_WhileOpen_NoRepeat() => AssertNoRepeatAfter($"HOLD {TargetFix} 090 R");

    [Fact]
    public void AfterCall_Standby_ReasksAfter90s()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, PilotTransmissionLog log) = after;
        SendOk(staged, "STBY");
        double standbyAt = staged.Engine.Scenario!.ElapsedSeconds;
        Assert.Equal(PilotPendingRequestResponseState.Standby, staged.Aircraft.PendingPilotRequest!.ResponseState);

        Tick(staged, log, 85);
        Assert.Single(log.RequestsLower);

        Assert.True(TickUntilRequests(staged, log, 2, 30), "No re-ask within 115 s of STBY");
        Assert.InRange(log.RequestsLower[1].AtSeconds - standbyAt, PilotRequestTracker.StandbyFollowUpDelaySeconds, 115);
        Assert.Equal(PilotPendingRequestKind.Descent, staged.Aircraft.PendingPilotRequest.Kind);
    }

    [Fact]
    public void AfterCall_NoAnswer_ReasksAfter120s()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, PilotTransmissionLog log) = after;
        double firstAt = log.RequestsLower[0].AtSeconds;

        Tick(staged, log, 110);
        Assert.Single(log.RequestsLower);

        Assert.True(TickUntilRequests(staged, log, 2, 30), "No re-ask within 140 s of an unanswered request");
        Assert.InRange(log.RequestsLower[1].AtSeconds - firstAt, PilotRequestTracker.NormalFollowUpDelaySeconds - 1, 140);
    }

    [Fact]
    public void AfterCall_UnrelatedHeading_LeavesItOpen()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, _) = after;
        SendOk(staged, "FH 270");

        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(staged.Aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Descent, request.Kind);
        Assert.Equal(PilotPendingRequestResponseState.None, request.ResponseState);
    }

    [Fact]
    public void SteppedDown_LevelAgain_PastNextTop_RequestsAgain()
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, PilotTransmissionLog log) = after;
        AircraftState aircraft = staged.Aircraft;
        NavigationTarget firstBelow = aircraft.Targets.NavigationRoute.First(t =>
            (t.AltitudeRestriction is { } r) && (r.Altitude1Ft < aircraft.Altitude - FlightPhysics.AltitudeSnapFt)
        );
        int stepAltitude = firstBelow.AltitudeRestriction!.Altitude1Ft + 1000;
        Assert.True(
            stepAltitude < aircraft.Altitude - 500,
            $"No room for a step above {firstBelow.Name}'s {firstBelow.AltitudeRestriction.Altitude1Ft}"
        );
        SendOk(staged, $"DM {stepAltitude / 100:D3}");
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, aircraft.PendingPilotRequest!.ResponseState);

        Assert.True(TickUntilRequests(staged, log, 2, 1800), $"No second request for lower after leveling at {stepAltitude}");
        Assert.InRange(aircraft.Altitude, stepAltitude - LevelToleranceFt, stepAltitude + LevelToleranceFt);
        Assert.Equal(PilotPendingRequestKind.Descent, aircraft.PendingPilotRequest!.Kind);
        Assert.True(aircraft.PendingPilotRequest.IsOpen);
    }

    [Fact]
    public void NoStar_TargetIsFieldPlus3000At10Nm()
    {
        // A B738 level at 9,000 ft and 240 KIAS inbound to KOAK (field elevation 9 ft) on an empty route: the target is
        // 3,009 ft, a 5,991 ft descent. At 3 NM per 1,000 ft that is 17.97 NM; the profile term is smaller (the B738
        // descends 3,300 fpm at 9,000 ft, so 1.82 min, under 17.97 NM below 593 kt). No slowdown below 10,000 ft. The
        // target is reached 10 NM out, so the top of descent is 27.97 NM from the field, and the call comes one minute
        // before it (about 4.6 NM at 276 kt): 1 NM before the top of descent is inside that lead, 12 NM is outside it.
        const double topOfDescentFromFieldNm = 10.0 + 17.97;

        AircraftState early = NoStarArrival(topOfDescentFromFieldNm + 12.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        PilotProactive.TickDescentRequest(early, NoStarScenario(), Airport);
        Assert.Empty(early.PendingPilotTransmissions);
        Assert.Null(early.PendingPilotRequest);

        AircraftState onTime = NoStarArrival(topOfDescentFromFieldNm + 1.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        PilotProactive.TickDescentRequest(onTime, NoStarScenario(), Airport);
        PilotTransmission queued = Assert.Single(onTime.PendingPilotTransmissions);
        Assert.Equal(RequestLowerTerminal, queued.Text);
        Assert.Equal(PilotPendingRequestKind.Descent, onTime.PendingPilotRequest!.Kind);
        Assert.Equal(9000, onTime.Altitude);
        Assert.Null(onTime.Targets.AssignedAltitude);
    }

    [Fact]
    public void HighFastDescent_AddsTheSlowdownDistance()
    {
        // A B763 level at 16,000 ft inbound to KOAK on an empty route: the target is 3,009 ft, a 12,991 ft descent. At
        // 3 NM per 1,000 ft that is 38.97 NM; the profile term is smaller (2,657 fpm at 16,000 ft, so 4.89 min, under
        // 38.97 NM below 478 kt). At 280 KIAS the descent crosses 10,000 ft above 250 KIAS and needs 10 NM more: the top
        // of descent is 10 + 48.97 = 58.97 NM from the field, against 10 + 38.97 = 48.97 NM at 240 KIAS. The call comes
        // one minute before it (under 6 NM at either speed): 1 NM before a top of descent is inside that lead, 9 NM is not.
        const double fastTopOfDescentNm = 10.0 + 38.97 + 10.0;
        const double slowTopOfDescentNm = 10.0 + 38.97;

        Assert.True(CallsAt(fastTopOfDescentNm + 1.0, indicatedAirspeed: 280), "280 KIAS: no call 1 NM before the top of descent");
        Assert.False(CallsAt(fastTopOfDescentNm + 9.0, indicatedAirspeed: 280), "280 KIAS: a call 9 NM before the top of descent");
        Assert.True(CallsAt(slowTopOfDescentNm + 1.0, indicatedAirspeed: 240), "240 KIAS: no call 1 NM before the top of descent");
        Assert.False(CallsAt(slowTopOfDescentNm + 9.0, indicatedAirspeed: 240), "240 KIAS: a call 9 NM before the top of descent");
        Assert.False(CallsAt(fastTopOfDescentNm + 1.0, indicatedAirspeed: 240), "240 KIAS called 10 NM early, as if slowing down");
    }

    private static bool CallsAt(double distanceFromFieldNm, double indicatedAirspeed)
    {
        AircraftState aircraft = NoStarArrival(distanceFromFieldNm, indicatedAirspeed, altitude: 16000, type: "B763");
        PilotProactive.TickDescentRequest(aircraft, NoStarScenario(), Airport);
        return aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent };
    }

    private static AircraftState NoStarArrival(double distanceFromFieldNm, double indicatedAirspeed, double altitude, string type)
    {
        LatLon koak = Assert.IsType<LatLon>(Airport("KOAK"));
        return new AircraftState
        {
            Callsign = "UAL325",
            AircraftType = type,
            Position = GeoMath.ProjectPoint(koak, new TrueHeading(90), distanceFromFieldNm),
            TrueHeading = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = indicatedAirspeed,
            IsOnGround = false,
            HasMadeInitialContact = true,
            HasControllerAcknowledgedInitialContact = true,
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = "KOAK" },
        };
    }

    private static SimScenarioState NoStarScenario() =>
        new()
        {
            ScenarioId = "descent-request-no-star",
            ScenarioName = "Descent request with no STAR",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static LatLon? Airport(string id) => NavigationDatabase.Instance.GetAirportPosition(id) is { } pos ? new LatLon(pos.Lat, pos.Lon) : null;

    private void AssertNoCallAfter(string command, int seconds)
    {
        if (StagePastTopOfDescentAfter(command) is not { } staged)
        {
            return;
        }

        AssertNoCall(staged, seconds);
    }

    private static void AssertNoCall(Staged staged, int seconds)
    {
        PilotTransmissionLog log = new(staged.Aircraft);
        Tick(staged, log, seconds);
        Assert.Empty(log.RequestsLower);
        Assert.False(staged.Aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent }, "A descent request was recorded");
    }

    private void AssertAnswerSatisfies(string command)
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, _) = after;
        Assert.True(staged.Aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent, IsOpen: true });
        SendOk(staged, command);
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, staged.Aircraft.PendingPilotRequest!.ResponseState);
    }

    private void AssertNoRepeatAfter(string command)
    {
        if (StageAfterCall() is not { } after)
        {
            return;
        }

        (Staged staged, PilotTransmissionLog log) = after;
        SendOk(staged, command);
        Tick(staged, log, 130);
        Assert.Single(log.RequestsLower);
    }

    private (Staged Staged, PilotTransmissionLog Log)? StageAfterCall()
    {
        if (StagePastTopOfDescent() is not { } staged)
        {
            return null;
        }

        PilotTransmissionLog log = new(staged.Aircraft);
        Assert.True(TickUntilRequests(staged, log, 1, 30), "No request for lower within 30 s past the top of descent");
        return (staged, log);
    }

    private Staged? StageBeforeTopOfDescent() => Stage(pastTopOfDescent: false, solo: true, positionType: "APP", beforeSolo: null, command: null);

    private Staged? StagePastTopOfDescent() => Stage(pastTopOfDescent: true, solo: true, positionType: "APP", beforeSolo: null, command: null);

    private Staged? StagePastTopOfDescentAfter(string command) =>
        Stage(pastTopOfDescent: true, solo: true, positionType: "APP", beforeSolo: null, command: command);

    private Staged? StagePastTopOfDescentWith(Action<AircraftState> beforeSolo) =>
        Stage(pastTopOfDescent: true, solo: true, positionType: "APP", beforeSolo: beforeSolo, command: null);

    private Staged? StagePastTopOfDescentRpo() => Stage(pastTopOfDescent: true, solo: false, positionType: "APP", beforeSolo: null, command: null);

    private Staged? StagePastTopOfDescentCenterStudent() =>
        Stage(pastTopOfDescent: true, solo: true, positionType: "CTR", beforeSolo: null, command: null);

    /// <summary>
    /// The OAKES3 jet at 16,000 past the STAR's first fix, joined to the STAR by JARR with no altitude clearance, its
    /// initial contact made and acknowledged, then the scenario switched to solo training with a
    /// <paramref name="positionType"/> student. With <paramref name="pastTopOfDescent"/> it first flies on, not yet solo,
    /// to <see cref="PastTopOfDescentNm"/> from FFIST (a late handoff); <paramref name="command"/> is issued there.
    /// </summary>
    private Staged? Stage(bool pastTopOfDescent, bool solo, string positionType, Action<AircraftState>? beforeSolo, string? command)
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return null;
        }

        Staged staged = new(harness.Engine, harness.Aircraft);
        TickUntilFirstFixPassed(staged);
        SendOk(staged, "JARR OAKES3");
        if (pastTopOfDescent)
        {
            int flown = 0;
            while (DistanceToTargetFixNm(staged.Aircraft) > PastTopOfDescentNm)
            {
                Assert.True(flown < 1800, "The aircraft never passed its top of descent");
                staged.Engine.TickOneSecond();
                flown++;
            }

            _output.WriteLine($"Flew {flown} s to {PastTopOfDescentNm} NM from {TargetFix}");
        }

        if (command is not null)
        {
            SendOk(staged, command);
        }

        AircraftState aircraft = staged.Aircraft;
        aircraft.HasMadeInitialContact = true;
        aircraft.HasControllerAcknowledgedInitialContact = true;
        beforeSolo?.Invoke(aircraft);
        SimScenarioState scenario = staged.Engine.Scenario!;
        scenario.SoloTrainingMode = solo;
        scenario.StudentPositionType = positionType;

        double along = 0;
        LatLon previous = aircraft.Position;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            along += GeoMath.DistanceNm(previous, fix.Position);
            previous = fix.Position;
            string restriction = fix.AltitudeRestriction is { } r ? $" {r.Type} {r.Altitude1Ft}/{r.Altitude2Ft} {r.Source}" : "";
            _output.WriteLine($"  {fix.Name} at {along:F1} NM{restriction}");
        }

        _output.WriteLine($"Staged at {aircraft.Altitude:F0} ft, {aircraft.IndicatedAirspeed:F0} KIAS, GS {aircraft.GroundSpeed:F0}");
        return staged;
    }

    /// <summary>The along-route distance to <see cref="TargetFix"/>: aircraft to the first route fix and on, fix by fix.</summary>
    private static double DistanceToTargetFixNm(AircraftState aircraft)
    {
        double along = 0;
        LatLon previous = aircraft.Position;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            along += GeoMath.DistanceNm(previous, fix.Position);
            previous = fix.Position;
            if (fix.Name == TargetFix)
            {
                return along;
            }
        }

        Assert.Fail($"{TargetFix} is not on the route");
        return 0;
    }

    private static void Tick(Staged staged, PilotTransmissionLog log, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            staged.Engine.TickOneSecond();
            log.Observe();
        }
    }

    private static void TickChecking(Staged staged, PilotTransmissionLog log, int seconds, Action eachSecond)
    {
        for (int t = 0; t < seconds; t++)
        {
            staged.Engine.TickOneSecond();
            log.Observe();
            eachSecond();
        }
    }

    private static bool TickUntilRequests(Staged staged, PilotTransmissionLog log, int count, int maxSeconds)
    {
        for (int t = 0; (t < maxSeconds) && (log.RequestsLower.Count < count); t++)
        {
            staged.Engine.TickOneSecond();
            log.Observe();
        }

        return log.RequestsLower.Count >= count;
    }

    private static void TickUntilFirstFixPassed(Staged staged)
    {
        string first = staged.Aircraft.Targets.NavigationRoute[0].Name;
        for (int t = 1; t <= 1800; t++)
        {
            staged.Engine.TickOneSecond();
            if (!staged.Aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(first, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        Assert.Fail($"The aircraft did not pass {first} within 1800 s");
    }

    private void SendOk(Staged staged, string command)
    {
        CommandResult result = staged.Engine.SendCommand(staged.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private sealed record Staged(SimulationEngine Engine, AircraftState Aircraft);

    /// <summary>Every line the aircraft spoke, read off its last-transmission stamp after each tick.</summary>
    private sealed class PilotTransmissionLog(AircraftState aircraft)
    {
        private double? _lastAt = aircraft.LastPilotTransmission?.AtSeconds;

        public List<AircraftLastPilotTransmission> Lines { get; } = [];

        public List<AircraftLastPilotTransmission> RequestsLower => [.. Lines.Where(l => l.Text == RequestLowerTerminal)];

        public void Observe()
        {
            if ((aircraft.LastPilotTransmission is { } line) && (line.AtSeconds != _lastAt))
            {
                _lastAt = line.AtSeconds;
                Lines.Add(line);
            }
        }
    }
}
