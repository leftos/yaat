using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// SFO's hold-bar set built from its seeded safety-logic runways (real navdata) and the committed SFO ground layout: the
/// per-runway counts, the placement rule (at the protected runway's edge, parallel to it) and
/// ids that are unique and the same on every build.
/// </summary>
public class AsdexHoldBarBuilderTests
{
    public AsdexHoldBarBuilderTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>SFO's safety-logic runways as the scenario-load seed gives them, resolved against navdata.</summary>
    internal static IReadOnlyList<AsdexHoldBarRunway> SfoRunways()
    {
        AsdexSafetyLogicConfig sfo = AsdexSafetyLogicSeed.Build(ZoaArtccConfig.Full, NavigationDatabase.Instance, ActiveRunways.Empty)["SFO"];
        return AsdexHoldBarBuilder.ResolveRunways("SFO", sfo.Runways, NavigationDatabase.Instance);
    }

    internal static AirportGroundLayout SfoLayout() =>
        new TestAirportGroundData().GetLayout("SFO") ?? throw new InvalidOperationException("TestData/sfo.geojson is missing");

    /// <summary>The safety-logic runway one of whose ends is <paramref name="end"/>.</summary>
    internal static AsdexHoldBarRunway RunwayWithEnd(IReadOnlyList<AsdexHoldBarRunway> runways, string end) =>
        runways.Single(runway => (runway.Runway.Id.End1 == end) || (runway.Runway.Id.End2 == end));

    /// <summary>
    /// One taxiway bar per hold-short node, same-side bars within 60 ft of each other merged into one, plus the bars across
    /// each crossing runway. A wide throat with a hold-short node per centerline gets one bar; the layout's M1 and M nodes
    /// near the 1L threshold overlap and merge, as do F1 and F on 1R's west side, 54 ft apart.
    /// </summary>
    [Theory]
    [InlineData("28R", 25)]
    [InlineData("28L", 27)]
    [InlineData("01L", 21)]
    [InlineData("01R", 19)]
    public void SfoBarCounts_PerProtectedRunway(string end, int expected)
    {
        IReadOnlyList<AsdexHoldBarRunway> runways = SfoRunways();
        IReadOnlyList<AsdexHoldBar> bars = AsdexHoldBarBuilder.Build(runways, SfoLayout());

        string protectedId = RunwayWithEnd(runways, end).Id;
        List<AsdexHoldBar> own = [.. bars.Where(bar => bar.ProtectedRunwayId == protectedId)];
        Assert.True(
            own.Count == expected,
            $"{own.Count} bars, expected {expected}: {string.Join(", ", own.Select(bar => $"{bar.Id}@{bar.AlongRunwayFt:F0}/{GeoMath.DistanceNm(bar.Points[0], bar.Points[1]) * GeoMath.FeetPerNm:F0}"))}"
        );
    }

    [Fact]
    public void SfoHasSixteenRunwayToRunwayBars_TwoPerRunwayPerCrossing()
    {
        IReadOnlyList<AsdexHoldBarRunway> runways = SfoRunways();
        var runwayIds = runways.Select(runway => runway.Id).ToHashSet();
        IReadOnlyList<AsdexHoldBar> bars = AsdexHoldBarBuilder.Build(runways, SfoLayout());

        Assert.Equal(16, bars.Count(bar => runwayIds.Contains(bar.Id.Split('|')[1])));
    }

    [Fact]
    public void EveryBar_IsTwoPoints_ParallelToItsRunway_AtItsEdge()
    {
        IReadOnlyList<AsdexHoldBarRunway> runways = SfoRunways();
        IReadOnlyList<AsdexHoldBar> bars = AsdexHoldBarBuilder.Build(runways, SfoLayout());
        Assert.NotEmpty(bars);

        foreach (AsdexHoldBar bar in bars)
        {
            Assert.Equal(2, bar.Points.Count);
            AsdexHoldBarRunway runway = runways.Single(r => r.Id == bar.ProtectedRunwayId);
            var end1 = new LatLon(runway.Runway.Lat1, runway.Runway.Lon1);
            var end2 = new LatLon(runway.Runway.Lat2, runway.Runway.Lon2);

            double barBearing = GeoMath.BearingTo(bar.Points[0], bar.Points[1]);
            double offAxis = GeoMath.AbsBearingDifference(barBearing, GeoMath.BearingTo(end1, end2));
            Assert.True(Math.Min(offAxis, 180 - offAxis) <= 2, $"{bar.Id} is {offAxis:F1}° off its runway's axis");

            var centre = new LatLon((bar.Points[0].Lat + bar.Points[1].Lat) / 2, (bar.Points[0].Lon + bar.Points[1].Lon) / 2);
            double fromCentrelineFt =
                Math.Abs(GeoMath.SignedCrossTrackDistanceNm(centre, end1, new TrueHeading(GeoMath.BearingTo(end1, end2)))) * GeoMath.FeetPerNm;
            Assert.True(
                Math.Abs(fromCentrelineFt - (runway.Runway.WidthFt / 2)) <= 10,
                $"{bar.Id} sits {fromCentrelineFt:F0} ft from its runway's centreline"
            );
        }
    }

