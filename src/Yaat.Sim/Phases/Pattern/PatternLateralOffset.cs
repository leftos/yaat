namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Per-leg lateral offset state: aircraft doglegs perpendicular to the current
/// pattern heading to acquire a parallel track offset by <see cref="TargetNm"/>
/// to the left or right of the leg's original ground track. State lives on the
/// active phase and is discarded when the phase completes — no carry-over into
/// subsequent legs.
/// </summary>
public sealed class PatternLateralOffsetState
{
    public required double TargetNm { get; init; }
    public required TurnDirection Direction { get; init; }

    /// <summary>
    /// True once the perpendicular cross-track from the original leg ground
    /// track reaches or exceeds <see cref="TargetNm"/>. Once acquired, the
    /// helper restores the leg heading so the aircraft holds parallel.
    /// </summary>
    public bool Acquired { get; set; }
}

/// <summary>
/// Computes the heading target a pattern leg should write each tick while a
/// <see cref="PatternLateralOffsetState"/> is active. During acquisition the
/// aircraft flies the leg heading biased by <see cref="InterceptDeg"/> in the
/// requested direction; once <see cref="GeoMath.SignedCrossTrackDistanceNm"/>
/// from the leg's reference track meets the target, the helper marks the state
/// acquired and returns the parallel leg heading.
/// </summary>
public static class PatternLateralOffsetHelper
{
    /// <summary>
    /// Intercept angle (degrees) used to acquire the offset. 30° matches
    /// standard pattern turn rate and the deviation used by <c>STurnPhase</c>.
    /// </summary>
    public const double InterceptDeg = 30.0;

    /// <summary>
    /// Returns the <see cref="TrueHeading"/> the phase should set on
    /// <c>ctx.Targets.TargetTrueHeading</c> this tick. Mutates
    /// <paramref name="state"/>.<see cref="PatternLateralOffsetState.Acquired"/>
    /// when the perpendicular distance from the original leg ground track meets
    /// or exceeds <see cref="PatternLateralOffsetState.TargetNm"/>.
    ///
    /// <paramref name="legReference"/> may be any fixed point on the original
    /// leg's ground track — threshold for downwind, departure end for upwind,
    /// crosswind-turn for crosswind. The cross-track computation is invariant
    /// to where along the line the reference sits.
    /// </summary>
    public static TrueHeading ComputeTargetHeading(PhaseContext ctx, TrueHeading legHeading, LatLon legReference, PatternLateralOffsetState state)
    {
        // GeoMath.SignedCrossTrackDistanceNm: positive = right of heading.
        double signedNm = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            legReference.Lat,
            legReference.Lon,
            legHeading
        );

        // Acquired distance on the requested side.
        double acquiredNm = state.Direction == TurnDirection.Right ? signedNm : -signedNm;

        if (acquiredNm >= state.TargetNm)
        {
            state.Acquired = true;
            return legHeading;
        }

        // Still acquiring — bias the heading toward the requested side.
        double sign = state.Direction == TurnDirection.Right ? 1.0 : -1.0;
        double interceptDeg = (legHeading.Degrees + sign * InterceptDeg + 360.0) % 360.0;
        return new TrueHeading(interceptDeg);
    }
}
