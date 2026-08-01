using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;

namespace Yaat.Sim.Tests;

/// <summary>
/// Route-expansion behaviour for military training routes.
///
/// The worked example throughout is AP/1B chapter 1 §IV.B.1's own:
/// <c>SAT263043 IR149 LRD040028</c>. Those two fix/radial/distance points are exactly the
/// published Fac/Rad/Dist of IR-149's entry point A and exit point I, so the document's example
/// and the parsed fixture agree end to end.
/// </summary>
[Collection("NavDbMutator")]
public sealed class MilitaryRouteExpansionTests
{
    private static readonly string[] Ir149AllPoints = ["IR149A", "IR149B", "IR149C", "IR149D", "IR149E", "IR149F", "IR149G", "IR149H", "IR149I"];

    public MilitaryRouteExpansionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static NavigationDatabase NavDb => NavigationDatabase.Instance;

    [Fact]
    public void Expand_PublishedFilingExample_YieldsEveryRoutePoint()
    {
        var expanded = RouteExpander.Expand("SAT263043 IR149 LRD040028", NavDb, includeAllTransitionsOnMismatch: false);

        Assert.Equal(Ir149AllPoints, expanded.Where(f => f.StartsWith("IR149", StringComparison.Ordinal)));
    }

    [Fact]
    public void Expand_EntryAnchorOnly_RunsToThePublishedExit()
    {
        var expanded = RouteExpander.Expand("SAT263043 IR149", NavDb, includeAllTransitionsOnMismatch: false);

        Assert.Equal(Ir149AllPoints, expanded.Where(f => f.StartsWith("IR149", StringComparison.Ordinal)));
    }

    [Fact]
    public void Expand_ExitAnchorOnly_StartsAtThePublishedEntry()
    {
        var expanded = RouteExpander.Expand("IR149 LRD040028", NavDb, includeAllTransitionsOnMismatch: false);

        Assert.Equal(Ir149AllPoints, expanded.Where(f => f.StartsWith("IR149", StringComparison.Ordinal)));
    }

    [Fact]
    public void Expand_BareDesignator_YieldsTheWholeRoute()
    {
        var expanded = RouteExpander.Expand("IR149", NavDb, includeAllTransitionsOnMismatch: false);

        Assert.Equal(Ir149AllPoints, expanded);
    }

    [Fact]
    public void Expand_MidRouteAnchors_YieldOnlyThatSpan()
    {
        // RSG141016 is point D's published FRD; COT269054 is point G's.
        var expanded = RouteExpander.Expand("RSG141016 IR149 COT269054", NavDb, includeAllTransitionsOnMismatch: false);

        Assert.Equal(["IR149D", "IR149E", "IR149F", "IR149G"], expanded.Where(f => f.StartsWith("IR149", StringComparison.Ordinal)));
    }

    [Fact]
    public void Expand_ReversedAnchors_FliesForwardInsteadOfReversing()
    {
        // AP/1B chapter 1 §V.B.1: routes are one-way and course reversals are not authorized. An
        // exit that snaps behind the entry is far more likely a bad snap than a reversed filing,
        // so expansion runs forward to the end of the route rather than walking backwards.
        var expanded = RouteExpander.Expand("COT269054 IR149 RSG141016", NavDb, includeAllTransitionsOnMismatch: false);
        var points = expanded.Where(f => f.StartsWith("IR149", StringComparison.Ordinal)).ToList();

        Assert.Equal(["IR149G", "IR149H", "IR149I"], points);
    }

    [Fact]
    public void Expand_AnchorFarFromEveryPoint_FallsBackToThePublishedEnds()
    {
        // SFO is roughly 1,300 NM from IR-149, which runs along the Texas border. An anchor that
        // far away means the filer joined by direct-to, not at a published point.
        var expanded = RouteExpander.Expand("SFO IR149", NavDb, includeAllTransitionsOnMismatch: false);

        Assert.Equal(Ir149AllPoints, expanded.Where(f => f.StartsWith("IR149", StringComparison.Ordinal)));
    }

