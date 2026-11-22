using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// The in-phase Class B boundary trigger (<see cref="PilotProactive.TickAirspaceBoundaryRespect"/> with a running phase): an
/// uncleared solo VFR aircraft whose running suspendable phase would carry it laterally into the SFO Class B within the 60 s
/// ray gets an orbit in front of that phase (<see cref="PhaseList.InsertHoldInFront"/>) and says so once, recording its Class B
/// request (none under RMOB); a Pending phase, a hold already current, a phase that cannot be suspended, an RPO session, a
/// cleared aircraft, a Charlie and a vertical entry get nothing; an approach-navigation leg inside the published FAF gets
/// nothing; below 400 ft AGL the pilot says "unable to hold, continuing." once per encounter; and the orbit waits for the
/// pilot to be able to speak. Real navdata: a VFR C172 west of the SFO Class B ring at 7500 ft eastbound, or just outside
/// the SFO surface area heading into it.
/// </summary>
public sealed class BoundaryHoldTriggerTests : IDisposable
{
    private const string Callsign = "N123AB";
    private const string Sfo = "SFO";
    private const string Advisory = "that'll take us into the bravo; we'll orbit";
    private const string Unable = "unable to hold, continuing.";

    /// <summary>West of the SFO Class B's outer ring at 7500 ft: east is into it, west keeps outside.</summary>
    private static readonly LatLon NearWestRing = new(37.62, -122.885);

    private static readonly LatLon Ksfo = new(37.6213, -122.3790);

    private readonly IDisposable _navScope;
    private readonly ITestOutputHelper _output;

    public BoundaryHoldTriggerTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    [Fact]
    public void S1_PatternEntryIntoTheBravo_ThroughTheEngineTick_OrbitsInFrontOfIt_AndRequestsTheBravo()
    {
        AircraftState ac = WestOfTheRing();
        PatternEntryPhase entry = OakPatternEntry();
        ac.Phases!.Add(entry);
        (SimulationEngine engine, _) = SoloEngine(ac);

        for (int t = 0; (t < 5) && (ac.Phases.HeldPhase is null); t++)
        {
            engine.TickOneSecond();
        }

        Assert.Same(entry, ac.Phases.HeldPhase);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
    }

