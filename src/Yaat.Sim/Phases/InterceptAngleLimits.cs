namespace Yaat.Sim.Phases;

/// <summary>
/// FAA 7110.65 §5-9-2 TBL 5-9-1, the maximum angle at which an aircraft may be turned onto the
/// final approach course:
///
///   | Less than 2 miles from the approach gate, or triple simultaneous approaches | 20 degrees |
///   | 2 miles or more                                                            | 30 degrees (45 degrees for helicopters) |
///
/// The helicopter allowance is scoped to the "2 miles or more" row — inside 2 miles the limit is
/// 20° for every category. A rotorcraft turns tight enough to capture the steeper cut, so applying
/// the 30° figure to one is a real refusal of a legal clearance, not a conservative approximation.
///
/// Triple simultaneous approaches are not modelled, so the 20° row is selected on distance alone.
/// </summary>
public static class InterceptAngleLimits
{
    /// <summary>Distance from the approach gate at which the tightened 20° row takes over.</summary>
    public const double CloseInDistanceNm = 2.0;

    public const double CloseInAngleDeg = 20.0;
    public const double StandardAngleDeg = 30.0;
    public const double HelicopterAngleDeg = 45.0;

    /// <summary>
    /// The "2 miles or more" row for <paramref name="category"/>. Use this where the distance to the
    /// approach gate is not known — <see cref="Phases.Approach.InterceptCoursePhase"/>'s bust-through
    /// gate measures cross-track to the course, not along-track to the gate.
    /// </summary>
    public static double BeyondGateAngleForCategory(AircraftCategory category) =>
        category == AircraftCategory.Helicopter ? HelicopterAngleDeg : StandardAngleDeg;

    /// <summary>
    /// The full TBL 5-9-1 lookup: the 20° row inside <see cref="CloseInDistanceNm"/> of the approach
    /// gate, otherwise the per-category row from <see cref="BeyondGateAngleForCategory"/>.
    /// </summary>
    public static double MaxAngleForCategory(AircraftCategory category, double distanceToGateNm) =>
        distanceToGateNm < CloseInDistanceNm ? CloseInAngleDeg : BeyondGateAngleForCategory(category);
}
