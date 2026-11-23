using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
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
/// The Class B path lookahead (<see cref="BravoPathPredictor"/>): a running phase's predicted path (pattern entry,
/// pattern legs, approach fixes then the final) replaces the straight 60 s ray for the in-phase hold, any clip into a
/// Class B volume whose band holds the path's altitude is an entry, and in an RPO session the instructor alone is warned
/// once per encounter, vertical entries included. Real navdata: the SFO Class B, KOAK 28R (a satellite outside it), KSFO
/// (inside it) and KSQL (under a shelf).
/// </summary>
public sealed class BravoPathPredictorTests : IDisposable
{
    private const string Callsign = "N123AB";
    private const string Sfo = "SFO";
    private const double Horizon = BravoPathPredictor.TriggerLookaheadSeconds;
    private const string WarningPattern = @"^N123AB: .+ path enters the SFO Class B uncleared in \d+ s \(RPO session: no hold\)$";

    private static readonly LatLon NearWestRing = new(37.62, -122.885);
    private static readonly LatLon Ksfo = new(37.6213, -122.3790);
    private static readonly LatLon Ksql = new(37.5119, -122.2494);

    private readonly IDisposable _navScope;

    public BravoPathPredictorTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    // --- Predictor ---

    [Fact]
    public void Predict_PatternEntry_WalksTheLeadInThenTheEntryPoint_InArcsOneSecondApart()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, altitude: 7500);
        LatLon leadIn = GeoMath.ProjectPoint(NearWestRing, new TrueHeading(270), 3);
        LatLon entryPoint = GeoMath.ProjectPoint(leadIn, new TrueHeading(0), 3);
        PatternEntryPhase entry = Active(EntryAt(entryPoint, leadIn));
        ac.Phases!.Add(entry);

        PredictedPath path = Assert.IsType<PredictedPath>(BravoPathPredictor.Predict(ac, ac.Phases, entry, Context(ac, Oak28R()), 600));

        Assert.Equal(ac.Position, path.Points[0].Position);
        Assert.Contains(path.Points, p => GeoMath.DistanceNm(p.Position, entryPoint) < 0.1);
        Assert.Contains(path.Points, p => GeoMath.DistanceNm(p.Position, leadIn) < 0.5);
        double stepNm = ac.GroundSpeed / 3600.0;
        for (int i = 1; i < path.Points.Count; i++)
        {
            Assert.Equal(path.Points[i - 1].Seconds + 1, path.Points[i].Seconds);
            Assert.Equal(stepNm, GeoMath.DistanceNm(path.Points[i - 1].Position, path.Points[i].Position), 3);
        }

        // The 180 deg turn back west is an arc at the turn rate, never a corner.
        for (int i = 2; i < path.Points.Count; i++)
        {
            var a = new TrueHeading(GeoMath.BearingTo(path.Points[i - 2].Position, path.Points[i - 1].Position));
            var b = new TrueHeading(GeoMath.BearingTo(path.Points[i - 1].Position, path.Points[i].Position));
            Assert.True(a.AbsAngleTo(b) <= CategoryPerformance.TurnRate(AircraftCategory.Piston) + 0.5, $"turn of {a.AbsAngleTo(b):F1} deg in 1 s");
        }
    }

    [Fact]
    public void Predict_PatternLegs_DownwindThenBase_TurnAtTheBaseTurnAndEndAtTheThreshold()
    {
        PatternWaypoints wp = OakLeftPattern();
        var start = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon);
        AircraftState ac = Airborne(start, wp.DownwindHeading.Degrees, wp.PatternAltitude);
        var downwind = new DownwindPhase { Status = PhaseStatus.Active, Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new LandingPhase());

        PredictedPath path = Assert.IsType<PredictedPath>(BravoPathPredictor.Predict(ac, ac.Phases, downwind, Context(ac, Oak28R()), 600));

        Assert.Contains(path.Points, p => GeoMath.DistanceNm(p.Position, new LatLon(wp.BaseTurnLat, wp.BaseTurnLon)) < 0.5);
        Assert.True(GeoMath.DistanceNm(path.Points[^1].Position, new LatLon(wp.ThresholdLat, wp.ThresholdLon)) < 0.1);
        Assert.All(path.Points, p => Assert.Equal(wp.PatternAltitude, p.AltitudeFt));
    }

    [Fact]
    public void Predict_ApproachNavigation_FollowsTheFixesFromTheCurrentOne_ThenTheFinalToTheThreshold()
    {
        RunwayInfo runway = Oak28R();
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        TrueHeading outbound = runway.TrueHeading + 180;
        LatLon far = GeoMath.ProjectPoint(threshold, outbound, 8);
        LatLon near = GeoMath.ProjectPoint(threshold, outbound, 5);
        AircraftState ac = Airborne(GeoMath.ProjectPoint(far, outbound, 3), runway.TrueHeading.Degrees, 2500);
        var approach = new ApproachNavigationPhase
        {
            Status = PhaseStatus.Active,
            Fixes = [new ApproachFix("FAR", far.Lat, far.Lon), new ApproachFix("NEAR", near.Lat, near.Lon)],
        };
        ac.Phases!.Add(approach);
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new LandingPhase());

        PredictedPath path = Assert.IsType<PredictedPath>(BravoPathPredictor.Predict(ac, ac.Phases, approach, Context(ac, runway), 600));

        Assert.Contains(path.Points, p => GeoMath.DistanceNm(p.Position, far) < 0.1);
        Assert.Contains(path.Points, p => GeoMath.DistanceNm(p.Position, near) < 0.1);
        Assert.True(GeoMath.DistanceNm(path.Points[^1].Position, threshold) < 0.1);
    }

    [Fact]
    public void Predict_APhaseWithNoPathSource_IsNull_SoTheRayIsUsed()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, altitude: 7500);
        Phase[] noPath =
        [
            new LowApproachPhase { Status = PhaseStatus.Active },
            new DownwindPhase { Status = PhaseStatus.Active },
            new ApproachNavigationPhase { Status = PhaseStatus.Active, Fixes = [] },
        ];
        foreach (Phase phase in noPath)
        {
            ac.Phases = new PhaseList();
            ac.Phases.Add(phase);
            Assert.Null(BravoPathPredictor.Predict(ac, ac.Phases, phase, Context(ac, Oak28R()), Horizon));
        }
    }

    [Fact]
    public void Cut_KeepsThePathNoLaterThanTheHorizon()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, altitude: 7500);
        PatternEntryPhase entry = Active(OakPatternEntry());
        ac.Phases!.Add(entry);
        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases, entry, Context(ac, Oak28R()), 120)!;

        PredictedPath cut = BravoPathPredictor.Cut(path, Horizon);

        Assert.Equal(61, cut.Points.Count);
        Assert.Equal(60, cut.Points[^1].Seconds);
    }

    [Fact]
    public void FirstEntry_APathThatEntersTheBravoAndLeavesIt_IsAnEntry_AndAboveTheBandIsNone()
    {
        // Westbound across the SFO surface area at 1000 ft: in at its east edge, out at its west edge, on to a point beyond.
        LatLon edge = FirstPointOutside(Ksfo, 90, v => v.LowerFtMsl == 0);
        LatLon start = GeoMath.ProjectPoint(edge, new TrueHeading(90), 0.5);
        LatLon beyond = FirstPointOutside(Ksfo, 270, v => v.LowerFtMsl == 0);

        BravoPathEntry entry = Assert.IsType<BravoPathEntry>(FirstEntryAcross(start, beyond, altitude: 1000));
        Assert.False(entry.Vertical);
        Assert.Equal(Sfo, AirspaceDatabase.ClassBAreaName(entry.Volume));
        Assert.InRange(entry.AlongNm, 0.4, 0.6);

        Assert.Null(FirstEntryAcross(start, beyond, altitude: 12000));
    }

    [Theory]
    [InlineData("C172", 90, 0.477)]
    [InlineData("B738", 180, 1.146)]
    [InlineData("C172", 10, BravoPathPredictor.MinTurnMarginNm)]
    public void TurnMarginNm_IsTheOrbitRadiusAtGroundSpeed_FlooredAt015(string type, double groundSpeedKt, double expectedNm)
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 90, altitude: 0);
        ac.AircraftType = type;
        ac.IsOnGround = true;
        ac.IndicatedAirspeed = groundSpeedKt;

        Assert.Equal(expectedNm, BravoPathPredictor.TurnMarginNm(ac), 2);
    }

    // --- Solo in-phase trigger ---

    [Fact]
    public void Hold_OakPatternEntryWhosePathTurnsIntoTheBravo_WhereTheRayDoesNot_HoldsInFront()
    {
        // KOAK, a satellite outside the SFO Class B: no grant, so the path into the SFO ring holds the entry.
        AircraftState ac = TurnedAwayFromTheRingWithThePathStillIn();
        var entry = (PatternEntryPhase)ac.Phases!.CurrentPhase!;

        SoloTick(ac, Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(entry, ac.Phases.HeldPhase);
    }

    [Fact]
    public void NoHold_APathThatTurnsAwayBeforeTheBravo_EvenWhenTheRayWouldEnter()
    {
        AircraftState ac = WestOfTheRayEntry(1.9);
        ac.Phases!.Add(Active(EntryAt(GeoMath.ProjectPoint(ac.Position, new TrueHeading(270), 10))));
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo));
        Assert.Null(PathEntry(ac));

        SoloTick(ac, Context(ac, Oak28R()));

        Assert.Single(ac.Phases.Phases);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void NoHold_APathThatEntersBeyond60s_ThenHoldsOnceTheEntryIsInside60s()
    {
        AircraftState far = WestOfTheRayEntry(5);
        far.Phases!.Add(Active(OakPatternEntry()));
        SoloTick(far, Context(far, Oak28R()));
        Assert.Single(far.Phases.Phases);

        AircraftState near = WestOfTheRayEntry(1.5);
        near.Phases!.Add(Active(OakPatternEntry()));
        SoloTick(near, Context(near, Oak28R()));
        Assert.IsType<AirspaceBoundaryHoldPhase>(near.Phases.CurrentPhase);
    }

    [Fact]
    public void NoHold_AKsfoPatternEntry_ClearedIntoTheBravo_ThoughThePathEntersIt()
    {
        AircraftState ac = WestOfTheRayEntry(1.5);
        ac.Phases!.Add(Active(EntryAt(Ksfo)));
        Assert.NotNull(PathEntry(ac));
        ac.GrantBravoClearance(Sfo);

        SoloTick(ac, Context(ac, Runway("SFO", "28R")));

        Assert.Single(ac.Phases.Phases);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    // --- RPO instructor warning ---

    [Fact]
    public void Rpo_PathEntersTheBravo_WarnsTheInstructorOnce_AndChangesNothingElseOnTheAircraft()
    {
        AircraftState ac = TurnedAwayFromTheRingWithThePathStillIn();
        string before = SnapshotWithoutTheLatch(ac);

        for (int t = 0; t < 3; t++)
        {
            RpoTick(ac);
        }

        Assert.Matches(WarningPattern, Assert.Single(ac.PendingWarnings));
        Assert.Equal(Sfo, ac.InPhaseWarnedArea);
        Assert.Equal(before, SnapshotWithoutTheLatch(ac));
        Assert.Single(ac.Phases!.Phases);
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Empty(ac.PendingPilotSpeech);
    }

    [Fact]
    public void Rpo_TheEncounterEnds_WhenTheEntryIsNoLongerSeen_AndTheNextEntryWarnsAgain()
    {
        AircraftState ac = WestOfTheRayEntry(1.5);
        ac.Phases!.Add(Active(OakPatternEntry()));
        RpoTick(ac);
        Assert.Single(ac.PendingWarnings);

        ac.Phases = new PhaseList();
        ac.Phases.Add(Active(EntryAt(GeoMath.ProjectPoint(ac.Position, new TrueHeading(270), 10))));
        ac.TrueTrack = new TrueHeading(270);
        RpoTick(ac);
        Assert.Null(ac.InPhaseWarnedArea);

        ac.Phases = new PhaseList();
        ac.Phases.Add(Active(OakPatternEntry()));
        ac.TrueTrack = new TrueHeading(90);
        RpoTick(ac);

        Assert.Equal(2, ac.PendingWarnings.Count);
        Assert.All(ac.PendingWarnings, w => Assert.Matches(WarningPattern, w));
    }

    [Fact]
    public void Rpo_AnAircraftClearedIntoTheBravo_GetsNoWarning()
    {
        AircraftState ac = WestOfTheRayEntry(1.5);
        ac.Phases!.Add(Active(OakPatternEntry()));
        ac.GrantBravoClearance(Sfo);

        RpoTick(ac);

        Assert.Empty(ac.PendingWarnings);
        Assert.Null(ac.InPhaseWarnedArea);
    }

    [Fact]
    public void Rpo_AVerticalEntryUnderAShelfNearKsql_Warns_WhereTheSoloHoldTakesNone()
    {
        AircraftState rpo = ClimbingIntoAShelfNearKsql();
        RpoTick(rpo);
        Assert.Matches(WarningPattern, Assert.Single(rpo.PendingWarnings));

        AircraftState solo = ClimbingIntoAShelfNearKsql();
        SoloTick(solo, Context(solo, Runway("SQL", "30")));
        Assert.Single(solo.Phases!.Phases);
        Assert.Empty(solo.PendingPilotTransmissions);
    }

    [Fact]
    public void SnapshotRoundTrip_KeepsTheRpoWarningLatch_AndWritesNoMemberWithoutIt()
    {
        AircraftState ac = WestOfTheRayEntry(1.5);
        ac.Phases!.Add(Active(OakPatternEntry()));
        string bare = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AircraftState.InPhaseWarnedArea), bare, StringComparison.OrdinalIgnoreCase);
        RpoTick(ac);
        Assert.Equal(Sfo, ac.InPhaseWarnedArea);

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = AircraftState.FromSnapshot(JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!, null);

        Assert.Equal(Sfo, restored.InPhaseWarnedArea);
        // The restored aircraft is still in the same encounter: no second warning.
        RpoTick(restored);
        Assert.Empty(restored.PendingWarnings);
    }

    // --- Fix round 1: where the path ends, horizon, turn rates, fly-over, RPO scope and wording ---

    [Fact]
    public void ExtendedDownwind_TowardTheSfoRing_Holds()
    {
        AircraftState turning = DownwindTowardTheRing(extended: false, baseTurnAheadNm: 0.3);
        Assert.Null(PathEntry(turning));

        AircraftState ac = DownwindTowardTheRing(extended: true, baseTurnAheadNm: 0.3);
        Assert.NotNull(PathEntry(ac));
        Phase downwind = ac.Phases!.CurrentPhase!;
        SoloTick(ac, Context(ac, Oak28R()));

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(downwind, ac.Phases.HeldPhase);
    }

    [Fact]
    public void ExtendedDownwind_TowardTheSfoRing_WarnsTheRpoOnce()
    {
        AircraftState ac = DownwindTowardTheRing(extended: true, baseTurnAheadNm: 0.3);
        double seconds = PathEntry(ac)!.Seconds;

        RpoTick(ac);
        RpoTick(ac);

        string expected = $"N123AB: Downwind path enters the SFO Class B uncleared in {seconds:F0} s (RPO session: no hold)";
        Assert.Equal(expected, Assert.Single(ac.PendingWarnings));
        Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        Assert.Null(ac.Phases.HeldPhase);
    }

    [Fact]
    public void PastTheBaseTurnPoint_TheDownwindPathKeepsGoingStraight()
    {
        AircraftState ac = DownwindTowardTheRing(extended: false, baseTurnAheadNm: -0.3);
        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases!, ac.Phases!.CurrentPhase!, Context(ac, Oak28R()), Horizon)!;

        Assert.Equal(Horizon, path.Points[^1].Seconds);
        for (int i = 1; i < path.Points.Count; i++)
        {
            Assert.Equal(90, GeoMath.BearingTo(path.Points[i - 1].Position, path.Points[i].Position), 0);
        }

        Assert.NotNull(PathEntry(ac));
    }

    [Fact]
    public void APathEndingAfter20s_ThenAMidfieldPhase_StillSeesAnEntryAt40s()
    {
        AircraftState ac = WestOfTheRayEntry(1.6);
        ac.Phases!.Add(Active(EntryAt(GeoMath.ProjectPoint(ac.Position, new TrueHeading(90), 0.8))));
        ac.Phases.Add(new MidfieldCrossingPhase());

        BravoPathEntry entry = Assert.IsType<BravoPathEntry>(PathEntry(ac));

        Assert.InRange(entry.Seconds, 35, 45);
    }

    [Fact]
    public void ALowApproach_ContinuesOnRunwayHeading_IntoAShelf()
    {
        (AircraftState ac, RunwayInfo runway, double thresholdNm) = OnAFinalWhoseUpwindEntersTheBravo();

        BravoPathEntry entry = Assert.IsType<BravoPathEntry>(PathEntryUnder(ac, Context(ac, runway)));

        Assert.True(entry.AlongNm > thresholdNm, "the entry lies past the threshold, on the low approach's runway heading");
    }

    [Fact]
    public void AFullStopLanding_StopsAtTheThreshold()
    {
        (AircraftState ac, RunwayInfo runway, double thresholdNm) = OnAFinalWhoseUpwindEntersTheBravo();
        ac.Phases = new PhaseList();
        ac.Phases.Add(Active(new FinalApproachPhase()));
        ac.Phases.Add(new LandingPhase());

        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases, ac.Phases.CurrentPhase!, Context(ac, runway), Horizon)!;

        Assert.True(path.Points[^1].AlongNm <= thresholdNm + 0.05);
        Assert.Null(PathEntryUnder(ac, Context(ac, runway)));
    }

    [Fact]
    public void AMissedApproach_ContinuesStraightOnItsLastTrack()
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 0, altitude: 3000);
        LatLon fix = GeoMath.ProjectPoint(NearWestRing, new TrueHeading(0), 0.5);
        var missed = new ApproachNavigationPhase
        {
            Status = PhaseStatus.Active,
            IsMissedApproach = true,
            Fixes = [new ApproachFix("MAP", fix.Lat, fix.Lon)],
        };
        ac.Phases!.Add(missed);

        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases, missed, Context(ac, Oak28R()), Horizon)!;

        Assert.Equal(Horizon, path.Points[^1].Seconds);
        Assert.Equal(0, new TrueHeading(GeoMath.BearingTo(fix, path.Points[^1].Position)).SignedAngleTo(new TrueHeading(0)), 0);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(119.5)]
    public void Entry59sAhead_Triggers_And61sAhead_DoesNot_OnATurningPath(double indicatedAirspeed)
    {
        AircraftState probe = TurningTowardTheRing(WestOfTheRayEntry(3.0).Position, 0, indicatedAirspeed);
        PredictedPath far = BravoPathPredictor.Predict(probe, probe.Phases!, probe.Phases!.CurrentPhase!, Context(probe, Oak28R()), 400)!;
        BravoPathEntry entry = Assert.IsType<BravoPathEntry>(BravoPathPredictor.FirstEntry(far, AirspaceDatabase.Default, 0, null));
        int entrySecond = (int)entry.Seconds;
        Assert.True(entrySecond > 70, $"entry at {entrySecond} s");

        foreach ((int ahead, bool holds) in (IEnumerable<(int, bool)>)[(59, true), (61, false)])
        {
            PathPoint start = far.Points[entrySecond - ahead];
            double track = GeoMath.BearingTo(far.Points[entrySecond - ahead - 1].Position, start.Position);
            AircraftState ac = TurningTowardTheRing(start.Position, track, indicatedAirspeed);
            SoloTick(ac, Context(ac, Oak28R()));
            Assert.Equal(holds, ac.Phases!.CurrentPhase is AirspaceBoundaryHoldPhase);
        }
    }

    [Fact]
    public void APatternLeg_UsesThePatternTurnRate()
    {
        PatternWaypoints wp = OakLeftPattern();
        AircraftState ac = Airborne(new LatLon(wp.DownwindStartLat, wp.DownwindStartLon), wp.DownwindHeading.Degrees, wp.PatternAltitude);
        var downwind = new DownwindPhase { Status = PhaseStatus.Active, Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Add(new BasePhase { Waypoints = wp });

        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases, downwind, Context(ac, Oak28R()), 600)!;

        double maxTurn = 0;
        for (int i = 2; i < path.Points.Count; i++)
        {
            var a = new TrueHeading(GeoMath.BearingTo(path.Points[i - 2].Position, path.Points[i - 1].Position));
            var b = new TrueHeading(GeoMath.BearingTo(path.Points[i - 1].Position, path.Points[i].Position));
            maxTurn = Math.Max(maxTurn, a.AbsAngleTo(b));
        }

        double rate = PatternCompression.TurnRate(wp, AircraftCategory.Piston);
        Assert.NotEqual(CategoryPerformance.TurnRate(AircraftCategory.Piston), rate);
        Assert.InRange(maxTurn, rate - 0.1, rate + 0.1);
    }

    [Fact]
    public void TriggerAndReleaseWalks_PlanTheSameUpwindAltitude()
    {
        // A pattern-exit departure's upwind, not started, so no live target stands: each walk levels off at its plan's altitude.
        RunwayInfo runway = Oak28R();
        LatLon departureEnd = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading, 1.0);
        AircraftState ac = Airborne(departureEnd, runway.TrueHeading.Degrees, altitude: 3400);
        ac.VerticalSpeed = 500;
        UpwindPhase upwind = Active(new UpwindPhase { Waypoints = OakLeftPattern(), DepartureClimbTargetFt = 4500 });
        ac.Phases!.Add(upwind);
        PhaseContext ctx = Context(ac, runway);
        Assert.Null(ctx.Targets.TargetAltitude);

        PredictedPath trigger = BravoPathPredictor.Predict(ac, ac.Phases, upwind, ctx, BravoPathPredictor.ReleaseHorizonSeconds)!;
        var hold = new AirspaceBoundaryHoldPhase
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = Sfo,
            NameText = Sfo,
            ReferencePosition = Ksfo,
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
        };
        ac.Phases.InsertHoldInFront(hold);
        Phase copy = upwind.ResumeAfterBoundaryHold(ctx).Copy!;
        PredictedPath release = BravoPathPredictor.PredictResume(ac, ac.Phases, copy, ctx, hold)!;

        Assert.Equal(4500.0, trigger.Points[^1].AltitudeFt);
        Assert.Equal(4500.0, release.Points[^1].AltitudeFt);
    }

    [Fact]
    public void AFlyOverFix_GetsNoTurnLead()
    {
        Assert.True(ClosestApproachToTheFirstFix(flyOver: true) < 0.05);
        Assert.True(ClosestApproachToTheFirstFix(flyOver: false) > 0.2);
    }

    [Fact]
    public void AWaypointInsideTheTurnCircle_LessThan90DegreesOffTrack_CountsAsPassed_SoTheWalkDoesNotCircleIt()
    {
        // A northbound C172: the first fix lies 0.3 nm off at 60 deg right, inside the circle it would turn right on.
        AircraftState ac = Airborne(NearWestRing, trueHeading: 0, altitude: 3000);
        double radiusNm = TurnShape.RadiusNm(ac.GroundSpeed, CategoryPerformance.TurnRate(AircraftCategory.Piston));
        LatLon inside = GeoMath.ProjectPoint(NearWestRing, new TrueHeading(60), 0.3);
        Assert.True(GeoMath.DistanceNm(GeoMath.ProjectPoint(NearWestRing, new TrueHeading(90), radiusNm), inside) < radiusNm);
        LatLon second = GeoMath.ProjectPoint(NearWestRing, new TrueHeading(0), 3);
        var approach = new ApproachNavigationPhase
        {
            Status = PhaseStatus.Active,
            Fixes = [new ApproachFix("ONE", inside.Lat, inside.Lon, IsFlyOver: true), new ApproachFix("TWO", second.Lat, second.Lon)],
        };
        ac.Phases!.Add(approach);

        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases, approach, Context(ac, Oak28R()), 120)!;

        // Passed at once, the walk flies straight on to the second fix: no loop around the first.
        Assert.Contains(path.Points, p => GeoMath.DistanceNm(p.Position, second) < 0.2);
        Assert.All(
            path.Points.Where(p => p.AlongNm <= 2.9),
            p =>
                Assert.True(
                    GeoMath.DistanceToSegmentFt(p.Position, NearWestRing, second) / GeoMath.FeetPerNm < 0.05,
                    $"off the line at {p.Seconds} s"
                )
        );
    }

    [Fact]
    public void Rpo_AlreadyInsideTheOuterShelf_GetsNoEntersWarning()
    {
        AircraftState ac = InsideTheOuterShelfShortOfTheNextVolume();
        Assert.Contains(AirspaceDatabase.Default.ClassBArea(Sfo), v => v.Contains(ac.Position, ac.Altitude));
        Assert.NotNull(PathEntry(ac));

        RpoTick(ac);

        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Rpo_WarningText_NamesPhaseAndSeconds()
    {
        AircraftState ac = WestOfTheRayEntry(1.5);
        PatternEntryPhase phase = Active(OakPatternEntry());
        ac.Phases!.Add(phase);
        BravoPathEntry entry = PathEntry(ac)!;

        RpoTick(ac);

        Assert.Equal("Pattern Entry", phase.Name);
        string expected = $"N123AB: Pattern Entry path enters the SFO Class B uncleared in {entry.Seconds:F0} s (RPO session: no hold)";
        Assert.Equal(expected, Assert.Single(ac.PendingWarnings));
    }

    [Fact]
    public void Rpo_ThroughTheEngine_TheWarningTickWritesNothingButTheLatch()
    {
        (SimulationEngine warns, AircraftState warned) = RpoEngine();
        (SimulationEngine latched, AircraftState already) = RpoEngine();
        already.InPhaseWarnedArea = Sfo;
        var warnings = new List<string>();
        warns.WarningEmitted += (_, text) => warnings.Add(text);
        latched.WarningEmitted += (_, text) => warnings.Add("latched: " + text);

        // In lockstep until the first engine's proactive step has warned.
        for (int t = 0; (t < 5) && (warnings.Count == 0); t++)
        {
            already.InPhaseWarnedArea = Sfo;
            warns.TickOneSecond();
            latched.TickOneSecond();
        }

        Assert.Matches(WarningPattern, Assert.Single(warnings));
        Assert.Equal(Sfo, warned.InPhaseWarnedArea);
        Assert.Equal(
            JsonSerializer.Serialize(already.ToSnapshot(), RecordingJsonOptions.Default),
            JsonSerializer.Serialize(warned.ToSnapshot(), RecordingJsonOptions.Default)
        );
    }

    /// <summary>
    /// An eastbound C172 at 7500 ft inside the SFO volume its ray first enters, 1 nm short of the next SFO volume east of
    /// it, on an Active KOAK pattern entry whose path runs on into that volume.
    /// </summary>
    private static AircraftState InsideTheOuterShelfShortOfTheNextVolume()
    {
        AircraftState probe = WestOfTheRayEntry(0);
        IReadOnlyList<AirspaceVolume> sfo = AirspaceDatabase.Default.ClassBArea(Sfo);
        AirspaceVolume outer = sfo.First(v => v.Contains(GeoMath.ProjectPoint(probe.Position, new TrueHeading(90), 0.1), 7500));
        for (double nm = 0.2; nm < 30; nm += 0.1)
        {
            LatLon point = GeoMath.ProjectPoint(probe.Position, new TrueHeading(90), nm);
            if (sfo.Any(v => (v != outer) && v.Contains(point, 7500)))
            {
                AircraftState ac = Airborne(GeoMath.ProjectPoint(point, new TrueHeading(270), 1.0), trueHeading: 90, altitude: 7500);
                ac.IndicatedAirspeed = 130;
                ac.Phases!.Add(Active(OakPatternEntry()));
                return ac;
            }
        }

        throw new InvalidOperationException("no second SFO volume east of the ring");
    }

    private static (SimulationEngine Engine, AircraftState Aircraft) RpoEngine()
    {
        AircraftState ac = WestOfTheRayEntry(1.5);
        ac.Phases!.Add(OakPatternEntry());
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = Scenario(solo: false) };
        engine.World.AddAircraft(ac);
        return (engine, ac);
    }

    /// <summary>
    /// An eastbound C172 1.5 nm west of the SFO ring at 7500 ft on an Active right downwind (heading 090) whose base turn
    /// lies <paramref name="baseTurnAheadNm"/> ahead (behind when negative), base south and final west, away from the ring.
    /// </summary>
    private static AircraftState DownwindTowardTheRing(bool extended, double baseTurnAheadNm)
    {
        AircraftState ac = WestOfTheRayEntry(1.5);
        LatLon baseTurn = GeoMath.ProjectPoint(ac.Position, new TrueHeading(90), baseTurnAheadNm);
        LatLon threshold = GeoMath.ProjectPoint(GeoMath.ProjectPoint(baseTurn, new TrueHeading(180), 1.0), new TrueHeading(270), 3.0);
        var wp = new PatternWaypoints
        {
            UpwindHeading = new TrueHeading(270),
            CrosswindHeading = new TrueHeading(0),
            DownwindHeading = new TrueHeading(90),
            BaseHeading = new TrueHeading(180),
            FinalHeading = new TrueHeading(270),
            BaseTurnLat = baseTurn.Lat,
            BaseTurnLon = baseTurn.Lon,
            ThresholdLat = threshold.Lat,
            ThresholdLon = threshold.Lon,
            PatternAltitude = ac.Altitude,
            Direction = PatternDirection.Right,
        };
        ac.Phases!.Add(
            new DownwindPhase
            {
                Status = PhaseStatus.Active,
                Waypoints = wp,
                IsExtended = extended,
            }
        );
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        return ac;
    }

    /// <summary>A C172 at 7500 ft on <paramref name="track"/> at <paramref name="position"/>, on an Active KOAK entry with no lead-in.</summary>
    private static AircraftState TurningTowardTheRing(LatLon position, double track, double indicatedAirspeed)
    {
        AircraftState ac = Airborne(position, track, 7500);
        ac.IndicatedAirspeed = indicatedAirspeed;
        ac.Phases!.Add(Active(OakPatternEntry()));
        return ac;
    }

    /// <summary>
    /// The closest the approach path comes to its first fix, 2 nm north of a northbound C172, with the second fix 2 nm east
    /// of it: a fly-over fix is crossed, a fly-by fix is cut inside the turn.
    /// </summary>
    private static double ClosestApproachToTheFirstFix(bool flyOver)
    {
        AircraftState ac = Airborne(NearWestRing, trueHeading: 0, altitude: 3000);
        LatLon first = GeoMath.ProjectPoint(NearWestRing, new TrueHeading(0), 2);
        LatLon second = GeoMath.ProjectPoint(first, new TrueHeading(90), 2);
        var approach = new ApproachNavigationPhase
        {
            Status = PhaseStatus.Active,
            Fixes = [new ApproachFix("ONE", first.Lat, first.Lon, IsFlyOver: flyOver), new ApproachFix("TWO", second.Lat, second.Lon)],
        };
        ac.Phases!.Add(approach);
        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases, approach, Context(ac, Oak28R()), 120)!;
        return path.Points.Min(p => GeoMath.DistanceNm(p.Position, first));
    }

    /// <summary>
    /// A C172 on a Bay Area final before its threshold, an Active final with a low approach behind it, whose path enters an
    /// SFO Class B volume only past the threshold, on runway heading; with the threshold's along-path distance.
    /// </summary>
    private static (AircraftState Aircraft, RunwayInfo Runway, double ThresholdNm) OnAFinalWhoseUpwindEntersTheBravo()
    {
        (string Airport, string End)[] finals =
        [
            ("OAK", "28R"),
            ("OAK", "28L"),
            ("OAK", "30"),
            ("HWD", "28L"),
            ("SQL", "30"),
            ("PAO", "31"),
            ("OAK", "12"),
        ];
        foreach ((string airport, string end) in finals)
        {
            RunwayInfo runway = Runway(airport, end);
            var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
            for (int altitude = 500; altitude <= 4000; altitude += 250)
            {
                AircraftState ac = Airborne(GeoMath.ProjectPoint(threshold, runway.TrueHeading + 180, 0.3), runway.TrueHeading.Degrees, altitude);
                ac.Phases!.Add(Active(new FinalApproachPhase()));
                ac.Phases.Add(new LowApproachPhase());
                if (PathEntryUnder(ac, Context(ac, runway)) is { AlongNm: > 0.35 })
                {
                    return (ac, runway, 0.3);
                }
            }
        }

        throw new InvalidOperationException("no Bay Area final whose upwind enters the SFO Class B");
    }

    private static BravoPathEntry? PathEntryUnder(AircraftState ac, PhaseContext ctx)
    {
        PredictedPath? path = BravoPathPredictor.Predict(ac, ac.Phases!, ac.Phases!.CurrentPhase!, ctx, Horizon);
        return path is null ? null : BravoPathPredictor.FirstEntry(path, AirspaceDatabase.Default, 0, null);
    }

    // --- Fixtures ---

    /// <summary>
    /// A C172 1.5 nm west of the SFO ring at 7500 ft on an Active KOAK pattern entry, turned off east to the first heading
    /// whose straight ray misses the Class B while the predicted path still enters it within 60 s.
    /// </summary>
    private static AircraftState TurnedAwayFromTheRingWithThePathStillIn()
    {
        foreach (int heading in (int[])[0, 180, 330, 210, 300, 240, 30, 150])
        {
            AircraftState ac = WestOfTheRayEntry(1.0);
            ac.TrueTrack = new TrueHeading(heading);
            ac.TrueHeading = ac.TrueTrack;
            ac.Phases!.Add(Active(OakPatternEntry()));
            if ((AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo) is null) && (PathEntry(ac) is { Vertical: false }))
            {
                return ac;
            }
        }

        throw new InvalidOperationException("no heading off the ring keeps the path entering while the ray misses");
    }

    /// <summary>A VFR C172 eastbound at 7500 ft, <paramref name="nm"/> west of where its straight ray meets the SFO ring.</summary>
    private static AircraftState WestOfTheRayEntry(double nm)
    {
        AircraftState probe = Airborne(NearWestRing, trueHeading: 90, altitude: 7500);
        probe.IndicatedAirspeed = 130;
        LatLon ring = AirspaceDatabase.Default.FindFirstProjectedEntry(probe, 60, AirspaceClass.Bravo)!.Intersection;
        AircraftState ac = Airborne(GeoMath.ProjectPoint(ring, new TrueHeading(270), nm), trueHeading: 90, altitude: 7500);
        ac.IndicatedAirspeed = 130;
        return ac;
    }

    /// <summary>
    /// A C172 under an SFO shelf near KSQL, laterally inside it and clear of every lower SFO floor, 300 ft below the shelf
    /// floor and climbing into it on an Active pattern entry whose entry point lies inside the same shelf.
    /// </summary>
    private static AircraftState ClimbingIntoAShelfNearKsql()
    {
        IReadOnlyList<AirspaceVolume> sfo = AirspaceDatabase.Default.ClassBArea(Sfo);
        for (double nm = 0; nm <= 8; nm += 1)
        {
            for (int bearing = 0; bearing < 360; bearing += 30)
            {
                LatLon point = GeoMath.ProjectPoint(Ksql, new TrueHeading(bearing), nm);
                if (ClimbingAt(point, sfo) is { } ac)
                {
                    return ac;
                }
            }
        }

        throw new InvalidOperationException("no SFO shelf near KSQL");
    }

    private static AircraftState? ClimbingAt(LatLon point, IReadOnlyList<AirspaceVolume> sfo)
    {
        List<AirspaceVolume> over = [.. sfo.Where(v => v.ContainsLateral(point)).OrderBy(v => v.LowerFtMsl)];
        if ((over.Count == 0) || (over[0].LowerFtMsl < 1500))
        {
            return null;
        }

        int floor = over[0].LowerFtMsl;
        for (int heading = 0; heading < 360; heading += 45)
        {
            LatLon ahead = GeoMath.ProjectPoint(point, new TrueHeading(heading), 1.0);
            if (sfo.Any(v => v.ContainsLateral(ahead) && (v.LowerFtMsl < floor)) || !over[0].ContainsLateral(ahead))
            {
                continue;
            }

            AircraftState ac = Airborne(point, heading, floor - 300);
            ac.VerticalSpeed = 1500;
            ac.Targets.TargetAltitude = floor + 1500;
            ac.Phases!.Add(Active(EntryAt(ahead)));
            if (PathEntry(ac) is { Vertical: true })
            {
                return ac;
            }
        }

        return null;
    }

    private static BravoPathEntry? PathEntry(AircraftState ac)
    {
        Phase current = ac.Phases!.CurrentPhase!;
        PredictedPath? path = BravoPathPredictor.Predict(ac, ac.Phases, current, Context(ac, Oak28R()), Horizon);
        return path is null ? null : BravoPathPredictor.FirstEntry(BravoPathPredictor.Cut(path, Horizon), AirspaceDatabase.Default, 0, null);
    }

    private static BravoPathEntry? FirstEntryAcross(LatLon start, LatLon end, double altitude)
    {
        AircraftState ac = Airborne(start, trueHeading: 270, altitude: altitude);
        PatternEntryPhase entry = Active(EntryAt(end));
        ac.Phases!.Add(entry);
        PredictedPath path = BravoPathPredictor.Predict(ac, ac.Phases, entry, Context(ac, Runway("SFO", "28R")), Horizon)!;
        return BravoPathPredictor.FirstEntry(path, AirspaceDatabase.Default, marginNm: 0, onlyArea: Sfo);
    }

    /// <summary>
    /// The aircraft's snapshot as JSON without <see cref="AircraftState.InPhaseWarnedArea"/>, the one member the warning may
    /// write.
    /// </summary>
    private static string SnapshotWithoutTheLatch(AircraftState ac)
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default))!.AsObject();
        foreach (
            string key in json.Select(p => p.Key)
                .Where(k => k.Equals(nameof(AircraftState.InPhaseWarnedArea), StringComparison.OrdinalIgnoreCase))
                .ToList()
        )
        {
            json.Remove(key);
        }

        return json.ToJsonString();
    }

    private static void SoloTick(AircraftState ac, PhaseContext ctx) =>
        PilotProactive.TickAirspaceBoundaryRespect(ac, Scenario(solo: true), AirspaceDatabase.Default, LookupAirport, _ => ctx);

    private static void RpoTick(AircraftState ac) =>
        PilotProactive.TickRpoPathWarning(ac, Scenario(solo: false), AirspaceDatabase.Default, a => Context(a, Oak28R()));

    private static T Active<T>(T phase)
        where T : Phase
    {
        phase.Status = PhaseStatus.Active;
        return phase;
    }

    private static PatternEntryPhase OakPatternEntry()
    {
        RunwayInfo runway = Oak28R();
        return EntryAt(GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + (-90), 1.0));
    }

    private static PatternEntryPhase EntryAt(LatLon point, LatLon? leadIn = null) =>
        new()
        {
            EntryLat = point.Lat,
            EntryLon = point.Lon,
            LeadInLat = leadIn?.Lat,
            LeadInLon = leadIn?.Lon,
            PatternAltitude = Oak28R().ElevationFt + 1000,
            Kind = PatternEntryKind.Direct,
        };

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

    private static RunwayInfo Oak28R() => Runway("OAK", "28R");

    private static RunwayInfo Runway(string airport, string designator) =>
        NavigationDatabase.Instance.GetRunway(airport, designator) ?? throw new InvalidOperationException($"{airport} {designator} missing");

    private static SimScenarioState Scenario(bool solo) =>
        new()
        {
            ScenarioId = "test-bravo-path-lookahead",
            ScenarioName = "Bravo Path Lookahead",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = solo,
            PrimaryAirportId = "SFO",
            StudentPositionType = "APP",
        };

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => Ksfo,
            _ => null,
        };
}
