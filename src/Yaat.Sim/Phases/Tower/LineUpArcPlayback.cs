namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Closed-form circular-arc playback state used by <see cref="LineUpPhaseV2"/>.
/// Position and heading are both derived from a single scalar phase variable
/// (<see cref="CurrentBearingFromCenterDeg"/>), so they cannot drift apart:
/// after an <see cref="Advance"/> the aircraft pose is, by construction,
/// exactly on the circle and its heading is exactly the tangent.
///
/// The struct holds only geometric state. The owning phase is responsible for
/// writing the resulting lat/lon/heading back to <c>ctx.Aircraft</c> each tick.
/// This separation keeps the playback unit-testable in isolation — a test can
/// construct an arc, tick it repeatedly, and assert that every sampled position
/// lies on the circle to machine precision, with no dependency on
/// <c>PhaseContext</c> or <c>FlightPhysics</c>.
///
/// Conventions:
/// <list type="bullet">
///   <item><see cref="CenterLat"/>, <see cref="CenterLon"/> are absolute
///         WGS-84 coordinates (degrees).</item>
///   <item><see cref="RadiusFt"/> is the circle radius in feet.</item>
///   <item><see cref="CurrentBearingFromCenterDeg"/> is the compass bearing
///         (degrees true, 0–360, 0 = north, increasing clockwise) from the
///         center of the circle to the aircraft's current position. This is
///         the scalar phase variable and is monotone non-decreasing in
///         absolute value over the life of the playback.</item>
///   <item><see cref="RemainingSweepDeg"/> is the angular distance left to
///         traverse, always non-negative. <see cref="Advance"/> decrements it.
///         The playback is complete when it reaches zero.</item>
///   <item><see cref="RightTurn"/> determines the sign of the bearing advance
///         and the tangent-offset direction. True = clockwise (right turn,
///         bearing increases); false = counter-clockwise (left turn, bearing
///         decreases).</item>
/// </list>
/// </summary>
public struct LineUpArcPlayback
{
    public double CenterLat { get; init; }
    public double CenterLon { get; init; }
    public double RadiusFt { get; init; }

    /// <summary>
    /// Compass bearing from center to aircraft. Advances by
    /// <c>speed·dt/radius</c> per tick, signed by <see cref="RightTurn"/>.
    /// </summary>
    public double CurrentBearingFromCenterDeg { get; set; }

    /// <summary>
    /// Remaining sweep in degrees, always &gt;= 0. Reaches 0 when the arc is
    /// fully traversed.
    /// </summary>
    public double RemainingSweepDeg { get; set; }

    public bool RightTurn { get; init; }

    /// <summary>
    /// Radius of the circle expressed in nautical miles. Pre-computed for
    /// convenience; callers use it to convert the GeoMath ProjectPoint distance.
    /// </summary>
    public readonly double RadiusNm => RadiusFt / GeoMath.FeetPerNm;

    /// <summary>
    /// True when the arc has been fully traversed. Checked after
    /// <see cref="Advance"/>.
    /// </summary>
    public readonly bool IsComplete => RemainingSweepDeg <= 1e-6;

    /// <summary>
    /// Compass bearing of the aircraft's tangent to the circle at the current
    /// phase position. For a right turn (clockwise), the tangent is 90°
    /// clockwise of the radial from center to aircraft; for a left turn it is
    /// 90° counter-clockwise. This is the aircraft's heading along the arc.
    /// </summary>
    public readonly double TangentHeadingDeg
    {
        get
        {
            double tangent = RightTurn ? CurrentBearingFromCenterDeg + 90.0 : CurrentBearingFromCenterDeg - 90.0;
            return ((tangent % 360.0) + 360.0) % 360.0;
        }
    }

    /// <summary>
    /// Advance the phase variable by <paramref name="dAngleDeg"/> degrees,
    /// clamped to <see cref="RemainingSweepDeg"/> so the playback cannot
    /// overshoot its own endpoint. Returns the actual angle consumed (useful
    /// when the caller needs to know how much of a requested step fit inside
    /// the remaining sweep).
    /// </summary>
    public double Advance(double dAngleDeg)
    {
        if (dAngleDeg <= 0.0)
        {
            return 0.0;
        }

        double consumed = Math.Min(dAngleDeg, RemainingSweepDeg);
        double signed = RightTurn ? +consumed : -consumed;

        CurrentBearingFromCenterDeg = ((CurrentBearingFromCenterDeg + signed) % 360.0 + 360.0) % 360.0;
        RemainingSweepDeg = Math.Max(0.0, RemainingSweepDeg - consumed);

        return consumed;
    }

    /// <summary>
    /// Compute the aircraft's current lat/lon by projecting from the circle
    /// center along <see cref="CurrentBearingFromCenterDeg"/> for
    /// <see cref="RadiusNm"/>. Pure function of the playback state — no
    /// dependence on aircraft history.
    /// </summary>
    public readonly (double Lat, double Lon) CurrentPosition() =>
        GeoMath.ProjectPoint(CenterLat, CenterLon, new TrueHeading(CurrentBearingFromCenterDeg), RadiusNm);
}
