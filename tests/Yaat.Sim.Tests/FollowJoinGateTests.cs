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
        var r28R = Runway28R();
        var offsetThreshold = GeoMath.ProjectPoint(new LatLon(r28R.ThresholdLatitude, r28R.ThresholdLongitude), r28R.TrueHeading - 90.0, 0.15);
        var offsetEnd = GeoMath.ProjectPoint(new LatLon(r28R.EndLatitude, r28R.EndLongitude), r28R.TrueHeading - 90.0, 0.15);
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
        var onCenterline = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), alongNm);
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }
        var perp = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
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
        var lead = MakeVfr(Leader, OffFinal(rwy, leadDistNm, 0), rwy.TrueHeading, altitude: leadDistNm * 318.0, ias: 75);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new FinalApproachPhase());

        var follower = MakeVfr(Follower, OffFinal(rwy, followerAlongNm, followerCrossNm), new TrueHeading(followerTrackDeg), 1000, ias: 90);
        follower.Approach.FollowingCallsign = Leader;
        var phase = new VfrFollowPhase(Leader);
        follower.Phases = new PhaseList();
        follower.Phases.Add(phase);

        Func<string, AircraftState?> lookup = cs =>
            cs == Leader ? lead
            : cs == Follower ? follower
            : null;
        var ctx = Ctx(follower, rwy, lookup);
        follower.Phases.Start(ctx);
        return (follower, phase, ctx);
    }

    // ─── Standard 30° intercept gate ───

    [Fact]
    public void JoinLeadFinal_SteepIntercept_DoesNotJoin()
    {
        var rwy = Runway28R();
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        // Converging at 35° — beyond the 30° visual-intercept gate; keep pursuing until shallower.
        var (follower, phase, ctx) = SetupFinalJoin(rwy, leadDistNm: 1.8, followerAlongNm: 3.5, followerCrossNm: 0.3, followerTrackDeg: 245);

        phase.OnTick(ctx);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void JoinLeadFinal_ShallowIntercept_Joins()
    {
        var rwy = Runway28R();
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        // 25° intercept, trailing spacing satisfied — commits the join.
        var (follower, phase, ctx) = SetupFinalJoin(rwy, leadDistNm: 1.8, followerAlongNm: 3.5, followerCrossNm: 0.3, followerTrackDeg: 255);

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
        var navDb = TestVnasData.NavigationDb;
        var rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }

        var southOf28L = OffFinal(rwy, 3.5, -0.6);
        var northSide = OffFinal(rwy, 3.5, 0.6);
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
        var rwy28R = Runway28R();
        var rwy28L = Runway28LParallel();
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R, rwy28L));
        // Follower 0.6 nm on the LEFT (28L) side of 28R's centerline: capturing 28R final
        // from there slices through 28L's final approach course.
        var (follower, phase, ctx) = SetupFinalJoin(rwy28R, leadDistNm: 1.8, followerAlongNm: 3.5, followerCrossNm: -0.6, followerTrackDeg: 300);

        phase.OnTick(ctx);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void JoinLeadFinal_FromFreeSideOfParallel_Joins()
    {
        var rwy28R = Runway28R();
        var rwy28L = Runway28LParallel();
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R, rwy28L));
        // Same geometry mirrored to the RIGHT (north) side — no runway between the
        // follower and 28R's centerline.
        var (follower, phase, ctx) = SetupFinalJoin(rwy28R, leadDistNm: 1.8, followerAlongNm: 3.5, followerCrossNm: 0.6, followerTrackDeg: 260);

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
        // Track 180° off the final course so the airborne TryJoinLeadFinal never fires and
        // the lead-landed shortcut is the only capture path under test.
        var (follower, phase, ctx) = SetupFinalJoin(rwy, leadDistNm: 0.8, followerAlongNm: 3.5, followerCrossNm, followerTrackDeg: 112);
        var lead = ctx.AircraftLookup!(Leader)!;
        phase.OnTick(ctx);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        lead.IsOnGround = true;
        return (follower, phase, ctx, lead);
    }

    [Fact]
    public void LeadLandedShortcut_FromFarSideOfParallel_EndsFollowInsteadOfCapturing()
    {
        var navDb = TestVnasData.NavigationDb;
        var rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }
        var (follower, phase, ctx, _) = SetupLeadLandedShortcut(rwy, followerCrossNm: -0.6);

        bool done = phase.OnTick(ctx);

        Assert.True(done, "The phase should end (lead on the ground) once the shortcut refuses the capture.");
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void LeadLandedShortcut_FromFreeSide_StillSequencesOntoRunwayFinal()
    {
        var navDb = TestVnasData.NavigationDb;
        var rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }
        var (follower, phase, ctx, _) = SetupLeadLandedShortcut(rwy, followerCrossNm: 0.6);

        bool done = phase.OnTick(ctx);

        Assert.True(done);
        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
    }

    // ─── Present-position downwind join predicate (issue #352 / D1) ───

    private static (PatternWaypoints Wp, RunwayInfo Rwy) ComputePattern()
    {
        var rwy = Runway28R();
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Piston, "", 0, PatternDirection.Right, null, null, [rwy], authoredRunway: null);
        return (wp, rwy);
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_PastAbeamOnTrack_True()
    {
        var (wp, rwy) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        var ac = MakeVfr(Follower, GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 0.5), wp.DownwindHeading, wp.PatternAltitude, 90);

        Assert.True(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_WellBeforeAbeam_False()
    {
        var (wp, rwy) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        var ac = MakeVfr(Follower, GeoMath.ProjectPoint(abeam, wp.DownwindHeading.ToReciprocal(), 1.5), wp.DownwindHeading, wp.PatternAltitude, 90);

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_FarOutOnExtendedDownwind_False()
    {
        // 6 nm past the abeam point is out on the ARRIVAL side of the circuit (well past the
        // base turn) — that aircraft flies a normal entry, not a present-position join.
        var (wp, rwy) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        var ac = MakeVfr(Follower, GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 6.0), wp.DownwindHeading.ToReciprocal(), wp.PatternAltitude, 90);

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_PastAbeamButFarOffTrack_False()
    {
        var (wp, rwy) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        double patternWidthNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(abeam, new LatLon(wp.ThresholdLat, wp.ThresholdLon), wp.FinalHeading));
        var alongPast = GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 0.5);
        var farOut = GeoMath.ProjectPoint(alongPast, rwy.TrueHeading + 90.0, patternWidthNm * 2.5);
        var ac = MakeVfr(Follower, farOut, wp.DownwindHeading, wp.PatternAltitude, 90);

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    [Fact]
    public void IsAtOrPastDownwindEntry_TooHighToLandFromHere_False()
    {
        // Alongside the downwind but 3,000 ft above TPA: the remaining ~2-3 nm of circuit
        // cannot absorb the descent at the pattern rate, so the aircraft takes the normal
        // (longer) entry, whose extra track miles are the descent room (mirrors the ERB
        // "too high for base" feasibility check).
        var (wp, rwy) = ComputePattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        var ac = MakeVfr(Follower, GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 0.5), wp.DownwindHeading, wp.PatternAltitude + 3000, 90);

        Assert.False(PatternCommandHandler.IsAtOrPastDownwindEntry(ac, wp, AircraftCategory.Piston, wp.DownwindAbeamLat, wp.DownwindAbeamLon));
    }

    // ─── Pattern-aware FOLLOW install: join-side priority ───
    //
    // The runway's established circuit must win over the follower's momentary side —
    // joining on whatever side the follower occupies can build opposing circuits for one
    // runway and, on close parallels, descends a base leg across the neighbor's final
    // (AIM §4-3-3 FIG 4-3-3 note 7). Priority: lead's circuit → runway natural side →
    // follower's side → left.

    private static AircraftState MakeLeadOnFinal(RunwayInfo rwy, PatternDirection? trafficDirection)
    {
        var lead = MakeVfr(Leader, OffFinal(rwy, 2.0, 0), rwy.TrueHeading, altitude: 650, ias: 75);
        lead.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = trafficDirection };
        lead.Phases.Add(new FinalApproachPhase());
        return lead;
    }

    [Fact]
    public void ChooseFollowJoinDirection_LeadCircuitDirection_WinsOverFollowerSide()
    {
        var rwy = Runway28R();
        var lead = MakeLeadOnFinal(rwy, PatternDirection.Right);
        // Follower on the LEFT (south) side — the lead's right circuit still wins; the
        // follower crosses midfield per AIM §4-3-3.1.b rather than flying an opposing circuit.
        var follower = MakeVfr(Follower, OffFinal(rwy, 1.0, -1.0), new TrueHeading(100), 1000, 90);

        Assert.Equal(PatternDirection.Right, CommandDispatcher.ChooseFollowJoinDirection(follower, lead, rwy));
    }

    [Fact]
    public void ChooseFollowJoinDirection_NoLeadDirection_UsesRunwayNaturalSide()
    {
        // Real KOAK: 28R with 28L present naturally flies right traffic (parallel inference).
        var navDb = TestVnasData.NavigationDb;
        var rwy = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || rwy is null)
        {
            return;
        }
        var lead = MakeLeadOnFinal(rwy, trafficDirection: null);
        var follower = MakeVfr(Follower, OffFinal(rwy, 1.0, -1.0), new TrueHeading(112), 1000, 90);

        Assert.Equal(PatternDirection.Right, CommandDispatcher.ChooseFollowJoinDirection(follower, lead, rwy));
    }

    [Fact]
    public void ChooseFollowJoinDirection_NoLeadOrNaturalDirection_UsesFollowerSide()
    {
        // Synthetic single runway "28R" with no sibling in the navdb scope: no natural side,
        // so the follower's own side decides. 280° runway: right-hand side is +90° (north).
        var rwy = Runway28R();
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy));
        var lead = MakeLeadOnFinal(rwy, trafficDirection: null);
        var north = MakeVfr(Follower, OffFinal(rwy, 1.0, 1.0), new TrueHeading(100), 1000, 90);
        var south = MakeVfr(Follower, OffFinal(rwy, 1.0, -1.0), new TrueHeading(100), 1000, 90);

        Assert.Equal(PatternDirection.Right, CommandDispatcher.ChooseFollowJoinDirection(north, lead, rwy));
        Assert.Equal(PatternDirection.Left, CommandDispatcher.ChooseFollowJoinDirection(south, lead, rwy));
    }
}