    [Fact]
    public void Expand_UnknownDesignator_FallsThroughAsAPlainFix()
    {
        var expanded = RouteExpander.Expand("IR999999", NavDb, includeAllTransitionsOnMismatch: false);

        Assert.Equal(["IR999999"], expanded);
    }

    [Fact]
    public void ExpandRouteForNavigation_ResolvesEveryPointToAPosition()
    {
        // The regression this feature exists for: before, every one of these names failed
        // GetFixPosition and the whole training-route portion was dropped from the flown route.
        var expanded = NavDb.ExpandRouteForNavigation("SAT263043 IR149 LRD040028", departureAirport: null);

        Assert.NotEmpty(expanded);
        Assert.All(expanded, name => Assert.NotNull(NavDb.ResolveFixOrFrd(name)));
    }

    [Fact]
    public void ExpandAirwaySegment_RefusesToReverseAlongAMilitaryRoute()
    {
        // Routes are shadowed into the airway index so JAWY and the radar context menu work
        // unchanged, but the airway walk is bidirectional and a military route is not.
        Assert.Equal(["IR149C", "IR149D", "IR149E"], NavDb.ExpandAirwaySegment("IR149", "IR149C", "IR149E"));
        Assert.Empty(NavDb.ExpandAirwaySegment("IR149", "IR149E", "IR149C"));
    }

    [Fact]
    public void MilitaryRoutes_AreVisibleToTheAirwayLookups()
    {
        // What makes JAWY IR149 and the "Join airway" context menu work with no change to either.
        Assert.True(NavDb.IsAirway("IR149"));
        Assert.Equal(Ir149AllPoints, NavDb.GetAirwayFixes("IR149"));
    }

    [Fact]
    public void RoutePoints_ResolveAsFixesButStayOutOfAutocomplete()
    {
        // The whole point of holding them outside _navDb: ~7,000 synthetic names must not flood
        // the fix dropdown, the DIST suggester, the scope's fix overlay, or FRD anchoring.
        Assert.NotNull(NavDb.GetFixPosition("IR149A"));
        Assert.DoesNotContain("IR149A", NavDb.AllFixNames);
        Assert.DoesNotContain(NavDb.GetFixTuples(), t => t.Name == "IR149A");
    }

    [Fact]
    public void ResolveFixOrFrd_HandlesBothPlainFixesAndFullFrds()
    {
        var fromFrd = NavDb.ResolveFixOrFrd("SAT263043");
        var pointA = NavDb.GetFixPosition("IR149A");

        Assert.NotNull(fromFrd);
        Assert.NotNull(pointA);
        // The FRD is point A's own published Fac/Rad/Dist, so the two must agree closely. The
        // residual is magnetic-declination drift between AP/1B's era and the live model.
        double separation = GeoMath.DistanceNm(fromFrd!.Value.Lat, fromFrd.Value.Lon, pointA!.Value.Lat, pointA.Value.Lon);
        Assert.True(separation < MilitaryRouteExpander.AnchorSnapToleranceNm, $"FRD resolved {separation:F1} NM from point A");
    }

    [Fact]
    public void ResolveFixOrFrd_RadialOnlyShape_IsNotTreatedAsAnFrd()
    {
        // FrdResolver also accepts {FIX}{radial:3}, which matches any 5+ character identifier
        // ending in three digits. Honouring it here would resolve a real fix of that shape to its
        // anchor's position instead of its own, so route anchoring takes the full form only.
        Assert.Null(NavDb.ResolveFixOrFrd("ZZZZ123"));
    }

    [Fact]
    public void ProgrammedFixes_IncludeTheRoutePointsForScopeHighlighting()
    {
        var fixes = ProgrammedFixResolver.Resolve(
            "SAT263043 IR149 LRD040028",
            expectedApproach: null,
            destination: null,
            departure: null,
            activeApproachFixNames: null,
            activeStarId: null,
            destinationRunway: null
        );

        Assert.Contains("IR149A", fixes);
        Assert.Contains("IR149I", fixes);
    }
}
