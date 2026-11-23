using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// How a Class B boundary hold standing in front of a running phase (<see cref="PhaseList.InsertHoldInFront"/>) ends: its
/// natural end on the tick goes through <see cref="PhaseRunner.EndHold"/> and resumes the held phase as it declares; a
/// heading already standing when the hold went in is not a vector; a new heading that keeps the aircraft outside the Class B
/// ends the orbit and is flown, one into it is held through and flown once cleared, and a later heading is judged the same way
/// in its turn; a new direct follows the same rule (flown at once when it keeps outside, stashed and put back once cleared when
/// it enters), and with neither left standing the held phase's route is put back; a hold with no held phase keeps the
/// free-flight path; and the visual backstop leaves a held go-around alone. Solo, on real navdata: a VFR C172 on a pattern
/// entry or a final to OAK 28R, or west of the SFO Class B ring, or a VFR M20P on the LVK 25R right downwind, held for the SFO
/// Class B, and an IFR B738 on the OAK 30 visual for the backstop; the gate is opened by granting the Class B clearance the hold
/// waits for.
/// </summary>
public sealed class BoundaryHoldEndTests : IDisposable
{
    private const string Sfo = "SFO";

    /// <summary>West of the SFO Class B's outer ring at 7500 ft: east is into it, west keeps outside.</summary>
    private static readonly LatLon NearWestRing = new(37.62, -122.885);

    private static readonly LatLon InsideTheSfoSurfaceArea = SfoClassBTestPoints.InsideTheSfoSurfaceArea;

    /// <summary>
    /// Inside the SFO 3000 ft shelf east of KSQL (<see cref="SqlShelfFloorFt"/>), 1.5 nm from both the 2300 ft shelf west of it
    /// and the 4000 ft shelf east of it.
    /// </summary>
    private static readonly LatLon InsideTheSqlShelf = new(37.55, -122.11);

    /// <summary>The floor of the SFO shelf east of KSQL.</summary>
    private const int SqlShelfFloorFt = 3000;

    /// <summary>
    /// Under the SFO 4000 ft shelf east of the 3000 ft one east of KSQL: a right-hand circuit flown east to it and turning south
    /// there keeps clear of the Class B at 3500 ft.
    /// </summary>
    private static readonly LatLon EastOfTheSqlShelf = new(37.55, -122.005);

    /// <summary>The fix the held initial climb was told direct to before the hold went in: Woodside VOR, southeast.</summary>
    private const string HeldRouteFix = "OSI";

    /// <summary>A direct that keeps the aircraft outside the SFO Class B from <see cref="NearWestRing"/>: Point Reyes VOR.</summary>
    private const string OutsideFix = "PYE";

    /// <summary>A direct that takes the aircraft into the SFO Class B from <see cref="NearWestRing"/>: San Francisco VOR.</summary>
    private const string IntoFix = "SFO";

    private readonly IDisposable _navScope;
    private readonly CapturingSimLogProvider _warnings = new(LogLevel.Warning, 200);

