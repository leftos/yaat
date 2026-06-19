using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pure-math tests for the straight-vs-pivot decision logic in
/// <see cref="LineUpGeometry"/>. The geometry module is a pure function of
/// (runway, aircraft pose, category) and returns a <see cref="LineUpPathPlan"/>
/// that <see cref="LineUpPhase"/> plays back per tick. These tests assert:
///
/// <list type="bullet">
///   <item>Pure math helpers (<c>ComputeWasteStraightFt</c>,
///         <c>ComputeWastePivotFt</c>) return expected values for the
///         reference UAL859 pose from issue #142 and the normal 90° turn.</item>
///   <item>The decision classifier picks Pivot for the UAL859 pose (shallow
///         dHdg + large cross-track → straight path wastes &gt; 20% of
///         remaining runway) and Aligned for normal perpendicular turns.</item>
///   <item>Fault conditions: null runway, heading diverging from centerline,
///         cross-track smaller than pivot radius.</item>
/// </list>
/// </summary>
public class LineUpGeometryTests(ITestOutputHelper output)
{
    /// <summary>
    /// Synthetic runway matching SFO 01R dimensions (10600 × 200 ft) and
    /// heading (15° true). Threshold placed at a round lat/lon for easy pose
    /// arithmetic. Used as the reference runway for issue #142 tests.
    /// </summary>
    private static RunwayInfo MakeSfo01RLikeRunway()
    {
        const double threshLat = 37.604;
        const double threshLon = -122.385;
        const double runwayHdgDeg = 15.0;
        const double lengthFt = 10600.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(threshLat, threshLon, new TrueHeading(runwayHdgDeg), lengthFt / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "01R",
            airportId: "KTEST",
            thresholdLat: threshLat,
            thresholdLon: threshLon,
            endLat: endLat,
            endLon: endLon,
            heading: runwayHdgDeg,
            lengthFt: lengthFt,
            widthFt: 200
        );
    }

    /// <summary>
    /// Synthetic runway matching OAK 28R dimensions (5336 × 150 ft) and heading
    /// (292.256° true), with the threshold at the real 28R departure end.
    /// Reference runway for issue #203 (N436MS lining up from taxiway B).
    /// </summary>
    private static RunwayInfo MakeOak28RLikeRunway()
    {
        const double threshLat = 37.72481455555556;
        const double threshLon = -122.20470872222222;
        const double runwayHdgDeg = 292.25597018104963;
        const double lengthFt = 5336.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(threshLat, threshLon, new TrueHeading(runwayHdgDeg), lengthFt / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            thresholdLat: threshLat,
            thresholdLon: threshLon,
            endLat: endLat,
            endLon: endLon,
            heading: runwayHdgDeg,
            lengthFt: lengthFt,
            widthFt: 150
        );
    }

    /// <summary>
    /// Synthetic runway matching KMIA 8R/26L dimensions (10506 × 200 ft) and the
    /// 08R-end heading (087° true), positioned at the real KMIA 08R threshold.
    /// Reference runway for issue #193.
    /// </summary>
    private static RunwayInfo MakeMia8RLikeRunway()
    {
        return TestRunwayFactory.Make(
            designator: "08R",
            airportId: "KMIA",
            thresholdLat: 25.80069936111111,
            thresholdLon: -80.301433,
            endLat: 25.802018111111114,
            endLon: -80.26953561111111,
            heading: 87.37069530318303,
            lengthFt: 10506,
            widthFt: 200
        );
    }

