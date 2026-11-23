using System.Globalization;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Suppress, radius and force corner fillet overrides applied by the Standard pass on the committed airport layouts.</summary>
public class FilletOverrideApplyTests
{
    /// <summary>
    /// How close a pinned radius must be, feet: the last bits of trigonometry differ between the x64 and arm64 builders, and
    /// the fillet geometry carries that difference into the radius (FCM J4 E/E differs by about 3e-9 ft).
    /// </summary>
    private const double RadiusPinToleranceFt = 1e-6;

    [Fact]
    public void Suppress_ABuiltOakCorner_LeavesNoArc_KeepsTheJunctionAndBothEdges()
    {
        AirportGroundLayout pre = Committed("oak");

        // An arc's Origin names only its taxiways, so the corner is the first built one whose taxiway pair no other arm pair
        // of its junction carries: then no arc of that Origin may remain.
        FilletReport standard = Standard("oak");
        FilletCornerReport built = standard.Corners.First(c =>
            (c.TaxiwayA is not null)
            && (c.Outcome == FilletCornerOutcome.Built)
            && (standard.Corners.Count(o => SidecarFilletOverrides.SamePair(o, c)) == 1)
        );
        (AirportGroundLayout layout, FilletReport report) = Run(pre, FilletReportLayouts.OverrideFor(built) with { Suppress = true });

        string a = built.TaxiwayA!;
        string b = built.TaxiwayB!;
        int id = built.JunctionNodeId;
        Assert.DoesNotContain(layout.Arcs, arc => (arc.Origin == $"corner@J{id}/{a}/{b}") || (arc.Origin == $"corner@J{id}/{b}/{a}"));
        Assert.True(layout.Nodes.ContainsKey(id), $"junction {id} was removed");
        List<GroundEdge> atJunction = [.. layout.Edges.Where(e => (e.Nodes[0].Id == id) || (e.Nodes[1].Id == id))];
        Assert.Equal((28, "W", "W6", 1, 1), (id, a, b, atJunction.Count(e => e.TaxiwayName == a), atJunction.Count(e => e.TaxiwayName == b)));

        FilletCornerReport row = RowLike(report, built);
        Assert.Equal(FilletCornerOutcome.Skipped, row.Outcome);
        Assert.Equal(FilletSkipReason.Suppressed, row.SkipReason);
        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
    }

    [Fact]
    public void Radius_ABuiltOakCorner_AsksForTheOverride_AndBuildsTheRadiusTheCutsAllow()
    {
        AirportGroundLayout pre = Committed("oak");
        FilletCornerReport built = Standard("oak").Corners.First(c => (c.TaxiwayA is not null) && (c.Outcome == FilletCornerOutcome.Built));
        (_, FilletReport report) = Run(pre, FilletReportLayouts.OverrideFor(built) with { RadiusFt = 25.0 });

        FilletCornerReport row = RowLike(report, built);
        Assert.Equal(25.0, row.RequestedRadiusFt);
        Assert.Equal(FilletCornerOutcome.Built, row.Outcome);
        AssertRadius(24.806855408420745, row.AchievedRadiusFt);
        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
    }

    [Fact]
    public void Force_OakJunction16_WW1_Collinear_IsRejectedNoTurn() =>
        AssertForce(
            new ForcedPair("oak", 16, "W", "W1", FilletSkipReason.Collinear),
            new Expected(FilletOverrideEffect.Rejected, FilletSkipReason.NoTurn, null)
        );

    [Fact]
    public void Force_OakJunction16_WW2_Collinear_BuildsTheCorner() =>
        AssertForce(
            new ForcedPair("oak", 16, "W", "W2", FilletSkipReason.Collinear),
            new Expected(FilletOverrideEffect.Applied, null, 74.84938082240849)
        );

    [Fact]
    public void Force_OakJunction16_W1W2_NearHairpin_IsRejectedUnsteerable() =>
        AssertForce(
            new ForcedPair("oak", 16, "W1", "W2", FilletSkipReason.NearHairpin),
            new Expected(FilletOverrideEffect.Rejected, FilletSkipReason.Unsteerable, 4.193808873352105)
        );

