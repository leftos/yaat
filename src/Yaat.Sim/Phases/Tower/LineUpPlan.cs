using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Immutable geometric plan for a lineup-onto-runway maneuver, produced by
/// <see cref="LineUpPlanBuilder.TryBuild"/> at phase start and consumed by
/// <see cref="LineUpPhaseV2"/> during tick playback. Contains everything the
/// state machine needs to drive the aircraft from its current hold-short
/// position onto the runway centerline, aligned and stopped, without any
/// per-tick feedback control: position and heading during the arc are
/// functions of a single scalar (the <see cref="LineUpArcPlayback"/> state's
/// current bearing from center), and the nose-out and rollout stages are
/// straight-line segments with fixed bearings.
///
/// <para>
/// Because the plan is a value object with only <c>init</c>-only fields, it
/// cannot be mutated after construction — this is invariant I1 from the
/// design document. The phase holds a read-only reference to the plan plus
/// a small amount of mutable progress state (current <c>s</c>, current state
/// enum, working copy of the arc playback) which never reaches back into the
/// plan.
/// </para>
///
/// <para>
/// A null return from <see cref="LineUpPlanBuilder.TryBuild"/> means the
/// geometry is outside the supported envelope (turn too steep, radius too
/// large for available cross-track distance, runway or context is null,
/// etc.). The phase enters the <c>Faulted</c> state on null.
/// </para>
/// </summary>
public sealed record LineUpPlan
{
    /// <summary>Aircraft category; used for speed / turn-rate lookups during playback.</summary>
    public required AircraftCategory Category { get; init; }

    /// <summary>Runway heading used as the rollout tangent. Degrees true, 0–360.</summary>
    public required double RunwayHeadingDeg { get; init; }

    /// <summary>
    /// Signed turn angle from the aircraft's initial heading to the runway
    /// heading, normalized to (−180°, +180°]. Positive = right turn, negative
    /// = left turn. Magnitude &lt; 5° triggers the already-aligned path (no
    /// nose-out, no arc — just rollout from the aircraft's projected position
    /// onto the centerline). Magnitude &gt; 150° is rejected at build time.
    /// </summary>
    public required double TurnAngleDeg { get; init; }

    /// <summary>True if the plan has no arc (already-aligned short-circuit).</summary>
    public required bool IsAlreadyAligned { get; init; }

    // ---- Stage 1: nose-out straight ----

    /// <summary>
    /// Start of the nose-out straight — the aircraft's initial lat/lon at
    /// plan build time. The state machine uses this for progress tracking
    /// and diagnostics; physics still moves the aircraft, so by the time
    /// NoseOut arrives the aircraft's actual position should equal this.
    /// </summary>
    public required double NoseOutFromLat { get; init; }
    public required double NoseOutFromLon { get; init; }

    /// <summary>End of the nose-out straight — the arc entry tangent point.</summary>
    public required double NoseOutToLat { get; init; }
    public required double NoseOutToLon { get; init; }

    /// <summary>Nose-out straight length in feet. Can be 0 when already-aligned.</summary>
    public required double NoseOutLengthFt { get; init; }

    /// <summary>
    /// Compass bearing (true, 0–360) along which the aircraft rolls during
    /// NoseOut. Equal to the aircraft's initial heading at plan build time.
    /// </summary>
    public required double NoseOutBearingDeg { get; init; }

    // ---- Stage 2: arc ----

    /// <summary>
    /// Initial arc playback state. The phase copies this into a mutable
    /// working field on state transition and advances the copy per tick.
    /// Null when <see cref="IsAlreadyAligned"/> is true.
    /// </summary>
    public required LineUpArcPlayback? InitialArcState { get; init; }

    // ---- Stage 3: rollout straight along runway centerline ----

    /// <summary>
    /// Start of the rollout straight — equal to the arc exit tangent point,
    /// which by construction lies on the runway centerline.
    /// </summary>
    public required double RolloutFromLat { get; init; }
    public required double RolloutFromLon { get; init; }

    /// <summary>End of the rollout straight — the "line up and wait" stop point.</summary>
    public required double RolloutToLat { get; init; }
    public required double RolloutToLon { get; init; }

    /// <summary>Rollout length in feet (distance from arc exit to stop point).</summary>
    public required double RolloutLengthFt { get; init; }

    // ---- Speed profile ----

