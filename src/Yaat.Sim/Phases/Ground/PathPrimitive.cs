namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Distinguishes the two shapes a ground-path primitive can take.
/// </summary>
public enum PathPrimitiveKind
{
    /// <summary>A straight line segment between two graph nodes.</summary>
    Straight,

    /// <summary>A circular arc (fillet) between two tangent points.</summary>
    Arc,
}

/// <summary>
/// Immutable geometric primitive consumed by <c>GroundNavigatorV2</c>'s tick
/// loop. A taxi route segment compiles into exactly one primitive;
/// <see cref="PathPrimitiveBuilder.FromSegment"/> does the conversion.
///
/// <para>
/// The two concrete subclasses (<see cref="PathPrimitiveStraight"/> and
/// <see cref="PathPrimitiveArc"/>) hold only the fields each shape needs for
/// playback. Common metadata (length, to-node id) lives on the base record.
/// </para>
///
/// <para>
/// These primitives are intentionally "compiled" — they hold pre-computed
/// geometry (arc center, radius, sweep for arcs; bearing for straights) so
/// the navigator's tick loop can advance without re-deriving geometry each
/// tick. This mirrors the role of <c>LineUpPlan</c> in V2 of the lineup
/// phase: plan once at setup, play back per tick.
/// </para>
/// </summary>
public abstract record PathPrimitive
{
    public required PathPrimitiveKind Kind { get; init; }

    /// <summary>Arc-length of the primitive in feet (straight line length or arc length).</summary>
    public required double LengthFt { get; init; }

    /// <summary>
    /// The graph node id at the end of the primitive, for arrival detection
    /// and phase handoff. For virtual/synthetic segments that do not correspond
    /// to a real graph node, the caller passes the synthetic id used by the
    /// <c>TaxiRoute</c>.
    /// </summary>
    public required int ToNodeId { get; init; }
}

/// <summary>
/// A straight path primitive. Position along the primitive is parameterised
/// by arc-length from <see cref="FromLat"/>/<see cref="FromLon"/> along
/// <see cref="BearingDeg"/>.
/// </summary>
public sealed record PathPrimitiveStraight : PathPrimitive
{
    public required double FromLat { get; init; }
    public required double FromLon { get; init; }
    public required double ToLat { get; init; }
    public required double ToLon { get; init; }

    /// <summary>Compass bearing from the From point to the To point (degrees true, 0–360).</summary>
    public required double BearingDeg { get; init; }
}

/// <summary>
/// A circular-arc path primitive. Position and heading are both functions of
/// a single scalar — the aircraft's current compass bearing from the arc
/// centre — so <c>GroundNavigatorV2</c>'s tick loop can write position and
/// heading together as pure functions of arc-length progress, with no
/// feedback loop and no risk of position/heading drift.
///
/// <para>
/// The arc is stored as a <i>true circle</i>, not as a Bezier approximation.
/// <see cref="PathPrimitiveBuilder.FromSegment"/> recovers the true-circle
/// parameters (<see cref="CenterLat"/>/<see cref="CenterLon"/>,
/// <see cref="RadiusFt"/>, <see cref="StartBearingFromCenterDeg"/>,
/// <see cref="SweepDeg"/>) from the <c>GroundArc</c>'s stored tangent
/// bearings and minimum radius of curvature. This is valid because
/// <c>FilletArcGenerator</c> tunes the Bezier kappa so the fillet is a
/// near-constant-radius circular arc (deviation &lt; 1 ft for radius ≥ 50 ft).
/// </para>
/// </summary>
public sealed record PathPrimitiveArc : PathPrimitive
{
    public required double CenterLat { get; init; }
    public required double CenterLon { get; init; }
    public required double RadiusFt { get; init; }

    /// <summary>
    /// Compass bearing from the centre to the aircraft at the arc entry (start
    /// of traversal). Advances monotonically during playback by
    /// <c>sign(turn)·v·dt/r</c> radians per tick.
    /// </summary>
    public required double StartBearingFromCenterDeg { get; init; }

    /// <summary>
    /// Unsigned sweep angle in degrees. The playback is complete when the
    /// accumulated angle reaches this value.
    /// </summary>
    public required double SweepDeg { get; init; }

    /// <summary>
    /// True for clockwise (right) turns, false for counter-clockwise (left).
    /// Determines the sign of the bearing advance and the tangent offset:
    /// tangent = radial-from-centre + 90° for right turns, -90° for left.
    /// </summary>
    public required bool RightTurn { get; init; }

    /// <summary>Tangent heading at the arc entry (= the departure direction of the preceding segment).</summary>
    public required double EntryTangentBearingDeg { get; init; }

    /// <summary>Tangent heading at the arc exit (= the departure direction of the next segment).</summary>
    public required double ExitTangentBearingDeg { get; init; }

    /// <summary>Radius in nautical miles. Pre-computed for the GeoMath primitives.</summary>
    public double RadiusNm => RadiusFt / GeoMath.FeetPerNm;
}