    [Fact]
    public void Force_SfoJunction744_BCAY1_RadiusFloor_IsRejectedUnsteerable() =>
        AssertForce(
            new ForcedPair("sfo", 744, "BC", "AY1", FilletSkipReason.RadiusFloor),
            new Expected(FilletOverrideEffect.Rejected, FilletSkipReason.Unsteerable, 1.6512364603518137)
        );

    [Fact]
    public void Force_FllJunction103_BB8_ChordDegraded_IsRejectedUnsteerable_AndKeepsTheChord() =>
        AssertForce(
            new ForcedPair("fll", 103, "B", "B8", null),
            new Expected(FilletOverrideEffect.Rejected, FilletSkipReason.Unsteerable, 4.775014197493274)
        );

    [Fact]
    public void Force_OakJunction20_WW_IsRejectedNoTurn() =>
        AssertForce(
            new ForcedPair("oak", 20, "W", "W", FilletSkipReason.Collinear),
            new Expected(FilletOverrideEffect.Rejected, FilletSkipReason.NoTurn, null)
        );

    [Fact]
    public void Force_SfoJunction34_AB5_LeavesTheOtherCornersRadii()
    {
        AssertForce(
            new ForcedPair("sfo", 34, "A", "B5", FilletSkipReason.NearHairpin),
            new Expected(FilletOverrideEffect.Rejected, FilletSkipReason.Unsteerable, 0.8658925495787674)
        );
        AssertOtherBuiltCornersKeepTheirRadii("sfo", 34, "A", "B5");
    }

    /// <summary>
    /// Forces <paramref name="a"/>/<paramref name="b"/> at the junction and asserts every other corner the plain pass builds
    /// there is built at exactly its plain achieved radius.
    /// </summary>
    private static void AssertOtherBuiltCornersKeepTheirRadii(string shortId, int junctionNodeId, string a, string b)
    {
        FilletReport standard = Standard(shortId);
        FilletCornerReport forced = standard.Corners.First(c => (c.JunctionNodeId == junctionNodeId) && (c.TaxiwayA == a) && (c.TaxiwayB == b));
        (_, FilletReport report) = Run(Committed(shortId), FilletReportLayouts.OverrideFor(forced) with { Force = true });

        List<FilletCornerReport> others =
        [
            .. standard.Corners.Where(c =>
                (c.JunctionNodeId == junctionNodeId) && (c.TaxiwayA is not null) && (c.Outcome == FilletCornerOutcome.Built) && (c != forced)
            ),
        ];
        Assert.NotEmpty(others);
        foreach (FilletCornerReport plain in others)
        {
            FilletCornerReport after = RowLike(report, plain);
            Assert.Equal(FilletCornerOutcome.Built, after.Outcome);
            Assert.Equal(plain.AchievedRadiusFt, after.AchievedRadiusFt);
        }
    }

    [Fact]
    public void Force_FcmJunction4_EE_Collinear_BuildsTheCorner_AndLeavesTheOtherCornersRadii()
    {
        AssertForce(
            new ForcedPair("FCM", 4, "E", "E", FilletSkipReason.Collinear),
            new Expected(FilletOverrideEffect.Applied, null, 14.046877398162376)
        );
        AssertOtherBuiltCornersKeepTheirRadii("FCM", 4, "E", "E");
    }

    [Fact]
    public void Force_FllCentrelineProjection364_IsNotReachable() => AssertForcedUnreachable("fll", 364);

    [Fact]
    public void Force_OakShapePoint21_IsNotReachable() => AssertForcedUnreachable("oak", 21);

    [Fact]
    public void Force_SfoJunction116_BB5_SameFarNode_IsRejected() =>
        AssertForce(
            new ForcedPair("sfo", 116, "B", "B5", FilletSkipReason.SameFarNode),
            new Expected(FilletOverrideEffect.Rejected, FilletSkipReason.SameFarNode, null)
        );

