using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>The fillet pass's per-corner report on the committed airport layouts: complete, reasoned, and geometry-neutral.</summary>
public class FilletReportTests
{
    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    [InlineData("fll")]
    [InlineData("iah")]
    public void Report_ListsEveryVisitedJunction_EachArmPairOnce_AndOneBuiltRowPerArc(string shortId)
    {
        AirportGroundLayout? pre = FilletReportLayouts.LoadPreFillet(shortId);
        if (pre is null)
        {
            return;
        }

        FilletStatistics stats = new FilletArcGenerator().Apply(LayoutCloner.DeepClone(pre), CornerFilletOverrides.None);
        IReadOnlyList<FilletCornerReport> corners = stats.Report.Corners;

        IEnumerable<int> overridable = pre
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.TaxiwayIntersection) && (n.Edges.Count >= 2))
            .Select(n => n.Id);
        Assert.Equal(overridable.Order(), corners.Select(c => c.JunctionNodeId).Distinct().Order());
        List<FilletCornerReport> pairRows = [.. corners.Where(c => c.TaxiwayA is not null)];
        int distinctPairs = pairRows.Select(c => (c.JunctionNodeId, c.TaxiwayA, c.BearingA, c.TaxiwayB, c.BearingB)).Distinct().Count();
        Assert.Equal(pairRows.Count, distinctPairs);
        Assert.Equal(stats.ArcsCreated, corners.Count(c => c.Outcome == FilletCornerOutcome.Built));
        Assert.All(corners.Where(c => c.Outcome == FilletCornerOutcome.Skipped), c => Assert.NotNull(c.SkipReason));
        Assert.All(corners.Where(c => c.Outcome == FilletCornerOutcome.Built), c => Assert.Null(c.SkipReason));
        Assert.All(corners.Where(c => c.Outcome == FilletCornerOutcome.Built), c => Assert.NotNull(c.AchievedRadiusFt));
    }

    [Fact]
    public void ShapePoint_OakNode21_IsSkippedAsAShapePoint() =>
        AssertCorner("oak", 21, null, null, FilletCornerOutcome.Skipped, FilletSkipReason.ShapePoint);

    [Fact]
    public void Ineligible_FllCentrelineProjection364_IsSkippedAsIneligible() =>
        AssertCorner("fll", 364, null, null, FilletCornerOutcome.Skipped, FilletSkipReason.Ineligible);

    [Fact]
    public void JunctionSkipped_FllJunction265_IsSkippedWhole() =>
        AssertCorner("fll", 265, null, null, FilletCornerOutcome.Skipped, FilletSkipReason.JunctionSkipped);

    [Fact]
    public void SameFarNode_SfoJunction116_BB5_IsSkipped() =>
        AssertCorner("sfo", 116, "B", "B5", FilletCornerOutcome.Skipped, FilletSkipReason.SameFarNode);

    [Fact]
    public void Collinear_OakJunction16_WW1_IsSkipped() =>
        AssertCorner("oak", 16, "W", "W1", FilletCornerOutcome.Skipped, FilletSkipReason.Collinear);

    [Fact]
    public void NearHairpin_OakJunction16_W1W2_IsSkipped() =>
        AssertCorner("oak", 16, "W1", "W2", FilletCornerOutcome.Skipped, FilletSkipReason.NearHairpin);

    [Fact]
    public void RadiusFloor_SfoJunction744_BCAY1_IsSkippedWithItsRequestedRadius()
    {
        FilletCornerReport? row = AssertCorner("sfo", 744, "BC", "AY1", FilletCornerOutcome.Skipped, FilletSkipReason.RadiusFloor);
        if (row is null)
        {
            return;
        }

        Assert.NotNull(row.RequestedRadiusFt);
    }

    [Fact]
    public void NoOwningCut_IahJunction229_RBRAMP_IsSkippedWithItsWarning()
    {
        FilletCornerReport? row = AssertCorner("iah", 229, "RB", "RAMP", FilletCornerOutcome.Skipped, FilletSkipReason.NoOwningCut);
        if (row is null)
        {
            return;
        }

        Assert.Contains(row.Warnings, w => w.StartsWith("NO_OWNING_CUT: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Deduplicated_OakJunction151_TRAMP_IsSkipped() =>
        AssertCorner("oak", 151, "T", "RAMP", FilletCornerOutcome.Skipped, FilletSkipReason.Deduplicated);

    [Fact]
    public void ChordDegraded_FllJunction103_BB8_IsReportedWithNoAchievedRadius()
    {
        FilletCornerReport? row = AssertCorner("fll", 103, "B", "B8", FilletCornerOutcome.ChordDegraded, null);
        if (row is null)
        {
            return;
        }

        Assert.Null(row.AchievedRadiusFt);
    }

    /// <summary>
    /// Asserts that the Standard pass at <paramref name="shortId"/> reports a row at <paramref name="junctionNodeId"/> for the
    /// arm pair <paramref name="taxiwayA"/>/<paramref name="taxiwayB"/> (null for the junction's own row) with that outcome
    /// and reason, and returns it; null when the layout is not committed.
    /// </summary>
    private static FilletCornerReport? AssertCorner(
        string shortId,
        int junctionNodeId,
        string? taxiwayA,
        string? taxiwayB,
        FilletCornerOutcome outcome,
        FilletSkipReason? reason
    )
    {
        FilletReport? report = FilletReportLayouts.StandardReport(shortId);
        if (report is null)
        {
            return null;
        }

        FilletCornerReport? row = report.Corners.FirstOrDefault(c =>
            (c.JunctionNodeId == junctionNodeId) && (c.TaxiwayA == taxiwayA) && (c.TaxiwayB == taxiwayB) && (c.SkipReason == reason)
        );
        Assert.True(row is not null, $"{shortId} junction {junctionNodeId} {taxiwayA}/{taxiwayB}: no row with reason {reason}");
        Assert.Equal(outcome, row.Outcome);
        return row;
    }

    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    public void MatchedOverrides_LeaveTheLayoutByteIdentical(string shortId)
    {
        AirportGroundLayout? pre = FilletReportLayouts.LoadPreFillet(shortId);
        if (pre is null)
        {
            return;
        }

        AirportGroundLayout plain = LayoutCloner.DeepClone(pre);
        FilletStatistics plainStats = new FilletArcGenerator().Apply(plain, CornerFilletOverrides.None);
        List<CornerFilletOverride> entries =
        [
            .. plainStats
                .Report.Corners.Where(c => c.TaxiwayA is not null)
                .Take(40)
                .Select((c, i) => FilletReportLayouts.OverrideFor(c) with { Suppress = (i % 4) == 0, RadiusFt = ((i % 4) == 1) ? 25.0 : null })
                .Select(o => o with { Force = (!o.Suppress) && (o.RadiusFt is null) }),
        ];

        AirportGroundLayout overridden = LayoutCloner.DeepClone(pre);
        FilletStatistics overriddenStats = new FilletArcGenerator().Apply(overridden, new CornerFilletOverrides(entries));

        Assert.Equal(entries.Count, overriddenStats.Report.Overrides.Count(o => o.Match == FilletOverrideMatch.Matched));
        Assert.Equal(GroundLayoutSerializer.Serialize(plain), GroundLayoutSerializer.Serialize(overridden));
    }
}

/// <summary>The committed pre-fillet test layouts the fillet report and override tests run on.</summary>
internal static class FilletReportLayouts
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FilletReport?> StandardReports = new();

    /// <summary>The Standard pass's report on <paramref name="shortId"/>'s committed layout, built once; null when it is not committed.</summary>
    public static FilletReport? StandardReport(string shortId) =>
        StandardReports.GetOrAdd(shortId, id => LoadPreFillet(id) is { } pre ? Apply(pre) : null);

    public static AirportGroundLayout? LoadPreFillet(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, TestLayoutInputs.For(shortId, videoMap: null), FilletMode.None);
    }

    /// <summary>An edit-free override keyed on <paramref name="corner"/>'s apex, taxiway names and arm bearings.</summary>
    public static CornerFilletOverride OverrideFor(FilletCornerReport corner)
    {
        Assert.NotNull(corner.TaxiwayA);
        Assert.NotNull(corner.TaxiwayB);
        return new(
            corner.ApexLat,
            corner.ApexLon,
            corner.TaxiwayA.Trim().ToUpperInvariant(),
            corner.TaxiwayB.Trim().ToUpperInvariant(),
            Assert.NotNull(corner.BearingA),
            Assert.NotNull(corner.BearingB),
            Suppress: false,
            RadiusFt: null,
            Force: false,
            TangentAFt: null,
            TangentBFt: null
        );
    }

    /// <summary>Runs the Standard pass on a copy of <paramref name="pre"/> with <paramref name="entries"/>.</summary>
    public static FilletReport Apply(AirportGroundLayout pre, params CornerFilletOverride[] entries) =>
        new FilletArcGenerator().Apply(LayoutCloner.DeepClone(pre), new CornerFilletOverrides(entries)).Report;
}
