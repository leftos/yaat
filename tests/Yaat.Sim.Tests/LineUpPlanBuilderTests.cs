using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="LineUpPlanBuilder"/> and
/// <see cref="LineUpArcPlayback"/>. These are pure-math tests with no
/// dependency on real airport data — every fixture is constructed inline
/// from synthesized lat/lon points and a <see cref="TestRunwayFactory"/>
/// runway.
/// </summary>
public class LineUpPlanBuilderTests
{
    /// <summary>
    /// Construct a minimal <see cref="PhaseContext"/> with just enough state
    /// for the plan builder. The runway is a KTEST strip pointing east
    /// (heading 90°) starting at (37.0, -122.0). The aircraft is placed at a
    /// hold-short position specified by offset-from-threshold.
    /// </summary>
    private static PhaseContext MakeCtx(
        double rwyHeadingDeg,
        double acLat,
        double acLon,
        double acHeadingDeg,
        AircraftCategory cat = AircraftCategory.Jet
    )
    {
        // Threshold at a fixed anchor so cross-track math is predictable.
        const double threshLat = 37.0;
        const double threshLon = -122.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(threshLat, threshLon, new TrueHeading(rwyHeadingDeg), 2.0);
        var runway = TestRunwayFactory.Make(
            designator: "TST",
            thresholdLat: threshLat,
            thresholdLon: threshLon,
            endLat: endLat,
            endLon: endLon,
            heading: rwyHeadingDeg
        );

        var aircraft = new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "B738",
            Latitude = acLat,
            Longitude = acLon,
            TrueHeading = new TrueHeading(acHeadingDeg),
            IsOnGround = true,
        };

        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = 0.25,
            Runway = runway,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };
    }

    // ---- Basic sanity ----

    [Fact]
    public void TryBuild_NullRunway_ReturnsNull()
    {
        var aircraft = new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "B738",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };

        Assert.Null(LineUpPlanBuilder.TryBuild(ctx));
    }

    // ---- Turn angle / direction ----

    [Fact]
    public void TryBuild_PerpendicularRightTurn_ProducesRightTurnArc()
    {
        // Runway heading east (90°). For a right turn onto the runway, the
        // aircraft must approach from the SOUTH (right side of runway when
        // looking along runway direction) heading NORTH.
        // Turn: 0° → 90° short way = +90° = right turn ✓.
        double rwyHdg = 90.0;
        double acHdg = 0.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0); // 200 ft south of centerline
        double acLon = -122.0 + 0.005; // a bit east of threshold

        var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg);
        var plan = LineUpPlanBuilder.TryBuild(ctx);

        Assert.NotNull(plan);
        Assert.False(plan.IsAlreadyAligned);
        Assert.NotNull(plan.InitialArcState);
        Assert.True(plan.InitialArcState.Value.RightTurn, "0°→90° short way is a right turn");
        Assert.InRange(Math.Abs(plan.TurnAngleDeg), 89.0, 91.0);
        Assert.InRange(plan.InitialArcState.Value.RemainingSweepDeg, 89.0, 91.0);
    }

    [Fact]
    public void TryBuild_PerpendicularLeftTurn_ProducesLeftTurnArc()
    {
        // Runway heading east (90°). For a left turn onto the runway, the
        // aircraft must approach from the NORTH (left side of runway when
        // looking along runway direction) heading SOUTH.
        // Turn: 180° → 90° short way = -90° = left turn ✓.
        double rwyHdg = 90.0;
        double acHdg = 180.0;
        double acLat = 37.0 + 200.0 / (GeoMath.FeetPerNm * 60.0); // 200 ft north of centerline
        double acLon = -122.0 + 0.005;

        var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg);
        var plan = LineUpPlanBuilder.TryBuild(ctx);

        Assert.NotNull(plan);
        Assert.False(plan.InitialArcState!.Value.RightTurn, "180°→90° short way is a left turn");
        Assert.InRange(Math.Abs(plan.TurnAngleDeg), 89.0, 91.0);
    }

    [Fact]
    public void TryBuild_SkewedTurn_PreservesSignedTurnAngle()
    {
        // Approximate the SFO 28R taxiway E geometry: turn of ~69.5° right.
        // Runway heading 297.9°, aircraft approaching at 228.4° (taxiway E).
        // A right turn into the runway means the aircraft is on the RIGHT
        // side of the runway direction (signed cross-track negative).
        double rwyHdg = 297.9;
        double acHdg = 228.4;

        // Place aircraft perpendicular-right of the threshold (rwyHdg + 90°).
        // Project 200 ft in that direction.
        double perpRightBearing = (rwyHdg + 90.0) % 360.0;
        var (acLat, acLon) = GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(perpRightBearing), 200.0 / GeoMath.FeetPerNm);
        // Nudge forward along runway so the aircraft isn't exactly at the threshold.
        (acLat, acLon) = GeoMath.ProjectPoint(acLat, acLon, new TrueHeading(rwyHdg), 500.0 / GeoMath.FeetPerNm);

        var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg);
        var plan = LineUpPlanBuilder.TryBuild(ctx);

        Assert.NotNull(plan);
        // Turn from 228.4° to 297.9° the short way is +69.5° = right turn.
        Assert.InRange(plan.TurnAngleDeg, 68.5, 70.5);
        Assert.True(plan.InitialArcState!.Value.RightTurn);
        Assert.InRange(plan.InitialArcState.Value.RemainingSweepDeg, 68.5, 70.5);
    }

    // ---- Already-aligned short circuit ----

    [Fact]
    public void TryBuild_AlreadyAligned_SkipsArc()
    {
        // Aircraft already heading runway direction, with a tiny 2° offset.
        double rwyHdg = 90.0;
        double acHdg = 92.0;
        double acLat = 37.0 - 50.0 / (GeoMath.FeetPerNm * 60.0); // 50 ft south of centerline
        double acLon = -122.0 + 0.005;

        var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg);
        var plan = LineUpPlanBuilder.TryBuild(ctx);

        Assert.NotNull(plan);
        Assert.True(plan.IsAlreadyAligned);
        Assert.Null(plan.InitialArcState);
        Assert.Equal(0.0, plan.NoseOutLengthFt);
        Assert.Equal(LineUpPlanBuilder.RolloutLengthFt, plan.RolloutLengthFt);
    }

    // ---- Rejection cases ----

    [Fact]
    public void TryBuild_SteepReversal_Rejected()
    {
        // Turn > 150° → should return null (can't fit nose-gear radius).
        double rwyHdg = 90.0;
        double acHdg = 260.0; // 260 → 90 the short way is -170° = extreme left turn
        double acLat = 37.0 + 500.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -122.0 + 0.005;

        var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg);
        Assert.Null(LineUpPlanBuilder.TryBuild(ctx));
    }

    [Fact]
    public void TryBuild_RadiusTooLargeForCrossTrack_Rejected()
    {
        // Aircraft 30 ft from centerline, category Jet (radius 70 ft).
        // Right turn: aircraft south of east-heading runway, heading north.
        // Radius doesn't fit — should reject.
        double rwyHdg = 90.0;
        double acHdg = 0.0; // 90° right turn required
        double acLat = 37.0 - 30.0 / (GeoMath.FeetPerNm * 60.0); // 30 ft south
        double acLon = -122.0 + 0.005;

        var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg);
        Assert.Null(LineUpPlanBuilder.TryBuild(ctx));
    }

    // ---- Speed profile invariant (I5) ----

    [Fact]
    public void TryBuild_ArcSpeedRespectsTurnRateHeadroom()
    {
        // For any category, arc speed should satisfy v/r ≤ 0.85 × GroundTurnRate.
        // v in ft/s, r in ft → v/r in rad/s.
        // Right turn into east-heading runway: aircraft south, heading north.
        foreach (var cat in new[] { AircraftCategory.Jet, AircraftCategory.Turboprop, AircraftCategory.Piston })
        {
            double rwyHdg = 90.0;
            double acHdg = 0.0;
            double acLat = 37.0 - 500.0 / (GeoMath.FeetPerNm * 60.0); // 500 ft south
            double acLon = -122.0 + 0.005;

            var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg, cat);
            var plan = LineUpPlanBuilder.TryBuild(ctx);
            Assert.NotNull(plan);

            double r = CategoryPerformance.LineUpTurnRadiusFt(cat);
            double vFtPerSec = plan.ArcSpeedKts * GeoMath.FeetPerNm / 3600.0;
            double tangentRateRadPerSec = vFtPerSec / r;
            double turnRateRadPerSec = CategoryPerformance.GroundTurnRate(cat) * Math.PI / 180.0;
            double ratio = tangentRateRadPerSec / turnRateRadPerSec;
            Assert.True(
                ratio <= 0.851,
                $"{cat}: tangent rate {tangentRateRadPerSec:F3} rad/s > 0.85 × turn rate {turnRateRadPerSec:F3} (ratio={ratio:F3})"
            );
        }
    }

    // ---- Arc playback invariants ----

    [Fact]
    public void ArcPlayback_AdvanceIsMonotonic()
    {
        var arc = new LineUpArcPlayback
        {
            CenterLat = 37.0,
            CenterLon = -122.0,
            RadiusFt = 70.0,
            CurrentBearingFromCenterDeg = 0.0,
            RemainingSweepDeg = 90.0,
            RightTurn = true,
        };

        double prev = arc.RemainingSweepDeg;
        for (int i = 0; i < 20; i++)
        {
            arc.Advance(5.0);
            Assert.True(arc.RemainingSweepDeg <= prev, "remaining sweep must be monotone non-increasing");
            prev = arc.RemainingSweepDeg;
        }
        Assert.True(arc.IsComplete);
    }

    [Fact]
    public void ArcPlayback_AdvanceClampsAtRemainingSweep()
    {
        var arc = new LineUpArcPlayback
        {
            CenterLat = 37.0,
            CenterLon = -122.0,
            RadiusFt = 70.0,
            CurrentBearingFromCenterDeg = 0.0,
            RemainingSweepDeg = 10.0,
            RightTurn = true,
        };

        double consumed = arc.Advance(50.0);
        Assert.Equal(10.0, consumed);
        Assert.Equal(0.0, arc.RemainingSweepDeg);
        Assert.True(arc.IsComplete);
    }

    [Fact]
    public void ArcPlayback_CurrentPosition_StaysOnCircle()
    {
        // Synthesize a 70-ft-radius arc at a known center; advance through
        // 90 degrees in small steps; verify every position is within 0.5 ft
        // of the circle at radius 70 ft from center.
        double centerLat = 37.0;
        double centerLon = -122.0;
        double radiusFt = 70.0;
        double radiusNm = radiusFt / GeoMath.FeetPerNm;

        var arc = new LineUpArcPlayback
        {
            CenterLat = centerLat,
            CenterLon = centerLon,
            RadiusFt = radiusFt,
            CurrentBearingFromCenterDeg = 0.0,
            RemainingSweepDeg = 90.0,
            RightTurn = true,
        };

        double maxErrorFt = 0;
        for (int i = 0; i < 100; i++)
        {
            var (lat, lon) = arc.CurrentPosition();
            double distFromCenterNm = GeoMath.DistanceNm(centerLat, centerLon, lat, lon);
            double errorFt = Math.Abs((distFromCenterNm - radiusNm) * GeoMath.FeetPerNm);
            maxErrorFt = Math.Max(maxErrorFt, errorFt);
            arc.Advance(1.0);
            if (arc.IsComplete)
            {
                break;
            }
        }

        Assert.True(maxErrorFt < 0.5, $"max position error {maxErrorFt:F3}ft exceeds 0.5ft tolerance");
    }

    [Fact]
    public void ArcPlayback_TangentHeading_MatchesCircleTangent_RightTurn()
    {
        // For a right turn at bearing-from-center = 0° (aircraft at north of
        // center), the tangent should point east (90°). As the bearing
        // advances clockwise to 90° (aircraft at east of center), the tangent
        // should point south (180°).
        var arc = new LineUpArcPlayback
        {
            CenterLat = 37.0,
            CenterLon = -122.0,
            RadiusFt = 70.0,
            CurrentBearingFromCenterDeg = 0.0,
            RemainingSweepDeg = 360.0,
            RightTurn = true,
        };
        Assert.Equal(90.0, arc.TangentHeadingDeg, 3);

        arc.CurrentBearingFromCenterDeg = 90.0;
        Assert.Equal(180.0, arc.TangentHeadingDeg, 3);
    }

    [Fact]
    public void ArcPlayback_TangentHeading_MatchesCircleTangent_LeftTurn()
    {
        // For a left turn at bearing-from-center = 0°, tangent points west (270°).
        var arc = new LineUpArcPlayback
        {
            CenterLat = 37.0,
            CenterLon = -122.0,
            RadiusFt = 70.0,
            CurrentBearingFromCenterDeg = 0.0,
            RemainingSweepDeg = 360.0,
            RightTurn = false,
        };
        Assert.Equal(270.0, arc.TangentHeadingDeg, 3);
    }

    // ---- Arc exit lies on runway centerline ----

    [Fact]
    public void TryBuild_ArcExitLiesOnRunwayCenterline()
    {
        // The whole point of the fillet construction: the arc exit must be
        // on the runway centerline within floating-point tolerance. If this
        // fails, the rollout stage starts off-center and the aircraft will
        // end up parallel-offset.
        // Right turn into east-heading runway: aircraft south, heading north.
        double rwyHdg = 90.0;
        double acHdg = 0.0;
        double acLat = 37.0 - 500.0 / (GeoMath.FeetPerNm * 60.0); // 500 ft south
        double acLon = -122.0 + 0.005;

        var ctx = MakeCtx(rwyHdg, acLat, acLon, acHdg);
        var plan = LineUpPlanBuilder.TryBuild(ctx);
        Assert.NotNull(plan);

        // Arc exit cross-track from runway centerline should be well under the
        // 3-ft end-state tolerance. 1 ft is a tight-but-achievable bound given
        // great-circle rounding error across a ~500 ft construction; if this
        // ever grows above ~1 ft, the fillet math has drifted.
        double exitCrossNm = GeoMath.SignedCrossTrackDistanceNm(
            plan.RolloutFromLat,
            plan.RolloutFromLon,
            ctx.Runway!.ThresholdLatitude,
            ctx.Runway.ThresholdLongitude,
            ctx.Runway.TrueHeading
        );
        double exitCrossFt = Math.Abs(exitCrossNm * GeoMath.FeetPerNm);
        Assert.True(exitCrossFt < 1.0, $"arc exit is {exitCrossFt:F3}ft off runway centerline — fillet math is broken");
    }
}