    [Fact]
    public void Force_ACornerThatAlreadyBuilds_ChangesNothing()
    {
        AirportGroundLayout pre = Committed("oak");
        FilletCornerReport built = Standard("oak").Corners.First(c => (c.TaxiwayA is not null) && (c.Outcome == FilletCornerOutcome.Built));
        (AirportGroundLayout layout, FilletReport report) = Run(pre, FilletReportLayouts.OverrideFor(built) with { Force = true });

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        Assert.Equal(PlainSerialized(pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void SameOverridesTwice_BuildByteIdenticalLayouts()
    {
        AirportGroundLayout pre = Committed("oak");
        CornerFilletOverride[] entries =
        [
            .. Standard("oak")
                .Corners.Where(c => c.TaxiwayA is not null)
                .Take(30)
                .Select(
                    (c, i) =>
                        (i % 3) switch
                        {
                            0 => FilletReportLayouts.OverrideFor(c) with { Suppress = true },
                            1 => FilletReportLayouts.OverrideFor(c) with { RadiusFt = 25.0 },
                            _ => FilletReportLayouts.OverrideFor(c) with { Force = true },
                        }
                ),
        ];

        (AirportGroundLayout first, _) = Run(pre, entries);
        (AirportGroundLayout second, _) = Run(pre, entries);

        Assert.Equal(GroundLayoutSerializer.Serialize(first), GroundLayoutSerializer.Serialize(second));
    }

    /// <summary>
    /// The same sidecar overrides build the same layout byte for byte whatever order the section lists them in: the
    /// resolver matches every entry against every corner and applies each to its own, so order reaches neither the graph
    /// nor the arc geometry.
    /// </summary>
    [Fact]
    public void Parse_WithTheSameSidecarOverrides_BuildsByteIdenticalLayouts()
    {
        string geoJson = File.ReadAllText(Path.Combine("TestData", "oak.geojson"));
        LayoutInputs inputs = TestLayoutInputs.For("OAK", videoMap: null);
        CornerFilletOverride[] entries = SidecarFilletOverrides.Entries();

        AirportGroundLayout listed = GeoJsonParser.Parse("OAK", geoJson, null, inputs with { CornerFillets = new CornerFilletOverrides(entries) });
        AirportGroundLayout reversed = GeoJsonParser.Parse(
            "OAK",
            geoJson,
            null,
            inputs with
            {
                CornerFillets = new CornerFilletOverrides([.. entries.Reverse()]),
            }
        );

        Assert.Equal(GroundLayoutSerializer.Serialize(listed), GroundLayoutSerializer.Serialize(reversed));
    }

    /// <summary>What a force's override row reports: its effect, rejection reason, and the pinned radius (achieved or refused).</summary>
    private sealed record Expected(FilletOverrideEffect Effect, FilletSkipReason? Reason, double? RadiusFt);

    /// <summary>A corner to force: its layout, junction, taxiway pair and the reason the Standard pass reports for it.</summary>
    private sealed record ForcedPair(string ShortId, int JunctionNodeId, string A, string B, FilletSkipReason? PlainReason);

    /// <summary>
    /// Forces <paramref name="pair"/> and asserts the override row and, for a built force, the corner's exact achieved radius
    /// and a built arc of that radius in the layout; a rejected force leaves the plain pass's layout byte for byte.
    /// </summary>
    private static void AssertForce(ForcedPair pair, Expected expected)
    {
        (string shortId, int junctionNodeId, string a, string b, FilletSkipReason? plainReason) = pair;
        AirportGroundLayout pre = Committed(shortId);
        FilletCornerReport plain = StandardRow(shortId, junctionNodeId, a, b, plainReason);
        (AirportGroundLayout layout, FilletReport report) = Run(pre, FilletReportLayouts.OverrideFor(plain) with { Force = true });

        FilletOverrideReport overrideRow = Assert.Single(report.Overrides);
        FilletCornerReport row = RowLike(report, plain);
        double? radius = (overrideRow.Effect == FilletOverrideEffect.Applied) ? row.AchievedRadiusFt : overrideRow.RejectedRadiusFt;
        string actual =
            $"{shortId} J{junctionNodeId} {a}/{b}: {overrideRow.Effect} {overrideRow.RejectedReason} {Num(radius)}; row {row.Outcome} "
            + $"{row.SkipReason}; "
            + string.Join(" | ", row.Warnings);
        Assert.True((overrideRow.Effect == expected.Effect) && (overrideRow.RejectedReason == expected.Reason), actual);
        Assert.True(SameRadius(expected.RadiusFt, radius), actual);
        if (expected.Effect == FilletOverrideEffect.Applied)
        {
            Assert.Equal(FilletCornerOutcome.Built, row.Outcome);
            string origin = $"corner@J{junctionNodeId}/{a}/{b}";
            Assert.True(
                layout.Arcs.Any(arc => (arc.Origin == origin) && SameRadius(expected.RadiusFt, arc.MinRadiusOfCurvatureFt)),
                $"{origin}: arcs of radius "
                    + string.Join(", ", layout.Arcs.Where(arc => arc.Origin == origin).Select(arc => Num(arc.MinRadiusOfCurvatureFt)))
            );
        }
        else
        {
            Assert.Equal(PlainSerialized(pre), GroundLayoutSerializer.Serialize(layout));
        }
    }

    /// <summary>Forces the first arm pair of a node the pass never visits and asserts the layout is the plain pass's.</summary>
    private static void AssertForcedUnreachable(string shortId, int nodeId)
    {
        AirportGroundLayout pre = Committed(shortId);
        CornerFilletOverride entry = OverrideAtNode(pre, nodeId) with { Force = true };
        (AirportGroundLayout layout, FilletReport report) = Run(pre, entry);

        FilletOverrideReport overrideRow = Assert.Single(report.Overrides);
        Assert.Equal(FilletOverrideMatch.Matched, overrideRow.Match);
        Assert.Equal(FilletOverrideEffect.NotReachable, overrideRow.Effect);
        Assert.Null(overrideRow.RejectedReason);
        Assert.Equal(PlainSerialized(pre), GroundLayoutSerializer.Serialize(layout));
    }

    /// <summary>An edit-free override on the first two arms of <paramref name="nodeId"/>, as the corner loop builds them.</summary>
    private static CornerFilletOverride OverrideAtNode(AirportGroundLayout pre, int nodeId)
    {
        AirportGroundLayout copy = LayoutCloner.DeepClone(pre);
        copy.RebuildAdjacencyLists();
        HashSet<int> manualArcNodes = ManualArcDetector.Detect(copy);
        GroundNode node = copy.Nodes[nodeId];
        IReadOnlyList<TaxiwayArm> arms = TaxiwayArmBuilder.BuildArms(node, manualArcNodes);
        Assert.True(arms.Count >= 2, $"node {nodeId} has {arms.Count} arms");
        return new CornerFilletOverride(
            node.Position.Lat,
            node.Position.Lon,
            arms[0].TaxiwayName.Trim().ToUpperInvariant(),
            arms[1].TaxiwayName.Trim().ToUpperInvariant(),
            arms[0].BearingFromJunctionDeg,
            arms[1].BearingFromJunctionDeg,
            Suppress: false,
            RadiusFt: null,
            Force: false,
            TangentAFt: null,
            TangentBFt: null
        );
    }

    private static void AssertRadius(double expectedFt, double? actualFt) =>
        Assert.True(SameRadius(expectedFt, actualFt), $"radius {Num(actualFt)} ft, pinned {Num(expectedFt)} ft");

    private static bool SameRadius(double? expectedFt, double? actualFt) =>
        (expectedFt is null) ? (actualFt is null) : ((actualFt is { } a) && (Math.Abs(a - expectedFt.Value) <= RadiusPinToleranceFt));

    private static string Num(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

    private static AirportGroundLayout Committed(string shortId)
    {
        AirportGroundLayout? pre = FilletReportLayouts.LoadPreFillet(shortId);
        Assert.NotNull(pre);
        return pre;
    }

    private static (AirportGroundLayout Layout, FilletReport Report) Run(AirportGroundLayout pre, params CornerFilletOverride[] entries)
    {
        AirportGroundLayout layout = LayoutCloner.DeepClone(pre);
        FilletStatistics stats = new FilletArcGenerator().Apply(layout, new CornerFilletOverrides(entries));
        return (layout, stats.Report);
    }

    private static byte[] PlainSerialized(AirportGroundLayout pre) => GroundLayoutSerializer.Serialize(Run(pre).Layout);

    private static FilletReport Standard(string shortId)
    {
        FilletReport? report = FilletReportLayouts.StandardReport(shortId);
        Assert.NotNull(report);
        return report;
    }

    private static FilletCornerReport StandardRow(string shortId, int junctionNodeId, string a, string b, FilletSkipReason? reason)
    {
        FilletCornerReport? row = Standard(shortId)
            .Corners.FirstOrDefault(c => (c.JunctionNodeId == junctionNodeId) && (c.TaxiwayA == a) && (c.TaxiwayB == b) && (c.SkipReason == reason));
        Assert.True(row is not null, $"{shortId} J{junctionNodeId} {a}/{b}: no row with reason {reason}");
        return row;
    }

    /// <summary>
    /// The row of <paramref name="report"/> for the same corner as <paramref name="like"/> (junction, names, bearings).
    /// </summary>
    private static FilletCornerReport RowLike(FilletReport report, FilletCornerReport like)
    {
        FilletCornerReport? row = report.Corners.FirstOrDefault(c =>
            (c.JunctionNodeId == like.JunctionNodeId)
            && (c.TaxiwayA == like.TaxiwayA)
            && (c.TaxiwayB == like.TaxiwayB)
            && (c.BearingA == like.BearingA)
            && (c.BearingB == like.BearingB)
        );
        Assert.True(row is not null, $"J{like.JunctionNodeId} {like.TaxiwayA}/{like.TaxiwayB}: no row");
        return row;
    }
}

/// <summary>
/// The four edits an airport sidecar's <c>cornerFillets</c> section can carry (a suppress, a radius, a force and a pair of
/// pinned tangents), each on its own junction of the committed OAK layout, where the Standard pass applies it.
/// </summary>
internal static class SidecarFilletOverrides
{
    /// <summary>OAK's junction whose collinear W/W2 pair a force builds.</summary>
    private const int ForcedJunction = 16;

    /// <summary>The four entries, in section order: suppress, radiusFt, force, tangentFt, each applied by the OAK parse.</summary>
    public static CornerFilletOverride[] Entries()
    {
        AirportGroundLayout pre = FilletReportLayouts.LoadPreFillet("oak") ?? throw new InvalidOperationException("oak layout not committed");
        FilletReport standard = FilletReportLayouts.StandardReport("oak") ?? throw new InvalidOperationException("no oak standard report");
        pre.RebuildAdjacencyLists();
        HashSet<int> manualArcNodes = ManualArcDetector.Detect(pre);
        List<FilletCornerReport> built =
        [
            .. standard.Corners.Where(c =>
                (c.JunctionNodeId != ForcedJunction)
                && (c.TaxiwayA is not null)
                && (c.Outcome == FilletCornerOutcome.Built)
                && UniquePair(standard, c)
            ),
        ];

        FilletCornerReport pinned = built.First(c => PinFits(pre, manualArcNodes, c, 40.0, 45.0));
        FilletCornerReport suppressed = built.First(c => c.JunctionNodeId != pinned.JunctionNodeId);
        FilletCornerReport radius = built.First(c => (c.JunctionNodeId != pinned.JunctionNodeId) && (c.JunctionNodeId != suppressed.JunctionNodeId));
        List<FilletCornerReport> forced =
        [
            .. standard.Corners.Where(c => (c.JunctionNodeId == ForcedJunction) && ((c.TaxiwayA, c.TaxiwayB) is ("W", "W2") or ("W2", "W"))),
        ];
        Assert.True(forced.Count == 1, $"OAK J{ForcedJunction} has {forced.Count} W/W2 rows, expected one");

        CornerFilletOverride[] entries =
        [
            FilletReportLayouts.OverrideFor(suppressed) with
            {
                Suppress = true,
            },
            FilletReportLayouts.OverrideFor(radius) with
            {
                RadiusFt = 25.0,
            },
            FilletReportLayouts.OverrideFor(Assert.Single(forced)) with
            {
                Force = true,
            },
            FilletReportLayouts.OverrideFor(pinned) with
            {
                TangentAFt = 40.0,
                TangentBFt = 45.0,
            },
        ];
        AssertAllApplied(entries);
        return entries;
    }

    /// <summary>
    /// True when both rows are arm pairs of one junction with the same two taxiway names, in either order.
    /// </summary>
    internal static bool SamePair(FilletCornerReport first, FilletCornerReport second) =>
        (first.JunctionNodeId == second.JunctionNodeId)
        && (
            ((first.TaxiwayA == second.TaxiwayA) && (first.TaxiwayB == second.TaxiwayB))
            || ((first.TaxiwayA == second.TaxiwayB) && (first.TaxiwayB == second.TaxiwayA))
        );

    /// <summary>Asserts the committed OAK parse reports all four entries applied, so an edit the pass never reaches fails here.</summary>
    private static void AssertAllApplied(CornerFilletOverride[] entries)
    {
        string geoJson = File.ReadAllText(Path.Combine("TestData", "oak.geojson"));
        LayoutInputs inputs = TestLayoutInputs.For("OAK", videoMap: null) with { CornerFillets = new CornerFilletOverrides(entries) };
        (_, _, FilletReport report) = GeoJsonParser.ParseWithDiagnostics("OAK", geoJson, null, inputs);

        Assert.Equal(4, report.Overrides.Count);
        Assert.All(report.Overrides, row => Assert.Equal(FilletOverrideEffect.Applied, row.Effect));
    }

    /// <summary>True when no other arm pair of <paramref name="corner"/>'s junction carries its taxiway pair.</summary>
    private static bool UniquePair(FilletReport report, FilletCornerReport corner) => report.Corners.Count(o => SamePair(o, corner)) == 1;

    /// <summary>
    /// True when a pin of <paramref name="a"/> and <paramref name="b"/> feet on the corner's arms is within the
    /// pinned-tangent rules.
    /// </summary>
    private static bool PinFits(AirportGroundLayout pre, HashSet<int> manualArcNodes, FilletCornerReport corner, double a, double b)
    {
        double turn = FilletGeometry.ComputeTurnAngle(Assert.NotNull(corner.BearingA), Assert.NotNull(corner.BearingB));
        if ((turn < 45.0) || (turn > 135.0))
        {
            return false;
        }

        (TaxiwayArm armA, TaxiwayArm armB) = Arms(pre, manualArcNodes, corner);
        return (a >= CornerPlanner.MinPinnedTangentFt)
            && (a <= CornerPlanner.PinFarLimitFt(armA))
            && (b >= CornerPlanner.MinPinnedTangentFt)
            && (b <= CornerPlanner.PinFarLimitFt(armB))
            && (armB.LengthFt >= 80.0);
    }

    /// <summary>The corner's two pre-fillet arms, the first the one <see cref="FilletCornerReport.TaxiwayA"/> names.</summary>
    private static (TaxiwayArm A, TaxiwayArm B) Arms(AirportGroundLayout pre, HashSet<int> manualArcNodes, FilletCornerReport corner)
    {
        IReadOnlyList<TaxiwayArm> arms = TaxiwayArmBuilder.BuildArms(pre.Nodes[corner.JunctionNodeId], manualArcNodes);
        TaxiwayArm armA = arms.First(x => (x.TaxiwayName == corner.TaxiwayA) && (x.BearingFromJunctionDeg == corner.BearingA));
        TaxiwayArm armB = arms.First(x => (x.TaxiwayName == corner.TaxiwayB) && (x.BearingFromJunctionDeg == corner.BearingB) && (x.Id != armA.Id));
        return (armA, armB);
    }
}