    /// <summary>
    /// Spans shaped like 1R's east side at SFO: two square-on 190 ft bars (F1, F) and the long bar of the shallow G throat,
    /// whose centre lies past F's but whose span starts before F1's ends. Walked by where each span starts from the low end,
    /// all three join into one bar named after F1, in either runway direction; walked by centre, F1 would stand alone beside a
    /// G+F bar overlapping it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Merge_JoinsALongShallowSpan_ThatStartsBeforeAnEarlierBarEnds_IntoOneBar(bool lowIsEnd1)
    {
        double sign = lowIsEnd1 ? 1 : -1;
        List<AsdexHoldBarBuilder.Draft> drafts = [new("F1", sign * 300, 100, 95), new("F", sign * 800, 100, 95), new("G", sign * 900, 100, 550)];

        AsdexHoldBarBuilder.Draft bar = Assert.Single(AsdexHoldBarBuilder.Merge(drafts, lowIsEnd1));

        Assert.Equal("F1", bar.Surface);
        Assert.Equal(sign * 827.5, bar.Along, 6);
        Assert.Equal(622.5, bar.HalfLengthFt, 6);
    }

    /// <summary>Two square-on bars join when the gap between them is at most the merge gap, and stay apart one foot past it.</summary>
    [Theory]
    [InlineData(AsdexHoldBarBuilder.MergeGapFt, 1)]
    [InlineData(AsdexHoldBarBuilder.MergeGapFt + 1, 2)]
    public void Merge_JoinsBarsUpToTheMergeGapApart(double gapFt, int expectedBars)
    {
        Assert.Equal(60, AsdexHoldBarBuilder.MergeGapFt);
        List<AsdexHoldBarBuilder.Draft> drafts = [new("M1", 1000, -100, 95), new("M", 1000 + 190 + gapFt, -100, 95)];

        Assert.Equal(expectedBars, AsdexHoldBarBuilder.Merge(drafts, lowIsEnd1: true).Count);
    }

    /// <summary>
    /// A runway that only meets another (its end inside the other's pavement) gets one bar there, on its own side: SFO's real
    /// 28R-10L, and the real 1R-19L cut off 30 ft past 28R's centreline, so its pavement never reaches 28R's far edge.
    /// </summary>
    [Fact]
    public void ARunwayEndingInsideTheProtectedRunway_GetsOneBar_OnItsOwnSide()
    {
        IReadOnlyList<AsdexHoldBarRunway> sfo = SfoRunways();
        AsdexHoldBarRunway protectedRunway = RunwayWithEnd(sfo, "28R");
        RunwayInfo full = RunwayWithEnd(sfo, "01R").Runway;
        var axis = new AsdexRunwayAxis(protectedRunway.Runway);
        double l1 = axis.Lateral(new LatLon(full.Lat1, full.Lon1));
        double l2 = axis.Lateral(new LatLon(full.Lat2, full.Lon2));
        double t = (l1 + (Math.Sign(l1) * 30)) / (l1 - l2);
        var cut = new RunwayInfo
        {
            AirportId = full.AirportId,
            Id = full.Id,
            Designator = full.Designator,
            Lat1 = full.Lat1,
            Lon1 = full.Lon1,
            Elevation1Ft = full.Elevation1Ft,
            TrueHeading1 = full.TrueHeading1,
            Lat2 = full.Lat1 + (t * (full.Lat2 - full.Lat1)),
            Lon2 = full.Lon1 + (t * (full.Lon2 - full.Lon1)),
            Elevation2Ft = full.Elevation2Ft,
            TrueHeading2 = full.TrueHeading2,
            WidthFt = full.WidthFt,
        };
        var meeting = new AsdexHoldBarRunway("1R-CUT", cut, []);

        IReadOnlyList<AsdexHoldBar> bars = AsdexHoldBarBuilder.Build([protectedRunway, meeting], SfoLayout());

        AsdexHoldBar bar = Assert.Single(bars, bar => bar.Id.StartsWith($"{protectedRunway.Id}|1R-CUT|", StringComparison.Ordinal));
        var centre = new LatLon((bar.Points[0].Lat + bar.Points[1].Lat) / 2, (bar.Points[0].Lon + bar.Points[1].Lon) / 2);
        Assert.Equal(Math.Sign(l1), Math.Sign(axis.Lateral(centre)));
    }

    [Fact]
    public void Ids_AreUnique_AndTheSameOnEveryBuild()
    {
        IReadOnlyList<AsdexHoldBar> first = AsdexHoldBarBuilder.Build(SfoRunways(), SfoLayout());
        IReadOnlyList<AsdexHoldBar> second = AsdexHoldBarBuilder.Build(SfoRunways(), TestAirportGroundData.LoadUncached("SFO")!);

        Assert.Equal(first.Count, first.Select(bar => bar.Id).Distinct().Count());
        Assert.Equal(first.Select(bar => bar.Id), second.Select(bar => bar.Id));
        Assert.Equal(first.SelectMany(bar => bar.Points), second.SelectMany(bar => bar.Points));
    }
}