    [Fact]
    public void S1_PatternEntryIntoTheBravo_InsertsThePair_AndSaysTheAdvisoryOnce_WithTheRequestRecorded()
    {
        AircraftState ac = WestOfTheRing();
        PatternEntryPhase entry = Active(OakPatternEntry());
        ac.Phases!.Add(entry);
        SimScenarioState scenario = SoloScenario();
        PhaseContext ctx = Context(ac, Oak28R());

        Tick(ac, scenario, ctx);
        Tick(ac, scenario, ctx);

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(AirspaceHoldMode.Orbit, hold.Mode);
        Assert.Equal(AirspaceClass.Bravo, hold.AirspaceClass);
        Assert.Same(entry, ac.Phases.HeldPhase);
        PilotTransmission advisory = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Proactive, advisory.Kind);
        string direction = hold.OrbitDirection == TurnDirection.Left ? "left" : "right";
        Assert.Equal($"that'll take us into the bravo; we'll orbit {direction} here, request bravo clearance.", advisory.Text);
        Assert.EndsWith($"we'll orbit {direction} here, request bravo clearance.", advisory.SpeechText);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.NotNull(ac.PendingPilotRequest?.BravoWording);
    }

    [Fact]
    public void S1_ApproachNavigationOutsideTheFaf_Inserts_AndS11_InsideThePublishedFaf_InsertsNothingAndSaysNothing()
    {
        (AircraftState inside, RunwayInfo runway, RunwayInfo opposite) = OnAFinalInsideTheFafHeadingIntoTheSurfaceArea();
        inside.Phases!.Add(Active(ApproachNavigation()));
        Tick(inside, SoloScenario(), Context(inside, runway));

        Assert.IsType<ApproachNavigationPhase>(inside.Phases.CurrentPhase);
        Assert.Empty(inside.PendingPilotTransmissions);
        Assert.Null(inside.InPhaseHoldUnableArea);

        (AircraftState outside, _, _) = OnAFinalInsideTheFafHeadingIntoTheSurfaceArea();
        ApproachNavigationPhase nav = Active(ApproachNavigation());
        outside.Phases!.Add(nav);
        // The same aircraft read against the opposite runway end is not on its final: outside its FAF.
        Assert.False(FinalApproachFix.IsInsidePublishedFaf(outside, opposite, null));
        Tick(outside, SoloScenario(), Context(outside, opposite));

        Assert.IsType<AirspaceBoundaryHoldPhase>(outside.Phases.CurrentPhase);
        Assert.Same(nav, outside.Phases.HeldPhase);
        Assert.Contains(Advisory, Assert.Single(outside.PendingPilotTransmissions).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void S2_DownwindIntoTheBravo_InsertsThePair()
    {
        AircraftState ac = WestOfTheRing();
        var downwind = new DownwindPhase { Status = PhaseStatus.Active };
        ac.Phases!.Add(downwind);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(downwind, ac.Phases.HeldPhase);
    }

    [Fact]
    public void S2_FinalApproachInsideTheFafAbove400FtAgl_InsertsThePair()
    {
        (AircraftState ac, RunwayInfo runway, _) = OnAFinalInsideTheFafHeadingIntoTheSurfaceArea();
        var final = new FinalApproachPhase { Status = PhaseStatus.Active };
        ac.Phases!.Add(final);

        Tick(ac, SoloScenario(), Context(ac, runway));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(final, ac.Phases.HeldPhase);
    }

    [Fact]
    public void S3_APhaseThatCannotBeSuspended_GetsNoHold()
    {
        AircraftState ac = WestOfTheRing();
        var lowApproach = new LowApproachPhase { Status = PhaseStatus.Active };
        Assert.False(lowApproach.CanSuspendForBoundaryHold(Context(ac, Oak28R())));
        ac.Phases!.Add(lowApproach);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.Same(lowApproach, Assert.Single(ac.Phases.Phases));
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void S4_AHoldAlreadyCurrent_GetsNoSecondHold()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(OakPatternEntry()));
        var standing = new AirspaceBoundaryHoldPhase
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = Sfo,
            NameText = Sfo,
            ReferencePosition = Ksfo,
            Mode = AirspaceHoldMode.Orbit,
        };
        ac.Phases.InsertHoldInFront(standing);
        PhaseRunner.Tick(ac, Context(ac, Oak28R()));
        Assert.Equal(PhaseStatus.Active, standing.Status);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.Equal(2, ac.Phases.Phases.Count);
        Assert.Same(standing, ac.Phases.CurrentPhase);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void ABravoBehindANearerCharlie_StillHoldsThePhase()
    {
        AircraftState ac = BehindANearerCharlie();
        PatternEntryPhase entry = Active(OakPatternEntry());
        ac.Phases!.Add(entry);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(AirspaceClass.Bravo, hold.AirspaceClass);
        Assert.Same(entry, ac.Phases.HeldPhase);
    }

    [Fact]
    public void FafGate_WithNoRunwayInTheContext_CanSuspend()
    {
        (AircraftState ac, RunwayInfo runway, _) = OnAFinalInsideTheFafHeadingIntoTheSurfaceArea();

        Assert.True(ApproachNavigation().CanSuspendForBoundaryHold(ContextWithoutRunway(ac, runway.ElevationFt)));
    }

    [Fact]
    public void FafGate_DoesNotApplyToAMissedApproachLeg_InsideTheFaf()
    {
        (AircraftState ac, RunwayInfo runway, _) = OnAFinalInsideTheFafHeadingIntoTheSurfaceArea();
        var missed = new ApproachNavigationPhase { Fixes = [], IsMissedApproach = true };
        Assert.True(FinalApproachFix.IsInsidePublishedFaf(ac, runway, null));

        Assert.True(missed.CanSuspendForBoundaryHold(Context(ac, runway)));
    }

    [Fact]
    public void S6_APendingCurrentPhase_GetsNoHoldThisTick()
    {
        AircraftState ac = WestOfTheRing();
        PatternEntryPhase entry = OakPatternEntry();
        ac.Phases!.Add(entry);
        Assert.Equal(PhaseStatus.Pending, entry.Status);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.Same(entry, Assert.Single(ac.Phases.Phases));
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void S7_AnRpoSession_GetsNoHold()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(OakPatternEntry()));
        SimScenarioState rpo = SoloScenario();
        rpo.SoloTrainingMode = false;

        Tick(ac, rpo, Context(ac, Oak28R()));

        Assert.Single(ac.Phases.Phases);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void S8_ClearedIntoThisBravo_GetsNoHold()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(OakPatternEntry()));
        ac.GrantBravoClearance(Sfo);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.Single(ac.Phases.Phases);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void S9_ACharlieAheadInAPhase_GetsNoHold()
    {
        // Over San Francisco, eastbound at 2000 ft: 600 kt stretches the 60 s ray to 10 nm, so that it reaches the Oakland
        // Charlie from here, an unrealistic speed taken only for the ray's length. Any Class B the ray meets on the way it
        // meets from inside its footprint, a vertical entry that never holds a phase, so the Charlie is all there is.
        AircraftState ac = Airborne(new LatLon(37.7213, -122.4200), trueHeading: 90, altitude: 2000);
        ac.IndicatedAirspeed = 600;
        ac.HasControllerAcknowledgedInitialContact = false;
        Assert.Equal(AirspaceClass.Charlie, AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60)?.Volume.Class);
        AirspaceBoundaryCrossing? bravo = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo);
        Assert.True((bravo is null) || bravo.Volume.ContainsLateral(ac.Position));
        ac.Phases!.Add(Active(OakPatternEntry()));

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.Single(ac.Phases.Phases);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void S12_AVerticalEntryFromInsideTheFootprint_GetsNoHold()
    {
        // Under an SFO shelf, laterally inside it, climbing into it: the cap's to handle, never a lateral orbit.
        LatLon inside = GeoMath.ProjectPoint(FirstPointOutside(Ksfo, 90, v => v.LowerFtMsl == 0), new TrueHeading(90), 0.5);
        AircraftState ac = Airborne(inside, trueHeading: 90, altitude: 1000);
        ac.VerticalSpeed = 3000;
        ac.Phases!.Add(Active(OakPatternEntry()));
        AirspaceBoundaryCrossing? crossing = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo);
        Assert.NotNull(crossing);
        Assert.True(crossing.Volume.ContainsLateral(ac.Position));

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.Single(ac.Phases.Phases);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void S10_Below400FtAgl_NoHold_UnableSaidOnce_ARayGoneUnder30s_DoesNotSayItAgain()
    {
        (AircraftState ac, PhaseContext ctx) = LowOutsideTheSurfaceArea();
        SimScenarioState scenario = SoloScenario();
        scenario.ElapsedSeconds = 100;

        for (int t = 0; t < 3; t++)
        {
            Tick(ac, scenario, ctx);
        }

        Assert.Single(ac.Phases!.Phases);
        PilotTransmission unable = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal(Unable, unable.Text);
        Assert.EndsWith($", {Unable}", unable.SpeechText);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);
        ac.PendingPilotTransmissions.Clear();

        // The ray drops out for 29 s, then comes back: the same encounter, nothing said.
        TurnAwayFromTheSurfaceArea(ac);
        scenario.ElapsedSeconds = 110;
        Tick(ac, scenario, ctx);
        scenario.ElapsedSeconds = 129;
        Tick(ac, scenario, ctx);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);
        TurnIntoTheSurfaceArea(ac);
        Tick(ac, scenario, ctx);
        Tick(ac, scenario, ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Single(ac.Phases.Phases);
    }

    [Fact]
    public void S10_Below400FtAgl_ARayGone30sOrMore_EndsTheEncounter_AndTheUnableIsSaidAgain()
    {
        (AircraftState ac, PhaseContext ctx) = LowOutsideTheSurfaceArea();
        SimScenarioState scenario = SoloScenario();
        scenario.ElapsedSeconds = 100;
        Tick(ac, scenario, ctx);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);
        ac.PendingPilotTransmissions.Clear();

        TurnAwayFromTheSurfaceArea(ac);
        scenario.ElapsedSeconds = 129;
        Tick(ac, scenario, ctx);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);
        scenario.ElapsedSeconds = 130;
        Tick(ac, scenario, ctx);
        Assert.Null(ac.InPhaseHoldUnableArea);

        TurnIntoTheSurfaceArea(ac);
        scenario.ElapsedSeconds = 131;
        Tick(ac, scenario, ctx);
        Tick(ac, scenario, ctx);

        Assert.Equal(Unable, Assert.Single(ac.PendingPilotTransmissions).Text);
        Assert.Single(ac.Phases!.Phases);
    }

    [Fact]
    public void S10_Below400FtAgl_AChangeOfPhase_EndsTheEncounter_AndTheUnableIsSaidAgain()
    {
        (AircraftState ac, PhaseContext ctx) = LowOutsideTheSurfaceArea();
        SimScenarioState scenario = SoloScenario();
        Tick(ac, scenario, ctx);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);
        ac.PendingPilotTransmissions.Clear();

        ac.Phases = new PhaseList();
        ac.Phases.Add(new DownwindPhase { Status = PhaseStatus.Active });
        Tick(ac, scenario, ctx);

        Assert.Equal(Unable, Assert.Single(ac.PendingPilotTransmissions).Text);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);
    }

    [Fact]
    public void S10_Below400FtAgl_AClearanceIntoTheBravo_EndsTheEncounter()
    {
        (AircraftState ac, PhaseContext ctx) = LowOutsideTheSurfaceArea();
        SimScenarioState scenario = SoloScenario();
        Tick(ac, scenario, ctx);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);

        ac.GrantBravoClearance(Sfo);
        Tick(ac, scenario, ctx);

        Assert.Null(ac.InPhaseHoldUnableArea);
    }

    private static void TurnAwayFromTheSurfaceArea(AircraftState ac)
    {
        ac.TrueTrack = new TrueHeading(90);
        ac.TrueHeading = ac.TrueTrack;
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo));
    }

    private static void TurnIntoTheSurfaceArea(AircraftState ac)
    {
        ac.TrueTrack = new TrueHeading(270);
        ac.TrueHeading = ac.TrueTrack;
    }

    [Fact]
    public void Below400FtAgl_WhenThePilotCannotSpeak_SaysUnableTheFirstSecondItCan_StillOnce()
    {
        (AircraftState ac, PhaseContext ctx) = LowOutsideTheSurfaceArea();
        SimScenarioState scenario = SoloScenario();
        QueueOtherCall(ac);

        Tick(ac, scenario, ctx);

        Assert.Null(ac.InPhaseHoldUnableArea);
        ac.PendingPilotTransmissions.Clear();
        Tick(ac, scenario, ctx);
        Tick(ac, scenario, ctx);

        Assert.Equal(Unable, Assert.Single(ac.PendingPilotTransmissions).Text);
    }

    [Fact]
    public void QueuedLines_TheOrbitGoesInAtOnce_AndTheAdvisoryQueuesBehindThem_WithTheRequestRecorded()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(OakPatternEntry()));
        QueueOtherCall(ac);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Equal("say again", ac.PendingPilotTransmissions[0].Text);
        Assert.Contains(Advisory, ac.PendingPilotTransmissions[1].Text, StringComparison.Ordinal);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.Null(ac.InPhaseOrbitAdvisoryOwed);
    }

    [Fact]
    public void AQueuedRepeatOfTheOpenRequest_IsReplacedByTheAdvisory()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(OakPatternEntry()));
        var landing = new PilotSpeechText("request full stop runway 28R.", "request full stop runway two eight right.");
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Landing, 0, landing, PilotRequestContext.Runway("28R", null));
        QueueOtherCall(ac);
        PilotResponder.QueueSoloPilotTransmission(ac, landing, PilotTransmissionKind.Proactive, initialContact: false);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Equal("say again", ac.PendingPilotTransmissions[0].Text);
        Assert.Contains(Advisory, ac.PendingPilotTransmissions[1].Text, StringComparison.Ordinal);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
    }

    [Fact]
    public void H1_AnOpenLandingRequest_PatternEntryIntoTheBravo_OrbitsAtOnce_AndTheClassBRequestReplacesIt()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(OakPatternEntry()));
        var landing = new PilotSpeechText("request full stop runway 28R.", "request full stop runway two eight right.");
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Landing, 0, landing, PilotRequestContext.Runway("28R", null));
        Assert.True(ac.PendingPilotRequest!.BlocksOtherCalls);

        Tick(ac, SoloScenario(), Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Contains(Advisory, Assert.Single(ac.PendingPilotTransmissions).Text, StringComparison.Ordinal);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.Equal(Sfo, ac.PendingPilotRequest?.AirspaceIdent);
    }

    [Fact]
    public void AnOwedAdvisory_MeetsTheBravoCheckInInOneTick_BothGoOut_CheckInFirst_AndTheCheckInRequestIsKept()
    {
        // Not yet in contact: the orbit goes in front of the climb and its advisory is owed until the first call.
        AircraftState ac = WestOfTheRing();
        ac.HasMadeInitialContact = false;
        ac.HasControllerAcknowledgedInitialContact = false;
        ac.FlightPlan.Destination = "KMOD";
        ac.Phases!.Add(Active(InitialClimb()));
        SimScenarioState scenario = SoloScenario();
        PhaseContext ctx = Context(ac, Oak28R());
        Tick(ac, scenario, ctx);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(Sfo, ac.InPhaseOrbitAdvisoryOwed);
        Assert.Empty(ac.PendingPilotTransmissions);

        // One engine tick: the check-in, which asks for the Bravo and records its request, then the boundary step.
        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, LookupAirport);
        PilotPendingRequest checkIn = Assert.IsType<PilotPendingRequest>(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, checkIn.Kind);
        Tick(ac, scenario, ctx);

        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.True(ac.PendingPilotTransmissions[0].IsInitialContact);
        Assert.Equal(checkIn.LastPilotLine, ac.PendingPilotTransmissions[0].Text);
        Assert.Contains(Advisory, ac.PendingPilotTransmissions[1].Text, StringComparison.Ordinal);
        Assert.Same(checkIn, ac.PendingPilotRequest);
    }

    [Fact]
    public void Below400FtAgl_AnOpenInitialContact_DoesNotHoldTheUnableBack()
    {
        (AircraftState ac, PhaseContext ctx) = LowOutsideTheSurfaceArea();
        var checkIn = new PilotSpeechText("approach, N123AB, C172.", "approach, november one two three alpha bravo, skyhawk.");
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.InitialContact, 0, checkIn, PilotRequestContext.Facility("approach"));

        Tick(ac, SoloScenario(), ctx);

        Assert.Equal(Unable, Assert.Single(ac.PendingPilotTransmissions).Text);
    }

    [Fact]
    public void AnOpenAirspaceEntryRequestForTheSameBravo_IsKept()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(OakPatternEntry()));
        var asked = new PilotSpeechText("request bravo clearance.", "request bravo clearance.");
        PilotPendingRequest open = PilotRequestTracker.RecordRequest(
            ac,
            PilotPendingRequestKind.AirspaceEntry,
            10,
            asked,
            PilotRequestContext.Airspace(AirspaceClass.Bravo, Sfo, Ksfo)
        );
        SimScenarioState scenario = SoloScenario();
        scenario.ElapsedSeconds = 40;

        Tick(ac, scenario, Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Contains(Advisory, Assert.Single(ac.PendingPilotTransmissions).Text, StringComparison.Ordinal);
        Assert.Same(open, ac.PendingPilotRequest);
        Assert.Equal(asked.Terminal, open.LastPilotLine);
        Assert.Equal(10, open.LastRequestedAtSeconds);
    }

    [Fact]
    public void NotYetInContact_TheOrbitGoesIn_TheAdvisoryIsOwed_AndSaidWithTheFirstCall()
    {
        AircraftState ac = WestOfTheRing();
        ac.HasMadeInitialContact = false;
        ac.Phases!.Add(Active(OakPatternEntry()));
        SimScenarioState scenario = SoloScenario();
        PhaseContext ctx = Context(ac, Oak28R());

        Tick(ac, scenario, ctx);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Equal(Sfo, ac.InPhaseOrbitAdvisoryOwed);
        Assert.Null(ac.PendingPilotRequest);

        // The pilot's first call goes out; the advisory follows it.
        ac.HasMadeInitialContact = true;
        QueueOtherCall(ac);
        Tick(ac, scenario, ctx);

        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Contains(Advisory, ac.PendingPilotTransmissions[1].Text, StringComparison.Ordinal);
        Assert.Null(ac.InPhaseOrbitAdvisoryOwed);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        Assert.Equal(2, ac.Phases.Phases.Count);
    }

    [Fact]
    public void TheOwedAdvisory_IsKeptWhileTheHoldIsPendingAndWhileItIsCurrent()
    {
        AircraftState ac = WestOfTheRing();
        ac.HasMadeInitialContact = false;
        ac.Phases!.Add(Active(OakPatternEntry()));
        SimScenarioState scenario = SoloScenario();
        PhaseContext ctx = Context(ac, Oak28R());
        Tick(ac, scenario, ctx);
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Pending, hold.Status);

        Tick(ac, scenario, ctx);
        Assert.Equal(Sfo, ac.InPhaseOrbitAdvisoryOwed);

        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Tick(ac, scenario, ctx);

        Assert.Equal(Sfo, ac.InPhaseOrbitAdvisoryOwed);
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Same(hold, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void TheOwedAdvisory_IsDroppedWhenTheHoldEndsFirst()
    {
        AircraftState ac = WestOfTheRing();
        ac.HasMadeInitialContact = false;
        PatternEntryPhase entry = Active(OakPatternEntry());
        ac.Phases!.Add(entry);
        SimScenarioState scenario = SoloScenario();
        PhaseContext ctx = Context(ac, Oak28R());
        Tick(ac, scenario, ctx);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(Sfo, ac.InPhaseOrbitAdvisoryOwed);

        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        ac.HasMadeInitialContact = true;
        Tick(ac, scenario, ctx);

        Assert.Null(ac.InPhaseOrbitAdvisoryOwed);
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(ac.PendingPilotRequest);
    }

    [Fact]
    public void TunedAway_TheOrbitGoesInSilently_AndOwesItsAdvisoryUntilBack()
    {
        AircraftState ac = WestOfTheRing();
        ac.TunedPosition = TrackOwner.CreateNonNas("OAK_TWR");
        SimScenarioState scenario = SoloScenario();
        Assert.True(ac.IsTunedAwayFrom(scenario.StudentPosition));
        ac.Phases!.Add(Active(OakPatternEntry()));

        Tick(ac, scenario, Context(ac, Oak28R()));
        Tick(ac, scenario, Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Equal(Sfo, ac.InPhaseOrbitAdvisoryOwed);
        Assert.Null(ac.PendingPilotRequest);

        // Back on the student's frequency: the advisory and its Class B request go out that second.
        ac.TunedPosition = null;
        Assert.False(ac.IsTunedAwayFrom(scenario.StudentPosition));
        Tick(ac, scenario, Context(ac, Oak28R()));

        Assert.Contains(Advisory, Assert.Single(ac.PendingPilotTransmissions).Text, StringComparison.Ordinal);
        Assert.Null(ac.InPhaseOrbitAdvisoryOwed);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
    }

    [Fact]
    public void UnderRmob_TheAdvisoryAsksNothing_AndNoRequestIsRecorded()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(InitialClimb()));
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        ac.PendingPilotTransmissions.Clear();

        Tick(ac, scenario, Context(ac, Oak28R()));

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        string direction = hold.OrbitDirection == TurnDirection.Left ? "left" : "right";
        Assert.Equal($"that'll take us into the bravo; we'll orbit {direction} here.", Assert.Single(ac.PendingPilotTransmissions).Text);
        Assert.NotEqual(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
    }

    [Fact]
    public void UnderRmob_TheAdvisoryQueuesBehindAQueuedRepeat_ReplacingNothing()
    {
        (AircraftState ac, SimScenarioState scenario, PilotSpeechText landing) = UnderRmobWithAQueuedLandingRequest();
        // The tracker has voiced one follow-up of the request and the repeat is still queued.
        PilotPendingRequest request = ac.PendingPilotRequest!;
        request.FollowUpsVoiced = 1;

        Tick(ac, scenario, Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases!.CurrentPhase);
        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Equal(landing.Terminal, ac.PendingPilotTransmissions[0].Text);
        Assert.Contains(Advisory, ac.PendingPilotTransmissions[1].Text, StringComparison.Ordinal);
        Assert.Same(request, ac.PendingPilotRequest);
        Assert.Equal(1, request.FollowUpsVoiced);
    }

    [Fact]
    public void UnderRmob_TheAdvisoryQueuesBehindAnUnspokenFirstRequest()
    {
        (AircraftState ac, SimScenarioState scenario, PilotSpeechText landing) = UnderRmobWithAQueuedLandingRequest();
        PilotPendingRequest request = ac.PendingPilotRequest!;
        Assert.Equal(0, request.FollowUpsVoiced);

        Tick(ac, scenario, Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases!.CurrentPhase);
        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Equal(landing.Terminal, ac.PendingPilotTransmissions[0].Text);
        Assert.Contains(Advisory, ac.PendingPilotTransmissions[1].Text, StringComparison.Ordinal);
        Assert.Same(request, ac.PendingPilotRequest);
        Assert.Equal(0, request.FollowUpsVoiced);
    }

    /// <summary>
    /// <see cref="WestOfTheRing"/> on an Active initial climb, told to remain outside the SFO Class B, with an open landing
    /// request whose line is queued and not yet said.
    /// </summary>
    private static (AircraftState Aircraft, SimScenarioState Scenario, PilotSpeechText Landing) UnderRmobWithAQueuedLandingRequest()
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(InitialClimb()));
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        ac.PendingPilotTransmissions.Clear();
        var landing = new PilotSpeechText("request full stop runway 28R.", "request full stop runway two eight right.");
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Landing, 0, landing, PilotRequestContext.Runway("28R", null));
        PilotResponder.QueueSoloPilotTransmission(ac, landing, PilotTransmissionKind.Proactive, initialContact: false);
        Assert.Equal(landing.Terminal, Assert.Single(ac.PendingPilotTransmissions).Text);
        return (ac, scenario, landing);
    }

    [Fact]
    public void Step7a_AfterTheAdvisory_AHeadingIntoTheBravo_IsTheImplicitGrant_AndThePairEndsAsAGrantEnds()
    {
        AircraftState ac = WestOfTheRing();
        InitialClimbPhase climb = Active(InitialClimb());
        ac.Phases!.Add(climb);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PhaseContext ctx = Context(ac, Oak28R());
        Tick(ac, scenario, ctx);
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(PilotPendingRequestKind.AirspaceEntry, ac.PendingPilotRequest?.Kind);
        PhaseRunner.Tick(ac, ctx);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 090");
        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsClearedIntoBravo(Sfo));
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Null(ac.Phases.HeldPhase);
    }

    [Fact]
    public void Step7b_UnderRmob_AHeadingIntoTheBravo_ReadsBackNotCleared_AndTheOrbitContinues()
    {
        AircraftState ac = WestOfTheRing();
        InitialClimbPhase climb = Active(InitialClimb());
        ac.Phases!.Add(climb);
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PhaseContext ctx = Context(ac, Oak28R());
        Tick(ac, scenario, ctx);
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        PhaseRunner.Tick(ac, ctx);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        ac.PendingPilotTransmissions.Clear();

        CommandResult result = engine.SendCommand(Callsign, "FH 090");
        Assert.True(result.Success, result.Message);
        PilotTransmission readback = Assert.Single(ac.PendingPilotTransmissions);
        Assert.StartsWith("heading 090 takes us into the bravo, we're not cleared", readback.Text, StringComparison.Ordinal);
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.False(ac.IsClearedIntoBravo(Sfo));
        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(climb, ac.Phases.HeldPhase);
    }

    [Fact]
    public void TheHoldInFrontOfAClimb_KeepsTheClimbTarget()
    {
        AircraftState ac = OrbitingInFrontOfAClimb(targetSpeed: 85, indicatedAirspeed: 120);

        Assert.Equal(ClimbTargetFt, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void TheHoldInFrontOfADescendingFinal_LevelsAtThePresentAltitude()
    {
        (AircraftState ac, RunwayInfo runway, _) = OnAFinalInsideTheFafHeadingIntoTheSurfaceArea();
        PhaseContext ctx = Context(ac, runway);
        var final = new FinalApproachPhase();
        ac.Phases!.Add(final);
        // The final starts and flies its glidepath: a descent rate below the aircraft.
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(final, ac.Phases.CurrentPhase);
        Assert.True(ac.Targets.DesiredVerticalRate < 0, $"the final should be descending; rate {ac.Targets.DesiredVerticalRate}");
        double altitude = ac.Altitude;

        Tick(ac, SoloScenario(), ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(final, ac.Phases.HeldPhase);
        Assert.Equal(altitude, ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void TheHoldInFrontOfADescendingBase_LevelsAtThePresentAltitude_AndTheBaseResumesItsDescentAfterIt()
    {
        AircraftState ac = WestOfTheRing();
        PhaseContext ctx = Context(ac, Oak28R());
        var basePhase = new BasePhase { Waypoints = OakLeftPattern(), Status = PhaseStatus.Active };
        ac.Phases!.Add(basePhase);
        basePhase.OnStart(ctx);
        Assert.True(ac.Targets.DesiredVerticalRate < 0, $"the base should be descending; rate {ac.Targets.DesiredVerticalRate}");
        Assert.True(ac.Targets.TargetAltitude < ac.Altitude);

        Tick(ac, SoloScenario(), ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(basePhase, ac.Phases.HeldPhase);
        Assert.Equal(ac.Altitude, ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.DesiredVerticalRate);

        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        BasePhase resumed = Assert.IsType<BasePhase>(ac.Phases.CurrentPhase);
        Assert.NotSame(basePhase, resumed);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.True(ac.Targets.DesiredVerticalRate < 0, $"the resumed base should descend; rate {ac.Targets.DesiredVerticalRate}");
        Assert.True(ac.Targets.TargetAltitude < ac.Altitude);
    }

    [Fact]
    public void HeldDescendingMidfieldCrossing_ResumesInPlace_AndDescendsToItsCrossingAltitude()
    {
        (AircraftState ac, PhaseContext ctx, MidfieldCrossingPhase crossing, double crossingAlt) = OrbitingInFrontOfADescendingCrossing(
            descentRateLeft: null
        );

        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(crossing, ac.Phases!.CurrentPhase);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(crossingAlt, ac.Targets.TargetAltitude);

        (SimulationEngine engine, _) = SoloEngine(ac);
        for (int t = 0; (t < 1500) && (ac.Altitude > crossingAlt + 50) && ReferenceEquals(crossing, ac.Phases?.CurrentPhase); t++)
        {
            engine.TickOneSecond();
        }

        Assert.Same(crossing, ac.Phases?.CurrentPhase);
        Assert.True(ac.Altitude <= crossingAlt + 50, $"the crossing should have descended to {crossingAlt:F0} ft; at {ac.Altitude:F0} ft");
    }

    [Fact]
    public void AControllerAltitudeDuringTheHold_IsKept_WhenTheHeldPhaseResumes()
    {
        AircraftState ac = WestOfTheRing();
        InitialClimbPhase climb = Active(InitialClimb());
        ac.Phases!.Add(climb);
        ac.Targets.TargetAltitude = ClimbTargetFt;
        (SimulationEngine engine, SimScenarioState scenario) = SoloEngine(ac);
        PhaseContext ctx = Context(ac, Oak28R());
        Tick(ac, scenario, ctx);
        PhaseRunner.Tick(ac, ctx);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);

        CommandResult result = engine.SendCommand(Callsign, "CM 085");
        Assert.True(result.Success, result.Message);
        Assert.Equal(8500, ac.Targets.TargetAltitude);
        PhaseRunner.Tick(ac, ctx);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(8500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void AHoldSnapshot_RoundTripsTheSavedVerticalTargets()
    {
        // A rate the held phase left standing, so the saved rate has something to carry.
        (AircraftState ac, PhaseContext ctx, _, double crossingAlt) = OrbitingInFrontOfADescendingCrossing(descentRateLeft: -500);
        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);
        Assert.IsType<AirspaceBoundaryHoldPhase>(restored.Phases?.CurrentPhase);

        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);
        restored.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(restored, Context(restored, Oak28R()));

        Assert.IsType<MidfieldCrossingPhase>(restored.Phases!.CurrentPhase);
        Assert.Equal(crossingAlt, restored.Targets.TargetAltitude);
        Assert.Equal(-500, restored.Targets.DesiredVerticalRate);
        Assert.Equal(ac.Targets.TargetAltitude, restored.Targets.TargetAltitude);
        Assert.Equal(ac.Targets.DesiredVerticalRate, restored.Targets.DesiredVerticalRate);
    }

    /// <summary>
    /// <see cref="WestOfTheRing"/> at 7500 ft on an Active Oakland midfield crossing flown at its far lower crossing altitude,
    /// with <paramref name="descentRateLeft"/> standing after the crossing started, held by the trigger and its hold started
    /// (levelled at the present altitude); with the crossing altitude.
    /// </summary>
    private static (
        AircraftState Aircraft,
        PhaseContext Context,
        MidfieldCrossingPhase Crossing,
        double CrossingAltitude
    ) OrbitingInFrontOfADescendingCrossing(double? descentRateLeft)
    {
        AircraftState ac = WestOfTheRing();
        PhaseContext ctx = Context(ac, Oak28R());
        var crossing = new MidfieldCrossingPhase { Waypoints = OakLeftPattern(), Status = PhaseStatus.Active };
        ac.Phases!.Add(crossing);
        crossing.OnStart(ctx);
        double crossingAlt = ac.Targets.TargetAltitude!.Value;
        Assert.True(crossingAlt < ac.Altitude - 3000, $"the crossing should start well above {crossingAlt:F0} ft");
        ac.Targets.DesiredVerticalRate = descentRateLeft;

        Tick(ac, SoloScenario(), ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(crossing, ac.Phases.HeldPhase);
        Assert.Equal(ac.Altitude, ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.DesiredVerticalRate);
        return (ac, ctx, crossing, crossingAlt);
    }

    private const double ClimbTargetFt = 9500;

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

    [Fact]
    public void TheHoldInFrontOfAPhase_LowersATargetAboveHoldingSpeedToHoldingSpeed()
    {
        AircraftState ac = OrbitingInFrontOfAClimb(targetSpeed: 120, indicatedAirspeed: 120);

        Assert.Equal(AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude), ac.Targets.TargetSpeed);
    }

    [Fact]
    public void TheHoldInFrontOfAPhase_KeepsATargetBelowHoldingSpeed()
    {
        AircraftState ac = OrbitingInFrontOfAClimb(targetSpeed: 70, indicatedAirspeed: 120);
        Assert.True(70 < AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude));

        Assert.Equal(70, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void TheHoldInFrontOfAPhase_WithNoSpeedTarget_NeverRaisesItAboveTheAirspeed()
    {
        AircraftState ac = OrbitingInFrontOfAClimb(targetSpeed: null, indicatedAirspeed: 70);
        Assert.True(70 < AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude));

        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.TargetSpeed <= 70, $"target {ac.Targets.TargetSpeed} kt above the 70 kt airspeed");
    }

    /// <summary>
    /// <see cref="WestOfTheRing"/> on an Active initial climb that left <paramref name="targetSpeed"/> and a
    /// <see cref="ClimbTargetFt"/> altitude target, held by the trigger and its hold ticked twice at <paramref name="indicatedAirspeed"/>.
    /// </summary>
    private static AircraftState OrbitingInFrontOfAClimb(double? targetSpeed, double indicatedAirspeed)
    {
        AircraftState ac = WestOfTheRing();
        ac.Phases!.Add(Active(InitialClimb()));
        ac.Targets.TargetAltitude = ClimbTargetFt;
        ac.Targets.TargetSpeed = targetSpeed;
        PhaseContext ctx = Context(ac, Oak28R());
        Tick(ac, SoloScenario(), ctx);
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        ac.IndicatedAirspeed = indicatedAirspeed;

        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, ac.Phases.CurrentPhase!.Status);
        return ac;
    }

    [Fact]
    public void SnapshotRoundTrip_KeepsTheUnableToHoldLatch_AndWritesNoMemberWithoutIt()
    {
        (AircraftState ac, PhaseContext ctx) = LowOutsideTheSurfaceArea();
        string bare = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AircraftSnapshotDto.InPhaseHoldUnableArea), bare, StringComparison.OrdinalIgnoreCase);
        Tick(ac, SoloScenario(), ctx);
        Assert.Equal(Sfo, ac.InPhaseHoldUnableArea);

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(Sfo, restored.InPhaseHoldUnableArea);
        Assert.Equal(ac.InPhaseHoldUnableSeenAtSeconds, restored.InPhaseHoldUnableSeenAtSeconds);
        Assert.NotNull(restored.InPhaseHoldUnableSeenAtSeconds);
        Assert.Equal(ac.InPhaseHoldUnablePhase, restored.InPhaseHoldUnablePhase);
        Assert.NotNull(restored.InPhaseHoldUnablePhase);
        // The restored pilot is still in the same encounter: it does not say it again.
        Tick(restored, SoloScenario(), Context(restored, Runway("SFO", "28R")));
        Assert.Empty(restored.PendingPilotTransmissions);
        Assert.Equal(Sfo, restored.InPhaseHoldUnableArea);
    }

    [Fact]
    public void SnapshotRoundTrip_KeepsTheOwedAdvisory_AndWritesNoMemberWithoutIt()
    {
        AircraftState ac = WestOfTheRing();
        ac.HasMadeInitialContact = false;
        ac.Phases!.Add(Active(OakPatternEntry()));
        string bare = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AircraftSnapshotDto.InPhaseOrbitAdvisoryOwed), bare, StringComparison.OrdinalIgnoreCase);
        Tick(ac, SoloScenario(), Context(ac, Oak28R()));
        Assert.Equal(Sfo, ac.InPhaseOrbitAdvisoryOwed);

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;

        Assert.Equal(Sfo, AircraftState.FromSnapshot(dto, null).InPhaseOrbitAdvisoryOwed);
    }

    [Fact]
    public void Replay_AcrossAnInPhaseInsert_SaysTheSameLinesAtTheSameSeconds()
    {
        var groundData = new TestAirportGroundData();
        var live = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        live.LoadScenario(ReplayScenarioJson, rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        live.Scenario!.SoloTrainingMode = true;
        List<string> liveLines = CollectLines(live);
        // The pilot calls up first; the pattern entry then answers its closed-traffic request.
        for (int t = 0; t < 30; t++)
        {
            live.TickOneSecond();
        }

        Assert.True(live.SendCommand(Callsign, "ELD 28R").Success);
        for (int t = 0; t < 10; t++)
        {
            live.TickOneSecond();
        }

        bool held = false;
        for (int t = 0; (t < 900) && !held; t++)
        {
            live.TickOneSecond();
            held = live.World.GetSnapshot().Single(a => a.Callsign == Callsign).Phases?.HeldPhase is not null;
        }

        for (int t = 0; t < 20; t++)
        {
            live.TickOneSecond();
        }

        foreach (string line in liveLines)
        {
            _output.WriteLine($"[live] {line}");
        }

        AircraftState liveAc = live.World.GetSnapshot().Single(a => a.Callsign == Callsign);
        Assert.True(
            held,
            $"the pattern phase should have been held in front of an orbit; phase {liveAc.Phases?.CurrentPhase?.Name}, contact "
                + $"{liveAc.HasMadeInitialContact}, at {liveAc.Position} {liveAc.Altitude:F0} ft, lines: {string.Join(" | ", liveLines)}"
        );
        Assert.Contains(liveLines, l => l.Contains(Advisory, StringComparison.Ordinal));

        var recording = new SessionRecording
        {
            ScenarioJson = ReplayScenarioJson,
            RngSeed = 42,
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            Actions = [.. live.Scenario.ActionLog],
            TotalElapsedSeconds = live.Scenario.ElapsedSeconds,
        };
        var replay = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        List<string> replayLines = CollectLines(replay);
        replay.ReplayWithScenarioOverride(recording, live.Scenario.ElapsedSeconds, scenario => scenario.SoloTrainingMode = true);

        Assert.Equal(liveLines, replayLines);
    }

    /// <summary>Every pilot line for <see cref="Callsign"/> the terminal shows, as "second kind callsign: text".</summary>
    private static List<string> CollectLines(SimulationEngine engine)
    {
        var lines = new List<string>();
        engine.TerminalEntryEmitted += entry =>
        {
            if ((entry.Callsign == Callsign) && (entry.Kind is "SayPilot" or "SayReadback"))
            {
                lines.Add($"{engine.Scenario?.ElapsedSeconds:F0} {entry.Kind} {entry.Callsign}: {entry.Message}");
            }
        };
        return lines;
    }

    /// <summary>A VFR C172 at 7500 ft some six miles west of the SFO Class B ring, eastbound, bound for Oakland.</summary>
    private const string ReplayScenarioJson = """
        {
          "id": "boundary-hold-trigger-replay",
          "name": "Boundary hold trigger replay",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N123AB",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates", "coordinates": { "lat": 37.62, "lon": -123.021 }, "altitude": 7500, "heading": 90, "speed": 120
              },
              "flightplan": {
                "rules": "VFR",
                "departure": "KHAF",
                "destination": "KOAK",
                "cruiseAltitude": 7500,
                "cruiseSpeed": 110,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              },
              "presetCommands": []
            }
          ]
        }
        """;

    private static void Tick(AircraftState ac, SimScenarioState scenario, PhaseContext ctx) =>
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, _ => ctx);

    private static void QueueOtherCall(AircraftState ac) =>
        PilotResponder.QueueSoloPilotTransmission(
            ac,
            new PilotSpeechText("say again", "say again"),
            PilotTransmissionKind.Proactive,
            initialContact: false
        );

    private static T Active<T>(T phase)
        where T : Phase
    {
        phase.Status = PhaseStatus.Active;
        return phase;
    }

    /// <summary>A VFR C172 eastbound at 7500 ft just west of the SFO Class B ring, on a running phase list.</summary>
    private static AircraftState WestOfTheRing()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, altitude: 7500);
        ac.IndicatedAirspeed = 120;
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo));
        return ac;
    }

    /// <summary>
    /// A C172 at 300 ft MSL (under 400 ft AGL at SFO) half a mile east of the SFO surface area, westbound into it, on an
    /// Active pattern entry.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context) LowOutsideTheSurfaceArea()
    {
        LatLon edge = FirstPointOutside(Ksfo, 90, v => v.LowerFtMsl == 0);
        AircraftState ac = Airborne(GeoMath.ProjectPoint(edge, new TrueHeading(90), 0.5), trueHeading: 270, altitude: 300);
        ac.Phases!.Add(Active(OakPatternEntry()));
        RunwayInfo sfo28R = Runway("SFO", "28R");
        AirspaceBoundaryCrossing? crossing = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo);
        Assert.NotNull(crossing);
        Assert.False(crossing.Volume.ContainsLateral(ac.Position));
        Assert.True(ac.Altitude - sfo28R.ElevationFt < 400);
        return (ac, Context(ac, sfo28R));
    }

    /// <summary>
    /// A C172 inbound on a Bay Area final inside its published FAF and at least 400 ft AGL, laterally outside an SFO Class B
    /// volume whose band holds its altitude and entering it within 60 s, with that runway and its opposite end.
    /// </summary>
    private static (AircraftState Aircraft, RunwayInfo Runway, RunwayInfo Opposite) OnAFinalInsideTheFafHeadingIntoTheSurfaceArea()
    {
        (string Airport, string End, string Opposite)[] finals =
        [
            ("SFO", "28R", "10L"),
            ("SFO", "28L", "10R"),
            ("SFO", "10L", "28R"),
            ("SFO", "10R", "28L"),
            ("SFO", "19L", "01R"),
            ("SFO", "01R", "19L"),
            ("OAK", "28R", "10L"),
            ("OAK", "30", "12"),
            ("OAK", "12", "30"),
            ("SQL", "30", "12"),
            ("SQL", "12", "30"),
            ("HWD", "28L", "10R"),
            ("PAO", "31", "13"),
            ("HAF", "12", "30"),
        ];
        foreach ((string airport, string end, string opposite) in finals)
        {
            if (FindOnFinalIntoTheBravo(airport, end) is { } ac)
            {
                return (ac, Runway(airport, end), Runway(airport, opposite));
            }
        }

        throw new InvalidOperationException("no Bay Area final inside its FAF meets the SFO Class B laterally");
    }

    /// <summary>
    /// A C172 inbound on the <paramref name="airport"/> <paramref name="end"/> final, inside its published FAF, laterally
    /// outside an SFO Class B volume whose band holds its altitude and entering it within 60 s; null when none on that final.
    /// </summary>
    private static AircraftState? FindOnFinalIntoTheBravo(string airport, string end)
    {
        if (NavigationDatabase.Instance.GetRunway(airport, end) is not { } runway)
        {
            return null;
        }

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        for (double alongNm = 1.0; alongNm <= 8.0; alongNm += 0.25)
        {
            for (int altitude = 1000; altitude <= 5000; altitude += 500)
            {
                AircraftState ac = Airborne(GeoMath.ProjectPoint(threshold, runway.TrueHeading + 180, alongNm), runway.TrueHeading.Degrees, altitude);
                ac.IndicatedAirspeed = 150;
                AirspaceBoundaryCrossing? crossing = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo);
                bool lateral = (crossing is not null) && !crossing.Volume.ContainsLateral(ac.Position);
                if (lateral && FinalApproachFix.IsInsidePublishedFaf(ac, runway, null) && (ac.Altitude - runway.ElevationFt >= 400))
                {
                    return ac;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A VFR C172 around Oakland whose 60 s ray enters a Class C first and a Class B behind it, laterally outside both, the
    /// first such start found on a grid over the East Bay.
    /// </summary>
    private static AircraftState BehindANearerCharlie()
    {
        LatLon oak = LookupAirport("OAK")!.Value;
        for (double dLat = -0.15; dLat <= 0.15; dLat += 0.025)
        {
            for (double dLon = -0.2; dLon <= 0.2; dLon += 0.025)
            {
                for (int heading = 0; heading < 360; heading += 30)
                {
                    foreach (int altitude in (int[])[1500, 2000, 2500, 3000])
                    {
                        AircraftState ac = Airborne(new LatLon(oak.Lat + dLat, oak.Lon + dLon), heading, altitude);
                        ac.IndicatedAirspeed = 200;
                        if (EntersACharlieThenABravo(ac))
                        {
                            return ac;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException("no East Bay start meets a Charlie before a Bravo");
    }

    private static bool EntersACharlieThenABravo(AircraftState ac)
    {
        AirspaceBoundaryCrossing? first = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60);
        if ((first is null) || (first.Volume.Class != AirspaceClass.Charlie) || first.Volume.ContainsLateral(ac.Position))
        {
            return false;
        }

        AirspaceBoundaryCrossing? bravo = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo);
        return (bravo is not null) && !bravo.Volume.ContainsLateral(ac.Position);
    }

    private static PatternEntryPhase OakPatternEntry()
    {
        RunwayInfo runway = Oak28R();
        LatLon entry = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + (-90), 1.0);
        return new PatternEntryPhase
        {
            EntryLat = entry.Lat,
            EntryLon = entry.Lon,
            PatternAltitude = runway.ElevationFt + 1000,
            Kind = PatternEntryKind.Direct,
        };
    }

    private static ApproachNavigationPhase ApproachNavigation() => new() { Fixes = [] };

    private static InitialClimbPhase InitialClimb() =>
        new()
        {
            Departure = new DefaultDeparture(),
            IsVfr = true,
            CruiseAltitude = 7500,
        };

    /// <summary>Walks from <paramref name="from"/> along <paramref name="bearing"/> to the first point no matching SFO volume covers.</summary>
    private static LatLon FirstPointOutside(LatLon from, double bearing, Func<AirspaceVolume, bool> matches)
    {
        IReadOnlyList<AirspaceVolume> sfo = AirspaceDatabase.Default.ClassBArea(Sfo);
        for (double nm = 0; nm < 60; nm += 0.1)
        {
            LatLon point = GeoMath.ProjectPoint(from, new TrueHeading(bearing), nm);
            if (!sfo.Any(v => matches(v) && v.ContainsLateral(point)))
            {
                return point;
            }
        }

        throw new InvalidOperationException("no edge found");
    }

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
            IndicatedAirspeed = 100,
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
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static PhaseContext ContextWithoutRunway(AircraftState ac, double fieldElevation) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = null,
            FieldElevation = fieldElevation,
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static RunwayInfo Oak28R() => Runway("OAK", "28R");

    private static RunwayInfo Runway(string airport, string designator) =>
        NavigationDatabase.Instance.GetRunway(airport, designator) ?? throw new InvalidOperationException($"{airport} {designator} missing");

    private static SimScenarioState SoloScenario() =>
        new()
        {
            ScenarioId = "test-boundary-hold-trigger",
            ScenarioName = "Boundary Hold Trigger",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = "SFO",
            StudentPositionType = "APP",
        };

    private static (SimulationEngine Engine, SimScenarioState Scenario) SoloEngine(AircraftState ac)
    {
        SimScenarioState scenario = SoloScenario();
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = scenario };
        engine.World.AddAircraft(ac);
        return (engine, scenario);
    }

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => Ksfo,
            "HAF" or "KHAF" => new LatLon(37.5133, -122.5008),
            _ => null,
        };
}
