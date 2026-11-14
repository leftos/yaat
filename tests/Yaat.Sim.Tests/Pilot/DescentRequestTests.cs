using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// YAAT-1693, YAAT-2249: an IFR arrival never leaves its altitude on its own; with no descent clearance it asks the radar
/// student "request lower" once 1,000 ft above its profile (field elevation + 300 ft per NM, a published STAR altitude ahead
/// as a second, lower line), 2,000 ft above it and no sooner than 180 s after a descent clearance, through
/// <see cref="PilotRequestTracker"/>. Real KOAK navdata and a generated jet on the real OAKES3 STAR joined by JARR, solo
/// training with an approach student.
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

    /// <summary>How far above the profile a pilot never stepped down asks for lower.</summary>
    private const double FirstRequestAboveProfileFt = 1000.0;

    /// <summary>How far above the profile a pilot asks again after a descent clearance.</summary>
    private const double StepDownRequestAboveProfileFt = 2000.0;

    /// <summary>The least time after a descent clearance before the pilot asks for lower again.</summary>
    private const double StepDownWaitSeconds = 180.0;

    /// <summary>The profile the aircraft gains on in a second of flight at these ground speeds (about 0.1 NM at 300 ft/NM).</summary>
    private const double OneSecondToleranceFt = 40.0;

    /// <summary>The profile's gradient: 300 ft per NM (7110.65 4-8-1.h.2(a) NOTE).</summary>
    private const double ProfileFtPerNm = 300.0;

    private readonly ITestOutputHelper _output;

    public DescentRequestTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void JarrOnly_PastTopOfDescent_RequestsLowerOnce()
    {
        Staged? staged = StagePastTopOfDescent();
        Assert.NotNull(staged);

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
        Staged? staged = StageBeforeTopOfDescent();
        Assert.NotNull(staged);

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
        Staged? staged = StagePastTopOfDescentWith(ac => ac.TunedPosition = TrackOwner.Elsewhere);
        Assert.NotNull(staged);

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void Vfr_NoCall()
    {
        Staged? staged = StagePastTopOfDescentWith(ac => ac.FlightPlan.FlightRules = "VFR");
        Assert.NotNull(staged);

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void RpoMode_NoCall()
    {
        Staged? staged = StagePastTopOfDescentRpo();
        Assert.NotNull(staged);

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void ClimbingToAssignedAbove_NoCall()
    {
        Staged? staged = StagePastTopOfDescentAfter("CM 180");
        Assert.NotNull(staged);

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
        Staged? staged = StagePastTopOfDescentWith(ac => ac.FlightPlan.Destination = "");
        Assert.NotNull(staged);

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void CheckInNotAcknowledged_NoCall()
    {
        Staged? staged = StagePastTopOfDescentWith(ac => ac.HasControllerAcknowledgedInitialContact = false);
        Assert.NotNull(staged);

        AssertNoCall(staged, 120);
    }

    [Fact]
    public void CenterStudent_UsesCenterCallName()
    {
        Staged? staged = StagePastTopOfDescentCenterStudent();
        Assert.NotNull(staged);

        PilotTransmissionLog log = new(staged.Aircraft);
        Assert.True(TickUntilRequests(staged, log, 1, 30), "No request for lower within 30 s past the top of descent");
        AircraftLastPilotTransmission call = Assert.Single(log.RequestsLower);
        Assert.StartsWith("center, ", call.SpeechText);
        Assert.EndsWith(", request lower.", call.SpeechText);
    }

    [Fact]
    public void AfterCall_DescendMaintain_SatisfiesAndNoRepeat()
    {
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

        (Staged staged, PilotTransmissionLog log) = after;
        SendOk(staged, "DM 060");
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, staged.Aircraft.PendingPilotRequest!.ResponseState);

        Tick(staged, log, 130);
        Assert.Single(log.RequestsLower);
    }

    [Fact]
    public void AtFixDescendMaintain_Queued_NoReaskBeforeItFires()
    {
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

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
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

        // A block whose AT trigger was missed is never applied or removed (FlightPhysics.TrackFrdMiss): its descent will
        // not come, so the pilot asks again, after the wait any descent clearance starts (180 s, 2,000 ft above the
        // profile). Staged by marking the queued block missed.
        (Staged staged, PilotTransmissionLog log) = after;
        SendOk(staged, $"AT {TargetFix} DM 090");
        CommandBlock queued = Assert.Single(staged.Aircraft.Queue.Blocks, b => !b.IsApplied);
        queued.TriggerMissed = true;

        Assert.True(TickUntilRequests(staged, log, 2, 600), "No new request for lower within 600 s of the AT trigger being missed");
        Assert.Equal(PilotPendingRequestKind.Descent, staged.Aircraft.PendingPilotRequest!.Kind);
        Assert.True(staged.Aircraft.PendingPilotRequest.IsOpen);
    }

    [Fact]
    public void DescendMaintain_WithPilotReactionDelay_NoReask()
    {
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

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
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

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
    public void RequestAfterStandby_RepeatsAfter150()
    {
        // A "standby" acknowledges the request, so the pilot waits longer than after silence: 150 s, not 120 s.
        (Staged staged, PilotTransmissionLog log) = Assert.NotNull(StageAfterCall());
        SendOk(staged, "STBY");
        double standbyAt = staged.Engine.Scenario!.ElapsedSeconds;
        Assert.Equal(PilotPendingRequestResponseState.Standby, staged.Aircraft.PendingPilotRequest!.ResponseState);

        Tick(staged, log, 145);
        Assert.Single(log.RequestsLower);

        Assert.True(TickUntilRequests(staged, log, 2, 30), "No re-ask within 175 s of STBY");
        Assert.Equal(standbyAt + PilotRequestTracker.StandbyFollowUpDelaySeconds, staged.Aircraft.PendingPilotRequest.LastRequestedAtSeconds, 3);
        Assert.Equal(PilotPendingRequestKind.Descent, staged.Aircraft.PendingPilotRequest.Kind);
    }

    [Fact]
    public void UnansweredRequest_RepeatsAfter120_AtMostTwice()
    {
        // The request and two re-asks, each queued 120 s after the last, then that request is not voiced again. (A
        // re-open in the 2,000 ft band is a new request: ReopenAfterCap_* below.)
        (Staged staged, PilotTransmissionLog log) = Assert.NotNull(StageAfterCall());
        PilotPendingRequest first = Assert.IsType<PilotPendingRequest>(staged.Aircraft.PendingPilotRequest);
        double askedAt = first.LastRequestedAtSeconds;

        Assert.True(TickUntilRequests(staged, log, 2, 130), "No first re-ask within 130 s");
        Assert.Equal(askedAt + PilotRequestTracker.NormalFollowUpDelaySeconds, first.LastRequestedAtSeconds, 3);
        Assert.True(TickUntilRequests(staged, log, 3, 130), "No second re-ask within 130 s of the first");
        Assert.Equal(askedAt + (2 * PilotRequestTracker.NormalFollowUpDelaySeconds), first.LastRequestedAtSeconds, 3);
        Assert.Equal(PilotRequestTracker.MaxFollowUps, first.FollowUpsVoiced);

        Tick(staged, log, 130);
        Assert.Equal(askedAt + (2 * PilotRequestTracker.NormalFollowUpDelaySeconds), first.LastRequestedAtSeconds, 3);
        Assert.Equal(PilotRequestTracker.MaxFollowUps, first.FollowUpsVoiced);
    }

    [Fact]
    public void ReopenAfterCap_AskedInTheThousandFootBand_ReopensOnceInTheTwoThousandFootBand()
    {
        // A B738 level at 9,000 ft inbound to KOAK (field 9 ft), no STAR: 25 NM out it is 9,000 - (9 + 300 x 25) = 1,491 ft
        // above the profile, so it asks in the 1,000 ft band. Unanswered, it re-asks at 120 s and 240 s and is capped.
        // Still 25 NM out (1,491 ft above) it does not re-open; 21 NM out it is 2,691 ft above, in the 2,000 ft band, and
        // re-opens once. That request re-asks twice and is capped too, and 15 NM out (3,491 ft above) nothing re-opens.
        AircraftState aircraft = NoStarArrival(25.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        SimScenarioState scenario = NoStarScenario();
        PilotProactive.TickDescentRequest(aircraft, scenario, Airport);
        PilotPendingRequest first = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.False(first.AskedInStepDownBand);
        AssertReasksTwiceThenCaps(aircraft, scenario, first, askedAt: 0);

        TickDescentRequestAt(aircraft, scenario, 360);
        Assert.Same(first, aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);

        aircraft.Position = FromField("KOAK", 21.0);
        TickDescentRequestAt(aircraft, scenario, 361);
        PilotPendingRequest reopened = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.NotSame(first, reopened);
        Assert.True(reopened.ReopenedAfterCap);
        Assert.True(reopened.AskedInStepDownBand);
        Assert.Equal(361, reopened.LastRequestedAtSeconds);
        Assert.Equal(RequestLowerTerminal, Assert.Single(aircraft.PendingPilotTransmissions).Text);
        aircraft.PendingPilotTransmissions.Clear();
        AssertReasksTwiceThenCaps(aircraft, scenario, reopened, askedAt: 361);

        aircraft.Position = FromField("KOAK", 15.0);
        TickDescentRequestAt(aircraft, scenario, 900);
        Assert.Same(reopened, aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void ReopenAfterCap_AskedInTheTwoThousandFootBand_NeverReopens()
    {
        // The same B738 21 NM out (2,691 ft above the profile), stepped down long ago: it asks in the 2,000 ft band.
        // Capped after its two re-asks, it does not re-open, even 15 NM out (3,491 ft above) and 600 s on.
        AircraftState aircraft = NoStarArrival(21.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        aircraft.DescentClearedAtSeconds = -1000;
        SimScenarioState scenario = NoStarScenario();
        PilotProactive.TickDescentRequest(aircraft, scenario, Airport);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.True(request.AskedInStepDownBand);
        AssertReasksTwiceThenCaps(aircraft, scenario, request, askedAt: 0);

        aircraft.Position = FromField("KOAK", 15.0);
        TickDescentRequestAt(aircraft, scenario, 600);
        Assert.Same(request, aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void ReopenAfterCap_FirstAskTwoThousandAbove_NeverReopens()
    {
        // The B738 21 NM out with no descent clearance: 9,000 - (9 + 300 x 21) = 2,691 ft above the profile. It asks at the
        // first request's 1,000 ft threshold, but from the 2,000 ft band, so once capped it never re-opens: not even 15 NM
        // out (3,491 ft above), 600 s on.
        AircraftState aircraft = NoStarArrival(21.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        SimScenarioState scenario = NoStarScenario();
        PilotProactive.TickDescentRequest(aircraft, scenario, Airport);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.True(request.AskedInStepDownBand);
        AssertReasksTwiceThenCaps(aircraft, scenario, request, askedAt: 0);

        aircraft.Position = FromField("KOAK", 15.0);
        TickDescentRequestAt(aircraft, scenario, 600);
        Assert.Same(request, aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void CappedDescentRequest_ReplacedAndAnswered_StaysCappedUntilTheBandRule()
    {
        // 25 NM out (1,491 ft above the profile) the B738 asks in the 1,000 ft band and is capped. A request for further
        // clearance replaces it and is answered: still in the 1,000 ft band the pilot asks nothing. 21 NM out (2,691 ft
        // above, the 2,000 ft band) it re-opens exactly once; capped and replaced again, it asks nothing even 15 NM out.
        AircraftState aircraft = NoStarArrival(25.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        SimScenarioState scenario = NoStarScenario();
        PilotProactive.TickDescentRequest(aircraft, scenario, Airport);
        PilotPendingRequest first = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        AssertReasksTwiceThenCaps(aircraft, scenario, first, askedAt: 0);

        ReplaceWithAnsweredFurtherClearance(aircraft, 360);
        TickDescentRequestAt(aircraft, scenario, 361);
        Assert.Equal(PilotPendingRequestKind.FurtherClearance, aircraft.PendingPilotRequest!.Kind);
        Assert.Empty(aircraft.PendingPilotTransmissions);

        aircraft.Position = FromField("KOAK", 21.0);
        TickDescentRequestAt(aircraft, scenario, 362);
        PilotPendingRequest reopened = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Descent, reopened.Kind);
        Assert.True(reopened.ReopenedAfterCap);
        Assert.Equal(RequestLowerTerminal, Assert.Single(aircraft.PendingPilotTransmissions).Text);
        aircraft.PendingPilotTransmissions.Clear();
        TickDescentRequestAt(aircraft, scenario, 363);
        Assert.Same(reopened, aircraft.PendingPilotRequest);
        AssertReasksTwiceThenCaps(aircraft, scenario, reopened, askedAt: 362);

        ReplaceWithAnsweredFurtherClearance(aircraft, 800);
        aircraft.Position = FromField("KOAK", 15.0);
        TickDescentRequestAt(aircraft, scenario, 801);
        Assert.Equal(PilotPendingRequestKind.FurtherClearance, aircraft.PendingPilotRequest!.Kind);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    private static void ReplaceWithAnsweredFurtherClearance(AircraftState aircraft, double nowSeconds)
    {
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.FurtherClearance,
            nowSeconds,
            FurtherClearanceLine,
            PilotRequestContext.Facility("approach")
        );
        Assert.True(PilotRequestTracker.SatisfyOpenRequest(aircraft, PilotPendingRequestKind.FurtherClearance, nowSeconds));
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    private static readonly PilotSpeechText FurtherClearanceLine = new(
        "request further clearance.",
        "approach, united three two five, request further clearance."
    );

    /// <summary>
    /// Re-asks <paramref name="request"/> (asked at <paramref name="askedAt"/>) at 120 s and 240 s after, exactly, and not
    /// at 360 s: the cap. Clears the queued lines as the frequency would.
    /// </summary>
    private static void AssertReasksTwiceThenCaps(AircraftState aircraft, SimScenarioState scenario, PilotPendingRequest request, double askedAt)
    {
        aircraft.PendingPilotTransmissions.Clear();
        double delay = PilotRequestTracker.NormalFollowUpDelaySeconds;
        Assert.False(PilotRequestTracker.TryQueueFollowUp(aircraft, askedAt + delay - 1, scenario.StudentPosition, Airport));
        Assert.True(PilotRequestTracker.TryQueueFollowUp(aircraft, askedAt + delay, scenario.StudentPosition, Airport));
        aircraft.PendingPilotTransmissions.Clear();
        Assert.True(PilotRequestTracker.TryQueueFollowUp(aircraft, askedAt + (2 * delay), scenario.StudentPosition, Airport));
        aircraft.PendingPilotTransmissions.Clear();
        Assert.False(PilotRequestTracker.TryQueueFollowUp(aircraft, askedAt + (3 * delay), scenario.StudentPosition, Airport));
        Assert.Empty(aircraft.PendingPilotTransmissions);
        Assert.Equal(PilotRequestTracker.MaxFollowUps, request.FollowUpsVoiced);
        Assert.Equal(askedAt + (2 * delay), request.LastRequestedAtSeconds);
        Assert.True(request.IsOpen);
    }

    private static void TickDescentRequestAt(AircraftState aircraft, SimScenarioState scenario, double nowSeconds)
    {
        scenario.ElapsedSeconds = nowSeconds;
        PilotProactive.TickDescentRequest(aircraft, scenario, Airport);
    }

    [Theory]
    [InlineData("KFAT", 5.0, 3000.0)] // 3,000 - (336 + 1,500) = 1,164 ft above the profile, inside 10 NM
    [InlineData("KFAT", 8.0, 6000.0)] // 6,000 - (336 + 2,400) = 3,264 ft above the profile, inside 10 NM
    public void Floor_NoRequestForLower(string field, double fromFieldNm, double altitudeFt)
    {
        AircraftState aircraft = NoStarArrival(fromFieldNm, indicatedAirspeed: 200, altitude: altitudeFt, type: "B738", destination: field);
        PilotProactive.TickDescentRequest(aircraft, NoStarScenario(), Airport);
        Assert.Null(aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void Floor_OutsideIt_AsksAtOneThousandAboveTheProfile()
    {
        // 25 NM from KOAK at 9,000 ft: 9,000 - (9 + 7,500) = 1,491 ft above the profile, outside the floor.
        AircraftState aircraft = NoStarArrival(25.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        PilotProactive.TickDescentRequest(aircraft, NoStarScenario(), Airport);
        Assert.Equal(PilotPendingRequestKind.Descent, aircraft.PendingPilotRequest!.Kind);
        Assert.False(aircraft.PendingPilotRequest.AskedInStepDownBand);
    }

    [Fact]
    public void Floor_OpenRequestInsideTenMiles_ClosesAsMoot()
    {
        AircraftState aircraft = NoStarArrival(9.0, indicatedAirspeed: 200, altitude: 6000, type: "B738", destination: "KFAT");
        PilotRequestTracker.RecordRequest(aircraft, PilotPendingRequestKind.Descent, 0, RequestLowerLine, PilotRequestContext.Facility("approach"));

        Assert.False(PilotRequestTracker.TryQueueFollowUp(aircraft, PilotRequestTracker.NormalFollowUpDelaySeconds, null, Airport));
        Assert.Equal(PilotPendingRequestResponseState.Superseded, aircraft.PendingPilotRequest!.ResponseState);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void CappedDescentRequest_DoesNotBlockTheApproachRequest()
    {
        // 8 NM from KOAK with a request for lower that used up its re-asks: the approach request is voiced on the tick it
        // is due and replaces it.
        AircraftState aircraft = NoStarArrival(8.0, indicatedAirspeed: 200, altitude: 5000, type: "B738");
        PilotPendingRequest descent = PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Descent,
            0,
            RequestLowerLine,
            PilotRequestContext.Facility("approach")
        );
        descent.FollowUpsVoiced = PilotRequestTracker.MaxFollowUps;
        SimScenarioState scenario = NoStarScenario();
        scenario.ElapsedSeconds = 400;

        PilotProactive.TickArrivalApproachRequest(aircraft, scenario, Airport);

        PilotPendingRequest approach = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Approach, approach.Kind);
        Assert.Equal(400, approach.LastRequestedAtSeconds);
        Assert.Single(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void PresetDescent_DoesNotStamp_FirstRequestAtOneThousandAbove()
    {
        // A scripted DM is not the student's descent clearance: no wait starts, and the pilot asks at the first request's
        // 1,000 ft, not 180 s on and 2,000 ft above.
        Staged? staged = StageBeforeTopOfDescent();
        Assert.NotNull(staged);
        SimScenarioState scenario = staged.Engine.Scenario!;
        scenario.PresetQueue.Add(
            new ScheduledPreset
            {
                Callsign = staged.Aircraft.Callsign,
                Command = "DM 140",
                FireAtSeconds = scenario.ElapsedSeconds,
            }
        );
        staged.Engine.TickOneSecond();
        Assert.Empty(scenario.PresetQueue);
        Assert.Null(staged.Aircraft.DescentClearedAtSeconds);

        (double highestSilent, double? askedAbove) = FlyUntilAsked(staged, 1800);
        _output.WriteLine($"Highest silent {highestSilent:F0} ft above the profile, asked at {askedAbove:F0}");
        Assert.NotNull(askedAbove);
        Assert.Null(staged.Aircraft.DescentClearedAtSeconds);
        Assert.InRange(askedAbove.Value, FirstRequestAboveProfileFt - OneSecondToleranceFt, FirstRequestAboveProfileFt + OneSecondToleranceFt);
    }

    [Fact]
    public void StudentDescent_AfterInitialContact_Stamps()
    {
        Staged? staged = StageBeforeTopOfDescent();
        Assert.NotNull(staged);
        double now = staged.Engine.Scenario!.ElapsedSeconds;
        SendOk(staged, "DM 140");
        Assert.Equal(now, staged.Aircraft.DescentClearedAtSeconds);
    }

    [Fact]
    public void StudentDescent_BeforeInitialContact_DoesNotStamp()
    {
        Staged? staged = Stage(
            pastTopOfDescent: false,
            solo: true,
            positionType: "APP",
            beforeSolo: ac => ac.HasMadeInitialContact = false,
            command: null
        );
        Assert.NotNull(staged);
        SendOk(staged, "DM 140");
        Assert.False(staged.Aircraft.HasMadeInitialContact, "The DM itself marked the pilot's initial contact");
        Assert.Null(staged.Aircraft.DescentClearedAtSeconds);
    }

    [Fact]
    public void DescentRequestState_SurvivesASnapshotRoundTrip()
    {
        AircraftState aircraft = NoStarArrival(25.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        aircraft.DescentClearedAtSeconds = 123.0;
        PilotPendingRequest request = PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Descent,
            100,
            RequestLowerLine,
            PilotRequestContext.Facility("approach")
        );
        request.FollowUpsVoiced = 2;
        request.ReopenedAfterCap = true;
        request.AskedInStepDownBand = true;
        aircraft.DescentRequestCap = DescentRequestCap.Reopenable;
        aircraft.DescentRequestCappedAtSeconds = 240.0;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), null);

        Assert.Equal(123.0, restored.DescentClearedAtSeconds);
        Assert.Equal(DescentRequestCap.Reopenable, restored.DescentRequestCap);
        Assert.Equal(240.0, restored.DescentRequestCappedAtSeconds);
        PilotPendingRequest restoredRequest = Assert.IsType<PilotPendingRequest>(restored.PendingPilotRequest);
        Assert.Equal(2, restoredRequest.FollowUpsVoiced);
        Assert.True(restoredRequest.ReopenedAfterCap);
        Assert.True(restoredRequest.AskedInStepDownBand);
    }

    [Fact]
    public void DescentRequestState_AbsentFromTheSnapshot_RestoresTheDefaults()
    {
        AircraftState aircraft = NoStarArrival(25.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        PilotRequestTracker.RecordRequest(aircraft, PilotPendingRequestKind.Descent, 100, RequestLowerLine, PilotRequestContext.Facility("approach"));
        AircraftSnapshotDto dto = aircraft.ToSnapshot();

        string json = JsonSerializer.Serialize(dto.PendingPilotRequest);
        Assert.DoesNotContain(nameof(PilotPendingRequestDto.FollowUpsVoiced), json);
        Assert.DoesNotContain(nameof(PilotPendingRequestDto.ReopenedAfterCap), json);
        Assert.DoesNotContain(nameof(PilotPendingRequestDto.AskedInStepDownBand), json);
        var restoredRequest = PilotPendingRequest.FromSnapshot(
            Assert.IsType<PilotPendingRequestDto>(JsonSerializer.Deserialize<PilotPendingRequestDto>(json))
        );
        Assert.Equal(0, restoredRequest.FollowUpsVoiced);
        Assert.False(restoredRequest.ReopenedAfterCap);
        Assert.False(restoredRequest.AskedInStepDownBand);

        // The aircraft-level keys are left out of the JSON at their defaults, and JSON without them restores none.
        string aircraftJson = JsonSerializer.Serialize(dto, RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AircraftSnapshotDto.DescentClearedAtSeconds), aircraftJson);
        Assert.DoesNotContain(nameof(AircraftSnapshotDto.DescentRequestCap), aircraftJson);
        Assert.DoesNotContain(nameof(AircraftSnapshotDto.DescentRequestCappedAtSeconds), aircraftJson);
        AircraftSnapshotDto fromJson = Assert.IsType<AircraftSnapshotDto>(
            JsonSerializer.Deserialize<AircraftSnapshotDto>(aircraftJson, RecordingJsonOptions.Default)
        );
        var restoredAircraft = AircraftState.FromSnapshot(fromJson, null);
        Assert.Null(restoredAircraft.DescentClearedAtSeconds);
        Assert.Equal(DescentRequestCap.None, restoredAircraft.DescentRequestCap);
        Assert.Null(restoredAircraft.DescentRequestCappedAtSeconds);
    }

    [Fact]
    public void ReopenAfterCap_LevelJetAtRealGroundSpeed_ReopensExactlyOneTwentyAfterTheLastRepeat()
    {
        // The OAKES3 jet flying level at its own ground speed asks at 1,000 ft above the profile (the 1,000 ft band),
        // repeats at 120 s and 240 s and is capped. Gaining on the profile it is soon in the 2,000 ft band, but it re-opens
        // only 120 s after the last repeat, exactly.
        Staged? staged = StageBeforeTopOfDescent();
        Assert.NotNull(staged);
        AircraftState aircraft = staged.Aircraft;
        PilotTransmissionLog log = new(aircraft);
        Assert.True(TickUntilRequests(staged, log, 1, 1800), "No request for lower within 1800 s");
        PilotPendingRequest first = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.False(first.AskedInStepDownBand);
        double askedAt = first.LastRequestedAtSeconds;

        Assert.True(TickUntilRequests(staged, log, 3, 260), "No second repeat within 260 s");
        double cappedAt = askedAt + (2 * PilotRequestTracker.NormalFollowUpDelaySeconds);
        Assert.Equal(cappedAt, first.LastRequestedAtSeconds, 3);
        Assert.Equal(DescentRequestCap.Reopenable, aircraft.DescentRequestCap);
        Assert.Equal(cappedAt, aircraft.DescentRequestCappedAtSeconds);

        for (int t = 0; (t < 400) && ReferenceEquals(first, aircraft.PendingPilotRequest); t++)
        {
            staged.Engine.TickOneSecond();
            log.Observe();
        }

        PilotPendingRequest reopened = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.NotSame(first, reopened);
        Assert.Equal(PilotPendingRequestKind.Descent, reopened.Kind);
        Assert.True(reopened.ReopenedAfterCap);
        Assert.Equal(cappedAt + 120.0, reopened.LastRequestedAtSeconds, 3);
        Assert.True(AboveProfileFt(aircraft) >= StepDownRequestAboveProfileFt, "Re-opened below the 2,000 ft band");
        Assert.Equal(DescentRequestCap.Spent, aircraft.DescentRequestCap);
    }

    /// <summary>
    /// The B738 21 NM from KOAK at <paramref name="altitudeFt"/>, its last request for lower capped with
    /// <paramref name="cap"/>, last repeated at 0 s.
    /// </summary>
    private static AircraftState CappedAt21Nm(DescentRequestCap cap, double altitudeFt)
    {
        AircraftState aircraft = NoStarArrival(21.0, indicatedAirspeed: 240, altitude: altitudeFt, type: "B738");
        aircraft.DescentRequestCap = cap;
        aircraft.DescentRequestCappedAtSeconds = 0;
        return aircraft;
    }

    [Fact]
    public void ReopenableCap_TwoThousandAbove_119sAfterTheLastRepeat_NoAsk()
    {
        // 9,000 - (9 + 300 x 21) = 2,691 ft above the profile.
        AircraftState aircraft = CappedAt21Nm(DescentRequestCap.Reopenable, 9000);
        TickDescentRequestAt(aircraft, NoStarScenario(), 119);
        Assert.Null(aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void ReopenableCap_TwoThousandAbove_120sAfterTheLastRepeat_Reopens()
    {
        AircraftState aircraft = CappedAt21Nm(DescentRequestCap.Reopenable, 9000);
        TickDescentRequestAt(aircraft, NoStarScenario(), 120);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Descent, request.Kind);
        Assert.True(request.ReopenedAfterCap);
        Assert.Equal(120, request.LastRequestedAtSeconds);
        Assert.Equal(DescentRequestCap.Spent, aircraft.DescentRequestCap);
    }

    [Fact]
    public void ReopenableCap_NineteenHundredAbove_300sAfterTheLastRepeat_NoAsk()
    {
        // 8,209 - (9 + 300 x 21) = 1,900 ft above the profile: below the 2,000 ft band.
        AircraftState aircraft = CappedAt21Nm(DescentRequestCap.Reopenable, 8209);
        TickDescentRequestAt(aircraft, NoStarScenario(), 300);
        Assert.Null(aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void SpentCap_NoAsk()
    {
        AircraftState aircraft = CappedAt21Nm(DescentRequestCap.Spent, 9000);
        TickDescentRequestAt(aircraft, NoStarScenario(), 600);
        Assert.Null(aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void DescentClearanceStamp_ClearsTheCap_StepDownRulesApply()
    {
        // A descent clearance at 10 s clears the cap and its time; the pilot then asks by the rules after any descent
        // clearance: not before 180 s after it, then from 2,000 ft above the profile (2,691 ft here), as a fresh request.
        AircraftState aircraft = CappedAt21Nm(DescentRequestCap.Reopenable, 9000);
        CompoundCommand descend = new([new ParsedBlock(null, [new DescendMaintainCommand(8000)])]);
        PilotRequestTracker.StampDescentClearance(aircraft, descend, 10);
        Assert.Equal(DescentRequestCap.None, aircraft.DescentRequestCap);
        Assert.Null(aircraft.DescentRequestCappedAtSeconds);
        Assert.Equal(10, aircraft.DescentClearedAtSeconds);

        SimScenarioState scenario = NoStarScenario();
        TickDescentRequestAt(aircraft, scenario, 189);
        Assert.Null(aircraft.PendingPilotRequest);
        TickDescentRequestAt(aircraft, scenario, 190);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.False(request.ReopenedAfterCap);
        Assert.True(request.AskedInStepDownBand);
    }

    private const string S3Fat3RecordingPath = "TestData/s3-fat-3-star-end-recording.yaat-bug-report-bundle.zip";

    [Fact]
    public void S3Fat3Recording_SoloRunForward_PinnedRequestCounts()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(S3Fat3RecordingPath);
        Assert.NotNull(archive);
        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            var groundData = RecordingGroundData.Evergreen("descent requests over the S3 FAT 3 session");
            SimLogBuilder.CreateForTest(_output).InitializeSimLog();
            SimulationEngine engine = new(groundData, PushPlanStore.CreateShipped());
            engine.Replay(recording, 0);
            SimScenarioState scenario = Assert.IsType<SimScenarioState>(engine.Scenario);
            scenario.SoloTrainingMode = true;
            Assert.True(scenario.PilotContacts.AnyAnswering, "Nobody answers pilots in the recording");
            Assert.Equal("APP", scenario.StudentPositionType);

            // A line already spoken at t=0 is not counted: only the transmissions of the 600 s run forward.
            var lastAt = engine
                .World.GetSnapshot()
                .ToDictionary(aircraft => aircraft.Callsign, aircraft => aircraft.LastPilotTransmission?.AtSeconds, StringComparer.Ordinal);
            Dictionary<string, int> requestsLower = new(StringComparer.Ordinal);
            Dictionary<string, int> approachRequests = new(StringComparer.Ordinal);
            for (int t = 1; t <= 600; t++)
            {
                engine.FastForwardTo(t, recording.Actions);
                foreach (AircraftState aircraft in engine.World.GetSnapshot())
                {
                    CountRequestCalls(aircraft, lastAt, requestsLower, approachRequests);
                }
            }

            // Pinned from a run. Before this change (no re-ask cap), counted the same way: request lower N160TM=1,
            // N2382V=4; approach N160TM=1, N172YY=1.
            Assert.Equal("N160TM=1, N2382V=2", Counts(requestsLower));
            Assert.Equal("N160TM=1, N172YY=1", Counts(approachRequests));
        }
    }

    private static string Counts(Dictionary<string, int> counts) =>
        string.Join(", ", counts.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"));

    private const string ApproachRequestTerminal = "request approach assignment.";

    private static void CountRequestCalls(
        AircraftState aircraft,
        Dictionary<string, double?> lastAt,
        Dictionary<string, int> requestsLower,
        Dictionary<string, int> approachRequests
    )
    {
        if ((aircraft.LastPilotTransmission is not { } line) || (lastAt.GetValueOrDefault(aircraft.Callsign) == line.AtSeconds))
        {
            return;
        }

        lastAt[aircraft.Callsign] = line.AtSeconds;
        if (line.Text.EndsWith(RequestLowerTerminal, StringComparison.Ordinal))
        {
            requestsLower[aircraft.Callsign] = requestsLower.GetValueOrDefault(aircraft.Callsign) + 1;
        }
        else if (line.Text.EndsWith(ApproachRequestTerminal, StringComparison.Ordinal))
        {
            approachRequests[aircraft.Callsign] = approachRequests.GetValueOrDefault(aircraft.Callsign) + 1;
        }
    }

    private static readonly PilotSpeechText RequestLowerLine = new(RequestLowerTerminal, "approach, united three two five, request lower.");

    [Fact]
    public void RequestLower_NotBeforeOneThousandFeetAboveTheProfile()
    {
        // The pilot asks on the first second it is 1,000 ft above the profile: within one second's gain of it either side.
        Staged? staged = StageBeforeTopOfDescent();
        Assert.NotNull(staged);

        (double highestSilent, double? askedAbove) = FlyUntilAsked(staged, 1800);
        _output.WriteLine($"Highest silent {highestSilent:F0} ft above the profile, asked at {askedAbove:F0}");
        Assert.NotNull(askedAbove);
        Assert.True(highestSilent < FirstRequestAboveProfileFt + OneSecondToleranceFt, $"Silent at {highestSilent:F0} ft above the profile");
        Assert.InRange(askedAbove.Value, FirstRequestAboveProfileFt - OneSecondToleranceFt, FirstRequestAboveProfileFt + OneSecondToleranceFt);
    }

    [Fact]
    public void RequestLower_AfterStepDown_WaitsOneEightyAndTwoThousandAbove()
    {
        (Staged staged, PilotTransmissionLog log) = Assert.NotNull(StageAfterCall());
        SendOk(staged, "DM 140");
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, staged.Aircraft.PendingPilotRequest!.ResponseState);
        AssertAsksOnlyAfterTheStepDownWait(staged, log, staged.Engine.Scenario!.ElapsedSeconds, 2);
    }

    [Fact]
    public void RequestLower_AfterUnpromptedStepDown_WaitsOneEightyAndTwoThousandAbove()
    {
        Staged? staged = StageBeforeTopOfDescent();
        Assert.NotNull(staged);

        // A descent the controller gives before the pilot asks starts the same wait.
        PilotTransmissionLog log = new(staged.Aircraft);
        Assert.Null(staged.Aircraft.PendingPilotRequest);
        SendOk(staged, "DM 140");
        AssertAsksOnlyAfterTheStepDownWait(staged, log, staged.Engine.Scenario!.ElapsedSeconds, 1);
    }

    [Fact]
    public void RequestLower_StarAltitudeAhead_IsTheLowerLimit()
    {
        // A B738 at 240 KIAS 5 NM short of a real OAKES3 fix, on a route of that fix alone, with its published window from
        // the navdata: the first OAKES3 fix (common legs, then transitions by name) at least 16 NM from KOAK (outside the
        // 10 NM floor) whose window top is low enough for its published-altitude line to lie below the field line. The field
        // line is KOAK's elevation + 300 ft per NM of 5 NM plus the fix to the field; the published-altitude line is the
        // window's top + 300 x 5. The aircraft flies 500 ft above the field line (no request), which is at least 1,000 ft
        // above the published-altitude line: on a STAR that lower line asks; off one, the fix's altitude is not a descent
        // plan and nothing is asked.
        Assert.False(CallsWithStarFixAhead(onStar: false), "A request off a STAR, where the field line is the only limit");
        Assert.True(CallsWithStarFixAhead(onStar: true), "No request on a STAR 1,000 ft or more above the published-altitude line");
    }

    private bool CallsWithStarFixAhead(bool onStar)
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        LatLon koak = Assert.IsType<LatLon>(Airport("KOAK"));
        double elevationFt = Assert.IsType<double>(NavigationDatabase.Instance.GetAirportElevation("KOAK"));
        (CifpLeg leg, CifpAltitudeRestriction window, LatLon fix) = StarFixes(star)
            .First(c =>
                (GeoMath.DistanceNm(c.Fix, koak) >= 16.0)
                && ((elevationFt + (ProfileFtPerNm * GeoMath.DistanceNm(c.Fix, koak)) - c.Window.Altitude1Ft) >= 600.0)
            );
        double fieldLineFt = elevationFt + (ProfileFtPerNm * (5.0 + GeoMath.DistanceNm(fix, koak)));
        double starLineFt = window.Altitude1Ft + (ProfileFtPerNm * 5.0);
        double altitudeFt = fieldLineFt + 500.0;
        _output.WriteLine(
            $"{leg.FixIdentifier} {window.Type} {window.Altitude1Ft}/{window.Altitude2Ft}: field line {fieldLineFt:F0}, STAR line {starLineFt:F0}"
        );
        Assert.True((altitudeFt - starLineFt) >= FirstRequestAboveProfileFt, "The staging is not 1,000 ft above the published-altitude line");

        AircraftState aircraft = NoStarArrival(0.0, indicatedAirspeed: 240, altitude: altitudeFt, type: "B738");
        aircraft.Position = GeoMath.ProjectPoint(fix, new TrueHeading(90), 5.0);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = leg.FixIdentifier,
                Position = fix,
                AltitudeRestriction = window,
            }
        );
        aircraft.Procedure.ActiveStarId = onStar ? "OAKES3" : null;
        PilotProactive.TickDescentRequest(aircraft, NoStarScenario(), Airport);
        return aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent };
    }

    /// <summary>
    /// Flies on after a descent clearance at <paramref name="clearedAt"/> until the next request for lower: it comes no
    /// sooner than 180 s after the clearance nor below 2,000 ft above the profile, and no later than a few seconds after
    /// both hold. <paramref name="expectedCount"/> is the number of requests the log holds then.
    /// </summary>
    private void AssertAsksOnlyAfterTheStepDownWait(Staged staged, PilotTransmissionLog log, double clearedAt, int expectedCount)
    {
        AircraftState aircraft = staged.Aircraft;
        int dueFor = 0;
        for (int t = 0; t < 1800; t++)
        {
            Tick(staged, log, 1);
            double since = staged.Engine.Scenario!.ElapsedSeconds - clearedAt;
            double above = AboveProfileFt(aircraft);
            if (aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent, IsOpen: true })
            {
                _output.WriteLine($"Asked {since:F0} s after the clearance, {above:F0} ft above the profile, at {aircraft.Altitude:F0}");
                Assert.True(since >= StepDownWaitSeconds, $"Asked {since:F0} s after the descent clearance");
                Assert.True(above >= StepDownRequestAboveProfileFt - OneSecondToleranceFt, $"Asked {above:F0} ft above the profile");
                Assert.True(TickUntilRequests(staged, log, expectedCount, 30), "The recorded request was not voiced");
                return;
            }

            dueFor = ((since >= StepDownWaitSeconds + 1) && (above >= StepDownRequestAboveProfileFt + OneSecondToleranceFt)) ? dueFor + 1 : 0;
            Assert.True(dueFor < 5, $"No request {since:F0} s after the clearance at {above:F0} ft above the profile");
        }

        Assert.Fail("No request for lower within 1800 s of the descent clearance");
    }

    /// <summary>
    /// Ticks one second at a time until a request for lower is recorded: the highest the aircraft was above the profile
    /// while silent, and how high it was above it when it asked (null when it never asked).
    /// </summary>
    private static (double HighestSilent, double? AskedAbove) FlyUntilAsked(Staged staged, int maxSeconds)
    {
        double highestSilent = double.MinValue;
        for (int t = 0; t < maxSeconds; t++)
        {
            staged.Engine.TickOneSecond();
            double above = AboveProfileFt(staged.Aircraft);
            if (staged.Aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent })
            {
                return (highestSilent, above);
            }

            highestSilent = Math.Max(highestSilent, above);
        }

        return (highestSilent, null);
    }

    [Fact]
    public void AfterCall_NoAnswer_ReasksAfter120s()
    {
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

        (Staged staged, PilotTransmissionLog log) = after;
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(staged.Aircraft.PendingPilotRequest);
        double askedAt = request.LastRequestedAtSeconds;

        Tick(staged, log, 110);
        Assert.Single(log.RequestsLower);

        Assert.True(TickUntilRequests(staged, log, 2, 30), "No re-ask within 140 s of an unanswered request");
        Assert.Equal(askedAt + PilotRequestTracker.NormalFollowUpDelaySeconds, request.LastRequestedAtSeconds, 3);
    }

    [Fact]
    public void AfterCall_UnrelatedHeading_LeavesItOpen()
    {
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

        (Staged staged, _) = after;
        SendOk(staged, "FH 270");

        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(staged.Aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Descent, request.Kind);
        Assert.Equal(PilotPendingRequestResponseState.None, request.ResponseState);
    }

    [Fact]
    public void SteppedDown_LevelAgain_PastNextTop_RequestsAgain()
    {
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

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
    public void NoStar_AsksOneThousandAboveTheFieldProfile()
    {
        // A B738 level at 9,000 ft and 240 KIAS inbound to KOAK (field elevation 9 ft) on an empty route: the profile is
        // 9 ft + 300 ft per NM from the field, and the pilot asks once 1,000 ft above it, at (9,000 - 1,000 - 9) / 300 =
        // 26.64 NM. No slowdown below 10,000 ft. 1 NM outside that it is 700 ft above the profile, 1 NM inside 1,300 ft.
        const double askFromFieldNm = (9000.0 - 1000.0 - 9.0) / 300.0;

        AircraftState early = NoStarArrival(askFromFieldNm + 1.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
        PilotProactive.TickDescentRequest(early, NoStarScenario(), Airport);
        Assert.Empty(early.PendingPilotTransmissions);
        Assert.Null(early.PendingPilotRequest);

        AircraftState onTime = NoStarArrival(askFromFieldNm - 1.0, indicatedAirspeed: 240, altitude: 9000, type: "B738");
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
        // A B763 level at 16,000 ft inbound to KOAK on an empty route: the profile is 9 ft + 300 ft per NM from the field,
        // and the pilot asks 1,000 ft above it, (16,000 - 1,000 - 9) / 300 = 49.97 NM out at 240 KIAS. At 280 KIAS the
        // descent crosses 10,000 ft above 250 KIAS and needs 10 NM more to slow, so the profile is 3,000 ft lower and the
        // pilot asks 59.97 NM out. 1 NM either side of each point is 300 ft either side of the threshold.
        const double slowAskNm = (16000.0 - 1000.0 - 9.0) / 300.0;
        const double fastAskNm = slowAskNm + 10.0;

        Assert.True(CallsAt(fastAskNm - 1.0, indicatedAirspeed: 280), "280 KIAS: no call 1,300 ft above the profile");
        Assert.False(CallsAt(fastAskNm + 1.0, indicatedAirspeed: 280), "280 KIAS: a call 700 ft above the profile");
        Assert.True(CallsAt(slowAskNm - 1.0, indicatedAirspeed: 240), "240 KIAS: no call 1,300 ft above the profile");
        Assert.False(CallsAt(slowAskNm + 1.0, indicatedAirspeed: 240), "240 KIAS: a call 700 ft above the profile");
        Assert.False(CallsAt(fastAskNm - 1.0, indicatedAirspeed: 240), "240 KIAS called 10 NM early, as if slowing down");
    }

    private static bool CallsAt(double distanceFromFieldNm, double indicatedAirspeed)
    {
        AircraftState aircraft = NoStarArrival(distanceFromFieldNm, indicatedAirspeed, altitude: 16000, type: "B763");
        PilotProactive.TickDescentRequest(aircraft, NoStarScenario(), Airport);
        return aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent };
    }

    /// <summary>
    /// Every leg of <paramref name="star"/> with a published altitude and a fix position, in order: the common legs, then
    /// the enroute and runway transitions by name.
    /// </summary>
    private static IEnumerable<(CifpLeg Leg, CifpAltitudeRestriction Window, LatLon Fix)> StarFixes(CifpStarProcedure star)
    {
        IEnumerable<CifpLeg> legs = star
            .CommonLegs.Concat(star.EnrouteTransitions.OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t => t.Value.Legs))
            .Concat(star.RunwayTransitions.OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t => t.Value.Legs));
        foreach (CifpLeg leg in legs)
        {
            if ((leg.Altitude is { } window) && (FixPosition(leg) is { } fix))
            {
                yield return (leg, window, fix);
            }
        }
    }

    private static LatLon? FixPosition(CifpLeg leg) =>
        leg.ResolveFixPosition(NavigationDatabase.Instance) is { } position ? new LatLon(position.Lat, position.Lon) : null;

    private static LatLon FromField(string field, double distanceNm) =>
        GeoMath.ProjectPoint(Assert.IsType<LatLon>(Airport(field)), new TrueHeading(90), distanceNm);

    private static AircraftState NoStarArrival(
        double distanceFromFieldNm,
        double indicatedAirspeed,
        double altitude,
        string type,
        string destination = "KOAK"
    )
    {
        return new AircraftState
        {
            Callsign = "UAL325",
            AircraftType = type,
            Position = FromField(destination, distanceFromFieldNm),
            TrueHeading = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = indicatedAirspeed,
            IsOnGround = false,
            HasMadeInitialContact = true,
            HasControllerAcknowledgedInitialContact = true,
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = destination },
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
        Staged? staged = StagePastTopOfDescentAfter(command);
        Assert.NotNull(staged);

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
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

        (Staged staged, _) = after;
        Assert.True(staged.Aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Descent, IsOpen: true });
        SendOk(staged, command);
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, staged.Aircraft.PendingPilotRequest!.ResponseState);
    }

    private void AssertNoRepeatAfter(string command)
    {
        (Staged Staged, PilotTransmissionLog Log) after = Assert.NotNull(StageAfterCall());

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

    /// <summary>How far the aircraft is above its descent profile: the lower of <see cref="FieldLineFt"/> and <see cref="StarLineFt"/>.</summary>
    private static double AboveProfileFt(AircraftState aircraft)
    {
        double field = FieldLineFt(aircraft);
        return aircraft.Altitude - (StarLineFt(aircraft) is { } star ? Math.Min(field, star) : field);
    }

    /// <summary>The field elevation plus 300 ft per NM of the along-route distance to the destination.</summary>
    private static double FieldLineFt(AircraftState aircraft)
    {
        string destination = aircraft.FlightPlan.Destination;
        LatLon field = Assert.IsType<LatLon>(Airport(destination));
        double elevationFt = Assert.IsType<double>(NavigationDatabase.Instance.GetAirportElevation(destination));
        double along = 0;
        LatLon previous = aircraft.Position;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            along += GeoMath.DistanceNm(previous, fix.Position);
            previous = fix.Position;
        }

        return Line(aircraft, elevationFt, along + GeoMath.DistanceNm(previous, field));
    }

    /// <summary>On a STAR, the first published altitude ahead below the aircraft plus 300 ft per NM back from its fix; else null.</summary>
    private static double? StarLineFt(AircraftState aircraft)
    {
        if (string.IsNullOrEmpty(aircraft.Procedure.ActiveStarId))
        {
            return null;
        }

        double along = 0;
        LatLon previous = aircraft.Position;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            along += GeoMath.DistanceNm(previous, fix.Position);
            previous = fix.Position;
            if ((fix.AltitudeRestriction is { } r) && (r.Altitude1Ft < aircraft.Altitude - FlightPhysics.AltitudeSnapFt))
            {
                return Line(aircraft, r.Altitude1Ft, along);
            }
        }

        return null;
    }

    /// <summary>
    /// The profile at <paramref name="distanceNm"/> back from where it reaches <paramref name="targetFt"/>, 10 NM nearer
    /// the target while the descent crosses 10,000 ft above 250 KIAS (the distance it takes to slow).
    /// </summary>
    private static double Line(AircraftState aircraft, double targetFt, double distanceNm)
    {
        bool slows = (aircraft.Altitude > 10000) && (targetFt < 10000) && (aircraft.IndicatedAirspeed > 250);
        return targetFt + (ProfileFtPerNm * Math.Max(0.0, distanceNm - (slows ? 10.0 : 0.0)));
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