    /// <summary>
    /// Place a pose at the given (signedCrossFt, alongFromThreshFt, hdgDeg)
    /// relative to <paramref name="rwy"/>. Signed cross is positive-right of
    /// the runway heading (matches GeoMath.SignedCrossTrackDistanceNm).
    /// </summary>
    private static (double Lat, double Lon, TrueHeading Hdg) PlacePose(RunwayInfo rwy, double signedCrossFt, double alongFromThreshFt, double hdgDeg)
    {
        double rwyHdgDeg = rwy.TrueHeading.Degrees;
        double perpRightBearingDeg = (rwyHdgDeg + 90.0) % 360.0;
        var (alongLat, alongLon) = GeoMath.ProjectPoint(
            rwy.ThresholdLatitude,
            rwy.ThresholdLongitude,
            rwy.TrueHeading,
            alongFromThreshFt / GeoMath.FeetPerNm
        );
        var (poseLat, poseLon) = GeoMath.ProjectPoint(alongLat, alongLon, new TrueHeading(perpRightBearingDeg), signedCrossFt / GeoMath.FeetPerNm);
        return (poseLat, poseLon, new TrueHeading(hdgDeg));
    }

    // ---- Pure math helpers ----

    [Fact]
    public void ComputeWasteStraightFt_Perpendicular_IsZero()
    {
        // 90° heading delta: aircraft is moving purely perpendicular to the
        // runway → along-runway waste to reach centerline is 0.
        double waste = LineUpGeometry.ComputeWasteStraightFt(crossTrackFt: 200.0, dHdgDeg: 90.0);
        Assert.InRange(waste, 0.0, 1e-6);
    }

    [Fact]
    public void ComputeWasteStraightFt_ShallowAngle_IsLarge()
    {
        // UAL859 pose: ~324 ft cross, ~10° dHdg → 324 / tan(10°) ≈ 1838 ft.
        double waste = LineUpGeometry.ComputeWasteStraightFt(crossTrackFt: 324.0, dHdgDeg: 10.0);
        Assert.InRange(waste, 1830.0, 1850.0);
    }

    [Fact]
    public void ComputeWasteStraightFt_45DegTurn_Matches_CrossTrack()
    {
        // At 45°, the waste equals the cross-track (tan 45° = 1).
        double waste = LineUpGeometry.ComputeWasteStraightFt(crossTrackFt: 200.0, dHdgDeg: 45.0);
        Assert.InRange(waste, 199.5, 200.5);
    }

    [Fact]
    public void ComputeWastePivotFt_Jet_IsSmallConservativeBound()
    {
        // Jet nose-wheel radius is 25 ft → waste-pivot bound should be on the
        // order of tens of feet, much less than any reasonable runway.
        double waste = LineUpGeometry.ComputeWastePivotFt(noseWheelRadiusFt: 25.0);
        Assert.InRange(waste, 10.0, 200.0);
    }

    // ---- Decision: already aligned ----