    public BoundaryHoldEndTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).CaptureInto(_warnings).InitializeSimLog();
    }

    public void Dispose()
    {
        _navScope.Dispose();
        _warnings.Dispose();
    }

    // --- Self-release: the held path clears the Class B by one turn margin ---

    [Fact]
    public void Release_AHeldPatternEntryWhosePathClearsTheBravoByMoreThanTheMargin_ResumesItInPlace_Uncleared()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = EntryWestOfTheRing(nmWest: 3.0, track: 270, scenarioSeconds: 0);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.False(ac.IsClearedIntoBravo(Sfo));
        Assert.Equal(ctx.ScenarioElapsedSeconds, AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));
    }

    [Fact]
    public void Release_AnOrbitUnderAShelf_WhoseResumedPatternEntryClimbsIntoIt_DoesNotRelease()
    {
        // Level in the orbit, but the resumed entry climbs to its pattern altitude inside the shelf's footprint.
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry, int floor) = EntryUnderTheOuterShelf(patternAboveFloorFt: 500);
        Assert.True(ac.Altitude < floor);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 3; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(entry, ac.Phases.HeldPhase);
        Assert.Contains(ResumePath(ac, ctx, hold, entry).Points, p => p.AltitudeFt > floor);
    }

    [Fact]
    public void Release_AnOrbitUnderAShelf_WhoseResumedPatternEntryStaysBelowIt_ResumesIt()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry, _) = EntryUnderTheOuterShelf(patternAboveFloorFt: -300);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.False(ac.IsClearedIntoBravo(Sfo));
    }

    [Fact]
    public void Release_APathWithinTheMarginOfTheBravo_ThatDoesNotEnterIt_DoesNotRelease()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = EntryWestOfTheRing(nmWest: 0.4, track: 270, scenarioSeconds: 0);
        Assert.InRange(BravoPathPredictor.TurnMarginNm(ac), 0.5, 1.0);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 5; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        PredictedPath path = ResumePath(ac, ctx, hold, entry);
        Assert.Null(BravoPathPredictor.FirstEntry(path, AirspaceDatabase.Default, marginNm: 0, onlyArea: Sfo));
        Assert.False(BravoPathPredictor.KeepsClearOf(path, AirspaceDatabase.Default, Sfo, BravoPathPredictor.TurnMarginNm(ac)));
        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(entry, ac.Phases.HeldPhase);
    }

    [Fact]
    public void Release_AHoldFlyingThroughAVectorIntoTheBravo_NeverEndsOnThePathTest()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = EntryWestOfTheRing(nmWest: 1.5, track: 270, scenarioSeconds: 0);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        // Started by hand, as the pending-start path would, so no tick of the hold's own exit test runs before the vector.
        hold.Status = PhaseStatus.Active;
        hold.OnStart(ctx);
        Assert.True(
            BravoPathPredictor.KeepsClearOf(ResumePath(ac, ctx, hold, entry), AirspaceDatabase.Default, Sfo, BravoPathPredictor.TurnMarginNm(ac))
        );
        var into = new MagneticHeading(90);
        Assert.NotNull(BravoEntryOn(ac, into));
        ac.Targets.AssignedMagneticHeading = into;

        for (int t = 0; t < 5; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(entry, ac.Phases.HeldPhase);
        Assert.True(HoldDto(hold).HeldThroughVector);
        Assert.Null(AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));
    }

    [Fact]
    public void ReHold_Within30sOfASelfRelease_LogsAWarning()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = EntryWestOfTheRing(nmWest: 3.0, track: 270, scenarioSeconds: 100);
        ac.Phases!.InsertHoldInFront(SfoBravoHold());
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(100, AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));
        _warnings.Drain();

        // Turned back east just short of the ring: the turn onto the entry point carries the path into the Class B.
        ac.Position = GeoMath.ProjectPoint(RayEntryOnTheRing(), new TrueHeading(270), 0.3);
        ac.TrueHeading = new TrueHeading(90);
        ac.TrueTrack = new TrueHeading(90);
        SimScenarioState scenario = SoloScenario(elapsedSeconds: 110);
        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport, _ => ctx);

        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.Same(entry, ac.Phases.HeldPhase);
        Assert.Contains(
            _warnings.Drain(),
            r => (r.Level == LogLevel.Warning) && (r.Message == "[BoundaryHold] N123AB: held again 10 s after a self-release at the SFO Class B")
        );
    }

    [Fact]
    public void Release_ADownwindRunningWithinOneMarginOfTheBoundary_NeverReleases()
    {
        // Accepted as the margin's cost: the hold stands until cleared in or vectored.
        // Along the ring, its base turn 0.4 nm outside it, the aircraft 3 nm back along the leg and 1.5 nm further out.
        double along = RingHeadingAtTheRayEntry();
        LatLon baseTurn = GeoMath.ProjectPoint(RayEntryOnTheRing(), new TrueHeading(along - 90), 0.4);
        LatLon start = GeoMath.ProjectPoint(GeoMath.ProjectPoint(baseTurn, new TrueHeading(along + 180), 3.0), new TrueHeading(along - 90), 1.5);
        (AircraftState ac, PhaseContext ctx, DownwindPhase downwind) = DownwindAlong(baseTurn, start, along);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 5; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        PredictedPath path = ResumePath(ac, ctx, hold, downwind);
        double margin = BravoPathPredictor.TurnMarginNm(ac);
        Assert.True(BravoPathPredictor.KeepsClearOf(BravoPathPredictor.Cut(path, 0), AirspaceDatabase.Default, Sfo, margin));
        Assert.Null(BravoPathPredictor.FirstEntry(path, AirspaceDatabase.Default, marginNm: 0, onlyArea: Sfo));
        Assert.False(BravoPathPredictor.KeepsClearOf(path, AirspaceDatabase.Default, Sfo, margin));
        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Same(downwind, ac.Phases.HeldPhase);
    }

    [Fact]
    public void Release_ARestartCopyDownwind_IsJudgedFromTheAircraftsPosition_AndSwapsInTheCopy()
    {
        // The same downwind, well clear of the ring; only where the aircraft orbits differs.
        LatLon baseTurn = GeoMath.ProjectPoint(RayEntryOnTheRing(), new TrueHeading(270), 3.0);
        LatLon nearTheRing = GeoMath.ProjectPoint(GeoMath.ProjectPoint(RayEntryOnTheRing(), new TrueHeading(270), 0.3), new TrueHeading(180), 1.0);
        (AircraftState near, PhaseContext nearCtx, DownwindPhase nearDownwind) = DownwindAlong(baseTurn, nearTheRing, 0);
        (AircraftState clear, PhaseContext clearCtx, DownwindPhase clearDownwind) = DownwindAlong(
            baseTurn,
            GeoMath.ProjectPoint(baseTurn, new TrueHeading(180), 3.0),
            0
        );
        AirspaceBoundaryHoldPhase nearHold = SfoBravoHold();
        AirspaceBoundaryHoldPhase clearHold = SfoBravoHold();
        near.Phases!.InsertHoldInFront(nearHold);
        clear.Phases!.InsertHoldInFront(clearHold);

        PhaseRunner.Tick(near, nearCtx);
        PhaseRunner.Tick(clear, clearCtx);

        Assert.Same(nearHold, near.Phases.CurrentPhase);
        Assert.Same(nearDownwind, near.Phases.HeldPhase);
        Assert.Equal(PhaseStatus.Completed, clearHold.Status);
        Assert.Equal(PhaseStatus.Completed, clearDownwind.Status);
        DownwindPhase copy = Assert.IsType<DownwindPhase>(clear.Phases.CurrentPhase);
        Assert.NotSame(clearDownwind, copy);
        Assert.Equal(PhaseStatus.Active, copy.Status);
    }

    // --- Self-release: the resumed leg flies the altitude it targets, not pattern altitude ---

    [Fact]
    public void HeldUpwind_ClimbTargetAboveShelf_DoesNotSelfRelease()
    {
        // A pattern-exit departure: the resumed upwind climbs on to 4500 ft, into the shelf it would pass under at pattern altitude.
        (AircraftState ac, PhaseContext ctx, Phase upwind) = LegTowardTheSqlShelf(CircuitLeg.Upwind, climbTargetFt: 4500);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 30; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        AssertStillHeld(ac, hold, upwind);
    }

    [Fact]
    public void HeldCrosswind_ClimbTargetAboveShelf_DoesNotSelfRelease()
    {
        (AircraftState ac, PhaseContext ctx, Phase crosswind) = LegTowardTheSqlShelf(CircuitLeg.Crosswind, climbTargetFt: 4500);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 30; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        AssertStillHeld(ac, hold, crosswind);
    }

    [Fact]
    public void HeldUpwind_NoClimbTarget_PatternBelowShelf_SelfReleases()
    {
        (AircraftState ac, PhaseContext ctx, Phase upwind) = LegTowardTheSqlShelf(CircuitLeg.Upwind, climbTargetFt: null);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        PhaseRunner.Tick(ac, ctx);

        AssertSelfReleasedToACopyOf(ac, ctx, hold, upwind);
    }

    [Fact]
    public void HeldDownwind_LegControllerAltitudeAboveFloor_DoesNotSelfRelease()
    {
        // A CM issued on the downwind before the hold: the restart copy holds 3500 ft, inside the shelf, not pattern altitude.
        (AircraftState ac, PhaseContext ctx, Phase downwind) = LegTowardTheSqlShelf(CircuitLeg.Downwind, climbTargetFt: null);
        IssueClimbMaintain(ac, 3500);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(3500, ((DownwindPhaseDto)downwind.ToSnapshot()).ControllerAltitudeFt);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 30; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        AssertStillHeld(ac, hold, downwind);
    }

    [Fact]
    public void HeldUpwind_CmBelowFloorDuringHold_SelfReleases()
    {
        // The hold's end leaves the CM standing: the resumed upwind descends to 2500 ft under the shelf, not on to 4500 ft.
        (AircraftState ac, PhaseContext ctx, Phase upwind) = LegTowardTheSqlShelf(CircuitLeg.Upwind, climbTargetFt: 4500);
        AirspaceBoundaryHoldPhase hold = HoldStartedInFrontUnjudged(ac, ctx);
        IssueClimbMaintain(ac, 2500);

        PhaseRunner.Tick(ac, ctx);

        AssertSelfReleasedToACopyOf(ac, ctx, hold, upwind);
        _warnings.Drain();

        // A minute on, flown as a solo session flies it, the trigger never holds the resumed upwind again.
        (bool reHeld, string trace) = FlyOnAfterTheRelease(ac, ctx, seconds: 60);

        Assert.False(reHeld, trace);
        Assert.DoesNotContain(_warnings.Drain(), r => r.Message.Contains("after a self-release", StringComparison.Ordinal));
    }

    [Fact]
    public void HeldUpwind_CmAboveFloorDuringHold_DoesNotSelfRelease()
    {
        (AircraftState ac, PhaseContext ctx, Phase upwind) = LegTowardTheSqlShelf(CircuitLeg.Upwind, climbTargetFt: null);
        AirspaceBoundaryHoldPhase hold = HoldStartedInFrontUnjudged(ac, ctx);
        IssueClimbMaintain(ac, 3500);

        for (int t = 0; t < 30; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        AssertStillHeld(ac, hold, upwind);
    }

    [Fact]
    public void HeldDownwind_CmDuringHold_CopyFliesIt()
    {
        // Pattern altitude, 3500 ft, lies inside the shelf; a CM to 2500 ft issued during the hold takes the resumed downwind under it.
        (AircraftState ac, PhaseContext ctx, Phase downwind) = LegTowardTheSqlShelf(
            CircuitLeg.Downwind,
            climbTargetFt: null,
            patternAltitudeFt: 3500
        );
        AirspaceBoundaryHoldPhase hold = HoldStartedInFrontUnjudged(ac, ctx);
        IssueClimbMaintain(ac, 2500);

        PhaseRunner.Tick(ac, ctx);

        AssertSelfReleasedToACopyOf(ac, ctx, hold, downwind);
        Assert.Equal(2500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void HeldCrosswind_StartsAbovePattern_WithUpwindCm_DoesNotSelfRelease()
    {
        // The CM to 3500 ft issued on the upwind stands as the crosswind starts above pattern altitude and sets no target of its
        // own: the resumed crosswind holds 3500 ft, inside the shelf, not pattern altitude.
        (AircraftState ac, PhaseContext ctx, Phase crosswind) = LegTowardTheSqlShelf(CircuitLeg.Crosswind, climbTargetFt: null, standingCmFt: 3500);
        Assert.Equal(3500, ac.Targets.TargetAltitude);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 30; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        AssertStillHeld(ac, hold, crosswind);
    }

    [Fact]
    public void HeldDownwind_AJet400FtAboveTheFloor_IsNotPredictedBelowItBefore20s()
    {
        // The resumed downwind descends at the jet's 1000 fpm pattern rate, not its 1800 fpm en-route rate: about 24 s to the floor.
        (AircraftState ac, PhaseContext ctx, Phase downwind) = LegTowardTheSqlShelf(CircuitLeg.Downwind, climbTargetFt: null, aircraftType: "B738");
        ac.IndicatedAirspeed = 210;
        Assert.Equal(SqlShelfFloorFt + 400, ac.Altitude);
        AirspaceBoundaryHoldPhase hold = HoldStartedInFrontUnjudged(ac, ctx);

        PathPoint firstBelow = ResumePath(ac, ctx, hold, downwind).Points.First(p => p.AltitudeFt < SqlShelfFloorFt);

        Assert.True(firstBelow.Seconds >= 20, $"below the {SqlShelfFloorFt} ft floor after {firstBelow.Seconds} s");
    }

    [Fact]
    public void HeldUpwind_ClimbingTowardAFloor_FasterInitialRate_IsNotJudgedClear()
    {
        // A Bonanza's resumed upwind climbs at its initial rate, above its category's: it reaches the 3000 ft floor inside the
        // shelf's footprint, where at the category rate it would reach it only past the turn margin.
        (AircraftState ac, PhaseContext ctx, Phase upwind) = UpwindClimbingOutFromUnderTheSqlShelf("BE33");
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 30; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        AssertStillHeld(ac, hold, upwind);
    }

    [Fact]
    public void TeardropReentryHold_NeverSelfReleases()
    {
        // The downwind behind it clears the Class B by more than the margin, but a teardrop re-entry has no path source, and its
        // resume, which would mark it complete at the abeam point it is past, is never built.
        (AircraftState ac, PhaseContext ctx, TeardropReentryPhase teardrop, DownwindPhase downwind) = TeardropPastAbeamAtOak();
        PredictedPath downwindPath = BravoPathPredictor.Predict(ac, ac.Phases!, downwind, ctx, BravoPathPredictor.ReleaseHorizonSeconds)!;
        Assert.True(BravoPathPredictor.KeepsClearOf(downwindPath, AirspaceDatabase.Default, Sfo, BravoPathPredictor.TurnMarginNm(ac)));
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        for (int t = 0; t < 30; t++)
        {
            PhaseRunner.Tick(ac, ctx);
        }

        AssertStillHeld(ac, hold, teardrop);
        Assert.Null(((TeardropReentryPhaseDto)teardrop.ToSnapshot()).CompleteAtAbeamOnResume);
    }

    /// <summary>The path the end of <paramref name="hold"/> would resume <paramref name="held"/> on, as the release judges it.</summary>
    private static PredictedPath ResumePath(AircraftState ac, PhaseContext ctx, AirspaceBoundaryHoldPhase hold, Phase held)
    {
        Phase resumed = held.ResumeAfterBoundaryHold(ctx).Copy ?? held;
        return BravoPathPredictor.PredictResume(ac, ac.Phases!, resumed, ctx, hold)!;
    }

    /// <summary>Where a C172 eastbound at 7500 ft from <see cref="NearWestRing"/> meets the SFO Class B ring.</summary>
    private static LatLon RayEntryOnTheRing() => RayEntryFrom(NearWestRing);

    /// <summary>The ring's direction at <see cref="RayEntryOnTheRing"/>: from its point 2 nm south to its point 2 nm north.</summary>
    private static double RingHeadingAtTheRayEntry()
    {
        LatLon south = RayEntryFrom(GeoMath.ProjectPoint(NearWestRing, new TrueHeading(180), 2.0));
        LatLon north = RayEntryFrom(GeoMath.ProjectPoint(NearWestRing, new TrueHeading(0), 2.0));
        return GeoMath.BearingTo(south, north);
    }

    /// <summary>
    /// A level C172 westbound at 145 kt, 300 ft below the floor of the outer SFO volume, laterally inside it midway between
    /// the ring and the next SFO volume east, on a started pattern entry to a point 3 nm west of the ring with nothing
    /// behind, its pattern altitude <paramref name="patternAboveFloorFt"/> above that floor; with the floor.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context, PatternEntryPhase Entry, int FloorFt) EntryUnderTheOuterShelf(
        int patternAboveFloorFt
    )
    {
        LatLon ring = RayEntryOnTheRing();
        IReadOnlyList<AirspaceVolume> sfo = AirspaceDatabase.Default.ClassBArea(Sfo);
        AirspaceVolume outer = sfo.First(v => v.ContainsLateral(GeoMath.ProjectPoint(ring, new TrueHeading(90), 0.1)));
        double nextNm = 0.2;
        while (!sfo.Any(v => (v != outer) && v.ContainsLateral(GeoMath.ProjectPoint(ring, new TrueHeading(90), nextNm))))
        {
            nextNm += 0.1;
        }

        Assert.True(nextNm > 4.0, $"the outer SFO volume is only {nextNm:F1} nm deep here");
        LatLon position = GeoMath.ProjectPoint(ring, new TrueHeading(90), nextNm / 2);
        LatLon entryPoint = GeoMath.ProjectPoint(ring, new TrueHeading(270), 3.0);
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), position);
        ac.Altitude = outer.LowerFtMsl - 300;
        ac.IndicatedAirspeed = 130;
        ac.VerticalSpeed = 0;
        ac.TrueHeading = new TrueHeading(270);
        ac.TrueTrack = new TrueHeading(270);
        var entry = new PatternEntryPhase
        {
            EntryLat = entryPoint.Lat,
            EntryLon = entryPoint.Lon,
            PatternAltitude = outer.LowerFtMsl + patternAboveFloorFt,
            Kind = PatternEntryKind.Direct,
        };
        ac.Phases!.Add(entry);
        ac.Phases.Start(ctx);
        return (ac, ctx, entry, outer.LowerFtMsl);
    }

    private static LatLon RayEntryFrom(LatLon from)
    {
        (AircraftState probe, _) = AircraftAt(Oak28R(), from);
        probe.Altitude = 7500;
        probe.IndicatedAirspeed = 130;
        probe.TrueHeading = new TrueHeading(90);
        probe.TrueTrack = new TrueHeading(90);
        return AirspaceDatabase.Default.FindFirstProjectedEntry(probe, 300, AirspaceClass.Bravo)!.Intersection;
    }

    /// <summary>
    /// A VFR C172 at 7500 ft and 130 kt, <paramref name="nmWest"/> west of where the SFO ring meets the eastbound ray from
    /// <see cref="NearWestRing"/>, on <paramref name="track"/>, flying a started pattern entry to a point 3 nm west of
    /// it with nothing behind, so its path runs on west over the ocean.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context, PatternEntryPhase Entry) EntryWestOfTheRing(
        double nmWest,
        double track,
        double scenarioSeconds
    )
    {
        LatLon position = GeoMath.ProjectPoint(RayEntryOnTheRing(), new TrueHeading(270), nmWest);
        LatLon entryPoint = GeoMath.ProjectPoint(position, new TrueHeading(270), 3.0);
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), position, scenarioSeconds);
        ac.Altitude = 7500;
        ac.IndicatedAirspeed = 130;
        ac.TrueHeading = new TrueHeading(track);
        ac.TrueTrack = new TrueHeading(track);
        var entry = new PatternEntryPhase
        {
            EntryLat = entryPoint.Lat,
            EntryLon = entryPoint.Lon,
            PatternAltitude = 7500,
            Kind = PatternEntryKind.Direct,
        };
        ac.Phases!.Add(entry);
        ac.Phases.Start(ctx);
        return (ac, ctx, entry);
    }

    /// <summary>
    /// A VFR C172 at 7500 ft and 130 kt at <paramref name="position"/>, on <paramref name="heading"/> on an Active left
    /// downwind along that heading whose base turn point is <paramref name="baseTurn"/>, base to the left and final back
    /// along the reciprocal to a threshold 2 nm and 1 nm beyond them, a full-stop landing behind, so the path stops there.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context, DownwindPhase Downwind) DownwindAlong(
        LatLon baseTurn,
        LatLon position,
        double heading
    )
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), position);
        ac.Altitude = 7500;
        ac.IndicatedAirspeed = 130;
        ac.TrueHeading = new TrueHeading(heading);
        ac.TrueTrack = new TrueHeading(heading);
        var baseHeading = new TrueHeading(heading - 90);
        var finalHeading = new TrueHeading(heading + 180);
        LatLon threshold = GeoMath.ProjectPoint(GeoMath.ProjectPoint(baseTurn, baseHeading, 2.0), finalHeading, 1.0);
        var wp = new PatternWaypoints
        {
            UpwindHeading = finalHeading,
            CrosswindHeading = new TrueHeading(heading + 90),
            DownwindHeading = new TrueHeading(heading),
            BaseHeading = baseHeading,
            FinalHeading = finalHeading,
            BaseTurnLat = baseTurn.Lat,
            BaseTurnLon = baseTurn.Lon,
            ThresholdLat = threshold.Lat,
            ThresholdLon = threshold.Lon,
            PatternAltitude = 7500,
            Direction = PatternDirection.Left,
        };
        var downwind = new DownwindPhase { Status = PhaseStatus.Active, Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Add(new LandingPhase());
        return (ac, ctx, downwind);
    }

    /// <summary>The leg of a circuit flown toward <see cref="InsideTheSqlShelf"/> when the hold goes in front of it.</summary>
    private enum CircuitLeg
    {
        Upwind,
        Crosswind,
        Downwind,
    }

    /// <summary>
    /// A level VFR C172 at 3400 ft and 100 kt under the SFO 4000 ft shelf, westbound 4.5 nm east of
    /// <see cref="InsideTheSqlShelf"/>, flying the started <paramref name="leg"/> of a right-hand circuit at
    /// <paramref name="patternAltitudeFt"/> whose end is that point (<see cref="CircuitFrom"/>), the circuit's later legs and a
    /// full-stop landing behind it. The circuit keeps clear of the lower shelves by more than the turn margin, so its path clears
    /// the Class B wherever it flies below the 3000 ft floor. The upwind and crosswind carry <paramref name="climbTargetFt"/>;
    /// <paramref name="aircraftType"/> flies it; a CM to <paramref name="standingCmFt"/>, when set, was issued on the leg before.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context, Phase Leg) LegTowardTheSqlShelf(
        CircuitLeg leg,
        int? climbTargetFt,
        int patternAltitudeFt = 1000,
        string aircraftType = "C172",
        int? standingCmFt = null
    )
    {
        LatLon position = GeoMath.ProjectPoint(InsideTheSqlShelf, new TrueHeading(90), 4.5);
        Assert.Equal(SqlShelfFloorFt, LowestSfoFloorOver(InsideTheSqlShelf));
        Assert.Equal(4000, LowestSfoFloorOver(position));
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), position, scenarioSeconds: 0, aircraftType);
        ac.Altitude = 3400;
        ac.VerticalSpeed = 0;
        ac.TrueHeading = new TrueHeading(270);
        ac.TrueTrack = new TrueHeading(270);
        if (standingCmFt is { } cmFt)
        {
            IssueClimbMaintain(ac, cmFt);
        }

        PatternWaypoints wp = CircuitFrom(InsideTheSqlShelf, leg, patternAltitudeFt);
        Phase[] circuit =
        [
            new UpwindPhase { Waypoints = wp, DepartureClimbTargetFt = climbTargetFt },
            new CrosswindPhase { Waypoints = wp, DepartureClimbTargetFt = climbTargetFt },
            new DownwindPhase { Waypoints = wp },
            new BasePhase { Waypoints = wp },
            new LandingPhase(),
        ];
        foreach (Phase phase in circuit.Skip((int)leg))
        {
            ac.Phases!.Add(phase);
        }

        ac.Phases!.Start(ctx);
        return (ac, ctx, circuit[(int)leg]);
    }

    /// <summary>
    /// A level VFR <paramref name="aircraftType"/> at 1800 ft and 100 kt under the SFO 3000 ft shelf east of KSQL, eastbound on
    /// a started upwind climbing to 3500 ft (its departure climb target), placed so that the shelf's footprint, widened by the
    /// turn margin, ends midway between where a climb from level at the type's initial climb rate and one at its category's
    /// climb rate first reach the floor. Its right-hand circuit at 1000 ft turns crosswind at <see cref="EastOfTheSqlShelf"/>,
    /// a full-stop landing last.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context, Phase Upwind) UpwindClimbingOutFromUnderTheSqlShelf(string aircraftType)
    {
        const double startFt = 1800;
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), InsideTheSqlShelf, scenarioSeconds: 0, aircraftType);
        ac.Altitude = startFt;
        ac.VerticalSpeed = 0;
        ac.TrueHeading = new TrueHeading(90);
        ac.TrueTrack = new TrueHeading(90);
        double initialFpm = AircraftPerformance.InitialClimbRate(aircraftType, ctx.Category);
        double categoryFpm = CategoryPerformance.ClimbRate(ctx.Category, startFt);
        Assert.True(initialFpm > categoryFpm, $"{aircraftType} climbs at {initialFpm} fpm initially, {categoryFpm} fpm by its category");
        double groundSpeedKt = ac.GroundSpeed;
        double NmToTheFloor(double fpm) => groundSpeedKt * Math.Ceiling((SqlShelfFloorFt - startFt) * 60 / fpm) / 3600;
        double exitNm = NmEastToClearOfTheSfoBravo(InsideTheSqlShelf, BravoPathPredictor.TurnMarginNm(ac));
        LatLon shelfExit = GeoMath.ProjectPoint(InsideTheSqlShelf, new TrueHeading(90), exitNm);
        ac.Position = GeoMath.ProjectPoint(shelfExit, new TrueHeading(270), (NmToTheFloor(initialFpm) + NmToTheFloor(categoryFpm)) / 2);
        Assert.Equal(SqlShelfFloorFt, LowestSfoFloorOver(ac.Position));
        PatternWaypoints wp = CircuitFrom(EastOfTheSqlShelf, CircuitLeg.Upwind, patternAltitudeFt: 1000, legHeadingDeg: 90);
        var upwind = new UpwindPhase { Waypoints = wp, DepartureClimbTargetFt = 3500 };
        ac.Phases!.Add(upwind);
        ac.Phases.Add(new CrosswindPhase { Waypoints = wp, DepartureClimbTargetFt = 3500 });
        ac.Phases.Add(new DownwindPhase { Waypoints = wp });
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Add(new LandingPhase());
        ac.Phases.Start(ctx);
        return (ac, ctx, upwind);
    }

    /// <summary>
    /// How far east of <paramref name="from"/> a path at 3500 ft first keeps clear of the SFO Class B by
    /// <paramref name="marginNm"/>, to 0.01 nm.
    /// </summary>
    private static double NmEastToClearOfTheSfoBravo(LatLon from, double marginNm)
    {
        for (double nm = 0; nm < 10; nm += 0.01)
        {
            var point = new PathPoint(GeoMath.ProjectPoint(from, new TrueHeading(90), nm), 3500, nm, 0);
            if (BravoPathPredictor.KeepsClearOf(new PredictedPath([point]), AirspaceDatabase.Default, Sfo, marginNm))
            {
                return nm;
            }
        }

        throw new InvalidOperationException("no point east of the shelf keeps clear of the SFO Class B");
    }

    /// <summary>
    /// A right-hand circuit at <paramref name="patternAltitudeFt"/> whose <paramref name="leg"/> runs on
    /// <paramref name="legHeadingDeg"/> (west unless set) to <paramref name="legEnd"/>, each later leg turning right: a 1.5 nm
    /// crosswind, a 2.5 nm downwind, a 1.5 nm base and a 1.2 nm final to the threshold. The legs before <paramref name="leg"/>
    /// end at <paramref name="legEnd"/> too; they are not flown.
    /// </summary>
    private static PatternWaypoints CircuitFrom(LatLon legEnd, CircuitLeg leg, int patternAltitudeFt, double legHeadingDeg = 270)
    {
        // Upwind, crosswind, downwind, base, final: each leg's heading and where it ends.
        double[] lengthsNm = [0, 1.5, 2.5, 1.5, 1.2];
        var headings = new TrueHeading[5];
        var ends = new LatLon[5];
        for (int i = 0; i < 5; i++)
        {
            headings[i] = new TrueHeading(legHeadingDeg + (90 * (i - (int)leg)));
            ends[i] = i <= (int)leg ? legEnd : GeoMath.ProjectPoint(ends[i - 1], headings[i], lengthsNm[i]);
        }

        LatLon abeam = GeoMath.ProjectPoint(ends[4], headings[3] + 180, lengthsNm[3]);
        return new PatternWaypoints
        {
            DepartureEndLat = ends[4].Lat,
            DepartureEndLon = ends[4].Lon,
            CrosswindTurnLat = ends[0].Lat,
            CrosswindTurnLon = ends[0].Lon,
            DownwindStartLat = ends[1].Lat,
            DownwindStartLon = ends[1].Lon,
            DownwindAbeamLat = abeam.Lat,
            DownwindAbeamLon = abeam.Lon,
            BaseTurnLat = ends[2].Lat,
            BaseTurnLon = ends[2].Lon,
            ThresholdLat = ends[4].Lat,
            ThresholdLon = ends[4].Lon,
            UpwindHeading = headings[0],
            CrosswindHeading = headings[1],
            DownwindHeading = headings[2],
            BaseHeading = headings[3],
            FinalHeading = headings[4],
            PatternAltitude = patternAltitudeFt,
            PatternSizeNm = lengthsNm[3],
            Direction = PatternDirection.Right,
        };
    }

    /// <summary>
    /// Flies <paramref name="seconds"/> s on after a self-release, each second its physics, its phases and the boundary trigger
    /// of a solo session; whether a hold went back in, and a line a second: altitude, vertical speed, phase, and the entry the
    /// trigger's 60 s path predicts.
    /// </summary>
    private static (bool ReHeld, string Trace) FlyOnAfterTheRelease(AircraftState ac, PhaseContext ctx, int seconds)
    {
        var trace = new List<string>();
        bool reHeld = false;
        for (int t = 1; t <= seconds; t++)
        {
            FlightPhysics.Update(ac, 1.0);
            PhaseRunner.Tick(ac, ctx);
            Phase current = ac.Phases!.CurrentPhase!;
            BravoPathEntry? entry = BravoPathPredictor.Predict(ac, ac.Phases, current, ctx, BravoPathPredictor.TriggerLookaheadSeconds) is { } path
                ? BravoPathPredictor.FirstEntry(
                    BravoPathPredictor.Cut(path, BravoPathPredictor.TriggerLookaheadSeconds),
                    AirspaceDatabase.Default,
                    0,
                    Sfo
                )
                : null;
            PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(t), AirspaceDatabase.Default, LookupAirport, _ => ctx);
            reHeld |= ac.Phases.CurrentPhase is AirspaceBoundaryHoldPhase;
            string entryText = entry is null ? "none" : $"{entry.Seconds:F0} s at {entry.AltitudeFt:F0} ft";
            trace.Add($"{t} s: {ac.Altitude:F0} ft, {ac.VerticalSpeed:F0} fpm, {current.Name}, trigger entry {entryText}");
        }

        return (reHeld, string.Join('\n', trace));
    }

    /// <summary>The lowest floor of the SFO Class B volumes over <paramref name="point"/>.</summary>
    private static int LowestSfoFloorOver(LatLon point) =>
        AirspaceDatabase.Default.ClassBArea(Sfo).Where(v => v.ContainsLateral(point)).Min(v => v.LowerFtMsl);

    /// <summary>What a CM to <paramref name="altitudeFt"/> writes on the aircraft, its pattern altitude override aside.</summary>
    private static void IssueClimbMaintain(AircraftState ac, int altitudeFt)
    {
        ac.NoteControllerAltitudeCommand();
        ac.Targets.TargetAltitude = altitudeFt;
        ac.Targets.AltitudeCeiling = null;
        ac.Targets.AssignedAltitude = altitudeFt;
    }

    /// <summary>
    /// Puts an SFO Class B hold in front of the running phase and starts it by hand, as the pending-start path would, with no
    /// tick, so no release has been judged yet.
    /// </summary>
    private static AirspaceBoundaryHoldPhase HoldStartedInFrontUnjudged(AircraftState ac, PhaseContext ctx)
    {
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        hold.Status = PhaseStatus.Active;
        hold.OnStart(ctx);
        return hold;
    }

    /// <summary><paramref name="hold"/> still stands in front of <paramref name="held"/> and never released itself.</summary>
    private static void AssertStillHeld(AircraftState ac, AirspaceBoundaryHoldPhase hold, Phase held)
    {
        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(held, ac.Phases.HeldPhase);
        Assert.Null(AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));
    }

    /// <summary><paramref name="hold"/> released itself, uncleared, and a restart copy of <paramref name="held"/> runs.</summary>
    private static void AssertSelfReleasedToACopyOf(AircraftState ac, PhaseContext ctx, AirspaceBoundaryHoldPhase hold, Phase held)
    {
        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Null(ac.Phases!.HeldPhase);
        Phase copy = ac.Phases.CurrentPhase!;
        Assert.IsType(held.GetType(), copy);
        Assert.NotSame(held, copy);
        Assert.Equal(ctx.ScenarioElapsedSeconds, AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));
        Assert.False(ac.IsClearedIntoBravo(Sfo));
    }

    /// <summary>
    /// A VFR C172 at pattern altitude on the OAK 28R left downwind line 0.2 nm past the abeam point, under the SFO 2100 ft
    /// shelf, flying a started teardrop re-entry with the downwind, the base and a full-stop landing behind it.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context, TeardropReentryPhase Teardrop, DownwindPhase Downwind) TeardropPastAbeamAtOak()
    {
        PatternWaypoints wp = OakLeftPattern();
        LatLon pastAbeam = GeoMath.ProjectPoint(new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon), wp.DownwindHeading, 0.2);
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), pastAbeam);
        ac.Altitude = wp.PatternAltitude;
        ac.TrueHeading = wp.DownwindHeading;
        ac.TrueTrack = wp.DownwindHeading;
        var teardrop = new TeardropReentryPhase { Waypoints = wp };
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(teardrop);
        ac.Phases.Add(downwind);
        ac.Phases.Add(new BasePhase { Waypoints = wp });
        ac.Phases.Add(new LandingPhase());
        ac.Phases.Start(ctx);
        return (ac, ctx, teardrop, downwind);
    }

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

    private static SimScenarioState SoloScenario(double elapsedSeconds) =>
        new()
        {
            ScenarioId = "test-boundary-hold-end",
            ScenarioName = "Boundary Hold End",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = "OAK",
            StudentPositionType = "TWR",
            ElapsedSeconds = elapsedSeconds,
        };

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => new LatLon(37.6213, -122.3790),
            _ => null,
        };

    [Fact]
    public void PhaseRunnerTick_PairedHoldCompletes_ResumesAnInPlacePhase_WithoutASecondOnStart()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        // PatternEntryPhase.OnStart writes its pattern altitude here: a second start would overwrite this. A controller
        // altitude command, so the hold's end leaves it standing.
        ac.NoteControllerAltitudeCommand();
        ac.Targets.TargetAltitude = 4500;
        ac.GrantBravoClearance(Sfo);

        Exception? thrown = Record.Exception(() => PhaseRunner.Tick(ac, ctx));

        Assert.Null(thrown);
        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(4500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void PhaseRunnerTick_PairedHoldCompletes_SwapsInARestartCopy_StartedOnceAfterTheHoldEnds()
    {
        AircraftState ac = OnDownwind(out DownwindPhase downwind);
        PhaseContext ctx = MinimalContext(ac);
        // The hold saves these as the targets to put back; the copy's OnStart writes its own over them only if it runs after.
        ac.Targets.TargetTrueHeading = downwind.Waypoints!.DownwindHeading + 40.0;
        ac.Targets.PreferredTurnDirection = TurnDirection.Right;
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        ac.GrantBravoClearance(Sfo);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Equal(PhaseStatus.Completed, downwind.Status);
        DownwindPhase copy = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        Assert.NotSame(downwind, copy);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(downwind.Waypoints.DownwindHeading, ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);

        PhaseRunner.Tick(ac, ctx);

        Assert.Same(copy, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, copy.Status);
    }

    [Fact]
    public void PhaseRunnerTick_PairedFinalResumes_KeepingItsPreFinalSpacingCeiling()
    {
        // A final into SFO 28R: its path runs inside the SFO Class B, so the hold stands until the grant.
        RunwayInfo sfo28R = NavigationDatabase.Instance.GetRunway("SFO", "28R") ?? throw new InvalidOperationException("SFO 28R missing");
        (AircraftState ac, PhaseContext ctx) = AircraftAt(sfo28R, PointOnFinal(sfo28R, 6.0));
        // The final latches its spacing ceiling at entry from the target standing then.
        ac.Targets.TargetSpeed = 180.0;
        var final = new FinalApproachPhase();
        ac.Phases!.Add(final);
        ac.Phases.Start(ctx);
        Assert.Equal(180.0, SpacingCeilingOf(final));
        // The hold saves and puts back this target: a copy that re-latched its ceiling at its start would take it.
        ac.Targets.TargetSpeed = 120.0;
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        ac.GrantBravoClearance(Sfo);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Equal(PhaseStatus.Completed, final.Status);
        FinalApproachPhase copy = Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        Assert.NotSame(final, copy);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(180.0, SpacingCeilingOf(copy));
    }

    [Fact]
    public void PairedHold_AHeadingStandingBeforeTheHold_IsNotFlown_AndThePatternEntryResumesItsRoute()
    {
        SimulationEngine engine = BuildOakEngine();
        RunwayInfo runway = Oak28R();
        (AircraftState ac, _) = AircraftAt(runway, GeoMath.ProjectPoint(EntryPoint(runway), runway.TrueHeading + 180, 6.0));
        // At 7500 ft the pattern entry's path runs inside the SFO Class B over the East Bay, so the hold stands until the grant.
        ac.Altitude = 7500;
        ac.Phases = null;
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand(ac.Callsign, "FH 270").Success);
        Assert.True(engine.SendCommand(ac.Callsign, "ELD 28R").Success);
        Assert.Equal(new MagneticHeading(270), ac.Targets.AssignedMagneticHeading);
        engine.TickOneSecond();
        PatternEntryPhase entry = Assert.IsType<PatternEntryPhase>(ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        List<string> entryRoute = [.. ac.Targets.NavigationRoute.Select(t => t.Name)];
        Assert.NotEmpty(entryRoute);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases.InsertHoldInFront(hold);

        for (int t = 0; t < 4; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(entry, ac.Phases.HeldPhase);

        ac.GrantBravoClearance(Sfo);
        engine.TickOneSecond();

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(entryRoute, ac.Targets.NavigationRoute.Select(t => t.Name));

        for (int t = 0; (t < 900) && (ac.Phases.CurrentPhase == entry); t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal(PhaseStatus.Completed, entry.Status);
    }

    [Fact]
    public void PairedHold_ANewHeadingThatStaysOutsideTheBravo_EndsTheHold_AndIsFlown()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        var assigned = new MagneticHeading(270);
        Assert.Null(BravoEntryOn(ac, assigned));
        Assert.True(engine.SendCommand(ac.Callsign, "FH 270").Success);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, climb.Status);
        Assert.Equal(assigned.ToTrue(ac.Declination), ac.Targets.TargetTrueHeading);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void PairedHold_ANewHeadingIntoTheBravo_IsHeldThrough_AndFlownOnceCleared()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        var assigned = new MagneticHeading(90);
        Assert.NotNull(BravoEntryOn(ac, assigned));
        Assert.True(engine.SendCommand(ac.Callsign, "FH 090").Success);

        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(climb, ac.Phases.HeldPhase);
        Assert.Equal(ac.TrueHeading + (-180.0), ac.Targets.TargetTrueHeading);
        Assert.Equal(TurnDirection.Left, ac.Targets.PreferredTurnDirection);
        Assert.Null(AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));

        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(assigned.ToTrue(ac.Declination), ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void PairedHold_ANewDirectThatStaysOutsideTheBravo_EndsTheHold_AndIsFlown()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        Assert.Null(BravoEntryToward(ac, OutsideFix));
        Assert.True(engine.SendCommand(ac.Callsign, $"DCT {OutsideFix}").Success);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases!.CurrentPhase);
        Assert.Equal(OutsideFix, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.Null(ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PairedHold_ANewDirectIntoTheBravo_IsHeldThrough_AndFlownOnceCleared()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        Assert.NotNull(BravoEntryToward(ac, IntoFix));
        Assert.True(engine.SendCommand(ac.Callsign, $"DCT {IntoFix}").Success);

        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(climb, ac.Phases.HeldPhase);
        // Stashed, so the orbit is what it steers by.
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(ac.TrueHeading + (-180.0), ac.Targets.TargetTrueHeading);
        Assert.True(HoldDto(hold).StashedDirect);

        // A snapshot taken mid-hold keeps the stash, and the restored hold puts the direct back.
        AirspaceBoundaryHoldPhase restored = RoundTripCurrentHold(ac, hold);
        Assert.True(HoldDto(restored).StashedDirect);
        Assert.Null(AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));
        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, restored.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(IntoFix, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.Null(ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PairedHold_AHeadingHeldThrough_ThenANewHeadingOutsideTheBravo_EndsTheHold_AndIsFlown()
    {
        var outside = new MagneticHeading(270);
        var into = new MagneticHeading(90);
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbWestOfTheBravo("FH 270");
        Assert.Null(BravoEntryOn(ac, outside));
        Assert.NotNull(BravoEntryOn(ac, into));
        Assert.Equal(270.0, HoldDto(hold).HeadingStandingAtStartDeg);

        // The heading standing when the hold went in is not a vector.
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(hold, ac.Phases!.CurrentPhase);

        Assert.True(engine.SendCommand(ac.Callsign, "FH 090").Success);
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Same(climb, ac.Phases.HeldPhase);
        AirspaceBoundaryHoldPhaseDto midHold = HoldDto(hold);
        Assert.Null(midHold.HeadingStandingAtStartDeg);
        Assert.True(midHold.HeldThroughVector);
        Assert.False(midHold.StashedDirect);

        // A snapshot taken mid-hold keeps the cleared reference: 270 is new now, and ends the orbit.
        AirspaceBoundaryHoldPhase restored = RoundTripCurrentHold(ac, hold);
        Assert.Null(HoldDto(restored).HeadingStandingAtStartDeg);
        Assert.True(engine.SendCommand(ac.Callsign, "FH 270").Success);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, restored.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(outside.ToTrue(ac.Declination), ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void PairedHold_AHeldThroughHeadingCleared_EndsWithTheHeldPhasesRouteRestored()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        AirspaceBoundaryHoldPhaseDto saved = HoldDto(hold);
        Assert.NotNull(BravoEntryOn(ac, new MagneticHeading(90)));
        Assert.True(engine.SendCommand(ac.Callsign, "FH 090").Success);
        PhaseRunner.Tick(ac, ctx);
        Assert.True(HoldDto(hold).HeldThroughVector);

        ac.Targets.AssignedMagneticHeading = null;
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Null(AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds(ac));
        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(HeldRouteFix, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.Equal(saved.OriginalTargetHeadingDeg, ac.Targets.TargetTrueHeading?.Degrees);
        Assert.Equal(saved.OriginalTurnDirection, (int?)ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PairedHold_WhenTheBravoCanNoLongerBeEntered_ResumesTheHeldPhase()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        // Level well beneath this volume, so the hold's first tick finds it can no longer be entered.
        AirspaceBoundaryHoldPhase hold = SfoBravoHoldBetween(8000, 10000);
        ac.Phases!.InsertHoldInFront(hold);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);
    }

    [Fact]
    public void PairedHold_WhoseHeldPhaseIsGone_EndsByTheOldPath()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        entry.Status = PhaseStatus.Completed;
        entry.OnEnd(ctx, PhaseStatus.Completed);
        Assert.True(ac.Phases!.Phases.Remove(entry));
        Assert.Null(ac.Phases.HeldPhase);
        ac.GrantBravoClearance(Sfo);

        Exception? thrown = Record.Exception(() => PhaseRunner.Tick(ac, ctx));

        Assert.Null(thrown);
        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.True(ac.Phases.IsComplete);
    }

    [Fact]
    public void FreeFlightHold_GivenAVector_StillEnds()
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), new LatLon(37.62, -122.12));
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.Add(hold);
        ac.Phases.Start(ctx);
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(hold, ac.Phases.CurrentPhase);
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(10);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.True(ac.Phases.IsComplete);
    }

    [Fact]
    public void SnapshotRoundTrip_OfAPairedHoldWithAStandingHeading_KeepsTheRecordedHeading()
    {
        (AircraftState ac, PhaseContext ctx, _) = GoAroundWestOfTheBravo();
        // West is outside the Class B: read as a new vector it would end the hold.
        var standing = new MagneticHeading(270);
        Assert.Null(BravoEntryOn(ac, standing));
        ac.Targets.AssignedMagneticHeading = standing;
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);

        string json = JsonSerializer.Serialize((AirspaceBoundaryHoldPhaseDto)hold.ToSnapshot(), RecordingJsonOptions.Default);
        AirspaceBoundaryHoldPhaseDto dto = JsonSerializer.Deserialize<AirspaceBoundaryHoldPhaseDto>(json, RecordingJsonOptions.Default)!;
        Assert.Equal(270.0, dto.HeadingStandingAtStartDeg);
        var restored = AirspaceBoundaryHoldPhase.FromSnapshot(dto);
        ac.Phases!.Phases[ac.Phases.CurrentIndex] = restored;

        PhaseRunner.Tick(ac, ctx);

        Assert.Same(restored, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, restored.Status);
        Assert.Equal(270.0, ((AirspaceBoundaryHoldPhaseDto)restored.ToSnapshot()).HeadingStandingAtStartDeg);

        // A hold with no standing heading writes no member for it.
        string bare = JsonSerializer.Serialize((AirspaceBoundaryHoldPhaseDto)SfoBravoHold().ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AirspaceBoundaryHoldPhaseDto.HeadingStandingAtStartDeg), bare, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualBackstop_HeldGoAroundOnAVisual_KeepsThePairAndTheApproach()
    {
        SimulationEngine engine = BuildOakEngine();
        AircraftState ac = EstablishOakCvaWithFieldInSight(engine);
        PhaseList list = ac.Phases!;
        var goAround = new GoAroundPhase { Status = PhaseStatus.Active };
        list.Phases[list.CurrentIndex] = goAround;
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        list.InsertHoldInFront(hold);
        // Started by hand, as the pending-start path would, so no tick of the hold's own exit test runs.
        hold.Status = PhaseStatus.Active;
        hold.OnStart(MinimalContext(ac));
        // Nothing in sight and no loss this pass: only the backstop would see it.
        ac.Approach.HasReportedFieldInSight = false;
        engine.World.Weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 1SM CLR 20/12 A2992"] };

        engine.TickVisualDetection();

        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(goAround, ac.Phases.HeldPhase);
        Assert.Equal(PhaseStatus.Active, goAround.Status);
        Assert.NotNull(ac.Phases.ActiveApproach);
    }

    /// <summary>
    /// <see cref="HeldClimbWestOfTheBravo"/> told DCT <see cref="HeldRouteFix"/> before the hold went in, so the held phase
    /// carries a route the hold saved.
    /// </summary>
    private static (
        SimulationEngine Engine,
        AircraftState Aircraft,
        PhaseContext Context,
        InitialClimbPhase Climb,
        AirspaceBoundaryHoldPhase Hold
    ) HeldClimbOnADirectWestOfTheBravo()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbWestOfTheBravo($"DCT {HeldRouteFix}");
        Assert.Equal(HeldRouteFix, Assert.Single(HoldDto(hold).OriginalRoute!).Name);
        return (engine, ac, ctx, climb, hold);
    }

    private static AirspaceBoundaryHoldPhaseDto HoldDto(AirspaceBoundaryHoldPhase hold) => (AirspaceBoundaryHoldPhaseDto)hold.ToSnapshot();

    /// <summary>Round-trips the current hold through JSON and puts the restored hold in its place in the list.</summary>
    private static AirspaceBoundaryHoldPhase RoundTripCurrentHold(AircraftState ac, AirspaceBoundaryHoldPhase hold)
    {
        string json = JsonSerializer.Serialize(HoldDto(hold), RecordingJsonOptions.Default);
        AirspaceBoundaryHoldPhaseDto dto = JsonSerializer.Deserialize<AirspaceBoundaryHoldPhaseDto>(json, RecordingJsonOptions.Default)!;
        var restored = AirspaceBoundaryHoldPhase.FromSnapshot(dto);
        ac.Phases!.Phases[ac.Phases.CurrentIndex] = restored;
        return restored;
    }

    /// <summary>
    /// A VFR C172 eastbound at 7500 ft west of the SFO Class B ring, on an initial climb given
    /// <paramref name="preHoldCommand"/> before the hold went in, with an SFO Class B hold started in front of it. The climb
    /// is Active and never ticked, so it cannot complete on its own.
    /// </summary>
    private static (
        SimulationEngine Engine,
        AircraftState Aircraft,
        PhaseContext Context,
        InitialClimbPhase Climb,
        AirspaceBoundaryHoldPhase Hold
    ) HeldClimbWestOfTheBravo(string preHoldCommand)
    {
        SimulationEngine engine = BuildOakEngine();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), NearWestRing);
        ac.Altitude = 7500;
        ac.IndicatedAirspeed = 120;
        ac.TrueHeading = new TrueHeading(90);
        ac.TrueTrack = new TrueHeading(90);
        var climb = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            IsVfr = true,
            CruiseAltitude = 7500,
            Status = PhaseStatus.Active,
        };
        ac.Phases!.Add(climb);
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand(ac.Callsign, preHoldCommand).Success);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        Assert.Empty(ac.Targets.NavigationRoute);
        return (engine, ac, ctx, climb, hold);
    }

    /// <summary>The SFO Class B volume a level track toward <paramref name="fix"/> enters, as the hold projects it; null for none.</summary>
    private static AirspaceVolume? BravoEntryToward(AircraftState ac, string fix)
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(fix) ?? throw new InvalidOperationException($"{fix} missing");
        var track = new TrueHeading(GeoMath.BearingTo(ac.Position, new LatLon(lat, lon)));
        return AirspaceDatabase.Default.FindLevelTrackClassBEntry(
            ac.Position,
            ac.Altitude,
            track,
            ac.GroundSpeed,
            ImplicitBravoClearance.WaitingEntryLookaheadSeconds,
            Sfo
        );
    }

    /// <summary>Puts an SFO Class B hold in front of the running phase and starts it with one tick, the gate still shut.</summary>
    private static AirspaceBoundaryHoldPhase StartedHoldInFront(AircraftState ac, PhaseContext ctx)
    {
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        Phase held = ac.Phases!.CurrentPhase!;
        ac.Phases.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(held, ac.Phases.HeldPhase);
        return hold;
    }

    /// <summary>The SFO Class B volume a level track on <paramref name="heading"/> enters, as the hold projects it; null for none.</summary>
    private static AirspaceVolume? BravoEntryOn(AircraftState ac, MagneticHeading heading) =>
        AirspaceDatabase.Default.FindLevelTrackClassBEntry(
            ac.Position,
            ac.Altitude,
            heading.ToTrue(ac.Declination),
            ac.GroundSpeed,
            ImplicitBravoClearance.WaitingEntryLookaheadSeconds,
            Sfo
        );

    /// <summary>A C172 eastbound at 7500 ft west of the SFO Class B ring, flying an Active go-around (never started), unheld.</summary>
    private static (AircraftState Aircraft, PhaseContext Context, GoAroundPhase GoAround) GoAroundWestOfTheBravo()
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), NearWestRing);
        ac.Altitude = 7500;
        ac.IndicatedAirspeed = 120;
        ac.TrueHeading = new TrueHeading(90);
        ac.TrueTrack = new TrueHeading(90);
        var goAround = new GoAroundPhase { Status = PhaseStatus.Active };
        ac.Phases!.Add(goAround);
        return (ac, ctx, goAround);
    }

    /// <summary>
    /// A C172 on a started pattern entry whose entry point lies inside the SFO Class B surface area, so the held path stays
    /// blocked and the hold never ends by itself (<see cref="AirspaceBoundaryHoldPhase.SelfReleasedAtSeconds"/>).
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context, PatternEntryPhase Entry) AircraftOnPatternEntry()
    {
        RunwayInfo runway = Oak28R();
        LatLon entryPoint = InsideTheSfoSurfaceArea;
        var entry = new PatternEntryPhase
        {
            EntryLat = entryPoint.Lat,
            EntryLon = entryPoint.Lon,
            PatternAltitude = runway.ElevationFt + 1000,
            Kind = PatternEntryKind.Direct,
        };
        (AircraftState ac, PhaseContext ctx) = AircraftAt(runway, GeoMath.ProjectPoint(entryPoint, runway.TrueHeading + 180, 6.0));
        ac.Phases!.Add(entry);
        ac.Phases.Start(ctx);
        PhaseRunner.Tick(ac, ctx);
        return (ac, ctx, entry);
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("OAK 28R missing");

    /// <summary>A left-traffic downwind for 28R lies to the south; the entry point is 1 nm abeam the threshold.</summary>
    private static LatLon EntryPoint(RunwayInfo runway) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + (-90), 1.0);

    private static LatLon PointOnFinal(RunwayInfo runway, double distanceNm) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + 180, distanceNm);

    /// <summary><see cref="AircraftAt(RunwayInfo, LatLon, double)"/> at scenario second 0.</summary>
    private static (AircraftState Aircraft, PhaseContext Context) AircraftAt(RunwayInfo runway, LatLon position) =>
        AircraftAt(runway, position, scenarioSeconds: 0);

    private static (AircraftState Aircraft, PhaseContext Context) AircraftAt(
        RunwayInfo runway,
        LatLon position,
        double scenarioSeconds,
        string aircraftType = "C172"
    )
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = aircraftType,
            AirportId = "OAK",
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 1500,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        var ctx = new PhaseContext
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
            StudentPositionType = "TWR",
            ScenarioElapsedSeconds = scenarioSeconds,
        };
        return (ac, ctx);
    }

    private static PhaseContext MinimalContext(AircraftState ac) => CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);

    /// <summary>A VFR M20P north of LVK, entered on the 25R right downwind with ERD 25R, the downwind started.</summary>
    private static AircraftState OnDownwind(out DownwindPhase downwind)
    {
        var ac = new AircraftState
        {
            Callsign = "N435C",
            AircraftType = "M20P",
            Position = new LatLon(37.71, -121.80),
            TrueHeading = new TrueHeading(70),
            TrueTrack = new TrueHeading(70),
            Altitude = 1400,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KLVK",
            },
        };
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("ERD 25R");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1), validateDctFixes: false));
        Assert.True(result.Success, result.Message);
        while ((ac.Phases?.CurrentPhase is not DownwindPhase) && (ac.Phases?.CurrentPhase is not null))
        {
            ac.Phases.AdvanceToNext(MinimalContext(ac));
        }

        downwind = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        if (downwind.Status == PhaseStatus.Pending)
        {
            downwind.Status = PhaseStatus.Active;
            downwind.OnStart(MinimalContext(ac));
        }

        return ac;
    }

    private static SimulationEngine BuildOakEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-boundary-hold-end",
                ScenarioName = "Boundary Hold End",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

    /// <summary>An IFR B738 six miles out on the OAK 30 final at 2000 ft, cleared for the visual with the field in sight.</summary>
    private static AircraftState EstablishOakCvaWithFieldInSight(SimulationEngine engine)
    {
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        var ac = new AircraftState
        {
            Callsign = "FLD1",
            AircraftType = "B738",
            Position = PointOnFinal(rwy, 6.0),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = 2000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(2000),
            },
        };
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand("FLD1", "RFIS").Success);
        Assert.True(engine.SendCommand("FLD1", "CVA 30").Success);
        for (int t = 1; (t <= 15) && !ac.Approach.HasReportedFieldInSight; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(ac.Approach.HasReportedFieldInSight, "field should be in sight in clear weather on final");
        Assert.StartsWith("VIS", ac.Phases!.ActiveApproach!.ApproachId, StringComparison.Ordinal);
        return ac;
    }

    private static AirspaceBoundaryHoldPhase SfoBravoHold() =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = Sfo,
            NameText = Sfo,
            ReferencePosition = new LatLon(37.6213, -122.3790),
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
        };

    /// <summary>An SFO Class B hold whose held volume spans <paramref name="lowerFtMsl"/> to <paramref name="upperFtMsl"/>.</summary>
    private static AirspaceBoundaryHoldPhase SfoBravoHoldBetween(int lowerFtMsl, int upperFtMsl) =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = Sfo,
            NameText = Sfo,
            ReferencePosition = new LatLon(37.6213, -122.3790),
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
            VolumeLowerFtMsl = lowerFtMsl,
            VolumeUpperFtMsl = upperFtMsl,
        };

    private static double? SpacingCeilingOf(FinalApproachPhase phase) => ((FinalApproachPhaseDto)phase.ToSnapshot()).SpacingCeilingKts;
}
