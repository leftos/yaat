using Yaat.Sim;

namespace Yaat.Client.Views;

/// <summary>
/// How a nav-route shape draws on both the Scope and the Radar, from its kind: cleared or flown shapes solid, expected,
/// pending and open-ended ones dashed, and an arrowhead at the last point of a shape that points somewhere, like a vector
/// tail.
/// </summary>
internal static class NavRouteShapeLook
{
    /// <summary>
    /// Whether a shape of <paramref name="kind"/> draws dashed, and whether it ends in an arrowhead along its last segment.
    /// </summary>
    internal static (bool Dashed, bool Arrow) For(NavRouteShapeKind kind) =>
        kind switch
        {
            // Open-ended: a heading flown until an altitude, distance or radial, so dashed like a vector tail.
            NavRouteShapeKind.CodedLegVector => (true, false),
            // Holds and procedure turns are definite flown paths.
            NavRouteShapeKind.HoldRacetrack => (false, false),
            NavRouteShapeKind.ProcedureTurn => (false, false),
            // A boundary rather than a path: where other traffic must not be (7110.65 §9-2-6.d).
            NavRouteShapeKind.MilitaryRouteCorridor => (true, false),
            // A hold queued at a fix ahead is not flown yet.
            NavRouteShapeKind.PendingHoldRacetrack => (true, false),
            // A VFR or boundary orbit is a nominal circle, not a flown path.
            NavRouteShapeKind.OrbitCircle => (true, false),
            NavRouteShapeKind.RouteEndCourse => (true, true),
            NavRouteShapeKind.HeadingVector => (true, true),
            NavRouteShapeKind.FinalApproachCourse => (false, false),
            NavRouteShapeKind.ExpectedFinalApproachCourse => (true, false),
            NavRouteShapeKind.PendingDepartHeading => (true, true),
            NavRouteShapeKind.PendingJoinRadial => (true, false),
            // A kind this client does not know, sent by a newer server: drawn solid with no arrow.
            _ => (false, false),
        };
}
