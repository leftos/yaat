using System.Collections.Immutable;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Commands;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.Asdex;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.Training;

/// <summary>
/// The solo evaluator's runway-user finding: at a takeoff roll's start or a landing's threshold crossing, every other aircraft
/// using the operation's runway (past its hold line, on its pavement, crossing it, in a LAHSO hold) that the pair rules do not
/// already judge scores a Safety finding "Runway not clear for takeoff/landing" (7110.65 3-9-5 / 3-9-6.a / 3-7-2.g.1 for a takeoff,
/// 3-10-3.a / 3-7-2.g.2(a) / 3-9-4.c.1(a) for a landing). Real runways and ground layouts (KOAK 33/28R, KSFO 28R/1L, and KSJC's
/// displaced runway); the aircraft are placed on them.
/// </summary>
public class SoloRunwayUserScoringTests
{
    private const string TakeoffRuleReference = "7110.65 3-9-5 / 3-9-6.a / 3-7-2.g.1; AIM 2-3-5.a.1";
    private const string LandingRuleReference = "7110.65 3-10-3.a / 3-7-2.g.2(a) / 3-9-4.c.1(a); AIM 2-3-5.a.1";
    private const string TakeoffTitle = "Runway not clear for takeoff";
    private const string LandingTitle = "Runway not clear for landing";

    public SoloRunwayUserScoringTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Landing_OverAFoulerPastTheRunway33Bar_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // The fouler is assigned 28R, so the pair rules never see it on 33: only the runway-user rule can.
        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: Runway("OAK", "28R"));
        AssertAheadOfTheLandingThreshold(oak, fouler);
        AircraftState lander = Lander("AAL10", oak.Runway, oak.Layout, new LandingPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lander, fouler], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lander, oak.Runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lander, fouler], 11, AirspaceDatabase.Default);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Equal(SoloTrainingEventSeverity.Safety, finding.Severity);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, finding.Category);
        Assert.Equal(LandingRuleReference, finding.RuleReference);
        Assert.Equal("33", finding.RunwayId);
        Assert.Equal([fouler.Callsign, lander.Callsign], finding.Callsigns);
        Assert.Contains("past the hold line", finding.Description);
    }

    [Fact]
    public void Departure_RollingPastAFouler_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);
        AircraftState departure = LinedUp("UAL20", oak.Runway, oak.Layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, oak.Runway, [departure, fouler]);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Equal(SoloTrainingEventSeverity.Safety, finding.Severity);
        Assert.Equal(TakeoffRuleReference, finding.RuleReference);
        Assert.Equal([fouler.Callsign, departure.Callsign], finding.Callsigns);
    }

    [Fact]
    public void Departure_CrosserProjectedClearBeforeTheRollReachesIt_DoesNotScore()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState crosser = MidCrossing(oak.Layout, oak.Runway, oak.Bar);
        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(crosser, oak.Runway, oak.Layout)?.Kind);
        AircraftState departure = LinedUp("UAL21", oak.Runway, oak.Layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, oak.Runway, [departure, crosser]);

        Assert.DoesNotContain(notices, e => e.Title == TakeoffTitle);
    }

    [Fact]
    public void Departure_HeldCrosserNeverProjectedClear_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // A crossing held on the pavement is restricted short of the far marking: the projection never clears it.
        AircraftState crosser = MidCrossing(oak.Layout, oak.Runway, oak.Bar);
        crosser.Ground.Hold = HoldDirective.HoldPosition;
        AircraftState departure = LinedUp("UAL22", oak.Runway, oak.Layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, oak.Runway, [departure, crosser]);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Contains("crossing the runway", finding.Description);
    }

    [Fact]
    public void Landing_CrosserStillOnThePavementAtTheThreshold_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // The same moving crossing the departure test projects clear: a landing judges at the threshold crossing, no later.
        AircraftState crosser = MidCrossing(oak.Layout, oak.Runway, oak.Bar);
        AssertAheadOfTheLandingThreshold(oak, crosser);
        AircraftState lander = Lander("AAL11", oak.Runway, oak.Layout, new LandingPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lander, crosser], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lander, oak.Runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lander, crosser], 11, AirspaceDatabase.Default);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Contains("crossing the runway", finding.Description);
    }

    [Fact]
    public void Departure_UserBehindTheStartPoint_DoesNotScore()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState behind = LinedUp("N30BH", runway, layout, alongFt: 500);
        AircraftState departure = LinedUp("UAL30", runway, layout, alongFt: 2000);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, runway, [departure, behind]);

        Assert.DoesNotContain(notices, e => e.Title == TakeoffTitle);
    }

    [Fact]
    public void Departure_LinedUpAircraftAhead_Scores()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState ahead = LinedUp("N31AH", runway, layout, alongFt: 4000);
        AircraftState departure = LinedUp("UAL31", runway, layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, runway, [departure, ahead]);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Equal([ahead.Callsign, departure.Callsign], finding.Callsigns);
    }

    [Fact]
    public void Departure_LineUpShortOfTheMarking_DoesNotScore()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // Cleared to line up and moving: its nose 10 ft short of the marking.
        AircraftState lineUp = RunwayOccupancyTests.SquareToTheMarking(oak.Runway, oak.Bar, "B738", facingRunway: true, -10.0, alongFt: 0);
        lineUp.Callsign = "N40LU";
        lineUp.IndicatedAirspeed = 8;
        lineUp.Targets.TargetSpeed = 8;
        SetPhase(lineUp, oak.Runway, new LineUpPhase());
        Assert.False(RunwayOccupancy.IsOverStrictPavement(lineUp.Position, oak.Runway));
        AircraftState departure = LinedUp("UAL40", oak.Runway, oak.Layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, oak.Runway, [departure, lineUp]);

        Assert.DoesNotContain(notices, e => e.Title == TakeoffTitle);
    }

    [Fact]
    public void Departure_LineUpWithItsMainGearOnThePavement_Scores()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState lineUp = OnCentreline("N41LU", runway, alongFt: 5000);
        lineUp.IndicatedAirspeed = 5;
        SetPhase(lineUp, runway, new LineUpPhase());
        AircraftState departure = LinedUp("UAL41", runway, layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, runway, [departure, lineUp]);

        Assert.Single(notices, e => e.Title == TakeoffTitle);
    }

    [Fact]
    public void Departure_LahsoHoldOnThePavementAhead_Scores()
    {
        RunwayInfo runway = Runway("SFO", "28R");
        AirportGroundLayout layout = Layout("SFO");
        AircraftState lahso = OnCentreline("SKW50", runway, alongFt: 6000);
        SetPhase(lahso, runway, new RunwayHoldingPhase("1L"));
        AircraftState departure = LinedUp("UAL50", runway, layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, runway, [departure, lahso]);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Equal([lahso.Callsign, departure.Callsign], finding.Callsigns);
        Assert.Contains("holding short on the runway", finding.Description);
    }

    [Fact]
    public void TouchAndGo_OverAFouler_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);
        AssertAheadOfTheLandingThreshold(oak, fouler);
        AircraftState lander = Lander("N60TG", oak.Runway, oak.Layout, new TouchAndGoPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lander, fouler], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lander, oak.Runway, new TouchAndGoPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lander, fouler], 11, AirspaceDatabase.Default);

        Assert.Single(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void TouchAndGo_BehindALandingStillRollingOut_RecordsArrivalBehindLanding()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState lead = OnCentreline("AAL61", runway, alongFt: 1500);
        lead.IndicatedAirspeed = 100;
        lead.Ground.Layout = layout;
        SetPhase(lead, runway, new LandingPhase());
        AircraftState touchAndGo = Lander("N61TG", runway, layout, new TouchAndGoPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lead, touchAndGo], 10, AirspaceDatabase.Default);
        CrossTheThreshold(touchAndGo, runway, new TouchAndGoPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lead, touchAndGo], 11, AirspaceDatabase.Default);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal("Arrival behind landing same-runway separation", pair.Title);
    }

    [Fact]
    public void RestrictedLowApproachAtFiveHundredFeet_OverAFouler_DoesNotScore()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);
        AircraftState lowApproach = Lander("N70LA", oak.Runway, oak.Layout, new LowApproachPhase());
        lowApproach.Altitude = oak.Runway.ElevationFt + 600;
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lowApproach, fouler], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lowApproach, oak.Runway, new LowApproachPhase());
        lowApproach.Altitude = oak.Runway.ElevationFt + 600;
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lowApproach, fouler], 11, AirspaceDatabase.Default);

        Assert.DoesNotContain(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void Departure_BehindThePrecedingLandingStillRollingOut_IsScoredOnceByThePairRule()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState lead = OnCentreline("AAL80", runway, alongFt: 1500);
        lead.IndicatedAirspeed = 100;
        SetPhase(lead, runway, new LandingPhase());
        AircraftState departure = LinedUp("UAL80", runway, layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, runway, [lead, departure]);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal("Departure behind landing same-runway separation", pair.Title);
    }

    [Fact]
    public void Departure_PastAnEarlierArrivalNowStoppedPastABar_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // The arrival landed on 33 (an operation the tracker recorded), then taxied off and stopped past another 33 bar: its
        // landing has ended, so the pair rules no longer judge it, and it is an ordinary runway user.
        AircraftState arrival = OnCentreline("AAL81", oak.Runway, alongFt: 1500);
        arrival.IndicatedAirspeed = 100;
        SetPhase(arrival, oak.Runway, new LandingPhase());
        AircraftState departure = LinedUp("UAL81", oak.Runway, oak.Layout, alongFt: 0);
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([arrival, departure], 10, AirspaceDatabase.Default);

        AircraftState stopped = HeldFouler(oak.Runway, oak.Bar, assignedRunway: oak.Runway);
        arrival.Position = stopped.Position;
        arrival.TrueHeading = stopped.TrueHeading;
        arrival.TrueTrack = stopped.TrueTrack;
        arrival.IndicatedAirspeed = 0;
        arrival.Phases = stopped.Phases;
        arrival.Ground.Hold = HoldDirective.HoldPosition;
        evaluator.Evaluate([arrival, departure], 11, AirspaceDatabase.Default);
        SetPhase(departure, oak.Runway, new TakeoffPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([arrival, departure], 12, AirspaceDatabase.Default);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Equal([arrival.Callsign, departure.Callsign], finding.Callsigns);
    }

    [Fact]
    public void Finding_DeactivatesWhenTheUserLeavesTheRunway()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState ahead = LinedUp("N90AH", runway, layout, alongFt: 6000);
        AircraftState departure = LinedUp("UAL90", runway, layout, alongFt: 0);
        var evaluator = new SoloTrainingEvaluator();

        SoloTrainingEvent finding = Assert.Single(StartTheRoll(departure, runway, [departure, ahead], evaluator), e => e.Title == TakeoffTitle);
        departure.Position = PositionOnRunway(runway, 300);
        departure.IndicatedAirspeed = 30;
        evaluator.Evaluate([departure, ahead], 12, AirspaceDatabase.Default);
        Assert.True(Timeline(evaluator, 12).Single(e => e.Id == finding.Id).IsActive);

        // Off the runway: well beyond its edge, on no phase of it.
        ahead.Position = GeoMath.ProjectPoint(ahead.Position, new TrueHeading(runway.TrueHeading.Degrees + 90.0), 0.2);
        ahead.Phases = new PhaseList();
        ahead.Phases.Add(new TaxiingPhase());
        evaluator.Evaluate([departure, ahead], 13, AirspaceDatabase.Default);

        Assert.False(Timeline(evaluator, 13).Single(e => e.Id == finding.Id).IsActive);
    }

    [Fact]
    public void Finding_SurvivesASnapshotRoundTripOfThePreTriggerWorld()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        SimulationEngine original = SoloEngine();
        original.World.AddAircraft(HeldFouler(oak.Runway, oak.Bar, assignedRunway: null));
        original.World.AddAircraft(LinedUp("UAL95", oak.Runway, oak.Layout, alongFt: 0));
        StateSnapshotDto snapshot = original.CaptureSnapshot();
        SimulationEngine restored = SoloEngine();
        restored.RestoreFromSnapshot(snapshot);
        restored.Scenario!.SoloTrainingMode = true;

        SoloTrainingEvent fromOriginal = Assert.Single(TriggerInEngine(original, oak.Runway), e => e.Title == TakeoffTitle);
        SoloTrainingEvent fromRestored = Assert.Single(TriggerInEngine(restored, oak.Runway), e => e.Title == TakeoffTitle);

        Assert.Equal(fromOriginal.Id, fromRestored.Id);
        Assert.True(fromRestored.IsActive);
        Assert.Equal(fromOriginal.Callsigns, fromRestored.Callsigns);
    }

    [Fact]
    public void Departure_LineUpMovingPastTheMarking_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // Taxiing to line up and wait (3-9-4.c.1(a)): its nose 20 ft past the marking, its main gear short of the pavement.
        AircraftState lineUp = LineUpAtTheMarking(oak.Runway, oak.Bar, groundSpeedKts: 8);
        AircraftState departure = LinedUp("UAL42", oak.Runway, oak.Layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, oak.Runway, [departure, lineUp]);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Contains("past the hold line", finding.Description);
    }

    [Fact]
    public void Departure_LineUpHeldPastTheMarking_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState lineUp = LineUpAtTheMarking(oak.Runway, oak.Bar, groundSpeedKts: 0);
        lineUp.Ground.Hold = HoldDirective.HoldPosition;
        AircraftState departure = LinedUp("UAL43", oak.Runway, oak.Layout, alongFt: 0);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, oak.Runway, [departure, lineUp]);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Contains("past the hold line", finding.Description);
    }

    [Theory]
    [InlineData(-0.5, true)]
    [InlineData(0.5, false)]
    public void Departure_CrosserAgainstTheRollHorizon_ScoresOnlyWhenNotClearInTime(double rollMinusClearSeconds, bool scores)
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // The departure is placed so its roll reaches the crosser's along-track position half a second before, or after, the
        // go-around's projection has the crosser's tail off the far pavement edge.
        AircraftState crosser = MidCrossing(oak.Layout, oak.Runway, oak.Bar);
        GroundNode farBar = Assert.IsType<GroundNode>(OccupiedRunwayGoAround.CrossingFarBar(crosser, RunwayUseKind.Crossing, oak.Runway, oak.Layout));
        double clearSeconds = OccupiedRunwayGoAround.SecondsToCrossingClear(crosser, oak.Runway, oak.Layout, farBar);
        var profile = GroundRollProfile.For("B738", AircraftCategorization.Categorize("B738"));
        double rollFt = profile.DistanceKtSecondsAt(clearSeconds + rollMinusClearSeconds) * GeoMath.FeetPerNm / 3600.0;
        AircraftState departure = LinedUp("UAL23", oak.Runway, oak.Layout, alongFt: AlongFt(oak.Runway, crosser.Position) - rollFt);

        List<SoloTrainingEvent> notices = StartTheRoll(departure, oak.Runway, [departure, crosser]);

        Assert.Equal(scores, notices.Any(e => e.Title == TakeoffTitle));
    }

    [Fact]
    public void StopAndGo_OverAFouler_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);
        AircraftState lander = Lander("N62SG", oak.Runway, oak.Layout, new StopAndGoPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lander, fouler], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lander, oak.Runway, new StopAndGoPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lander, fouler], 11, AirspaceDatabase.Default);

        Assert.Single(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void StopAndGo_SecondRollPastAFouler_ScoresTheTakeoffOnly()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);
        AircraftState stopAndGo = OnCentreline("N63SG", oak.Runway, alongFt: 0);
        stopAndGo.Ground.Layout = oak.Layout;
        SetPhase(stopAndGo, oak.Runway, StopAndGo(oak.Runway, reaccelerating: false));
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([stopAndGo, fouler], 10, AirspaceDatabase.Default);
        SetPhase(stopAndGo, oak.Runway, StopAndGo(oak.Runway, reaccelerating: true));
        List<SoloTrainingEvent> notices = evaluator.Evaluate([stopAndGo, fouler], 11, AirspaceDatabase.Default);

        SoloTrainingEvent finding = Assert.Single(notices);
        Assert.Equal(TakeoffTitle, finding.Title);
        Assert.Equal([fouler.Callsign, stopAndGo.Callsign], finding.Callsigns);
    }

    [Fact]
    public void UnrestrictedLowApproachBelowFiveHundredFeet_OverAFouler_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);
        AircraftState lowApproach = Lander("N71LA", oak.Runway, oak.Layout, new LowApproachPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lowApproach, fouler], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lowApproach, oak.Runway, new LowApproachPhase());
        lowApproach.Altitude = oak.Runway.ElevationFt + 300;
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lowApproach, fouler], 11, AirspaceDatabase.Default);

        Assert.Single(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void RestrictedLowApproach_OverAnAircraftLinedUpAndWaiting_Scores()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState luaw = LinedUp("N72LU", runway, layout, alongFt: 4000);
        List<SoloTrainingEvent> notices = FlyRestrictedLowApproach(runway, layout, luaw, config: null);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Equal([luaw.Callsign, "N73LA"], finding.Callsigns);
    }

    [Fact]
    public void RestrictedLowApproach_OverAPrecedingLandingRollingOut_RecordsNoPairFinding()
    {
        // 3-10-10 NOTE 2: an altitude-restricted low approach is authorized over a preceding landing aircraft.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState lead = OnCentreline("AAL74", runway, alongFt: 1500);
        lead.IndicatedAirspeed = 100;
        SetPhase(lead, runway, new LandingPhase());

        List<SoloTrainingEvent> notices = FlyRestrictedLowApproach(runway, layout, lead, config: null);

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
    }

    [Fact]
    public void RestrictedLowApproach_OverATouchAndGoReaccelerating_RecordsArrivalBehindDeparture()
    {
        // 3-10-10 NOTE 2: a restricted low approach is not authorized over a departing aircraft. A touch-and-go re-accelerating
        // on the runway counts as a departure (3-8-2), so the 3-10-10 exemption does not apply and 3-10-3.a.2 judges the pair.
        List<string> titles = RestrictedLowApproachPairTitlesOver(
            new TouchAndGoPhase(),
            runway =>
                TouchAndGoPhase.FromSnapshot(
                    new TouchAndGoPhaseDto
                    {
                        Status = (int)PhaseStatus.Active,
                        ElapsedSeconds = 12,
                        FieldElevation = runway.ElevationFt,
                        RunwayHeadingDeg = runway.TrueHeading.Degrees,
                        ThresholdLat = runway.ThresholdLatitude,
                        ThresholdLon = runway.ThresholdLongitude,
                        RolloutDuration = 10,
                        RolloutElapsed = 10,
                        Reaccelerating = true,
                        Airborne = false,
                        RollElapsedSeconds = 2,
                    }
                ),
            (option, runway) => PlaceOnTheRunway(option, runway, alongFt: 3000, speedKts: 120)
        );

        Assert.Equal([ArrivalBehindDepartureTitle], titles);
    }

    [Fact]
    public void RestrictedLowApproach_OverATouchAndGoLiftedOff_RecordsArrivalBehindDeparture()
    {
        // A touch-and-go is a departure (3-8-2) through its liftoff and climb-out too: still no 3-10-10 exemption over it.
        List<string> titles = RestrictedLowApproachPairTitlesOver(
            new TouchAndGoPhase(),
            LiftedOffOptionPhases.TouchAndGo,
            (option, runway) => PlaceAirborneOverTheRunway(option, runway, alongFt: 1500)
        );

        Assert.Equal([ArrivalBehindDepartureTitle], titles);
    }

    [Fact]
    public void RestrictedLowApproach_OverAStoppedStopAndGo_RecordsArrivalBehindDeparture()
    {
        // A stop-and-go is a departure from its complete stop (3-8-2): no 3-10-10 exemption over it once stopped.
        List<string> titles = RestrictedLowApproachPairTitlesOver(
            new StopAndGoPhase(),
            runway => StopAndGo(runway, reaccelerating: false),
            (option, runway) => PlaceOnTheRunway(option, runway, alongFt: 3000, speedKts: 0)
        );

        Assert.Equal([ArrivalBehindDepartureTitle], titles);
    }

    private const string ArrivalBehindDepartureTitle = "Arrival behind departure same-runway separation";

    /// <summary>
    /// The pair-finding titles when a restricted low approach (600 ft) crosses the OAK 28R threshold behind an option that
    /// crossed it first in <paramref name="thresholdPhase"/> and is then flying <paramref name="phaseAhead"/>, placed by
    /// <paramref name="place"/>.
    /// </summary>
    private static List<string> RestrictedLowApproachPairTitlesOver(
        Phase thresholdPhase,
        Func<RunwayInfo, Phase> phaseAhead,
        Action<AircraftState, RunwayInfo> place
    )
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState option = Lander("N76TG", runway, layout, thresholdPhase);
        AircraftState lowApproach = Lander("N77LA", runway, layout, new LowApproachPhase());
        lowApproach.Position = PositionOnRunway(runway, -20000);
        lowApproach.Altitude = runway.ElevationFt + 600;
        List<AircraftState> all = [option, lowApproach];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        CrossTheThreshold(option, runway, thresholdPhase);
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        place(option, runway);
        SetPhase(option, runway, phaseAhead(runway));
        lowApproach.Position = PositionOnRunway(runway, -300);
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        CrossTheThreshold(lowApproach, runway, new LowApproachPhase());
        lowApproach.Altitude = runway.ElevationFt + 600;
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        return [.. notices.Where(e => e.Category == SoloTrainingEventCategory.RunwayWake).Select(e => e.Title)];
    }

    private static void PlaceOnTheRunway(AircraftState aircraft, RunwayInfo runway, double alongFt, double speedKts)
    {
        aircraft.Position = PositionOnRunway(runway, alongFt);
        aircraft.IsOnGround = true;
        aircraft.Altitude = runway.ElevationFt;
        aircraft.IndicatedAirspeed = speedKts;
    }

    private static void PlaceAirborneOverTheRunway(AircraftState aircraft, RunwayInfo runway, double alongFt)
    {
        aircraft.Position = PositionOnRunway(runway, alongFt);
        aircraft.IsOnGround = false;
        aircraft.Altitude = runway.ElevationFt + 100;
        aircraft.IndicatedAirspeed = 80;
    }

    [Fact]
    public void UnrestrictedLowApproach_OverAPrecedingLandingRollingOut_RecordsArrivalBehindLanding()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState lead = OnCentreline("AAL75", runway, alongFt: 1500);
        lead.IndicatedAirspeed = 100;
        SetPhase(lead, runway, new LandingPhase());
        AircraftState lowApproach = Lander("N75LA", runway, layout, new LowApproachPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lead, lowApproach], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lowApproach, runway, new LowApproachPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lead, lowApproach], 11, AirspaceDatabase.Default);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal("Arrival behind landing same-runway separation", pair.Title);
    }

    [Fact]
    public void Landing_LahsoArrival_CrosserBeyondItsHoldShortPoint_DoesNotScore()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // 3-7-2.g.3: the arrival holds short 200 ft before the crossing, so the crossing may proceed beyond it.
        AircraftState crosser = MidCrossing(oak.Layout, oak.Runway, oak.Bar);

        List<SoloTrainingEvent> notices = LandAndHoldShort(oak, crosser, holdShortPastUserFt: -200.0);

        Assert.DoesNotContain(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void Landing_LahsoArrival_CrosserShortOfItsHoldShortPoint_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // The hold-short point is 200 ft past the crossing: the crossing is inside the arrival's landing distance.
        AircraftState crosser = MidCrossing(oak.Layout, oak.Runway, oak.Bar);

        List<SoloTrainingEvent> notices = LandAndHoldShort(oak, crosser, holdShortPastUserFt: 200.0);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Contains("crossing the runway", finding.Description);
    }

    [Fact]
    public void Landing_LahsoArrival_FoulerBeyondItsHoldShortPoint_DoesNotScore()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // A fouler held past a bar beyond the hold-short point occupies less of the runway than a crossing there (3-7-2.g.3).
        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);

        List<SoloTrainingEvent> notices = LandAndHoldShort(oak, fouler, holdShortPastUserFt: -200.0);

        Assert.DoesNotContain(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void Landing_LahsoArrival_EarlierLahsoHoldBeyondItsHoldShortPoint_Scores()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // An earlier LAHSO arrival holding short on the runway is a landed aircraft (3-10-3.a.1), not a crossing, wherever it is.
        double displacementFt = LandingThreshold.DisplacementFt(oak.Runway, oak.Layout);
        AircraftState held = OnCentreline("SKW13", oak.Runway, alongFt: displacementFt + 3000.0);
        SetPhase(held, oak.Runway, new RunwayHoldingPhase("28R"));
        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(held, oak.Runway, oak.Layout)?.Kind);

        List<SoloTrainingEvent> notices = LandAndHoldShort(oak, held, holdShortPastUserFt: -200.0);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Equal([held.Callsign, "AAL12"], finding.Callsigns);
        Assert.Contains("holding short on the runway", finding.Description);
    }

    [Fact]
    public void RestrictedLowApproach_OnAnIntersectingRunwayBehindALanding_RecordsThePairFinding()
    {
        // 3-10-10 is deferred to by 3-10-3.a (same runway) only; 3-10-4 (intersecting runways) has no such clause.
        // SFO 28R and 1L cross; the lead rolls out on 28R short of the intersection.
        RunwayInfo runway28R = Runway("SFO", "28R");
        RunwayInfo runway1L = Runway("SFO", "1L");
        AirportGroundLayout layout = Layout("SFO");
        double intersectionFt = CentrelineIntersection(runway28R, runway1L).FirstDistFromThresholdNm * GeoMath.FeetPerNm;
        AircraftState lead = OnCentreline("AAL93", runway28R, alongFt: intersectionFt / 2.0);
        lead.IndicatedAirspeed = 100;
        lead.Ground.Layout = layout;
        SetPhase(lead, runway28R, new LandingPhase());

        List<SoloTrainingEvent> notices = FlyRestrictedLowApproach(runway1L, layout, lead, config: null);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal("Arrival behind landing intersecting-runway separation", pair.Title);
    }

    [Theory]
    [InlineData("OAK", true)]
    [InlineData("SFO", false)]
    public void RestrictedLowApproach_OverALineUp_ScoresOnlyWithoutFullSafetyLogic(string airport, bool scores)
    {
        // 3-9-4.c.1(a) forbids it over an aircraft taxiing to LUAW without full safety logic; 3-9-4.c.2(a) permits it with it.
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        Assert.Equal(!scores, AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(zoa, LoadSeed(zoa), airport));
        RunwayInfo runway = Runway(airport, "28R");
        AircraftState lineUp = OnCentreline("N85LU", runway, alongFt: 5000);
        lineUp.IndicatedAirspeed = 5;
        SetPhase(lineUp, runway, new LineUpPhase());

        List<SoloTrainingEvent> notices = FlyRestrictedLowApproach(runway, Layout(airport), lineUp, zoa);

        Assert.Equal(scores, notices.Any(e => e.Title == LandingTitle));
    }

    [Theory]
    [InlineData("SJC", true)]
    [InlineData("SFO", false)]
    public void Landing_BehindTheLandingThreshold_LineUpScoresOnlyWithoutFullSafetyLogic(string airport, bool scores)
    {
        // 3-9-4.c.1(a) names an aircraft "taxiing to LUAW" with one holding in position, anywhere on the runway. OAK has no
        // landing threshold displaced far enough to line up behind; SJC, like it, has no full safety logic.
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        Assert.Equal(!scores, AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(zoa, LoadSeed(zoa), airport));
        (RunwayInfo runway, AirportGroundLayout layout, double displacementFt) = DisplacedRunway(airport);
        AircraftState lineUp = OnCentreline("N86LU", runway, alongFt: displacementFt / 2.0);
        lineUp.IndicatedAirspeed = 5;
        SetPhase(lineUp, runway, new LineUpPhase());

        List<SoloTrainingEvent> notices = LandOver(runway, layout, zoa, lineUp);

        Assert.Equal(scores, notices.Any(e => e.Title == LandingTitle));
    }

    [Fact]
    public void Landing_BehindTheLandingThreshold_LineUpForAnotherRunway_DoesNotScore()
    {
        // Lining up for the parallel runway, its path across this runway's displaced area: a crossing behind the landing, not
        // an aircraft taxiing onto this runway to line up (3-9-4.c.1(a)).
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        Assert.False(AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(zoa, LoadSeed(zoa), "SJC"));
        (RunwayInfo runway, AirportGroundLayout layout, double displacementFt) = DisplacedRunway("SJC");
        string parallel = runway.Designator.EndsWith('L') ? runway.Designator[..^1] + "R" : runway.Designator[..^1] + "L";
        AircraftState lineUp = CrossingInPlace("N87LU", runway, alongFt: displacementFt / 2.0);
        lineUp.IndicatedAirspeed = 5;
        SetPhase(lineUp, Runway("SJC", parallel), new LineUpPhase());
        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(lineUp, runway, layout)?.Kind);

        List<SoloTrainingEvent> notices = LandOver(runway, layout, zoa, lineUp);

        Assert.DoesNotContain(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void Departure_BehindADepartureThatRejectedOnTheRunway_IsScoredOnceByThePairRule()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState d1 = LinedUp("UAL86", runway, layout, alongFt: 1000);
        AircraftState d2 = LinedUp("UAL87", runway, layout, alongFt: 0);
        List<AircraftState> all = [d1, d2];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        SetPhase(d1, runway, new TakeoffPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        d1.Position = PositionOnRunway(runway, 3000);
        d1.IndicatedAirspeed = 30;
        SetPhase(d1, runway, new RejectedTakeoffPhase(10.0));
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        SetPhase(d2, runway, new TakeoffPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal("Departure behind departure same-runway separation", pair.Title);
    }

    [Fact]
    public void StopAndGo_OnCrossRunwayClosedTraffic_IsJudgedOnItsPatternRunway()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "28R") is not { } oak)
        {
            return;
        }

        // Took off on 33, flies the pattern to 28R: DepartureRunway still names 33, the stop-and-go rolls again on 28R.
        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, assignedRunway: null);
        Assert.True(AlongFt(oak.Runway, fouler.Position) > 0.0);
        AircraftState stopAndGo = OnCentreline("N64SG", oak.Runway, alongFt: 0);
        stopAndGo.Ground.Layout = oak.Layout;
        SetPhase(stopAndGo, oak.Runway, StopAndGo(oak.Runway, reaccelerating: false));
        stopAndGo.Phases!.DepartureRunway = Runway("OAK", "33");
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([stopAndGo, fouler], 10, AirspaceDatabase.Default);
        SetPhase(stopAndGo, oak.Runway, StopAndGo(oak.Runway, reaccelerating: true));
        stopAndGo.Phases!.DepartureRunway = Runway("OAK", "33");
        List<SoloTrainingEvent> notices = evaluator.Evaluate([stopAndGo, fouler], 11, AirspaceDatabase.Default);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Equal("28R", finding.RunwayId);
        Assert.Equal([fouler.Callsign, stopAndGo.Callsign], finding.Callsigns);
    }

    [Fact]
    public void StopAndGo_SnapshotMidSecondRoll_RestoresTheSameNextFinding()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        SimulationEngine original = SoloEngine();
        AircraftState stopAndGo = OnCentreline("N65SG", runway, alongFt: 2000);
        stopAndGo.Ground.Layout = layout;
        SetPhase(stopAndGo, runway, StopAndGo(runway, reaccelerating: false));
        original.World.AddAircraft(stopAndGo);
        original.World.AddAircraft(Lander("AAL66", runway, layout, new LandingPhase()));
        original.Scenario!.ElapsedSeconds = 10;
        original.TickSoloTrainingEvaluation();
        SetPhase(stopAndGo, runway, StopAndGo(runway, reaccelerating: true));
        original.Scenario.ElapsedSeconds = 11;
        original.TickSoloTrainingEvaluation();
        StateSnapshotDto snapshot = original.CaptureSnapshot();
        SimulationEngine restored = SoloEngine();
        restored.RestoreFromSnapshot(snapshot);
        restored.Scenario!.SoloTrainingMode = true;

        SoloTrainingEvent fromOriginal = Assert.Single(
            LandInEngine(original, runway, "AAL66"),
            e => e.Category == SoloTrainingEventCategory.RunwayWake
        );
        SoloTrainingEvent fromRestored = Assert.Single(
            LandInEngine(restored, runway, "AAL66"),
            e => e.Category == SoloTrainingEventCategory.RunwayWake
        );

        Assert.Equal("Arrival behind departure same-runway separation", fromOriginal.Title);
        Assert.Equal(fromOriginal.Title, fromRestored.Title);
    }

    [Fact]
    public void Departure_BehindAShadowLanderTurningOff_IsScoredOnceByThePairRule()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState shadow = OnCentreline("SWA88", runway, alongFt: -300);
        shadow.IsOnGround = false;
        shadow.Altitude = runway.ElevationFt + 40;
        shadow.IndicatedAirspeed = 140;
        shadow.Ground.Layout = layout;
        shadow.FlightPlan.Destination = "OAK";
        shadow.LiveTraffic = new AircraftLiveTraffic();
        AircraftState departure = LinedUp("UAL88", runway, layout, alongFt: 0);
        List<AircraftState> all = [shadow, departure];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        shadow.Position = PositionOnRunway(runway, 100);
        shadow.Altitude = runway.ElevationFt + 30;
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        // Turning off at an exit: on the pavement, 45 degrees off the runway axis, stopped there.
        shadow.Position = PositionOnRunway(runway, 5000);
        shadow.IsOnGround = true;
        shadow.Altitude = runway.ElevationFt;
        shadow.IndicatedAirspeed = 0;
        shadow.TrueHeading = new TrueHeading(runway.TrueHeading.Degrees + 45.0);
        shadow.TrueTrack = shadow.TrueHeading;
        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(shadow, runway, layout)?.Kind);
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        SetPhase(departure, runway, new TakeoffPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal("Departure behind landing same-runway separation", pair.Title);
    }

    [Fact]
    public void Departure_PastAnAircraftRollingOnAnIntersectingRunwayAfterItsLanding_DoesNotScoreIt()
    {
        // X landed 28R (still the runway's last operation), then departed 1L and rolls through the 28R intersection: its 1L
        // departure is its latest operation, which the intersecting-runway pair rule judges.
        RunwayInfo runway28R = Runway("SFO", "28R");
        RunwayInfo runway1L = Runway("SFO", "1L");
        AirportGroundLayout layout = Layout("SFO");
        AircraftState x = Lander("N89X", runway28R, layout, new LandingPhase());
        AircraftState departure = LinedUp("UAL89", runway28R, layout, alongFt: 0);
        List<AircraftState> all = [x, departure];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        CrossTheThreshold(x, runway28R, new LandingPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        x.Position = PositionOnRunway(runway1L, 0);
        x.TrueHeading = runway1L.TrueHeading;
        x.TrueTrack = runway1L.TrueHeading;
        x.IsOnGround = true;
        x.Altitude = runway1L.ElevationFt;
        x.IndicatedAirspeed = 0;
        SetPhase(x, runway1L, new LinedUpAndWaitingPhase());
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        SetPhase(x, runway1L, new TakeoffPhase());
        evaluator.Evaluate(all, 13, AirspaceDatabase.Default);
        (double intersectionLat, double intersectionLon, _, _) = CentrelineIntersection(runway28R, runway1L);
        x.Position = new LatLon(intersectionLat, intersectionLon);
        x.IndicatedAirspeed = 80;
        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(x, runway28R, layout)?.Kind);
        evaluator.Evaluate(all, 14, AirspaceDatabase.Default);
        SetPhase(departure, runway28R, new TakeoffPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 15, AirspaceDatabase.Default);

        Assert.DoesNotContain(notices, e => e.Title == TakeoffTitle);
    }

    [Fact]
    public void Landing_BehindTheLandingThreshold_LinedUpScoresWithoutSafetyLogicButACrosserDoesNot()
    {
        // SJC has no full safety logic (ZOA snapshot): an aircraft lined up on the displaced area behind the landing threshold
        // still scores (3-9-4.c.1(a)); a crossing there is behind the landing and does not.
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        Assert.False(AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(zoa, LoadSeed(zoa), "SJC"));
        (RunwayInfo runway, AirportGroundLayout layout, double displacementFt) = DisplacedRunway("SJC");
        AircraftState luaw = LinedUp("N76LU", runway, layout, alongFt: displacementFt / 2.0);
        AircraftState crosser = CrossingInPlace("N77CR", runway, alongFt: displacementFt / 2.0 + 100.0);

        List<SoloTrainingEvent> notices = LandOver(runway, layout, zoa, luaw, crosser);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Equal([luaw.Callsign, "AAL78"], finding.Callsigns);
        Assert.Equal(LandingRuleReference, finding.RuleReference);
    }

    [Fact]
    public void Landing_BehindTheLandingThreshold_LinedUpDoesNotScoreWithFullSafetyLogic()
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        Assert.True(AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(zoa, LoadSeed(zoa), "SFO"));
        (RunwayInfo runway, AirportGroundLayout layout, double displacementFt) = DisplacedRunway("SFO");
        AircraftState luaw = LinedUp("N79LU", runway, layout, alongFt: displacementFt / 2.0);

        List<SoloTrainingEvent> notices = LandOver(runway, layout, zoa, luaw);

        Assert.DoesNotContain(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void RunwayUser_ScoresAtAsdexAirportWhileFacilityOnLimited()
    {
        // SFO's Safety Logic on LIMITED raises closed-runway alerts only: the 3-9-4.c.1(a) restriction applies again, and the
        // aircraft lined up behind the landing threshold scores as it does at an airport without full safety logic.
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> seed = LoadSeed(zoa);
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> limited = seed.SetItem(
            "SFO",
            seed["SFO"] with
            {
                RunwayConfigurationId = "limited",
            }
        );
        (RunwayInfo runway, AirportGroundLayout layout, double displacementFt) = DisplacedRunway("SFO");
        AircraftState luaw = LinedUp("N80LU", runway, layout, alongFt: displacementFt / 2.0);

        List<SoloTrainingEvent> notices = LandOverWithLiveConfigs(runway, layout, zoa, limited, luaw);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Equal([luaw.Callsign, "AAL78"], finding.Callsigns);
    }

    [Theory]
    [InlineData("limited", true)]
    [InlineData(null, false)] // the configuration the scenario load seeded, a named one
    public void Engine_RunwayUser_AtSfo_ScoresOnlyWhileTheFacilityIsOnLimited(string? configurationId, bool scores)
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(
            RunwayOccupancyWarningTests.OneAircraftAtSfo,
            7,
            MagneticDeclination.EvaluationDateUtc,
            scenario => scenario.ArtccConfig = ZoaArtccConfig.Full
        );
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        AsdexSafetyLogicConfig sfo = scenario.AsdexSafetyLogicConfigs["SFO"];
        Assert.True(AsdexSafetyLogicSeed.NamesRunwayConfiguration(scenario.ArtccConfig, "SFO", sfo.RunwayConfigurationId));
        if (configurationId is not null)
        {
            scenario.AsdexSafetyLogicConfigs = scenario.AsdexSafetyLogicConfigs.SetItem("SFO", sfo with { RunwayConfigurationId = configurationId });
        }

        (RunwayInfo runway, AirportGroundLayout layout, double displacementFt) = DisplacedRunway("SFO");
        engine.World.AddAircraft(LinedUp("N82LU", runway, layout, alongFt: displacementFt / 2.0));
        engine.World.AddAircraft(Lander("AAL83", runway, layout, new LandingPhase()));

        List<SoloTrainingEvent> notices = LandInEngine(engine, runway, "AAL83");

        Assert.Equal(scores, notices.Any(e => e.Title == LandingTitle));
    }

    [Fact]
    public void RunwayUser_NotScoredAtAsdexAirportOnNamedConfiguration()
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> seed = LoadSeed(zoa);
        Assert.True(AsdexSafetyLogicSeed.NamesRunwayConfiguration(zoa, "SFO", seed["SFO"].RunwayConfigurationId));
        (RunwayInfo runway, AirportGroundLayout layout, double displacementFt) = DisplacedRunway("SFO");
        AircraftState luaw = LinedUp("N81LU", runway, layout, alongFt: displacementFt / 2.0);

        List<SoloTrainingEvent> notices = LandOverWithLiveConfigs(runway, layout, zoa, seed, luaw);

        Assert.DoesNotContain(notices, e => e.Title == LandingTitle);
    }

    [Fact]
    public void Departure_PastALandingStillRollingOutBehindATouchAndGo_ScoresTheLanding()
    {
        // L1 rolls out; a touch-and-go crosses the threshold behind it and lifts off; D starts its roll with L1 still on the
        // runway. The pair rule judges D against the touch-and-go only, so L1 is a runway user.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState l1 = OnCentreline("AAL82", runway, alongFt: 3000);
        l1.IndicatedAirspeed = 60;
        SetPhase(l1, runway, new LandingPhase());
        AircraftState touchAndGo = Lander("N82TG", runway, layout, new TouchAndGoPhase());
        AircraftState departure = LinedUp("UAL82", runway, layout, alongFt: 0);
        List<AircraftState> all = [l1, touchAndGo, departure];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        CrossTheThreshold(touchAndGo, runway, new TouchAndGoPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        touchAndGo.Position = PositionOnRunway(runway, 6000);
        touchAndGo.Altitude = runway.ElevationFt + 300;
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        SetPhase(departure, runway, new TakeoffPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == TakeoffTitle);
        Assert.Equal([l1.Callsign, departure.Callsign], finding.Callsigns);
    }

    [Fact]
    public void Landing_OverAPatternAircraftNowLinedUpAfterItsOwnDepartureAndLanding_Scores()
    {
        // N1 departed 28R, flew the pattern and landed (both operations recorded), and now waits lined up on 28R. Neither of its
        // operations is under way, so it is an ordinary runway user at the next landing.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState n1 = OnCentreline("N1PT", runway, alongFt: 0);
        n1.IndicatedAirspeed = 40;
        n1.Ground.Layout = layout;
        SetPhase(n1, runway, new TakeoffPhase());
        AircraftState lander = Lander("AAL83", runway, layout, new FinalApproachPhase());
        lander.Position = PositionOnRunway(runway, -20000);
        List<AircraftState> all = [n1, lander];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        n1.Position = PositionOnRunway(runway, -300);
        n1.IsOnGround = false;
        n1.Altitude = runway.ElevationFt + 40;
        n1.IndicatedAirspeed = 70;
        SetPhase(n1, runway, new FinalApproachPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        CrossTheThreshold(n1, runway, new LandingPhase());
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        n1.Position = PositionOnRunway(runway, 4000);
        n1.IsOnGround = true;
        n1.Altitude = runway.ElevationFt;
        n1.IndicatedAirspeed = 0;
        SetPhase(n1, runway, new LinedUpAndWaitingPhase());
        lander.Position = PositionOnRunway(runway, -300);
        evaluator.Evaluate(all, 13, AirspaceDatabase.Default);
        CrossTheThreshold(lander, runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 14, AirspaceDatabase.Default);

        SoloTrainingEvent finding = Assert.Single(notices, e => e.Title == LandingTitle);
        Assert.Equal([n1.Callsign, lander.Callsign], finding.Callsigns);
    }

    [Fact]
    public void Finding_EndsWhenTheDepartureIsAirborne()
    {
        (SoloTrainingEvaluator evaluator, AircraftState departure, AircraftState ahead, SoloTrainingEvent finding) =
            DepartureFindingOverALinedUpAircraft("UAL91");

        departure.Position = PositionOnRunway(Runway("OAK", "28R"), 5000);
        departure.IsOnGround = false;
        departure.Altitude += 100;
        evaluator.Evaluate([departure, ahead], 12, AirspaceDatabase.Default);

        Assert.False(Timeline(evaluator, 12).Single(e => e.Id == finding.Id).IsActive);
    }

    [Fact]
    public void Finding_EndsWhenTheDepartureRejectsItsTakeoff()
    {
        (SoloTrainingEvaluator evaluator, AircraftState departure, AircraftState ahead, SoloTrainingEvent finding) =
            DepartureFindingOverALinedUpAircraft("UAL92");

        departure.IndicatedAirspeed = 50;
        SetPhase(departure, Runway("OAK", "28R"), new RejectedTakeoffPhase(10.0));
        evaluator.Evaluate([departure, ahead], 12, AirspaceDatabase.Default);

        Assert.False(Timeline(evaluator, 12).Single(e => e.Id == finding.Id).IsActive);
    }

    [Fact]
    public void TouchAndGo_InsideTheCwtSpacingBehindAHeavyLanding_RecordsTheApproachWakeFinding()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AircraftState lead = OnCentreline("UAE1", runway, alongFt: -100);
        lead.AircraftType = "A388";
        lead.IsOnGround = false;
        lead.Altitude = runway.ElevationFt + 100;
        lead.IndicatedAirspeed = 140;
        SetPhase(lead, runway, new FinalApproachPhase());
        AircraftState touchAndGo = OnCentreline("N84TG", runway, alongFt: -6.0 * GeoMath.FeetPerNm);
        touchAndGo.IsOnGround = false;
        touchAndGo.Altitude = runway.ElevationFt + 1900;
        touchAndGo.IndicatedAirspeed = 140;
        SetPhase(touchAndGo, runway, new TouchAndGoPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lead, touchAndGo], 300, AirspaceDatabase.Default);
        lead.Position = PositionOnRunway(runway, 0);
        SetPhase(lead, runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate([lead, touchAndGo], 301, AirspaceDatabase.Default);

        SoloTrainingEvent wake = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Contains("7 NM", wake.RequiredText);
    }

    [Fact]
    public void Landing_BehindATouchAndGoStillRolling_RecordsTheArrivalBehindDepartureFinding()
    {
        // 7110.65 3-8-2 makes a touch-and-go a departure from touchdown; YAAT classifies it at liftoff as a simplification and
        // judges its ground interval by 3-10-3.a.2. A Category I touch-and-go rolling 4,000 ft down earns no a.1 landed credit
        // behind it, and on the ground it does not meet a.2.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState touchAndGo = Lander("N86TG", runway, layout, new TouchAndGoPhase());
        touchAndGo.AircraftType = "C172";
        AircraftState arrival = Lander("N87AR", runway, layout, new FinalApproachPhase());
        arrival.AircraftType = "C172";
        arrival.Position = PositionOnRunway(runway, -20000);
        List<AircraftState> all = [touchAndGo, arrival];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        CrossTheThreshold(touchAndGo, runway, new TouchAndGoPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        touchAndGo.Position = PositionOnRunway(runway, 4000);
        touchAndGo.IsOnGround = true;
        touchAndGo.Altitude = runway.ElevationFt;
        touchAndGo.IndicatedAirspeed = 40;
        arrival.Position = PositionOnRunway(runway, -300);
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        CrossTheThreshold(arrival, runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal("Arrival behind departure same-runway separation", pair.Title);
    }

    [Fact]
    public void BrakingStopAndGoAhead_CatIArrivalCrossesAt3500FtByDay_NoFinding()
    {
        // 7110.65 3-8-2: a stop-and-go is an arrival until it makes a complete stop. Still braking 3,500 ft down, a Category I
        // stop-and-go is a landed aircraft past the 3-10-3.a.1 landmark (3,000 ft) for the Category I arrival behind it.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState stopAndGo = Lander("N91SG", runway, layout, new StopAndGoPhase());
        stopAndGo.AircraftType = "C172";
        AircraftState arrival = Lander("N92AR", runway, layout, new FinalApproachPhase());
        arrival.AircraftType = "C172";
        arrival.Position = PositionOnRunway(runway, -20000);
        List<AircraftState> all = [stopAndGo, arrival];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        CrossTheThreshold(stopAndGo, runway, new StopAndGoPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        stopAndGo.Position = PositionOnRunway(runway, 3500);
        stopAndGo.IsOnGround = true;
        stopAndGo.Altitude = runway.ElevationFt;
        stopAndGo.IndicatedAirspeed = 30;
        arrival.Position = PositionOnRunway(runway, -300);
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        CrossTheThreshold(arrival, runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        Assert.False(((StopAndGoPhase)stopAndGo.Phases!.CurrentPhase!).HasStopped);
        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
    }

    [Fact]
    public void TouchAndGo_FirstSeenAirborne_IsRecordedAsWhenWatchedThroughItsLanding()
    {
        // Watched without a break, a touch-and-go records one operation, its landing at the threshold crossing; a stop-and-go
        // records its second roll as well. Seen first after liftoff, it is recorded the same way, not as a fresh departure.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");

        List<string> watched = ArrivalPairFindingsBehindAnAirborneTouchAndGo(runway, layout, watchTheLanding: true);
        List<string> seeded = ArrivalPairFindingsBehindAnAirborneTouchAndGo(runway, layout, watchTheLanding: false);

        Assert.Equal([ArrivalBehindDepartureTitle], watched);
        Assert.Equal(watched, seeded);
    }

    [Fact]
    public void TouchAndGoLiftedOff1500FtAhead_C172Arrival_RecordsArrivalBehindDeparture()
    {
        // 7110.65 3-8-2: a touch-and-go is a departure from touchdown, through its liftoff. Airborne 1,500 ft down, a Category I
        // touch-and-go is short of the 3-10-3.a.2 distance (3,000 ft) for the Category I arrival crossing behind it.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState touchAndGo = Lander("N93TG", runway, layout, new TouchAndGoPhase());
        touchAndGo.AircraftType = "C172";
        AircraftState arrival = Lander("N94AR", runway, layout, new FinalApproachPhase());
        arrival.AircraftType = "C172";
        arrival.Position = PositionOnRunway(runway, -20000);
        List<AircraftState> all = [touchAndGo, arrival];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        CrossTheThreshold(touchAndGo, runway, new TouchAndGoPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        PlaceAirborneOverTheRunway(touchAndGo, runway, alongFt: 1500);
        SetPhase(touchAndGo, runway, LiftedOffOptionPhases.TouchAndGo(runway));
        arrival.Position = PositionOnRunway(runway, -300);
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        CrossTheThreshold(arrival, runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        SoloTrainingEvent pair = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(ArrivalBehindDepartureTitle, pair.Title);
    }

    [Fact]
    public void StopAndGoLiftingOffWhileItsFindingIsOpen_IsStillJudgedAsADeparture()
    {
        // The arrival crossed behind a Category I stop-and-go still braking 1,000 ft down: an open 3-10-3.a.1 finding. The
        // stop-and-go then stops, rolls and lifts off — a departure from its stop (3-8-2), judged by 3-10-3.a.2 — so airborne
        // only 1,500 ft down, short of 3,000 ft, the finding stays open rather than ending on the liftoff tick.
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState stopAndGo = Lander("N95SG", runway, layout, new StopAndGoPhase());
        stopAndGo.AircraftType = "C172";
        AircraftState arrival = Lander("N96AR", runway, layout, new FinalApproachPhase());
        arrival.AircraftType = "C172";
        arrival.Position = PositionOnRunway(runway, -20000);
        List<AircraftState> all = [stopAndGo, arrival];
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
        CrossTheThreshold(stopAndGo, runway, new StopAndGoPhase());
        evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        PlaceOnTheRunway(stopAndGo, runway, alongFt: 1000, speedKts: 30);
        arrival.Position = PositionOnRunway(runway, -300);
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        CrossTheThreshold(arrival, runway, new LandingPhase());
        SoloTrainingEvent finding = Assert.Single(
            evaluator.Evaluate(all, 13, AirspaceDatabase.Default),
            e => e.Category == SoloTrainingEventCategory.RunwayWake
        );
        PlaceAirborneOverTheRunway(stopAndGo, runway, alongFt: 1500);
        SetPhase(stopAndGo, runway, LiftedOffOptionPhases.StopAndGo(runway));
        evaluator.Evaluate(all, 14, AirspaceDatabase.Default);

        Assert.Equal("Arrival behind landing same-runway separation", finding.Title);
        Assert.True(Timeline(evaluator, 14).Single(e => e.Id == finding.Id).IsActive);
    }

    /// <summary>
    /// The pair-finding titles when an arrival crosses the threshold behind a touch-and-go lifted off 1,500 ft down, the
    /// touch-and-go first seen either short of the threshold (<paramref name="watchTheLanding"/>) or only after its liftoff.
    /// </summary>
    private static List<string> ArrivalPairFindingsBehindAnAirborneTouchAndGo(RunwayInfo runway, AirportGroundLayout layout, bool watchTheLanding)
    {
        AircraftState touchAndGo = Lander("N88TG", runway, layout, new TouchAndGoPhase());
        AircraftState arrival = Lander("N89AR", runway, layout, new FinalApproachPhase());
        arrival.Position = PositionOnRunway(runway, -20000);
        List<AircraftState> all = [touchAndGo, arrival];
        var evaluator = new SoloTrainingEvaluator();

        if (watchTheLanding)
        {
            evaluator.Evaluate(all, 10, AirspaceDatabase.Default);
            CrossTheThreshold(touchAndGo, runway, new TouchAndGoPhase());
            evaluator.Evaluate(all, 11, AirspaceDatabase.Default);
        }
        else
        {
            evaluator.Evaluate([arrival], 10, AirspaceDatabase.Default);
            evaluator.Evaluate([arrival], 11, AirspaceDatabase.Default);
        }

        touchAndGo.Position = PositionOnRunway(runway, 1500);
        touchAndGo.Altitude = runway.ElevationFt + 100;
        touchAndGo.IsOnGround = false;
        SetPhase(touchAndGo, runway, LiftedOffOptionPhases.TouchAndGo(runway));
        arrival.Position = PositionOnRunway(runway, -300);
        evaluator.Evaluate(all, 12, AirspaceDatabase.Default);
        CrossTheThreshold(arrival, runway, new LandingPhase());
        List<SoloTrainingEvent> notices = evaluator.Evaluate(all, 13, AirspaceDatabase.Default);

        return [.. notices.Where(e => e.Category == SoloTrainingEventCategory.RunwayWake).Select(e => e.Title)];
    }

    private static (SoloTrainingEvaluator, AircraftState, AircraftState, SoloTrainingEvent) DepartureFindingOverALinedUpAircraft(string callsign)
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AirportGroundLayout layout = Layout("OAK");
        AircraftState ahead = LinedUp("N90AH", runway, layout, alongFt: 6000);
        AircraftState departure = LinedUp(callsign, runway, layout, alongFt: 0);
        var evaluator = new SoloTrainingEvaluator();
        SoloTrainingEvent finding = Assert.Single(StartTheRoll(departure, runway, [departure, ahead], evaluator), e => e.Title == TakeoffTitle);
        return (evaluator, departure, ahead, finding);
    }

    private static List<SoloTrainingEvent> FlyRestrictedLowApproach(
        RunwayInfo runway,
        AirportGroundLayout layout,
        AircraftState other,
        ArtccConfigRoot? config
    )
    {
        AircraftState lowApproach = Lander("N73LA", runway, layout, new LowApproachPhase());
        lowApproach.Altitude = runway.ElevationFt + 600;
        var context = new SoloTrainingServiceContext(
            InitialContactEligibilityContext.Empty,
            WakeDirectiveCatalog.Empty,
            config,
            (config is null) ? AsdexSafetyLogicSeed.Empty : LoadSeed(config),
            SoloTrainingServiceContext.NoSimulatedTower
        );
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([other, lowApproach], 10, AirspaceDatabase.Default, context);
        CrossTheThreshold(lowApproach, runway, new LowApproachPhase());
        lowApproach.Altitude = runway.ElevationFt + 600;
        return evaluator.Evaluate([other, lowApproach], 11, AirspaceDatabase.Default, context);
    }

    /// <summary>
    /// An arrival crosses the landing threshold cleared to hold short (LAHSO) at a point <paramref name="holdShortPastUserFt"/>
    /// along the runway from <paramref name="user"/> (negative: before it).
    /// </summary>
    private static List<SoloTrainingEvent> LandAndHoldShort(
        (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar) oak,
        AircraftState user,
        double holdShortPastUserFt
    )
    {
        double displacementFt = LandingThreshold.DisplacementFt(oak.Runway, oak.Layout);
        double holdShortFt = AlongFt(oak.Runway, user.Position) - displacementFt + holdShortPastUserFt;
        Assert.True(holdShortFt > 0.0, $"the hold-short point is {holdShortFt:F0} ft past the landing threshold");
        AircraftState lander = Lander("AAL12", oak.Runway, oak.Layout, new LandingPhase());
        var evaluator = new SoloTrainingEvaluator();

        evaluator.Evaluate([lander, user], 10, AirspaceDatabase.Default);
        CrossTheThreshold(lander, oak.Runway, new LandingPhase());
        LatLon holdShort = PositionOnRunway(oak.Runway, displacementFt + holdShortFt);
        lander.Phases!.LahsoHoldShort = new LahsoTarget
        {
            Lat = holdShort.Lat,
            Lon = holdShort.Lon,
            DistFromThresholdNm = holdShortFt / GeoMath.FeetPerNm,
            CrossingRunwayId = "28R",
        };
        return evaluator.Evaluate([lander, user], 11, AirspaceDatabase.Default);
    }

    private static List<SoloTrainingEvent> LandInEngine(SimulationEngine engine, RunwayInfo runway, string landerCallsign)
    {
        engine.Scenario!.ElapsedSeconds = 12;
        engine.TickSoloTrainingEvaluation();
        CrossTheThreshold(engine.World.FindAircraft(landerCallsign)!, runway, new LandingPhase());
        engine.Scenario.ElapsedSeconds = 13;
        return [.. engine.TickSoloTrainingEvaluation()];
    }

    /// <summary>Where the two runways cross (<see cref="RunwayIntersectionCalculator.FindIntersection(RunwayInfo, RunwayInfo)"/>).</summary>
    private static (double Lat, double Lon, double FirstDistFromThresholdNm, double SecondDistFromThresholdNm) CentrelineIntersection(
        RunwayInfo a,
        RunwayInfo b
    ) => Assert.NotNull(RunwayIntersectionCalculator.FindIntersection(a, b));

    private static List<SoloTrainingEvent> LandOver(
        RunwayInfo runway,
        AirportGroundLayout layout,
        ArtccConfigRoot config,
        params AircraftState[] users
    ) => LandOverWithLiveConfigs(runway, layout, config, LoadSeed(config), users);

    private static List<SoloTrainingEvent> LandOverWithLiveConfigs(
        RunwayInfo runway,
        AirportGroundLayout layout,
        ArtccConfigRoot config,
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> liveConfigs,
        params AircraftState[] users
    )
    {
        AircraftState lander = Lander("AAL78", runway, layout, new LandingPhase());
        List<AircraftState> all = [lander, .. users];
        var context = new SoloTrainingServiceContext(
            InitialContactEligibilityContext.Empty,
            WakeDirectiveCatalog.Empty,
            config,
            liveConfigs,
            SoloTrainingServiceContext.NoSimulatedTower
        );
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate(all, 10, AirspaceDatabase.Default, context);
        CrossTheThreshold(lander, runway, new LandingPhase());
        return evaluator.Evaluate(all, 11, AirspaceDatabase.Default, context);
    }

    /// <summary>The ASDE-X Safety Logic configuration a scenario load starts every facility on, with no active runways.</summary>
    private static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> LoadSeed(ArtccConfigRoot config) =>
        AsdexSafetyLogicSeed.Build(config, NavigationDatabase.Instance, ActiveRunways.Empty);

    /// <summary>The first end at <paramref name="airport"/> with a landing threshold displaced more than 300 ft.</summary>
    private static (RunwayInfo Runway, AirportGroundLayout Layout, double DisplacementFt) DisplacedRunway(string airport)
    {
        AirportGroundLayout layout = Layout(airport);
        string[] ends = ["28L", "28R", "10L", "10R", "1L", "1R", "19L", "19R", "30L", "30R", "12L", "12R"];
        foreach (string end in ends)
        {
            if (
                NavigationDatabase.Instance.GetRunway(airport, end) is { } runway
                && LandingThreshold.DisplacementFt(runway, layout) is > 300.0 and var ft
            )
            {
                return (runway, layout, ft);
            }
        }

        Assert.Fail($"no runway end at {airport} has a landing threshold displaced more than 300 ft");
        throw new InvalidOperationException();
    }

    /// <summary>A B738 stopped across <paramref name="runway"/> on its centreline, in a phase that says nothing about the runway.</summary>
    private static AircraftState CrossingInPlace(string callsign, RunwayInfo runway, double alongFt)
    {
        AircraftState aircraft = OnCentreline(callsign, runway, alongFt);
        aircraft.TrueHeading = new TrueHeading(runway.TrueHeading.Degrees + 90.0);
        aircraft.TrueTrack = aircraft.TrueHeading;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new TaxiingPhase());
        aircraft.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return aircraft;
    }

    private static AircraftState LineUpAtTheMarking(RunwayInfo runway, GroundNode bar, double groundSpeedKts)
    {
        AircraftState lineUp = RunwayOccupancyTests.SquareToTheMarking(runway, bar, "B738", facingRunway: true, 20.0, alongFt: 0);
        lineUp.Callsign = "N44LU";
        lineUp.IndicatedAirspeed = groundSpeedKts;
        lineUp.Targets.TargetSpeed = groundSpeedKts;
        SetPhase(lineUp, runway, new LineUpPhase());
        Assert.False(RunwayOccupancy.IsOverStrictPavement(lineUp.Position, runway));
        return lineUp;
    }

    private static StopAndGoPhase StopAndGo(RunwayInfo runway, bool reaccelerating) =>
        StopAndGoPhase.FromSnapshot(
            new StopAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 20,
                FieldElevation = runway.ElevationFt,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ThresholdLat = runway.ThresholdLatitude,
                ThresholdLon = runway.ThresholdLongitude,
                PauseDuration = 5,
                PauseElapsed = reaccelerating ? 5 : 1,
                Stopped = true,
                Reaccelerating = reaccelerating,
                Airborne = false,
                GoTriggered = false,
                RollElapsedSeconds = 0,
            }
        );

    private static List<SoloTrainingEvent> TriggerInEngine(SimulationEngine engine, RunwayInfo runway)
    {
        engine.Scenario!.ElapsedSeconds = 10;
        engine.TickSoloTrainingEvaluation();
        SetPhase(engine.World.FindAircraft("UAL95")!, runway, new TakeoffPhase());
        engine.Scenario.ElapsedSeconds = 11;
        return [.. engine.TickSoloTrainingEvaluation()];
    }

    private static SimulationEngine SoloEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "solo-runway-user",
                ScenarioName = "Solo runway user",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
                ElapsedSeconds = 0,
                SoloTrainingMode = true,
                PrimaryAirportId = "OAK",
            },
        };

    private static List<SoloTrainingEvent> StartTheRoll(AircraftState departure, RunwayInfo runway, List<AircraftState> aircraft) =>
        StartTheRoll(departure, runway, aircraft, new SoloTrainingEvaluator());

    private static List<SoloTrainingEvent> StartTheRoll(
        AircraftState departure,
        RunwayInfo runway,
        List<AircraftState> aircraft,
        SoloTrainingEvaluator evaluator
    )
    {
        evaluator.Evaluate(aircraft, 10, AirspaceDatabase.Default);
        SetPhase(departure, runway, new TakeoffPhase());
        return evaluator.Evaluate(aircraft, 11, AirspaceDatabase.Default);
    }

    private static List<SoloTrainingEvent> Timeline(SoloTrainingEvaluator evaluator, double seconds) =>
        evaluator.BuildReport(true, seconds, new ApproachReportData([], [], seconds, "N/A"), AircraftDebriefContext.Empty).Timeline;

    private static void AssertAheadOfTheLandingThreshold((AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar) oak, AircraftState user)
    {
        double alongFt = AlongFt(oak.Runway, user.Position);
        double displacementFt = LandingThreshold.DisplacementFt(oak.Runway, oak.Layout.FindRunway(oak.Runway.Designator));
        Assert.True(alongFt > displacementFt, $"the user is {alongFt:F0} ft down, the landing threshold {displacementFt:F0} ft");
    }

    /// <summary>A B738 stopped and held past <paramref name="bar"/>, its nose 150 ft past the marking (over the 33 pavement).</summary>
    private static AircraftState HeldFouler(RunwayInfo runway, GroundNode bar, RunwayInfo? assignedRunway)
    {
        AircraftState fouler = RunwayOccupancyTests.SquareToTheMarking(runway, bar, "B738", facingRunway: true, 150.0, alongFt: 0);
        fouler.Phases = new PhaseList { AssignedRunway = assignedRunway };
        fouler.Phases.Add(new TaxiingPhase());
        fouler.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        fouler.Ground.Hold = HoldDirective.HoldPosition;
        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(fouler, runway, Layout("OAK"))?.Kind);
        return fouler;
    }

    /// <summary>A B738 crossing <paramref name="runway"/> at 15 kt, nose 20 ft past the centreline (as the go-around tests place it).</summary>
    private static AircraftState MidCrossing(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar)
    {
        double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
        AircraftState crossing = RunwayOccupancyTests.SquareToTheRunway(runway, bar, "B738", facingRunway: true, barFt + 20.0, alongFt: 0);
        crossing.Callsign = "CROSS1";
        crossing.IndicatedAirspeed = 15;
        crossing.Targets.TargetSpeed = 15;
        crossing.Phases = new PhaseList();
        crossing.Phases.Add(new CrossingRunwayPhase(bar.Id, RunwayOccupancyTests.FarSideBar(layout, runway, bar).Id, runway.Designator));
        crossing.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return crossing;
    }

    private static AircraftState LinedUp(string callsign, RunwayInfo runway, AirportGroundLayout layout, double alongFt)
    {
        AircraftState aircraft = OnCentreline(callsign, runway, alongFt);
        aircraft.Ground.Layout = layout;
        SetPhase(aircraft, runway, new LinedUpAndWaitingPhase());
        return aircraft;
    }

    /// <summary>An arrival 300 ft short of the pavement threshold at 40 ft above it, in <paramref name="phase"/>.</summary>
    private static AircraftState Lander(string callsign, RunwayInfo runway, AirportGroundLayout layout, Phase phase)
    {
        AircraftState aircraft = OnCentreline(callsign, runway, alongFt: -300);
        aircraft.IsOnGround = false;
        aircraft.Altitude = runway.ElevationFt + 40;
        aircraft.IndicatedAirspeed = 140;
        aircraft.Ground.Layout = layout;
        SetPhase(aircraft, runway, phase);
        return aircraft;
    }

    private static void CrossTheThreshold(AircraftState lander, RunwayInfo runway, Phase phase)
    {
        lander.Position = PositionOnRunway(runway, 100);
        lander.Altitude = runway.ElevationFt + 30;
        SetPhase(lander, runway, phase);
    }

    private static AircraftState OnCentreline(string callsign, RunwayInfo runway, double alongFt) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = PositionOnRunway(runway, alongFt),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            HasMadeInitialContact = true,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                AircraftType = "B738",
                FlightRules = "IFR",
            },
        };

    private static void SetPhase(AircraftState aircraft, RunwayInfo runway, Phase phase)
    {
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(phase);
        aircraft.Phases.CurrentPhase!.Status = PhaseStatus.Active;
    }

    private static LatLon PositionOnRunway(RunwayInfo runway, double alongFt) =>
        GeoMath.ProjectPoint(
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            alongFt >= 0 ? runway.TrueHeading : runway.TrueHeading.ToReciprocal(),
            Math.Abs(alongFt) / GeoMath.FeetPerNm
        );

    private static double AlongFt(RunwayInfo runway, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading)
        * GeoMath.FeetPerNm;

    private static RunwayInfo Runway(string airport, string designator) =>
        Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway(airport, designator));

    private static AirportGroundLayout Layout(string airport) => Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout(airport));
}