    [Fact]
    public void Compute_HeadingMatchesRunway_KindIsAlignedAlreadyAligned()
    {
        var rwy = MakeSfo01RLikeRunway();
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: 0.0, alongFromThreshFt: 1000.0, hdgDeg: rwy.TrueHeading.Degrees);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} IsAlreadyAligned={plan.IsAlreadyAligned} turn={plan.TurnAngleDeg:F2}°");
        Assert.Equal(LineUpPathKind.Aligned, plan.Kind);
        Assert.True(plan.IsAlreadyAligned, "small turn magnitude should short-circuit to already-aligned");
    }

    // ---- Decision: normal perpendicular turn ----

    [Fact]
    public void Compute_PerpendicularTurn_KindIsAlignedNotAlreadyAligned()
    {
        var rwy = MakeSfo01RLikeRunway();
        // Aircraft 200 ft left of centerline, heading perpendicular-right
        // toward the runway (rwyHdg + 90° - 180° for "right turn toward"
        // would be wrong; we want heading that converges to centerline from
        // the left). From the left side, the aircraft is on the runway's
        // left, so to turn right onto runway heading we point perpendicular-
        // to-the-right, which is rwyHdg - 90°. But to CONVERGE to centerline
        // from the left, we need to be pointing right-of-runway-heading, so
        // heading = rwyHdg + 90° (pointing right across the runway).
        double rwyHdg = rwy.TrueHeading.Degrees;
        double acHdg = (rwyHdg + 90.0) % 360.0;
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: -200.0, alongFromThreshFt: 1000.0, hdgDeg: acHdg);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} turn={plan.TurnAngleDeg:F2}° waste=(perp)");
        Assert.Equal(LineUpPathKind.Aligned, plan.Kind);
        Assert.False(plan.IsAlreadyAligned);
    }

    // ---- Decision: UAL859 shallow pose ----

    [Fact]
    public void Compute_SfoRwy01rShallowPose_KindIsPivot()
    {
        // Reproduce UAL859 pose from issue #142: ~324 ft left of centerline,
        // ~10° right of runway heading, ~2500 ft past threshold (well onto
        // the runway). Waste-straight ≈ 1840 ft vs ~8100 ft remaining →
        // 22.7% > 20% threshold → pivot path.
        var rwy = MakeSfo01RLikeRunway();
        double rwyHdg = rwy.TrueHeading.Degrees;
        double acHdg = (rwyHdg + 10.0) % 360.0;
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: -324.0, alongFromThreshFt: 2500.0, hdgDeg: acHdg);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} turn={plan.TurnAngleDeg:F2}°");
        Assert.Equal(LineUpPathKind.Pivot, plan.Kind);
    }

    [Fact]
    public void Compute_ShallowAngleSmallCrossTrack_KindIsAligned()
    {
        // Same shallow 10° heading but only 50 ft cross-track. Waste-straight
        // ≈ 284 ft — well under the 20% × ~8100 ft ≈ 1620 ft threshold.
        var rwy = MakeSfo01RLikeRunway();
        double rwyHdg = rwy.TrueHeading.Degrees;
        double acHdg = (rwyHdg + 10.0) % 360.0;
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: -100.0, alongFromThreshFt: 2500.0, hdgDeg: acHdg);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} turn={plan.TurnAngleDeg:F2}°");
        Assert.Equal(LineUpPathKind.Aligned, plan.Kind);
    }

    // ---- Decision: issue #193 reversed parallel-taxiway pose ----

    [Fact]
    public void Compute_Mia8RReversedParallelTaxiwayPose_KindIsPivot()
    {
        // Issue #193: ENY3516 reached the KMIA 8R hold short on a taxiway running
        // parallel to the runway — stopped ~279 ft south of the centerline, ~315 ft
        // past the 08R threshold, heading 284° true (nearly the 26L reciprocal; a
        // 163° net change to line up on 08R/087°). The pivot accomplishes that net
        // turn via two ~90° turns, so it must NOT fault on the single-arc 150° cap.
        var rwy = MakeMia8RLikeRunway();
        var acHdg = new TrueHeading(284.32135440824527);

        var plan = LineUpGeometry.Compute(rwy, 25.79997480483538, -80.30043573387157, acHdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} turn={plan.TurnAngleDeg:F1}° reason={plan.FaultReason}");
        Assert.Equal(LineUpPathKind.Pivot, plan.Kind);
        Assert.NotNull(plan.PivotTurn1);
        Assert.NotNull(plan.PivotTurn2);
        Assert.True(plan.PivotStraightLengthFt > 0, $"pivot straight should be positive, got {plan.PivotStraightLengthFt:F1}");
        double exitHdg = plan.PivotTurn2!.ExitTangentBearingDeg;
        Assert.True(GeoMath.AbsBearingDifference(exitHdg, rwy.TrueHeading.Degrees) < 0.01, $"pivot should end aligned with 087°, got {exitHdg:F1}");
    }

    // ---- Decision: issue #203 OAK 28R steep hold-short turn ----

    [Fact]
    public void Compute_HoldShortTurnExceeds90_KindIsPivot()
    {
        // Issue #203: N436MS (C182) holds short of OAK 28R on taxiway B at the
        // east-end junction, heading ~186° (SSE) — a ~106° net turn onto the
        // runway heading (292°). The aligned single-arc path's straight nose-out
        // runs along the current heading; with a >90° turn that heading points
        // backward along the runway, so the nose-out backs the aircraft toward
        // the runway-start corner before the arc corrects. Such a pose must use
        // the pivot, which crosses toward the centerline first.
        var rwy = MakeOak28RLikeRunway();
        var acHdg = new TrueHeading(185.76476702628463);

        var plan = LineUpGeometry.Compute(rwy, 37.7255308801834, -122.20450743709728, acHdg, AircraftCategory.Piston);

        output.WriteLine($"plan.Kind={plan.Kind} turn={plan.TurnAngleDeg:F1}° reason={plan.FaultReason}");
        Assert.Equal(LineUpPathKind.Pivot, plan.Kind);
        Assert.NotNull(plan.PivotTurn1);
        Assert.NotNull(plan.PivotTurn2);
    }

    // ---- Decision: fault conditions ----

    [Fact]
    public void Compute_HeadingDivergingWithCrossTrack_KindIsPivot()
    {
        // Aircraft 200 ft left of centerline, heading pointing further left
        // (rwyHdg - 45°) — diverging from centerline, so no aligned straight
        // intercept. With 200 ft of cross-track (≫ nose-wheel radius) the pivot
        // recovers: turn to perpendicular-toward-centerline, cross, turn onto
        // runway heading. (Pre-#193 this faulted; the convergence gate is now
        // scoped to the aligned path only.)
        var rwy = MakeSfo01RLikeRunway();
        double rwyHdg = rwy.TrueHeading.Degrees;
        double acHdg = (rwyHdg - 45.0 + 360.0) % 360.0;
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: -200.0, alongFromThreshFt: 1000.0, hdgDeg: acHdg);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} reason={plan.FaultReason}");
        Assert.Equal(LineUpPathKind.Pivot, plan.Kind);
    }

    [Fact]
    public void Compute_SteepTurnLargeCrossTrack_KindIsPivot()
    {
        // 170° heading delta — a near-reversal beyond the 150° single-arc cap.
        // Issue #193 shows real airport geometry produces exactly this (a hold
        // short on a taxiway parallel to the runway). With 300 ft of cross-track
        // the pivot lines up via two slow turns rather than faulting.
        var rwy = MakeSfo01RLikeRunway();
        double rwyHdg = rwy.TrueHeading.Degrees;
        double acHdg = (rwyHdg + 170.0) % 360.0;
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: -300.0, alongFromThreshFt: 1000.0, hdgDeg: acHdg);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} reason={plan.FaultReason}");
        Assert.Equal(LineUpPathKind.Pivot, plan.Kind);
    }

    [Fact]
    public void Compute_ReversedHeadingOnCenterline_KindIsFault()
    {
        // Genuine collapse: aircraft essentially ON the centerline (≈8 ft cross,
        // below the 25 ft jet nose-wheel radius) but pointing nearly the
        // reciprocal. There is no room for the pivot's perpendicular cross-and-
        // turn, so the geometry faults and the user must recover (TAXI / CANCEL).
        var rwy = MakeSfo01RLikeRunway();
        double rwyHdg = rwy.TrueHeading.Degrees;
        double acHdg = (rwyHdg + 170.0) % 360.0;
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: 8.0, alongFromThreshFt: 1000.0, hdgDeg: acHdg);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        output.WriteLine($"plan.Kind={plan.Kind} reason={plan.FaultReason}");
        Assert.Equal(LineUpPathKind.Fault, plan.Kind);
    }

    // ---- Aligned plan structure ----

    [Fact]
    public void Compute_AlignedPath_NoseOutAndArcGeometryChain()
    {
        // Perpendicular 90° right turn, 200 ft south of an east-heading
        // runway. Assert the aligned plan's stages chain correctly:
        //   NoseOutFrom = aircraft pose
        //   NoseOutTo = arc entry
        //   Arc entry -> Arc exit (on centerline)
        //   RolloutFrom = arc exit, RolloutTo = stop point forward of exit
        var rwy = TestRunwayFactory.Make(
            designator: "09",
            thresholdLat: 37.0,
            thresholdLon: -122.0,
            endLat: GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(90), 10000.0 / GeoMath.FeetPerNm).Lat,
            endLon: GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(90), 10000.0 / GeoMath.FeetPerNm).Lon,
            heading: 90.0,
            lengthFt: 10000,
            widthFt: 150
        );
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995; // ~500 ft east of threshold
        var hdg = new TrueHeading(0.0);

        var plan = LineUpGeometry.Compute(rwy, acLat, acLon, hdg, AircraftCategory.Jet);

        Assert.Equal(LineUpPathKind.Aligned, plan.Kind);
        Assert.False(plan.IsAlreadyAligned);
        Assert.NotNull(plan.InitialArcState);
        Assert.True(plan.NoseOutLengthFt >= 0);
        Assert.True(plan.RolloutLengthFt > 0);
        // Arc exit should coincide with rollout from.
        Assert.Equal(plan.ArcExitLat, plan.RolloutFromLat, 10);
        Assert.Equal(plan.ArcExitLon, plan.RolloutFromLon, 10);
    }

    // ---- Pivot plan structure ----

    [Fact]
    public void Compute_PivotPath_PrimitiveGeometryChains()
    {
        // UAL859-like pose → pivot plan. Assert the pivot stages chain:
        //   PivotTurn1 exit = PivotStraight from
        //   PivotStraight to = PivotTurn2 entry
        //   PivotTurn2 exit = RolloutFrom (on centerline, aligned with rwy)
        var rwy = MakeSfo01RLikeRunway();
        double rwyHdg = rwy.TrueHeading.Degrees;
        double acHdg = (rwyHdg + 10.0) % 360.0;
        var (lat, lon, hdg) = PlacePose(rwy, signedCrossFt: -324.0, alongFromThreshFt: 2500.0, hdgDeg: acHdg);

        var plan = LineUpGeometry.Compute(rwy, lat, lon, hdg, AircraftCategory.Jet);

        Assert.Equal(LineUpPathKind.Pivot, plan.Kind);
        Assert.NotNull(plan.PivotTurn1);
        Assert.NotNull(plan.PivotTurn2);
        Assert.True(plan.PivotStraightLengthFt > 0, $"straight length should be positive, got {plan.PivotStraightLengthFt:F2}");

        // PivotTurn2 exits on bearing = runway heading.
        double exitHdg = plan.PivotTurn2!.ExitTangentBearingDeg;
        double hdgDiff = GeoMath.AbsBearingDifference(exitHdg, rwy.TrueHeading.Degrees);
        Assert.True(hdgDiff < 0.01, $"PivotTurn2 exit heading {exitHdg:F2}° should match runway heading {rwy.TrueHeading.Degrees:F2}°");

        // PivotTurn2 final position should be ON the runway centerline.
        // End of turn 2 = (CenterLat, CenterLon) + radius in bearing
        // (StartBearingFromCenter + RightTurn? +sweep : -sweep) from center.
        var t2 = plan.PivotTurn2;
        double finalBearing = t2.StartBearingFromCenterDeg + (t2.RightTurn ? t2.SweepDeg : -t2.SweepDeg);
        finalBearing = ((finalBearing % 360.0) + 360.0) % 360.0;
        var (finalLat, finalLon) = GeoMath.ProjectPoint(t2.CenterLat, t2.CenterLon, new TrueHeading(finalBearing), t2.RadiusNm);
        double crossAfterTurn2 = GeoMath.SignedCrossTrackDistanceNm(
            finalLat,
            finalLon,
            rwy.ThresholdLatitude,
            rwy.ThresholdLongitude,
            rwy.TrueHeading
        );
        double crossFtAfterTurn2 = Math.Abs(crossAfterTurn2) * GeoMath.FeetPerNm;
        Assert.True(crossFtAfterTurn2 < 1.0, $"PivotTurn2 should end on centerline, got {crossFtAfterTurn2:F2}ft cross-track");
    }
}
