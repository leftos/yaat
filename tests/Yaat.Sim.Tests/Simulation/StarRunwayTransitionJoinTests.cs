using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The two branches of <see cref="NavigationCommandHandler.SelectRunwayTransitionLegsToAppend"/> that ALTTA9 cannot
/// reach: its RW29R transition begins on TTE, the STAR's only common-route fix, so every ALTTA9 route either joins the
/// transition on one of its own fixes or is empty. OAKES3 into KOAK has a three-fix common route (OAKES GRUDG BANND)
/// and a RW28B transition that begins on the common route's last fix (BANND TOOOL FFIST), so a route stopped inside
/// the common route and a route already past the transition each show their own branch.
/// </summary>
public class StarRunwayTransitionJoinTests
{
    private const string Airport = "KOAK";
    private const string StarId = "OAKES3";

    [Fact]
    public void RouteEndingOnTheCommonRoute_GetsTheWholeRunwayTransition() => Assert.Equal(["BANND", "TOOOL", "FFIST"], Appended(["OAKES", "GRUDG"]));

    // The ILS RWY 30 AFFIST transition runs FFIST PRFCT PARBB ZOLUM WUVON. PRFCT and PARBB are past the STAR's
    // last fix and sit on neither the STAR's common route nor its runway transition.
    [Fact]
    public void RoutePastTheTransitionOnTheRunway30Approach_GetsNothing() => Assert.Empty(Appended(["PRFCT", "PARBB"]));

    private static string[] Appended(string[] routeNames)
    {
        TestVnasData.EnsureInitialized();
        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar(Airport, StarId);
        Assert.NotNull(star);
        CifpTransition? transition = NavigationCommandHandler.LookupRunwayTransition(star.RunwayTransitions, "28R");
        Assert.NotNull(transition);

        return [.. NavigationCommandHandler.SelectRunwayTransitionLegsToAppend(transition.Legs, star, routeNames).Select(l => l.FixIdentifier!)];
    }
}
