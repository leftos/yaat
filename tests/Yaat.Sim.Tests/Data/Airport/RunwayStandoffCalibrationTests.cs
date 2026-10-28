using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// Per-runway paint calibration of the base hold-short separation S: a runway with three or more paint-paired bars takes S
/// as the median of their near-end cross-track distances (<see cref="HoldShortStandoff.CalibratedFt"/>), which beats the
/// authored value and the rule; with no videomap S stays as the rule or the authored value resolves it.
/// </summary>
public class RunwayStandoffCalibrationTests(ITestOutputHelper output)
{
    private static readonly string OakLayoutPath = Path.Combine("TestData", "oak.geojson");
    private static readonly string OakMapPath = Path.Combine("TestData", "TowerCabMaps", "oak.geojson");

    // The values the parse measures. The issue's 28L/10R figure of 258.0 is the median of near ends rounded to whole feet;
    // the drawn near ends give 257.6 and 259.4 as the middle two, so 258.5.
    [Theory]
    [InlineData("28R/10L", 252.1)]
    [InlineData("28L/10R", 258.5)]
    [InlineData("30/12", 280.8)]
    [InlineData("15/33", 164.9)]
    public void Oak_WithItsMap_EachRunwayTakesTheMedianNearEndOfItsPairedBars(string runwayName, double calibratedFt)
    {
        AirportGroundLayout layout = ParseOak(OakMap());
        GroundRunway runway = RunwayNamed(layout, runwayName);
        output.WriteLine(PairedNearEnds(layout, runway));

        Assert.Equal(calibratedFt, runway.HoldShortStandoffFt);
        Assert.Equal(calibratedFt, RunwayCrossingDetector.BuildRunwayRectangle(runway).HoldShortNm * GeoMath.FeetPerNm, precision: 6);
    }

    [Fact]
    public void Oak_Calibration_BeatsTheAuthoredValue_AndTheRuleThatOverridesIt()
    {
        AirportGroundLayout mapped = ParseOak(OakMap());
        AirportGroundLayout unmapped = ParseOak(map: null);

        // 28R/10L authors 225 ft, which the 250 ft rule overrides; its paint says 252.1 ft.
        Assert.Equal(225.0, RunwayNamed(mapped, "28R/10L").HoldShortDistanceFt);
        Assert.True(RunwayNamed(unmapped, "28R/10L").HoldShortStandoffFt >= 250.0);
        Assert.Equal(252.1, RunwayNamed(mapped, "28R/10L").HoldShortStandoffFt);

        // 15/33 authors 175 ft, which stands without paint; its paint says 164.9 ft.
        Assert.Equal(175.0, RunwayNamed(mapped, "15/33").HoldShortDistanceFt);
        Assert.Equal(175.0, RunwayNamed(unmapped, "15/33").HoldShortStandoffFt);
        Assert.Equal(164.9, RunwayNamed(mapped, "15/33").HoldShortStandoffFt);
    }

    [Fact]
    public void Oak_WithItsMap_SeatingMeetsTheMeasuredNumberOfCrossingsThePlanningPassDidNotSee()
    {
        // A crossing whose boundary edge an earlier runway's bar split is not in the planning pass; seating pairs it against
        // the uncalibrated D_rule itself.
        AirportGroundLayout layout = ParseOak(OakMap());
        GroundRunway shared = RunwayNamed(layout, "28R/10L");
        foreach (GroundRunway runway in layout.Runways)
        {
            output.WriteLine($"{runway.Name}: {runway.UnplannedPaintCrossings} crossings not in the planning pass");
            if (!ReferenceEquals(runway, shared))
            {
                Assert.Equal(0, runway.UnplannedPaintCrossings);
            }
        }

        // Measured: three, all on 28R/10L.
        Assert.Equal(3, layout.Runways.Sum(runway => runway.UnplannedPaintCrossings));
        Assert.Equal(3, shared.UnplannedPaintCrossings);
    }

    [Fact]
    public void Oak_PlanningPass_CountsEachPaintedBarOnce()
    {
        // This pins the near-end count, not the TryAdd dedupe: OAK has no runway where two crossings rank the same painted
        // bar first (paired crossings / distinct bars / near ends: 30-12 7/7/7, 28L-10R 6/6/6, 28R-10L 11/11/11,
        // 15-33 7/7/7), so an unconditional add still leaves one near end per distinct bar.
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = ParseOak(map: null);
        TowerCabVideoMap map = OakMap();
        List<RunwayPlan> plans =
        [
            .. layout.Runways.Select(runway =>
                RunwayCrossingDetector.PlanRunway(
                    new GeoJsonParser.RunwayFeature(runway.Name, runway.Coordinates, HoldShortDistanceFt: runway.HoldShortDistanceFt),
                    layout.AirportId,
                    "OAK",
                    calibratedFt: null
                )
            ),
        ];
        var paint = new PaintSource(
            map.Segments,
            [.. layout.Runways.Select(runway => (IReadOnlyList<LatLon>)[.. runway.Coordinates.Select(c => new LatLon(c.Lat, c.Lon))])]
        );

        foreach (RunwayPlan plan in plans)
        {
            (IReadOnlyDictionary<(int OnId, int OffId), IReadOnlyList<PaintPair>> pairs, List<double> nearEndsFt) =
                RunwayCrossingDetector.PairWithUncalibratedStandoff(plan, plans, layout, paint);
            List<int> firstSegments = [.. pairs.Values.Where(candidates => candidates.Count > 0).Select(candidates => candidates[0].SegmentIndex)];
            int distinct = firstSegments.Distinct().Count();
            output.WriteLine($"{plan.Feature.Name}: {firstSegments.Count} paired crossings, {distinct} distinct bars, {nearEndsFt.Count} near ends");

            Assert.True(distinct >= HoldShortStandoff.MinCalibrationBars, $"{plan.Feature.Name} pairs only {distinct} bars");
            Assert.Equal(distinct, nearEndsFt.Count);
        }
    }

