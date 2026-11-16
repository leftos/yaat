using Xunit;
using Yaat.Client.Views;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// The look each nav-route shape kind draws with on both the Scope and the Radar: cleared or flown shapes solid, expected,
/// pending and open-ended ones dashed, and a vector-tail arrowhead on the shapes that point somewhere; a kind this client
/// does not know (from a newer server) draws solid with no arrow.
/// </summary>
public class NavRouteShapeLookTests
{
    [Theory]
    [InlineData(NavRouteShapeKind.CodedLegVector, true, false)]
    [InlineData(NavRouteShapeKind.HoldRacetrack, false, false)]
    [InlineData(NavRouteShapeKind.ProcedureTurn, false, false)]
    [InlineData(NavRouteShapeKind.MilitaryRouteCorridor, true, false)]
    [InlineData(NavRouteShapeKind.PendingHoldRacetrack, true, false)]
    [InlineData(NavRouteShapeKind.OrbitCircle, true, false)]
    [InlineData(NavRouteShapeKind.RouteEndCourse, true, true)]
    [InlineData(NavRouteShapeKind.HeadingVector, true, true)]
    [InlineData(NavRouteShapeKind.FinalApproachCourse, false, false)]
    [InlineData(NavRouteShapeKind.ExpectedFinalApproachCourse, true, false)]
    [InlineData(NavRouteShapeKind.PendingDepartHeading, true, true)]
    [InlineData(NavRouteShapeKind.PendingJoinRadial, true, false)]
    [InlineData((NavRouteShapeKind)99, false, false)]
    public void For_GivesEachKindItsDashAndArrow(NavRouteShapeKind kind, bool dashed, bool arrow)
    {
        (bool Dashed, bool Arrow) look = NavRouteShapeLook.For(kind);

        Assert.Equal(dashed, look.Dashed);
        Assert.Equal(arrow, look.Arrow);
    }
}
