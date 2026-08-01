using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;

namespace Yaat.Sim.Tests;

/// <summary>
/// Fixture-level tests for the committed AP/1B military route data (built by
/// tools/build-mtr-data.py). These assert the shape and internal consistency of the data itself;
/// route expansion and command behaviour are covered separately.
/// </summary>
public sealed class MilitaryRouteDatabaseTests
{
    private static MilitaryRouteDatabase Db => MilitaryRouteDatabase.Default;

    [Fact]
    public void Default_LoadsEveryPublishedRoute()
    {
        // AP/1B cycle 2607 publishes 213 IR, 304 VR and 131 SR routes. The counts are pinned
        // because a drift means the extractor's header scan changed behaviour, not that the DoD
        // reorganised the book -- the build tool gates on the same numbers.
        Assert.Equal(648, Db.Count);
        Assert.Equal(213, Db.OfType(MilitaryRouteType.Ir).Count());
        Assert.Equal(304, Db.OfType(MilitaryRouteType.Vr).Count());
        Assert.Equal(131, Db.OfType(MilitaryRouteType.Sr).Count());
    }

    [Fact]
    public void Get_AcceptsHyphenatedAndFlightPlanForms()
    {
        var route = Db.Get("IR149");

        Assert.NotNull(route);
        Assert.Same(route, Db.Get("IR-149"));
        Assert.Same(route, Db.Get("ir149"));
        Assert.Same(route, Db.Get(" IR149 "));
        Assert.Equal("IR-149", route!.Printed);
    }

    [Fact]
    public void Get_UnknownDesignator_ReturnsNull()
    {
        Assert.Null(Db.Get("IR999999"));
        Assert.Null(Db.Get(""));
        Assert.Null(Db.Get("V6"));
    }

    [Fact]
    public void Ir002_MatchesThePublishedRouteDescription()
    {
        // AP/1B 2-3: eight points A-H anchored on the VXV VORTAC, entering at 6000 MSL and
        // exiting at 9000 MSL. Independently confirmed against the FAA AIS MTRSegment layer.
        var route = Db.Get("IR002");

        Assert.NotNull(route);
        Assert.Equal(MilitaryRouteType.Ir, route!.Type);
        Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H"], route.Points.Select(p => p.Id));
        Assert.Equal(36.0667, route.Points[0].Position.Lat, 3);
        Assert.Equal(-84.65, route.Points[0].Position.Lon, 3);
        Assert.Equal(35.55, route.Points[^1].Position.Lat, 3);
        Assert.Equal(-83.1667, route.Points[^1].Position.Lon, 3);
    }

    [Fact]
    public void AltitudeBlock_AppliesToTheSegmentTerminatingAtThePoint()
    {
        // IR-002's row for point B reads "05 AGL B 60 MSL to B", and the FAA layer's A-to-B
        // segment is 500 HEI / 6000 ALT. The block therefore governs the leg flown *into* B.
        var block = Db.Get("IR002")!.Points[1].Altitude;

        Assert.Equal(MilitaryRouteAltitudeKind.Block, block.Kind);
        Assert.Equal(500, block.FloorFt);
        Assert.Equal(AltitudeReference.Agl, block.FloorReference);
        Assert.Equal(6000, block.CeilingFt);
        Assert.Equal(AltitudeReference.Msl, block.CeilingReference);
        Assert.True(block.IsBlock);
        Assert.True(block.HasAglBound);
    }

    [Fact]
    public void EveryRoute_HasAtLeastTwoPointsWithUsableCoordinates()
    {
        foreach (var route in Db.Routes)
        {
            Assert.True(route.Points.Count >= 2, $"{route.Designator} has {route.Points.Count} point(s)");
            Assert.All(
                route.Points,
                p =>
                {
                    Assert.InRange(p.Position.Lat, -60, 75);
                    Assert.InRange(p.Position.Lon, -180, -30);
                }
            );
        }
    }

    [Fact]
    public void SyntheticPointNames_AreNeverReadAsFrdAnchors()
    {
        // Point names land in the same flat fix dictionary as real navaids, where FrdResolver
        // reads a name whose last three or six characters are all digits as {FIX}{radial}{dist}.
        // AP/1B labels always start with a letter, so a minted name can never take that shape --
        // this pins the property rather than trusting it.
        foreach (var route in Db.Routes)
        {
            foreach (var point in route.Points)
            {
                Assert.StartsWith(route.Designator, point.Name, StringComparison.Ordinal);
                Assert.False(FrdResolver.IsFrdIdentifier(point.Name), $"{point.Name} looks like an FRD identifier");

                var parsed = FrdResolver.ParseFrd(point.Name);
                Assert.NotNull(parsed);
                Assert.Null(parsed!.Value.Radial);
                Assert.Null(parsed.Value.Distance);
            }
        }
    }