    /// <summary>
    /// Target cruise speed through the arc, in knots. Chosen so that the
    /// tangent rotation rate <c>v/r</c> stays under 85% of
    /// <see cref="CategoryPerformance.GroundTurnRate"/> (invariant I5).
    /// </summary>
    public required double ArcSpeedKts { get; init; }

    /// <summary>Provenance tag for diagnostics (e.g. "graph", "synthetic", "already-aligned").</summary>
    public string? Provenance { get; init; }
}

/// <summary>
/// Builds a <see cref="LineUpPlan"/> from a <see cref="PhaseContext"/>. The
/// builder is a pure function — it reads ctx and produces a plan (or null on
/// rejection) without any side effects. This makes it trivially unit-testable:
/// a test can construct a minimal <c>PhaseContext</c> and assert exact
/// lat/lon, angles, and speeds on the returned plan.
/// </summary>
public static class LineUpPlanBuilder
{
    private static readonly ILogger Log = SimLog.CreateLogger("LineUpPlanBuilder");

    /// <summary>Below this turn-angle magnitude (degrees) the aligned short-circuit fires.</summary>
    public const double AlignedMaxTurnDeg = 5.0;

    /// <summary>Above this turn-angle magnitude (degrees) the builder rejects the plan.</summary>
    public const double MaxTurnDeg = 150.0;

    /// <summary>
    /// Roll-forward distance past the geometric arc exit along runway
    /// heading. Matches real "line up and wait" where a jet rolls ~50–200 ft
    /// onto the runway before stopping; 80 ft is a sane default for the
    /// middle of that range.
    /// </summary>
    public const double RolloutLengthFt = 80.0;

    /// <summary>
    /// Safety factor for the tangent-rotation-rate cap. At
    /// <c>v/r = GroundTurnRate × π/180 × SafetyFactor</c>, the arc integrator
    /// has <c>(1 − SafetyFactor)</c> of turn-rate authority headroom —
    /// enough for the one-shot entry snap to absorb small entry errors
    /// without saturating the physical cap. Invariant I5.
    /// </summary>
    public const double HeadroomSafetyFactor = 0.85;