    [Fact]
    public void Oak_WithoutAMap_EveryRunwayKeepsTheRuleOrAuthoredValue()
    {
        AirportGroundLayout layout = ParseOak(map: null);

        Assert.NotEmpty(layout.Runways);
        foreach (GroundRunway runway in layout.Runways)
        {
            var inputs = RunwayStandoffInputs.Lookup(NavigationDatabase.Instance, "OAK", RunwayIdentifier.Parse(runway.Name));
            double expectedFt = HoldShortStandoff.BaseSeparationFt(calibratedFt: null, runway.HoldShortDistanceFt, inputs).Ft;
            Assert.Equal(expectedFt, runway.HoldShortStandoffFt);
        }
    }

    [Fact]
    public void CalibratedFt_FewerThanThreeBars_LeavesTheRunwayUncalibrated()
    {
        Assert.Null(HoldShortStandoff.CalibratedFt([]));
        Assert.Null(HoldShortStandoff.CalibratedFt([252.0]));
        Assert.Null(HoldShortStandoff.CalibratedFt([248.0, 256.0]));

        // Three is enough.
        Assert.Equal(252.0, HoldShortStandoff.CalibratedFt([248.0, 256.0, 252.0]));
    }

    [Fact]
    public void CalibratedFt_TwoBars_LeaveTheAuthoredOrRuleValueInForce()
    {
        var inputs = RunwayStandoffInputs.WithoutNavData(150.0);
        HoldShortStandoffResult authored = HoldShortStandoff.BaseSeparationFt(HoldShortStandoff.CalibratedFt([240.0, 244.0]), 245.0, inputs);
        HoldShortStandoffResult rule = HoldShortStandoff.BaseSeparationFt(HoldShortStandoff.CalibratedFt([240.0, 244.0]), null, inputs);

        Assert.Equal(HoldShortStandoffSource.Authored, authored.Source);
        Assert.Equal(245.0, authored.Ft);
        Assert.Equal(HoldShortStandoffSource.Rule, rule.Source);
    }

    [Fact]
    public void CalibratedFt_AnEvenCount_TakesTheMeanOfTheMiddleTwo()
    {
        // OAK 28L/10R's near ends rounded to whole feet: their median is 258.0, since the measured middle pair 257.6/259.4
        // gives 258.5.
        Assert.Equal(258.0, HoldShortStandoff.CalibratedFt([262.0, 237.0, 288.0, 252.0, 259.0, 257.0]));
        Assert.Equal(251.0, HoldShortStandoff.CalibratedFt([250.0, 270.0, 240.0, 252.0]));
    }

    [Fact]
    public void CalibratedFt_RoundsToATenthOfAFoot()
    {
        Assert.Equal(251.2, HoldShortStandoff.CalibratedFt([260.0, 251.234, 250.0]));
        Assert.Equal(251.7, HoldShortStandoff.CalibratedFt([250.0, 251.0, 252.37, 270.0]));
    }

    /// <summary>The near ends of the runway's bars seated on paint, for the report: the pairs behind its S.</summary>
    private static string PairedNearEnds(AirportGroundLayout layout, GroundRunway runway)
    {
        TowerCabVideoMap map = OakMap();
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        var reference = new LatLon(rect.RefLat, rect.RefLon);
        double CrossFt(LatLon point) => Math.Abs(GeoMath.SignedCrossTrackDistanceNm(point, reference, rect.TrueHeading)) * GeoMath.FeetPerNm;

        List<string> bars =
        [
            .. layout
                .Nodes.Values.Where(node => (node.PaintSegmentIndex is not null) && Equals(node.RunwayId, runway.Id))
                .Select(node => (node.Id, Segment: node.PaintSegmentIndex!.Value))
                .Select(bar => (bar.Id, bar.Segment, NearFt: Math.Min(CrossFt(map.Segments[bar.Segment].A), CrossFt(map.Segments[bar.Segment].B))))
                .OrderBy(bar => bar.NearFt)
                .Select(bar => $"#{bar.Id} seg {bar.Segment} near end {bar.NearFt:F1} ft"),
        ];
        return $"{runway.Name}: S {runway.HoldShortStandoffFt:F1} ft, {bars.Count} seated paint bars: {string.Join("; ", bars)}";
    }

    private static GroundRunway RunwayNamed(AirportGroundLayout layout, string name) =>
        layout.Runways.Single(runway => RunwayIdentifier.Parse(runway.Name).Equals(RunwayIdentifier.Parse(name)));

    private static TowerCabVideoMap OakMap() => TowerCabVideoMap.Read(File.ReadAllText(OakMapPath));

    private static AirportGroundLayout ParseOak(TowerCabVideoMap? map)
    {
        TestVnasData.EnsureInitialized();
        return GeoJsonParser.Parse(
            "oak",
            File.ReadAllText(OakLayoutPath),
            "OAK",
            new LayoutInputs(TestStandLeadIns.For("oak"), map),
            FilletMode.None
        );
    }
}