    [Fact]
    public void RepeatedPointLabels_AlwaysResolveToTheSamePosition()
    {
        // 44 routes restate a label. That is real AP/1B structure, not a parse error: an
        // alternate branch is printed inline starting from the mainline point it leaves, so
        // IR-033 reads A B C D E F1 G E F2 with both Es at N29 56.0 W83 25.0. Every one of the
        // 65 repeats across the publication carries identical coordinates, which is what keeps
        // the synthetic-name-to-position mapping unambiguous when the points are registered as
        // fixes. A repeat that disagreed on position would mean two rows had been conflated.
        foreach (var route in Db.Routes)
        {
            foreach (var group in route.Points.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var first = group.First().Position;
                Assert.All(
                    group,
                    p =>
                    {
                        Assert.Equal(first.Lat, p.Position.Lat, 4);
                        Assert.Equal(first.Lon, p.Position.Lon, 4);
                    }
                );
            }
        }
    }

    [Fact]
    public void SrRoutes_MostlyOmitTheFacRadDist()
    {
        // AP/1B chapter 4 §II: "Many SRs do not show the FRD of the published entry/alternate
        // entry points or published exit/alternate exit points." IR and VR routes almost always
        // publish one. This asymmetry is a load-bearing property of the data, not an accident.
        double irCoverage = FrdCoverage(MilitaryRouteType.Ir);
        double vrCoverage = FrdCoverage(MilitaryRouteType.Vr);
        double srCoverage = FrdCoverage(MilitaryRouteType.Sr);

        Assert.True(irCoverage > 0.9, $"IR FRD coverage was {irCoverage:P1}");
        Assert.True(vrCoverage > 0.9, $"VR FRD coverage was {vrCoverage:P1}");
        Assert.True(srCoverage < 0.1, $"SR FRD coverage was {srCoverage:P1}");
    }

    [Fact]
    public void DesignatorDigitCount_ReflectsTheFifteenHundredAglRule()
    {
        // AP/1B chapter 1 §II: three-digit IR/VR designators have a segment above 1500 ft AGL,
        // four-digit ones do not. The rule explicitly does not apply to SR routes.
        Assert.True(Db.Get("IR002")!.HasSegmentsAboveFifteenHundredAgl);
        Assert.False(Db.Get("VR1257")!.HasSegmentsAboveFifteenHundredAgl);
        Assert.All(Db.OfType(MilitaryRouteType.Sr), r => Assert.False(r.HasSegmentsAboveFifteenHundredAgl));
    }

    [Fact]
    public void IndexOf_FindsPointsCaseInsensitivelyAndReportsMisses()
    {
        var route = Db.Get("IR002")!;

        Assert.Equal(0, route.IndexOf("A"));
        Assert.Equal(7, route.IndexOf("h"));
        Assert.Equal(-1, route.IndexOf("ZZ"));
    }

    [Fact]
    public void WidthAt_ReturnsThePublishedProtectedWidth()
    {
        // IR-002: "ROUTE WIDTH - 5 NM either side of centerline for the entire route."
        var span = Db.Get("IR002")!.WidthAt("D");

        Assert.NotNull(span);
        Assert.Equal(5, span!.LeftNm);
        Assert.Equal(5, span.RightNm);
    }

    [Fact]
    public void EntryAndExitPoints_DefaultToTheRouteEnds()
    {
        var route = Db.Get("IR002")!;

        Assert.Equal("A", route.EntryPoints[0]);
        Assert.Equal("H", route.ExitPoints[0]);
    }

    [Fact]
    public void ScopedOverride_RestoresThePreviousInstance()
    {
        var original = MilitaryRouteDatabase.Default;
        var replacement = new MilitaryRouteDatabase([]);

        using (MilitaryRouteDatabase.ScopedOverride(replacement))
        {
            Assert.Same(replacement, MilitaryRouteDatabase.Default);
        }

        Assert.Same(original, MilitaryRouteDatabase.Default);
    }

    [Fact]
    public void FromJson_MissingRoutesArray_YieldsEmptyDatabase()
    {
        Assert.Equal(0, MilitaryRouteDatabase.FromJson("""{"metadata":{}}""").Count);
    }

    private static double FrdCoverage(MilitaryRouteType type)
    {
        var points = Db.OfType(type).SelectMany(r => r.Points).ToList();
        return points.Count == 0 ? 0 : (double)points.Count(p => p.Frd is not null) / points.Count;
    }
}