    /// <summary>
    /// Try to build a plan for the lineup maneuver. Returns null if the
    /// geometry is outside the supported envelope; the caller should enter
    /// <c>Faulted</c>.
    /// </summary>
    public static LineUpPlan? TryBuild(PhaseContext ctx)
    {
        if (ctx.Runway is null)
        {
            Log.LogDebug("[LineUpPlan] null runway");
            return null;
        }

        var rwy = ctx.Runway;
        double rwyHdgDeg = rwy.TrueHeading.Degrees;
        double acLat = ctx.Aircraft.Latitude;
        double acLon = ctx.Aircraft.Longitude;
        double acHdgDeg = ctx.Aircraft.TrueHeading.Degrees;

        // Signed turn angle from aircraft heading to runway heading.
        // Positive = clockwise (right turn), negative = counter-clockwise (left turn).
        double dthetaDeg = (((rwyHdgDeg - acHdgDeg) + 540.0) % 360.0) - 180.0;
        double turnMagnitudeDeg = Math.Abs(dthetaDeg);
        bool rightTurn = dthetaDeg > 0;

        // Reject steep reversals — the physical geometry can't fit a nose-
        // gear-radius arc in the space available between hold-short and the
        // opposite side of the runway.
        if (turnMagnitudeDeg > MaxTurnDeg)
        {
            Log.LogDebug("[LineUpPlan] turn {Turn:F1}° exceeds max {Max:F1}°, rejecting", turnMagnitudeDeg, MaxTurnDeg);
            return null;
        }

        double radiusFt = CategoryPerformance.LineUpTurnRadiusFt(ctx.Category);
        double arcSpeedKts = ComputeArcSpeedKts(ctx.Category, radiusFt);

        // Already-aligned short-circuit: skip nose-out and arc; the plan is
        // just a straight rollout from the aircraft's projected position onto
        // the runway centerline.
        if (turnMagnitudeDeg < AlignedMaxTurnDeg)
        {
            return BuildAlignedPlan(ctx, rwyHdgDeg, dthetaDeg, radiusFt, arcSpeedKts);
        }

        // Compute the aircraft's signed cross-track distance from the runway
        // centerline. Positive = left of runway (looking along runway heading);
        // negative = right of runway.
        double signedCrossNm = GeoMath.SignedCrossTrackDistanceNm(acLat, acLon, rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading);
        double crossMagnitudeFt = Math.Abs(signedCrossNm) * GeoMath.FeetPerNm;

        // Radius must fit in the available cross-track distance. If the
        // aircraft is too close to the centerline for the category's minimum
        // nose-gear radius, the geometry collapses.
        if (crossMagnitudeFt < radiusFt)
        {
            Log.LogDebug("[LineUpPlan] |crossTrack|={Cross:F1}ft < radius={Radius:F1}ft, rejecting", crossMagnitudeFt, radiusFt);
            return null;
        }

        // Precondition: the aircraft must be heading TOWARD the runway
        // centerline, not away from it. In runway-local coordinates with +X
        // along runway heading and +Y perpendicular-right (the sign
        // convention of GeoMath.SignedCrossTrackDistanceNm), the aircraft's
        // position is (x_a, y_a=signedCross) and its direction unit vector is
        // (cos(dHdg), sin(dHdg)) where dHdg = ac_hdg - rwy_hdg. The aircraft
        // is moving toward y=0 iff sign(y_a)·sign(Vy) is negative — i.e.
        // signedCross × sin(dHdg) < 0. Reject otherwise (pointing parallel
        // or away).
        double dHdgRad = (acHdgDeg - rwyHdgDeg) * Math.PI / 180.0;
        double sinDHdg = Math.Sin(dHdgRad);
        if (signedCrossNm * sinDHdg >= -1e-9)
        {
            Log.LogDebug(
                "[LineUpPlan] aircraft not converging to runway centerline (cross={Cross:F1}ft, dHdg={DHdg:F1}°), rejecting",
                signedCrossNm * GeoMath.FeetPerNm,
                ((acHdgDeg - rwyHdgDeg + 540) % 360) - 180
            );
            return null;
        }

        // Distance from aircraft along its current heading to the point where
        // the heading line intercepts the runway centerline. In local coords:
        //   y_a + t·sin(dHdg) = 0  →  t = -y_a / sin(dHdg) = -signedCross / sinDHdg
        // The precondition above guarantees t > 0.
        double distToCornerNm = -signedCrossNm / sinDHdg;

        // Project from aircraft along current heading to get corner point I.
        var (cornerLat, cornerLon) = GeoMath.ProjectPoint(acLat, acLon, ctx.Aircraft.TrueHeading, distToCornerNm);

        // Tangent distance from the corner: for a circle of radius r tangent
        // to both lines meeting at angle theta, the tangent points are
        // r·tan(theta/2) from the corner along each line.
        double halfTurnRad = (turnMagnitudeDeg / 2.0) * Math.PI / 180.0;
        double tangentDistFt = radiusFt * Math.Tan(halfTurnRad);
        double tangentDistNm = tangentDistFt / GeoMath.FeetPerNm;

        // Nose-out must be non-negative. If the aircraft is already past the
        // arc entry (distToCorner < tangentDist), the geometry collapses.
        double noseOutNm = distToCornerNm - tangentDistNm;
        if (noseOutNm < 0)
        {
            Log.LogDebug(
                "[LineUpPlan] aircraft past arc entry (distToCorner={D:F1}ft < tangent={T:F1}ft), rejecting",
                distToCornerNm * GeoMath.FeetPerNm,
                tangentDistFt
            );
            return null;
        }

        // Arc entry: distToCornerNm - tangentDistNm from aircraft along current heading
        var (entryLat, entryLon) = GeoMath.ProjectPoint(acLat, acLon, ctx.Aircraft.TrueHeading, noseOutNm);

        // Arc exit: tangentDist forward from corner along runway heading
        var (exitLat, exitLon) = GeoMath.ProjectPoint(cornerLat, cornerLon, rwy.TrueHeading, tangentDistNm);

        // Arc center: perpendicular from arc entry toward turn direction, at
        // radius distance. For a right turn the center is 90° clockwise of
        // aircraft heading; for a left turn it is 90° CCW.
        double perpHdgDeg = ((acHdgDeg + (rightTurn ? 90.0 : -90.0)) + 360.0) % 360.0;
        var (centerLat, centerLon) = GeoMath.ProjectPoint(entryLat, entryLon, new TrueHeading(perpHdgDeg), radiusFt / GeoMath.FeetPerNm);

        // Initial arc playback state. The current bearing from center is the
        // opposite of perpHdg (since center is perpHdg from entry, entry is
        // perpHdg+180 from center).
        double initialBearingFromCenterDeg = ((perpHdgDeg + 180.0) % 360.0 + 360.0) % 360.0;
        var arcState = new LineUpArcPlayback
        {
            CenterLat = centerLat,
            CenterLon = centerLon,
            RadiusFt = radiusFt,
            CurrentBearingFromCenterDeg = initialBearingFromCenterDeg,
            RemainingSweepDeg = turnMagnitudeDeg,
            RightTurn = rightTurn,
        };

        // Rollout stop point: RolloutLengthFt past the arc exit along runway heading.
        var (stopLat, stopLon) = GeoMath.ProjectPoint(exitLat, exitLon, rwy.TrueHeading, RolloutLengthFt / GeoMath.FeetPerNm);

        return new LineUpPlan
        {
            Category = ctx.Category,
            RunwayHeadingDeg = rwyHdgDeg,
            TurnAngleDeg = dthetaDeg,
            IsAlreadyAligned = false,
            NoseOutFromLat = acLat,
            NoseOutFromLon = acLon,
            NoseOutToLat = entryLat,
            NoseOutToLon = entryLon,
            NoseOutLengthFt = noseOutNm * GeoMath.FeetPerNm,
            NoseOutBearingDeg = acHdgDeg,
            InitialArcState = arcState,
            RolloutFromLat = exitLat,
            RolloutFromLon = exitLon,
            RolloutToLat = stopLat,
            RolloutToLon = stopLon,
            RolloutLengthFt = RolloutLengthFt,
            ArcSpeedKts = arcSpeedKts,
            Provenance = "graph",
        };
    }

