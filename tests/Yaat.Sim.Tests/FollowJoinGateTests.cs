using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Adversarial gate checks for the FOLLOW join geometry (issue #352):
///
/// - <see cref="VfrFollowPhase"/>'s final join keeps the 30° visual-intercept gate and
///   never captures through a parallel runway's final approach course.
/// - <see cref="PatternCommandHandler.IsAtOrPastDownwindEntry"/>: the present-position
///   downwind join fires only alongside the circuit (at/past the entry point, short of the
///   base turn, laterally near the track, and not high above pattern altitude).
/// - <see cref="CommandDispatcher.ChooseFollowJoinDirection"/>: the pattern-aware FOLLOW
///   install flies the runway's established circuit side, falling back to the follower's
///   own side only when neither the lead nor the runway defines one.
/// </summary>
[Collection("NavDbMutator")]
public class FollowJoinGateTests
{
    private const string Leader = "LEAD";
    private const string Follower = "FOLL";

    public FollowJoinGateTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // Runway 28R at KTEST: heading 280°, sea level.
    private static RunwayInfo Runway28R() => TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 0);

    /// <summary>A close parallel 28L whose centerline sits 0.15 nm on the LEFT (south) side of 28R's.</summary>
    private static RunwayInfo Runway28LParallel()
    {
        RunwayInfo r28R = Runway28R();
        LatLon offsetThreshold = GeoMath.ProjectPoint(new LatLon(r28R.ThresholdLatitude, r28R.ThresholdLongitude), r28R.TrueHeading - 90.0, 0.15);
        LatLon offsetEnd = GeoMath.ProjectPoint(new LatLon(r28R.EndLatitude, r28R.EndLongitude), r28R.TrueHeading - 90.0, 0.15);
        return TestRunwayFactory.Make(
            designator: "28L",
            heading: 280,
            elevationFt: 0,
            thresholdLat: offsetThreshold.Lat,
            thresholdLon: offsetThreshold.Lon,
            endLat: offsetEnd.Lat,
            endLon: offsetEnd.Lon
        );
    }

    private static AircraftState MakeVfr(string callsign, LatLon pos, TrueHeading heading, double altitude, double ias) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = pos,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST", FlightRules = "VFR" },
            Approach = new AircraftApproachState { HasReportedTrafficInSight = true },
        };

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, Func<string, AircraftState?> lookup) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            AircraftLookup = lookup,
            Logger = NullLogger.Instance,
        };

    /// <summary>Point at <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> laterally
    /// (positive toward the runway heading's right-hand side).</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading.ToReciprocal(),
            alongNm
        );
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }
        TrueHeading perp = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perp, Math.Abs(crossNm));
    }

    /// <summary>Follower in <see cref="VfrFollowPhase"/> behind a lead on a bare straight-in final (no pattern waypoints).</summary>
    private static (AircraftState Follower, VfrFollowPhase Phase, PhaseContext Ctx) SetupFinalJoin(
        RunwayInfo rwy,
        double leadDistNm,
        double followerAlongNm,
        double followerCrossNm,
        double followerTrackDeg
    )
    {
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, leadDistNm, 0), rwy.TrueHeading, altitude: leadDistNm * 318.0, ias: 75);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new FinalApproachPhase());

        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, followerAlongNm, followerCrossNm), new TrueHeading(followerTrackDeg), 1000, ias: 90);
        follower.Approach.FollowingCallsign = Leader;
        var phase = new VfrFollowPhase(Leader, patternReturn: null);
        follower.Phases = new PhaseList();
        follower.Phases.Add(phase);

        AircraftState? lookup(string cs) =>
            cs == Leader ? lead
            : cs == Follower ? follower
            : null;
        PhaseContext ctx = Ctx(follower, rwy, lookup);
        follower.Phases.Start(ctx);
        return (follower, phase, ctx);
    }

    // ─── Standard 30° intercept gate ───

    [Fact]
    public void JoinLeadFinal_SteepIntercept_DoesNotJoin()
    {
        RunwayInfo rwy = Runway28R();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        // Converging at 35° — beyond the 30° visual-intercept gate; keep pursuing until shallower.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy,
            leadDistNm: 1.8,
            followerAlongNm: 3.5,
            followerCrossNm: 0.3,
            followerTrackDeg: 245
        );

        phase.OnTick(ctx);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void JoinLeadFinal_ShallowIntercept_Joins()
    {
        RunwayInfo rwy = Runway28R();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        // 25° intercept, trailing spacing satisfied — commits the join.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy,
            leadDistNm: 1.8,
            followerAlongNm: 3.5,
            followerCrossNm: 0.3,
            followerTrackDeg: 255
        );

        phase.OnTick(ctx);

        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
    }

    // ─── Visual follow: the final-join in-trail floor ───

    /// <summary>
    /// The in-trail floor the final join waits for: <see cref="VfrFollowPhase.SameRunwayInTrailFloorNm"/> behind a lead with
    /// no wake minimum for this follower, <see cref="VfrFollowPhase.WakeLeadInTrailFloorNm"/> behind one that has one. The
    /// radar wake minimum itself does not apply to a visual follow (AIM 7-4-8.b).
    /// </summary>
    [Theory]
    [InlineData("C550", "C172", 1.5)] // TBL 5-5-2 has no (I, I) cell: a light jet carries no minimum for a light single
    [InlineData("C172", "C172", 1.5)]
    [InlineData("B738", "B738", 1.5)] // no (F, F) cell either
    [InlineData("B738", "C172", 2.5)] // TBL 5-5-2 (F, I) = 4 nm
    [InlineData("B744", "C172", 2.5)] // TBL 5-5-2 (B, I) = 6 nm
    public void FinalInTrail_IsTheSameRunwayFloorOrTheWakeFloor(string leadType, string followerType, double expected)
    {
        AircraftState lead = MakeVfr(Leader, new LatLon(37.0, -122.0), new TrueHeading(280), altitude: 1000, ias: 90);
        lead.AircraftType = leadType;
        AircraftState follower = MakeVfr(Follower, new LatLon(37.0, -122.0), new TrueHeading(280), altitude: 1000, ias: 90);
        follower.AircraftType = followerType;
        PhaseContext ctx = Ctx(follower, Runway28R(), cs => cs == Leader ? lead : null);

        Assert.Equal(expected, VfrFollowPhase.RequiredFinalInTrailNm(ctx, lead));
    }

    [Fact]
    public void JoinLeadFinal_BehindALightJetWithNoWakeMinimum_JoinsAtTheSameRunwayFloor()
    {
        RunwayInfo rwy = Runway28R();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        // 2.0 nm in trail of a C550 on a 1.4 nm straight-in final, 0.5 nm off the centerline, 25° intercept.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy,
            leadDistNm: 1.4,
            followerAlongNm: 3.4,
            followerCrossNm: 0.5,
            followerTrackDeg: 255
        );
        ctx.AircraftLookup!(Leader)!.AircraftType = "C550";

        phase.OnTick(ctx);

        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
    }

    [Fact]
    public void JoinLeadFinal_BehindAWakeLead_WaitsForTheWakeFloor()
    {
        RunwayInfo rwy = Runway28R();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        // 2.0 nm in trail of a B744: inside the 2.5 nm wake floor, so the join keeps pursuing.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy,
            leadDistNm: 1.4,
            followerAlongNm: 3.4,
            followerCrossNm: 0.5,
            followerTrackDeg: 255
        );
        ctx.AircraftLookup!(Leader)!.AircraftType = "B744";

        phase.OnTick(ctx);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void JoinLeadFinal_BehindAWakeLead_JoinsPastTheWakeFloor()
    {
        RunwayInfo rwy = Runway28R();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        // 2.7 nm in trail of a B744: past the 2.5 nm wake floor, so the join commits.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy,
            leadDistNm: 1.4,
            followerAlongNm: 4.1,
            followerCrossNm: 0.5,
            followerTrackDeg: 255
        );
        ctx.AircraftLookup!(Leader)!.AircraftType = "B744";

        phase.OnTick(ctx);

        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
    }

    // ─── Parallel-final capture gate ───

    /// <summary>
    /// Real-navdata regression: KOAK stores its runways oriented to the low-numbered ends
    /// (10L/10R), so a gate that only tests the stored <c>TrueHeading</c> silently never
    /// fires for 28R/28L. The synthetic-runway tests below cannot catch that class of bug.
    /// </summary>
    [Fact]
    public void JoinCapturePathCrossesParallelFinal_RealKoak_FiresAcross28L()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }

        LatLon southOf28L = OffFinal(rwy, 3.5, -0.6);
        LatLon northSide = OffFinal(rwy, 3.5, 0.6);
        Assert.True(
            VfrFollowPhase.JoinCapturePathCrossesParallelFinal(southOf28L, rwy),
            "A follower on the far side of 28L must not capture 28R's final across it."
        );
        Assert.False(
            VfrFollowPhase.JoinCapturePathCrossesParallelFinal(northSide, rwy),
            "A follower on the free (north) side of 28R has no parallel in between."
        );
    }

    [Fact]
    public void JoinLeadFinal_FromFarSideOfParallel_DoesNotJoin()
    {
        RunwayInfo rwy28R = Runway28R();
        RunwayInfo rwy28L = Runway28LParallel();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R, rwy28L));
        // Follower 0.6 nm on the LEFT (28L) side of 28R's centerline: capturing 28R final
        // from there slices through 28L's final approach course.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy28R,
            leadDistNm: 1.8,
            followerAlongNm: 3.5,
            followerCrossNm: -0.6,
            followerTrackDeg: 300
        );

        phase.OnTick(ctx);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void JoinLeadFinal_FromFreeSideOfParallel_Joins()
    {
        RunwayInfo rwy28R = Runway28R();
        RunwayInfo rwy28L = Runway28LParallel();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R, rwy28L));
        // Same geometry mirrored to the RIGHT (north) side — no runway between the
        // follower and 28R's centerline.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy28R,
            leadDistNm: 1.8,
            followerAlongNm: 3.5,
            followerCrossNm: 0.6,
            followerTrackDeg: 260
        );

        phase.OnTick(ctx);

        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
    }

    /// <summary>Follower in <see cref="VfrFollowPhase"/> with the lead's landing runway captured
    /// (one tick while the lead is airborne on final), then the lead on the ground.</summary>
    private static (AircraftState Follower, VfrFollowPhase Phase, PhaseContext Ctx, AircraftState Lead) SetupLeadLandedShortcut(
        RunwayInfo rwy,
        double followerCrossNm
    )
    {
        // Track 180° off the final course: the airborne TryJoinLeadFinal never fires, and once the lead is down the landed-lead
        // gates refuse the final join (an intercept far past 30°), so the follow ends into a pattern re-entry for 28R.
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx) = SetupFinalJoin(
            rwy,
            leadDistNm: 0.8,
            followerAlongNm: 3.5,
            followerCrossNm,
            followerTrackDeg: 112
        );
        AircraftState lead = ctx.AircraftLookup!(Leader)!;
        phase.OnTick(ctx);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        lead.IsOnGround = true;
        return (follower, phase, ctx, lead);
    }

    [Fact]
    public void LeadLandedShortcut_FromFarSideOfParallel_ReentersByMidfieldCrossing()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx, AircraftState _) = SetupLeadLandedShortcut(rwy, followerCrossNm: -0.6);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";

        bool done = phase.OnTick(ctx);

        // Beyond 28L from 28R's right-traffic circuit: the landed-lead gates refuse the capture, the follow ends and the
        // follower re-enters 28R's pattern by crossing midfield (AIM 4-3-3) rather than joining the final directly.
        Assert.True(done, "The phase should end (lead on the ground) once the shortcut refuses the capture.");
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<MidfieldCrossingPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.DoesNotContain(follower.Phases.Phases, p => p is PatternEntryPhase { Kind: PatternEntryKind.Final });
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
    }

    [Fact]
    public void LeadLandedShortcut_FromFreeSide_ReentersThePatternFor28R()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }
        (AircraftState? follower, VfrFollowPhase? phase, PhaseContext? ctx, AircraftState _) = SetupLeadLandedShortcut(rwy, followerCrossNm: 0.6);

        bool done = phase.OnTick(ctx);

        // On 28R's free side but tracking away from the final: the follow ends into a pattern entry for the landed runway, never
        // a direct join of its final.
        Assert.True(done);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.DoesNotContain(follower.Phases.Phases, p => p is PatternEntryPhase { Kind: PatternEntryKind.Final });
    }

    // ─── Present-position downwind join predicate (issue #352 / D1) ───

    private static (PatternWaypoints Wp, RunwayInfo Rwy) ComputePattern()
    {
        RunwayInfo rwy = Runway28R();
        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Piston,
            "",
            0,
            PatternDirection.Right,
            null,
            null,
            [rwy],
            authoredRunway: null
        );
        return (wp, rwy);
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_PastAbeamOnTrack_True()
    {
        (PatternWaypoints? wp, RunwayInfo? _) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        AircraftState ac = MakeVfr(Follower, GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 0.5), wp.DownwindHeading, wp.PatternAltitude, 90);

        Assert.True(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_WellBeforeAbeam_False()
    {
        (PatternWaypoints? wp, RunwayInfo? _) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        AircraftState ac = MakeVfr(
            Follower,
            GeoMath.ProjectPoint(abeam, wp.DownwindHeading.ToReciprocal(), 1.5),
            wp.DownwindHeading,
            wp.PatternAltitude,
            90
        );

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_FarOutOnExtendedDownwind_False()
    {
        // 6 nm past the abeam point is out on the ARRIVAL side of the circuit (well past the
        // base turn) — that aircraft flies a normal entry, not a present-position join.
        (PatternWaypoints? wp, RunwayInfo? _) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        AircraftState ac = MakeVfr(
            Follower,
            GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 6.0),
            wp.DownwindHeading.ToReciprocal(),
            wp.PatternAltitude,
            90
        );

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_PastAbeamButFarOffTrack_False()
    {
        (PatternWaypoints? wp, RunwayInfo? rwy) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        double patternWidthNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(abeam, new LatLon(wp.ThresholdLat, wp.ThresholdLon), wp.FinalHeading));
        LatLon alongPast = GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 0.5);
        LatLon farOut = GeoMath.ProjectPoint(alongPast, rwy.TrueHeading + 90.0, patternWidthNm * 2.5);
        AircraftState ac = MakeVfr(Follower, farOut, wp.DownwindHeading, wp.PatternAltitude, 90);

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_TooHighToLandFromHere_False()
    {
        // Alongside the downwind but 3,000 ft above TPA: the remaining ~2-3 nm of circuit
        // cannot absorb the descent at the pattern rate, so the aircraft takes the normal
        // (longer) entry, whose extra track miles are the descent room (mirrors the ERB
        // "too high for base" feasibility check).
        (PatternWaypoints? wp, RunwayInfo? _) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        AircraftState ac = MakeVfr(Follower, GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 0.5), wp.DownwindHeading, wp.PatternAltitude + 3000, 90);

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    // ─── Pattern-aware FOLLOW install: join-side priority ───
    //
    // The runway's established circuit must win over the follower's momentary side —
    // joining on whatever side the follower occupies can build opposing circuits for one
    // runway and, on close parallels, descends a base leg across the neighbor's final
    // (AIM §4-3-3 FIG 4-3-3 note 7). Priority: lead's circuit → runway natural side →
    // follower's side → left.

    [Fact]
    public void ChooseFollowJoinDirection_LeadCircuitDirection_WinsOverFollowerSide()
    {
        RunwayInfo rwy = Runway28R();
        // Follower on the LEFT (south) side — the lead's right circuit still wins; the
        // follower crosses midfield per AIM §4-3-3.1.b rather than flying an opposing circuit.
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 1.0, -1.0), new TrueHeading(100), 1000, 90);

        Assert.Equal(PatternDirection.Right, CommandDispatcher.ChooseFollowJoinDirection(follower, PatternDirection.Right, rwy));
    }

    [Fact]
    public void ChooseFollowJoinDirection_NoLeadDirection_UsesRunwayNaturalSide()
    {
        // Real KOAK: 28R with 28L present naturally flies right traffic (parallel inference).
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 1.0, -1.0), new TrueHeading(112), 1000, 90);

        Assert.Equal(PatternDirection.Right, CommandDispatcher.ChooseFollowJoinDirection(follower, null, rwy));
    }

    [Fact]
    public void ChooseFollowJoinDirection_NoLeadOrNaturalDirection_UsesFollowerSide()
    {
        // Synthetic single runway "28R" with no sibling in the navdb scope: no natural side,
        // so the follower's own side decides. 280° runway: right-hand side is +90° (north).
        RunwayInfo rwy = Runway28R();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        AircraftState north = MakeVfr(Follower, OffFinal(rwy, 1.0, 1.0), new TrueHeading(100), 1000, 90);
        AircraftState south = MakeVfr(Follower, OffFinal(rwy, 1.0, -1.0), new TrueHeading(100), 1000, 90);

        Assert.Equal(PatternDirection.Right, CommandDispatcher.ChooseFollowJoinDirection(north, null, rwy));
        Assert.Equal(PatternDirection.Left, CommandDispatcher.ChooseFollowJoinDirection(south, null, rwy));
    }

    // ─── Lead ahead of the follower's track (FOLLOW from base onto a runwayless lead) ───

    /// <summary>Follower tracking 190° (a right base to 28R); lead 1 nm away on the given relative bearing.</summary>
    private static (AircraftState Follower, AircraftState Lead) AheadPair(double relativeBearingDeg)
    {
        var origin = new LatLon(37.70, -122.15);
        var track = new TrueHeading(190);
        AircraftState follower = MakeVfr(Follower, origin, track, 1000, 90);
        AircraftState lead = MakeVfr(Leader, GeoMath.ProjectPoint(origin, track + relativeBearingDeg, 1.0), new TrueHeading(280), 1000, 90);
        return (follower, lead);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(59.0)]
    [InlineData(-59.0)]
    public void IsLeadAheadOfTrack_WithinSixtyDegrees_IsAhead(double relativeBearingDeg)
    {
        (AircraftState follower, AircraftState lead) = AheadPair(relativeBearingDeg);

        Assert.True(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead));
    }

    [Theory]
    [InlineData(61.0)]
    [InlineData(-61.0)]
    [InlineData(90.0)]
    [InlineData(180.0)]
    public void IsLeadAheadOfTrack_BeyondSixtyDegrees_IsNotAhead(double relativeBearingDeg)
    {
        (AircraftState follower, AircraftState lead) = AheadPair(relativeBearingDeg);

        Assert.False(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead));
    }

    [Fact]
    public void IsLeadAheadOfTrack_UsesTrackNotHeading()
    {
        // Crabbing 20° into the wind: heading 170°, track 190°. A lead 65° left of the track is only
        // 45° left of the nose, and must still count as not ahead: the track is what closes on it.
        (AircraftState follower, AircraftState lead) = AheadPair(-65.0);
        follower.TrueHeading = new TrueHeading(170);

        Assert.False(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead));
    }
}