    /// <summary>
    /// Build a plan for the already-aligned case (turn angle magnitude below
    /// <see cref="AlignedMaxTurnDeg"/>). The plan is a single rollout segment
    /// from the aircraft's current position to a stop point
    /// <see cref="RolloutLengthFt"/> forward along runway heading.
    /// </summary>
    private static LineUpPlan BuildAlignedPlan(PhaseContext ctx, double rwyHdgDeg, double dthetaDeg, double radiusFt, double arcSpeedKts)
    {
        var rwy = ctx.Runway!;
        double acLat = ctx.Aircraft.Latitude;
        double acLon = ctx.Aircraft.Longitude;

        var (stopLat, stopLon) = GeoMath.ProjectPoint(acLat, acLon, rwy.TrueHeading, RolloutLengthFt / GeoMath.FeetPerNm);

        return new LineUpPlan
        {
            Category = ctx.Category,
            RunwayHeadingDeg = rwyHdgDeg,
            TurnAngleDeg = dthetaDeg,
            IsAlreadyAligned = true,
            NoseOutFromLat = acLat,
            NoseOutFromLon = acLon,
            NoseOutToLat = acLat,
            NoseOutToLon = acLon,
            NoseOutLengthFt = 0.0,
            NoseOutBearingDeg = ctx.Aircraft.TrueHeading.Degrees,
            InitialArcState = null,
            RolloutFromLat = acLat,
            RolloutFromLon = acLon,
            RolloutToLat = stopLat,
            RolloutToLon = stopLon,
            RolloutLengthFt = RolloutLengthFt,
            ArcSpeedKts = arcSpeedKts,
            Provenance = "already-aligned",
        };
    }

    /// <summary>
    /// Compute the target cruise speed through the arc so that the tangent
    /// rotation rate <c>v/r</c> stays at <see cref="HeadroomSafetyFactor"/>
    /// of the category's <see cref="CategoryPerformance.GroundTurnRate"/>.
    /// Also caps at the category's taxi corner speed so the plan doesn't
    /// command unrealistically fast arcs when the radius is large.
    /// </summary>
    private static double ComputeArcSpeedKts(AircraftCategory cat, double radiusFt)
    {
        // v/r (rad/s) = GroundTurnRate (rad/s) × safety
        // v (ft/s) = (GroundTurnRate_rad_per_sec × safety) × r_ft
        // v (kt)  = (GroundTurnRate_deg_per_sec × π/180 × safety) × r_ft × 3600 / FeetPerNm
        double turnRateRadPerSec = CategoryPerformance.GroundTurnRate(cat) * Math.PI / 180.0;
        double authoritySpeedKts = turnRateRadPerSec * HeadroomSafetyFactor * radiusFt * 3600.0 / GeoMath.FeetPerNm;

        // Cap at the taxi corner speed (applied when the turn is >= 90°) —
        // real jets don't enter tight fillets faster than this even when the
        // math would allow it.
        double cornerCapKts = CategoryPerformance.TaxiCornerSpeed(cat);
        return Math.Min(authoritySpeedKts, cornerCapKts);
    }
}
